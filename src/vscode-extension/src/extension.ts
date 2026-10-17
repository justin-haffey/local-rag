import * as fs from "node:fs/promises";
import * as path from "node:path";
import { spawn } from "node:child_process";
import * as vscode from "vscode";
import { applyReferenceChange, parseProjectConfiguration, ProjectConfiguration, selectedSourceIds, selectProjectConfigurationPath } from "./projectConfiguration";
import { sharedHostToken } from "./sharedHostToken";
import { providerEnvironment, resolveInstallerConfiguration, ResolvedInstallerConfiguration } from "./installerSettings";
import { environmentMcpToken, isMissingSource, recoveryPresentation, rootPathHash, sourceCanContribute, SourceLifecycle, SourceState, withLifecycle } from "./sourceState";
import { boundedContextText, indexSelectedFolder, isDirectory, LatestRequest, realWorkspaceFilePath, refreshSelectedFolder, sourceForFolder, toggleSelectedFolder } from "./sourceControls";

let sourceStatusIndicator: vscode.StatusBarItem | undefined;
let localRagView: LocalRagProvider | undefined;
let activeSearchClient: LocalRagClient | undefined;
let sourcePollingClient: LocalRagClient | undefined;
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
        const state = recoveryPresentation(source);
        this.description = state.label;
        this.tooltip = `${source.displayName}: ${state.label}${state.detail ? ` — ${state.detail}` : ""}`;
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
        const configUri = await chooseProjectConfiguration();
        if (!configUri) return;
        let sourceIds: string[];
        try {
            const document = await vscode.workspace.openTextDocument(configUri);
            const config = parseProjectConfiguration(document.getText());
            const [catalog, lifecycle] = await Promise.all([client.get<CatalogSnapshot>("/api/v1/catalog"), client.get<SourceLifecycle[]>("/api/v1/lifecycle")]);
            sourceIds = selectedSourceIds(config, catalog.instanceId, catalog.sources.map(source => source.sourceId));
            await client.post("/api/v1/project-config/validate", { content: document.getText() });
            const unavailable = withLifecycle(catalog.sources, lifecycle).filter(source => sourceIds.includes(source.sourceId) && !sourceCanContribute(source));
            if (unavailable.length) throw new Error(`Selected sources cannot contribute: ${unavailable.map(source => `${source.displayName} (${source.sourceId}): ${recoveryPresentation(source).label}`).join(", ")}. Resume or repair their lifecycle state.`);
        } catch (error) {
            const action = await vscode.window.showWarningMessage(`Project search blocked: ${error instanceof Error ? error.message : "Project not configured."}`, "Open project configuration");
            if (action) await vscode.window.showTextDocument(configUri);
            return;
        }
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
                sourceIds,
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
    context.subscriptions.push(vscode.commands.registerCommand("localRag.openProjectConfiguration", async () => {
        const uri = await chooseProjectConfiguration();
        if (!uri) return;
        try { await vscode.window.showTextDocument(uri); }
        catch {
            const client = await ensureBackend(context, status);
            const content = await prepareProjectContent(client, uri);
            await client.post("/api/v1/project-config/save", { projectRoot: path.dirname(uri.fsPath), content, expectedRevision: null });
            await vscode.window.showTextDocument(uri);
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.openManager", async () => {
        const client = await ensureBackend(context, status);
        const endpoint = vscode.workspace.getConfiguration("localRag").get<string>("backendEndpoint", "http://127.0.0.1:5198").replace(/\/$/, "");
        const dataDirectory = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "LocalRag") : context.globalStorageUri.fsPath;
        let managementToken = process.env.LOCALRAG_MANAGEMENT_TOKEN?.trim();
        if (!managementToken) {
            try { managementToken = (await fs.readFile(path.join(dataDirectory, "management-token"), "utf8")).trim(); }
            catch { /* A retrieval-only host can still open a normal manager session. */ }
        }
        const { ticket } = await client.post<{ ticket: string }>("/manage/tickets", { managementToken });
        await vscode.env.openExternal(vscode.Uri.parse(`${endpoint}/manage#ticket=${encodeURIComponent(ticket)}`));
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.useKnowledgeBase", async () => {
        const client = await ensureBackend(context, status);
        const source = await pickSource(client);
        const uri = source ? await chooseProjectConfiguration() : undefined;
        if (uri && source) await changeProjectReference(client, uri, source.sourceId);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.removeProjectReference", async () => {
        const uri = await chooseProjectConfiguration();
        if (!uri) return;
        const client = await ensureBackend(context, status);
        try {
            const document = await vscode.workspace.openTextDocument(uri);
            const config = parseProjectConfiguration(document.getText());
            const source = await vscode.window.showQuickPick(config.knowledgeBases.map(kb => kb.sourceId), { placeHolder: "Remove a reference; indexed data remains available" });
            if (source) await changeProjectReference(client, uri, source, true);
        } catch (error) { vscode.window.showErrorMessage(`Configuration unchanged: ${error instanceof Error ? error.message : String(error)}`); }
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.editKnowledgeBase", async () => {
        const client = await ensureBackend(context, status);
        const catalog = await client.get<CatalogSnapshot>("/api/v1/catalog");
        const selected = await vscode.window.showQuickPick(catalog.sources.map(source => ({ label: source.displayName, description: `${source.scope} | ${source.sourceId} | ${source.canonicalRootPath}`, source })), { placeHolder: "Choose knowledge base to edit (including unclassified legacy sources)" });
        if (!selected) return;
        const project = await chooseProjectConfiguration();
        if (!project) return;
        const metadata = await promptMetadata(selected.source.displayName, path.dirname(project.fsPath), selected.source);
        if (!metadata) return;
        await client.post(`/api/v1/catalog/sources/${encodeURIComponent(selected.source.sourceId)}/metadata`, { ...metadata, expectedRevision: selected.source.metadataRevision });
        await refreshSourceState(context, decorations, client);
    }));
    context.subscriptions.push(vscode.commands.registerCommand("localRag.toggleIndexing", async (uri?: vscode.Uri) => {
        const target = await selectedFolderSource(context, decorations, status, uri);
        if (!target) return;
        if (!target.source) { await indexFolder(context, decorations, status, target.folder); return; }
        const action = target.source.status === "Paused" || target.source.indexingState === "Resuming" || target.source.desiredIndexingState === "Paused" ? "resume" : "pause";
        await target.client.post(`/api/v1/sources/${encodeURIComponent(target.source.sourceId)}/${action}`, undefined);
        await refreshSourceState(context, decorations, target.client);
        vscode.window.showInformationMessage(`${action === "pause" ? "Paused" : "Resumed"} ${target.source.displayName}.`);
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
    // The installed host is shared by editor windows, browser and MCP clients.

    let sourceRefreshRunning = false;
    const sourcePoll = setInterval(() => {
        if (!sourcePollingClient || sourceRefreshRunning || !vscode.window.state.focused) return;
        sourceRefreshRunning = true;
        void sourcePollingClient.isLive()
            .then(live => live && sourcePollingClient ? refreshSourceState(context, decorations, sourcePollingClient) : undefined)
            .catch(() => { status.tooltip = "Local RAG status is stale; reconnect to review the current host state."; })
            .finally(() => { sourceRefreshRunning = false; });
    }, 5000);
    context.subscriptions.push({ dispose: () => { clearInterval(sourcePoll); sourcePollingClient = undefined; } });
    void ensureBackend(context, status)
        .then(client => { sourcePollingClient = client; return refreshSourceState(context, decorations, client); })
        .catch(error => {
            status.tooltip = error instanceof Error ? error.message : String(error);
        });
}

async function refreshSourceState(
    context: vscode.ExtensionContext,
    decorations: SourceDecorationProvider,
    client: LocalRagClient): Promise<SourceState[]> {
    sourcePollingClient = client;
    const [registered, states] = await Promise.all([client.get<SourceState[]>("/api/v1/sources"), client.get<SourceLifecycle[]>("/api/v1/lifecycle")]);
    const sources = withLifecycle(registered, states);
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
    const configUri = await chooseProjectConfiguration();
    if (!configUri) return;
    if (target.source) {
        const action = await vscode.window.showQuickPick(["Use knowledge base in this project", "Edit knowledge base"], { placeHolder: `${target.source.displayName} (${target.source.sourceId}) already indexes this root` });
        if (action === "Use knowledge base in this project") await changeProjectReference(target.client, configUri, target.source.sourceId);
        else if (action === "Edit knowledge base") await vscode.commands.executeCommand("localRag.editKnowledgeBase");
        return;
    }
    const metadata = await promptMetadata(path.basename(target.folder.fsPath), path.dirname(configUri.fsPath));
    if (!metadata) return;
    // Validate existing JSON before mutating the host. Cancellation through all prompts leaves it untouched.
    let prepared: string;
    try {
        prepared = await prepareProjectContent(target.client, configUri);
        await target.client.post("/api/v1/project-config/validate", { content: prepared });
    }
    catch (error) { vscode.window.showErrorMessage(`Configuration needs repair before indexing: ${error instanceof Error ? error.message : String(error)}`); return; }
    const result = await target.client.post<{ source: CatalogSource; alreadyRegistered: boolean }>("/api/v1/catalog/sources", { rootPath: target.folder.fsPath, ...metadata });
    await refreshSourceState(context, decorations, target.client);
    await changeProjectReference(target.client, configUri, result.source.sourceId, false, true, prepared);
}

interface CatalogSource extends SourceState { canonicalRootPath: string; description: string; scope: string; owningProjectRoot?: string; metadataRevision: number }
interface CatalogSnapshot { instanceId: string; sources: CatalogSource[] }

async function chooseProjectConfiguration(): Promise<vscode.Uri | undefined> {
    const configured = vscode.workspace.getConfiguration("localRag").get<string>("projectConfigurationPath");
    const roots = vscode.workspace.workspaceFolders ?? [];
    const selected = await selectProjectConfigurationPath(roots.map(root => root.uri.fsPath), configured, async paths => {
        if (paths.length > 1) {
            const choice = await vscode.window.showQuickPick(roots.map(root => ({ label: root.name, description: root.uri.fsPath, root: root.uri.fsPath })), { placeHolder: "Choose exactly one project root for .localrag.json" });
            return choice?.root;
        }
        return (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, openLabel: "Choose project root for .localrag.json" }))?.[0]?.fsPath;
    });
    return selected ? vscode.Uri.file(selected) : undefined;
}

