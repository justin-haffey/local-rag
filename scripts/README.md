# LocalRAG build and installation scripts

This directory contains the scripts used to publish the Windows host, package the VS Code extension, provision the local embedding model, and build the Windows installer.

The product name is **LocalRAG** and the host executable/project is **`LocalRag.Host`**. Weaviate is an external prerequisite. None of these scripts install, start, stop, or configure Weaviate or Docker.

## Script overview

### `Publish-Backend.ps1`

Publishes `src\LocalRag.Host\LocalRag.Host.csproj` as a self-contained Windows x64 application. The default configuration is `Release`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Backend.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Backend.ps1 -Configuration Debug
```

The publish output is written to `src\LocalRag.Host\bin\win-x64\`. This is the host project's self-contained publish directory, not the final per-user installation directory. It contains `LocalRag.Host.exe`, `LocalRag.Host.dll`, the `.deps.json` and `.runtimeconfig.json` files, `appsettings.json`, .NET runtime files, native dependencies such as `onnxruntime.dll` and `e_sqlite3.dll`, and the other files needed for a self-contained launch.

### `Package-VsCodeExtension.ps1`

Restores the VS Code extension's locked Node dependencies, compiles and lints the TypeScript extension, runs its tests, and creates a `.vsix` package. By default it also installs and verifies the package with the VS Code command-line launcher. Host publishing is intentionally separate; use `Publish-Backend.ps1` or `Build-Installer.ps1` when you need the standalone host.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1 -SkipInstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1 -OutputPath .\artifacts\local-rag-test.vsix
```

Important options:

- `-SkipInstall` creates the VSIX without installing it into VS Code.
- `-SkipDependencyRestore` skips `npm ci`.
- `-SkipTests` skips the extension lint/test phase.
- `-OutputPath` selects the VSIX output path; relative paths are resolved from the repository root.

The installer build produces a **host-free** VSIX. The extension discovers the separately installed host through `%LOCALAPPDATA%\LocalRag\installation.json`; `src\vscode-extension\.vscodeignore` excludes `bin\**` as a safety measure so stale local build output cannot be shipped accidentally.

### `Install-LocalRagEmbeddingModel.ps1`

Downloads the pinned BGE Small English v1.5 ONNX assets described by `embedding-model.manifest.json`. Each file is downloaded to a temporary path, checked for the expected size and SHA-256 hash, and then moved into place. Existing files are reused only when both checks pass.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-LocalRagEmbeddingModel.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-LocalRagEmbeddingModel.ps1 -ModelDirectory C:\LocalRag\models\bge-small-en-v1.5
```

The default model directory is `%LOCALAPPDATA%\LocalRag\models\bge-small-en-v1.5\`. Use `-ManifestPath` when testing a different pinned manifest. Do not edit the URLs, revision, hashes, or sizes casually; these values are the supply-chain verification boundary for the model assets.

### `Build-Installer.ps1`

Builds the complete per-user Windows setup executable. It publishes the host into the host project's `bin` directory, validates the required host files, builds a host-free VSIX, verifies the VSIX layout, validates and stages the model assets, compiles `src\installer\LocalRag.iss` with Inno Setup 6, and writes the setup executable plus an adjacent SHA-256 sidecar file.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -SkipModelDownload
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -IsccPath 'C:\Path\To\ISCC.exe' -OutputDirectory .\artifacts
```

Important options:

- `-SkipModelDownload` uses the existing model cache after the script still verifies its hashes.
- `-SkipDependencyRestore` passes the dependency-restore skip through to VSIX packaging.
- `-SkipExtensionTests` skips the extension test phase.
- `-ModelCacheDirectory` selects the model cache to validate and stage.
- `-IsccPath` explicitly selects the Inno Setup compiler.
- `-OutputDirectory` selects where the VSIX, setup executable, and hash sidecar are written.

The temporary `.installer-stage-<guid>` directory is created under the output directory and removed after the build, including when the build fails. The final setup output is normally `artifacts\LocalRag-Setup-<version>.exe` and `artifacts\LocalRag-Setup-<version>.exe.sha256`.

`embedding-model.manifest.json` is not a script. It pins the model repository, revision, URLs, file sizes, and SHA-256 hashes consumed by `Install-LocalRagEmbeddingModel.ps1` and `Build-Installer.ps1`.

## Publishing and installation layout

There are three different locations to keep straight: the repository publish output, the temporary installer staging area, and the user's installed application/data directories.

```text
Repository (build time)
\- src\LocalRag.Host\bin\win-x64\
   \- LocalRag.Host.exe
   \- LocalRag.Host.dll
   \- LocalRag.Host.deps.json
   \- LocalRag.Host.runtimeconfig.json
   \- appsettings.json
   \- .NET runtime and native dependency files

Temporary installer staging (removed by Build-Installer.ps1)
\- artifacts\.installer-stage-<guid>\
   \- host\                 # complete self-contained host publish
   \- model\                # verified model.onnx and vocab.txt

Final per-user installation
\- %LOCALAPPDATA%\Programs\LocalRag\
   \- Host\<version>\
      \- LocalRag.Host.exe  # canonical installed host application
      \- LocalRag.Host.dll
      \- LocalRag.Host.deps.json
      \- LocalRag.Host.runtimeconfig.json
      \- appsettings.json
      \- .NET runtime and native dependency files

Mutable per-user LocalRAG data
\- %LOCALAPPDATA%\LocalRag\
   \- installation.json     # installed host path and version
   \- install-settings.json  # non-secret Weaviate endpoint
   \- models\bge-small-en-v1.5\
      \- model.onnx
      \- vocab.txt
   \- SQLite data, logs, and other runtime state

VS Code extension (managed by VS Code)
\- %USERPROFILE%\.vscode\extensions\starlinx-llc.local-rag-<version>\
   \- package.json
   \- out\extension.js
   \- out\installerSettings.js
```

The exact VS Code extensions root can differ for Insiders, portable installs, or a custom `--extensions-dir`; use `code --list-extensions --show-versions` to verify registration. The VSIX does not own the host executable. The extension reads `installation.json`, validates the recorded executable, and reports that LocalRAG needs repair if the standalone host is missing or corrupt.

The installer is per-user (`PrivilegesRequired=lowest`). Uninstall removes the installed application and attempts to remove the VS Code extension, but intentionally retains the model cache and mutable `%LOCALAPPDATA%\LocalRag` data so user indexes and configuration are not silently destroyed.

## Typical developer workflows

Build and test the extension package without installing it:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1 -SkipInstall
```

Build the complete setup executable using already downloaded model assets:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -SkipModelDownload
```

Run the repository tests separately when changing host code:

```powershell
dotnet test .\LocalRag.sln -c Release
```

The installer collects the external Weaviate base URL and port, defaulting to `http://localhost` and `8080`, and stores the resulting endpoint as `http://localhost:8080`. A connectivity check is advisory only; an unavailable Weaviate endpoint must not prevent the host, model, or VS Code extension from being installed.
