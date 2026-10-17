import { promises as fs } from "node:fs";
import * as path from "node:path";
import * as crypto from "node:crypto";

export interface SharedTokenStorage {
    createDirectory(directory: string): Promise<void>;
    createExclusive(filename: string, token: string): Promise<void>;
    read(filename: string): Promise<string>;
    pause(milliseconds: number): Promise<void>;
}

const localStorage: SharedTokenStorage = {
    createDirectory: async directory => { await fs.mkdir(directory, { recursive: true }); },
    createExclusive: async (filename, token) => {
        const handle = await fs.open(filename, "wx", 0o600);
        try { await handle.writeFile(token, "utf8"); await handle.sync(); }
        finally { await handle.close(); }
    },
    read: filename => fs.readFile(filename, "utf8"),
    pause: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))
};

function validToken(token: string): boolean { return /^[\x21-\x7e]{32,}$/.test(token); }
const transientFileErrors = new Set(["EACCES", "EPERM", "EBUSY", "ENOENT"]);

/** Shared with Start-LocalRag.ps1. Never log the result or place it in browser URLs. */
export async function sharedHostToken(dataDirectory: string, previousToken?: string, storage: SharedTokenStorage = localStorage): Promise<string> {
    const tokenPath = path.join(dataDirectory, "client-token");
    const candidate = previousToken?.trim() || crypto.randomBytes(32).toString("base64url");
    if (!validToken(candidate)) throw new Error("The stored Local RAG credential is invalid. Repair the installation before connecting.");
    let lastAccessError: unknown;
    // FileShare.None in the Windows launcher can deny both CreateNew and ReadAllText.
    // Retry the complete operation: a failed exclusive create never replaces the winner.
    for (let attempt = 0; attempt < 20; attempt++) {
        try {
            await storage.createDirectory(dataDirectory);
            try { await storage.createExclusive(tokenPath, candidate); }
            catch (error) { if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error; }
            const token = (await storage.read(tokenPath)).trim();
            if (validToken(token)) return token;
            lastAccessError = undefined;
        } catch (error) {
            if (!transientFileErrors.has((error as NodeJS.ErrnoException).code ?? "")) throw error;
            lastAccessError = error;
        }
        if (attempt < 19) await storage.pause(50);
    }
    if (lastAccessError) throw new Error("Shared Local RAG credential is locked or access is denied. Close other launchers and retry; if access remains denied, check this account's permissions to the Local RAG data directory or repair the installation.", { cause: lastAccessError });
    throw new Error("Shared Local RAG credential is incomplete or invalid. Repair the installation before connecting.");
}
