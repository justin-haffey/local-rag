# High-Level Design Document: [System / Feature Name]

**Version:** [0.1]
**Date:** [YYYY-MM-DD]
**Status:** [Draft | Proposed | Approved | Superseded]
**Primary Stack:** [Languages, frameworks, persistence, and integration protocols]

---

## 1. Executive Summary

[State the problem, the proposed system, the users it serves, and the primary outcome. Summarize the major integration surfaces and operating model in a few paragraphs.]

[State where data is processed and stored, including any local-first, hosted, or hybrid boundary.]

---

## 2. Objectives

### 2.1 Primary Objectives

1. [Objective with a measurable user or system outcome.]
2. [Objective.]
3. [Objective.]

### 2.2 Non-Goals for This Release

- [Explicitly excluded capability.]
- [Explicitly excluded capability.]
- [Explicitly excluded capability.]

---

## 3. Principal Use Cases

### 3.1 [Use Case Name]

1. [Actor initiates an action.]
2. [System validates inputs and policy.]
3. [System performs the core work.]
4. [Actor receives an observable outcome.]

### 3.2 [Use Case Name]

1. [Step.]
2. [Step.]
3. [Step.]

### 3.3 [Use Case Name]

[Describe the user-visible workflow, relevant filters or choices, and expected result.]

---

## 4. Architecture Overview

```text
[Client or caller]
        |
        v
[Interface / adapter] ----> [Authentication and authorization]
        |
        v
[Application services / domain layer]
   |             |             |
   v             v             v
[Input/store] [State store] [External dependency]
```

### 4.1 Architectural Principles

- [Principle, such as one core application layer with multiple thin adapters.]
- [Principle, such as least privilege or data-locality by default.]
- [Principle, such as explicit contracts and bounded resource use.]

### 4.2 Context and Trust Boundaries

| Boundary                    | Trusted inputs        | Untrusted inputs          | Enforcement                          |
| --------------------------- | --------------------- | ------------------------- | ------------------------------------ |
| [Client to API]             | [Identity / token]    | [Request fields]          | [Authentication, validation, limits] |
| [Application to dependency] | [Configured endpoint] | [Dependency response]     | [Timeouts, schema checks, retries]   |
| [Content boundary]          | [Policy metadata]     | [User or indexed content] | [Treat as data, preserve provenance] |

---

## 5. Major Components

### 5.1 [Client, Extension, or User Interface]

**Technology:** [Technology and version range.]

**Responsibilities**

- [Responsibility.]
- [Responsibility.]
- [Responsibility.]

**Commands, routes, or entry points**

- `[command-or-route]` — [Purpose.]
- `[command-or-route]` — [Purpose.]

**Communication with backend or dependencies**

- [Transport, authentication method, request lifecycle, and failure behavior.]

### 5.2 [Backend Host or Primary Service]

**Technology:** [Runtime, hosting model, and version range.]

[Describe the long-running responsibilities, public interfaces, background work, and deployment modes.]

**Hosting modes**

1. [Recommended mode and rationale.]
2. [Optional mode and constraints.]

### 5.3 [External Integration or Protocol Adapter]

**Purpose:** [Why this adapter exists and which callers use it.]

**Transport and endpoint**

- [Transport and protocol version.]
- `[endpoint or connection convention]`
- [Binding, encryption, and exposure policy.]

**Capability model**

| Capability      | Purpose   | Default access         | Limits / policy                           |
| --------------- | --------- | ---------------------- | ----------------------------------------- |
| `[operation]` | [Purpose] | [Read / Write / Admin] | [Scope, timeout, result or payload limit] |
| `[operation]` | [Purpose] | [Read / Write / Admin] | [Scope, timeout, result or payload limit] |

**Representative contract**

```json
{
  "requestField": "[value]",
  "scope": ["[authorized-scope-id]"],
  "limit": 20,
  "filters": {
    "[filter]": "[value]"
  }
}
```

```json
{
  "results": [
    {
      "id": "[stable-id]",
      "metadata": {},
      "content": "[content or reference, subject to policy]"
    }
  ],
  "diagnostics": {
    "truncated": false,
    "elapsedMilliseconds": 0
  }
}
```

