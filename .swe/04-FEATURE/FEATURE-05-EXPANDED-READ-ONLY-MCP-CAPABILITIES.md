# Feature: Expanded Read-Only MCP Capabilities

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: shared search/context/capability services, `Api/RagMcpTools.cs`, MCP resources, contracts/tests  
ADRs: No standalone ADR exists; DESIGN.md Sections 5.3 and 19 govern the draft. The read-only adapter and restricted-vector policies require accepted ADRs before Ready.

---

## Implementation plan (step-by-step)

- [ ] Finalize schemas, URIs, limits, errors, capability/version fields, and source-authorization mapping.
- [ ] Add shared application methods for similarity, context, languages, and index capabilities.
- [ ] Implement thin tools: `rag_find_similar`, `rag_expand_context`, `rag_list_languages`, and `rag_get_index_capabilities`.
- [ ] Implement stable read-only resources for sources, source/status, chunk, and capabilities.
- [ ] Keep supplied-vector search disabled unless a separate restricted policy is explicitly accepted; keep administration unregistered.
- [ ] Add schema snapshots, authorization, malformed/boundary, cancellation, parity, and no-direct-Weaviate tests.
- [ ] Run build/test/format and live MCP/Weaviate verification; record evidence.
- [ ] Update MCP discovery, client configuration, limits, security, and compatibility documentation.

---

## Purpose

Expand agent retrieval and inspection with bounded similarity, context, language, and capability operations while preserving shared behavior, source scope, and read-only defaults.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Exact Phase 2 agent capabilities and excluded administrative behavior |
| Engineering | Stable MCP schemas/resources and thin-adapter application boundaries |
| DevOps / SRE | Limits, cancellation, audit metrics, and compatibility behavior |
| QA | Discovery/schema snapshots, auth matrix, malformed/boundary, and live-client flows |

---

## Scope

### In scope

- `rag_find_similar`, `rag_expand_context`, `rag_list_languages`, and `rag_get_index_capabilities`.
- `rag://sources`, `rag://sources/{sourceId}`, source status, chunk, and capabilities resources.
- Shared service calls, source authorization, validation, cancellation, budgets, diagnostics, and contracts.

### Out of scope

- Reindex/delete/other administrative MCP tools, arbitrary GraphQL or Weaviate passthrough, direct insertion, prompts, and default caller-supplied vectors.

---

## Business Rules

- R2.5-tools: tools are parameterized thin adapters and contain no direct Weaviate/SQLite/domain logic.
- R2.5-resources: resources expose stable addressable read-only data through shared application policy.
- R2.5-limits: validate identity, source scope, IDs, dimensions/finite values where applicable, payload, result, character, and timeout bounds before expensive work.
- Tool names and resource URIs remain stable; compatible changes are additive.
- Unauthorized resources/chunks/sources return a safe denial/not-found contract without existence leakage.
- Administrative tools remain absent from default server registration.
- Full content is logged only if a future explicit policy allows it; default logs contain tool, client, source IDs, latency, status, and count.
- Capabilities report actual enabled profiles/modes/limits, not aspirational features.

---

## User Flows

### Primary flows

1. Find similar content
   - Actor: Authorized MCP client
   - Trigger: Existing chunk ID or text query within policy.
   - Steps: Authenticate/authorize; validate; call shared similarity service; budget/redact; return.
   - Result: Source-scoped similar chunks with provenance.
2. Expand context
   - Actor: Authorized MCP client
   - Trigger: Chunk ID and bounded neighbor count.
   - Steps: Resolve authorized chunk; read same-file neighbors; clamp; return ordered result.
   - Result: Useful adjacent context without arbitrary file access.
3. Inspect capabilities/resources
   - Actor: Authorized MCP client
   - Trigger: Tool call or resource read.
   - Steps: Resolve principal/scope; query shared service; omit unauthorized aggregates; return stable schema.
   - Result: Accurate safe discovery metadata.

### Edge cases

- Unknown/ungranted chunk/source → safe deny/not-found.
- Oversized, malformed, non-finite, wrong-dimension, or over-limit input → reject before embedding/vector work.
- Cancellation/timeout → downstream work stops and structured error returns.
- Capability disabled by policy/dependency → report disabled/degraded accurately; do not expose a callable mutation path.
- Resource changed/removed between authorization and lookup → no stale unauthorized response.

