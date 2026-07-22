# PLAN-01: Phase 1 — Local MVP Implementation Plan

## Summary

Build a greenfield, Windows x64 local RAG product: a .NET 10 ASP.NET Core host, a VS Code extension, SQLite operational state, and loopback-only Weaviate Docker. Use a shared application layer for REST and the four read-only MCP tools.

- Target `net10.0` (active LTS), TypeScript/VS Code Extension API, Weaviate `1.38.2`, and Streamable HTTP MCP at `/mcp`. [ .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), [MCP HTTP transport](https://csharp.sdk.modelcontextprotocol.io/concepts/transports/transports.html), [Weaviate Docker](https://docs.weaviate.io/deploy/installation-guides/docker-installation)
- Use `BAAI/bge-small-en-v1.5` as the sole local ONNX embedding profile: 384-dimensional, cosine-normalized vectors, 512-token model maximum; persist the fetched model revision and hashes. [model card](https://huggingface.co/BAAI/bge-small-en-v1.5)
- Preserve the existing `.codex` assets; create the product at the repository root under `src/`, `tests/`, `vscode-extension/`, and `deploy/`.

## Implementation Changes

- Establish the solution structure from the design: Domain, Application, FileSystem, Chunking, Embeddings, Weaviate, SQLite, Host, Contracts, and test projects. Use central package management, `global.json`, and pinned container/model revisions.
- Implement application interfaces for source lifecycle, indexing, search, chunks, embedding, and policy. REST and MCP adapters must call these interfaces only; neither may query Weaviate directly.
- Persist `Sources`, `Files`, `Chunks`, `IndexJobs`, `IndexCheckpoints`, `EmbeddingProfiles`, `SchemaVersions`, and dead-letter failures in SQLite. Use canonical roots, relative paths, content hashes, deterministic file/chunk IDs, and source removal that stops watching and deletes its indexed chunks.
- Add a loopback Weaviate Compose deployment with a persistent volume, `RagChunk_v1`, and vectorization disabled; the host supplies all vectors. Implement schema initialization, batch upsert, stale-chunk deletion only after successful replacement, and hybrid BM25/vector retrieval with default alpha `0.65`. [bring-your-own vectors](https://docs.weaviate.io/weaviate/concepts/search/vector-search), [hybrid search](https://docs.weaviate.io/weaviate/concepts/search/hybrid-search)
- Build the indexing pipeline: iterative discovery; default VCS/build/dependency/secret-pattern ignores; no symlink traversal; 5 MiB file limit; encoding/line-ending normalization; generic line-preserving swe/text chunks using the selected model tokenizer (384-token target, 480-token maximum, 64-token overlap); ONNX embedding with mean pooling and L2 normalization; bounded queues and retries.
- Add a recursive `FileSystemWatcher` with 5-second coalescing and 1.5-second stability checks. Run a startup and explicit reindex reconciliation; if watcher overflow occurs, mark the source degraded and require reindex—automatic overflow recovery remains Phase 2.
- Host REST at `/api/v1`: source create/list/get/delete, source status, explicit reindex, search, chunk retrieval, plus live/ready health. Return only source IDs, display names, relative paths, line ranges, hashes, scores, status, and bounded diagnostics.
- Require a generated bearer token for REST and MCP. Keep it in VS Code Secret Storage, pass it only to the child host environment, write a discovery file containing endpoint/PID/token reference, bind strictly to `127.0.0.1`, and reject non-loopback host headers. The local token can access registered visible sources; unknown, removed, paused, and hidden source IDs are rejected.
- Expose only `rag_search`, `rag_get_chunk`, `rag_list_sources`, and `rag_get_source_status` through authenticated, stateless Streamable HTTP MCP. Enforce request/result limits, source scope, path redaction, cancellation, and no administrative or arbitrary-Weaviate operations.
- Build the extension as the backend supervisor: package and launch the self-contained Windows x64 host, poll readiness, register Explorer commands for Mark as RAG Source, Remove Source, Reindex Source, and Show Source Status, and show indexing/ready/degraded/error state. Docker Desktop and `docker compose up -d` are documented prerequisites; the extension does not manage Docker.

## Features

### [Feature 1]

- [Feature Name]
- [Table of Requirements (rows numbered `R#.##-[requirement_name]`)]
- [Link to the detailed implementation plan for each Feature in `.swe\features\<feature>.md`]

## Test Plan

- Unit-test path/ignore policy, deterministic identities, normalization and chunk boundaries, embedding pooling/normalization, event coalescing, manifest diffs, token validation, auth scope, and hybrid-result mapping.
- Integration-test SQLite migrations/restart recovery, Weaviate collection setup, upsert/stale deletion, ONNX fixture embeddings, REST/MCP contract parity, authentication, limits, and cancellation against containerized Weaviate.
- End-to-end test a fixture repository: register, index, search through REST and MCP, restart the host, run explicit reindex without re-embedding unchanged content, then create/change/rename/delete files and verify index state and status.
- Acceptance gate: all 15 MVP criteria in [DESIGN.md](/C:/Users/justin/Source/codex/codex-codegen/.swe/DESIGN.md:1428), including loopback-only services, usable search during indexing, and no direct Weaviate MCP access.

## Assumptions

- Phase 1 is single-user, Windows x64, English-focused, and requires Docker Desktop; macOS/Linux packaging, multi-client grants, automatic watcher-overflow recovery, language-aware symbol extraction, search UI, resources/prompts, and write-capable MCP tools are Phase 2+.
- `symbolName` is nullable for generic chunks; results always include relative path and line range.
- The selected embedding model’s immutable revision/checksums become part of the initial embedding profile; changing it creates a new profile and requires reindexing.
