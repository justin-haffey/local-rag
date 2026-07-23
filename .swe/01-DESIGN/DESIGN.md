# High-Level Design Document: Local RAG Pipeline for Developer Folders with Hosted C# MCP Server

**Version:** 2.0  
**Date:** July 2026  
**Status:** Proposed Architecture  
**Primary Stack:** C#/.NET 8+, ASP.NET Core, Model Context Protocol C# SDK, Weaviate, Docker, VS Code Extension, SQLite, Local Embeddings

---

## 1. Executive Summary

This document defines a local-first Retrieval-Augmented Generation (RAG) platform for software-development folders. Developers can designate one or more folders as managed RAG sources from Visual Studio Code. The platform continuously discovers, parses, chunks, embeds, and indexes supported files into Weaviate, then exposes the indexed content through:

- A VS Code extension.
- A conventional local HTTP API.
- A hosted C# Model Context Protocol (MCP) server.
- Optional CLI and automation clients.

The hosted MCP server is a first-class integration surface for AI coding assistants and external agent hosts. It exposes controlled search, vector, metadata, and index-inspection operations over Streamable HTTP while reusing the same application services as the VS Code and REST interfaces.

The design is local by default. Source files, embeddings, metadata, and search results remain on the developer workstation unless the operator explicitly configures a remote embedding model, remote Weaviate instance, or network-accessible MCP endpoint.

---

## 2. Objectives

### 2.1 Primary Objectives

1. Allow a developer to mark any accessible folder as a RAG source from VS Code.
2. Perform reliable initial indexing and incremental synchronization.
3. Produce high-quality, code-aware chunks with stable identities.
4. Support local embedding generation and bring-your-own-vector workflows.
5. Provide semantic, lexical, and hybrid retrieval.
6. Expose retrieval through a hosted C# MCP server for AI tools and agents.
7. Preserve folder and file security boundaries.
8. Remain usable for repositories ranging from small projects to multi-gigabyte monorepos.
9. Support extensibility for new file types, chunkers, embedding models, and vector stores.

### 2.2 Non-Goals for the Initial Release

The first release does not attempt to:

- Replace a full language server or compiler-grade semantic index.
- Execute arbitrary code returned by search.
- Provide autonomous write access to indexed repositories through MCP.
- Synchronize source files to a cloud service by default.
- Guarantee transactional consistency across the file system and vector database.
- Parse every binary or proprietary file format.
- Serve as an enterprise multi-tenant SaaS platform.

---

## 3. Principal Use Cases

### 3.1 Folder Registration

1. The user right-clicks a folder in the VS Code Explorer.
2. The user selects **Mark as RAG Source**.
3. The extension sends the canonical folder path and optional settings to the backend.
4. The backend validates access, persists the source definition, creates a watcher, and starts the initial scan.
5. The extension displays a decoration and indexing state.

### 3.2 Continuous Indexing

1. Files are created, changed, renamed, or deleted.
2. The watcher emits normalized change candidates.
3. A debounce and stability stage suppresses transient events.
4. Eligible files are parsed and chunked.
5. Changed chunks are embedded and upserted.
6. Obsolete chunks are removed.
7. Source statistics and health are updated.

### 3.3 Interactive Search

A user searches from the VS Code sidebar and can:

- Select one or more source folders.
- Restrict by language, path, file type, branch, or tag.
- Choose lexical, vector, or hybrid search.
- Open a result at the indexed line range.
- Copy selected chunks into a prompt or workspace note.

### 3.4 MCP-Assisted Retrieval

An MCP-capable client connects to the hosted C# MCP endpoint and can:

- Discover available RAG sources.
- Execute hybrid or vector search.
- Retrieve a specific indexed chunk.
- Fetch neighboring chunks for context expansion.
- Inspect source statistics and indexing status.
- Request similarity search using text or, when authorized, a supplied vector.

MCP tools are read-only by default. Administrative index operations are excluded from the default tool set and require a separate privileged policy if enabled.

---

## 4. Architecture Overview

```text
┌──────────────────────────────────────────────────────────────────────┐
│                          Client Surfaces                             │
│                                                                      │
│  VS Code Extension      MCP Clients / Agents       CLI / Automation │
└──────────────┬──────────────────────┬──────────────────────┬─────────┘
               │ HTTPS/HTTP           │ Streamable HTTP MCP  │ HTTP
               ▼                      ▼                      ▼
┌──────────────────────────────────────────────────────────────────────┐
│                    ASP.NET Core Backend Host                         │
│                                                                      │
│  REST Endpoints       Hosted MCP Server       Health / Metrics       │
│          │                    │                        │              │
│          └──────────────┬─────┴────────────────────────┘              │
│                         ▼                                            │
│                  Application Services                                │
│                                                                      │
│  Source Registry  Search Service  Chunk Service  Index Coordinator  │
│  Policy Service   Embedding Service  Job Queue    Result Formatter   │
└──────────────┬───────────────────┬───────────────────────┬───────────┘
               │                   │                       │
               ▼                   ▼                       ▼
┌────────────────────┐  ┌─────────────────────┐  ┌────────────────────┐
│ File-System Layer  │  │ Metadata / State    │  │ Vector Store       │
│                    │  │                     │  │                    │
│ Scanners            │  │ SQLite             │  │ Weaviate           │
│ Watchers             │  │ Source registry    │  │ Chunks + vectors   │
│ Stability checks    │  │ Jobs/checkpoints   │  │ Hybrid retrieval   │
└────────────────────┘  └─────────────────────┘  └────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────────────────────┐
│                     Processing Plug-ins                              │
│                                                                      │
│ File filters | Parsers | Language-aware chunkers | Embedding models │
└──────────────────────────────────────────────────────────────────────┘
```