---

## System Behaviour

- Entry points: MCP tools/calls and MCP resource reads on authenticated Streamable HTTP.
- Reads from: current principal/source grants, shared search/context/source/capability services.
- Writes to: metrics/audit only; no source/index mutation.
- Side effects / emitted events: safe MCP call/resource metrics and audit records.
- Idempotency: Yes; read-only operations over stable committed state.
- Error handling: structured validation/auth/not-found/timeout/dependency errors with no internals leak.
- Security / permissions: FEATURE-04 policy, redaction, response budgets, administrative registration deny.
- Feature flags / toggles: individual read-only capabilities; restricted vector search off by default; administration off.
- Performance / SLAs: MCP adapter overhead below 25 ms beyond shared service and bounded total timeout.
- Observability: tool/resource name, client, source IDs, latency, status, result/truncation count.

---

## Diagrams

~~~mermaid
flowchart LR
    A[MCP request] --> B[Authenticate and authorize]
    B --> C[Validate schema and limits]
    C --> D[Shared application service]
    D --> E[Budget and redact]
    E --> F[Tool or resource result]
~~~

---

## Verification

### Test environment

- Environment / stack: ASP.NET MCP test host, official-compatible MCP client, multiple clients/sources, disposable SQLite, live external Weaviate.
- Data and reset strategy: deterministic indexed fixtures and fresh client/grant/database/collection per suite.
- External dependencies: real MCP transport and live Weaviate/ONNX for release evidence; fakes only for bounded unit isolation.

### Test commands

- build: `dotnet build .\LocalRag.sln -c Release`
- test: `dotnet test .\LocalRag.sln -c Release`
- format: `dotnet format .\LocalRag.sln --verify-no-changes`
- coverage: no command currently defined; add before completion or record reviewer-approved omission.

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-05-001 | Expand authorized chunk context | MCP/Integration | Ordered bounded neighbors with provenance | Ordinal fixture |
| POS-05-002 | Find similar by chunk/text | MCP/Integration | Authorized ranked matches using shared service | Live Weaviate |
| POS-05-003 | Read languages/capabilities/resources | MCP | Accurate stable data limited to principal scope | Two-client fixture |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-05-001 | Ungranted source/chunk/resource | MCP | Safe deny/not-found without existence/count leak | Known IDs |
| NEG-05-002 | Oversized/malformed/non-finite/wrong-dimension input | MCP | Rejected before dependency work | Boundary payloads |
| NEG-05-003 | Administrative tool discovery/call | MCP | Tool absent or disabled; no mutation | Discovery snapshot |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-05-001 | Payload/result/character/time boundary | MCP | Deterministic clamp/rejection and truncation indicator | Exact boundaries |
| EDGE-05-002 | Cancellation during similarity/context | Integration | Downstream cancellation and structured error | Delayed adapter |
| EDGE-05-003 | Resource removed after authorization | Integration | No stale content leak | Race barrier |

### Test mapping

- Integration tests: live tools/resources, transport, cancellation, dependencies, authorization.
- API tests: MCP schema/discovery snapshots, validation/errors, compatibility.
- UI / E2E tests: Codex/compatible client invokes each read-only capability.
- Unit tests: argument validators, response budgets, resource URI parsing, adapters.
- Static analysis: verify adapter dependency graph contains only application interfaces; build/format.

### Non-functional checks

- Performance / load: concurrent MCP calls and adapter overhead measurement.
- Security / privacy: tool/resource authorization, registration allowlist, redaction, no arbitrary query passthrough.
- Observability: per-tool/resource status/latency/result assertions without content.

---

## Definition of Done

- Behaviour matches R2.5 rules and every capability is a thin shared-service adapter.
- Positive, negative, edge, discovery, authorization, cancellation, and live-client tests pass.
- Administrative tools and direct Weaviate access remain absent from default MCP.
- Static analysis and security/redaction review pass.
- Build/test/live dependency commands pass and schemas/compatibility/limits/docs are updated.
- FEATURE-05 and PLAN-02 evidence is recorded and reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `src/LocalRag.Host/Api/RagMcpTools.cs`, `Application/Contracts.cs`, `Application/RagSearchService.cs`

