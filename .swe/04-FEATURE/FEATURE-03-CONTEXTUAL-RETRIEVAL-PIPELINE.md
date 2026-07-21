# Feature: Contextual Retrieval Pipeline

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: `Application/RagSearchService.cs`, search/domain contracts, `Infrastructure/Weaviate`, SQLite chunk state, REST/MCP adapters  
ADRs: No standalone ADR exists; DESIGN.md Section 5.9 is the draft source. Ranking/reranker policy must be accepted before Ready.

---

## Implementation plan (step-by-step)

- [ ] Approve relevance corpus/thresholds, diversification policy, candidate multiplier, reranker policy, neighbor defaults, and budgets.
- [ ] Split shared retrieval into validation/scope, candidate retrieval, shaping, optional reranking, context expansion, and budgeting services.
- [ ] Extend additive REST/application contracts for modes, filters, diagnostics, and context options.
- [ ] Implement deterministic deduplication/diversification and ordinal neighbor lookup.
- [ ] Implement optional approved local reranker behind disabled-by-default configuration, timeout, and deterministic fallback.
- [ ] Add parity, authorization, relevance, latency, cancellation, and boundary tests.
- [ ] Run build/test/format, real inference, live Weaviate evaluation, and load checks; record evidence.
- [ ] Update retrieval, configuration, API/MCP, and performance documentation.

---

## Purpose

Return more useful, less repetitive, source-authorized results with nearby context and optional local reranking while maintaining predictable latency and output limits.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Search behavior and measurable relevance/latency acceptance |
| Engineering | Ordered pipeline stages, contracts, fallback, and policy boundaries |
| DevOps / SRE | Model/config dependencies, timeouts, metrics, and degradation behavior |
| QA | Judged corpus, parity, source-boundary, cancellation, and load scenarios |

---

## Scope

### In scope

- Wider candidate retrieval; score shaping; near-duplicate removal; file/symbol diversification.
- Bounded ordinal neighbor expansion within the same file/source.
- Optional approved local reranker, disabled by default, with model/version provenance and fallback.
- Additive filters, modes, context, diagnostics, and output budgets shared across adapters.

### Out of scope

- LLM answer synthesis, remote reranking by default, parent-child retrieval, dependency graphs, or arbitrary caller-supplied vectors.

---

## Business Rules

- R2.3-diversify: shaping is deterministic for equal inputs/data and never widens authorized source/filter scope.
- R2.3-neighbors: neighbors share source and file, follow ordinal order, and obey count/character/token budgets.
- R2.3-rerank: reranking is optional, local-only unless a future explicit decision changes that, disabled by default, timed, versioned, and fallible.
- Validation and authorization occur before query embedding/vector-store work.
- Adapter inputs map to one application contract; REST and MCP cannot implement separate ranking.
- Reranker timeout/failure returns the deterministic non-reranked result unless cancellation was requested.
- Final results retain provenance, scores/diagnostics allowed by policy, and truncation status.

---

## User Flows

### Primary flows

1. Contextual search
   - Actor: Authorized client
   - Trigger: Search with sources, mode, filters, limit, and context options.
   - Steps: Validate/scope; embed if required; retrieve wider candidates; shape; optionally rerank; expand; budget.
   - Result: Diverse authorized results with bounded context and diagnostics.
2. Expand an existing result
   - Actor: Authorized client
   - Trigger: Chunk ID plus neighbor count.
   - Steps: Resolve chunk under policy; query same-file ordinals; clamp; return ordered context.
   - Result: Stable context without arbitrary file access.

### Edge cases

- Reranker timeout/unavailable → non-reranked ordering plus safe diagnostic.
- First/last/stale/deleted chunk → clamp/omit missing neighbors safely.
- Search while indexing → return committed searchable state and accurate truncation/diagnostics.
- Cancellation → propagate through embedding, vector retrieval, reranking, and expansion.
- Repetitive file dominates candidates → configured diversification enforces bounded file/symbol representation.

---

## System Behaviour

