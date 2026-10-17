"use strict";
const LocalRagManagerLinks = {
    editorFileUri(root, relativePath, line) {
        const file = root.replaceAll("\\", "/").replace(/\/$/, "") + "/" + relativePath.replaceAll("\\", "/");
        const encoded = encodeURIComponent(file).replace(/%2F/gi, "/").replace(/%3A/gi, ":");
        return `vscode://file/${encoded}:${Math.max(1, Math.trunc(line))}`;
    }
};
if (typeof module !== "undefined") module.exports = LocalRagManagerLinks;