### 4.1 Architectural Principle: One Core, Multiple Adapters

The REST API, MCP server, VS Code extension, and CLI must not implement independent retrieval or indexing logic. Each interface is an adapter over the same application services.

This prevents:

- Different ranking behavior between clients.
- Duplicate authorization logic.
- Divergent filter semantics.
- MCP-specific data leakage.
- Inconsistent result schemas.

---

## 5. Major Components

## 5.1 VS Code Extension

**Technology:** TypeScript and the VS Code Extension API.

### Responsibilities

- Register folder context-menu commands.
- Display managed-source decorations and state.
- Start or connect to the backend.
- Register and remove source folders.
- Show indexing progress, warnings, and failures.
- Provide a search sidebar and result navigation.
- Persist user-interface preferences in workspace or global state.
- Detect multi-root workspaces and remote-development contexts.
- Avoid sending source content through extension telemetry.

### Commands

- `localRag.markAsSource`
- `localRag.removeSource`
- `localRag.reindexSource`
- `localRag.pauseSource`
- `localRag.resumeSource`
- `localRag.search`
- `localRag.openDashboard`
- `localRag.showSourceStatus`
- `localRag.copyMcpConfiguration`

### Extension-to-Backend Communication

Recommended for the MVP:

- Loopback HTTP over `127.0.0.1`.
- A generated per-installation bearer token.
- A backend discovery file containing endpoint, process identifier, and token reference.
- Readiness polling through `/health/ready`.

Alternative transports such as named pipes or Unix domain sockets may be added later for stricter local isolation.

---

## 5.2 ASP.NET Core Backend Host

**Technology:** .NET 8 or later, ASP.NET Core Generic Host.

The backend is a long-running process containing:

- REST endpoints.
- The hosted MCP server.
- Background indexing workers.
- Source watchers.
- State persistence.
- Search and embedding services.
- Health, metrics, and diagnostics endpoints.

### Hosting Modes

1. Child process launched and supervised by the VS Code extension.
2. User-level background service.
3. Windows Service.
4. `systemd --user` service.
5. Containerized host with explicit bind mounts.
6. Network-hosted service for controlled team environments.

The local child-process mode is the recommended MVP. The service mode is recommended once multiple editors or agents must share the same index.

---

## 5.3 Hosted C# MCP Server

### 5.3.1 Purpose

The MCP server provides a standardized retrieval interface for AI agents, coding assistants, and other MCP clients. It exposes search and vector-store capabilities without granting clients direct access to Weaviate or unrestricted access to the local file system.

### 5.3.2 Technology and Transport

- Official Model Context Protocol C# SDK.
- `ModelContextProtocol.AspNetCore` for HTTP hosting.
- Streamable HTTP transport.
- ASP.NET Core endpoint mapping.
- JSON Schema-derived tool inputs and structured results.
- Stateless HTTP mode when supported by the selected stable SDK and negotiated protocol revision.
- Optional `stdio` companion host for clients that cannot connect over HTTP.

Streamable HTTP is the preferred hosted transport. Legacy SSE transport should not be enabled unless compatibility requires it and compensating controls are applied.

### 5.3.3 Endpoint

Recommended local endpoint:

```text
http://127.0.0.1:{port}/mcp
```

Recommended remotely hosted endpoint:

```text
https://rag.example.internal/mcp
```

The server must bind to loopback only unless the operator explicitly enables network access.

### 5.3.4 MCP Capability Model

#### Tools

| Tool | Purpose | Default Access |
|---|---|---|
| `rag_search` | Hybrid, vector, or lexical search over indexed chunks | Read |
| `rag_find_similar` | Find chunks similar to a chunk ID or supplied text | Read |
| `rag_vector_search` | Search using a caller-supplied vector | Restricted read |
| `rag_get_chunk` | Retrieve a chunk and its metadata | Read |
| `rag_expand_context` | Retrieve neighboring chunks around a result | Read |
| `rag_list_sources` | List sources visible to the caller | Read |
| `rag_get_source_status` | Return index health and counts | Read |
| `rag_list_languages` | Enumerate indexed languages and counts | Read |
| `rag_get_index_capabilities` | Report supported models, dimensions, and search modes | Read |
| `rag_reindex_source` | Queue a source re-index | Administrative; disabled by default |
| `rag_delete_source_index` | Delete indexed objects for a source | Administrative; disabled by default |

#### Resources

Optional MCP resources may expose stable, read-only representations:

```text
rag://sources
rag://sources/{sourceId}
rag://sources/{sourceId}/status
rag://chunks/{chunkId}
rag://capabilities
```

Resources should be used for addressable data. Tools should be used for parameterized searches and operations.

#### Prompts

Optional server prompts can provide reusable retrieval workflows, for example:

- `analyze_code_change`
- `locate_implementation`
- `summarize_subsystem`
- `trace_symbol_usage`

Prompts must not hard-code a specific LLM vendor.

### 5.3.5 Core Search Tool Contract

Example logical input for `rag_search`:

```json
{
  "query": "Where is retry backoff configured?",
  "sourceIds": ["src_01J..."],
  "searchMode": "hybrid",
  "limit": 12,
  "alpha": 0.65,
  "filters": {
    "languages": ["csharp", "json"],
    "pathPrefixes": ["src/", "config/"],
    "excludeGenerated": true,
    "tags": ["production"]
  },
  "context": {
    "includeNeighbors": 1,
    "maxContentCharactersPerResult": 6000
  }
}
```

