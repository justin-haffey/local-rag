const assert = require("node:assert/strict");
const path = require("node:path");
const test = require("node:test");

const { isMissingSource, missingRootMessage, rootPathHash } = require("../out/sourceState.js");

test("root path identity is case-insensitive and ignores a trailing separator", () => {
    const root = path.resolve("C:\\LocalRagFixture");
    assert.equal(rootPathHash(root), rootPathHash(root.toLowerCase() + path.sep));
});

test("different roots produce different opaque identities", () => {
    assert.notEqual(rootPathHash("C:\\first"), rootPathHash("C:\\second"));
    assert.match(rootPathHash("C:\\first"), /^[a-f0-9]{64}$/);
});

test("only the explicit missing-root degraded state is considered stale", () => {
    const base = { sourceId: "source", rootPathHash: "hash", displayName: "source" };
    assert.equal(isMissingSource({ ...base, status: "Degraded", lastError: missingRootMessage }), true);
    assert.equal(isMissingSource({ ...base, status: "Degraded", lastError: "Weaviate is unavailable." }), false);
    assert.equal(isMissingSource({ ...base, status: "Ready", lastError: missingRootMessage }), false);
});