**Security boundaries**

1. [Authenticate before operation invocation.]
2. [Intersect requested scope with the caller's authorized scope.]
3. [Enforce payload, result, and execution-time budgets.]
4. [Redact or withhold fields/content according to policy.]
5. [Exclude elevated or destructive operations unless separately enabled.]

### 5.4 [State Store or Source Registry]

**Purpose:** [Durable facts held here and why this store is authoritative.]

| Entity / table / collection | Key fields                        | Retention and lifecycle             |
| --------------------------- | --------------------------------- | ----------------------------------- |
| `[entity]`                | [Identifiers and critical fields] | [Create, update, cleanup, recovery] |
| `[entity]`                | [Identifiers and critical fields] | [Create, update, cleanup, recovery] |

### 5.5 [Ingestion, Discovery, or Change-Detection Subsystem]

- **Initial discovery:** [Eligibility, enumeration, and checkpoint approach.]
- **Change detection:** [Events, polling, reconciliation, or other mechanism.]
- **Stability and idempotency:** [Debounce, content hashes, versions, and duplicate handling.]
- **Exclusions:** [Default and configurable exclusions, including sensitive content.]

### 5.6 [Extraction and Normalization]

- **Supported inputs:** [Formats, encodings, and version assumptions.]
- **Normalization:** [Canonicalization that affects identity or matching.]
- **Unsupported/binary content:** [Explicit skip, safe extractor, or failure behavior.]
- **Provenance:** [How origin, location, version, and hash are retained.]

### 5.7 [Core Processing or Domain Engine]

[Describe how the system transforms input into durable or queryable results. State stable identifiers, size or concurrency limits, error handling, and extension points.]

| Input   | Processing       | Output   | Invariants                                            |
| ------- | ---------------- | -------- | ----------------------------------------------------- |
| [Input] | [Transformation] | [Output] | [Identity, ordering, validation, or policy guarantee] |
| [Input] | [Transformation] | [Output] | [Invariant]                                           |

### 5.8 [Model, Search, or Decision Service] *(if applicable)*

- **Modes:** [Supported modes and selection criteria.]
- **Profile/version:** [Pinned model, dimensions, tokenizer, or ruleset.]
- **Validation:** [Startup/probe validation and invalid-output behavior.]
- **Batching and caching:** [Boundaries, invalidation, and resource limits.]

### 5.9 [External Data Store or Service] *(if applicable)*

- **Contract:** [Schema, collection, queue, or API contract.]
- **Read paths:** [Query modes, filters, ranking, or consistency expectation.]
- **Write paths:** [Upsert/delete semantics, partial failure handling, and commit point.]
- **Availability:** [Readiness dependency, degraded behavior, and recovery.]

---

## 6. Processing Pipeline

```text
[Input event or request]
        |
        v
[Validate and authorize]
        |
        v
[Normalize and deduplicate]
        |
        v
[Core processing]
        |
        v
[Persist or call dependency]
        |
        v
[Return outcome and emit observability data]
```

### 6.1 Idempotency and Consistency

[Define stable identities, version/hash checks, upsert semantics, checkpoints, compensation, and cleanup. State explicitly where atomicity is not guaranteed.]

### 6.2 Backpressure and Scheduling

- [Queue/channel bounds.]
- [Concurrency limits and prioritization.]
- [Cancellation and overload behavior.]

---

## 7. API and Contract Design

### 7.1 Public Interfaces

```text
[METHOD] /[route]                 [Purpose]
[METHOD] /[route]/{id}            [Purpose]
[METHOD] /health/[readiness]      [Purpose]
```

### 7.2 Versioning and Compatibility

- [Route, protocol, or schema version policy.]
- [Additive-change, deprecation, and migration rules.]
- [Compatibility test or consumer contract.]

### 7.3 Validation and Error Contract

| Condition                | Status / error code | Safe client-facing response         | Server action                      |
| ------------------------ | ------------------- | ----------------------------------- | ---------------------------------- |
| [Invalid input]          | [Code]              | [Actionable, non-sensitive message] | [Reject before work]               |
| [Unauthorized scope]     | [Code]              | [Do not reveal inaccessible data]   | [Audit and reject/omit]            |
| [Dependency unavailable] | [Code]              | [Retry guidance if appropriate]     | [Bounded retry or degraded status] |

---

## 8. Data Flows

### 8.1 [Primary Write or Registration Flow]

```text
[Actor]
  |
  v
[Interface] --> [Validation and policy] --> [Service] --> [State / dependency]
  |
  v
[Observable status or result]
```

### 8.2 [Incremental or Background Flow]

```text
[Trigger] --> [Debounce / queue] --> [Process] --> [Persist] --> [Reconcile]
```

### 8.3 [Primary Query or Read Flow]

```text
[Caller] --> [Authenticate] --> [Validate scope] --> [Application service]
    --> [Policy/redaction] --> [Structured result]
```

---

## 9. Security and Privacy

### 9.1 Default Deployment Posture

- [Default network binding and encryption posture.]
- [Credential generation, storage, rotation, and revocation.]
- [Minimum privileges for processes and data stores.]

### 9.2 Hosted or Shared Deployment

- [Required identity provider or trust mechanism.]
- [Tenant, project, or source isolation model.]
- [Rate limits, timeouts, network policy, and audit controls.]

### 9.3 Content Protection

- [Sensitive-file/default exclusion policy.]
- [Secret detection/redaction policy and limitations.]
- [Absolute-path, content, and metadata exposure rules.]

### 9.4 Untrusted Content and Prompt Injection

[Explain how retrieved, imported, or user-provided content is labeled and treated as data. Confirm it cannot override policy, authorization, tool selection, or destructive-action controls.]

---

## 10. Reliability and Recovery

| Failure                        | Required behavior             | Retry / stop condition     | Recovery signal     |
| ------------------------------ | ----------------------------- | -------------------------- | ------------------- |
| [Host restart]                 | [Restore/reconcile work]      | [Bounded startup behavior] | [Health/status]     |
| [Dependency unavailable]       | [Degrade or queue safely]     | [Backoff cap]              | [Actionable error]  |
| [Partial write]                | [Do not commit false success] | [Replay-safe retry only]   | [Audit/job state]   |
| [Invalid schema/configuration] | [Block unsafe work]           | [No automatic retry]       | [Readiness failure] |

### Graceful Shutdown

1. [Stop accepting new work.]
2. [Drain or cancel active work within a time budget.]
3. [Persist safe checkpoints.]
4. [Release resources without losing recovery state.]

---

## 11. Observability

### Logging

- [Correlation, actor, operation, scope-safe identifiers, latency, outcome, and retry count.]
- [Fields that must never be logged, including source content or credentials.]

### Metrics

- [Throughput, queue depth, latency, failure, retry, and truncation metrics.]
- [Resource and dependency health metrics.]

### Health Checks

| State       | Meaning                            | Required checks | Client behavior         |
| ----------- | ---------------------------------- | --------------- | ----------------------- |
| Ready       | [All required dependencies usable] | [Checks]        | [Normal operation]      |
| Degraded    | [Limited safe capability]          | [Checks]        | [Surface limitation]    |
| Unavailable | [Core operation unsafe]            | [Checks]        | [Reject or fail safely] |

---

## 12. Performance Targets

| Scenario                | Target                      | Measurement conditions              |
| ----------------------- | --------------------------- | ----------------------------------- |
| [Interactive request]   | [p95 target]                | [Dataset, hardware, and exclusions] |
| [Background processing] | [Throughput/latency target] | [Dataset and concurrency]           |
| [Resource use]          | [Memory/CPU bound]          | [Load condition]                    |

[Targets are engineering goals unless a service-level agreement is explicitly approved.]

---

## 13. Configuration Model

```yaml
server:
  bindAddress: [loopback-or-approved-interface]
  port: [port]

security:
  requireAuthentication: true

processing:
  maxConcurrentWork: [number]
  requestTimeoutSeconds: [number]

dependency:
  endpoint: [non-secret-endpoint]
```

### Per-Scope Overrides

- [Allowed per-project, per-source, per-tenant, or per-user override.]
- [Override precedence and validation.]
- [Settings that must remain global or administrator-controlled.]

---

## 14. Deployment

### 14.1 Runtime Topology

[Describe the supported deployment topology, ownership of each dependency, network interfaces, persistent volumes or stores, and configuration/secrets boundary.]

### 14.2 Packaging and Upgrade

- [Supported packages or installation artifacts.]
- [Configuration migration and compatibility process.]
- [Rollback and version-pinning approach.]

---

## 15. Suggested Solution Structure

```text
[solution-root]/
  src/
    [Host or interface]/
    [Application]/
    [Domain]/
    [Infrastructure]/
    [Contracts]/
  tests/
    [Unit tests]/
    [Integration tests]/
    [End-to-end tests]/
  deploy/
    [deployment assets]/
```

[Note responsibility boundaries and the rule that adapters do not bypass application policy or domain services.]

---

## 16. Testing Strategy

### Unit Tests

- [Deterministic validation, normalization, policy, and domain behavior.]

### Integration Tests

- [Persistence, external-service, transport, authentication, and migration behavior.]

### End-to-End Tests

1. [Representative user workflow.]
2. [Incremental/change or recovery workflow.]
3. [Authorization and safe failure workflow.]

### Load and Resilience Tests

- [Representative volume/concurrency scenario.]
- [Dependency interruption and restart scenario.]
- [Resource-bound and oversized-input scenario.]

---

## 17. Risks and Mitigations

| Risk   | Likelihood        | Impact            | Mitigation         | Residual risk / owner      |
| ------ | ----------------- | ----------------- | ------------------ | -------------------------- |
| [Risk] | [Low/Medium/High] | [Low/Medium/High] | [Concrete control] | [Remaining risk and owner] |
| [Risk] | [Low/Medium/High] | [Low/Medium/High] | [Concrete control] | [Remaining risk and owner] |

---

## 18. Delivery Phases

[Ensure phase coverage of the entire DESIGN document]

### Phase 1: [Minimum Viable Outcome]

- [Required capability.]
- [Required capability.]
- [Required security, observability, and verification gate.]

### Phase 2: [Hardening or Quality]

- [Capability.]
- [Capability.]

### Phase 3: [Advanced Capability]

- [Capability.]
- [Capability.]

---

## 19. Architecture Decisions

[List initial ADR's]

### ADR-001: [Decision Title]

**Decision:** [What is being decided.]

**Rationale:** [Why this approach best meets the objectives and constraints.]

**Consequences:** [Benefits, costs, migration needs, and follow-up work.]

**Details:** [Link to the detailed ADR in `.swe\02-ADR\ADR-###- .md`

### ADR-002: [Decision Title]

**Decision:** [What is being decided.]

**Rationale:** [Why.]

**Consequences:** [Effects.]

---

## 20. Acceptance Criteria for [Release / MVP]

The release is complete when:

1. [Observable user or operational outcome.]
2. [Core workflow outcome.]
3. [Security or authorization outcome.]
4. [Reliability/recovery outcome.]
5. [Performance or capacity outcome.]
6. [Automated and/or live validation evidence required for release.]

---

## 21. Immediate Next Steps

1. [Resolve a prerequisite, decision, or dependency.]
2. [Create the contract, interface, or implementation skeleton.]
3. [Implement the highest-risk vertical slice.]
4. [Add validation and release evidence.]

---

## 22. Reference Material

- [Authoritative specification or documentation — URL and version/date.]
- [Architecture decision, issue, or relevant internal document.]
- [Benchmark, threat model, or evaluation evidence.]

---

## 23. Conclusion

[Restate the proposed design, its bounded scope, the principal protections or trade-offs, and the decision or implementation action requested.]

## Filing checklist

- [ ] File saved under `.swe/01-DESIGN/DESIGN.md` (not in `.swe/swe/templates/`).
- [ ] Status reflects real state (`Proposed`, `Accepted`, `Rejected`, `Superseded`).
- [ ] Links to related features, tests, and ADRs are filled in.
- [ ] Major Components contains at least one Mermaid diagram for each component.
- [ ] Related ADRs added or updated in `.swe/02-ADR/*`.
