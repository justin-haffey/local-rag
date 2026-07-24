import { rootPathHash, SourceState } from "./sourceState";
import * as path from "node:path";
import { promises as fs } from "node:fs";

export interface SourceControlClient {
    post<T>(pathName: string, body: unknown): Promise<T>;
    delete(pathName: string): Promise<void>;
}

export interface SearchContextResult {
    chunkId: string;
    sourceId: string;
    relativePath: string;
    startLine: number;
    endLine: number;
    content: string;
}

export interface SearchContextSelection {
    chunkId: string;
    sourceId: string;
    relativePath: string;
}

export class LatestRequest {
    private generation = 0;

    public begin(): number { return ++this.generation; }
    public isCurrent(generation: number): boolean { return generation === this.generation; }
}

/**
 * Resolves an Explorer folder to its current backend source summary. The opaque
 * root hash keeps canonical paths inside the extension and host boundary.
 */
export function sourceForFolder(sources: readonly SourceState[], folderPath: string): SourceState | undefined {
    const folderHash = rootPathHash(folderPath);
    return sources.find(source => source.rootPathHash === folderHash);
}

export function isDirectory(fileType: number, directoryFlag: number): boolean {
    return (fileType & directoryFlag) !== 0;
}

export async function indexSelectedFolder(
    client: SourceControlClient,
    existing: SourceState | undefined,
    folderPath: string): Promise<{ kind: "already-indexed"; source: SourceState } | { kind: "indexed"; source: SourceState }> {
    if (existing) return { kind: "already-indexed", source: existing };
    const source = await client.post<SourceState>("/api/v1/sources", { rootPath: folderPath });
    return { kind: "indexed", source };
}

export async function toggleSelectedFolder(
    client: SourceControlClient,
    existing: SourceState | undefined,
    folderPath: string,
    confirmRemoval: (source: SourceState) => Promise<boolean>): Promise<"cancelled" | "indexed" | "removed"> {
    if (!existing) {
        await indexSelectedFolder(client, undefined, folderPath);
        return "indexed";
    }
    if (!await confirmRemoval(existing)) return "cancelled";
    await client.delete(`/api/v1/sources/${encodeURIComponent(existing.sourceId)}`);
    return "removed";
}

export async function refreshSelectedFolder(client: SourceControlClient, existing: SourceState | undefined): Promise<boolean> {
    if (!existing) return false;
    await client.post(`/api/v1/sources/${encodeURIComponent(existing.sourceId)}/reindex`, undefined);
    return true;
}

export function workspaceFilePath(workspaceRoot: string, relativePath: string): string | undefined {
    if (!relativePath || path.isAbsolute(relativePath)) return undefined;
    const candidate = path.resolve(workspaceRoot, relativePath);
    const relative = path.relative(workspaceRoot, candidate);
    if (!relative || relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) return undefined;
    return candidate;
}

export async function realWorkspaceFilePath(
    workspaceRoot: string,
    relativePath: string,
    realpath: (pathName: string) => Promise<string> = fs.realpath): Promise<string | undefined> {
    const candidate = workspaceFilePath(workspaceRoot, relativePath);
    if (!candidate) return undefined;
    try {
        const [realRoot, realCandidate] = await Promise.all([realpath(workspaceRoot), realpath(candidate)]);
        const relative = path.relative(realRoot, realCandidate);
        if (!relative || relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) return undefined;
        return realCandidate;
    } catch {
        return undefined;
    }
}

export function boundedContextText(
    context: SearchContextResult,
    selected: SearchContextSelection,
    maximumCharacters: number): string | undefined {
    if (context.chunkId !== selected.chunkId || context.sourceId !== selected.sourceId || context.relativePath !== selected.relativePath) return undefined;
    const content = context.content.slice(0, maximumCharacters);
    const truncation = context.content.length > content.length ? "\n\n[Context truncated by Local RAG]" : "";
    return `${context.relativePath}:${context.startLine}-${context.endLine}\n\n${content}${truncation}`;
}
