# Feature: Metrics and Diagnostics Dashboard

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: `Infrastructure/Diagnostics/OperationalMetrics.cs`, health/source/job state, diagnostics API, extension dashboard  
ADRs: No standalone ADR exists; DESIGN.md Section 11 is the draft source. Metric format, retention, diagnostic permission, and redaction decisions require acceptance before Ready.

---

## Implementation plan (step-by-step)

- [ ] Approve metric format/types, bounded labels, retention/restart semantics, diagnostics permission, and dashboard interaction.
- [ ] Define safe application diagnostics contracts for dependencies, sources, jobs, search/MCP, and recovery.
- [ ] Replace minimal counters with concurrency-safe counters/histograms/gauges and bounded recent-failure data if approved.
- [ ] Instrument indexing, queue, embedding, Weaviate, search, MCP, overflow, and reconciliation events.
- [ ] Add authenticated diagnostics endpoint and `localRag.openDashboard` extension view.
- [ ] Add authorization, redaction, concurrency, cardinality, restart, degraded-state, and UI tests.
- [ ] Run build/test/format, load/cardinality, and log/metric scan; record evidence.
- [ ] Update metric catalog, health/status semantics, troubleshooting, privacy, and retention documentation.

---

## Purpose

Give users and operators actionable, source-safe visibility into indexing, retrieval, dependencies, failures, and automatic recovery without exposing repository content or secrets.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Dashboard states, recovery guidance, and user-visible diagnostics |
| Engineering | Metric/event contracts, instrumentation points, and bounded storage |
| DevOps / SRE | Health semantics, metric catalog, retention, and troubleshooting |
| QA | Exact counter/histogram assertions, auth/redaction, load/cardinality, and UI states |

---

## Scope

### In scope

- Design-listed files/chunks/jobs/queue/embedding/Weaviate/search/MCP/overflow/reconciliation measures.
- Safe dependency/source/job diagnostics, authenticated endpoint, dashboard, guidance, retention, and redaction.

### Out of scope

- Third-party telemetry export, cloud monitoring, full query/content logging, absolute-path labels, unbounded per-file series, or cross-tenant dashboards.

---

## Business Rules

- R2.7-metrics: counters/gauges/histograms are concurrency-safe, documented, and bounded in label cardinality.
- R2.7-dashboard: dashboard reports actual dependency/source/job state and actionable recovery guidance through authorized contracts.
- R2.7-redaction: never expose credentials, absolute roots, full query/chunk content, or unbounded relative paths.
- Ready/degraded/unhealthy dashboard meaning matches health/status endpoints and recovery state.
- Aggregates and source-level diagnostics are source-authorized; unauthorized counts cannot leak inventory.
- Retention and restart/reset semantics are explicit and testable.
- Metric/log names and units are stable or versioned.
- Diagnostics must not become a new direct SQLite/Weaviate adapter in the extension.

---

## User Flows

### Primary flows

1. Diagnose degraded source
   - Actor: Authorized developer
   - Trigger: Open `localRag.openDashboard`.
   - Steps: Fetch safe snapshot; display dependency health, source recovery/job status, measures, and guidance.
   - Result: User can distinguish unavailable Weaviate/model, dirty/recovering source, failed job, or healthy state.
2. Observe retrieval/indexing
   - Actor: Operator/developer
   - Trigger: Search/index/reconciliation activity.
   - Steps: Instrument stages; aggregate bounded measures; refresh dashboard.
   - Result: Throughput, latency, queue, result, failure, and recovery trends are visible.

### Edge cases

- Unauthorized diagnostic principal → deny without aggregate/source inventory leak.
- Many sources/files/errors → bounded labels/retention and stable memory.
- Restart → documented counter/reset/persisted failure semantics.
- Dependency flaps → health/dashboard state converges without stale Ready.
- Diagnostic serialization/logging failure → core indexing/search remains available and safe.

---

## System Behaviour

