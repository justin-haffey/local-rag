import * as crypto from "node:crypto";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import { ChildProcess, spawn } from "node:child_process";
import * as vscode from "vscode";
import { resolveInstallerConfiguration } from "./installerSettings";
import { environmentMcpToken, isMissingSource, recoveryPresentation, rootPathHash, SourceState } from "./sourceState";

interface BackendDiscovery {
    endpoint: string;
    processId: number;
    tokenReference: string;
}

let backend: ChildProcess | undefined;
let sourceStatusIndicator: vscode.StatusBarItem | undefined;
const sourceStateKey = "localRag.sourceState";

class SourceDecorationProvider implements vscode.FileDecorationProvider {
    private readonly changed = new vscode.EventEmitter<vscode.Uri | vscode.Uri[] | undefined>();
    private registeredRoots = new Set<string>();

    public readonly onDidChangeFileDecorations = this.changed.event;

    public update(sources: readonly SourceState[]): void {
        this.registeredRoots = new Set(sources.map(source => source.rootPathHash));
        this.changed.fire(undefined);
    }

    public provideFileDecoration(uri: vscode.Uri): vscode.ProviderResult<vscode.FileDecoration> {
        if (uri.scheme !== "file" || !this.registeredRoots.has(rootPathHash(uri.fsPath))) return undefined;
        return {
            badge: "R",
            tooltip: "Local RAG source",
            color: new vscode.ThemeColor("gitDecoration.addedResourceForeground"),
            propagate: false
        };
    }

    public dispose(): void {
        this.changed.dispose();
    }
}

