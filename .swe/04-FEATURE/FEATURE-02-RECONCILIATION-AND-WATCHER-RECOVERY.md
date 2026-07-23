# Feature: Reconciliation and Watcher Recovery

Status: Completed
Approval: Explicitly approved by the user on 2026-07-22 for governed implementation through `$swe-code FEATURE-02`.

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: `Infrastructure/Indexing`, SQLite job/checkpoint/source state, status APIs, diagnostics  
ADRs: [ADR-002: Durable Reconciliation State and Watcher Recovery](../02-ADR/ADR-002-durable-reconciliation-state-and-watcher-recovery.md) is Accepted and governs implementation.

---

## Implementation plan (step-by-step)

- [x] Specify dirty/recovery states, causes, generations, persistence, and status transitions.
- [x] Add SQLite migration/repositories for recovery state and checkpoint outcome.
- [x] Route watcher errors and scheduled/startup scans through one coalescing reconciliation scheduler.
- [x] Make reconciliation generation-aware, restart-safe, idempotent, and bounded.
- [x] Clear degraded/dirty state only after full successful convergence; retain recoverable work on dependency failure.
- [x] Add overflow, duplicate-event, save-burst, restart, locked-file, and unavailable-dependency tests.
- [x] Run build/test/format plus real Weaviate and real ONNX recovery scenarios; record evidence.
- [x] Update operations, status, configuration, and recovery documentation.

---

## Purpose

Guarantee eventual index convergence despite missed, duplicated, or overflowed filesystem events while avoiding manual reindex and unnecessary re-embedding.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Observable automatic recovery and user-facing degraded/recovering states |
| Engineering | Durable state machine, coalescing, idempotency, and dependency semantics |
| DevOps / SRE | Recovery metrics, safe diagnostics, configuration, and restart behavior |
| QA | Deterministic overflow/race/fault injection and convergence assertions |

---

## Scope

### In scope

- Startup/periodic reconciliation and explicit watcher-overflow recovery.
- Persisted dirty cause, generation, queued/running/completed outcome, and timestamps.
- Coalescing, restart recovery, source status, metrics, retries, and no-unchanged-reembedding proof.

### Out of scope

- Distributed filesystem event processing, network shares beyond documented platform guarantees, or remote source synchronization.
- Weaviate lifecycle control or destructive recovery outside registered source scope.

---

## Business Rules

- R2.2-overflow_recovery: watcher errors mark the source dirty/degraded and queue automatic reconciliation.
- R2.2-restart_safe: dirty and in-flight recovery state survives restart and resumes.
- R2.2-no_reembed: content-hash-identical files/chunks are not re-embedded.
- Events are hints; the manifest and current eligible filesystem state determine convergence.
- At most one reconciliation generation executes per source; later hints create at most one follow-up generation.
- Degraded/dirty state clears only after successful file/chunk/vector/state convergence.
- Dependency failures preserve jobs/checkpoints and use bounded retry/dead-letter rules.
- Missing-source grace-period behavior remains distinct from watcher/reconciliation failure.

---

## User Flows

### Primary flows

1. Overflow recovery
   - Actor: File watcher
   - Trigger: Watcher overflow or error.
   - Steps: Persist dirty cause; mark degraded; coalesce/queue generation; scan/diff/apply; persist completion; restore Ready.
   - Result: Index matches filesystem without manual reindex.
2. Scheduled reconciliation
   - Actor: Background service
   - Trigger: Configured interval or startup.
   - Steps: Queue eligible sources; skip/coalesce active generations; reconcile changed/deleted items only.
   - Result: Missed changes converge and unchanged files are not re-embedded.

### Edge cases

- Repeated overflow during active recovery → one bounded follow-up generation.
- Host exits mid-scan → recovery resumes from durable state after restart.
- Weaviate/model unavailable → job retained, status degraded, bounded retry.
- Source root missing temporarily → existing grace policy applies; no premature delete.
- Source removed during recovery → cancellation and scoped cleanup complete safely.

---

## System Behaviour

- Entry points: `SourceWatcherRegistry.Error`, startup service, `ReconciliationService`, explicit reindex.
- Reads from: source registry, persisted recovery/job/checkpoint state, filesystem manifest, indexing configuration.
- Writes to: SQLite recovery/job/source state and Weaviate only through existing coordinator/vector interfaces.
- Side effects / emitted events: source status, overflow/reconciliation counters, durations, changed/deleted/unchanged counts.
- Idempotency: Yes; source generation, deterministic IDs, content hashes, and existing upsert semantics.
- Error handling: classify cancellation/transient/permanent; bounded retry; retain evidence and actionable status.
- Security / permissions: operate only on registered roots; diagnostics use source IDs/path hashes, never full content.
- Feature flags / toggles: reconciliation interval and concurrency remain configurable; automatic overflow recovery is enabled.
- Performance / SLAs: incremental detection-to-index p95 below 10 seconds when healthy; scans avoid unchanged embedding.
- Observability: overflow cause/count, queued/running generation, scan duration/results, retries, last success/failure.

---

## Diagrams