- Entry points: instrumentation APIs, health/status queries, authenticated diagnostics REST, dashboard command/view.
- Reads from: concurrency-safe measures, source/job/recovery state, health checks, bounded failure store if approved.
- Writes to: in-memory metrics and optionally approved bounded SQLite diagnostic records; no content store.
- Side effects / emitted events: endpoint responses and safe structured logs.
- Idempotency: reads are side-effect-free; metric increments occur once at defined lifecycle events.
- Error handling: diagnostic failure is isolated, logged safely, and cannot crash core indexing/search.
- Security / permissions: FEATURE-04 diagnostic scope/source filtering; schema allowlist and redaction.
- Feature flags / toggles: dashboard/endpoint enabled locally; external export absent unless future decision.
- Performance / SLAs: low-overhead instrumentation; bounded memory/labels; dashboard does not block indexing/search.
- Observability: self-observe endpoint failures/redactions without recursive unbounded logging.

---

## Diagrams

~~~mermaid
flowchart LR
    A[Index search MCP recovery events] --> B[Safe metric and diagnostic services]
    B --> C[Authenticated diagnostics API]
    C --> D[VS Code dashboard]
    B --> E[Structured logs]
~~~

---

## Verification

### Test environment

- Environment / stack: .NET host, disposable SQLite, instrumented fake/live dependencies, extension dashboard tests, load harness.
- Data and reset strategy: deterministic lifecycle events and clean metric/diagnostic instance per test; bounded seeded failures.
- External dependencies: live ONNX/Weaviate for end-to-end measures; controlled failing dependencies for state tests.

### Test commands

- build: `dotnet build .\LocalRag.sln -c Release`; extension `npm run compile`
- test: `dotnet test .\LocalRag.sln -c Release`; extension `npm test`
- format: `dotnet format .\LocalRag.sln --verify-no-changes`; extension `npm run lint`
- coverage: no coverage command currently defined; add before completion or record reviewer-approved omission.

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-07-001 | Index/retry/search/MCP/overflow/reconcile lifecycle | Integration | Each documented measure updates once with correct units | Controlled events |
| POS-07-002 | Authorized degraded-source dashboard | API/UI | Accurate dependency/recovery/job state and guidance | Live/failing dependency |
| POS-07-003 | Healthy operation and refresh | UI | Ready state, bounded metrics, no content | Extension dashboard |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-07-001 | Principal lacks diagnostic/source grant | API/UI | Denied/filtered with no aggregate leak | Multi-client seed |
| NEG-07-002 | Token/path/query/content enters error context | Integration/Security | Sensitive value absent from output/log/metric | Canary strings |
| NEG-07-003 | Diagnostics exporter/serialization fails | Integration | Core request/job continues; safe bounded failure | Fault adapter |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-07-001 | High source/file/error cardinality | Load | Memory/series/retention remain within approved bound | Load corpus |
| EDGE-07-002 | Concurrent increments and snapshot | Unit/Load | No lost/corrupt counts; consistent documented snapshot | Parallel harness |
| EDGE-07-003 | Restart and dependency flapping | Integration | Documented reset/persistence and convergent state | Restart clock |

### Test mapping

- Integration tests: instrumentation lifecycle, health/recovery consistency, auth, restart, dependency failure.
- API tests: schema, filters, redaction, errors, bounded payload.
- UI / E2E tests: every dashboard state, refresh, guidance, permission, package assets.
- Unit tests: metric types, concurrency, label allowlist, retention, redaction.
- Static analysis: build/format/lint and forbidden-field/schema scan.

### Non-functional checks

- Performance / load: instrumentation overhead, cardinality, memory, snapshot latency.
- Security / privacy: canary token/path/query/content scan across responses/logs/metrics/UI.
- Observability: metric catalog and state-transition assertions are themselves complete.

---

## Definition of Done

- Behaviour matches R2.7 rules and approved format/retention/permission decisions.
- Positive, negative, edge, concurrency, cardinality, restart, redaction, and dashboard tests pass.
- Static analysis and sensitive-data scans pass.
- Build/test/live dependency/load/package verification passes without unresolved diagnostic regressions.
- Metric catalog, endpoint/view, health semantics, privacy, retention, and troubleshooting docs are updated.
- FEATURE-07 and PLAN-02 evidence is recorded and reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `Infrastructure/Diagnostics/OperationalMetrics.cs`, `Program.cs`, health checks, source/job state, extension

