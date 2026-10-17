import * as fs from "node:fs/promises";
import * as path from "node:path";
import type { WorkspaceConfiguration } from "vscode";

export const packagedWeaviateEndpoint = "http://127.0.0.1:8080";

export type VectorStoreProvider = "Weaviate" | "LanceDb";

export interface WeaviateInstallerSettings {
    schemaVersion: 1 | 2;
    provider: "Weaviate";
    weaviate: {
        endpoint: string;
    };
}

export interface LanceDbInstallerSettings {
    schemaVersion: 2;
    provider: "LanceDb";
    lanceDb: {
        path: string;
    };
}

export type InstallerSettings = WeaviateInstallerSettings | LanceDbInstallerSettings;

export interface InstallationManifest {
    schemaVersion: number;
    version: string;
    hostExecutable: string;
}

export type ResolvedInstallerConfiguration =
    | {
        provider: "Weaviate";
        weaviateEndpoint: string;
        hostExecutable: string;
    }
    | {
        provider: "LanceDb";
        lanceDbPath: string;
        hostExecutable: string;
    };

export async function resolveInstallerConfiguration(
    configuration: WorkspaceConfiguration,
    localAppData: string | undefined
): Promise<ResolvedInstallerConfiguration> {
    if (!localAppData) {
        throw new Error("Local RAG installation discovery is unavailable because LOCALAPPDATA is not set. Repair or reinstall Local RAG.");
    }

    const installerSettings = await readInstallerSettings(path.join(localAppData, "LocalRag", "install-settings.json"));
    if (!installerSettings) {
        throw new Error("Local RAG provider settings are missing, corrupt, or use an unsupported schema. Repair or reinstall Local RAG; the provider will not be changed automatically.");
    }

    // Host binaries live outside the VSIX so extension updates cannot replace the runtime.
    const installation = await readInstallationManifest(path.join(localAppData, "LocalRag", "installation.json"));
    if (!installation) {
        throw new Error("Local RAG host installation is missing or invalid. Repair or reinstall Local RAG to restore installation.json and the standalone host.");
    }

    if (installerSettings.provider === "LanceDb") {
        return {
            provider: "LanceDb",
            lanceDbPath: installerSettings.lanceDb.path,
            hostExecutable: installation.hostExecutable
        };
    }

    // Workspace/user endpoint settings are a compatibility override for Weaviate only.
    const inspectedEndpoint = configuration.inspect<string>("weaviateEndpoint");
    const hasExplicitEndpoint = inspectedEndpoint !== undefined && [
        inspectedEndpoint.globalValue,
        inspectedEndpoint.workspaceValue,
        inspectedEndpoint.workspaceFolderValue
    ].some(value => value !== undefined);

    const configuredEndpoint = hasExplicitEndpoint
        ? configuration.get<string>("weaviateEndpoint")
        : installerSettings.weaviate.endpoint;

    return {
        provider: "Weaviate",
        weaviateEndpoint: normalizeHttpEndpoint(configuredEndpoint) ?? packagedWeaviateEndpoint,
        hostExecutable: installation.hostExecutable
    };
}

export async function readInstallerSettings(settingsPath: string): Promise<InstallerSettings | undefined> {
    try {
        const candidate = JSON.parse(await fs.readFile(settingsPath, "utf8")) as Record<string, unknown>;
        if (candidate.schemaVersion === 1) {
            const weaviateEndpoint = normalizeHttpEndpoint(candidate.weaviateEndpoint);
            return weaviateEndpoint
                ? { schemaVersion: 1, provider: "Weaviate", weaviate: { endpoint: weaviateEndpoint } }
                : undefined;
        }

        if (candidate.schemaVersion !== 2) return undefined;
        if (candidate.provider === "Weaviate") {
            if (candidate.lanceDb !== undefined) return undefined;
            const weaviate = asRecord(candidate.weaviate);
            const endpoint = normalizeHttpEndpoint(weaviate?.endpoint);
            return endpoint
                ? { schemaVersion: 2, provider: "Weaviate", weaviate: { endpoint } }
                : undefined;
        }
        if (candidate.provider === "LanceDb") {
            if (candidate.weaviate !== undefined) return undefined;
            const lanceDb = asRecord(candidate.lanceDb);
            const databasePath = normalizeAbsolutePath(lanceDb?.path);
            return databasePath
                ? { schemaVersion: 2, provider: "LanceDb", lanceDb: { path: databasePath } }
                : undefined;
        }
        return undefined;
    } catch {
        return undefined;
    }
}

export async function readInstallationManifest(manifestPath: string): Promise<InstallationManifest | undefined> {
    try {
        const candidate = JSON.parse(await fs.readFile(manifestPath, "utf8")) as Partial<InstallationManifest>;
        const hostExecutable = normalizeHostExecutable(candidate.hostExecutable);
        if (candidate.schemaVersion !== 1 || typeof candidate.version !== "string" || !candidate.version.trim() || !hostExecutable) {
            return undefined;
        }
        await fs.access(hostExecutable);
        return { schemaVersion: 1, version: candidate.version.trim(), hostExecutable };
    } catch {
        return undefined;
    }
}

export function providerEnvironment(
    installed: ResolvedInstallerConfiguration
): Record<string, string> {
    if (installed.provider === "LanceDb") {
        return {
            LocalRag__VectorStore__Provider: "LanceDb",
            LocalRag__VectorStore__LanceDb__Path: installed.lanceDbPath
        };
    }
    return {
        LocalRag__VectorStore__Provider: "Weaviate",
        LocalRag__VectorStore__Weaviate__Endpoint: installed.weaviateEndpoint
    };
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
    return typeof value === "object" && value !== null && !Array.isArray(value)
        ? value as Record<string, unknown>
        : undefined;
}

function normalizeHttpEndpoint(value: unknown): string | undefined {
    if (typeof value !== "string") return undefined;
    try {
        const parsed = new URL(value.trim());
        if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return undefined;
        if (!parsed.hostname || parsed.username || parsed.password || parsed.search || parsed.hash) return undefined;
        return parsed.toString().replace(/\/$/, "");
    } catch {
        return undefined;
    }
}

function normalizeAbsolutePath(value: unknown): string | undefined {
    if (typeof value !== "string") return undefined;
    const trimmed = value.trim();
    return trimmed && path.isAbsolute(trimmed) ? path.normalize(trimmed) : undefined;
}

function normalizeHostExecutable(value: unknown): string | undefined {
    if (typeof value !== "string") return undefined;
    const trimmed = value.trim();
    return path.isAbsolute(trimmed) && path.extname(trimmed).toLowerCase() === ".exe" ? trimmed : undefined;
}