export function activate(context: vscode.ExtensionContext): void {
    const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    status.text = "$(database) Local RAG";
    status.tooltip = "Local RAG backend is not connected";
    status.command = "localRag.showSourceStatus";
    status.show();
    sourceStatusIndicator = status;
    context.subscriptions.push(status);

    const decorations = new SourceDecorationProvider();
    decorations.update(context.globalState.get<SourceState[]>(sourceStateKey, []));
    context.subscriptions.push(decorations, vscode.window.registerFileDecorationProvider(decorations));

    context.subscriptions.push(vscode.commands.registerCommand("localRag.markAsSource", async (uri?: vscode.Uri) => {
        const folder = await resolveFolder(uri);
        if (!folder) return;
        const client = await ensureBackend(context, status);
        const sources = await refreshSourceState(context, decorations, client);
        const existing = sources.find(source => source.rootPathHash === rootPathHash(folder.fsPath));
        if (existing) {
            await client.delete(`/api/v1/sources/${encodeURIComponent(existing.sourceId)}`);
            await refreshSourceState(context, decorations, client);
            vscode.window.showInformationMessage(`Removed ${existing.displayName} from Local RAG.`);
            return;
        }

        const source = await client.post<SourceState>("/api/v1/sources", { rootPath: folder.fsPath });
        const refreshed = await refreshSourceState(context, decorations, client);
        vscode.window.showInformationMessage(`Local RAG is indexing ${source.displayName}.`);
        const stale = refreshed.filter(candidate =>
            candidate.sourceId !== source.sourceId &&
            candidate.displayName.localeCompare(source.displayName, undefined, { sensitivity: "accent" }) === 0 &&
            isMissingSource(candidate));
        if (stale.length > 0) {
            const action = await vscode.window.showWarningMessage(
                `Local RAG found ${stale.length} stale index${stale.length === 1 ? "" : "es"} for a previous location of ${source.displayName}.`,
                "Remove stale index");
            if (action === "Remove stale index") {
                for (const candidate of stale) {
                    await client.delete(`/api/v1/sources/${encodeURIComponent(candidate.sourceId)}`);
                }
                await refreshSourceState(context, decorations, client);
            }
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.removeSource", async () => {
        const client = await ensureBackend(context, status);
        const source = await pickSource(client);
        if (!source) return;
        await client.delete(`/api/v1/sources/${encodeURIComponent(source.sourceId)}`);
        await refreshSourceState(context, decorations, client);
        vscode.window.showInformationMessage(`Removed ${source.displayName} from Local RAG.`);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.reindexSource", async () => {
        const client = await ensureBackend(context, status);
        const source = await pickSource(client);
        if (!source) return;
        await client.post(`/api/v1/sources/${encodeURIComponent(source.sourceId)}/reindex`, undefined);
        vscode.window.showInformationMessage(`Reindex queued for ${source.displayName}.`);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.showSourceStatus", async () => {
        const client = await ensureBackend(context, status);
        const sources = await refreshSourceState(context, decorations, client);
        if (sources.length === 0) {
            vscode.window.showInformationMessage("No Local RAG sources are registered.");
            return;
        }
        const selected = await pickSource(client, sources);
        if (!selected) return;
        if (selected.recovery && !isMissingSource(selected)) {
            const presentation = recoveryPresentation(selected);
            const message = `${selected.displayName}: ${presentation.label}${presentation.detail ? ` - ${presentation.detail}` : ""}`;
            if (presentation.action) {
                const action = await vscode.window.showWarningMessage(message, presentation.action);
                if (action === "Queue recovery") {
                    await client.post(`/api/v1/sources/${encodeURIComponent(selected.sourceId)}/reindex`, undefined);
                    vscode.window.showInformationMessage(`Recovery queued for ${selected.displayName}.`);
                }
            } else {
                vscode.window.showInformationMessage(message);
            }
            return;
        }
        if (isMissingSource(selected)) {
            const action = await vscode.window.showWarningMessage(
                `${selected.displayName}: ${selected.status} — ${selected.lastError}`,
                "Remove stale source");
            if (action === "Remove stale source") {
                await client.delete(`/api/v1/sources/${encodeURIComponent(selected.sourceId)}`);
                await refreshSourceState(context, decorations, client);
            }
            return;
        }
        vscode.window.showInformationMessage(`${selected.displayName}: ${selected.status}${selected.lastError ? ` — ${selected.lastError}` : ""}`);
    }));
    context.subscriptions.push({ dispose: () => backend?.kill() });

    void ensureBackend(context, status)
        .then(client => refreshSourceState(context, decorations, client))
        .catch(error => {
            status.tooltip = error instanceof Error ? error.message : String(error);
        });
}

async function refreshSourceState(
    context: vscode.ExtensionContext,
    decorations: SourceDecorationProvider,
    client: LocalRagClient): Promise<SourceState[]> {
    const sources = await client.get<SourceState[]>("/api/v1/sources");
    decorations.update(sources);
    await context.globalState.update(sourceStateKey, sources);
    updateStatusIndicator(sources);
    return sources;
}

function updateStatusIndicator(sources: readonly SourceState[]): void {
    if (!sourceStatusIndicator) return;
    const degraded = sources.filter(source => source.recovery?.state.toLowerCase() === "degraded").length;
    const recovering = sources.filter(source => ["queued", "running"].includes(source.recovery?.state.toLowerCase() ?? "")).length;
    if (degraded > 0) {
        sourceStatusIndicator.text = `$(warning) Local RAG (${degraded})`;
        sourceStatusIndicator.tooltip = `${degraded} source${degraded === 1 ? " needs" : "s need"} recovery attention. Select to review and retry.`;
    } else if (recovering > 0) {
        sourceStatusIndicator.text = `$(sync~spin) Local RAG (${recovering})`;
        sourceStatusIndicator.tooltip = `${recovering} source${recovering === 1 ? " is" : "s are"} recovering automatically.`;
    } else {
        sourceStatusIndicator.text = "$(database) Local RAG";
        sourceStatusIndicator.tooltip = `${sources.length} registered source${sources.length === 1 ? "" : "s"}; recovery state is healthy.`;
    }
}

async function ensureBackend(context: vscode.ExtensionContext, status: vscode.StatusBarItem): Promise<LocalRagClient> {
    const settings = vscode.workspace.getConfiguration("localRag");
    const endpoint = settings.get<string>("backendEndpoint", "http://127.0.0.1:5198").replace(/\/$/, "");
    const installed = await resolveInstallerConfiguration(settings, process.env.LOCALAPPDATA);
    const token = await getOrCreateToken(context);
    const client = new LocalRagClient(endpoint, token);
    if (!await client.isLive()) {
        await launchBackend(context, installed.hostExecutable, endpoint, token, installed.weaviateEndpoint);
    }
    await waitForReady(client);
    status.text = "$(database) Local RAG";
    status.tooltip = `Connected to ${endpoint}`;
    return client;
}

async function launchBackend(context: vscode.ExtensionContext, executable: string, endpoint: string, token: string, weaviateEndpoint: string): Promise<void> {
    if (backend && !backend.killed) return;
    try { await fs.access(executable); } catch {
        throw new Error(`Local RAG standalone host was not found at ${executable}. Repair or reinstall Local RAG.`);
    }
    backend = spawn(executable, [], {
        windowsHide: true,
        env: {
            ...process.env,
            ASPNETCORE_URLS: endpoint,
            LocalRag__Authentication__Token: token,
            LocalRag__Weaviate__Endpoint: weaviateEndpoint
        }
    });
    backend.stderr?.on("data", data => console.error(`Local RAG backend: ${data}`));
    backend.on("exit", () => { backend = undefined; });
    const dataDirectory = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "LocalRag") : context.globalStorageUri.fsPath;
    await fs.mkdir(dataDirectory, { recursive: true });
    const discovery: BackendDiscovery = { endpoint, processId: backend.pid ?? 0, tokenReference: "VS Code SecretStorage: localRag.token" };
    await fs.writeFile(path.join(dataDirectory, "backend.json"), JSON.stringify(discovery, undefined, 2));
}