async function promptMetadata(name: string, projectRoot: string, existing?: CatalogSource): Promise<{ name: string; description: string; scope: string; owningProjectRoot?: string } | undefined> {
    const enteredName = await vscode.window.showInputBox({ prompt: "Knowledge base name", value: name, validateInput: value => !value.trim() || value.trim().length > 120 ? "Enter 1–120 characters" : undefined });
    if (enteredName === undefined) return;
    const description = await vscode.window.showInputBox({ prompt: "Description (plain text)", value: existing?.description ?? "", validateInput: value => value.length > 2000 ? "Use at most 2,000 characters" : undefined });
    if (description === undefined) return;
    const scope = await vscode.window.showQuickPick([{ label: "Local", value: "local", description: "Associated with the selected project; explicit reuse is still allowed" }, { label: "Global", value: "global", description: "Reusable; each project still selects it explicitly" }], { placeHolder: "Choose intended reuse (local by default)" });
    if (!scope) return;
    return { name: enteredName.trim(), description, scope: scope.value, owningProjectRoot: scope.value === "local" ? projectRoot : undefined };
}

async function prepareProjectContent(client: LocalRagClient, uri: vscode.Uri): Promise<string> {
    const open = vscode.workspace.textDocuments.find(document => document.uri.toString() === uri.toString());
    if (open) { parseProjectConfiguration(open.getText()); return open.getText(); }
    const snapshot = await client.post<{ content: string | null; revision: string | null }>("/api/v1/project-config/read", { projectRoot: path.dirname(uri.fsPath) });
    if (snapshot.content !== null) { parseProjectConfiguration(snapshot.content); return snapshot.content; }
    const name = await vscode.window.showInputBox({ prompt: "Solution name for the new project configuration", value: path.basename(path.dirname(uri.fsPath)), validateInput: value => !value.trim() || value.trim().length > 120 ? "Enter 1–120 characters" : undefined });
    if (name === undefined) throw new Error("Cancelled; project configuration was not created.");
    const description = await vscode.window.showInputBox({ prompt: "Solution description", value: "", validateInput: value => value.length > 2000 ? "Use at most 2,000 characters" : undefined });
    if (description === undefined) throw new Error("Cancelled; project configuration was not created.");
    const catalog = await client.get<CatalogSnapshot>("/api/v1/catalog");
    return JSON.stringify({ schemaVersion: 1, solution: { name: name.trim(), description }, instanceId: catalog.instanceId, knowledgeBases: [] } satisfies ProjectConfiguration, null, 2) + "\n";
}

