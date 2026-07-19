# Local RAG

Supercharge your agentic coding agents!  **Local RAG** **indexes approved local folders** using a local **ONNX** **embedding model** and stores externally generated vectors in a separately deployed **Weaviate** instance. It never starts, stops, or configures Docker.

## Local-First Agentic RAG for Developers

Built by **Justin Haffey**, an independent agentic engineer, this project is a local-first Retrieval-Augmented Generation platform designed for developers who want their coding assistants and autonomous agents to understand real repositories without surrendering source code to a cloud service. Developers can register folders directly from Visual Studio Code, where the system continuously discovers, parses, chunks, embeds, and indexes source files, documentation, configuration, and project metadata. The platform is built on C#/.NET, ASP.NET Core, SQLite, Weaviate, Docker, and local embedding models, giving engineers a practical foundation for semantic, lexical, and hybrid search across everything from small applications to multi-gigabyte monorepos.

The core architecture is intentionally agent-ready: a hosted C# Model Context Protocol (MCP) server exposes structured, read-only tools for source discovery, hybrid search, chunk retrieval, similarity search, context expansion, and index health. AI coding assistants and external agent hosts can retrieve precise, source-scoped context with file paths, symbols, line ranges, scores, hashes, and neighboring chunks—without receiving direct access to the file system or vector database. REST, MCP, VS Code, CLI, and automation clients all share the same application services, ensuring consistent ranking, authorization, filtering, and response contracts across every integration surface.

Engineers can extend the platform through pluggable parsers, language-aware chunkers, embedding providers, vector stores, retrieval policies, and deployment modes. Resilient file watchers, reconciliation scans, deterministic chunk identities, bounded processing queues, embedding caches, incremental updates, and recoverable indexing jobs make the system suitable for serious development workflows—not just demos. With loopback-only defaults, explicit remote opt-in, source-level authorization, secret filtering, path redaction, and read-only MCP capabilities, the project provides a secure and production-minded starting point for building developer tools, coding copilots, repository intelligence systems, and multi-agent engineering workflows.

## Components

* **Windows Host App -** Hosts the MCP Server and the ONNX embedding model runtime
* **VSIX - VS Code Extension -** `Right click -> Mark as RAG Source` any project folder to index code and create a knowledge base.

## Prerequisites

- .NET SDK 10.0.302
- Node.js 22+ for the VS Code extension
- A local Weaviate endpoint at `http://127.0.0.1:8080`, bound to loopback, with no vectorizer module
- BGE Small English v1.5 ONNX assets in `%LOCALAPPDATA%\LocalRag\models\bge-small-en-v1.5`: `model.onnx` and `vocab.txt`

Provision the model explicitly; the bootstrap downloads the pinned official assets and verifies their SHA-256 hashes before installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-LocalRagEmbeddingModel.ps1
```

## Configuration

Defaults are in [`src/LocalRag.Host/appsettings.json`](src/LocalRag.Host/appsettings.json). Copy `appsettings.Local.json.example` to `appsettings.Local.json` for local overrides, or use environment variables such as `LocalRag__Weaviate__Endpoint` and `LocalRag__Weaviate__ApiKey`.

The backend requires `LocalRag__Authentication__Token`; the extension creates and retains this in VS Code Secret Storage. Start manually only when supplying a token explicitly.

## Build and Run

```powershell
dotnet restore LocalRag.sln
dotnet test LocalRag.sln
pwsh ./scripts/Publish-Backend.ps1
Set-Location vscode-extension
npm install
npm run compile
```

## Package and Install the VS Code Extension

From the repository root, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1
```

The script restores extension dependencies, publishes the Windows x64 backend, compiles and validates the extension, creates `artifacts\local-rag-0.1.0.vsix`, installs it with the VS Code CLI, and verifies `starlinx-llc.local-rag@0.1.0`. Use `-SkipInstall` to create the VSIX without installing it.

To require real local inference during test execution, set `LOCALRAG_ONNX_TESTS=1`. To run live Weaviate integration tests, set `WEAVIATE_TEST_ENDPOINT=http://127.0.0.1:8080`.

The extension manages only the packaged backend process. If Weaviate or model assets are unavailable, `/health/ready` becomes degraded and the extension provides an actionable error.