Example logical result:

```json
{
  "query": "Where is retry backoff configured?",
  "searchMode": "hybrid",
  "embeddingProfile": "nomic-embed-text-v1.5-768",
  "results": [
    {
      "chunkId": "chk_01J...",
      "sourceId": "src_01J...",
      "relativePath": "src/Infrastructure/RetryOptions.cs",
      "language": "csharp",
      "symbol": "RetryOptions",
      "startLine": 8,
      "endLine": 39,
      "score": 0.91,
      "vectorScore": 0.88,
      "lexicalScore": 0.72,
      "content": "...",
      "contentHash": "sha256:...",
      "lastIndexedUtc": "2026-07-18T14:22:31Z"
    }
  ],
  "diagnostics": {
    "candidateCount": 80,
    "elapsedMilliseconds": 43,
    "truncated": false
  }
}
```

### 5.3.6 Vector Operation Rules

A caller-supplied vector is accepted only when:

- The tool is enabled by policy.
- The vector dimension matches the selected embedding profile.
- Every value is finite.
- The payload remains below configured size limits.
- The caller is authorized for every selected source.
- The request does not attempt to override internal collection names.

Direct vector insertion, object mutation, arbitrary GraphQL, and arbitrary Weaviate query passthrough are prohibited in the default MCP surface.

### 5.3.7 MCP Security Boundaries

The MCP adapter must:

1. Resolve caller identity before tool invocation.
2. Intersect requested sources with authorized sources.
3. Apply server-side path and metadata filters.
4. Enforce result-size and execution-time budgets.
5. Remove absolute file paths unless explicitly permitted.
6. Avoid returning secrets detected by configured content policies.
7. Log tool name, caller, source IDs, latency, and result count without logging full source content by default.
8. Reject administrative tools unless a separate role and deployment policy enable them.

### 5.3.8 MCP Server Registration Sketch

