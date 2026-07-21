# PLAN-01: — [Phase or Plan Name] Plan

> TEMPLATE ONLY — remove this note and replace every placeholder before saving a real plan under `.swe/03-PLAN/`.

**Plan ID:** PLAN-##
**Phase:** Phase # — [Name]
**Status:** Draft | Ready | In Progress | Blocked | Complete
**Owner:** [Person / team]
**Target release:** [Version or date]
**Last updated:** [YYYY-MM-DD]

Related documents:

- Design: `.swe/01-DESIGN/DESIGN.md`
- ADRs: `.swe/02-ADR/`
- Features: `.swe/04-FEATURE/`
- Architecture: [link]

---

## Summary

[State the outcome this phase delivers, the users or systems affected, and the primary constraints. Keep this section short enough to orient a reviewer before the implementation detail.]

### Phase objectives

1. [Measurable objective.]
2. [Measurable objective.]
3. [Measurable objective.]

### Non-goals

- [Capability explicitly deferred to a later phase.]
- [Capability explicitly excluded from this release.]

---

## Scope and boundaries

### In scope

- [System, workflow, contract, or operational capability.]

### Out of scope

- [System, workflow, or capability not changed by this phase.]

### Constraints and invariants

- [Compatibility, security, data, platform, or performance constraint.]
- [Invariant that implementation and tests must preserve.]

---

## Implementation changes

Describe the implementation as bounded workstreams. Each item should name the responsibility, the affected modules or files, and the observable result.

1. **[Workstream name]**

   - Change: [What will be added or changed.]
   - Boundaries: [Interfaces, services, stores, or clients involved.]
   - Configuration/data: [Keys, migrations, schemas, or compatibility notes.]
   - Verification: [Test IDs and command or inspection that proves it.]
2. **[Workstream name]**

   - Change:
   - Boundaries:
   - Configuration/data:
   - Verification:

### Delivery sequence

- [ ] [Prerequisite or contract first.]
- [ ] [Highest-risk vertical slice.]
- [ ] [Integration and recovery behavior.]
- [ ] [Documentation and release evidence.]

---

## Features

> This section is the feature registry for the phase. Keep one block per independently verifiable capability. Do not put an entire phase into one feature row. Move deep, feature-specific detail to `.swe/04-FEATURE/<feature>.md` and link it here.

### Feature registry

| Feature ID | Feature name            | Priority | Detailed plan                    | Requirements | Verification             | Status      |
| ---------- | ----------------------- | -------- | -------------------------------- | ------------ | ------------------------ | ----------- |
| FEAT-01    | [Short capability name] | Must     | `.swe/04-FEATURE/[feature].md` | R#.#-*       | POS-/NEG-/EDGE-/INT- IDs | Not started |

### Feature blocks

Repeat this block for each feature in the registry.

#### FEAT-01 — [Feature name]

**Purpose:** [User or system outcome.]
**Detailed plan:** `.swe/04-FEATURE/[feature-name].md`

**Scope**

- In scope: [Capability and boundary.]
- Out of scope: [Explicit exclusions.]
- Dependencies: [Other features, services, migrations, or decisions.]

**Requirements**

Use stable IDs in the form `R#.#-requirement_name`. Names should be short, lowercase, and stable enough for code, tests, and review references.

| Requirement ID        | Requirement                        | Priority              | Source / rationale                  | Verification IDs |
| --------------------- | ---------------------------------- | --------------------- | ----------------------------------- | ---------------- |
| R#.#-requirement_name | [Testable behavior or constraint.] | Must / Should / Could | [Design section, ADR, or decision.] | POS-001, NEG-001 |

**User and system flows**

1. **[Primary flow]** — [Actor] triggers [entry point]; the system [steps]; outcome is [observable result].
2. **[Failure or recovery flow]** — [Failure]; system response; retry, status, and user guidance.
3. **[Boundary flow]** — [Limit, authorization, concurrency, or malformed input]; expected safe behavior.

**Acceptance criteria**

- [ ] [Observable success criterion linked to requirement IDs.]
- [ ] [Negative or forbidden behavior is rejected safely.]
- [ ] [Recovery, idempotency, and persistence behavior is proven.]
- [ ] [Security, redaction, limits, and observability requirements are proven.]

**Implementation touchpoints**

