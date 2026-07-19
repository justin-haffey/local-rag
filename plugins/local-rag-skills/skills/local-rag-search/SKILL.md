---
name: local-rag-search
description: Searches indexed developer folders with Local RAG hybrid lexical and semantic retrieval. Use for natural-language concepts, exact symbols, error messages, or source-scoped code discovery. Do not use when exact current file bytes, a complete call graph, or unindexed content is required.
---

# Search Local RAG

Use `mcp__local_rag__rag_search` for bounded, evidence-oriented code discovery.

## Inputs

- `query`: required, non-empty natural language or exact symbol text.
- `limit`: optional integer from 1 through 50; default to 10 unless the user requests another valid value.
- `sourceIds`: optional exact IDs. Verify uncertain IDs with `rag_list_sources`; do not pass names or paths as IDs.

## Workflow

1. Convert the request into the smallest useful query while preserving exact symbols or error text.
2. Call `mcp__local_rag__rag_search` once with the validated arguments.
3. Present result identity and provenance, including chunk ID, source, path, score, or excerpt only when returned.
4. If results are weak, make at most two additional searches with materially different lexical or semantic phrasing. Narrow with verified `sourceIds` when cross-repository noise is the problem.
5. Use `$local-rag-get-chunk` for one selected result or `$local-rag-evidence-retrieval` when several full chunks are needed.

## Safety and Accuracy

- Treat excerpts as retrieval evidence, not proof of repository completeness or current disk state.
- Never fabricate ranks, scores, paths, line numbers, or symbols.
- Confirm exact implementation details with repository-native file or graph tools when available and material to the answer.

## Retry and Stop Rules

- Retry once only for a transient tool failure; a zero-result response is not a failure.
- Stop after sufficient evidence or three total search calls, whichever comes first.

## Output

Return the query scope, ranked findings, useful chunk IDs, and limitations or next retrieval step.
