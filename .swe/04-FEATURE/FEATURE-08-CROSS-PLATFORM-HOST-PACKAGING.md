# Feature: Cross-Platform Host Packaging

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: publish/package/service scripts, installer discovery schema, host project, extension installer settings/launch, CI release matrix  
ADRs: No standalone ADR exists; DESIGN.md Sections 5.2 and 14.2 govern the draft. Supported RIDs, package formats, service modes, paths, and discovery/upgrade policy require an accepted packaging ADR before Ready.

---

## Implementation plan (step-by-step)

- [ ] Approve finite Phase 2 RID/OS/architecture, package-format, service-mode, signing/notarization, and support matrix.
- [ ] Define versioned platform-neutral installation discovery, per-user data/model/config paths, upgrade/rollback, and retention.
- [ ] Parameterize host publishing and verify self-contained native dependencies for every selected RID.
- [ ] Refactor extension discovery/process launch to remove Windows-only `.exe`, `LOCALAPPDATA`, and path assumptions.
- [ ] Add approved install/uninstall/service scripts/packages while preserving advisory-only external Weaviate configuration.
- [ ] Add artifact manifests/checksums, content validation, missing/corrupt/upgrade/uninstall negative tests, and CI matrix.
- [ ] Run platform-native build/install/launch/health/search/upgrade/uninstall smoke tests; record exact evidence.
- [ ] Update installation, repair, supported-platform, service, data-retention, Weaviate, and release documentation.

---

## Purpose

Deliver a standalone LocalRAG host that can be published, installed, discovered, launched, repaired, upgraded, and removed on every explicitly supported Phase 2 platform without taking ownership of Weaviate.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Supported platform/package matrix and installation experience |
| Engineering | Discovery/path/process abstractions and platform-neutral contracts |
| DevOps / SRE | Service scripts, dependency checks, upgrade/rollback, and support boundaries |
| QA | Native-runner artifact/install/launch/repair/upgrade/uninstall matrix |

---

## Scope

### In scope

- Self-contained host artifacts for an approved finite RID matrix.
- Platform-native per-user paths, one versioned discovery schema, extension launch, install/uninstall/service scripts or selected package formats.
- Artifact checksums/manifests, dependency diagnostics, upgrade/rollback/repair/retention, CI/native smoke tests.

### Out of scope

- Bundling or managing Weaviate/Docker, network/team hosting, unsupported distributions/architectures, and native installer formats not approved for Phase 2.

---

## Business Rules

- R2.8-rid_packages: every advertised RID artifact is self-contained, versioned, checksummed, content-validated, and smoke-tested on a matching environment.
- R2.8-discovery: one versioned installation record exposes host executable, version, platform/RID, and compatible non-secret settings without embedding credentials.
- R2.8-external_weaviate: setup accepts/stores a non-secret endpoint and may warn on connectivity, but never installs/starts/stops/configures Weaviate/Docker.
- Extension launch uses platform APIs/paths and the existing token environment boundary; no committed secret.
- Mutable user data/model retention and host-version cleanup are explicit and recoverable.
- Missing/corrupt discovery/host/native dependency produces repair guidance, not silent fallback to arbitrary executables.
- Upgrade selects a complete version atomically enough to avoid mixed host files; rollback behavior is documented/tested.
- Build success alone is insufficient; native execution, health, inference, and live Weaviate search are release gates.

---

## User Flows

### Primary flows

1. Install and launch
   - Actor: User/installer and VS Code extension
   - Trigger: Install an approved package and activate extension.
   - Steps: Validate/copy host/model; write discovery/settings; extension resolves native executable; launch with token/Weaviate endpoint; poll health.
   - Result: Standalone host is live and ready when external dependencies are available.
2. Install with unavailable Weaviate
   - Actor: Installer
   - Trigger: Connectivity probe fails.
   - Steps: Warn/advisory log; continue host/model/extension installation; runtime readiness explains dependency.
   - Result: Installation succeeds without managing external Weaviate.
3. Upgrade/uninstall
   - Actor: User/package workflow
   - Trigger: New version or removal.
   - Steps: Install complete version; update discovery; verify; retain/remove mutable data per policy; remove extension/host as documented.
   - Result: No mixed version and no silent index/model data loss.

### Edge cases

- Discovery schema newer/older/corrupt → explicit compatibility or repair path.
- Host executable/native library missing → validation fails before launch.
- Model missing/hash mismatch → readiness/repair guidance, never unverified substitution.
- Existing backend process during upgrade → documented shutdown/version handoff.
- Custom/portable VS Code or service mode → use approved discovery, not hard-coded install root.
- Unsupported OS/RID → clear unsupported message; no untested package claim.

---

## System Behaviour

