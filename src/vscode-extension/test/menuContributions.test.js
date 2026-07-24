const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8"));

test("Explorer exposes the Local RAG submenu only for local Explorer folders", () => {
    const explorerItem = manifest.contributes.menus["explorer/context"].find(item => item.submenu === "localRag.explorer");
    assert.equal(explorerItem.when, "resourceScheme == file && explorerResourceIsFolder");
    assert.equal(explorerItem.group, "navigation@10");
    assert.equal(manifest.contributes.submenus.find(item => item.id === "localRag.explorer").label, "Local RAG");
});

test("Local RAG submenu contains the selected-folder commands", () => {
    const commands = manifest.contributes.menus["localRag.explorer"].map(item => item.command);
    assert.deepEqual(commands, [
        "localRag.indexFolder",
        "localRag.refreshFolderIndex",
        "localRag.showFolderStatus",
        "localRag.toggleIndexing"
    ]);
    const declared = new Set(manifest.contributes.commands.map(command => command.command));
    for (const command of commands) assert.ok(declared.has(command));
});

test("folder commands use plain language, icons, and a Local RAG Command Palette category", () => {
    const commands = new Map(manifest.contributes.commands.map(command => [command.command, command]));
    assert.equal(commands.get("localRag.indexFolder").title, "Index Folder");
    assert.equal(commands.get("localRag.refreshFolderIndex").title, "Refresh Index");
    assert.equal(commands.get("localRag.showFolderStatus").title, "View Status");
    assert.equal(commands.get("localRag.toggleIndexing").title, "Turn Indexing On/Off");
    for (const commandId of ["localRag.indexFolder", "localRag.refreshFolderIndex", "localRag.showFolderStatus", "localRag.toggleIndexing"]) {
        const command = commands.get(commandId);
        assert.equal(command.category, "Local RAG");
        assert.match(command.icon, /^\$\([a-z-]+\)$/);
    }
});

test("a single LOCAL RAG Explorer tab combines indexed folders and search results", () => {
    assert.deepEqual(manifest.contributes.views.explorer, [{ id: "localRag.main", name: "LOCAL RAG" }]);
    assert.equal(manifest.contributes.menus["view/title"][0].when, "view == localRag.main");
    for (const item of manifest.contributes.menus["view/item/context"]) {
        assert.match(item.when, /^view == localRag\.main/);
    }
});