async function changeProjectReference(client: LocalRagClient, uri: vscode.Uri, sourceId: string, remove = false, indexed = false, prepared?: string): Promise<void> {
    try {
        const open = vscode.workspace.textDocuments.find(document => document.uri.toString() === uri.toString());
        const location = { projectRoot: path.dirname(uri.fsPath) };
        const result = await applyReferenceChange({
            read: () => client.post("/api/v1/project-config/read", location),
            validate: content => client.post("/api/v1/project-config/validate", { content }),
            save: (content, expectedRevision) => client.post("/api/v1/project-config/save", { ...location, content, expectedRevision })
        }, sourceId, remove, async () => prepared ?? await prepareProjectContent(client, uri), open ? {
            text: () => open.getText(), version: () => open.version,
            apply: async (proposed, expectedVersion) => {
                if (open.version !== expectedVersion) return false;
                const edit = new vscode.WorkspaceEdit();
                edit.replace(uri, new vscode.Range(open.positionAt(0), open.positionAt(open.getText().length)), proposed);
                return vscode.workspace.applyEdit(edit);
            }
        } : undefined, async () => {
            if (!open) return true;
            return !!await vscode.window.showInformationMessage(`${remove ? "Remove" : "Add"} source ${sourceId} in ${uri.fsPath}${open.isDirty ? " (unsaved buffer)" : ""}?`, "Apply reference edit");
        });
        if (result === "cancelled") {
            if (indexed) vscode.window.showWarningMessage(`Indexed; project configuration not saved. Retry Use Knowledge Base in This Project with source ID ${sourceId}.`);
        } else vscode.window.showInformationMessage(result === "buffer" ? "Project reference updated in the editor buffer. Save when ready." : "Project configuration saved.");
    } catch (error) {
        const action = await vscode.window.showErrorMessage(`${indexed ? "Indexed; project configuration not saved." : "Configuration unchanged."} ${error instanceof Error ? error.message : String(error)} Source ID: ${sourceId}`, "Copy source reference", "Retry reference edit");
        if (action === "Copy source reference") await vscode.env.clipboard.writeText(JSON.stringify({ sourceId }));
        if (action === "Retry reference edit") await changeProjectReference(client, uri, sourceId, remove, indexed);
    }
}

