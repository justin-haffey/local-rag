---
name: rag-index
description: Indexes one accessible local folder through the privileged Local RAG management MCP surface. Use only when the user explicitly asks to register or index a folder. Do not use for searching, reindexing an existing source, removal, or reset.
---

# Index a Folder with Local RAG

Use `mcp__local_rag_mgmt__rag_index` once to register an exact folder and queue its initial index.

## Core Contract

- Require one explicit folder path from the user.
- Treat invocation of this manually selected skill with that path as authorization to register and index the folder.
- Send the path only to the management MCP tool. Do not access Local RAG SQLite or Weaviate directly.
- Do not claim that indexing completed when the response says only that work was queued.

## Workflow

1. Confirm the input identifies one folder path. Ask for the path if it is absent or ambiguous.
2. Call `mcp__local_rag_mgmt__rag_index` once with `folderPath` set to the supplied path.
3. Report only the operation ID, source ID, state, bounded counts, and safe error code returned by the tool.
4. If the result reports an existing or overlapping root, describe the bounded conflict or no-op without attempting another spelling of the path.

## Safety and Stop Rules

- Never mutate, move, or delete files in the source folder.
- Do not broaden the requested folder, enumerate its contents, or substitute a parent or child path.
- Do not retry automatically after a timeout, transport failure, validation failure, conflict, or uncertain result. Stop and explain that retrying requires a new explicit request.
- Never expose management tokens, raw dependency errors, source content, connection strings, or unreturned canonical paths.

## Validation

- Confirm exactly one tool call was made with the user-supplied folder path.
- Distinguish queued, completed, no-op/conflict, and failed states exactly as returned.

## Output

Return the requested path as the user supplied it, the safe management result, and whether initial indexing was queued. Include no secret or backend details.
