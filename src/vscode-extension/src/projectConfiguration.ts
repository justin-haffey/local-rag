import * as path from "node:path";

export interface ProjectConfiguration {
    schemaVersion: 1;
    solution: { name: string; description: string };
    instanceId: string;
    knowledgeBases: { sourceId: string }[];
}

export async function selectProjectConfigurationPath(roots: readonly string[], configured: string | undefined,
    chooseRoot: (roots: readonly string[]) => Promise<string | undefined>): Promise<string | undefined> {
    if (configured) {
        if (!path.isAbsolute(configured) || path.basename(configured) !== ".localrag.json") throw new Error("Configured path must be an absolute .localrag.json path.");
        return configured;
    }
    const root = roots.length === 1 ? roots[0] : await chooseRoot(roots);
    if (!root) return undefined;
    if (!path.isAbsolute(root) || (roots.length > 1 && !roots.includes(root))) throw new Error("Choose exactly one explicit project root.");
    return path.join(root, ".localrag.json");
}

function object(value: unknown, keys: string[]): asserts value is Record<string, unknown> {
    if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).some(key => !keys.includes(key)) || keys.some(key => !(key in value)))
        throw new Error("Unsupported project properties. Repair the selected configuration before editing or searching.");
}

/** Strict v1 validation: opaque source identities are nonblank strings, never inferred UUIDs. */
export function parseProjectConfiguration(text: string): ProjectConfiguration {
    const value: unknown = JSON.parse(text.replace(/^\uFEFF/, ""));
    object(value, ["schemaVersion", "solution", "instanceId", "knowledgeBases"]);
    object(value.solution, ["name", "description"]);
    if (value.schemaVersion !== 1) throw new Error("Unsupported schemaVersion. Open the project configuration to repair it.");
    if (typeof value.solution.name !== "string" || !value.solution.name.trim() || value.solution.name.length > 120 ||
        typeof value.solution.description !== "string" || value.solution.description.length > 2000)
        throw new Error("Solution name must have 1–120 characters and description at most 2,000 characters.");
    if (typeof value.instanceId !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.instanceId))
        throw new Error("A valid catalog instanceId is required.");
    if (!Array.isArray(value.knowledgeBases)) throw new Error("knowledgeBases must be an array.");
    const ids = new Set<string>();
    for (const reference of value.knowledgeBases) {
        object(reference, ["sourceId"]);
        if (typeof reference.sourceId !== "string" || !reference.sourceId.trim() || ids.has(reference.sourceId))
            throw new Error("Source IDs must be distinct, nonblank strings.");
        ids.add(reference.sourceId);
    }
    // Lexically inspect object properties, catching duplicates that JSON.parse would discard.
    const objectKeys: Set<string>[] = [];
    for (let i = 0; i < text.length; i++) {
        if (text[i] === "{") objectKeys.push(new Set());
        else if (text[i] === "}") objectKeys.pop();
        else if (text[i] === '"') {
            const start = i++;
            while (i < text.length && text[i] !== '"') { if (text[i] === "\\") i++; i++; }
            const token: string = JSON.parse(text.slice(start, i + 1));
            if (/^\s*:/.test(text.slice(i + 1))) {
                const keys = objectKeys[objectKeys.length - 1];
                if (keys?.has(token)) throw new Error("Duplicate configuration properties are not supported.");
                keys?.add(token);
            }
        }
    }
    return value as unknown as ProjectConfiguration;
}

export function selectedSourceIds(config: ProjectConfiguration, instanceId: string, knownIds: readonly string[]): string[] {
    if (config.instanceId.toLowerCase() !== instanceId.toLowerCase()) throw new Error("Catalog instance mismatch. Rebind the selected configuration explicitly.");
    const ids = config.knowledgeBases.map(kb => kb.sourceId);
    const missing = ids.filter(id => !knownIds.includes(id));
    if (missing.length) throw new Error(`Unknown/deleted source IDs: ${missing.join(", ")}. Open project configuration to repair references.`);
    if (!ids.length) throw new Error("No knowledge bases selected. Use a knowledge base in this project before searching.");
    return ids;
}

/** Replaces only the root membership array; every byte outside the array survives. */
export function editProjectReference(text: string, sourceId: string, remove = false): string {
    const config = parseProjectConfiguration(text);
    if (!sourceId.trim()) throw new Error("A source ID is required.");
    const refs = config.knowledgeBases.filter(kb => !remove || kb.sourceId !== sourceId);
    if (!remove && !refs.some(kb => kb.sourceId === sourceId)) refs.push({ sourceId });
    let depth = 0;
    for (let i = 0; i < text.length; i++) {
        if (text[i] === "{") depth++;
        else if (text[i] === "}") depth--;
        else if (text[i] === '"') {
            const start = i++;
            while (i < text.length && text[i] !== '"') { if (text[i] === "\\") i++; i++; }
            if (depth !== 1 || JSON.parse(text.slice(start, i + 1)) !== "knowledgeBases") continue;
            let begin = text.indexOf(":", i + 1) + 1;
            while (/\s/.test(text[begin])) begin++;
            let end = begin + 1;
            for (; end < text.length; end++) {
                if (text[end] === '"') { end++; while (text[end] !== '"') { if (text[end] === "\\") end++; end++; } }
                else if (text[end] === "]") break;
            }
            return text.slice(0, begin) + JSON.stringify(refs) + text.slice(end + 1);
        }
    }
    throw new Error("knowledgeBases property is missing.");
}

export interface ProjectFileSnapshot { content: string | null; revision: string | null }
export interface ProjectEditHost {
    read(): Promise<ProjectFileSnapshot>;
    validate(content: string): Promise<unknown>;
    save(content: string, revision: string | null): Promise<unknown>;
}
export interface ProjectBuffer {
    text(): string;
    version(): number;
    apply(text: string, expectedVersion: number): Promise<boolean>;
}

/** The editor owns open buffers; the host owns atomic closed-file writes. Both check revisions. */
export async function applyReferenceChange(host: ProjectEditHost, sourceId: string, remove: boolean,
    initialContent: () => Promise<string>, buffer?: ProjectBuffer,
    approve: (proposed: string) => Promise<boolean> = async () => true): Promise<"buffer" | "saved" | "cancelled"> {
    const previous = await host.read();
    const version = buffer?.version();
    const original = buffer?.text() ?? previous.content ?? await initialContent();
    const proposed = editProjectReference(original, sourceId, remove);
    await host.validate(proposed);
    if (!await approve(proposed)) return "cancelled";
    if (buffer) {
        const latest = await host.read();
        if (buffer.version() !== version || latest.revision !== previous.revision) throw new Error("Configuration changed. Review current content and retry.");
        if (!await buffer.apply(proposed, version!)) throw new Error("Editor refused the reference edit. Review the buffer before retrying.");
        return "buffer";
    }
    await host.save(proposed, previous.revision);
    return "saved";
}