async function getOrCreateToken(context: vscode.ExtensionContext): Promise<string> {
    const sharedMcpToken = environmentMcpToken(process.env.LOCALRAG_MCP_TOKEN);
    if (sharedMcpToken) return sharedMcpToken;

    const key = "localRag.token";
    const existing = await context.secrets.get(key);
    if (existing) return existing;
    const token = crypto.randomBytes(32).toString("base64url");
    await context.secrets.store(key, token);
    return token;
}

async function waitForReady(client: LocalRagClient): Promise<void> {
    for (let attempt = 0; attempt < 30; attempt++) {
        if (await client.isReady()) return;
        await new Promise(resolve => setTimeout(resolve, 500));
    }
    throw new Error("The Local RAG backend did not become ready. Confirm Weaviate is running at the configured endpoint, the RagChunk_v1 schema is compatible, and run scripts/Install-LocalRagEmbeddingModel.ps1 to install verified ONNX assets.");
}

async function resolveFolder(uri?: vscode.Uri): Promise<vscode.Uri | undefined> {
    if (uri) return uri;
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (workspaceFolder) return workspaceFolder.uri;
    vscode.window.showErrorMessage("Open a folder before registering a Local RAG source.");
    return undefined;
}

async function pickSource(client: LocalRagClient, sources?: SourceState[]): Promise<SourceState | undefined> {
    const values = sources ?? await client.get<SourceState[]>("/api/v1/sources");
    const picks = values.map(source => ({ label: source.displayName, description: recoveryPresentation(source).label, source }));
    return (await vscode.window.showQuickPick(picks, { placeHolder: "Select a Local RAG source" }))?.source;
}

class LocalRagClient {
    public constructor(private readonly endpoint: string, private readonly token: string) { }

    public async isLive(): Promise<boolean> {
        try { return (await fetch(`${this.endpoint}/health/live`)).ok; } catch { return false; }
    }
    public async isReady(): Promise<boolean> {
        try { return (await fetch(`${this.endpoint}/health/ready`)).ok; } catch { return false; }
    }
    public get<T>(pathName: string): Promise<T> { return this.request<T>(pathName, "GET"); }
    public post<T>(pathName: string, body: unknown): Promise<T> { return this.request<T>(pathName, "POST", body); }
    public delete(pathName: string): Promise<void> { return this.request<void>(pathName, "DELETE"); }

    private async request<T>(pathName: string, method: string, body?: unknown): Promise<T> {
        const response = await fetch(`${this.endpoint}${pathName}`, {
            method,
            headers: { "Authorization": `Bearer ${this.token}`, "Content-Type": "application/json" },
            body: body === undefined ? undefined : JSON.stringify(body)
        });
        if (!response.ok) throw new Error(await response.text());
        if (response.status === 204 || response.status === 202) return undefined as T;
        return await response.json() as T;
    }
}
