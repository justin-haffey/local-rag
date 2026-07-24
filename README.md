# Local RAG

**Quickly RAG-ify any project folder.**  Local RAG indexes local folders using a fast local **ONNX** **embedding model** and stores externally generated vectors in a **Weaviate** instance.

## Introduction

Local-First Agentic RAG for Developers

This project is a local-first Retrieval-Augmented Generation platform designed for developers who want their coding assistants and autonomous agents to understand real repositories without surrendering source code to a cloud service. Developers can register folders directly from Visual Studio Code, where the system continuously discovers, parses, chunks, embeds, and indexes source files, documentation, configuration, and project metadata. The platform is built on C#/.NET, ASP.NET Core, SQLite, Weaviate, Docker, and local embedding models, giving engineers a practical foundation for semantic, lexical, and hybrid search across everything from small applications to multi-gigabyte monorepos.

The core architecture is intentionally agent-ready: a hosted C# Model Context Protocol (MCP) server exposes structured, read-only tools for source discovery, hybrid search, chunk retrieval, similarity search, context expansion, and index health. AI coding assistants and external agent hosts can retrieve precise, source-scoped context with file paths, symbols, line ranges, scores, hashes, and neighboring chunks—without receiving direct access to the file system or vector database. REST, MCP, VS Code, CLI, and automation clients all share the same application services, ensuring consistent ranking, authorization, filtering, and response contracts across every integration surface.

Engineers can extend the platform through pluggable parsers, language-aware chunkers, embedding providers, vector stores, retrieval policies, and deployment modes. Resilient file watchers, reconciliation scans, deterministic chunk identities, bounded processing queues, embedding caches, incremental updates, and recoverable indexing jobs make the system suitable for serious development workflows—not just demos. With loopback-only defaults, explicit remote opt-in, source-level authorization, secret filtering, path redaction, and read-only MCP capabilities, the project provides a secure and production-minded starting point for building developer tools, coding copilots, repository intelligence systems, and multi-agent engineering workflows.

## Installation

### Components

- **Windows Host App -** Hosts the Synchronization engine, MCP Server and ONNX embedding model runtime.
- **VS Code Extension (VSIX) -** `Right-click → Local RAG` provides folder-scoped index, toggle, refresh, and status actions, while the **Local RAG Search** Explorer view provides safe in-editor retrieval.
- **Weaviate -** Stored chunked vectors in a local Weaviate database (Docker hosted container or Weaviate cloud).

### Prerequisites

- .NET SDK 10.0.302
- Node.js 22+ for the VS Code extension
- A local Weaviate endpoint at `http://127.0.0.1:8080`, bound to loopback, with no vectorizer module
- BGE Small English v1.5 ONNX assets in `%LOCALAPPDATA%\LocalRag\models\bge-small-en-v1.5`: `model.onnx` and `vocab.txt`

