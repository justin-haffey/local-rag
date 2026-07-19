# Local RAG MVP

The application indexes approved local folders using a local ONNX embedding model and stores externally generated vectors in a separately deployed Weaviate instance. It never starts, stops, or configures Docker.

## About

[todo: insert a brief marketing overview of the solution. Explain the use case and tought the top benefits]

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
