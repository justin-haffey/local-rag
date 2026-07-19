import * as fs from "node:fs/promises";
import * as path from "node:path";
import type { WorkspaceConfiguration } from "vscode";

export const packagedWeaviateEndpoint = "http://127.0.0.1:8080";

export interface InstallerSettings {
    schemaVersion: number;
    weaviateEndpoint: string;
}

export interface InstallationManifest {
    schemaVersion: number;
    version: string;
    hostExecutable: string;
}

export interface ResolvedInstallerConfiguration {
    weaviateEndpoint: string;
    hostExecutable: string;
}

export async function resolveInstallerConfiguration(
    configuration: WorkspaceConfiguration,
    localAppData: string | undefined
): Promise<ResolvedInstallerConfiguration> {
    if (!localAppData) {
        throw new Error("Local RAG installation discovery is unavailable because LOCALAPPDATA is not set. Repair or reinstall Local RAG.");
    }

    // The installer owns this file; workspace/user settings intentionally override it.
    const installerSettings = localAppData
        ? await readInstallerSettings(path.join(localAppData, "LocalRag", "install-settings.json"))
        : undefined;
    // Host binaries live outside the VSIX so extension updates cannot replace the runtime.
    const installation = await readInstallationManifest(path.join(localAppData, "LocalRag", "installation.json"));
    if (!installation) {
        throw new Error("Local RAG host installation is missing or invalid. Repair or reinstall Local RAG to restore installation.json and the standalone host.");
    }

    const inspectedEndpoint = configuration.inspect<string>("weaviateEndpoint");
    const hasExplicitEndpoint = inspectedEndpoint !== undefined && [
        inspectedEndpoint.globalValue,
        inspectedEndpoint.workspaceValue,
        inspectedEndpoint.workspaceFolderValue
    ].some(value => value !== undefined);

    const configuredEndpoint = hasExplicitEndpoint
        ? configuration.get<string>("weaviateEndpoint")
        : installerSettings?.weaviateEndpoint;

    return {
        weaviateEndpoint: normalizeHttpEndpoint(configuredEndpoint) ?? packagedWeaviateEndpoint,
        hostExecutable: installation.hostExecutable
    };
}

export async function readInstallerSettings(settingsPath: string): Promise<InstallerSettings | undefined> {
    try {
        const candidate = JSON.parse(await fs.readFile(settingsPath, "utf8")) as Partial<InstallerSettings>;
        if (candidate.schemaVersion !== 1) return undefined;

        const weaviateEndpoint = normalizeHttpEndpoint(candidate.weaviateEndpoint);
        if (!weaviateEndpoint) return undefined;

        return {
            schemaVersion: 1,
            weaviateEndpoint
        };
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

function normalizeHostExecutable(value: unknown): string | undefined {
    if (typeof value !== "string") return undefined;
    const trimmed = value.trim();
    return path.isAbsolute(trimmed) && path.extname(trimmed).toLowerCase() === ".exe" ? trimmed : undefined;
}