Provision the model explicitly; the bootstrap downloads the pinned official assets and verifies their SHA-256 hashes before installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-LocalRagEmbeddingModel.ps1
```

### Configuration

Defaults are in [`src/LocalRag.Host/appsettings.json`](src/LocalRag.Host/appsettings.json). Copy `appsettings.Local.json.example` to `appsettings.Local.json` for local overrides, or use environment variables such as `LocalRag__Weaviate__Endpoint` and `LocalRag__Weaviate__ApiKey`.

Structural chunking is enabled by default for C#, TypeScript/JavaScript, Python, Markdown, JSON, YAML, TOML, and the XML family (`.xml`, `.csproj`, `.props`, and `.targets`). Unsupported or malformed files use the mandatory generic line-preserving fallback. Every chunk records its kind, symbol/qualified symbol when available, structural locator, chunker ID/version, profile fingerprint, and exact line range. Both structural and generic chunks use the configured BERT WordPiece tokenizer as the final model-limit check.

Changing adapter enablement, token settings, embedding/tokenizer identity, or a chunker version changes the canonical chunk-profile fingerprint. The next reconciliation performs a forced full-source reindex and keeps that source out of search and chunk retrieval until all new chunks and stale-vector deletions succeed. Restart and retry resume the durable transition; failure leaves the source degraded and query-invisible. To roll back to generic chunking, set `LocalRag__Chunking__EnabledAdapters` to an empty array in local configuration, restart the host, and reindex the source. Restore the approved adapter list to roll forward again.

The backend requires `LocalRag__Authentication__Token`; the extension creates and retains this in VS Code Secret Storage. Start manually only when supplying a token explicitly.

### Automatic reconciliation and recovery

Filesystem events are treated as hints. Each registered source also has durable, generation-based reconciliation state in SQLite, so watcher overflow, missed events, dependency failures, and host restarts can converge without discarding the existing index. Work is single-flight per source and bounded globally; a hint received during an active scan becomes at most one follow-up generation. Content already committed with the same hash is not re-embedded.

The following `LocalRag:Indexing` settings are validated when the host starts:

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `ReconciliationIntervalMinutes` | 30 | 1-1440 | Periodic safety scan interval |
| `ReconciliationLeaseDurationSeconds` | 120 | 30-3600 | Expiry window for durable in-flight work |
| `ReconciliationLeaseRenewalSeconds` | 30 | 5-1200 and less than the lease duration | Renewal cadence for a running scan |
| `MaxConcurrentReconciliations` | 2 | 1-32 | Maximum sources reconciled at once |
| `ReconciliationDispatchPollSeconds` | 5 | 1-60 | Maximum idle delay before durable due work is checked |
| `ReconciliationHistoryLimit` | 20 | 1-100 | Successful/cancelled generations retained per source after a later checkpoint |

Environment-variable overrides use the normal double-underscore form, for example `LocalRag__Indexing__MaxConcurrentReconciliations=2`. Increase concurrency only when local ONNX, disk, and Weaviate capacity can sustain it. Keep the renewal interval comfortably below the lease duration; startup validation rejects unsafe combinations.

Authenticated REST source responses and the `rag_list_sources` / `rag_get_source_status` MCP tools include an additive `recovery` object. `Clean` means the completed generation has caught up, `Queued` means durable work is waiting or backing off, `Running` is displayed as **Recovering** by the VS Code extension, and `Degraded` means automatic recovery needs attention. Status includes bounded causes, generation watermarks, safe failure codes/summaries, timestamps, and changed/deleted/unchanged counts. It does not expose source roots, relative paths, file content, lease identifiers, stack traces, or raw exception messages.

For a degraded source, first verify that its registered folder, Weaviate endpoint, and local model assets are available. Then select the Local RAG status item in VS Code and choose **Queue recovery**, or call `POST /api/v1/sources/{sourceId}/reindex`. Do not edit the SQLite recovery tables or delete vector data manually. A restart preserves queued/running obligations and recovers expired leases; only one active host may use a given Local RAG data directory.

The authenticated `/metrics` endpoint reports bounded request causes, watcher overflows, runs/outcomes/retries, recovered leases, durations, result counts, and current dirty/degraded gauges without source-, path-, content-, error-, or lease-labelled dimensions.

The `local-rag-skills` Codex plugin bundles the backend's Streamable HTTP MCP endpoint as `local_rag`. To let Codex and the extension share its bearer token without committing a secret, generate a high-entropy user environment variable once, then restart VS Code:

```powershell
$tokenBytes = [byte[]]::new(32)
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($tokenBytes)
} finally {
    $random.Dispose()
}
$token = [Convert]::ToBase64String($tokenBytes)
[Environment]::SetEnvironmentVariable('LOCALRAG_MCP_TOKEN', $token, 'User')
```

When `LOCALRAG_MCP_TOKEN` is available, the extension uses it to launch the backend and the plugin-provided MCP client uses it for bearer authentication. Otherwise, the extension continues to use VS Code Secret Storage, but the plugin-provided MCP client cannot authenticate automatically.

The separate `local-rag-mgmt` plugin provides explicitly invoked `rag-index`, `rag-remove-index`, and `rag-reset` skills through `/management/mcp`. Management is disabled by default. To enable it, configure `LocalRag__Management__Enabled=true` and set a high-entropy `LocalRag__Management__Token` that is different from the standard token; expose the same value to the plugin as `LOCALRAG_MANAGEMENT_TOKEN`.

Removal and reset use a two-call confirmation flow. Reset irreversibly recreates only the configured Local RAG SQLite database and the ownership-verified, loopback Weaviate collection; source folders, models, configuration, and unrelated collections/files are preserved. If reset fails after destructive work begins, `/health/ready` remains unhealthy and `reset-state.json` remains in the configured data directory. Correct the dependency problem and invoke `rag-reset` again, including a new explicit confirmation. Do not delete the recovery marker or retry against a shared, remote, or ownership-unverified Weaviate instance.

### Build and Run

```powershell
dotnet restore LocalRag.sln
dotnet test LocalRag.sln
pwsh ./scripts/Publish-Backend.ps1
Set-Location vscode-extension
npm install
npm run compile
```

### Package and Install the VS Code Extension

From the repository root, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1
```

The script restores extension dependencies, publishes the Windows x64 backend, compiles and validates the extension, creates `artifacts\local-rag-0.1.0.vsix`, installs it with the VS Code CLI, and verifies `starlinx-llc.local-rag@0.1.0`. Use `-SkipInstall` to create the VSIX without installing it.

To require real local inference during test execution, set `LOCALRAG_ONNX_TESTS=1`. To run live Weaviate integration tests, set `WEAVIATE_TEST_ENDPOINT=http://127.0.0.1:8080`. FEATURE-01's paired 32-query retrieval evaluation additionally uses `LOCALRAG_STRUCTURAL_EVAL=1` and can write its machine-readable evidence to `LOCALRAG_EVALUATION_REPORT`.