| Layer                    | Modules / files       | Responsibility                        | Contract or migration impact     |
| ------------------------ | --------------------- | ------------------------------------- | -------------------------------- |
| Domain / application     | [Paths or namespaces] | [Core behavior.]                      | [Interface or invariant.]        |
| Adapter / API / UI       | [Paths or routes]     | [Caller-facing behavior.]             | [Request/response or command.]   |
| Persistence / dependency | [Paths or schema]     | [Durability or external integration.] | [Migration/schema/availability.] |

**Verification mapping**

| Verification ID | Level                         | Scenario                                      | Expected result        | Test / command          |
| --------------- | ----------------------------- | --------------------------------------------- | ---------------------- | ----------------------- |
| POS-001         | Unit / Integration / API / UI | [Happy path.]                                 | [Observable outcome.]  | [Test name or command.] |
| NEG-001         | Unit / Integration / API / UI | [Invalid, unauthorized, or unavailable path.] | [Safe failure.]        | [Test name or command.] |
| EDGE-001        | Unit / Integration / API / UI | [Boundary, retry, restart, or race.]          | [Invariant preserved.] | [Test name or command.] |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

---

## Cross-cutting requirements

| Area               | Requirement                                                                               | Verification                  |
| ------------------ | ----------------------------------------------------------------------------------------- | ----------------------------- |
| Security / privacy | [Authentication, authorization, redaction, secret handling, and untrusted-content rules.] | [Test or inspection.]         |
| Reliability        | [Restart, retry, idempotency, reconciliation, and partial-failure behavior.]              | [Test or fault injection.]    |
| Performance        | [Concurrency, payload, timeout, memory, or latency budgets.]                              | [Benchmark or bounded test.]  |
| Observability      | [Safe logs, metrics, traces, and health/readiness signals.]                               | [Assertions or inspection.]   |
| Compatibility      | [Versioning, migration, rollback, and client compatibility.]                              | [Migration or contract test.] |

---

## Test plan

### Test environment

- Runtime / platform: [Local, CI, container, staging, or production-like environment.]
- Data and reset strategy: [Fixtures, seed data, migrations, cleanup.]
- External dependencies: [Real, fake, sandbox, or explicitly configured endpoint.]
- Required environment variables / secrets: [Names only; never commit values.]

### Test commands

- Build: [Command from `AGENTS.md`]
- Unit tests: [Command]
- Integration tests: [Command and required endpoint/configuration]
- End-to-end tests: [Command]
- Format / lint / static analysis: [Command]
- Coverage or release smoke test: [Command, or remove if not applicable]

### Scenario matrix

| ID       | Scenario                                               | Level                 | Expected result       | Evidence            |
| -------- | ------------------------------------------------------ | --------------------- | --------------------- | ------------------- |
| POS-001  | [Primary happy path.]                                  | Unit / Int / API / UI | [Outcome.]            | [Test or artifact.] |
| NEG-001  | [Invalid or unauthorized request.]                     | Unit / Int / API / UI | [Safe rejection.]     | [Test or artifact.] |
| EDGE-001 | [Boundary, restart, race, or dependency interruption.] | Unit / Int / API / UI | [Invariant/recovery.] | [Test or artifact.] |

### Release gate

- [ ] All feature requirements map to passing tests.
- [ ] Regression suite is green.
- [ ] Live dependencies, when required, were tested explicitly rather than skipped.
- [ ] Security, readiness, limits, and rollback evidence is recorded.
- [ ] Known failures, deferred work, and residual risks are documented.

---

## Acceptance criteria

The phase is complete only when:

1. [Primary user-visible outcome.]
2. [Required integration or operational outcome.]
3. [Security and authorization outcome.]
4. [Reliability and recovery outcome.]
5. [Verification and release-evidence outcome.]

---

## Assumptions, risks, and decisions

### Assumptions

- [Assumption and how to revisit it.]

### Risks and mitigations

| Risk   | Likelihood          | Impact              | Mitigation          | Owner / residual risk       |
| ------ | ------------------- | ------------------- | ------------------- | --------------------------- |
| [Risk] | Low / Medium / High | Low / Medium / High | [Concrete control.] | [Owner and remaining risk.] |

### Open decisions

| Decision           | Options         | Decision owner | Due date     | Result / linked ADR |
| ------------------ | --------------- | -------------- | ------------ | ------------------- |
| [Decision needed.] | [Option A / B.] | [Owner.]       | [YYYY-MM-DD] | [Pending or link.]  |

---

## References

- [Design section or requirement source.]
- [ADR, feature document, issue, or test evidence.]
- [Authoritative external specification, version, and URL.]
