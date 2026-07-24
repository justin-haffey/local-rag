const assert = require("node:assert/strict");
const path = require("node:path");
const test = require("node:test");

const { rootPathHash } = require("../out/sourceState.js");
const {
    indexSelectedFolder,
    isDirectory,
    LatestRequest,
    boundedContextText,
    realWorkspaceFilePath,
    refreshSelectedFolder,
    sourceForFolder,
    toggleSelectedFolder,
    workspaceFilePath
} = require("../out/sourceControls.js");

function source(sourceId = "selected") {
    return { sourceId, rootPathHash: rootPathHash(`C:\\LocalRagFixture\\${sourceId}`), displayName: sourceId, status: "Ready" };
}

function recordingClient() {
    const calls = [];
    return {
        calls,
        post: async (pathName, body) => {
            calls.push({ method: "POST", pathName, body });
            return source();
        },
        delete: async pathName => { calls.push({ method: "DELETE", pathName }); }
    };
}

test("selected-folder matching uses the opaque current root identity", () => {
    const selectedPath = path.resolve("C:\\LocalRagFixture\\selected");
    const sources = [
        { sourceId: "other", rootPathHash: rootPathHash("C:\\LocalRagFixture\\other"), displayName: "other", status: "Ready" },
        { sourceId: "selected", rootPathHash: rootPathHash(selectedPath), displayName: "selected", status: "Ready" }
    ];

    assert.equal(sourceForFolder(sources, selectedPath.toLowerCase() + path.sep)?.sourceId, "selected");
});

test("an unmatched folder never falls back to an arbitrary source", () => {
    const sources = [
        { sourceId: "other", rootPathHash: rootPathHash("C:\\LocalRagFixture\\other"), displayName: "other", status: "Ready" }
    ];

    assert.equal(sourceForFolder(sources, "C:\\LocalRagFixture\\unregistered"), undefined);
});

test("directory guard accepts directories and rejects file selections", () => {
    const file = 1;
    const directory = 2;
    const symbolicDirectory = directory | 64;

    assert.equal(isDirectory(directory, directory), true);
    assert.equal(isDirectory(symbolicDirectory, directory), true);
    assert.equal(isDirectory(file, directory), false);
});

test("indexing posts only the selected folder root", async () => {
    const client = recordingClient();

    const result = await indexSelectedFolder(client, undefined, "C:\\LocalRagFixture\\selected");

    assert.equal(result.kind, "indexed");
    assert.deepEqual(client.calls, [{
        method: "POST",
        pathName: "/api/v1/sources",
        body: { rootPath: "C:\\LocalRagFixture\\selected" }
    }]);
});

test("toggle cancellation prevents deletion and confirmed toggle deletes only the matched source", async () => {
    const selected = source("selected");
    const cancelledClient = recordingClient();
    assert.equal(await toggleSelectedFolder(cancelledClient, selected, "C:\\LocalRagFixture\\selected", async () => false), "cancelled");
    assert.deepEqual(cancelledClient.calls, []);

    const confirmedClient = recordingClient();
    assert.equal(await toggleSelectedFolder(confirmedClient, selected, "C:\\LocalRagFixture\\selected", async () => true), "removed");
    assert.deepEqual(confirmedClient.calls, [{ method: "DELETE", pathName: "/api/v1/sources/selected" }]);
});

test("refresh uses only the matched source ID and refuses an unmatched folder", async () => {
    const client = recordingClient();
    assert.equal(await refreshSelectedFolder(client, undefined), false);
    assert.deepEqual(client.calls, []);

    assert.equal(await refreshSelectedFolder(client, source("selected")), true);
    assert.deepEqual(client.calls, [{ method: "POST", pathName: "/api/v1/sources/selected/reindex", body: undefined }]);
});

test("workspace navigation rejects absolute and parent-traversal result paths", () => {
    const root = path.resolve("C:\\LocalRagFixture\\selected");
    assert.equal(workspaceFilePath(root, "src\\feature.ts"), path.resolve(root, "src\\feature.ts"));
    assert.equal(workspaceFilePath(root, "..\\outside.ts"), undefined);
    assert.equal(workspaceFilePath(root, path.resolve("C:\\outside.ts")), undefined);
});

test("workspace navigation rejects a symlink or junction that resolves outside the root", async () => {
    const root = path.resolve("C:\\LocalRagFixture\\selected");
    const candidate = path.resolve(root, "linked-outside\\secret.ts");
    const outside = path.resolve("C:\\outside\\secret.ts");
    const realpath = async value => value === root ? root : value === candidate ? outside : value;

    assert.equal(await realWorkspaceFilePath(root, "linked-outside\\secret.ts", realpath), undefined);
});

test("workspace navigation returns a real file inside the root", async () => {
    const root = path.resolve("C:\\LocalRagFixture\\selected");
    const candidate = path.resolve(root, "src\\feature.ts");
    const realpath = async value => value;

    assert.equal(await realWorkspaceFilePath(root, "src\\feature.ts", realpath), candidate);
});

test("latest-request guard prevents a superseded search from overwriting newer results", () => {
    const requests = new LatestRequest();
    const first = requests.begin();
    const second = requests.begin();

    assert.equal(requests.isCurrent(first), false);
    assert.equal(requests.isCurrent(second), true);
});

test("context copy validates provenance and applies its character bound", () => {
    const selected = { chunkId: "chunk", sourceId: "source", relativePath: "src/feature.ts" };
    const context = { ...selected, startLine: 4, endLine: 8, content: "abcdefghijklmnopqrstuvwxyz" };

    assert.equal(boundedContextText(context, { ...selected, sourceId: "other" }, 12), undefined);
    assert.equal(
        boundedContextText(context, selected, 12),
        "src/feature.ts:4-8\n\nabcdefghijkl\n\n[Context truncated by Local RAG]");
});
