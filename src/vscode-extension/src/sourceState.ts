import * as crypto from "node:crypto";
import * as path from "node:path";

export const missingRootMessage = "Source root is no longer accessible.";

export interface RecoveryState {
    state: string;
    desiredGeneration: number;
    completedGeneration: number;
    activeGeneration?: number;
    causes: readonly string[];
    requestedUtc?: string;
    startedUtc?: string;
    lastSucceededUtc?: string;
    lastFailedUtc?: string;
    lastOutcome?: string;
    lastErrorCode?: string;
    lastErrorSummary?: string;
    changedFiles: number;
    deletedFiles: number;
    unchangedFiles: number;
}

export interface SourceState {
    sourceId: string;
    rootPathHash: string;
    displayName: string;
    status: string;
    lastScanUtc?: string;
    lastSuccessfulIndexUtc?: string;
    lastError?: string;
    recovery?: RecoveryState;
}

export interface RecoveryPresentation {
    label: string;
    detail?: string;
    action?: "Queue recovery";
}

/** Produces the same opaque, case-insensitive Windows path identity returned by the backend. */
export function rootPathHash(rootPath: string): string {
    const resolved = path.resolve(rootPath);
    const pathRoot = path.parse(resolved).root;
    const canonical = (resolved.length > pathRoot.length ? resolved.replace(/[\\/]+$/, "") : resolved).toUpperCase();
    return crypto.createHash("sha256").update(canonical, "utf8").digest("hex");
}

export function isMissingSource(source: SourceState): boolean {
    return source.status.toLowerCase() === "degraded" && source.lastError === missingRootMessage;
}

/** Converts additive recovery state into a concise, actionable status without exposing private diagnostics. */
export function recoveryPresentation(source: SourceState): RecoveryPresentation {
    const recovery = source.recovery;
    if (!recovery) return { label: source.status, detail: source.lastError };

    const progress = `generation ${recovery.completedGeneration} of ${recovery.desiredGeneration}`;
    switch (recovery.state.toLowerCase()) {
        case "running":
            return { label: "Recovering", detail: `Automatic index recovery is running (${progress}).` };
        case "queued":
            return { label: "Recovery queued", detail: `Automatic index recovery is queued (${progress}).` };
        case "degraded": {
            const failure = recovery.lastErrorSummary ??
                (recovery.lastErrorCode ? `Recovery stopped with ${recovery.lastErrorCode}.` : "Automatic index recovery could not complete.");
            return { label: "Recovery degraded", detail: `${failure} Restore local dependencies, then queue recovery.`, action: "Queue recovery" };
        }
        default:
            return { label: source.status, detail: recovery.lastOutcome ? `Last recovery: ${recovery.lastOutcome}.` : source.lastError };
    }
}

/** Returns the shared MCP bearer token only when the environment value is non-empty. */
export function environmentMcpToken(value: string | undefined): string | undefined {
    const token = value?.trim();
    return token ? token : undefined;
}
