# Windows native CI

The manual [Windows native workflow](../workflows/windows-native.yml) implements the existing `R2.10-supply_chain` requirement to build the native adapter in CI ([FEATURE-10](../../.swe/04-FEATURE/FEATURE-10-EMBEDDED-LANCEDB-VECTOR-STORE.md)). This definition has local syntax/fixture validation; **no remote CI run has been triggered or passed by adding it**. The actual CI requirement remains open until an authorized run executes successfully.

An owner can select **Windows native verification → Run workflow** after the source changes are reviewed and available on the remote. The default is `windows-2022`; an approved larger GitHub-hosted Windows x64 runner label may be supplied when needed. The script deliberately refuses local and self-hosted execution because model provisioning uses the disposable hosted user's normal model directory. It does not change authentication settings, publish a release, install the product, start an editor, or use Docker/Weaviate.

## Inputs and gates

- Checkout, .NET setup and artifact upload use complete commit SHAs from the official action releases. Checkout does not retain Git credentials; workflow permissions are `contents: read`. The only trigger is explicit manual dispatch. [Checkout documentation](https://github.com/actions/checkout), [.NET setup](https://github.com/actions/setup-dotnet).
- The SDK is the exact version in `global.json`, currently `10.0.302`. Rust is `1.91.0`, with rustfmt/clippy and target `x86_64-pc-windows-msvc`. `Cargo.lock` is used by build, test, clippy and target graph audit. A fresh private target directory prevents accepting a cached DLL.
- [native-tools.json](native-tools.json) pins protoc 35.0 and cargo-audit 0.22.2 archive URLs and SHA-256 values from their official release assets. Runtime version checks follow hash verification; no `latest` tool archive is resolved during a run. [Protobuf release](https://github.com/protocolbuffers/protobuf/releases/tag/v35.0), [RustSec release](https://github.com/RustSec/rustsec/releases/tag/cargo-audit/v0.22.2).
- The existing model manifest provisions and verifies both ONNX/vocabulary files before the native build. Missing, changed, unavailable or incompatible assets fail the job; they do not become optional skips.
- Existing scripts perform the fresh managed/native publish, manager/package validation, native PE/exports/ABI/hash/license inventory, app-local signed CRT import closure, Windows-target supply-chain audit and disposable-process safety probes. Rust tests, fmt and clippy run with explicit results. Existing Linux-only audit exceptions remain guarded by the actual Windows dependency graph; unmaintained warnings remain visible.
- Required .NET tests include real native CRUD/ranking/fault/rollback, real ONNX recovery, embedded public HTTP lifecycle/reset/nested-path behavior, all four active-write pause races and fourteen durable journal recovery cases. The 28 exact required results are in `NativeCi.Contracts.psm1`, including each parameterized recovery window. The generated TRX must contain every required result exactly once, all selected tests must pass, and zero-test, skipped, failed, missing or unavailable evidence fails the job.
- Git HEAD, source inputs before/after, exact tools, model manifest, native build provenance, runtime/license/import inventories, resolved NuGet graph, host-file hashes, commands/durations, TRX and process outcomes are retained. Central NuGet versions are pinned, but this repository has no transitive NuGet lockfile; the resolved graph is recorded rather than falsely called locked. Hosted VM/MSVC images can change and their resulting package/runtime identities are recorded. No byte-identical build claim is made.

The fresh target observed locally occupies about 3.7 GB, excluding dependency/model/SDK/staging storage. The original native compile used several GB of RAM. Standard public Windows runners are documented at 16 GB RAM/14 GB SSD; private/larger runner resources can differ. Two Cargo build jobs limit concurrent compilation pressure; the 120-minute job timeout remains an actual failure boundary. Runner resources and network/tool availability must be assessed from the eventual run, not inferred from local fixture passes. [GitHub-hosted runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

## Evidence without binary redistribution

Native/Host/CRT/model binaries stay in private job working directories. The artifact path is only `artifacts/ci/windows-native/evidence/`. A separate `always()` safety step validates that directory before upload; it rejects reparse points, non-UTF8 data, PE files even when renamed, unapproved extensions and empty evidence. Only JSON/log/TRX/text metadata is eligible. Upload is conditional on that safety step succeeding, even when a prior build/test gate failed. Failing build/test gates remain failed; retaining their logs does not turn the job green. [Artifact upload behavior](https://github.com/actions/upload-artifact).

No `.dll`, `.exe`, `.pdb`, model, package archive, installed database, or redistributable runtime is uploaded. The recorded runtime-redistribution approval remains false. License notices and file hashes are evidence, not a grant of redistribution entitlement. Existing candidate3/candidate4 packages and snapshots are not inputs overwritten by this workflow.

This lane is Windows native component verification, **not** the full release certification: external Weaviate parity, installer execution, installed launcher, paired desktop clients, performance targets, human acceptance and distribution authorization remain separately evidenced. A CI definition or local contract test closes none of those gates.

## Local validation

Run from the repository root:

```powershell
pwsh -NoProfile -File scripts/Test-WindowsNativeCiContract.ps1 `
  -OutputDirectory artifacts/runs/<new-run>/ci-contracts
```

The fixtures exercise missing/zero/skipped/failed TRX refusal, exact required-test presence, binary upload rejection, changed/missing/empty model asset refusal, trigger/permission/tool-pin contracts, nonzero tool exit propagation, PowerShell script parameter binding and actual local provisioning refusal. They parse the three PowerShell files and retain fixture results. They do not provision tools, compile Rust, download a model or run inference. Workflow YAML/expressions are additionally checked with pinned upstream actionlint in the local implementation evidence; actual hosted execution is still pending.
