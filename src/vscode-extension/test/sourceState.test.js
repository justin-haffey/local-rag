const assert = require("node:assert/strict");
const path = require("node:path");
const test = require("node:test");

const { environmentMcpToken, isMissingSource, missingRootMessage, recoveryPresentation, rootPathHash } = require("../out/sourceState.js");

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

test("shared MCP tokens are trimmed and blank values fall back to Secret Storage", () => {
    assert.equal(environmentMcpToken("  shared-token  "), "shared-token");
    assert.equal(environmentMcpToken("   "), undefined);
    assert.equal(environmentMcpToken(undefined), undefined);
});

test("running recovery is rendered separately from compatibility indexing status", () => {
    const source = {
        sourceId: "source",
        rootPathHash: "hash",
        displayName: "source",
        status: "Indexing",
        recovery: {
            state: "Running",
            desiredGeneration: 4,
            completedGeneration: 3,
            activeGeneration: 4,
            causes: ["WatcherOverflow"],
            changedFiles: 0,
            deletedFiles: 0,
            unchangedFiles: 0
        }
    };

    assert.deepEqual(recoveryPresentation(source), {
        label: "Recovering",
        detail: "Automatic index recovery is running (generation 3 of 4)."
    });
});

test("degraded recovery provides a bounded actionable retry presentation", () => {
    const source = {
        sourceId: "source",
        rootPathHash: "hash",
        displayName: "source",
        status: "Degraded",
        recovery: {
            state: "Degraded",
            desiredGeneration: 2,
            completedGeneration: 1,
            causes: ["Retry"],
            lastErrorCode: "DependencyUnavailable",
            lastErrorSummary: "A required local dependency is unavailable.",
            changedFiles: 0,
            deletedFiles: 0,
            unchangedFiles: 0
        }
    };

    const presentation = recoveryPresentation(source);
    assert.equal(presentation.label, "Recovery degraded");
    assert.equal(presentation.action, "Queue recovery");
    assert.match(presentation.detail, /Restore local dependencies/);
    assert.doesNotMatch(presentation.detail, /C:\\\\/);
});
