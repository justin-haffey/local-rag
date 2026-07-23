---
name: local-rag-list-sources
description: Lists developer-folder sources visible through the plugin-provided Local RAG MCP server. Use when a user asks what is indexed, needs source IDs before a scoped search, or needs to confirm source visibility. Do not use for source creation, deletion, or reindexing.
---

# List Local RAG Sources

Return the current source inventory with `mcp__local_rag__rag_list_sources` without changing index state.

## Core Contract

- Treat the tool response as the authority for source IDs and reported metadata.
- Do not infer readiness, freshness, or indexing success from source presence alone.
- Do not expose tokens, backend configuration, or unrelated local paths.

## Workflow

1. Call `mcp__local_rag__rag_list_sources` with no arguments.
2. Preserve each returned source ID exactly; include its name, root/path, state, or timestamps only when the tool returns them.
3. If no sources are visible, say so and stop. Explain that registration or indexing must occur through an existing Local RAG management surface; this skill has no mutation tool.
4. If the user needs health or freshness, hand off to `$local-rag-source-status` for selected source IDs or `$local-rag-source-triage` for a bounded inventory review.

## Retry and Stop Rules

- Retry once only for a clearly transient transport or availability failure.
- Do not repeat a successful inventory call in the same task unless the user asks for a refresh.
- Stop after the inventory is reported or the bounded retry fails.

## Validation

- Confirm every reported source ID came from the tool response.
- Distinguish an empty inventory from a tool failure.

## Output

Return a compact source table or list, followed by any limitation or next useful skill.
