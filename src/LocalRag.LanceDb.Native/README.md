# LocalRAG LanceDB native boundary

This `cdylib` pins Rust 1.91.0, LanceDB 0.31.0, and Arrow 58.0.0 for
`x86_64-pc-windows-msvc`. `include/localrag_lancedb.h` is the ABI-v1 contract
and `Cargo.lock` is the reproducible dependency boundary.

The native library owns the LanceDB connection/runtime and exposes bounded,
callback-free C operations for readiness, Arrow IPC merge-upsert/delete,
vector/BM25/hybrid search, ownership verification, owned reset, optimization,
cancellation, result buffers, and errors. The versioned table uses a fixed
384-element `float32` vector, cosine IVF-Flat index, BM25 content index, and
LocalRAG schema/ownership metadata. Reset drops and recreates only the owned
table.

Build and package from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-LanceDbNative.ps1 `
  -Configuration Release `
  -OutputDirectory .\artifacts\runs\<run-id>\native `
  -TargetDirectory .\artifacts\runs\<run-id>\target-fresh
```

The build is locked, produces a dependency/license inventory and hashed
manifest, and verifies the win-x64 PE plus every ABI export. Run `cargo test
--locked`, `cargo fmt -- --check`, and `cargo clippy --locked -- -D warnings`
before release. Native assets are installed with the host and are never
downloaded at runtime.

Use a new target directory to establish a build from source; the output directory
alone does not isolate Cargo's cache. `CARGO_TARGET_DIR` is honored when
`-TargetDirectory` is omitted. The build explicitly selects the Windows target
and writes `lancedb-build.provenance.json` beside the strict runtime manifest.
It records native source/lock/header hashes, tool/build details, working-tree
state and package hashes. This is source provenance, not a byte-identical-build
claim. Preserve the complete package and provenance as a unit.

Managed-only tests explicitly skip unavailable native assets. For the required
native lane, set `LOCALRAG_NATIVE_RELEASE=1` and
`LOCALRAG_LANCEDB_NATIVE_MANIFEST` to the freshly staged manifest. The release
flag makes an absent manifest fail instead of skip. Set
`LOCALRAG_NATIVE_RUN_DIRECTORY` beneath `artifacts/runs/<run-id>/` to isolate
native fixtures. Real ONNX/recovery and cross-provider corpus checks also require
`LOCALRAG_ONNX_TESTS=1` and the latter requires `WEAVIATE_TEST_ENDPOINT`.
Inspect TRX skips before treating a release campaign as complete.

`scripts/Test-LanceDbSupplyChain.ps1` proves vulnerable quick-xml versions are
absent from the actual Windows graph before applying the two Linux-only
lockfile advisory exceptions. Compatible transitive patch versions are locked;
new Windows-target vulnerabilities must be fixed, not broadly suppressed.
The October 16 audit retains visible upstream unmaintained warnings for `paste`
and `unsafe-libyaml`; the SDK pins and current release evidence document them.

The managed loader retains one validated module reference per canonical DLL
path until process teardown because SDK worker pools outlive database handles.
A changed DLL hash at the same loaded path requires restart. Store disposal drains native work before
releasing its exclusive writer lock. Direct C callers must supply live valid
pointers and obey ownership/release rules; arbitrary invalid addresses cannot
be made safe by length checks. Run destructive/corrupt-store probes only through
the disposable-process harness, never against an installed index.

Current scenario outcomes, source hashes and unresolved release gates are in
[`reports/10-16-2026/evidence/P1-NATIVE-RELEASE.md`](../../reports/10-16-2026/evidence/P1-NATIVE-RELEASE.md).

The Windows package includes the signed app-local Microsoft x64 release CRT,
its hash/signature/version/source manifest, Microsoft notice and PE import
inventory. This avoids relying on a globally installed developer runtime.
Redistribution remains subject to Microsoft's terms and the distributor's
entitlement; the candidate records that human distribution review is pending.
