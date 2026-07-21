# Feature: Client-to-Source Authorization

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: authentication/authorization, application policy, source/search/chunk services, SQLite client/grant/audit state, REST/MCP  
ADRs: No standalone ADR exists. A security ADR for client identity, token verifier storage, grant migration, revocation, and audit policy is mandatory before Ready.

---

## Implementation plan (step-by-step)

- [ ] Threat-model local clients and approve principal, credential-verifier, grant, bootstrap, revocation, cache, and audit design.
- [ ] Add versioned SQLite client/grant/audit schema and deny-by-default migration/rollback path.
- [ ] Replace implicit identity with a principal resolver and shared source-authorization service.
- [ ] Enforce policy in source listing/status, search, chunk, neighbor, capability/language aggregates, MCP resources, metrics, and UI contracts.
- [ ] Add credential creation/rotation/revocation administration through an explicitly approved local workflow outside default MCP.
- [ ] Add REST/MCP parity, leak-resistance, revocation, concurrency, and migration tests.
- [ ] Run build/test/format and security/redaction inspection; record evidence.
- [ ] Update authentication, configuration, bootstrap, rotation, recovery, and audit documentation.

---

## Purpose

Ensure every LocalRAG client sees only explicitly granted sources and that source scope is enforced once in shared application policy rather than duplicated in adapters.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Local-client authorization behavior and bootstrap/revocation UX |
| Engineering | Principal/grant contracts, enforcement coverage, migration, and caching |
| DevOps / SRE | Credential rotation/recovery, audit/retention, and diagnostics permission |
| QA | Cross-adapter policy matrix, leak tests, races, and migration scenarios |

---

## Scope

### In scope

- Persisted client identities, non-recoverable credential verifiers/protected references, explicit source grants, revocation, and audit metadata.
- Shared effective-source policy for all source-derived reads and diagnostics.
- Safe bootstrap of current local extension/MCP clients under an accepted migration.

### Out of scope

- OIDC/OAuth, TLS/network hosting, tenant namespaces, enterprise roles, source writes through MCP, or non-loopback binding.

---

## Business Rules

- R2.4-deny_default: unknown, invalid, revoked, ungranted, removed, hidden, or paused scope is denied/omitted as the operation contract requires.
- R2.4-shared_policy: every adapter calls one authorization service before source-derived reads.
- R2.4-token_safety: no recoverable bearer-token plaintext is stored; comparisons remain constant-time.
- Explicit source IDs are intersected with grants; omitted source IDs mean all currently granted/visible sources, never all registered sources.
- Chunk IDs and aggregates cannot be used to infer ungranted source existence.
- Grant/revocation changes are durable, audited, and effective within a documented bounded cache interval.
- Local-first/read-only defaults and loopback enforcement remain unchanged.
- Audit/log records exclude credentials, absolute paths, queries, and full content.

---

## User Flows

### Primary flows

1. Authorized search
   - Actor: Local extension or MCP client
   - Trigger: Request with a valid credential and source scope.
   - Steps: Authenticate; resolve principal; load grants; intersect visible scope; execute shared service; redact response.
   - Result: Only granted-source results are returned.
2. Credential rotation
   - Actor: Approved local administrator workflow
   - Trigger: Rotate or revoke a client credential.
   - Steps: Create verifier/reference; distribute secret through approved channel; revoke old credential; audit.
   - Result: New credential works and old credential fails within the documented bound.

### Edge cases

- Known but ungranted source ID → safe denial without existence/content disclosure.
- Source removed/paused/hidden during a request → no later unauthorized chunk/neighbor emission.
- Grant revoked while cached → bounded invalidation behavior is documented and tested.
- Migration from current single token → explicit reviewed bootstrap, never silent allow-all for future clients.
- Invalid credential lengths/encodings → safe failure without timing-sensitive string handling.

---

## System Behaviour