~~~mermaid
stateDiagram-v2
    Ready --> Dirty: watcher error or scheduled scan
    Dirty --> Recovering: generation leased
    Recovering --> Ready: convergence succeeds
    Recovering --> Dirty: later hint arrives
    Recovering --> Degraded: dependency or permanent failure
    Degraded --> Recovering: retry or dependency restored
~~~

---

## Verification

### Test environment

- Environment / stack: .NET integration host, temporary source roots/SQLite, controllable watcher abstraction or fault seam, real external Weaviate for release.
- Data and reset strategy: seed manifest/index, mutate fixture filesystem, reset database/collection per case.
- External dependencies: real BGE ONNX and Weaviate for end-to-end convergence; deterministic failing adapters for fault injection.

### Test commands

- build: `dotnet build .\LocalRag.sln -c Release`
- test: `dotnet test .\LocalRag.sln -c Release`
- format: `dotnet format .\LocalRag.sln --verify-no-changes`
- coverage: `dotnet test .\LocalRag.sln -c Release --no-build --no-restore --collect:"Code Coverage;Format=Cobertura" --results-directory <artifact-directory>`

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-02-001 | Overflow with create/change/delete | Integration | One recovery converges SQLite/Weaviate and returns Ready | Real Weaviate |
| POS-02-002 | Restart with dirty/in-flight state | Integration | Recovery resumes and completes | Persisted test database |
| POS-02-003 | Reconcile unchanged source | Integration | Zero passage embeddings/upserts for unchanged content | Recording counters |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-02-001 | Weaviate/model unavailable | Integration | Bounded retry, retained job, degraded status | Fault/live dependency stop |
| NEG-02-002 | Source removed during recovery | Integration | Work cancels and cannot resurrect source/chunks | Race test |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-02-001 | Repeated overflow during active recovery | Integration | One follow-up generation; no storm/lost hint | Controlled scheduler |
| EDGE-02-002 | Crash after vector upsert before state commit | Integration | Retry converges idempotently | Fault injection |
| EDGE-02-003 | Temporarily missing root | Integration | Grace policy preserves source and recovery state | Existing policy fixture |

### Test mapping

- Integration tests: overflow, restart, dependency failure, atomicity, convergence, unchanged embeddings.
- API tests: status transitions and bounded diagnostics.
- UI / E2E tests: extension displays degraded/recovering/ready and action guidance.
- Unit tests: state machine, coalescer, generation scheduler, failure classification.
- Static analysis: solution build and `dotnet format --verify-no-changes`.

### Non-functional checks

- Performance / load: save burst and large source scan with bounded queue/memory and responsive search.
- Security / privacy: registered-root scope and diagnostic redaction.
- Observability: assert every state transition and recovery outcome metric/log.

---

## Definition of Done

- Behaviour matches R2.2 rules and flows.
- All positive, negative, edge, restart, and race tests are automated and pass.
- Static analysis has no new unresolved issues.
- Build/test/live ONNX/live Weaviate commands pass; skipped live tests are not release evidence.
- SQLite migrations, recovery/rollback behavior, metrics, configuration, and operator docs are updated.
- FEATURE-02 and PLAN-02 evidence is recorded and explicitly reviewed.

### Completion evidence (2026-07-22)

- Release build: `dotnet build .\LocalRag.sln -c Release --no-restore` succeeded with zero warnings and zero errors.
- Deterministic local suite: `dotnet test .\LocalRag.sln -c Release --no-build` passed 124 tests, skipped only four explicitly gated external/evaluation tests, and failed zero tests.
- Live release suite: with `LOCALRAG_ONNX_TESTS=1` and `WEAVIATE_TEST_ENDPOINT=http://127.0.0.1:8080`, the real Weaviate contract, pinned real BGE ONNX profile, and combined overflow/restart create-change-delete/no-unchanged-reembedding recovery scenarios passed 3/3 with zero skips and zero failures.
- Fault and concurrency matrix: automated coverage includes 64-request coalescing, one-current/one-successor ordering, delayed retry precedence, lease expiry/stale completion, retry exhaustion/dead-letter retention, forced-profile successor precedence, legacy migration/dead-letter preservation, bounded SQLite writer replay, locked-file retry, actual removal during a blocked vector mutation, vector-upsert-before-manifest convergence, temporary missing-root recovery, 64-file scan, unchanged zero-embedding/upsert behavior, and status/log/metrics redaction.
- Coverage: the documented Cobertura command passed and reported 6,932/9,216 lines (75.22%) and 2,012/3,222 branches (62.45%); the machine-readable report was retained in the run's temporary artifact directory.
- Static/client gates: `dotnet format .\LocalRag.sln --verify-no-changes --no-restore`, VS Code `npm.cmd run lint`, and all 9 extension tests passed.
- Operations and compatibility: additive SQLite migration/rollback guidance, bounded retry/retention configuration, recovery metrics/status, API/MCP redaction, VS Code recovery presentation/action, and README operations guidance are implemented.
- Review disposition: independent solution-architecture and C# implementation/test-evidence reviews both returned `APPROVE` on the final bytes after lease-expiry, fail-closed traversal, retry-coalescing, dispatcher wakeup, SQLite contention, generation/profile, retention, and evidence findings were corrected.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `SourceWatcherRegistry.cs`, `ReconciliationService.cs`, `IndexCoordinator.cs`, job/checkpoint/source SQLite stores
