import * as crypto from "node:crypto";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import { ChildProcess, spawn } from "node:child_process";
import * as vscode from "vscode";

interface SourceResponse {
    sourceId: string;
    displayName: string;
    status: string;
    lastScanUtc?: string;
    lastSuccessfulIndexUtc?: string;
    lastError?: string;
}

interface BackendDiscovery {
    endpoint: string;
    processId: number;
    tokenReference: string;
}

let backend: ChildProcess | undefined;

export function activate(context: vscode.ExtensionContext): void {
    const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    status.text = "$(database) Local RAG";
    status.tooltip = "Local RAG backend is not connected";
    status.command = "localRag.showSourceStatus";
    status.show();
    context.subscriptions.push(status);

    context.subscriptions.push(vscode.commands.registerCommand("localRag.markAsSource", async (uri?: vscode.Uri) => {
        const folder = await resolveFolder(uri);
        if (!folder) return;
        const client = await ensureBackend(context, status);
        const source = await client.post<SourceResponse>("/api/v1/sources", { rootPath: folder.fsPath });
        vscode.window.showInformationMessage(`Local RAG is indexing ${source.displayName}.`);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.removeSource", async () => {
        const client = await ensureBackend(context, status);
        const source = await pickSource(client);
        if (!source) return;
        await client.delete(`/api/v1/sources/${encodeURIComponent(source.sourceId)}`);
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
        const sources = await client.get<SourceResponse[]>("/api/v1/sources");
        if (sources.length === 0) {
            vscode.window.showInformationMessage("No Local RAG sources are registered.");
            return;
        }
        const selected = await pickSource(client, sources);
        if (selected) vscode.window.showInformationMessage(`${selected.displayName}: ${selected.status}${selected.lastError ? ` — ${selected.lastError}` : ""}`);
    }));
    context.subscriptions.push({ dispose: () => backend?.kill() });
}

async function ensureBackend(context: vscode.ExtensionContext, status: vscode.StatusBarItem): Promise<LocalRagClient> {
    const settings = vscode.workspace.getConfiguration("localRag");
    const endpoint = settings.get<string>("backendEndpoint", "http://127.0.0.1:5198").replace(/\/$/, "");
    const token = await getOrCreateToken(context);
    const client = new LocalRagClient(endpoint, token);
    if (!await client.isLive()) {
        await launchBackend(context, endpoint, token, settings.get<string>("weaviateEndpoint", "http://127.0.0.1:8080"));
    }
    await waitForReady(client);
    status.text = "$(database) Local RAG";
    status.tooltip = `Connected to ${endpoint}`;
    return client;
}

async function launchBackend(context: vscode.ExtensionContext, endpoint: string, token: string, weaviateEndpoint: string): Promise<void> {
    if (backend && !backend.killed) return;
    const executable = path.join(context.extensionPath, "bin", "win32-x64", "LocalRag.Host.exe");
    try { await fs.access(executable); } catch {
        throw new Error(`Local RAG backend was not packaged at ${executable}. Run scripts/Publish-Backend.ps1 before installing the extension.`);
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

async function pickSource(client: LocalRagClient, sources?: SourceResponse[]): Promise<SourceResponse | undefined> {
    const values = sources ?? await client.get<SourceResponse[]>("/api/v1/sources");
    const picks = values.map(source => ({ label: source.displayName, description: source.status, source }));
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