- Entry points: authentication handler and authorization service invoked by all application query services/endpoints/MCP adapters.
- Reads from: client/verifier/grant records, source status/visibility, current principal.
- Writes to: client/grant/audit state only through approved administration; normal retrieval remains read-only.
- Side effects / emitted events: safe auth success/failure, policy decision, operation/source IDs, latency, revocation events.
- Idempotency: grant/rotation commands use stable IDs/idempotency rules; policy reads are side-effect-free except audit.
- Error handling: indistinguishable safe denial where existence disclosure matters; structured forbidden/unauthenticated contracts.
- Security / permissions: deny by default, least privilege, constant-time verifier comparison, no plaintext token persistence.
- Feature flags / toggles: no bypass flag; legacy migration is time-bounded and removed after transition.
- Performance / SLAs: authorization overhead is bounded and included in search/MCP latency measurements.
- Observability: principal/client ID, decision, operation, source IDs, latency; no secret/content.

---

## Diagrams

~~~mermaid
flowchart LR
    A[Credential] --> B[Authenticate principal]
    B --> C[Load source grants]
    C --> D[Intersect requested scope]
    D --> E[Shared application service]
    E --> F[Redacted response]
~~~

---

## Verification

### Test environment

- Environment / stack: ASP.NET/MCP test host, disposable SQLite, multiple clients/sources/statuses, live retrieval for end-to-end parity.
- Data and reset strategy: generate per-test credentials, store only approved verifier form, seed grants, dispose database/collection after run.
- External dependencies: live Weaviate/ONNX for end-to-end source leakage tests; fakes for pure policy unit tests.

### Test commands

- build: `dotnet build .\LocalRag.sln -c Release`
- test: `dotnet test .\LocalRag.sln -c Release`
- format: `dotnet format .\LocalRag.sln --verify-no-changes`
- coverage: no command currently defined; add before completion or record reviewer-approved omission.

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-04-001 | Client granted two of three sources | API/MCP | List/search/chunk/context expose only two | Cross-adapter matrix |
| POS-04-002 | Same principal/input via REST and MCP | Integration | Equivalent effective scope and results | Live retrieval |
| POS-04-003 | Credential rotation | Integration | New works; old revoked within bound; audit safe | Generated credentials |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-04-001 | Explicit ungranted source | API/MCP | Denied without existence/content leak | Known source |
| NEG-04-002 | Chunk/resource/aggregate from ungranted source | API/MCP | Denied/omitted consistently | Stable IDs |
| NEG-04-003 | Invalid/revoked credential and storage inspection | Integration/Security | Authentication fails; no plaintext secret stored/logged | DB/log scan |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-04-001 | Revocation during active/cached use | Integration | Subsequent request denied within documented bound | Clock/cache seam |
| EDGE-04-002 | Source removed/paused during request | Integration | No late result/context leak | Concurrency barrier |
| EDGE-04-003 | Legacy-token migration and rollback | Integration | Reviewed bootstrap only; no accidental allow-all | Old/new schema fixtures |

### Test mapping

- Integration tests: migrations, authentication, grants, revocation, audit, live source-scoped retrieval.
- API tests: every REST/MCP/resource/diagnostic operation and error contract.
- UI / E2E tests: client bootstrap/rotation failure and authorized source controls.
- Unit tests: verifier, scope intersection, status/visibility rules, cache invalidation.
- Static analysis: solution build/format plus secret-pattern and serialized-contract inspection.

### Non-functional checks

- Performance / load: authorization latency/cache under concurrent search and many grants.
- Security / privacy: threat-model checklist, timing-safe verifier, database/log/content leak scans.
- Observability: audit allowlist and denied/allowed decision assertions.

---

## Definition of Done

- Behaviour matches R2.4 rules and all public data paths use shared policy.
- All positive, negative, edge, migration, concurrency, and leak scenarios pass.
- Security ADR and threat-model review are accepted.
- Static analysis and secret/redaction inspection pass with no unresolved issue.
- Build/test/live retrieval commands pass; contracts, bootstrap, rotation, recovery, and rollback are documented.
- FEATURE-04 and PLAN-02 evidence is recorded and explicitly reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `Authentication/LocalTokenAuthenticationHandler.cs`, `Application/RagSearchService.cs`, `Api/RagMcpTools.cs`, SQLite source registry
