# LocalRAG build and installation scripts

This directory contains the scripts used to publish the Windows host, package the VS Code extension, provision the local embedding model, and build the Windows installer.

The product name is **LocalRAG** and the host executable/project is **`LocalRag.Host`**. The Windows x64 package supports one selected vector store: embedded LanceDB or external local Weaviate. None of these scripts install, start, stop, or configure Weaviate or Docker.

## Script overview

### `Publish-Backend.ps1`

Publishes `src\LocalRag.Host\LocalRag.Host.csproj` as a self-contained Windows x64 application. The default configuration is `Release`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Backend.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Backend.ps1 -Configuration Debug
```

The publish output is written to `src\LocalRag.Host\bin\win-x64\`. The script also runs `Build-LanceDbNative.ps1`, which performs a locked Rust build, stages `localrag_lancedb.dll` and its PDB, generates a Cargo dependency/license inventory, writes the hashed ABI manifest, and validates the real PE machine and exports. Rust 1.91.0, Protocol Buffers, and the Visual Studio 2022 C++ build tools must be available.

### `Build-LanceDbNative.ps1`

Builds and packages the pinned win-x64 Rust `cdylib` into a specified host output directory. Tool paths can be supplied explicitly with `-CargoPath`, `-ProtocPath`, and `-VsDevCmdPath`; otherwise the script resolves standard installations. Runtime download of native code is not supported.

The build also runs `Stage-MsvcRuntime.ps1` to copy the unmodified signed x64
release CRT from the matching Visual Studio redist folder. Its separate
`msvc-runtime.manifest.json` records versions, hashes, signatures and origin;
`msvc-runtime-imports.json` records packaged PE imports. Package validation
checks the runtime inventory and MSVC dependency closure. These app-local files
also satisfy ONNX/OCR imports. They require servicing with application updates.
Microsoft redistribution terms and entitlement remain part of the required
human distribution review; a locally built candidate is not an approval.

### `Package-VsCodeExtension.ps1`

Restores the VS Code extension's locked Node dependencies, compiles and lints the TypeScript extension, runs its tests, and creates a `.vsix` package. By default it also installs and verifies the package with the VS Code command-line launcher. Host publishing is intentionally separate; use `Publish-Backend.ps1` or `Build-Installer.ps1` when you need the standalone host.

Packaging uses pinned VSCE 4 and requires Node.js 22 or later; this build-tool minimum does not change the extension host compatibility declared by `engines.vscode`. Default secret and `.env` checks remain enabled. See the [October 17 build-tool advisory and migration evidence](../reports/10-17-2026/evidence/P1-BUILD-TOOL-ADVISORIES.md).

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

Builds the complete per-user Windows x64 setup executable. It publishes the host into a fresh candidate `host/` directory, validates required host/manager/launcher/native files, builds a host-free VSIX, validates its layout and model assets, compiles `src\installer\LocalRag.iss` with Inno Setup 6, and writes setup plus SHA-256, source-snapshot and release manifests. Compiled/build inputs include untracked source and are hashed before and after packaging; a changed input fails the candidate.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -SkipModelDownload
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -IsccPath 'C:\Path\To\ISCC.exe' -OutputDirectory .\artifacts\releases\0.1.0\my-build-id
```

Important options:

- `-SkipModelDownload` uses the existing model cache after the script still verifies its hashes.
- `-SkipDependencyRestore` passes the dependency-restore skip through to VSIX packaging.
- `-SkipExtensionTests` skips the extension test phase.
- `-ModelCacheDirectory` selects the model cache to validate and stage.
- `-IsccPath` explicitly selects the Inno Setup compiler.
- `-OutputDirectory` selects where the VSIX, setup executable, and hash sidecar are written.

The default candidate directory is `artifacts/releases/<version>/<UTC-build-id>/`. An explicit output directory must not already contain host/VSIX/setup assets; existing candidates are never overwritten. Temporary `.installer-stage-<guid>` content is removed after the build; the fresh host publish and complete candidate remain for inspection. `release-manifest.json` starts with `approvedForDistribution=false`; compilation/package validation does not supply human release acceptance. `Publish-Backend.ps1 -OutputDirectory` permits an isolated standalone publish while retaining its original default path for existing callers.

