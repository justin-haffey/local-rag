---
name: local-rag-source-triage
description: Triages visible Local RAG sources by inventorying sources and checking bounded indexing status. Use when a coding agent needs to find ready, stale, failed, processing, or unknown sources before retrieval. Do not use to register, remove, repair, or reindex sources.
---

# Triage Local RAG Sources

Build a read-only readiness overview using `rag_list_sources` and `rag_get_source_status`.

## Workflow

1. Call `rag_list_sources` once.
2. If the user names sources, resolve exact IDs and inspect those. Otherwise inspect every source when there are at most 20; for larger inventories, check up to 20 relevant or recently reported sources and disclose the omitted count.
3. Call `rag_get_source_status` once per selected source. Do not retry zero-information status responses; retry one transient failure per source at most.
4. Classify each source only from explicit response fields:
   - ready: a successful/ready state is reported;
   - processing: indexing/queued/running is reported;
   - failed: a failure/error state is reported;
   - stale: the service explicitly reports staleness;
   - unknown: status is absent, ambiguous, or unavailable.
5. Rank blockers first, then ready sources. Recommend `$local-rag-indexing-verification` when searchability needs confirmation.

## Safe Defaults

- Read only; this MCP surface exposes no source mutation tool.
- Never equate an old timestamp with staleness unless the user supplies a freshness threshold or the service reports stale.
- Do not print authentication values or backend configuration.

## Stop Rules

- Stop after the selected inventory is classified or the 20-source default bound is reached.
- After repeated server-wide failure, report one concise blocker rather than calling every source.

## Output

Return a table with source ID, label/path if returned, reported status, last success, classification, and diagnostic. Include coverage and limitations.
