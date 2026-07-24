import * as crypto from "node:crypto";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import { ChildProcess, spawn } from "node:child_process";
import * as vscode from "vscode";
import { resolveInstallerConfiguration } from "./installerSettings";
import { environmentMcpToken, isMissingSource, recoveryPresentation, rootPathHash, SourceState } from "./sourceState";
import { boundedContextText, indexSelectedFolder, isDirectory, LatestRequest, realWorkspaceFilePath, refreshSelectedFolder, sourceForFolder, toggleSelectedFolder } from "./sourceControls";

interface BackendDiscovery {
    endpoint: string;
    processId: number;
    tokenReference: string;
}

let backend: ChildProcess | undefined;
let sourceStatusIndicator: vscode.StatusBarItem | undefined;
let localRagView: LocalRagProvider | undefined;
let activeSearchClient: LocalRagClient | undefined;
let activeSearchAbortController: AbortController | undefined;
const latestSearchRequest = new LatestRequest();
const sourceStateKey = "localRag.sourceState";
const maximumCopiedContextCharacters = 12000;

interface SearchResult {
    chunkId: string;
    sourceId: string;
    relativePath: string;
    language: string;
    symbolName?: string;
    startLine: number;
    endLine: number;
    score: number;
    content: string;
}

interface SearchResponse {
    query: string;
    results: SearchResult[];
    candidateCount: number;
    elapsedMilliseconds: number;
    truncated: boolean;
}

interface ChunkContext {
    chunkId: string;
    sourceId: string;
    relativePath: string;
    startLine: number;
    endLine: number;
    content: string;
}

class SearchResultItem extends vscode.TreeItem {
    public constructor(public readonly result: SearchResult) {
        super(`${result.relativePath}:${result.startLine}`, vscode.TreeItemCollapsibleState.None);
        this.description = `${result.language}  •  ${result.score.toFixed(2)}`;
        this.tooltip = `${result.symbolName ?? result.relativePath} (${result.startLine}-${result.endLine})`;
        this.contextValue = "localRag.searchResult";
        this.command = { command: "localRag.openSearchResult", title: "Open Local RAG result", arguments: [this] };
    }
}

class IndexedFolderItem extends vscode.TreeItem {
    public constructor(source: SourceState) {
        super(source.displayName, vscode.TreeItemCollapsibleState.None);
        this.description = source.status;
        this.tooltip = `${source.displayName}: ${source.status}`;
        this.contextValue = "localRag.indexedFolder";
        this.iconPath = new vscode.ThemeIcon("database");
    }
}

class IndexedFoldersSection extends vscode.TreeItem {
    public constructor(count: number) {
        super("Indexed Folders", vscode.TreeItemCollapsibleState.Expanded);
        this.description = `${count}`;
        this.iconPath = new vscode.ThemeIcon("folder-library");
    }
}

class SearchResultsSection extends vscode.TreeItem {
    public constructor(count: number) {
        super("Search Results", vscode.TreeItemCollapsibleState.Expanded);
        this.description = count === 0 ? "Run Search to begin" : `${count}`;
        this.iconPath = new vscode.ThemeIcon("search");
    }
}

class LocalRagMessageItem extends vscode.TreeItem {
    public constructor(label: string) {
        super(label, vscode.TreeItemCollapsibleState.None);
        this.iconPath = new vscode.ThemeIcon("info");
    }
}

type LocalRagTreeItem = IndexedFoldersSection | SearchResultsSection | IndexedFolderItem | SearchResultItem | LocalRagMessageItem;

class LocalRagProvider implements vscode.TreeDataProvider<LocalRagTreeItem> {
    private readonly changed = new vscode.EventEmitter<void>();
    private sources: readonly SourceState[] = [];
    private results: readonly SearchResult[] = [];

    public readonly onDidChangeTreeData = this.changed.event;