async function showSourceStatus(
    selected: SourceState,
    client: LocalRagClient,
    context: vscode.ExtensionContext,
    decorations: SourceDecorationProvider): Promise<void> {
    if ((selected.recovery || !sourceCanContribute(selected)) && (!isMissingSource(selected) || selected.indexingState === "Resuming")) {
        const presentation = recoveryPresentation(selected);
        const message = `${selected.displayName}: ${presentation.label}${presentation.detail ? ` - ${presentation.detail}` : ""}`;
        if (presentation.action) {
            const action = await vscode.window.showWarningMessage(message, presentation.action);
            if (action === "Queue recovery") {
                await client.post(`/api/v1/sources/${encodeURIComponent(selected.sourceId)}/reindex`, undefined);
                vscode.window.showInformationMessage(`Recovery queued for ${selected.displayName}.`);
            } else if (action === "Retry resume") {
                await client.post(`/api/v1/sources/${encodeURIComponent(selected.sourceId)}/resume`, undefined);
                await refreshSourceState(context, decorations, client);
                vscode.window.showInformationMessage(`Resume reconciliation retried for ${selected.displayName}.`);
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
            case 409: return "A selected source is paused, resuming, or rebuilding. Review source status and retry after reconciliation.";
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
    const recovering = sources.filter(source => ["queued", "running"].includes(source.recovery?.state.toLowerCase() ?? "") || ["Pausing", "Resuming"].includes(source.indexingState ?? "")).length;
    const paused = sources.filter(source => source.indexingState === "Paused" || source.status.toLowerCase() === "paused").length;
    if (degraded > 0) {
        sourceStatusIndicator.text = `$(warning) Local RAG (${degraded})`;
        sourceStatusIndicator.tooltip = `${degraded} source${degraded === 1 ? " needs" : "s need"} recovery attention. Select to review and retry.`;
    } else if (recovering > 0) {
        sourceStatusIndicator.text = `$(sync~spin) Local RAG (${recovering})`;
        sourceStatusIndicator.tooltip = `${recovering} source${recovering === 1 ? " is" : "s are"} recovering automatically.`;
    } else {
        sourceStatusIndicator.text = "$(database) Local RAG";
        sourceStatusIndicator.tooltip = `${sources.length} registered source${sources.length === 1 ? "" : "s"}; ${paused} paused and excluded from search.`;
    }
}

async function ensureBackend(context: vscode.ExtensionContext, status: vscode.StatusBarItem): Promise<LocalRagClient> {
    const settings = vscode.workspace.getConfiguration("localRag");
    const endpoint = settings.get<string>("backendEndpoint", "http://127.0.0.1:5198").replace(/\/$/, "");
    const address = new URL(endpoint);
    if (!["http:", "https:"].includes(address.protocol) || !["localhost", "127.0.0.1", "[::1]"].includes(address.hostname) || address.username || address.password || address.pathname !== "/" || address.search || address.hash)
        throw new Error("Local RAG backendEndpoint must be a loopback HTTP(S) origin.");
    const token = await getOrCreateToken(context);
    const client = new LocalRagClient(endpoint, token);
    if (!await client.isLive()) {
        const installed = await resolveInstallerConfiguration(settings, process.env.LOCALAPPDATA);
        await launchBackend(context, installed, endpoint, token);
        await waitForLive(client);
    }
    status.text = "$(database) Local RAG";
    status.tooltip = `Connected to ${endpoint}`;
    return client;
}

async function launchBackend(context: vscode.ExtensionContext, installed: ResolvedInstallerConfiguration, endpoint: string, token: string): Promise<void> {
    const launcher = path.join(path.dirname(installed.hostExecutable), "Start-LocalRag.ps1");
    try { await fs.access(launcher); } catch { throw new Error(`Local RAG launcher is missing at ${launcher}. Repair the installation.`); }
    await new Promise<void>((resolve, reject) => {
        const launch = spawn("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", launcher, "-NoBrowser", "-Endpoint", endpoint], {
            windowsHide: true,
            env: { ...process.env, LOCALRAG_MCP_TOKEN: token, ...providerEnvironment(installed) },
            stdio: ["ignore", "ignore", "ignore"]
        });
        launch.once("error", reject);
        launch.once("exit", code => code === 0 ? resolve() : reject(new Error(`Local RAG launcher failed (${code}). Open Local RAG independently to inspect startup diagnostics.`)));
    });
}

async function getOrCreateToken(context: vscode.ExtensionContext): Promise<string> {
    const sharedMcpToken = environmentMcpToken(process.env.LOCALRAG_MCP_TOKEN);
    if (sharedMcpToken) return sharedMcpToken;

    const key = "localRag.token";
    const existing = await context.secrets.get(key);
    const dataDirectory = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "LocalRag") : context.globalStorageUri.fsPath;
    const token = await sharedHostToken(dataDirectory, existing);
    await context.secrets.store(key, token);
    return token;
}

async function waitForLive(client: LocalRagClient): Promise<void> {
    for (let attempt = 0; attempt < 30; attempt++) {
        if (await client.isLive()) return;
        await new Promise(resolve => setTimeout(resolve, 500));
    }
    throw new Error("The Local RAG backend did not become ready. Check the selected vector-store provider diagnostics, verify the installed native assets or external Weaviate endpoint as applicable, and confirm the verified ONNX model assets are installed.");
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
    return (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, openLabel: "Choose folder to index" }))?.[0];
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