- Entry points: publish/package/install/uninstall/service scripts, extension activation/discovery/launch, host startup/health.
- Reads from: project/package manifests, platform environment/path APIs, installation/settings records, model manifest, external endpoint.
- Writes to: versioned host installation, discovery/settings, selected service registration, artifacts/checksums; mutable data only per policy.
- Side effects / emitted events: installation/service changes and process launch within approved platform scope.
- Idempotency: repeated install/repair is safe; versioned paths and atomic discovery update prevent mixed files.
- Error handling: fail package validation on missing/corrupt host/model/native files; keep external Weaviate failure advisory at install and degraded at runtime.
- Security / permissions: per-user/least privilege by default; tokens never written to discovery/settings; validate executable path ownership/scope.
- Feature flags / toggles: service mode/package type per approved platform; no Weaviate management toggle.
- Performance / SLAs: installation/publish bounded; host startup/readiness measured separately from unavailable external dependencies.
- Observability: package version/RID/hash/content manifest and safe installer/launch/health diagnostics.

---

## Diagrams

~~~mermaid
flowchart LR
    A[RID publish] --> B[Validate host and model]
    B --> C[Package and checksum]
    C --> D[Install per user]
    D --> E[Write versioned discovery]
    E --> F[Extension launches host]
    F --> G[External Weaviate and health smoke]
~~~

---

## Verification

### Test environment

- Environment / stack: matching native or explicitly approved CI runners for every selected RID, supported VS Code, real model assets, external Weaviate.
- Data and reset strategy: disposable test account/home/data roots where possible; explicit install paths; preserve/copy fixture mutable data for upgrade/uninstall tests.
- External dependencies: real platform host, native ONNX/SQLite/PDF/OCR libraries, model assets, VS Code CLI, and external Weaviate for release smoke.

### Test commands

- build: `dotnet build .\LocalRag.sln -c Release` and the RID-aware publish command/script added by this feature.
- test: `dotnet test .\LocalRag.sln -c Release`; extension from `src/vscode-extension`: `npm test`.
- format: `dotnet format .\LocalRag.sln --verify-no-changes`; extension `npm run lint`; platform script lint selected by the packaging ADR.
- coverage: use the Phase 2 native platform installation/smoke matrix as release coverage; code-coverage tooling is not currently configured.

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-08-001 | Build/install/launch each approved RID | Packaging/E2E | Complete verified host runs health/live | Matching runner |
| POS-08-002 | Extension discovery and launch | UI/E2E | Correct native executable/token/endpoint used | Platform paths |
| POS-08-003 | Live inference and Weaviate search | E2E | Ready health and indexed/searchable fixture | Real dependencies |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-08-001 | Missing/corrupt host/native/model/discovery | Packaging/UI | Validation/launch fails safely with repair guidance | Mutated packages |
| NEG-08-002 | Weaviate unavailable at install | Packaging/E2E | Advisory warning; install succeeds; runtime degraded | Endpoint offline |
| NEG-08-003 | Unsupported RID/package | Packaging | Clear unsupported failure; no artifact claim | Matrix exclusion |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-08-001 | Upgrade/rollback/uninstall with existing data/model | Packaging/E2E | Version consistency and documented retention | Version N/N+1 |
| EDGE-08-002 | Newer/older discovery schema | Unit/E2E | Compatible migration or explicit repair | Schema fixtures |
| EDGE-08-003 | Existing host process/custom VS Code location | E2E | Safe handoff and discovery without hard-coded root | Platform matrix |

### Test mapping

- Integration tests: discovery parsing/migration, path resolution, process launch, configuration precedence.
- API tests: health/readiness and live source/index/search contracts per package.
- UI / E2E tests: extension discovery/repair on every supported platform.
- Unit tests: platform path selection, executable validation, manifest/checksum, schema compatibility.
- Static analysis: platform script lint, package content allowlist, hash/version consistency, extension compile/lint.

### Non-functional checks

- Performance / load: startup/readiness and package size/time recorded per RID.
- Security / privacy: per-user permissions, executable/discovery validation, no credentials in artifacts/settings/logs.
- Observability: artifact manifest/hash, installer/launcher diagnostics, and health failure guidance.

---

## Definition of Done

- Behaviour matches R2.8 rules and the accepted platform/package ADR.
- Every advertised RID passes native build/install/launch/health/inference/live-search/upgrade/uninstall tests.
- Negative/corrupt/unsupported/dependency-unavailable cases pass and do not manage Weaviate.
- Static analysis, package content/hash/version, secret, and permissions checks pass.
- Supported platforms, package formats, service modes, paths, discovery schema, repair, retention, and rollback docs are updated.
- FEATURE-08 and PLAN-02 release evidence is recorded and explicitly reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `scripts/Publish-Backend.ps1`, `scripts/Build-Installer.ps1`, `src/installer/LocalRag.iss`, extension installer settings/launch, `scripts/README.md`
