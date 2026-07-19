---
name: local-rag-source-status
description: Retrieves indexing status and the last successful indexing time for one Local RAG source. Use when a user provides a source ID or asks whether one indexed folder is ready, stale, failed, or still processing. Do not use for a multi-source census or to trigger indexing.
---

# Get Local RAG Source Status

Inspect one source through `mcp__local_rag__rag_get_source_status`.

## Core Contract

- Require an exact source ID returned by `rag_list_sources`.
- Report only states, timestamps, errors, and progress fields present in the response.
- This is read-only; never claim that the source was reindexed or repaired.

## Workflow

1. If an exact source ID is supplied, call `mcp__local_rag__rag_get_source_status` with `{ "sourceId": "..." }`.
2. If the user names a source but lacks its ID, call `rag_list_sources` once and resolve only an unambiguous match. Ask for selection when multiple sources match.
3. Summarize the reported state, last successful index time, and any reported failure or progress detail.
4. Recommend `$local-rag-indexing-verification` only when the user needs evidence that content is searchable, not merely a status field.

## Retry and Stop Rules

- Retry the status call once only after a transient MCP failure.
- Do not guess a source ID or substitute a path.
- Stop after one source is reported; use `$local-rag-source-triage` for multiple sources.

## Validation

- Confirm the returned source corresponds to the requested ID.
- Label missing timestamps or details as unavailable, not as failures.

## Output

Return the source ID, reported status, last successful indexing time, diagnostics, and any read-only limitation.
