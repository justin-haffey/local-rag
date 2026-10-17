const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const {
    packagedWeaviateEndpoint,
    providerEnvironment,
    readInstallationManifest,
    readInstallerSettings,
    resolveInstallerConfiguration
} = require("../out/installerSettings.js");

function configuration(explicitValue) {
    return {
        inspect: () => ({
            defaultValue: packagedWeaviateEndpoint,
            globalValue: explicitValue,
            workspaceValue: undefined,
            workspaceFolderValue: undefined
        }),
        get: () => explicitValue ?? packagedWeaviateEndpoint
    };
}

async function withSettings(settings, callback) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), "local-rag-settings-"));
    try {
        const settingsDirectory = path.join(root, "LocalRag");
        await fs.mkdir(settingsDirectory, { recursive: true });
        await fs.writeFile(path.join(settingsDirectory, "install-settings.json"), JSON.stringify(settings));
        const hostExecutable = path.join(root, "Programs", "LocalRag", "Host", "0.1.0", "LocalRag.Host.exe");
        await fs.mkdir(path.dirname(hostExecutable), { recursive: true });
        await fs.writeFile(hostExecutable, "test host");
        await fs.writeFile(path.join(settingsDirectory, "installation.json"), JSON.stringify({
            schemaVersion: 1,
            version: "0.1.0",
            hostExecutable
        }));
        await callback(root);
    } finally {
        await fs.rm(root, { recursive: true, force: true });
    }
}

test("installer endpoint and installed host override packaged defaults", async () => {
    await withSettings({
        schemaVersion: 1,
        weaviateEndpoint: "http://localhost:8081/"
    }, async root => {
        const result = await resolveInstallerConfiguration(configuration(undefined), root);
        assert.equal(result.provider, "Weaviate");
        assert.equal(result.weaviateEndpoint, "http://localhost:8081");
        assert.match(result.hostExecutable, /Programs[\\/]LocalRag[\\/]Host[\\/]0\.1\.0[\\/]LocalRag\.Host\.exe$/);
    });
});

test("schema v2 Weaviate settings are discriminated and use only Weaviate launch keys", async () => {
    await withSettings({
        schemaVersion: 2,
        provider: "Weaviate",
        weaviate: { endpoint: "http://127.0.0.1:8090/" }
    }, async root => {
        const result = await resolveInstallerConfiguration(configuration(undefined), root);
        assert.deepEqual(providerEnvironment(result), {
            LocalRag__VectorStore__Provider: "Weaviate",
            LocalRag__VectorStore__Weaviate__Endpoint: "http://127.0.0.1:8090"
        });
        assert.equal("lanceDbPath" in result, false);
    });
});

test("schema v2 LanceDb settings ignore the Weaviate workspace override and use only LanceDb launch keys", async () => {
    const lanceDbPath = path.resolve("test-lancedb");
    await withSettings({
        schemaVersion: 2,
        provider: "LanceDb",
        lanceDb: { path: lanceDbPath }
    }, async root => {
        const result = await resolveInstallerConfiguration(configuration("https://ignored.example.test:8443"), root);
        assert.equal(result.provider, "LanceDb");
        assert.deepEqual(providerEnvironment(result), {
            LocalRag__VectorStore__Provider: "LanceDb",
            LocalRag__VectorStore__LanceDb__Path: path.normalize(lanceDbPath)
        });
        assert.equal("weaviateEndpoint" in result, false);
    });
});

test("schema v2 rejects mixed, missing, unknown, and newer provider settings", async () => {
    const settingsPath = path.join(await fs.mkdtemp(path.join(os.tmpdir(), "local-rag-invalid-settings-")), "settings.json");
    try {
        for (const candidate of [
            { schemaVersion: 2, provider: "Weaviate", weaviate: { endpoint: "http://localhost:8080" }, lanceDb: { path: path.resolve("db") } },
            { schemaVersion: 2, provider: "LanceDb", lanceDb: { path: "relative" } },
            { schemaVersion: 2, provider: "Other" },
            { schemaVersion: 3, provider: "Weaviate", weaviate: { endpoint: "http://localhost:8080" } }
        ]) {
            await fs.writeFile(settingsPath, JSON.stringify(candidate));
            assert.equal(await readInstallerSettings(settingsPath), undefined);
        }
    } finally {
        await fs.rm(path.dirname(settingsPath), { recursive: true, force: true });
    }
});

test("an explicit VS Code endpoint overrides installer settings", async () => {
    await withSettings({ schemaVersion: 1, weaviateEndpoint: "http://localhost:8081" }, async root => {
        const result = await resolveInstallerConfiguration(configuration("https://weaviate.example.test:8443"), root);
        assert.equal(result.weaviateEndpoint, "https://weaviate.example.test:8443");
    });
});

test("invalid settings are rejected and missing installation discovery is actionable", async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), "local-rag-settings-"));
    try {
        const settingsPath = path.join(root, "settings.json");
        await fs.writeFile(settingsPath, "{not-json");
        assert.equal(await readInstallerSettings(settingsPath), undefined);
        assert.equal(await readInstallationManifest(settingsPath), undefined);
        await assert.rejects(
            resolveInstallerConfiguration(configuration(undefined), root),
            /Repair or reinstall Local RAG/
        );
    } finally {
        await fs.rm(root, { recursive: true, force: true });
    }
});