- Entry points: `IRagSearchService.SearchAsync`, context/similarity methods used by REST and MCP.
- Reads from: client source scope, embedding service, vector candidates, SQLite chunk ordinals, ranking configuration.
- Writes to: metrics/logs only; search remains read-only.
- Side effects / emitted events: latency by stage/mode, candidate/result/truncation counts, reranker outcome.
- Idempotency: Read-only and deterministic for stable data/config/model.
- Error handling: validate first; structured timeouts/cancellation; optional-stage fallback; dependency errors remain actionable.
- Security / permissions: source grants and path/content redaction before result emission.
- Feature flags / toggles: reranker disabled by default; diversification/context bounded and configurable.
- Performance / SLAs: search p95 below 300 ms excluding cold model load; MCP adapter overhead below 25 ms.
- Observability: stage timings and counts, no full query/chunk content by default.

---

## Diagrams

~~~mermaid
flowchart LR
    A[Validate and authorize] --> B[Retrieve candidates]
    B --> C[Deduplicate and diversify]
    C --> D[Optional local rerank]
    D --> E[Expand neighbors]
    E --> F[Enforce budgets]
~~~

---

## Verification

### Test environment

- Environment / stack: application/unit tests, API/MCP test host, disposable SQLite, live external Weaviate, real BGE ONNX, approved judged corpus.
- Data and reset strategy: deterministic seeded chunks/vectors plus fresh live collection per evaluation run.
- External dependencies: real ONNX/Weaviate for release relevance and latency evidence; approved local reranker only if selected.

### Test commands

- build: `dotnet build .\LocalRag.sln -c Release`
- test: `dotnet test .\LocalRag.sln -c Release`
- format: `dotnet format .\LocalRag.sln --verify-no-changes`
- coverage: no command currently defined; add before completion or record reviewer-approved omission.

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-03-001 | Repetitive candidate set | Integration | Deterministic relevant multi-file/symbol diversity | Judged corpus |
| POS-03-002 | Search with neighbor expansion | API/MCP | Ordered same-file context within budgets | Ordinal fixtures |
| POS-03-003 | Approved local reranker enabled | Integration | Version recorded; agreed relevance gain/latency bound | Real model |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-03-001 | Neighbor belongs to ungranted source/file | API/MCP | Denied/omitted without leak | Mixed-scope seed |
| NEG-03-002 | Reranker timeout/failure | Integration | Deterministic base ordering, safe diagnostic | Fault adapter |
| NEG-03-003 | Invalid mode/filter/limit/context | API/MCP | Rejected before embedding/database work | Boundary requests |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-03-001 | First/last/stale chunk, large neighbor count | Unit/Integration | Clamp to current same-file bounds | Ordinal gaps |
| EDGE-03-002 | Cancellation during each pipeline stage | Integration | Downstream work stops and structured cancellation returns | Controlled delays |
| EDGE-03-003 | Search concurrent with reindex | Integration/Load | Committed results remain valid and responsive | Live Weaviate |

### Test mapping

- Integration tests: live candidate retrieval, relevance corpus, reranker/fallback, concurrency, cancellation.
- API tests: modes/filters/context/budgets and REST/MCP parity.
- UI / E2E tests: context rendering/navigation and truncated/degraded states.
- Unit tests: validation, scoring, deduplication, diversification, expansion, budgeting.
- Static analysis: solution build and formatting verification.

### Non-functional checks

- Performance / load: p50/p95/p99 by mode with indexing active and cold/warm model separated.
- Security / privacy: source-scope intersection and no query/content logging by default.
- Observability: stage-timing, reranker outcome, truncation, and cancellation assertions.

---

## Definition of Done

- Behaviour matches R2.3 rules and flows.
- All automated scenarios, judged relevance thresholds, parity, and latency checks pass.
- Static analysis has no new unresolved issues.
- Build/test/live inference/live Weaviate and any selected real reranker verification pass explicitly.
- Contracts, configuration, evaluation method/results, fallback, and known limitations are documented.
- FEATURE-03 and PLAN-02 evidence is recorded and reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `Application/RagSearchService.cs`, `Application/Contracts.cs`, `Infrastructure/Weaviate/WeaviateVectorStore.cs`, SQLite index state

