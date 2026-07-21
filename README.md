# Local RAG

Supercharge your agentic coding agents!  **Local RAG** **indexes approved local folders** using a local **ONNX** **embedding model** and stores externally generated vectors in a separately deployed **Weaviate** instance.

## Introduction

**Local-First Agentic RAG for Developers**

Built by **Justin Haffey**, this project is a local-first Retrieval-Augmented Generation platform designed for developers who want their coding assistants and autonomous agents to understand real repositories without surrendering source code to a cloud service. Developers can register folders directly from Visual Studio Code, where the system continuously discovers, parses, chunks, embeds, and indexes source files, documentation, configuration, and project metadata. The platform is built on C#/.NET, ASP.NET Core, SQLite, Weaviate, Docker, and local embedding models, giving engineers a practical foundation for semantic, lexical, and hybrid search across everything from small applications to multi-gigabyte monorepos.

The core architecture is intentionally agent-ready: a hosted C# Model Context Protocol (MCP) server exposes structured, read-only tools for source discovery, hybrid search, chunk retrieval, similarity search, context expansion, and index health. AI coding assistants and external agent hosts can retrieve precise, source-scoped context with file paths, symbols, line ranges, scores, hashes, and neighboring chunks—without receiving direct access to the file system or vector database. REST, MCP, VS Code, CLI, and automation clients all share the same application services, ensuring consistent ranking, authorization, filtering, and response contracts across every integration surface.

Engineers can extend the platform through pluggable parsers, language-aware chunkers, embedding providers, vector stores, retrieval policies, and deployment modes. Resilient file watchers, reconciliation scans, deterministic chunk identities, bounded processing queues, embedding caches, incremental updates, and recoverable indexing jobs make the system suitable for serious development workflows—not just demos. With loopback-only defaults, explicit remote opt-in, source-level authorization, secret filtering, path redaction, and read-only MCP capabilities, the project provides a secure and production-minded starting point for building developer tools, coding copilots, repository intelligence systems, and multi-agent engineering workflows.

## Installation

### Components

- **Windows Host App -** Hosts the Synchronization engine, MCP Server and ONNX embedding model runtime.
- **VS Code Extension (VSIX) -** `Right-click → Toggle RAG Source` turns any project folder into an agent-aware, searchable knowledge source without disrupting the developer workflow.
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

The project-level `.codex/config.toml` registers the backend's Streamable HTTP MCP endpoint as `local_rag`. To let Codex and the extension share its bearer token without committing a secret, generate a high-entropy user environment variable once, then restart VS Code:

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

When `LOCALRAG_MCP_TOKEN` is available, the extension uses it to launch the backend. Otherwise, the extension continues to use VS Code Secret Storage, but project-configured MCP clients cannot authenticate automatically.

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

* **VS Code Integration.** `Right-click → Toggle RAG Source` turns any project folder into an agent-aware, searchable knowledge source without disrupting the developer workflow.
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
