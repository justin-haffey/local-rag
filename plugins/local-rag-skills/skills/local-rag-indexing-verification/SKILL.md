---
name: local-rag-indexing-verification
description: Verifies that a Local RAG source is visible, reports usable indexing status, and returns retrievable sentinel content. Use after indexing, reindexing, source configuration changes, or suspected stale results. Do not use to trigger indexing or to certify complete corpus coverage.
---

# Verify Local RAG Indexing

Perform a read-only, evidence-based indexing check across inventory, status, search, and chunk retrieval.

## Inputs

- Target source name or ID.
- Prefer one or two expected distinctive symbols, filenames, routes, or phrases supplied by the user.
- Optional freshness threshold or expected indexing time.

## Workflow

1. Call `rag_list_sources`; resolve the target to one exact source ID. Stop on no match or ambiguity.
2. Call `rag_get_source_status` for that ID. Record the reported state and last successful indexing time.
3. Choose one or two sentinel queries that are distinctive for the source. Prefer user-supplied expectations; otherwise use known repository identifiers from the active task, not generic words.
4. Call `rag_search` scoped to the source ID with `limit: 5` for each sentinel, stopping after two searches.
5. Fetch one top, clearly relevant chunk with `rag_get_chunk` and verify its returned provenance and content align with the sentinel.
6. Report one outcome:
   - verified: visible source, acceptable reported status, sentinel hit, and retrievable matching chunk;
   - partial: some checks pass but status, freshness, or retrieval evidence is missing;
   - failed: source missing, explicit failed status, no sentinel hit after two distinct queries, or chunk retrieval failure.

## Safety and Accuracy

- This skill cannot reindex, repair, register, or delete a source.
- Do not call an old timestamp stale without a supplied threshold or explicit stale state.
- A successful sentinel proves sample retrievability, not complete indexing.
- Confirm exact current bytes separately when freshness is critical.

## Retry and Stop Rules

- Retry a transient call once; do not retry zero-result searches unchanged.
- Stop after two searches and one successful chunk retrieval, or immediately on an unrecoverable source/status failure.

## Output

Return the outcome, checks performed, source ID/status/time, sentinel evidence and chunk ID, limitations, and the required operator action when read-only verification fails.