`embedding-model.manifest.json` is not a script. It pins the model repository, revision, URLs, file sizes, and SHA-256 hashes consumed by `Install-LocalRagEmbeddingModel.ps1` and `Build-Installer.ps1`.

### `Test-LanceDbNativePackage.ps1`

Validates the native package already present in a Windows x64 host publish directory. It fails closed unless `lancedb-native.manifest.json` contains exactly the supported manifest fields, schema version 1, ABI version 1, RID `win-x64`, DLL name `localrag_lancedb.dll`, a matching SHA-256, the exact ABI-v1 export list, and resolvable license declarations. Production validation also parses the PE header and resolves every export from the binary.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-LanceDbNativePackage.ps1 -HostPublishDirectory .\src\LocalRag.Host\bin\win-x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-InstallerPackaging.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-InstallerScript.Tests.ps1
```

The manifest fixture test uses `-SkipBinaryInspection` only for its four-byte negative-test fixture. The locked release build always performs binary inspection, and `LanceDbNativeIntegrationTests` exercises the staged DLL through the managed ABI.

### `Test-LanceDbSupplyChain.ps1`

Runs the Windows-x64 Cargo dependency-graph and RustSec gates. LanceDB 0.31.0
includes `lance-testing`; its Linux-only profiling edge locks `quick-xml 0.26.0`,
which has RUSTSEC-2026-0194 and RUSTSEC-2026-0195. The script first proves that
every vulnerable quick-xml version is absent from the `x86_64-pc-windows-msvc`
graph, then applies only those two target-inapplicable lockfile exceptions.
Upstream `paste` and `unsafe-libyaml` unmaintained warnings remain visible.
The October 16 lock updates fixed h2, quinn-proto and rustls vulnerabilities
with compatible patches; no broad advisory suppression was added.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-LanceDbSupplyChain.ps1 `
  -CargoPath cargo.exe `
  -CargoAuditPath cargo-audit.exe
```

## Publishing and installation layout

There are three different locations to keep straight: the repository publish output, the temporary installer staging area, and the user's installed application/data directories.