The precise API may vary by SDK version, but the intended host composition is:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRagApplicationServices(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<RagSearchTools>()
    .WithResources<RagResources>();

builder.Services
    .AddAuthentication()
    .AddScheme<LocalTokenOptions, LocalTokenHandler>(
        "LocalToken",
        _ => { });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapGroup("/api/v1")
   .MapRagRestEndpoints()
   .RequireAuthorization();

app.MapMcp("/mcp")
   .RequireAuthorization();

await app.RunAsync();
```

MCP tool classes should be thin adapters over `IRagSearchService`, `ISourceQueryService`, and `IIndexAdministrationService`.

### 5.3.9 Optional Local Management Surface

ADR-003 adds an opt-in `/management/mcp` adapter without changing the default `/mcp` capability set. The host filters each MCP session to either the read-only retrieval tools or the management tools and protects the management route with a distinct bearer-token policy. `rag_index`, `rag_remove_index`, and `rag_reset` delegate to shared application services; plugin code never accesses SQLite, source files, or Weaviate directly.

Remove and reset require short-lived, one-use confirmation challenges bound to the action, target, and management principal. Reset also requires a loopback Weaviate endpoint and an installation ownership marker on the configured collection. A host-wide maintenance coordinator blocks new mutations, cancels and drains active indexing work, and keeps readiness unhealthy while an external `reset-state.json` marker records incomplete recovery. The reset scope is limited to `localrag.db` plus its WAL/SHM sidecars and the ownership-verified collection. Source folders, model assets, configuration, and unrelated files or collections are outside that boundary.

---

## 5.4 Source Registry and State Store

**Recommended technology:** SQLite.

JSON configuration is acceptable for a prototype, but SQLite is recommended for concurrency, migrations, checkpoints, and auditability.

### Core Tables

- `Sources`
- `SourceSettings`
- `Files`
- `Chunks`
- `IndexJobs`
- `IndexCheckpoints`
- `EmbeddingProfiles`
- `SchemaVersions`
- `DeadLetterJobs`
- `ApiClients`
- `AuditEvents`

### Source Record

```text
SourceId
CanonicalRootPath
DisplayName
Status
CreatedUtc
UpdatedUtc
LastScanUtc
LastSuccessfulIndexUtc
ConfigurationVersion
EmbeddingProfileId
ChunkingProfileId
IgnoreProfileId
ContentNamespace
```

The canonical root path is internal. External APIs should prefer a source ID and display name.

---

## 5.5 File Discovery and Watcher Subsystem

### Initial Discovery

- Canonicalize and validate the root.
- Prevent duplicate or overlapping registrations unless explicitly allowed.
- Walk the directory tree iteratively.
- Apply ignore rules before opening files.
- Avoid following symbolic links by default.
- Record file identity, relative path, size, timestamps, and content hash.
- Queue eligible files in bounded batches.

### Change Detection

`FileSystemWatcher` is treated as an event hint, not a complete source of truth.

The system must combine:

- Recursive `FileSystemWatcher`.
- Debounce/coalescing.
- Periodic reconciliation scans.
- Startup reconciliation.
- Overflow recovery.
- Rename normalization.
- File identity and content-hash comparison.

### Stability Check

Before reading a changed file:

1. Wait for the debounce interval.
2. Capture size and last-write time.
3. Attempt a safe read/open.
4. Wait for the configured stability interval.
5. Recheck size and last-write time.
6. Process only if stable.
7. Retry with bounded exponential backoff.
8. Move persistently failing items to a dead-letter state.

### Default Ignore Rules

- `.git/`
- `.svn/`
- `.hg/`
- `node_modules/`
- `bin/`
- `obj/`
- `.vs/`
- `.idea/`
- build outputs
- dependency caches
- temporary and lock files
- minified bundles
- generated code, when configured
- files exceeding configured limits
- binary files without a registered extractor

Rules should support `.gitignore` semantics plus product-specific overrides.

---

## 5.6 Content Extraction and Normalization

### Supported MVP Content

- Source code.
- Markdown.
- Plain text.
- JSON, YAML, TOML, XML.
- Project and solution metadata.
- Selected configuration files.
- Shell scripts.
- SQL.

### Normalization

- Preserve line numbering.
- Normalize line endings for hashing and chunking.
- Detect encoding.
- Remove invalid control characters.
- Preserve language-significant whitespace.
- Retain a bounded amount of surrounding metadata.
- Avoid altering source content returned to users.

### Binary and Document Extraction

Future extractors may support PDF, DOCX, notebooks, and images. Extracted text must be associated with:

- Original file path.
- Extractor name and version.
- Page, cell, or section locator.
- Extraction warnings.
- Source checksum.

---

## 5.7 Chunking Engine

### Chunking Strategy

Use a layered strategy:

1. Language-aware structural parsing.
2. Symbol-level chunks for classes, methods, functions, interfaces, and modules.
3. Logical-section chunks for documentation and configuration.
4. Recursive text splitting as a fallback.
5. Token-window enforcement.
6. Optional parent-child chunk relationships.

The accepted Phase 2 boundary is defined by [standalone ADR-001](../02-ADR/ADR-001-language-aware-structural-chunking.md). The host uses one composite selector with bounded in-process adapters for C#, TypeScript/JavaScript, Python, Markdown, JSON, YAML, TOML, and XML-family files. Unsupported or malformed content falls back atomically to the generic line-preserving chunker. Structural and generic chunks share the production BERT WordPiece tokenizer for the final hard-limit proof.

### Recommended Chunk Metadata

```text
ChunkId
SourceId
FileId
RelativePath
Language
ChunkKind
SymbolName
QualifiedSymbolName
StructuralLocator
StartLine
EndLine
ParentChunkId
Ordinal
Content
ContentHash
FileContentHash
TokenCount
ChunkerId
ChunkerVersion
ChunkProfileFingerprint
EmbeddingProfileId
LastModifiedUtc
LastIndexedUtc
Tags
```

### Stable Chunk Identity

Chunk IDs should be deterministic where practical:

```text
SHA-256(
  sourceId +
  normalizedRelativePath +
  structuralLocator +
  normalizedChunkContent +
  chunkerId +
  chunkerVersion +
  chunkProfileFingerprint
)
```

A separate logical locator should enable updates when content changes while preserving lineage.

### Chunk Sizing

Configurable defaults:

- Target: 384 tokens.
- Maximum chunk setting: 480 tokens.
- Hard embedding-model limit: 512 tokens, including special tokens.
- Overlap: 64 tokens for fallback text chunks.
- Symbol chunks: preserve complete symbols up to the hard maximum.
- Oversized symbols: split into signature, sections, and bounded continuations.

---

## 5.8 Embedding Service

### Embedding Modes

1. **Local ONNX embedding**
   - Maximum privacy and deterministic deployment.
   - Model downloaded or installed explicitly.
   - CPU by default, optional GPU acceleration.

2. **Local model server**
   - Ollama, LM Studio, or another OpenAI-compatible local endpoint.
   - Backend uses a configured embedding adapter.

3. **Weaviate vectorizer module**
   - Weaviate performs vectorization.
   - Simpler application code but tighter schema/module coupling.

4. **Remote embedding provider**
   - Explicit opt-in only.
   - Requires clear disclosure that source content leaves the workstation.

### Embedding Profile

An embedding profile is immutable after use and includes:

```text
ProfileId
Provider
ModelName
ModelRevision
Dimensions
DistanceMetric
Normalization
Tokenizer
MaximumInputTokens
DeploymentEndpoint
CreatedUtc
```

Changing model, dimensions, normalization, or distance metric requires a new profile and re-index.

### Batch and Cache Behavior

- Batch by model token and request limits.
- Cache by normalized content hash plus embedding profile.
- Limit concurrent model invocations.
- Apply retries only to transient failures.
- Track per-batch duration and failure reason.
- Never combine chunks from unauthorized sources in externally observable telemetry.

---

## 5.9 Weaviate Integration

### Collection Model

Recommended collection name:

```text
RagChunk_v1
```

Use a collection alias or application-level schema version to support migrations.

### Properties

- `chunkId`
- `sourceId`
- `fileId`
- `relativePath`
- `fileName`
- `extension`
- `language`
- `chunkKind`
- `symbolName`
- `qualifiedSymbolName`
- `structuralLocator`
- `startLine`
- `endLine`
- `ordinal`
- `content`
- `contentHash`
- `fileContentHash`
- `tokenCount`
- `lastModifiedUtc`
- `lastIndexedUtc`
- `embeddingProfileId`
- `chunkerVersion`
- `chunkerId`
- `chunkProfileFingerprint`
- `tags`
- `isGenerated`
- `isDeleted`

Absolute paths should remain outside Weaviate unless there is a compelling local-only requirement.

### Search Modes

1. **Lexical**
   - BM25.
   - Useful for exact symbols, error codes, identifiers, and configuration names.

2. **Vector**
   - Semantic similarity using the configured distance metric.

3. **Hybrid**
   - Combines BM25 and vector relevance.
   - Default for natural-language project search.

4. **Similarity by object**
   - Uses an existing chunk vector to find related chunks.

### Ranking Pipeline

Recommended stages:

1. Validate query and scope.
2. Generate query embedding when required.
3. Retrieve a wider candidate set.
4. Apply source and metadata filters.
5. Blend lexical and vector scores.
6. Deduplicate near-identical chunks.
7. Diversify by file or symbol when configured.
8. Optionally rerank locally.
9. Expand adjacent context.
10. Enforce output character and token budgets.

### Write Semantics

- Upsert in batches.
- Use deterministic chunk IDs.
- Commit SQLite state only after successful vector upsert.
- Remove stale chunks after the replacement batch succeeds.
- Use tombstones or a job generation identifier during large re-indexes.
- Retry transient database failures.
- Send permanent failures to a dead-letter queue.
- Persist one active/pending chunk-profile fingerprint per source. A mismatch creates durable forced reindex work that bypasses unchanged-file shortcuts.
- Keep a transitioning, interrupted, or failed source out of all search and chunk-retrieval surfaces until stale-vector deletion succeeds and SQLite atomically promotes the pending fingerprint.

---

## 6. Processing Pipeline

```text
Discover
   │
   ▼
Filter ──► Reject/Ignore Record
   │
   ▼
Stability Check
   │
   ▼
Read + Normalize
   │
   ▼
Parse + Chunk
   │
   ▼
Diff Against Prior Manifest
   │
   ├── Unchanged ──► Update metadata only, if required
   │
   ├── Deleted ────► Delete/tombstone old chunks
   │
   ▼
Embed Changed Chunks
   │
   ▼
Batch Upsert to Weaviate
   │
   ▼
Commit File/Chunk Manifest
   │
   ▼
Publish Progress and Metrics
```

### 6.1 Idempotency

Every indexing job must be safe to retry. Idempotency is achieved through:

- Deterministic source, file, and chunk identities.
- Content hashes.
- Job generation identifiers.
- Upsert semantics.
- Checkpointed manifests.
- Stale-chunk cleanup after successful replacement.

### 6.2 Backpressure

Use bounded channels between pipeline stages. Configurable limits should include:

- Maximum queued files.
- Maximum concurrent file readers.
- Maximum concurrent chunkers.
- Maximum embedding batches.
- Maximum Weaviate write batches.
- Per-source and global concurrency.

Interactive search receives higher scheduling priority than background indexing.

---

## 7. API Design

## 7.1 REST API

Suggested endpoints:

```text
POST   /api/v1/sources
GET    /api/v1/sources
GET    /api/v1/sources/{sourceId}
PATCH  /api/v1/sources/{sourceId}
DELETE /api/v1/sources/{sourceId}

POST   /api/v1/sources/{sourceId}/reindex
POST   /api/v1/sources/{sourceId}/pause
POST   /api/v1/sources/{sourceId}/resume
GET    /api/v1/sources/{sourceId}/status
GET    /api/v1/sources/{sourceId}/stats

POST   /api/v1/search
GET    /api/v1/chunks/{chunkId}
POST   /api/v1/chunks/{chunkId}/neighbors

GET    /health/live
GET    /health/ready
GET    /metrics
```

### API Versioning

- Use a versioned route prefix.
- Version request and response contracts independently of storage schemas.
- Additive changes are preferred.
- MCP tool names remain stable; add new optional fields rather than changing semantics.

---

## 8. Data Flows

## 8.1 Registration Flow

```text
User
 │
 │ Right-click "Mark as RAG Source"
 ▼
VS Code Extension
 │ POST /api/v1/sources
 ▼
Backend
 │ Validate path and policy
 │ Persist source
 │ Start watcher
 │ Queue initial scan
 ▼
Index Coordinator
 │ Scan → chunk → embed → upsert
 ▼
Weaviate + SQLite
 │
 ▼
Extension receives progress/status
```

## 8.2 Incremental Update Flow

```text
File-system event
 │
 ▼
Watcher Normalizer
 │
 ▼
Debounce + Stability Queue
 │
 ▼
File Processor
 │ content hash differs?
 ├─ No ─► Ignore or metadata update
 └─ Yes
      │
      ▼
   Chunk + Diff
      │
      ▼
   Embed changed chunks
      │
      ▼
   Upsert new chunks
      │
      ▼
   Remove stale chunks
      │
      ▼
   Commit manifest
```

## 8.3 MCP Search Flow

```text
MCP Client
 │ tools/call: rag_search
 ▼
ASP.NET Core MCP Endpoint
 │ Authenticate and authorize
 ▼
RagSearchTools Adapter
 │ Validate limits and source scope
 ▼
IRagSearchService
 │ Embed query, if needed
 │ Execute Weaviate hybrid search
 │ Deduplicate and expand context
 ▼
Policy/Redaction Layer
 │ Remove disallowed fields/content
 ▼
Structured MCP Tool Result
```

---

## 9. Security and Privacy

### 9.1 Local Default

- Bind backend and MCP endpoints to loopback.
- Generate a high-entropy local access token.
- Store secrets using the operating system credential store where possible.
- Reject requests with an unrecognized `Host` header when appropriate.
- Require explicit configuration before binding to non-loopback interfaces.

### 9.2 Network-Hosted Mode

When hosted beyond the local workstation:

- Require TLS.
- Use OIDC/OAuth 2.0, mutual TLS, or a trusted reverse proxy identity.
- Authorize access per source.
- Enforce tenant or user namespaces.
- Configure rate limiting, request-body limits, and timeouts.
- Restrict CORS; MCP clients do not require permissive browser CORS by default.
- Use network policies to limit Weaviate access to the backend.
- Place Weaviate on a private network.
- Rotate credentials.
- Audit administrative operations.

### 9.3 Content Protection

- Ignore known secret files by default, including `.env`, private keys, credential stores, and user-configurable patterns.
- Optionally scan chunks for high-confidence secrets before indexing or returning them.
- Keep absolute source paths internal.
- Never expose unrestricted local-file reads through MCP.
- Return only content already present in the authorized index.
- Record the filter policy and extraction version used for each source.

### 9.4 Prompt-Injection Considerations

Indexed files are untrusted content. The system must:

- Label retrieved text as data, not instructions.
- Avoid allowing retrieved text to alter MCP tool authorization.
- Preserve source provenance.
- Provide line ranges and content hashes.
- Limit automatic follow-on operations.
- Keep write and execution tools out of the retrieval MCP server.
- Encourage clients to distinguish repository instructions from system or user instructions.

---

## 10. Reliability and Recovery

### Failure Classes

| Failure | Required Behavior |
|---|---|
| Backend restart | Reload sources, reconcile watchers, resume pending jobs |
| Watcher overflow | Mark source dirty and run reconciliation scan |
| Weaviate unavailable | Pause writes, retry with backoff, retain jobs |
| Embedding model unavailable | Queue work and expose degraded status |
| File locked | Retry after stability delay |
| File deleted during read | Treat as delete candidate after reconciliation |
| Schema mismatch | Fail readiness and require migration |
| Corrupt state database | Stop writes, preserve files, provide recovery command |
| MCP request timeout | Cancel downstream query and return structured error |
| Oversized MCP request | Reject before vector or database processing |

### Graceful Shutdown

The host should:

1. Stop accepting new indexing jobs.
2. Cancel or drain active requests within a time budget.
3. Flush completed manifests.
4. Dispose watchers.
5. Close SQLite connections.
6. Preserve incomplete jobs for restart.

---

## 11. Observability

### Logging

Use structured logs with:

- Correlation ID.
- Source ID.
- File ID or relative path hash.
- Index job ID.
- MCP request/tool name.
- Search mode.
- Duration.
- Retry count.
- Error category.

Do not log full chunk content by default.

### Metrics

Recommended metrics:

- Files discovered, indexed, skipped, failed, and deleted.
- Chunks created, updated, and removed.
- Index queue depth.
- Embedding batch latency and throughput.
- Weaviate upsert latency.
- Search latency by mode.
- MCP tool calls by name and status.
- Result counts and truncation counts.
- Watcher overflow count.
- Reconciliation duration.
- CPU, memory, and model utilization.

### Health Checks

- **Liveness:** process and event loop operational.
- **Readiness:** state store available, schema compatible, Weaviate reachable, required embedding profile available.
- **Degraded:** searches available but indexing paused, or lexical search available while embedding service is unavailable.

---

## 12. Performance Targets

Initial targets for a modern developer workstation:

- Search p95 below 300 ms for typical filtered queries, excluding first model load.
- MCP adapter overhead below 25 ms beyond the core search service.
- Incremental detection-to-index p95 below 10 seconds after file stability.
- Initial indexing throughput of at least 10–30 text files per second for moderate files, hardware and embedding model dependent.
- Bounded memory use during multi-gigabyte scans.
- Search remains responsive during indexing.
- Reconciliation scans avoid re-embedding unchanged content.

These are engineering targets, not contractual guarantees, and should be validated with representative repositories.

---

## 13. Configuration Model

### Global Configuration

```yaml
server:
  bindAddress: 127.0.0.1
  port: 5198
  requireAuthentication: true

mcp:
  enabled: true
  path: /mcp
  transport: streamableHttp
  allowAdministrativeTools: false
  maxResults: 50
  maxRequestBytes: 1048576
  maxResponseCharacters: 200000
  requestTimeoutSeconds: 30

management:
  enabled: false
  path: /management/mcp
  tokenEnvironmentVariable: LOCALRAG_MANAGEMENT_TOKEN
  confirmationLifetimeSeconds: 120
  maintenanceDrainTimeoutSeconds: 30

indexing:
  debounceMilliseconds: 5000
  stabilityIntervalMilliseconds: 1500
  reconciliationIntervalMinutes: 30
  maxConcurrentFiles: 4
  maxFileBytes: 5242880

chunking:
  targetTokens: 800
  maximumTokens: 1500
  overlapTokens: 120

embedding:
  profile: local-onnx-default

weaviate:
  endpoint: http://127.0.0.1:8080
  collection: RagChunk_v1
  batchSize: 100

privacy:
  exposeAbsolutePaths: false
  secretScanning: true
```

### Per-Source Overrides

- Include/exclude patterns.
- File-size limit.
- Chunking profile.
- Embedding profile.
- Generated-code policy.
- Git-tracked-files-only mode.
- Tags.
- Search visibility.
- Reconciliation frequency.

---

## 14. Deployment

## 14.1 Docker Compose for Weaviate

Illustrative local configuration:

```yaml
services:
  weaviate:
    image: cr.weaviate.io/semitechnologies/weaviate:<pinned-version>
    restart: unless-stopped
    ports:
      - "127.0.0.1:8080:8080"
    environment:
      QUERY_DEFAULTS_LIMIT: "25"
      AUTHENTICATION_ANONYMOUS_ACCESS_ENABLED: "true"
      PERSISTENCE_DATA_PATH: "/var/lib/weaviate"
      DEFAULT_VECTORIZER_MODULE: "none"
      ENABLE_MODULES: ""
      CLUSTER_HOSTNAME: "local-rag-node"
    volumes:
      - weaviate_data:/var/lib/weaviate

volumes:
  weaviate_data:
```

Production and shared-host configurations must disable anonymous access and use supported authentication controls. Pin the image version rather than using `latest`.

## 14.2 Backend Packaging

Recommended artifacts:

- Self-contained .NET executables for Windows, macOS, and Linux.
- Framework-dependent package for advanced users.
- Service installation scripts.
- VS Code extension package.
- Versioned configuration migration tool.
- Optional `stdio` MCP launcher that connects to the same application layer or local HTTP service.

---

## 15. Suggested Solution Structure

```text
LocalRag.sln

src/
  LocalRag.Host/
    Program.cs
    Rest/
    Mcp/
    Health/
    Authentication/

  LocalRag.Application/
    Sources/
    Search/
    Indexing/
    Policies/
    Abstractions/

  LocalRag.Domain/
    Sources/
    Files/
    Chunks/
    Jobs/
    Embeddings/

  LocalRag.Infrastructure.FileSystem/
    Discovery/
    Watchers/
    Stability/
    IgnoreRules/

  LocalRag.Infrastructure.Chunking/
    Parsers/
    Chunkers/
    Tokenization/

  LocalRag.Infrastructure.Embeddings/
    Onnx/
    OpenAiCompatible/
    WeaviateModules/

  LocalRag.Infrastructure.Weaviate/
    Schema/
    Search/
    Upserts/
    Migrations/

  LocalRag.Infrastructure.Sqlite/
    Entities/
    Migrations/
    Repositories/

  LocalRag.Contracts/
    Rest/
    Mcp/
    Events/

  LocalRag.Cli/

vscode-extension/
  src/
    commands/
    api/
    decorations/
    views/
    backend/
  package.json

tests/
  LocalRag.UnitTests/
  LocalRag.IntegrationTests/
  LocalRag.EndToEndTests/
  LocalRag.LoadTests/

deploy/
  docker-compose.yml
  systemd/
  windows-service/
```

---

## 16. Testing Strategy

### Unit Tests

- Ignore-rule evaluation.
- Path normalization.
- Stable chunk identity.
- Chunk-boundary behavior.
- File-event coalescing.
- Search-filter validation.
- MCP tool argument validation.
- Authorization scope intersection.
- Score blending and deduplication.

### Integration Tests

- SQLite migrations.
- Weaviate schema creation and upgrades.
- Batch upsert and stale-chunk deletion.
- Local embedding adapter.
- ASP.NET Core REST and MCP endpoints.
- Cancellation and timeout propagation.
- Service restart and job recovery.

### End-to-End Tests

1. Register a fixture repository.
2. Complete initial indexing.
3. Query by REST and MCP and compare results.
4. Change, rename, and delete files.
5. Verify incremental consistency.
6. Simulate locked and partially copied files.
7. Restart the backend.
8. Restart Weaviate.
9. Trigger watcher overflow or force reconciliation.
10. Validate source-level access controls.

### Load Tests

- Multi-gigabyte monorepository.
- Large numbers of small files.
- Large generated trees excluded by rules.
- Concurrent MCP searches during full indexing.
- High-frequency save bursts.
- Multiple registered roots.
- Vector requests near configured dimensions and payload limits.

---

## 17. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| File watchers miss or duplicate events | Treat events as hints; use manifests and reconciliation |
| Chunk quality varies by language | Plug-in chunkers, language parsers, evaluation corpus |
| Embedding model changes invalidate vectors | Immutable embedding profiles and explicit re-index |
| Large repositories saturate CPU or memory | Bounded queues, priorities, concurrency controls |
| Weaviate schema evolves | Versioned collections, aliases, migration tooling |
| MCP exposes excessive repository content | Source-scoped authorization, output budgets, path redaction |
| MCP clients send malformed vectors | Dimension, numeric, size, and policy validation |
| Local endpoint is accessed by another process | High-entropy token, loopback binding, OS credential storage |
| Remote mode becomes multi-tenant accidentally | Separate deployment profile and mandatory identity controls |
| Secret files enter the index | Default excludes, optional secret scanner, audit command |
| Search differs across REST and MCP | Shared `IRagSearchService` and contract tests |
| SDK or protocol evolves | Pin dependencies, isolate MCP adapter, compatibility tests |

---

## 18. Delivery Phases

## Phase 1: Local MVP

- ASP.NET Core backend.
- SQLite source registry.
- Weaviate Docker deployment.
- Folder registration and removal.
- Initial scan and incremental watcher.
- Generic swe/text chunker.
- One local embedding profile.
- Batch upsert and hybrid search.
- Basic VS Code commands and status.
- Hosted read-only MCP tools:
  - `rag_search`
  - `rag_get_chunk`
  - `rag_list_sources`
  - `rag_get_source_status`

## Phase 2: Retrieval Quality and Operational Hardening

- Language-aware chunkers.
- Reconciliation scans and watcher-overflow recovery.
- Neighbor expansion.
- Query diversification and optional reranking.
- Rich VS Code search UI.
- Metrics and diagnostics dashboard.
- Additional MCP tools and resources.
- Source-level authorization model.
- Cross-platform service packaging.

## Phase 3: Advanced Repository Intelligence

- Compiler- or Tree-sitter-backed symbol extraction.
- Git-aware metadata and branch/commit filters.
- Parent-child retrieval.
- Relationship and dependency indexing.
- Local LLM answer synthesis.
- Notebook, PDF, and office-document extractors.
- Optional image and multimodal embeddings.
- Team-hosted deployment mode.
- Index export/import and encrypted backup.

---

## 19. Architecture Decisions

### ADR-001: ASP.NET Core Hosts Both REST and MCP

**Decision:** Use one ASP.NET Core process for REST, MCP, health endpoints, and indexing workers.

**Rationale:** Shared dependency injection, authentication, policy, lifecycle, and application services reduce operational and semantic divergence.

### ADR-002: MCP Is an Adapter, Not the Domain Layer

**Decision:** MCP tool implementations call application interfaces and contain no direct Weaviate queries.

**Rationale:** This maintains consistent retrieval behavior, simplifies testing, and prevents protocol-specific authorization bypasses.

### ADR-003: Read-Only MCP by Default

**Decision:** Only search and inspection tools are enabled in the default MCP profile.

**Rationale:** Retrieval is the primary requirement. Index mutation creates substantially greater security and operational risk.

### ADR-004: Streamable HTTP for Hosted MCP

**Decision:** Use Streamable HTTP for networked or local hosted MCP access.

**Rationale:** It is the recommended remote transport in the official C# SDK documentation and supports standard ASP.NET Core hosting.

### ADR-005: SQLite Holds Operational Truth

**Decision:** SQLite stores source definitions, manifests, jobs, and checkpoints; Weaviate stores searchable chunk objects and vectors.

**Rationale:** The vector store should not be the sole source of truth for file synchronization and job recovery.

### ADR-006: Watchers Require Reconciliation

**Decision:** Combine file-system events with periodic scans.

**Rationale:** Cross-platform watcher behavior is not sufficiently reliable for strict index correctness.

### Phase 2 Decision Record: Language-Aware Structural Chunking

**Decision:** [Standalone ADR-001](../02-ADR/ADR-001-language-aware-structural-chunking.md) governs the approved language corpus, bounded parser strategy, exact tokenizer enforcement, additive provenance, versioned chunk identities, source-level profile cutover, rollback, and paired retrieval evaluation.

**Rationale:** Structural metadata improves symbol/section retrieval, while a durable query-invisible transition prevents old and new chunk semantics from being externally mixed.

### Phase 2 Decision Record: Durable Reconciliation State and Watcher Recovery

**Decision:** [Standalone ADR-002](../02-ADR/ADR-002-durable-reconciliation-state-and-watcher-recovery.md) governs the SQLite generation watermarks, bounded cause coalescing, token-fenced leases, durable retry dispatch, per-source external-mutation gate, removal tombstone, compatibility status projection, and privacy-safe recovery diagnostics used by FEATURE-02.

**Rationale:** Filesystem events are lossy hints. Durable desired/completed generations and one active plus one follow-up generation prove that events arriving during recovery are not lost, while lease and removal fencing prevent stale completion or source resurrection.

---

## 20. Acceptance Criteria for the MVP

The MVP is complete when:

1. A user can register and remove a folder from VS Code.
2. The backend survives restart and restores registered sources.
3. Initial indexing completes for a representative repository.
4. File create, change, rename, and delete operations are reflected in the index.
5. Unchanged files are not re-embedded during reconciliation.
6. Hybrid search returns file, symbol, line range, score, and content.
7. The hosted MCP server is discoverable by a compatible client.
8. MCP clients can list sources and perform source-scoped search.
9. Unauthorized source IDs are rejected or omitted.
10. Direct arbitrary Weaviate access is not exposed through MCP.
11. Search remains usable while background indexing is active.
12. Health endpoints distinguish ready, degraded, and unavailable states.
13. Logs and metrics identify failed indexing jobs without recording full source content.
14. Integration tests run against containerized Weaviate.
15. All default services bind to loopback.

---

## 21. Immediate Next Steps

1. Confirm the MVP embedding model and vector dimensions.
2. Pin supported .NET, MCP SDK, and Weaviate versions.
3. Create the solution skeleton and application interfaces.
4. Define REST and MCP contracts.
5. Implement source registration and SQLite migrations.
6. Implement discovery, filtering, manifests, and bounded indexing jobs.
7. Add generic chunking and the first embedding adapter.
8. Add Weaviate schema management and hybrid search.
9. Implement the four MVP MCP tools.
10. Build the VS Code folder commands and progress display.
11. Establish integration fixtures and retrieval-quality evaluations.
12. Produce installation packages and a local threat model.

---

## 22. Reference Material

- Model Context Protocol C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- MCP C# SDK transport documentation: https://csharp.sdk.modelcontextprotocol.io/concepts/transports/transports.html
- MCP C# SDK getting started: https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html
- Model Context Protocol transport specification: https://modelcontextprotocol.io/specification/2025-03-26/basic/transports
- Weaviate documentation: https://docs.weaviate.io/
- Visual Studio Code Extension API: https://code.visualstudio.com/api
- .NET `FileSystemWatcher`: https://learn.microsoft.com/dotnet/api/system.io.filesystemwatcher
- ONNX Runtime for C#: https://onnxruntime.ai/docs/get-started/with-csharp.html

---

## 23. Conclusion

The proposed system provides a modular, local-first RAG platform for developer content. It combines resilient file synchronization, code-aware chunking, configurable embeddings, hybrid Weaviate retrieval, and a consistent set of application services.

The hosted C# MCP server extends the design from an editor-specific feature into a reusable AI retrieval service. By exposing narrowly scoped, read-only search and vector operations through Streamable HTTP—and by keeping the MCP layer separate from indexing internals—the platform can support multiple AI clients without sacrificing local privacy, source-level authorization, operational reliability, or retrieval consistency.