The extension manages only the packaged backend process. If Weaviate or model assets are unavailable, `/health/ready` becomes degraded and the extension provides an actionable error.

## Key Features

### Developer Experience

* **VS Code Integration.** `Right-click → Local RAG` provides **Index Folder**, **Toggle Indexing**, **Refresh Index**, and **Source Status** for the selected folder. Toggle removal requires confirmation and never targets another folder.
* **In-Editor Search.** Open **Local RAG Search** in the Explorer, run **Local RAG: Search**, choose sources plus Hybrid/Lexical/Vector mode and optional language/path filters, then open a result at its line or copy bounded context with provenance. Navigation stays within the matching real workspace path.
* **Multiple Client Surfaces.** Access retrieval and indexing capabilities through VS Code, REST APIs, MCP-compatible agents, command-line tools, and automation clients.
* **Shared Retrieval Core.** Every integration uses the same application services, authorization policies, filters, ranking pipeline, and response contracts.

### Search

* **Semantic, Lexical, and Hybrid Search.** Combine vector similarity with BM25 keyword matching to locate concepts, symbols, configuration values, error codes, and implementation details.
* **Code-Aware Chunking.** Language-aware parsers preserve classes, methods, functions, interfaces, documentation sections, configuration blocks, symbols, and line ranges as meaningful retrieval units.
* **Context Expansion.** Retrieve neighboring chunks around a result to give agents the surrounding implementation context they need.

### Agents

* **Codex Plugin.** Wraps MCP tools exposing basic search and advanced repo mapping functionality through codex skills.
* **Agent-Ready MCP Server.** A hosted C# Model Context Protocol server exposes structured, read-only tools for repository search, chunk retrieval, similarity discovery, context expansion, and index inspection.
* **Structured Results.** Agents receive source-scoped results with file paths, symbols, line ranges, relevance scores, content hashes, and indexing metadata.
* **Read-Only by Default.** MCP clients can search and inspect indexed content without receiving unrestricted file-system, database, or repository write access.

### Indexing

* **Continuous Incremental Indexing.** File-system monitoring, content hashing, debounce controls, and reconciliation scans keep the index synchronized as files are created, modified, renamed, or deleted.
* **Microsoft Word Indexing.** Modern `.docx` files are extracted locally from bounded Open XML parts, including body text, tables, headers, footers, notes, and comments, without executing macros or following external relationships.
* **PDF Indexing.** Native `.pdf` text is extracted locally in page reading order, while scanned pages use bundled English Tesseract OCR. Page, extracted-text, OCR-page, DPI, and rendered-pixel limits bound processing.
* **Efficient Reprocessing.** Unchanged files and chunks are not re-embedded unnecessarily, reducing indexing time and compute usage.
* **Repository Scale.** Bounded processing queues, batch operations, retry policies, and recoverable jobs support large repositories and multi-gigabyte monorepos.
* **Language Aware Structural Chunking.** Produces higher-quality searchable chunks that preserve meaningful swe/configuration boundaries and provenance, while remaining deterministic, bounded, and safe when parsing is unsupported or fails.  Extensible, but already supports most major programming languages and common file formats.

### Models

* **Local Embeddings.** Uses local ONNX models or local model servers such as Ollama and LM Studio to keep source content on the developer workstation.
* **Pluggable Providers.** Support Weaviate vectorizers and explicitly configured remote embedding services when local execution is not required.
* **Versioned Profiles.** Embedding models, vector dimensions, normalization settings, and distance metrics are tracked through immutable embedding profiles.

### Security

* **Local-First by Design.** Source code, embeddings, metadata, and search results remain local unless a remote provider or shared deployment is explicitly configured.
* **Source-Level Authorization.** Search requests are restricted to the folders and repositories each client is permitted to access.
* **Content Protection.** Secret filtering, path redaction, request limits, loopback-only defaults, and narrowly scoped tools reduce accidental exposure.

### Reliability

* **Resilient Synchronization.** File watchers are backed by startup and periodic reconciliation scans to detect missed, duplicated, or overflowed file events.
* **Recoverable Processing.** Deterministic chunk identities, checkpoints, stale-chunk cleanup, retry policies, and dead-letter handling make indexing safe to resume.
* **Operational Visibility.** Health endpoints, structured logs, metrics, source status, and indexing diagnostics expose the state of the system.

### Extensibility

* **Modular Architecture.** Add file types, parsers, chunking strategies, embedding models, vector stores, rerankers, and retrieval policies through well-defined interfaces.
* **One Core, Multiple Adapters.** REST, MCP, VS Code, CLI, and automation clients remain thin adapters over the same application layer.
* **Flexible Deployment.** Run the backend as a VS Code-managed process, local service, container, or controlled shared environment.
