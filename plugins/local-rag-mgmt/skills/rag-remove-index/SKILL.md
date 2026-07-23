---
name: rag-remove-index
description: Removes the Local RAG index for one exact registered folder through a two-phase confirmation challenge. Use only when the user explicitly asks to remove that folder's index. Do not use to delete source files, remove multiple roots, or reset all Local RAG data.
---

# Remove One Local RAG Folder Index

Use `mcp__local_rag_mgmt__rag_remove_index` in exactly two phases and pause for explicit confirmation before the destructive phase.

## Core Contract

- Require one exact folder path from the user.
- Removal deletes only Local RAG state and vectors associated with the exact registered root. It must never delete or modify the folder or source files.
- The host, not this skill, canonicalizes and resolves the root.
- A typed phrase without a host-issued challenge is not sufficient confirmation.

## Two-Phase Workflow

1. Confirm the user supplied one unambiguous folder path.
2. Call `mcp__local_rag_mgmt__rag_remove_index` once with `folderPath` set to that path and omit `confirmationToken`.
3. Continue only if the tool returns `ConfirmationRequired`, a one-use token, an expiry, and a fixed warning. Do not invent missing confirmation data.
4. Show the fixed warning and expiry to the user. State clearly that the folder's Local RAG index will be irreversibly removed while its source files remain untouched. Ask the user to explicitly confirm, then pause.
5. If the user declines, changes the target, or does not confirm before expiry, stop without a second tool call. A changed target requires a new challenge.
6. After explicit confirmation, call the same tool once with the identical `folderPath` and the returned `confirmationToken`.
7. Report only the operation ID, source ID, state, bounded counts, and safe error code returned by the tool.

## Safety and Stop Rules

- Never reveal, log, persist, transform, or ask the user to repeat the confirmation token.
- Never call the confirmed phase without a fresh challenge from the current task and explicit user confirmation after its warning.
- Never retry either phase automatically for any reason. After an expiry, rejection, timeout, transport failure, replay error, uncertain result, or safe failure, stop. A later attempt must start at phase one and obtain a new token.
- Do not attempt alternate path spellings when the host rejects or cannot resolve the root.
- Never expose management tokens, raw errors, source content, connection strings, or unreturned canonical paths.

## Validation

- Confirm the second call used the same folder path and the exact returned token.
- Confirm explicit user approval occurred between the challenge and destructive calls.
- Distinguish `SourceNotFound`, refusal, failure, and successful removal exactly as returned.

## Output

Before confirmation, return only the host warning, expiry, target supplied by the user, and confirmation question. Afterward, return the safe terminal result and reaffirm that source files were not deleted.
