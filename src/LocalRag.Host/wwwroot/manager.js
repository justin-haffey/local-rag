"use strict";
const $ = id => document.getElementById(id);
let catalog = [], lifecycle = [], statuses = [], instanceId = "", revision = null, readTarget = null, pickerCurrent = null;
const chosen = new Set();
const csrf = document.querySelector('meta[name="csrf-token"]').content;
const statusName = value => typeof value === "number" ? ["Pending", "Indexing", "Ready", "Degraded", "Paused", "Failed"][value] : value;
function notice(text) { $("notice").textContent = text; }
async function api(path, body, method = body === undefined ? "GET" : "POST") {
    const response = await fetch(path, { method, credentials: "same-origin", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf }, body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) {
        if (response.status === 401) throw Error("Session expired or not signed in. Use the launcher or sign in below; the current form is preserved.");
        if (response.status === 403) throw Error("Request denied. Refresh the page and sign in with the management token for destructive actions.");
        const text = await response.text();
        let detail = ""; try { detail = JSON.parse(text).error || ""; } catch { /* Never render raw server HTML. */ }
        throw Error(detail || `Host request failed (${response.status}). Check System and retry.`);
    }
    return response.status === 204 ? null : response.json();
}
function button(text, fn) { const element = document.createElement("button"); element.type = "button"; element.textContent = text; element.addEventListener("click", () => run(fn, element)); return element; }
async function run(fn, element) { if (element) element.disabled = true; try { await fn(); } catch (error) { notice(error.message || "Connection lost. Your inputs are preserved; retry when the host is available."); } finally { if (element) element.disabled = false; } }
async function copyText(text, label) {
    try {
        await navigator.clipboard.writeText(text);
        notice(label + " copied to the clipboard.");
    } catch {
        $("copyFallbackText").value = text;
        $("copyFallback").showModal();
        $("copyFallbackText").focus(); $("copyFallbackText").select();
        notice("Clipboard permission is unavailable. The copy dialog contains the exact text; use Ctrl+C or your browser's Copy action.");
    }
}
function selectedIds() { return catalog.filter(s => chosen.has(s.sourceId)).map(s => s.sourceId); }
function drawSources() {
    const filter = $("filter").value.toLocaleLowerCase(); $("sources").replaceChildren();
    for (const source of catalog.filter(s => `${s.displayName} ${s.description} ${s.scope}`.toLocaleLowerCase().includes(filter))) {
        const state = lifecycle.find(x => x.sourceId === source.sourceId); const row = document.createElement("tr");
        const select = document.createElement("input"); select.type = "checkbox"; select.checked = chosen.has(source.sourceId); select.setAttribute("aria-label", `Select ${source.displayName}`);
        select.addEventListener("change", () => select.checked ? chosen.add(source.sourceId) : chosen.delete(source.sourceId));
        const cell = document.createElement("td"); cell.append(select); row.append(cell);
        const name = document.createElement("td"); name.textContent = source.displayName; const description = document.createElement("small"); description.textContent = source.description; name.append(description); row.append(name);
        for (const text of [source.scope, state?.state === "Removing" ? "Removing; source files preserved" : state?.state === "Paused" ? "Paused; not searched" : state?.state === "Pausing" ? "Pausing; draining work" : state?.state === "Resuming" ? (["Failed", "Degraded"].includes(statusName(source.status)) ? "Resume failed; not searched. Restore dependencies and retry Resume." : `Resuming; not searched (${statusName(source.status)})`) : statusName(source.status), source.lastSuccessfulIndexUtc ? new Date(source.lastSuccessfulIndexUtc).toLocaleString() : "No successful run"]) { const td = document.createElement("td"); td.textContent = text; row.append(td); }
        const actions = document.createElement("td");
        actions.append(button("Details / edit", () => details(source)), button(state?.desiredState === "Paused" ? "Resume" : "Pause", async () => { notice("Applying indexing transition…"); await api(`/api/v1/sources/${source.sourceId}/${state?.desiredState === "Paused" ? "resume" : "pause"}`, {}); await refresh(); notice(state?.desiredState === "Paused" ? "Resume queued; source is not searched until reconciliation completes." : "Paused; not searched. Existing indexed content and metadata are retained."); }));
        if (state?.state === "Resuming" && ["Failed", "Degraded"].includes(statusName(source.status))) actions.append(button("Retry Resume", async () => { await api(`/api/v1/sources/${source.sourceId}/resume`, {}); await refresh(); notice("Resume retry queued; source remains hidden until reconciliation completes."); }));
        const reindex = button("Reindex", async () => { await api(`/api/v1/sources/${source.sourceId}/reindex`, {}); notice("Reindex accepted; status will refresh."); await refresh(); }); reindex.disabled = state?.state !== "Running"; actions.append(reindex, button("Remove…", () => remove(source))); row.append(actions); $("sources").append(row);
    }
    if (!catalog.length) { const row = document.createElement("tr"), cell = document.createElement("td"); cell.colSpan = 6; cell.textContent = "Catalog is empty. Add a folder to begin."; row.append(cell); $("sources").append(row); }
}
function details(source) {
    const state = lifecycle.find(x => x.sourceId === source.sourceId), recovery = statuses.find(x => x.sourceId === source.sourceId);
    $("detailState").textContent = JSON.stringify({ sourceId: source.sourceId, root: source.canonicalRootPath, status: statusName(source.status), indexing: state, recovery, lastError: source.lastError?.slice(0, 2000), updatedUtc: source.updatedUtc }, null, 2);
    const form = $("metadata"); for (const [name, value] of Object.entries({ sourceId: source.sourceId, expectedRevision: source.metadataRevision, name: source.displayName, description: source.description, scope: ["local", "global"].includes(source.scope) ? source.scope : "", owningProjectRoot: source.owningProjectRoot || "" })) form.elements.namedItem(name).value = value;
    $("details").showModal();
}
async function remove(source) {
    const path = `/api/v1/management/sources/${source.sourceId}/remove`;
    const prepared = await api(path, {});
    if (!prepared.confirmationToken) throw Error(prepared.errorCode || "Removal could not be prepared.");
    if (!confirm(`${prepared.warning}\n\n${source.displayName}\n${source.canonicalRootPath}\nID: ${source.sourceId}\n\nRemove this exact index?`)) return;
    notice("Removing index; waiting for active work to drain…");
    const result = await api(path, { confirmationToken: prepared.confirmationToken });
    if (result.errorCode) throw Error(result.errorCode);
    chosen.delete(source.sourceId); await refresh(); notice("Index removed. Original source files remain intact. Project references may now be stale.");
}
async function refresh() {
    const [c, l, s, system] = await Promise.all([api("/api/v1/catalog"), api("/api/v1/lifecycle"), api("/api/v1/sources"), api("/api/v1/system")]);
    catalog = c.sources; instanceId = c.instanceId; lifecycle = l; statuses = s; drawSources(); $("systemState").textContent = JSON.stringify(system, null, 2);
    $("freshness").textContent = `Connected. Last successful refresh: ${new Date().toLocaleString()}`; if ($("notice").textContent === "Connecting…" || $("notice").textContent.startsWith("Disconnected or stale.")) notice("Connected to the local host.");
}
$("login").addEventListener("submit", event => { event.preventDefault(); run(async () => { const data = Object.fromEntries(new FormData(event.target)); await api("/manage/session", data); event.target.reset(); location.reload(); }); });
$("logout").addEventListener("click", () => run(async () => { await api("/manage/logout", {}); notice("Signed out. Inputs remain available; sign in to continue."); }));
$("filter").addEventListener("input", drawSources); $("refresh").addEventListener("click", () => run(async () => { await refresh(); notice("Catalog refreshed."); }));
$("add").addEventListener("submit", event => { event.preventDefault(); run(async () => { const data = Object.fromEntries(new FormData(event.target)); const registered = await api("/api/v1/catalog/sources", data); event.target.reset(); await refresh(); if (registered.alreadyRegistered) { chosen.add(registered.source.sourceId); drawSources(); details(registered.source); notice("This folder is already registered. Existing details are open; use the selected knowledge base in the project file."); return; } notice("Folder registered; reconciliation was queued. Ready appears only after completion."); }); });
$("metadata").addEventListener("submit", event => { event.preventDefault(); run(async () => { const data = Object.fromEntries(new FormData(event.target)); data.expectedRevision = Number(data.expectedRevision); await api(`/api/v1/catalog/sources/${data.sourceId}/metadata`, data); $("details").close(); await refresh(); notice("Knowledge base metadata saved."); }); });
$("detailsClose").onclick = () => $("details").close();
$("copyFallbackClose").onclick = () => $("copyFallback").close();
async function listFolders(path) {
    try { const listing = await api(`/api/v1/folders${path ? `?path=${encodeURIComponent(path)}` : ""}`); pickerCurrent = listing.current; $("pickerPath").value = listing.current || ""; $("directories").replaceChildren(); $("drives").replaceChildren();
        for (const drive of listing.drives) $("drives").append(button(drive, () => listFolders(drive)));
        for (const child of listing.directories) { const li = document.createElement("li"); li.append(button(child, () => listFolders(child))); $("directories").append(li); }
        $("pickerParent").disabled = !listing.parent; $("pickerParent").onclick = () => run(() => listFolders(listing.parent)); $("pickerSelect").disabled = !pickerCurrent; $("pickerError").textContent = listing.directories.length === 500 ? "First 500 folders shown. Paste a path to navigate directly." : "";
    } catch (error) { pickerCurrent = null; $("pickerSelect").disabled = true; $("pickerError").textContent = error.message; }
}
$("browse").onclick = () => { $("picker").showModal(); run(() => listFolders($("sourceRoot").value)); };
$("pickerGo").onclick = () => run(() => listFolders($("pickerPath").value)); $("pickerSelect").onclick = () => { if (pickerCurrent) $("sourceRoot").value = pickerCurrent; $("picker").close(); }; $("pickerClose").onclick = () => $("picker").close();
$("searchForm").addEventListener("submit", event => { event.preventDefault(); run(async () => {
    $("results").replaceChildren(); notice("Searching…");
    const form = Object.fromEntries(new FormData(event.target)); const request = { query: form.query, mode: ["Hybrid", "Lexical", "Vector"].indexOf(form.mode), language: form.language || null, pathPrefix: form.pathPrefix || null, limit: Number(form.limit), alpha: Number(form.alpha) };
    let response;
    if (form.scope === "project") response = await api("/api/v1/project-config/search", { content: $("config").value, search: request });
    else { request.sourceIds = form.scope === "all" ? null : selectedIds(); if (form.scope === "selected" && !request.sourceIds.length) throw Error("Select knowledge bases explicitly, or choose All catalog."); response = await api("/api/v1/search", request); }
    $("results").replaceChildren();
    for (const result of response.results) {
        const article = document.createElement("article"), title = document.createElement("h3"), preview = document.createElement("pre"); const source = catalog.find(s => s.sourceId === result.sourceId);
        const location = `${source?.canonicalRootPath || result.sourceId}\\${result.relativePath}:${result.startLine}`;
        title.textContent = `${source?.displayName || result.sourceId} / ${result.relativePath}:${result.startLine}–${result.endLine}`; preview.textContent = result.content.slice(0, 16000); article.append(title, preview);
        article.append(button("Copy location", () => copyText(location, "File location")), button("Copy context", () => copyText(`Source: ${result.sourceId}\n${result.relativePath}:${result.startLine}-${result.endLine}\n\n${result.content.slice(0, 16000)}${result.content.length > 16000 ? "\n[Truncated]" : ""}`, "Bounded context")));
        if (source) { const link = document.createElement("a"); link.textContent = "Open in VS Code"; link.href = LocalRagManagerLinks.editorFileUri(source.canonicalRootPath, result.relativePath, result.startLine); link.title = "Requires VS Code's registered URI handler. Copy location if the handoff is unavailable."; article.append(link); }
        $("results").append(article);
    }
    notice(`${response.results.length} results. Editor handoff requires VS Code; Copy location is always available.`);
}); });
$("readConfig").onclick = () => run(async () => { const root = $("projectRoot").value; const result = await api("/api/v1/project-config/read", { projectRoot: root }); $("config").value = result.content || ""; revision = result.revision; readTarget = root; notice(result.content ? "Loaded chosen project; edits are protected by its revision." : "No project file exists at this target."); });
$("newConfig").onclick = () => run(async () => { const ids = selectedIds(); if (!ids.length) throw Error("Select knowledge bases first."); if ($("config").value.trim()) await api("/api/v1/project-config/validate", { content: $("config").value }); const value = $("config").value.trim() ? JSON.parse($("config").value) : { schemaVersion: 1, solution: { name: "My solution", description: "" }, instanceId }; value.knowledgeBases = ids.map(sourceId => ({ sourceId })); $("config").value = JSON.stringify(value, null, 2) + "\n"; });
$("validateConfig").onclick = () => run(async () => { await api("/api/v1/project-config/validate", { content: $("config").value }); notice("Project file is valid for this installation."); });
$("saveConfig").onclick = () => run(async () => { if (readTarget !== $("projectRoot").value) throw Error("Read the chosen project folder before saving, so existing edits are preserved."); const result = await api("/api/v1/project-config/save", { projectRoot: $("projectRoot").value, content: $("config").value, expectedRevision: revision }); revision = result.revision; notice("Saved the chosen project's .localrag.json."); });
$("copyConfig").onclick = () => run(() => copyText($("config").value, "Project configuration"));
$("downloadConfig").onclick = () => { const url = URL.createObjectURL(new Blob([$("config").value], { type: "application/json" })); const link = document.createElement("a"); link.href = url; link.download = ".localrag.json"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); };
$("reset").onclick = () => run(async () => { const prepared = await api("/api/v1/management/reset", {}); if (!prepared.confirmationToken) throw Error(prepared.errorCode || "Reset unavailable."); if (!confirm(prepared.warning) || prompt("Type RESET ALL LOCAL RAG to confirm this installation's reset") !== "RESET ALL LOCAL RAG") return; notice("Reset running; indexing is fenced…"); const result = await api("/api/v1/management/reset", { confirmationToken: prepared.confirmationToken }); if (result.errorCode) throw Error(result.errorCode); await refresh(); notice("Confirmed reset completed. Original source files remain intact."); });
let failures = 0;
async function poll() {
    if (!document.hidden && !$("details").open && !$("picker").open) {
        try { await refresh(); failures = 0; } catch (error) { failures++; $("freshness").textContent = $("freshness").textContent.replace(/^Connected\./, "Stale; disconnected.") || "No successful refresh yet."; notice(`Disconnected or stale. ${error.message}`); }
    }
    const active = lifecycle.some(x => x.state === "Resuming" || x.state === "Pausing" || x.state === "Removing") || catalog.some(x => ["Pending", "Indexing"].includes(statusName(x.status)));
    setTimeout(poll, failures ? Math.min(60000, 5000 * 2 ** Math.min(failures, 4)) : active ? 3000 : 10000);
}
async function start() { const ticket = location.hash.startsWith("#ticket=") ? location.hash.slice(8) : null; if (ticket) { history.replaceState(null, "", "/manage"); try { await api("/manage/session-ticket", { ticket }); location.reload(); return; } catch (error) { notice(error.message); } } await poll(); }
start();