```text
Complete installer candidate (build time)
\- artifacts\releases\<version>\<build-id>\host\
   \- LocalRag.Host.exe
   \- LocalRag.Host.dll
   \- LocalRag.Host.deps.json
   \- LocalRag.Host.runtimeconfig.json
   \- appsettings.json
   \- .NET runtime and native dependency files

Temporary installer staging (removed by Build-Installer.ps1)
\- artifacts\releases\<version>\<build-id>\.installer-stage-<guid>\
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
      \- Start-LocalRag.ps1
      \- LocalRag.Launcher.psm1
      \- lancedb-native.manifest.json
      \- localrag_lancedb.dll
      \- declared native license files
      \- .NET runtime and native dependency files

Mutable per-user LocalRAG data
\- %LOCALAPPDATA%\LocalRag\
   \- installation.json     # installed host path and version
   \- install-settings.json  # schema-v2 selected provider settings
   \- data\lancedb\          # mutable embedded store when LanceDb is selected
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

`Publish-Backend.ps1` alone retains its default `src\LocalRag.Host\bin\win-x64`
output. `Build-Installer.ps1` supplies a new candidate-specific `host` output,
captures all current compiled/build inputs including untracked files, and
refuses to accept a candidate if those input hashes change during packaging.

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

The installer UI presents LanceDB as its fresh-install default and permits external Weaviate. Distribution/recommendation remains pending the required [native release gates](../reports/10-16-2026/evidence/P1-NATIVE-RELEASE.md); the UI wording does not establish release readiness. Fresh unattended installation requires `/PROVIDER=LanceDb` or `/PROVIDER=Weaviate`; use `/LANCEDBPATH` only for LanceDB and `/WEAVIATEBASEURL` plus `/WEAVIATEPORT` only for Weaviate. Schema-v1/v2 upgrades and repairs preserve their provider/settings. Provider mismatch, corrupt/newer settings and unsupported version downgrade are refused before installer writes.

`Test-InstallerExecution.ps1` compiles and executes a distinct test-AppId variant
with absolute paths confined beneath `artifacts/runs/`, including isolated VS
Code user/extension directories. It tests actual setup, provider refusal,
schema-v1 upgrade, schema-v2 repair, downgrade refusal and uninstall retention
using the candidate's host/native/VSIX/model assets. This is not a fresh-user/VM
test of the normal installer. The normal installation/settings/token file hashes
must remain unchanged. `Test-LanceDbProcessSafety.ps1` separately runs native-only
disposable subprocesses for exclusive-open, shutdown and corruption boundaries.

The extension launches the host with `LocalRag__VectorStore__Provider` and exactly one matching `LocalRag__VectorStore__LanceDb__Path` or `LocalRag__VectorStore__Weaviate__Endpoint`. The VS Code `localRag.weaviateEndpoint` override applies only to Weaviate.

### Embedded repair and recovery

Use a short local path for custom LanceDB data directories. A bounded Windows
probe succeeded with a 128-character vector path but failed with the generic
native-operation error at 238 characters, before adding Lance's own internal
table/transaction paths. This is an observed path-depth limitation, not a
universal maximum-length guarantee; very deep custom paths are not certified.
Keep the complete existing data bundle and obtain a coherent checkpoint before
changing its location. Do not move an active store or delete it to hide this
error. Native test runs should explicitly set `LOCALRAG_NATIVE_RUN_DIRECTORY`
to their isolated run root instead of inheriting a deep test-binary directory.

Missing, corrupt, wrong-RID or wrong-ABI native packages fail closed with repair
guidance. Close the host before repairing the complete installed package; the
native module stays loaded until process exit because SDK worker pools can
outlive database handles. A changed DLL at an already-loaded path requires a
restart. A second writer to the same embedded path is refused. Do not remove
its lock or table files while the first host is active.

Preserve a damaged store for diagnosis instead of deleting it or changing the
configured provider to bypass readiness. Provider fingerprint mismatch with
indexed state requires an explicit owned reset/rebuild or coherent rollback;
there is no automatic provider migration or fallback. Reset requires existing
confirmation and ownership checks and preserves original source files.

For rollback, stop and drain indexing/watchers, close native handles, checkpoint
SQLite WAL with no busy writer, close connections, and preserve the complete
matching SQLite, vector, ownership and provider-identity bundle together.
Restore only while writers are stopped. Copying an active SQLite main file or
restoring only vector files is not a coherent checkpoint. The executed
quiesced rollback and corruption/refusal/restore probes are recorded in
[P1 evidence](../reports/10-16-2026/evidence/P1-NATIVE-RELEASE.md).

Current verification includes a fresh native build, shared real-provider
relevance/search gates, real ONNX lifecycle/reset, six Rust tests, .NET format,
46 extension tests and ten actual isolated setup scenarios. The isolated
installer branch does not certify normal fresh-profile installation or
standalone launch. The serial reference corpus measures about 2.9 files/s;
the hardware/model-dependent 10 files/s engineering target is not claimed met.
Final package provenance, production ingestion measurements and outstanding
release review remain in the linked evidence record.

### Missing results from previously indexed nested folders on Windows

An earlier Windows reconciliation bug compared native file separators with
normalized chunk paths, which could delete vectors for files in subfolders
while leaving their SQLite file/chunk rows. The corrected host prevents this
and correctly removes nested files when they are deleted. It does not
automatically reconstruct vectors already lost from an affected catalog.

If an affected source remains Ready but its nested files do not appear in
search, record its metadata and explicit project memberships, remove that
source registration, then register the same folder again and wait for Ready.
Removal preserves the original source files but the new registration has a
new source ID: update each explicit project configuration to use that ID.
An ordinary reindex can skip unchanged file rows and must not be relied on
to repair these missing vectors. No automatic reset or provider switch is
needed. The executed nested-folder regression and production measurements
are recorded in [ingestion evidence](../reports/10-16-2026/evidence/P3-PRODUCTION-INGESTION.md).

### Stale results after an interrupted indexing write

Earlier hosts could commit a vector write without its SQLite file manifest,
leaving a deleted or replaced file searchable after Pause/Resume. The corrected
host journals attempted vector mutations durably and reconciles unfinished
writes before completing Resume. This protects newly journaled operations; it
does not automatically discover historical orphan vectors from older hosts.

If an affected source still returns deleted or replaced content, record its
metadata and explicit project memberships, remove the source registration,
then register the same folder again and wait for Ready. Removal preserves
the original source files. Re-registration creates a new source ID, so update
each explicit project configuration to use it. Ordinary reindex is not a
repair guarantee for unjournaled vectors, and no automatic blanket reset is
performed. See the [active-write corrective plan](../reports/10-17-2026/plan/ACTIVE-WRITE-PAUSE.md)
for the failure, scope and verification boundaries.

## Run and release artifact retention

New disposable test runs belong under `artifacts/runs/<run-id>/`. Each run directory should contain its own source fixture copy (when needed), SQLite database and WAL/SHM companions, LanceDB tables and transaction files, logs, and a `manifest.json` recording the provider/configuration, source commit and dirty-state identifier, commands, outcomes, and retention decision. Give concurrent runs distinct IDs. Do not place scratch databases in the repository root.

Keep release candidates under `artifacts/releases/<version>/<build-id>/`. A candidate manifest must identify the exact source commit and dirty-state snapshot; installer, VSIX, native DLL/PDB and native manifest; SHA-256 for every package file; relevant commands and test outcomes; and whether a human approved that candidate. Select an approved package pair through a documented pointer in the manifest or release note. Matching filenames or version strings do not establish that files came from the same build.

The three existing `local-rag-0.1.0.vsix` files are distinct retained candidates and have not been moved or designated approved:

| Existing path | SHA-256 |
| --- | --- |
| `artifacts/local-rag-0.1.0.vsix` | `305410D83A65E1AF985B215EDFA790FB5E66FAE06664B21A6B481B399B30FD89` |
| `artifacts/feature10-final/local-rag-0.1.0.vsix` | `1EC6B18B2136875F27920969D56456886F8046B55F8B1B9DB0B2B72C0C3D0930` |
| `src/vscode-extension/local-rag-0.1.0.vsix` | `D08761D6CE631FB282C0DED5FAF1AE52689B1253EAF49568F5435F74CCC635DD` |

`artifacts/native-test/` and `artifacts/analysis-2026-10-16/` are retained verification evidence. `.tmp-feature10-e2e/` is a tracked historical run bundle with source sentinel, SQLite DB/WAL/SHM, installation identity, exclusive lock, LanceDB tables/manifests/transactions, and host logs. Its ownership and writer lifecycle are not established, so retain the complete bundle in place; do not delete or split it. The P0 snapshot also preserves it through the baseline Git bundle and copies the ignored log files.

### Dry-run cache inventory

Before any cache cleanup, enumerate only these in-repository rebuild outputs: `src/LocalRag.Host/bin/`, `src/LocalRag.Host/obj/`, `src/tests/LocalRag.Host.Tests/bin/`, `src/tests/LocalRag.Host.Tests/obj/`, `src/vscode-extension/node_modules/`, `src/vscode-extension/out/`, and `src/LocalRag.LanceDb.Native/target/`. First resolve each path against the repository root and list it with `Get-ChildItem`; a dry run may use `Remove-Item -WhatIf` on these exact paths after their build is idle. This is an inventory, not authorization to remove outputs.

Exclude `artifacts/runs/`, `artifacts/releases/`, the retained `artifacts/native-test/`, `artifacts/analysis-2026-10-16/`, `artifacts/feature10-final/`, all retained package candidates and their checksums/manifests, and all user configuration or linked paths. Do not use `git clean -fdx` or recursively follow reparse points. `src/LocalRag.LanceDb.Native/target/` is ignored; `Cargo.toml`, `Cargo.lock`, `rust-toolchain.toml`, `include/localrag_lancedb.h`, `src/lib.rs`, and the native README remain version-control candidates.
