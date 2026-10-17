const assert = require("node:assert/strict");
const test = require("node:test");
const { editorFileUri } = require("../../LocalRag.Host/wwwroot/manager-links.js");

test("manager editor handoff preserves external Windows roots, spaces, Unicode and URI delimiters", () => {
    assert.equal(editorFileUri("C:\\External references", "日本語\\notes #1?.md", 12), "vscode://file/C:/External%20references/%E6%97%A5%E6%9C%AC%E8%AA%9E/notes%20%231%3F.md:12");
    assert.equal(editorFileUri("\\\\server\\share", "a%name.txt", 1), "vscode://file///server/share/a%25name.txt:1");
    assert.equal(editorFileUri("D:\\", "plain.txt", 0), "vscode://file/D:/plain.txt:1");
});