    public updateSources(sources: readonly SourceState[]): void {
        this.sources = [...sources].sort((left, right) => left.displayName.localeCompare(right.displayName));
        this.changed.fire();
    }

    public updateResults(results: readonly SearchResult[]): void {
        this.results = results;
        this.changed.fire();
    }

    public getTreeItem(item: LocalRagTreeItem): vscode.TreeItem { return item; }
    public getChildren(item?: LocalRagTreeItem): LocalRagTreeItem[] {
        if (!item) return [new IndexedFoldersSection(this.sources.length), new SearchResultsSection(this.results.length)];
        if (item instanceof IndexedFoldersSection) {
            return this.sources.length === 0
                ? [new LocalRagMessageItem("No project folders are indexed. Right-click a local folder and choose Local RAG > Index Folder.")]
                : this.sources.map(source => new IndexedFolderItem(source));
        }
        if (item instanceof SearchResultsSection) {
            return this.results.length === 0
                ? [new LocalRagMessageItem("No search results yet. Use the Search button above.")]
                : this.results.map(result => new SearchResultItem(result));
        }
        return [];
    }
    public dispose(): void { this.changed.dispose(); }
}

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
    const localRag = new LocalRagProvider();
    localRag.updateSources(context.globalState.get<SourceState[]>(sourceStateKey, []));
    localRagView = localRag;
    context.subscriptions.push(localRag, vscode.window.registerTreeDataProvider("localRag.main", localRag));

    context.subscriptions.push(vscode.commands.registerCommand("localRag.search", async () => {
        const query = await vscode.window.showInputBox({ prompt: "Search Local RAG", placeHolder: "Describe code, configuration, or an error" });
        if (!query?.trim()) return;
        const client = await ensureBackend(context, status);
        const sources = await refreshSourceState(context, decorations, client);
        const selections = await vscode.window.showQuickPick(
            sources.map(source => ({ label: source.displayName, description: source.status, sourceId: source.sourceId })),
            { canPickMany: true, placeHolder: "Select sources to search (leave empty for all visible sources)" });
        if (selections === undefined) return;
        const mode = await vscode.window.showQuickPick([
            { label: "Hybrid", value: "Hybrid", description: "Blend lexical and vector matching" },
            { label: "Lexical", value: "Lexical", description: "Prioritize exact terms" },
            { label: "Vector", value: "Vector", description: "Prioritize semantic similarity" }
        ], { placeHolder: "Choose search mode" });
        if (!mode) return;
        const language = await vscode.window.showInputBox({ prompt: "Optional language filter", placeHolder: "Example: csharp, typescript, markdown" });
        if (language === undefined) return;
        const pathPrefix = await vscode.window.showInputBox({ prompt: "Optional workspace-relative path filter", placeHolder: "Example: src/LocalRag.Host/" });
        if (pathPrefix === undefined) return;
        activeSearchAbortController?.abort();
        const controller = new AbortController();
        const generation = latestSearchRequest.begin();
        activeSearchAbortController = controller;
        try {
            const response = await client.post<SearchResponse>("/api/v1/search", {
                query: query.trim(),
                sourceIds: selections.length === 0 ? undefined : selections.map(selection => selection.sourceId),
                limit: 25,
                alpha: 0.65,
                mode: mode.value,
                language: language.trim() || undefined,
                pathPrefix: pathPrefix.trim() || undefined
            }, controller.signal);
            if (!latestSearchRequest.isCurrent(generation)) return;
            activeSearchClient = client;
            localRag.updateResults(response.results);
            const suffix = response.truncated ? " Results were truncated." : "";
            vscode.window.showInformationMessage(`${response.results.length} Local RAG result${response.results.length === 1 ? "" : "s"} in ${response.elapsedMilliseconds} ms.${suffix}`);
        } catch (error) {
            if (controller.signal.aborted) return;
            localRag.updateResults([]);
            vscode.window.showWarningMessage(`Local RAG search is unavailable. ${safeSearchError(error)}`);
        } finally {
            if (activeSearchAbortController === controller) activeSearchAbortController = undefined;
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.openSearchResult", async (item?: SearchResultItem) => {
        if (!item) return;
        const target = await resolveSearchResultPath(item.result, context.globalState.get<SourceState[]>(sourceStateKey, []));
        if (!target) {
            vscode.window.showWarningMessage("This Local RAG result is not inside an open matching workspace folder.");
            return;
        }
        try {
            const document = await vscode.workspace.openTextDocument(vscode.Uri.file(target));
            const editor = await vscode.window.showTextDocument(document, { preview: true });
            const line = Math.max(0, item.result.startLine - 1);
            editor.selection = new vscode.Selection(line, 0, line, 0);
            editor.revealRange(new vscode.Range(line, 0, line, 0), vscode.TextEditorRevealType.InCenter);
        } catch {
            vscode.window.showWarningMessage("The indexed file is no longer available at its safe workspace-relative path.");
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.copySearchContext", async (item?: SearchResultItem) => {
        if (!item || !activeSearchClient) return;
        try {
            const chunk = await activeSearchClient.get<ChunkContext>(`/api/v1/chunks/${encodeURIComponent(item.result.chunkId)}`);
            const text = boundedContextText(chunk, item.result, maximumCopiedContextCharacters);
            if (!text) {
                vscode.window.showWarningMessage("Local RAG context no longer matches the selected result.");
                return;
            }
            await vscode.env.clipboard.writeText(text);
            vscode.window.showInformationMessage("Bounded Local RAG context copied with provenance.");
        } catch {
            vscode.window.showWarningMessage("Local RAG context is unavailable for the selected result.");
        }
    }));

    context.subscriptions.push(vscode.commands.registerCommand("localRag.indexFolder", async (uri?: vscode.Uri) => {
        await indexFolder(context, decorations, status, uri);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.toggleIndexing", async (uri?: vscode.Uri) => {
        const target = await selectedFolderSource(context, decorations, status, uri);
        if (!target) return;
        const result = await toggleSelectedFolder(target.client, target.source, target.folder.fsPath, async source => {
            const action = await vscode.window.showWarningMessage(
                `Stop indexing ${source.displayName} and remove its Local RAG source?`,
                { modal: true },
                "Stop indexing");
            return action === "Stop indexing";
        });
        if (result === "cancelled") return;
        await refreshSourceState(context, decorations, target.client);
        if (result === "removed" && target.source) {
            vscode.window.showInformationMessage(`Removed ${target.source.displayName} from Local RAG.`);
        } else if (result === "indexed") {
            vscode.window.showInformationMessage("Local RAG is indexing this folder.");
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.refreshFolderIndex", async (uri?: vscode.Uri) => {
        const target = await selectedFolderSource(context, decorations, status, uri);
        if (!target) return;
        if (!await refreshSelectedFolder(target.client, target.source)) {
            vscode.window.showInformationMessage("This folder is not indexed by Local RAG. Choose Index Folder first.");
            return;
        }
        if (!target.source) return;
        vscode.window.showInformationMessage(`Reindex queued for ${target.source.displayName}.`);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.showFolderStatus", async (uri?: vscode.Uri) => {
        const target = await selectedFolderSource(context, decorations, status, uri);
        if (!target) return;
        if (!target.source) {
            vscode.window.showInformationMessage("This folder is not indexed by Local RAG.");
            return;
        }
        await showSourceStatus(target.source, target.client, context, decorations);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.markAsSource", async (uri?: vscode.Uri) => {
        await vscode.commands.executeCommand("localRag.toggleIndexing", uri);
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
        await showSourceStatus(selected, client, context, decorations);
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
    localRagView?.updateSources(sources);
    await context.globalState.update(sourceStateKey, sources);
    updateStatusIndicator(sources);
    return sources;
}

interface SelectedFolderSource {
    folder: vscode.Uri;
    client: LocalRagClient;
    source: SourceState | undefined;
}

async function selectedFolderSource(
    context: vscode.ExtensionContext,
    decorations: SourceDecorationProvider,
    status: vscode.StatusBarItem,
    uri?: vscode.Uri): Promise<SelectedFolderSource | undefined> {
    const folder = await resolveFolder(uri);
    if (!folder) return undefined;
    const client = await ensureBackend(context, status);
    const sources = await refreshSourceState(context, decorations, client);
    return { folder, client, source: sourceForFolder(sources, folder.fsPath) };
}

async function indexFolder(
    context: vscode.ExtensionContext,
    decorations: SourceDecorationProvider,
    status: vscode.StatusBarItem,
    uri?: vscode.Uri): Promise<void> {
    const target = await selectedFolderSource(context, decorations, status, uri);
    if (!target) return;
    const result = await indexSelectedFolder(target.client, target.source, target.folder.fsPath);
    if (result.kind === "already-indexed") {
        vscode.window.showInformationMessage(`${result.source.displayName} is already indexed by Local RAG.`);
        return;
    }
    await refreshSourceState(context, decorations, target.client);
    vscode.window.showInformationMessage(`Local RAG is indexing ${result.source.displayName}.`);
}

async function showSourceStatus(
    selected: SourceState,
    client: LocalRagClient,
    context: vscode.ExtensionContext,
    decorations: SourceDecorationProvider): Promise<void> {
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
}

async function resolveSearchResultPath(result: SearchResult, sources: readonly SourceState[]): Promise<string | undefined> {
    const source = sources.find(candidate => candidate.sourceId === result.sourceId);
    if (!source) return undefined;
    const workspaceFolder = vscode.workspace.workspaceFolders?.find(folder => rootPathHash(folder.uri.fsPath) === source.rootPathHash);
    return workspaceFolder ? realWorkspaceFilePath(workspaceFolder.uri.fsPath, result.relativePath) : undefined;
}

function safeSearchError(error: unknown): string {
    if (error instanceof DOMException && error.name === "AbortError") return "The request was cancelled.";
    if (error instanceof LocalRagClientError) {
        switch (error.status) {
            case 401: return "Authentication failed. Reconnect Local RAG or refresh its token.";
            case 403: return "The selected source is not available to this Local RAG client.";
            case 400: return "The query or selected filters are invalid.";
            case 408:
            case 504: return "The request timed out. Narrow the query or try again.";
            default: return "Confirm the local host and selected sources are available.";
        }
    }
    return "Confirm the local host is running and the selected sources are available.";
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
    if (uri) {
        try {
            const stat = await vscode.workspace.fs.stat(uri);
            if (isDirectory(stat.type, vscode.FileType.Directory)) return uri;
        } catch {
            // Fall through to the folder-only message below.
        }
        vscode.window.showErrorMessage("Choose a local folder to use Local RAG Explorer actions.");
        return undefined;
    }
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
    public post<T>(pathName: string, body: unknown, signal?: AbortSignal): Promise<T> { return this.request<T>(pathName, "POST", body, signal); }
    public delete(pathName: string): Promise<void> { return this.request<void>(pathName, "DELETE"); }

    private async request<T>(pathName: string, method: string, body?: unknown, signal?: AbortSignal): Promise<T> {
        const response = await fetch(`${this.endpoint}${pathName}`, {
            method,
            headers: { "Authorization": `Bearer ${this.token}`, "Content-Type": "application/json" },
            body: body === undefined ? undefined : JSON.stringify(body),
            signal
        });
        if (!response.ok) throw new LocalRagClientError(response.status);
        if (response.status === 204 || response.status === 202) return undefined as T;
        return await response.json() as T;
    }
}

class LocalRagClientError extends Error {
    public constructor(public readonly status: number) {
        super(`Local RAG request failed with status ${status}.`);
    }
}
