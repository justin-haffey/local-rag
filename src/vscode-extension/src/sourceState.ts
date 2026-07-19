import * as crypto from "node:crypto";
import * as path from "node:path";

export const missingRootMessage = "Source root is no longer accessible.";

export interface SourceState {
    sourceId: string;
    rootPathHash: string;
    displayName: string;
    status: string;
    lastScanUtc?: string;
    lastSuccessfulIndexUtc?: string;
    lastError?: string;
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
