# PLAN-02: Phase 2 — Retrieval Quality and Operational Hardening Plan

**Plan ID:** PLAN-02
**Phase:** Phase 2 — Retrieval Quality and Operational Hardening
**Status:** Approved
**Owner:** LocalRAG engineering
**Target release:** Phase 2; version and date not yet committed
**Last updated:** 2026-07-22

Related documents:

- Design: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- ADRs: [ADR-001: Language-Aware Structural Chunking](../02-ADR/ADR-001-language-aware-structural-chunking.md), [ADR-002: Durable Reconciliation State and Watcher Recovery](../02-ADR/ADR-002-durable-reconciliation-state-and-watcher-recovery.md), [ADR-003: Local Management and Destructive Reset](../02-ADR/ADR-003-local-management-and-destructive-reset.md), [ADR-004: Contextual Retrieval Ranking and Reranking](../02-ADR/ADR-004-contextual-retrieval-ranking-and-reranking.md), and [ADR-005: VS Code Explorer Source Controls](../02-ADR/ADR-005-vscode-explorer-source-controls.md) govern their owning features. The user explicitly authorized FEATURE-06's current search/context extension implementation without ADR-004; this narrow exception uses existing host contracts and does not change FEATURE-04's rejected status.
- Features: [.swe/04-FEATURE/](../04-FEATURE/)
- Architecture: [DESIGN.md Sections 4–16](../01-DESIGN/DESIGN.md)

---

## Summary

Phase 2 turns the working local Windows MVP into a higher-quality, recoverable retrieval product with structural chunks, resilient synchronization, context-aware ranking, client-to-source authorization, richer MCP and VS Code experiences, actionable diagnostics, and an approved cross-platform packaging path. The phase extends the existing shared application layer and preserves local-first, read-only-by-default, externally managed Weaviate boundaries.

### Phase objectives

1. Improve retrieval quality on an approved language corpus, with structural provenance and measurable evaluation gains over the generic chunker baseline.
2. Automatically detect and recover from missed or overflowed file events without re-embedding unchanged content.
3. Deliver source-authorized contextual retrieval consistently through REST, MCP, and a VS Code search experience.
4. Make indexing and search failures diagnosable through safe metrics, status, and dashboard views.
5. Produce and smoke-test host packages for the approved Phase 2 operating-system and runtime-identifier matrix.

### Non-goals

- Compiler-grade or Tree-sitter dependency graphs, Git branch/commit filters, parent-child retrieval, and relationship indexing; these remain Phase 3.
- Team-hosted or enterprise multi-tenant deployment, non-loopback binding, autonomous repository writes, and default MCP administrative tools.
- New embedding providers, local answer synthesis, index export/import, encrypted backup, or Weaviate lifecycle management.
- Replacing the existing DOCX/PDF extraction paths; Phase 2 chunking consumes their normalized output but does not broaden document extraction.

---

## Scope and boundaries

### In scope

- Pluggable structural chunking with generic fallback and retrieval-quality evaluation.
- Periodic/startup reconciliation, watcher-overflow dirty-state persistence, automatic recovery, and restart-safe status.
- Wider candidate retrieval, deterministic diversification, optional local reranking, and bounded neighbor expansion.
- Persisted client identities and explicit source grants enforced by shared application services.
- Additional read-only MCP retrieval tools and resources backed by those same services.
- A VS Code search view with source/filter controls, result navigation, context actions, and safe error states.
- Expanded metrics, diagnostics contracts, and a VS Code diagnostics dashboard.
- Cross-platform host publishing, discovery, data-path conventions, service scripts, and package smoke tests for an approved RID matrix.

### Out of scope

- Any direct MCP-to-Weaviate query path or unrestricted local-file read.
- Remote identity providers, TLS termination, tenant namespaces, and shared-service rollout.
- Write-capable MCP tools, source mutation through MCP, or release/deployment to end users.
- Native installers for every platform unless separately selected in the packaging decision.

### Constraints and invariants

- REST, MCP, and VS Code remain adapters over shared application services; ranking and authorization cannot diverge by client.
- Weaviate remains externally managed and uses application-supplied vectors; Phase 2 packaging must not install, start, stop, or configure it.
- Default bindings remain loopback-only, secrets remain outside commits, absolute source paths remain internal, and retrieved content is treated as untrusted data.
- SQLite remains operational truth; Weaviate remains the searchable chunk/vector store.
- MCP stays read-only by default; restricted vector search is disabled unless policy and source authorization both permit it.
- Structural chunk changes must preserve line ranges, deterministic identity semantics, profile provenance, and generic fallback.
- The current `net10.0`, BGE Small English v1.5 384-dimensional profile, and `RagChunk_v1` collection remain unchanged unless an accepted ADR explicitly changes them.
- This plan is a review candidate only. It does not authorize implementation, infrastructure changes, deployment, or release.

---

## Implementation changes

1. **Structural indexing**

   - Change: Introduce a chunker selection/composition boundary and structural chunkers for the approved Phase 2 language corpus, retaining `GenericChunker` as the fallback.
   - Boundaries: `IChunker`, `ChunkRecord`, `FileIndexingService`, processing registrations, SQLite chunk metadata, and Weaviate properties.
   - Configuration/data: Add stable chunk kind, qualified symbol, structural locator, chunker ID/version, and migration/version rules; do not silently reinterpret existing chunks.
   - Verification: POS-01-001 through EDGE-01-001 and a retrieval-quality comparison corpus.
2. **Synchronization recovery**

   - Change: Persist dirty/overflow state, coalesce recovery requests, run reconciliation automatically, and clear degraded state only after a successful scan.
   - Boundaries: `SourceWatcherRegistry`, `ReconciliationService`, `IndexCoordinator`, job/checkpoint state, source status, and operational metrics.
   - Configuration/data: Persist reconciliation cause, generation, timestamps, and recovery outcome without storing full paths in diagnostics.
   - Verification: POS-02-001 through EDGE-02-001, including overflow, restart, repeated-event, and unchanged-file cases.
3. **Contextual retrieval pipeline**

   - Change: Separate candidate retrieval from result shaping; add diversification, optional local reranking, neighbor expansion, and output budgets.
   - Boundaries: `IRagSearchService`, `IVectorStore`, `IIndexStateStore`, search contracts, REST, and MCP.
   - Configuration/data: Add bounded candidate multiplier, diversification policy, neighbor count, and reranker policy with reranking disabled by default.
   - Verification: POS-03-001 through EDGE-03-001 plus p95 and relevance-evaluation evidence.
4. **Client/source authorization**

   - Change: Replace the single implicit `local-extension` identity with persisted client principals and explicit source grants resolved by a shared authorization service.
   - Boundaries: Authentication handler, authorization policies, source/search/chunk application services, SQLite migrations, REST, MCP, metrics, and audit events.
   - Configuration/data: Store only token verifiers or protected references, never recoverable bearer-token plaintext; define revocation and deny-by-default migration behavior.
   - Verification: POS-04-001 through EDGE-04-001 and cross-adapter parity tests.
5. **Read-only MCP expansion**

   - Change: Add `rag_find_similar`, `rag_expand_context`, `rag_list_languages`, `rag_get_index_capabilities`, and stable read-only resources; keep supplied-vector search restricted and administration disabled.
   - Boundaries: MCP adapters/resources and shared query/capability/source services only.
   - Configuration/data: Add per-tool limits and capability reporting; no collection names, absolute paths, or unauthorized counts.
   - Verification: POS-05-001 through EDGE-05-001 and MCP contract snapshots.
6. **VS Code retrieval experience**

   - Change: Add a search sidebar, filters, source selection, mode selection, result navigation, copy actions, and bounded context expansion.
   - Boundaries: VS Code contributions/views, `LocalRagClient`, REST search/context contracts, workspace/global state, and extension tests.
   - Configuration/data: Persist non-secret UI preferences only; preserve multi-root and missing-backend handling.
   - Verification: POS-06-001 through EDGE-06-001, TypeScript tests, and a packaged-VSIX smoke test.
7. **Operational diagnostics**

   - Change: Expand counters into safe metric families, source/job diagnostics, reconciliation/overflow visibility, and a VS Code diagnostics dashboard.
   - Boundaries: `OperationalMetrics`, health/status endpoints, job/source state, structured logs, REST diagnostics, and extension dashboard.
   - Configuration/data: Bounded retention and cardinality; source IDs or relative-path hashes only; never full chunk content or secrets.
   - Verification: POS-07-001 through EDGE-07-001 and log/metric redaction inspection.
8. **Cross-platform packaging**

   - Change: Parameterize host publishing, installation discovery, data/model paths, process launch, and service scripts across the approved RID matrix.
   - Boundaries: project publish settings, scripts, extension host discovery, platform packages, CI matrix, and documentation.
   - Configuration/data: Use one versioned installation-discovery schema and platform-native per-user paths; Weaviate endpoint remains non-secret external configuration.
   - Verification: POS-08-001 through EDGE-08-001 on real or approved CI runners for every selected RID.
9. **Privileged local index management**

   - Change: Add the separately authenticated `local-rag-mgmt` Codex plugin and a management-only host surface for index-by-path, remove-by-path, and an explicitly confirmed full Local RAG reset.
   - Boundaries: Plugin manifest/skills, management MCP and REST adapters, application management service, source/reconciliation fencing, SQLite reset, owned Weaviate collection reset, readiness, and bounded audit metrics.
   - Configuration/data: Disabled by default; use a distinct management token, expiring one-use confirmations, loopback-only ownership validation, and no source-file mutation.
   - Verification: POS-09-001 through EDGE-09-003 plus standard-MCP compatibility and destructive-boundary evidence.

### Delivery sequence

- [ ] Approve the Phase 2 boundary, create/accept the missing standalone ADRs, select the language corpus, authorization schema, reranker policy, and packaging RID matrix.
- [x] Implement FEATURE-01 and FEATURE-02 in parallel after their data/contract decisions are accepted.
- [ ] Implement FEATURE-04 before exposing any new source-scoped surface.
- [ ] Implement FEATURE-03, then validate retrieval and authorization through shared REST contracts.
- [ ] Implement FEATURE-05 and FEATURE-06 over the shared retrieval/authorization contracts.
- [ ] Implement FEATURE-07 after indexing, search, and authorization events are stable.
- [ ] Implement FEATURE-08 after runtime contracts and discovery schema are stable.
- [x] Implement FEATURE-09 only after ADR-003 is accepted; preserve the standard read-only MCP surface and externally managed Weaviate process boundary.
- [ ] Run the complete quality, security, live-dependency, packaging, and documentation release gate.

---

## Features

### Feature registry

| Feature ID | Feature name                        | Priority | Detailed plan                                                                | Requirements | Verification          | Status      |
| ---------- | ----------------------------------- | -------- | ---------------------------------------------------------------------------- | ------------ | --------------------- | ----------- |
| FEATURE-01 | Language-aware structural chunking  | Must     | [FEATURE-01](../04-FEATURE/FEATURE-01-LANGUAGE-AWARE-STRUCTURAL-CHUNKING.md)  | R2.1-*       | POS-01/NEG-01/EDGE-01 | Completed   |
| FEATURE-02 | Reconciliation and watcher recovery | Must     | [FEATURE-02](../04-FEATURE/FEATURE-02-RECONCILIATION-AND-WATCHER-RECOVERY.md) | R2.2-*       | POS-02/NEG-02/EDGE-02 | Completed   |
| FEATURE-03 | Contextual retrieval pipeline       | Must     | [FEATURE-03](../04-FEATURE/FEATURE-03-CONTEXTUAL-RETRIEVAL-PIPELINE.md)       | R2.3-*       | POS-03/NEG-03/EDGE-03 | Not started |
| FEATURE-04 | Client-to-source authorization      | Rejected | [FEATURE-04](../04-FEATURE/FEATURE-04-CLIENT-TO-SOURCE-AUTHORIZATION.md)      | R2.4-*       | POS-04/NEG-04/EDGE-04 | Not started |
| FEATURE-05 | Expanded read-only MCP capabilities | Should   | [FEATURE-05](../04-FEATURE/FEATURE-05-EXPANDED-READ-ONLY-MCP-CAPABILITIES.md) | R2.5-*       | POS-05/NEG-05/EDGE-05 | Not started |
| FEATURE-06 | VS Code search experience           | Must     | [FEATURE-06](../04-FEATURE/FEATURE-06-VSCODE-SEARCH-EXPERIENCE.md)            | R2.6-*       | POS-06/NEG-06/EDGE-06 | Not started |
| FEATURE-07 | Metrics and diagnostics dashboard   | Must     | [FEATURE-07](../04-FEATURE/FEATURE-07-METRICS-AND-DIAGNOSTICS-DASHBOARD.md)   | R2.7-*       | POS-07/NEG-07/EDGE-07 | Not started |
| FEATURE-08 | Cross-platform host packaging       | Rejected | [FEATURE-08](../04-FEATURE/FEATURE-08-CROSS-PLATFORM-HOST-PACKAGING.md)       | R2.8-*       | POS-08/NEG-08/EDGE-08 | Not started |
| FEATURE-09 | Local RAG management plugin and host API | Must | [FEATURE-09](../04-FEATURE/FEATURE-09-LOCAL-RAG-MANAGEMENT-PLUGIN.md) | R2.9-* | POS-09/NEG-09/EDGE-09 | Completed |

### Feature blocks

#### FEATURE-01 — Language-aware structural chunking

**Purpose:** Improve code and configuration retrieval by preserving structural units and symbol provenance while retaining safe fallback behavior.
**Detailed plan:** [FEATURE-01](../04-FEATURE/FEATURE-01-LANGUAGE-AWARE-STRUCTURAL-CHUNKING.md)

**Scope**

- In scope: Chunker dispatch, structural metadata, approved language adapters, fallback, migrations, and relevance evaluation.
- Out of scope: Compiler-grade semantics, dependency graphs, and Phase 3 relationship indexing.
- Dependencies: Approved language corpus and chunk metadata/versioning decision.

**Requirements**

| Requirement ID        | Requirement                                                                                                | Priority | Source / rationale          | Verification IDs        |
| --------------------- | ---------------------------------------------------------------------------------------------------------- | -------- | --------------------------- | ----------------------- |
| R2.1-structural_units | Preserve complete structural units up to the hard token limit and split oversized units deterministically. | Must     | Design Sections 5.7 and 12. | POS-01-001, EDGE-01-001 |
| R2.1-provenance       | Persist language, chunk kind, symbol/qualified name, line range, structural locator, and chunker version.  | Must     | Design Sections 5.7–5.9.   | POS-01-002, NEG-01-001  |
| R2.1-fallback         | Unsupported or malformed files use the generic chunker without losing content or line mapping.             | Must     | Layered fallback design.    | NEG-01-002, EDGE-01-002 |

**User and system flows**

1. **Structural indexing** — The indexing service selects a language adapter, emits bounded structural chunks, embeds changed chunks, and publishes searchable provenance.
2. **Parser failure** — A malformed or unsupported file records a safe diagnostic and falls back to generic chunking.
3. **Version change** — A chunker-version change creates explicit reindex work rather than silently mixing incompatible chunks.

**Acceptance criteria**

- [ ] R2.1 requirements map to automated unit/integration tests and a representative evaluation corpus.
- [ ] Unsupported and malformed inputs cannot drop the file or bypass size/token limits.
- [ ] Reindexing identical input with the same versions yields stable IDs and metadata.
- [ ] Retrieval-quality results and regressions are recorded by language.

**Implementation touchpoints**

| Layer                    | Modules / files                                               | Responsibility                                       | Contract or migration impact          |
| ------------------------ | ------------------------------------------------------------- | ---------------------------------------------------- | ------------------------------------- |
| Domain / application     | `Application/Contracts.cs`, `Domain/Models.cs`            | Chunker selection and structural metadata.           | Add versioned chunk contract fields.  |
| Adapter / API / UI       | Search response contracts                                     | Surface approved symbol metadata consistently.       | Additive response fields only.        |
| Persistence / dependency | `Infrastructure/Processing/`, SQLite state, Weaviate schema | Structural parsing, persistence, and search filters. | Versioned migration/reindex required. |

**Verification mapping**

| Verification ID | Level       | Scenario                                         | Expected result                                          | Test / command                          |
| --------------- | ----------- | ------------------------------------------------ | -------------------------------------------------------- | --------------------------------------- |
| POS-01-001      | Unit        | Supported file contains nested structural units. | Units retain symbol and exact line bounds.               | `dotnet test LocalRag.sln -c Release` |
| NEG-01-001      | Integration | Parser emits invalid or over-limit metadata.     | Input is rejected or bounded; no corrupt chunk persists. | Host test suite                         |
| EDGE-01-001     | Unit        | One symbol exceeds the model hard maximum.       | Deterministic signature/continuation chunks fit limits.  | Chunker boundary tests                  |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

#### FEATURE-02 — Reconciliation and watcher recovery

**Purpose:** Make filesystem events hints rather than correctness dependencies and recover automatically from overflow, restart, and missed changes.
**Detailed plan:** [FEATURE-02](../04-FEATURE/FEATURE-02-RECONCILIATION-AND-WATCHER-RECOVERY.md)

**Scope**

- In scope: Dirty-state persistence, overflow recovery, reconciliation generations, coalescing, restart recovery, status, and metrics.
- Out of scope: Distributed watchers or remote filesystem synchronization.
- Dependencies: SQLite operational state and current indexing job queue.

**Requirements**

| Requirement ID         | Requirement                                                                                | Priority | Source / rationale                               | Verification IDs        |
| ---------------------- | ------------------------------------------------------------------------------------------ | -------- | ------------------------------------------------ | ----------------------- |
| R2.2-overflow_recovery | A watcher failure marks the source dirty/degraded and queues one automatic reconciliation. | Must     | Design Sections 5.5, 10, and ADR-006 summary.    | POS-02-001, EDGE-02-001 |
| R2.2-restart_safe      | Dirty and in-flight reconciliation state survives host restart.                            | Must     | SQLite operational truth and reliability design. | POS-02-002, EDGE-02-002 |
| R2.2-no_reembed        | Reconciliation does not re-embed unchanged content.                                        | Must     | Design performance and MVP acceptance criteria.  | POS-02-003              |

**User and system flows**

1. **Overflow recovery** — The watcher reports failure; the source becomes degraded, a generation is queued, reconciliation completes, and readiness/status recover.
2. **Dependency failure** — Weaviate or embeddings are unavailable; the job remains recoverable with bounded retry and visible diagnostics.
3. **Save burst** — Repeated hints coalesce into bounded work without losing the final filesystem state.

**Acceptance criteria**

- [x] Overflow recovery needs no manual reindex when dependencies are healthy.
- [x] Degraded status clears only after a complete successful generation.
- [x] Restart and duplicate-event tests prove idempotency.
- [x] Metrics show overflows, reconciliation duration, result counts, and failures without source content.

**Implementation touchpoints**

| Layer                    | Modules / files                                                                   | Responsibility                         | Contract or migration impact  |
| ------------------------ | --------------------------------------------------------------------------------- | -------------------------------------- | ----------------------------- |
| Domain / application     | Source status and coordinator contracts                                           | Dirty/recovery lifecycle.              | Add reason/generation fields. |
| Adapter / API / UI       | Source status REST/MCP and extension status                                       | Explain recovery without path leakage. | Additive diagnostics fields.  |
| Persistence / dependency | `SourceWatcherRegistry.cs`, `ReconciliationService.cs`, job/checkpoint stores | Persist and execute reconciliation.    | SQLite migration.             |

**Verification mapping**

| Verification ID | Level       | Scenario                                               | Expected result                                           | Test / command         |
| --------------- | ----------- | ------------------------------------------------------ | --------------------------------------------------------- | ---------------------- |
| POS-02-001      | Integration/live | Watcher overflow with created/changed/deleted files. | One reconciliation converges SQLite and real Weaviate.    | `ReconciliationLiveIntegrationTests` |
| NEG-02-001      | Integration | Dependency remains unavailable through exhaustion.     | Retry is bounded; source stays degraded and job retained. | `SqliteReconciliationStoreTests`      |
| EDGE-02-001     | Integration | Overflow repeats during active reconciliation.         | A later generation runs; no parallel storm or lost state. | `ReconciliationRecoveryTests`         |

**Feature completion checklist**

- [x] Requirements are implemented and linked to tests.
- [x] Positive, negative, and edge scenarios pass.
- [x] Public contracts, configuration, migrations, and docs are updated.
- [x] Review evidence and remaining limitations are recorded.

#### FEATURE-03 — Contextual retrieval pipeline

**Purpose:** Improve useful result diversity and context while preserving deterministic limits, latency, and adapter parity.
**Detailed plan:** [FEATURE-03](../04-FEATURE/FEATURE-03-CONTEXTUAL-RETRIEVAL-PIPELINE.md)

**Scope**

- In scope: Candidate widening, score normalization, deduplication/diversification, optional local reranking, neighbor expansion, and budgets.
- Out of scope: LLM answer synthesis, parent-child retrieval, and remote reranking by default.
- Dependencies: FEATURE-01 metadata; FEATURE-04 authorization before client exposure.

**Requirements**

| Requirement ID | Requirement                                                                                           | Priority | Source / rationale                   | Verification IDs        |
| -------------- | ----------------------------------------------------------------------------------------------------- | -------- | ------------------------------------ | ----------------------- |
| R2.3-diversify | Diversify candidates by file or symbol deterministically without bypassing source/filter scope.       | Must     | Design Section 5.9 ranking pipeline. | POS-03-001, NEG-03-001  |
| R2.3-neighbors | Expand adjacent chunks within the same authorized file and within configured count/character budgets. | Must     | Design Sections 3.4 and 5.9.         | POS-03-002, EDGE-03-001 |
| R2.3-rerank    | Support an optional local reranker behind a disabled-by-default policy and bounded timeout.           | Should   | Phase 2 roadmap.                     | POS-03-003, NEG-03-002  |

**User and system flows**

1. **Contextual search** — Search retrieves wider candidates, shapes them, optionally reranks, expands neighbors, and enforces final budgets.
2. **Reranker failure** — Timeout/unavailability records a safe diagnostic and returns the deterministic non-reranked ordering.
3. **Context boundary** — First/last chunks, deleted chunks, and cross-source adjacencies never leak unrelated content.

**Acceptance criteria**

- [ ] REST and MCP return equivalent ordering and context for equivalent principals and inputs.
- [ ] Unauthorized or out-of-file neighbors are never returned.
- [ ] Optional reranking has an explicit configuration, model/version provenance, timeout, and fallback.
- [ ] Representative searches meet the design's p95 target or record an approved exception.

**Implementation touchpoints**

| Layer                    | Modules / files                                                    | Responsibility                           | Contract or migration impact                   |
| ------------------------ | ------------------------------------------------------------------ | ---------------------------------------- | ---------------------------------------------- |
| Domain / application     | `IRagSearchService`, search models, new context/ranking services | Orchestrate candidate and result stages. | Additive search/context contracts.             |
| Adapter / API / UI       | REST search/context, MCP, extension client                         | Pass filters and render diagnostics.     | Versioned optional fields.                     |
| Persistence / dependency | `WeaviateVectorStore.cs`, `IIndexStateStore`                   | Wider retrieval and ordinal neighbors.   | Query/index changes; no direct adapter access. |

**Verification mapping**

| Verification ID | Level       | Scenario                                                | Expected result                                       | Test / command              |
| --------------- | ----------- | ------------------------------------------------------- | ----------------------------------------------------- | --------------------------- |
| POS-03-001      | Integration | Repetitive top candidates span one file.                | Diversification returns relevant multi-file coverage. | Live Weaviate evaluation    |
| NEG-03-001      | API         | Requested context crosses unauthorized source scope.    | Request is denied or context is safely omitted.       | Authorization contract test |
| EDGE-03-001     | Unit        | Result is first/last chunk with large neighbor request. | Expansion clamps to file and output bounds.           | Neighbor service tests      |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

#### FEATURE-04 — Client-to-source authorization

**Purpose:** Ensure every retrieval and inspection operation is evaluated against the authenticated client's explicit source grants.
**Detailed plan:** [FEATURE-04](../04-FEATURE/FEATURE-04-CLIENT-TO-SOURCE-AUTHORIZATION.md)

**Scope**

- In scope: Client records, source grants, principal resolution, revocation, audit metadata, and shared enforcement.
- Out of scope: OIDC/OAuth, non-loopback hosting, enterprise tenants, and write roles.
- Dependencies: Accepted authorization data-model and token-protection ADR.

**Requirements**

| Requirement ID     | Requirement                                                                                                                  | Priority | Source / rationale                    | Verification IDs        |
| ------------------ | ---------------------------------------------------------------------------------------------------------------------------- | -------- | ------------------------------------- | ----------------------- |
| R2.4-deny_default  | Unknown, revoked, ungranted, removed, paused, and hidden sources are denied or omitted consistently.                         | Must     | Design Sections 5.3.7 and 9.          | POS-04-001, NEG-04-001  |
| R2.4-shared_policy | REST, MCP, chunk lookup, neighbors, sources, status, languages, capabilities, metrics, and UI use one authorization service. | Must     | One-core/multiple-adapters principle. | POS-04-002, NEG-04-002  |
| R2.4-token_safety  | Persist no recoverable bearer-token plaintext and compare supplied credentials in constant time.                             | Must     | Local security boundary.              | NEG-04-003, EDGE-04-001 |

**User and system flows**

1. **Authorized retrieval** — Authentication resolves a principal; source grants are intersected with requested scope before data access.
2. **Revoked client** — A revoked credential fails authentication and cannot use cached or explicit source IDs.
3. **Source lifecycle race** — A source removed or hidden during a request cannot yield additional chunks after policy evaluation.

**Acceptance criteria**

- [ ] Every public data operation has explicit policy coverage and parity tests.
- [ ] Source lists omit unauthorized entries and explicit unauthorized IDs fail safely.
- [ ] Revocation takes effect within the documented cache bound.
- [ ] Audit records identify client, operation, source IDs, status, and latency without content or secrets.

**Implementation touchpoints**

| Layer                    | Modules / files                                         | Responsibility                             | Contract or migration impact     |
| ------------------------ | ------------------------------------------------------- | ------------------------------------------ | -------------------------------- |
| Domain / application     | New principal/grant and authorization contracts         | Resolve effective source scope.            | New stable policy interfaces.    |
| Adapter / API / UI       | Authentication handler, endpoint policies, MCP adapters | Establish identity and call shared policy. | Authentication contract changes. |
| Persistence / dependency | SQLite client/grant/audit repositories                  | Durable grants and revocation.             | Security-sensitive migration.    |

**Verification mapping**

| Verification ID | Level       | Scenario                                            | Expected result                                | Test / command                 |
| --------------- | ----------- | --------------------------------------------------- | ---------------------------------------------- | ------------------------------ |
| POS-04-001      | API/MCP     | Client granted two of three sources lists/searches. | Only granted sources and chunks are visible.   | Cross-adapter integration test |
| NEG-04-001      | API/MCP     | Client requests a known but ungranted source ID.    | Safe denial with no existence/content leak.    | Contract test                  |
| EDGE-04-001     | Integration | Grant is revoked during active use.                 | Subsequent calls fail within documented bound. | Revocation test                |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

#### FEATURE-05 — Expanded read-only MCP capabilities

**Purpose:** Give agents safe similarity, context, language, and capability inspection without exposing mutation or vector-store internals.
**Detailed plan:** [FEATURE-05](../04-FEATURE/FEATURE-05-EXPANDED-READ-ONLY-MCP-CAPABILITIES.md)

**Scope**

- In scope: Four new read-only tools, stable resources, validation, limits, authorization, and contract tests.
- Out of scope: Reindex/delete tools, arbitrary GraphQL, direct vector insertion, prompts, and default supplied-vector access.
- Dependencies: FEATURE-03 and FEATURE-04.

**Requirements**

| Requirement ID | Requirement                                                                                                                         | Priority | Source / rationale            | Verification IDs        |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------- | -------- | ----------------------------- | ----------------------- |
| R2.5-tools     | Expose find-similar, expand-context, list-languages, and index-capabilities as thin adapters.                                       | Must     | Design Section 5.3.4.         | POS-05-001, POS-05-002  |
| R2.5-resources | Expose only stable, read-only source/status/chunk/capability resources with shared policy enforcement.                              | Should   | Design resource model.        | POS-05-003, NEG-05-001  |
| R2.5-limits    | Validate source scope, dimensions where applicable, finite values, payloads, timeouts, and response budgets before dependency work. | Must     | Design Sections 5.3.6–5.3.7. | NEG-05-002, EDGE-05-001 |

**User and system flows**

1. **Agent context expansion** — An authorized client expands a result through a bounded application service.
2. **Resource inspection** — The client reads a stable resource and receives only source-authorized metadata.
3. **Malformed request** — Oversized, non-finite, unknown, or unauthorized input is rejected before Weaviate work.

**Acceptance criteria**

- [ ] New MCP capabilities contain no domain logic or direct Weaviate calls.
- [ ] Tool/resource schemas and errors are stable and contract-tested.
- [ ] Administrative tools remain absent from the default registration.
- [ ] Logs contain tool, client, authorized source IDs, latency, and count without full content.

**Implementation touchpoints**

| Layer                    | Modules / files                                    | Responsibility                         | Contract or migration impact   |
| ------------------------ | -------------------------------------------------- | -------------------------------------- | ------------------------------ |
| Domain / application     | Search/context/capability services                 | Implement authorized operations.       | Add read-only service methods. |
| Adapter / API / UI       | `Api/RagMcpTools.cs`, new resource adapter       | Translate MCP schemas only.            | Additive MCP capabilities.     |
| Persistence / dependency | Index state/vector store through application layer | Serve neighbors/similarity/aggregates. | No adapter-owned queries.      |

**Verification mapping**

| Verification ID | Level | Scenario                                             | Expected result                                      | Test / command                |
| --------------- | ----- | ---------------------------------------------------- | ---------------------------------------------------- | ----------------------------- |
| POS-05-001      | MCP   | Expand context for an authorized chunk.              | Ordered bounded neighbors with provenance.           | MCP contract/integration test |
| NEG-05-001      | MCP   | Read resource for ungranted source.                  | Not found/denied without existence leak.             | MCP authorization test        |
| EDGE-05-001     | MCP   | Input reaches configured payload or result boundary. | Deterministic clamp or rejection before excess work. | Boundary test                 |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

#### FEATURE-06 — VS Code search experience

**Purpose:** Let developers search, filter, inspect, navigate, and copy authorized LocalRAG results inside VS Code.
**Detailed plan:** [FEATURE-06](../04-FEATURE/FEATURE-06-VSCODE-SEARCH-EXPERIENCE.md)

**Scope**

- In scope: Search view, commands, source/mode/filter controls, result navigation, context, copy, loading, empty, and error states.
- Out of scope: Answer synthesis, editor writes, prompt execution, and extension telemetry containing source content.
- Dependencies: FEATURE-03 and FEATURE-04; FEATURE-05 only for MCP-facing parity, not the UI transport.

**Requirements**

| Requirement ID       | Requirement                                                                                                     | Priority | Source / rationale                      | Verification IDs        |
| -------------------- | --------------------------------------------------------------------------------------------------------------- | -------- | --------------------------------------- | ----------------------- |
| R2.6-search_view     | Provide a sidebar search flow with authorized sources, search mode, language/path filters, and bounded results. | Must     | Design Sections 3.3 and 5.1.            | POS-06-001, NEG-06-001  |
| R2.6-navigation      | Open a selected result at its indexed relative path and line range; handle stale/missing files safely.          | Must     | Interactive-search use case.            | POS-06-002, EDGE-06-001 |
| R2.6-content_actions | Copy selected chunk/context with provenance and without executing retrieved content.                            | Must     | Prompt-injection and provenance design. | POS-06-003, NEG-06-002  |

**User and system flows**

1. **Search and open** — The user selects authorized sources/filters, searches, chooses a result, and opens its indexed line.
2. **Backend degraded** — The view preserves the query, shows actionable status, and offers retry/status rather than hanging.
3. **Stale result** — A missing or changed local file produces a safe warning while leaving indexed provenance visible.

**Acceptance criteria**

- [ ] Search UI works in single- and multi-root workspaces with no absolute-path API dependency.
- [ ] Loading, empty, truncated, degraded, denied, timeout, and stale-file states are testable.
- [ ] UI preferences contain no tokens or source content.
- [ ] Packaged VSIX includes compiled view assets and passes smoke tests.

**Implementation touchpoints**

| Layer                    | Modules / files                                 | Responsibility                      | Contract or migration impact    |
| ------------------------ | ----------------------------------------------- | ----------------------------------- | ------------------------------- |
| Domain / application     | REST search/context contracts                   | Supply filtered authorized results. | Additive filters/context.       |
| Adapter / API / UI       | `src/vscode-extension/src/`, `package.json` | Views, commands, navigation, state. | New view/command contributions. |
| Persistence / dependency | VS Code workspace/global state                  | Store non-secret UI preferences.    | Versioned state key.            |

**Verification mapping**

| Verification ID | Level  | Scenario                                          | Expected result                              | Test / command   |
| --------------- | ------ | ------------------------------------------------- | -------------------------------------------- | ---------------- |
| POS-06-001      | UI     | Search with source/language/path filters.         | View renders authorized bounded results.     | `npm test`     |
| NEG-06-001      | UI/API | Backend returns denied or malformed-filter error. | Safe actionable message; no stale data leak. | Client/view test |
| EDGE-06-001     | UI     | Indexed file is missing or line range changed.    | Warning shown; no arbitrary path access.     | Navigation test  |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

#### FEATURE-07 — Metrics and diagnostics dashboard

**Purpose:** Make indexing, retrieval, dependencies, and recovery diagnosable without logging or exposing repository content.
**Detailed plan:** [FEATURE-07](../04-FEATURE/FEATURE-07-METRICS-AND-DIAGNOSTICS-DASHBOARD.md)

**Scope**

- In scope: Bounded metric families, source/job diagnostics, safe endpoint, dashboard, health linkage, and redaction.
- Out of scope: Hosted telemetry export, third-party SaaS monitoring, and unbounded per-file labels.
- Dependencies: Stable events from FEATURE-02 through FEATURE-06.

**Requirements**

| Requirement ID | Requirement                                                                                                                             | Priority | Source / rationale                            | Verification IDs        |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------------- | -------- | --------------------------------------------- | ----------------------- |
| R2.7-metrics   | Record design-listed indexing, queue, embedding, Weaviate, search, MCP, overflow, and reconciliation measures with bounded cardinality. | Must     | Design Section 11.                            | POS-07-001, EDGE-07-001 |
| R2.7-dashboard | Provide an authenticated dashboard with dependency health, source/job status, recent safe failures, and recovery guidance.              | Must     | Phase 2 roadmap and`openDashboard` command. | POS-07-002, NEG-07-001  |
| R2.7-redaction | Metrics/logs never include tokens, absolute roots, full chunk content, or unbounded relative paths.                                     | Must     | Security/observability design.                | NEG-07-002              |

**User and system flows**

1. **Diagnose degraded source** — The user opens the dashboard and sees dependency, queue, last-reconciliation, and safe error information.
2. **Unauthorized dashboard** — A client without diagnostic permission receives no aggregate or source-level data.
3. **High-cardinality workload** — Metrics aggregate or hash bounded labels and retain stable memory usage.

**Acceptance criteria**

- [ ] Dashboard distinguishes ready, degraded, failed, paused, dirty, and recovering states.
- [ ] Counters/histograms are concurrency-safe and documented.
- [ ] Authorization and redaction are tested across endpoint, logs, and UI.
- [ ] Diagnostic retention and reset/restart semantics are explicit.

**Implementation touchpoints**

| Layer                    | Modules / files                                                | Responsibility                        | Contract or migration impact     |
| ------------------------ | -------------------------------------------------------------- | ------------------------------------- | -------------------------------- |
| Domain / application     | Diagnostics/query contracts                                    | Produce safe operational snapshots.   | Additive diagnostics contract.   |
| Adapter / API / UI       | `OperationalMetrics.cs`, `Program.cs`, extension dashboard | Export and render diagnostics.        | Authenticated endpoint/view.     |
| Persistence / dependency | Job/source SQLite state and logging                            | Recent failure and recovery evidence. | Bounded persistence if approved. |

**Verification mapping**

| Verification ID | Level       | Scenario                                             | Expected result                                   | Test / command         |
| --------------- | ----------- | ---------------------------------------------------- | ------------------------------------------------- | ---------------------- |
| POS-07-001      | Integration | Index, retry, search, MCP call, overflow, reconcile. | Each safe metric changes as specified.            | Host integration tests |
| NEG-07-001      | API/UI      | Principal lacks diagnostic scope.                    | Dashboard endpoint denies without aggregate leak. | Authorization test     |
| EDGE-07-001     | Load        | Many sources/files/errors create labels.             | Cardinality and memory remain bounded.            | Load/inspection test   |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

#### FEATURE-08 — Cross-platform host packaging

**Purpose:** Make the standalone LocalRAG host installable and discoverable on every approved Phase 2 platform while keeping Weaviate external.
**Detailed plan:** [FEATURE-08](../04-FEATURE/FEATURE-08-CROSS-PLATFORM-HOST-PACKAGING.md)

**Scope**

- In scope: Approved RID builds, per-user paths, discovery schema, launch behavior, service scripts, checksums, uninstall/upgrade safety, and smoke tests.
- Out of scope: Bundling Weaviate/Docker, shared/network deployment, and native installer formats not selected for Phase 2.
- Dependencies: Accepted RID/package matrix and stable runtime/discovery contracts.

**Requirements**

| Requirement ID         | Requirement                                                                                                                              | Priority | Source / rationale                  | Verification IDs        |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- | -------- | ----------------------------------- | ----------------------- |
| R2.8-rid_packages      | Publish self-contained host artifacts for every approved RID and verify executable, native dependencies, model discovery, and checksums. | Must     | Design Section 14.2.                | POS-08-001, NEG-08-001  |
| R2.8-discovery         | Use one versioned installation-discovery contract and platform-native per-user paths in host and extension.                              | Must     | Existing standalone host ownership. | POS-08-002, EDGE-08-001 |
| R2.8-external_weaviate | Installers/scripts accept and validate a non-secret Weaviate endpoint but never manage its lifecycle.                                    | Must     | Repository packaging invariant.     | NEG-08-002              |

**User and system flows**

1. **Install and launch** — The platform package installs the host, records discovery, and the extension launches it with token/endpoint environment.
2. **Dependency unavailable** — Installation succeeds with an advisory warning; runtime readiness explains the external dependency.
3. **Upgrade/uninstall** — Versioned host files update safely while mutable data and model cache follow documented retention rules.

**Acceptance criteria**

- [ ] Every approved RID is built and smoke-tested on a matching runner or approved real environment.
- [ ] Extension discovery and launch contain no Windows-only path or executable assumptions.
- [ ] Upgrade, corrupt-discovery, missing-host, dependency-unavailable, and uninstall-retention cases are tested.
- [ ] Artifact hashes, versions, contents, and install documentation are release evidence.

**Implementation touchpoints**

| Layer                    | Modules / files                              | Responsibility                                | Contract or migration impact       |
| ------------------------ | -------------------------------------------- | --------------------------------------------- | ---------------------------------- |
| Domain / application     | Installation discovery schema                | Platform-neutral host metadata.               | Schema version/migration rules.    |
| Adapter / API / UI       | Extension installer settings and launch code | Resolve and start native host.                | Cross-platform process/path logic. |
| Persistence / dependency | Publish/package/service scripts and CI       | Produce, verify, install, upgrade, uninstall. | New RID/package matrix.            |

**Verification mapping**

| Verification ID | Level     | Scenario                                                  | Expected result                                       | Test / command           |
| --------------- | --------- | --------------------------------------------------------- | ----------------------------------------------------- | ------------------------ |
| POS-08-001      | Packaging | Build and launch each approved RID package.               | Health/live succeeds and required files exist.        | RID matrix smoke scripts |
| NEG-08-001      | Packaging | Native dependency or discovery record is missing/corrupt. | Validation fails safely with repair guidance.         | Package negative test    |
| EDGE-08-001     | E2E       | Upgrade then uninstall with existing mutable data.        | Selected version runs; retention policy is preserved. | Platform install test    |

**Feature completion checklist**

- [ ] Requirements are implemented and linked to tests.
- [ ] Positive, negative, and edge scenarios pass.
- [ ] Public contracts, configuration, migrations, and docs are updated.
- [ ] Review evidence and remaining limitations are recorded.

---

## Cross-cutting requirements

| Area               | Requirement                                                                                                                                                                   | Verification                                                             |
| ------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| Security / privacy | Resolve authenticated principals before data access; intersect source grants; keep loopback/read-only defaults; redact absolute paths, tokens, content, and unbounded labels. | Cross-adapter auth tests, secret/path scans, MCP contract inspection.    |
| Reliability        | Persist recovery work, coalesce duplicate hints, retry idempotently, preserve incomplete work across restart, and clear degraded state only after successful recovery.        | Restart, overflow, race, locked-file, and dependency-failure tests.      |
| Performance        | Search p95 below 300 ms excluding first model load; MCP overhead below 25 ms; incremental detection-to-index p95 below 10 seconds; bounded memory/cardinality.                | Representative benchmark/load corpus with indexing active.               |
| Observability      | Record safe indexing/search/MCP/recovery measures with correlation, client, source ID, status, latency, and counts but no full content.                                       | Metric assertions and structured-log redaction inspection.               |
| Compatibility      | Prefer additive REST/MCP fields, stable tool names/resource URIs, versioned SQLite/discovery schemas, and explicit chunker/reindex transitions.                               | Migration, downgrade/repair, contract snapshot, and package smoke tests. |

---

## Test plan

### Test environment

- Runtime / platform: .NET SDK pinned by `global.json`; Node.js 22+; current Windows x64 baseline plus the approved Phase 2 RID runner matrix.
- Data and reset strategy: Disposable SQLite databases, checked-in swe/config fixtures, retrieval-quality corpus with judgments, temporary source roots, and isolated Weaviate test collections.
- External dependencies: Real ONNX model inference when `LOCALRAG_ONNX_TESTS=1`; real externally managed Weaviate when `WEAVIATE_TEST_ENDPOINT` is set; no mocked live-dependency release claim.
- Required environment variables / secrets: `LOCALRAG_ONNX_TESTS`, `WEAVIATE_TEST_ENDPOINT`, `LocalRag__Authentication__Token`, and test-only client credentials generated during the test.

### Test commands

- Build: `dotnet build .\LocalRag.sln -c Release`
- Unit tests: `dotnet test .\LocalRag.sln -c Release`
- Integration tests: set `LOCALRAG_ONNX_TESTS=1` and `WEAVIATE_TEST_ENDPOINT=http://127.0.0.1:8080`, then run `dotnet test .\LocalRag.sln -c Release`
- End-to-end tests: run the Phase 2 fixture-repository and overflow/restart suite added by FEATURE-02, FEATURE-03, FEATURE-04, and FEATURE-08.
- Format / lint / static analysis: `dotnet format .\LocalRag.sln --verify-no-changes`; from `src/vscode-extension`, run `npm ci`, `npm run lint`, and `npm test`.
- Coverage or release smoke test: publish each approved RID, run host health/search smoke tests, package the VSIX, inspect package contents, and execute the platform installation matrix.

### Scenario matrix

| ID          | Scenario                                                     | Level            | Expected result                                          | Evidence                         |
| ----------- | ------------------------------------------------------------ | ---------------- | -------------------------------------------------------- | -------------------------------- |
| POS-01-001  | Structural chunking of approved-language fixtures.           | Unit/Integration | Stable bounded chunks and correct provenance.            | Chunker tests/evaluation report. |
| POS-02-001  | Watcher overflow followed by automatic reconciliation.       | Integration      | Index converges without re-embedding unchanged files.    | Recovery test/metrics.           |
| POS-03-001  | Diversified, optionally reranked search with neighbors.      | Integration/API  | Authorized useful context within latency/budget.         | Live Weaviate evaluation.        |
| POS-04-001  | Client with subset source grants uses REST and MCP.          | API/MCP          | Identical effective scope across adapters.               | Parity tests.                    |
| POS-05-001  | New read-only MCP tools/resources are invoked.               | MCP              | Stable schemas and bounded authorized results.           | Contract snapshots.              |
| POS-06-001  | User searches and opens a result from VS Code.               | UI/E2E           | Correct file/line navigation and provenance.             | Extension tests/smoke capture.   |
| POS-07-001  | Dashboard diagnoses degraded/recovering source.              | API/UI           | Safe actionable state and metrics.                       | Endpoint/UI tests.               |
| POS-08-001  | Install and launch every approved RID.                       | Packaging/E2E    | Native host and discovery work; Weaviate stays external. | Matrix artifacts.                |
| NEG-04-001  | Client requests ungranted or removed source.                 | API/MCP/UI       | Denied or omitted without content/existence leak.        | Security tests.                  |
| NEG-05-002  | Malformed/oversized MCP request.                             | MCP              | Rejected before vector/database work.                    | Boundary assertions.             |
| NEG-07-002  | Content/token/path appears in diagnostics input.             | Integration      | Sensitive values do not appear in logs/metrics/UI.       | Redaction scan.                  |
| EDGE-02-001 | Repeated overflows during recovery and restart.              | Integration      | Bounded generations converge after restart.              | Race/restart test.               |
| EDGE-03-001 | Neighbor expansion at file/source boundaries.                | Unit/Integration | No cross-file/source leakage; output clamped.            | Neighbor tests.                  |
| EDGE-08-001 | Upgrade/uninstall with existing data and missing dependency. | Packaging/E2E    | Retention and advisory-only dependency behavior hold.    | Platform smoke report.           |

### Release gate

- [ ] The design is explicitly accepted for Phase 2 and standalone ADRs cover host/adapters, read-only MCP, SQLite truth, reconciliation, authorization/token storage, ranking/reranking, and packaging/discovery decisions.
- [ ] The approved language corpus, retrieval evaluation thresholds, authorization migration, optional reranker policy, diagnostics retention, and RID/package matrix are recorded.
- [ ] All feature requirements map to passing automated tests and review evidence.
- [ ] Regression suite, extension suite, static analysis, and representative retrieval-quality evaluation are green.
- [ ] Real ONNX inference and live Weaviate indexing/search/recovery tests pass explicitly rather than being skipped.
- [ ] Security, readiness, limits, migration/rollback, package contents, and cross-platform smoke evidence are recorded.
- [ ] Known failures, deferred work, residual risks, and any performance exceptions are documented.
- [ ] A reviewer explicitly accepts PLAN-02 and all eight feature plans before implementation is treated as authorized.

---

## Acceptance criteria

The phase is complete only when:

1. Approved-language fixtures produce stable structural chunks and the agreed retrieval-quality threshold is met without regressing generic fallback.
2. Watcher overflow, missed events, save bursts, dependency outages, and host restart converge automatically with unchanged content not re-embedded.
3. REST, MCP, and VS Code expose the same authorized contextual retrieval behavior; ungranted source data and absolute paths never leak.
4. The rich VS Code search and diagnostics views handle success, empty, degraded, denied, timeout, stale-file, and recovery states.
5. Additional MCP tools/resources remain read-only, bounded, thin, contract-tested adapters with administrative operations disabled.
6. Metrics and logs provide actionable indexing/search/recovery evidence without content, tokens, absolute roots, or unbounded labels.
7. Every approved platform package installs/launches the standalone host, preserves the external Weaviate boundary, and passes upgrade/uninstall smoke tests.
8. All mapped verification and release evidence passes, and explicit plan/feature review approval is recorded.

---

## Assumptions, risks, and decisions

### Assumptions

- Phase 1 behavior in the current repository is the compatibility baseline, even where the older PLAN-01 artifact is incomplete or stale.
- The existing local ONNX embedding profile and Weaviate collection remain in place; structural chunking may require a controlled reindex but not an embedding-model change.
- Source authorization is delivered for local clients first and must not enable non-loopback or shared hosting.
- Cross-platform packaging covers the standalone host and extension integration; Weaviate and Docker remain operator-managed prerequisites.

### Risks and mitigations

| Risk                                                              | Likelihood | Impact   | Mitigation                                                                                                          | Owner / residual risk                                             |
| ----------------------------------------------------------------- | ---------- | -------- | ------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| Structural parsers create unstable identities or worse recall.    | Medium     | High     | Version chunkers, retain fallback, use golden fixtures and comparative evaluation, require controlled reindex.      | Retrieval owner; parser/library upgrades remain a risk.           |
| Recovery storms overload large repositories.                      | Medium     | High     | Persist/coalesce generations, bound concurrency, prioritize search, test repeated overflow and save bursts.         | Indexing owner; extreme filesystem behavior remains.              |
| Reranking adds latency or leaks content to a remote service.      | Medium     | High     | Local-only approved adapter, disabled by default, timeout/fallback, explicit model provenance.                      | Security/retrieval owners; model footprint remains.               |
| Source-grant migration accidentally broadens access.              | Medium     | Critical | Deny-by-default migration, verifier-only tokens, parity/security tests, explicit rollback.                          | Security owner; current clients need a documented bootstrap path. |
| Dashboard creates content or high-cardinality leakage.            | Medium     | High     | Schema allowlist, hashed/bounded labels, redaction tests, bounded retention.                                        | Observability owner; operator diagnostics may be less granular.   |
| Cross-platform native dependencies or paths fail after packaging. | High       | High     | Approve a finite RID matrix, test on matching runners, inspect contents, version discovery, retain repair guidance. | Release owner; untested distributions remain unsupported.         |
| Missing standalone ADRs allow conflicting implementation choices. | High       | High     | Keep plan Draft and block Ready/implementation until required decisions are accepted.                               | Architecture owner; schedule risk remains.                        |

### Open decisions

| Decision                                                              | Options                                                                        | Decision owner               | Due date                         | Result / linked ADR                                                           |
| --------------------------------------------------------------------- | ------------------------------------------------------------------------------ | ---------------------------- | -------------------------------- | ----------------------------------------------------------------------------- |
| Approve design and extract Section 19 summaries into standalone ADRs. | Accept as written / revise before extraction.                                  | Architecture owner           | Before PLAN-02 Ready             | ADR-001, ADR-002, ADR-003, and ADR-005 are Accepted; ADR-004 remains Proposed and FEATURE-04 still requires its own security ADR. |
| Select the Phase 2 language corpus and parser strategy.               | In-house lightweight parsers / approved parser libraries / staged mix.         | Retrieval owner              | Before FEATURE-01 Ready          | Accepted in[ADR-001](../02-ADR/ADR-001-language-aware-structural-chunking.md). |
| Set relevance evaluation corpus and success thresholds.               | Recall/nDCG/MRR and latency targets by language/use case.                      | Product and retrieval owners | Before FEATURE-01 implementation | Accepted in[ADR-001](../02-ADR/ADR-001-language-aware-structural-chunking.md). |
| Approve client/grant schema and credential protection/bootstrap.      | Verifier records / OS credential references / other reviewed local mechanism.  | Security owner               | Before FEATURE-04 Ready          | Pending security ADR.                                                         |
| Approve diversification and reranker policy.                          | Deterministic diversification only / optional approved local reranker.         | Retrieval owner              | Before FEATURE-03 Ready          | ADR-004 is Proposed; its FEATURE-04 authorization gate remains unresolved.   |
| Select diagnostics format and retention.                              | In-memory snapshot / bounded SQLite events / standards-based metrics endpoint. | Operations owner             | Before FEATURE-07 Ready          | Pending observability decision.                                               |
| Select supported RIDs and package formats.                            | Finite Windows/macOS/Linux matrix and portable/service/native formats.         | Release owner                | Before FEATURE-08 Ready          | Pending packaging ADR.                                                        |
| Commit Phase 2 version and target date.                               | Semantic-version increment and release date after sizing.                      | Product owner                | Before plan acceptance           | Pending.                                                                      |

---

## References

- [High-level design and Phase 2 roadmap](../01-DESIGN/DESIGN.md)
- [Phase 1 implementation plan](PLAN-01-PHASE1-MVP.md)
- [SWE repository governance](../../.codex/AGENTS.md)
- [Current application contracts](../../src/LocalRag.Host/Application/Contracts.cs)
- [Current generic chunker](../../src/LocalRag.Host/Infrastructure/Processing/GenericChunker.cs)
- [Current watcher and reconciliation implementation](../../src/LocalRag.Host/Infrastructure/Indexing/ReconciliationService.cs)
- [Current search service](../../src/LocalRag.Host/Application/RagSearchService.cs)
- [Current MCP adapter](../../src/LocalRag.Host/Api/RagMcpTools.cs)
- [Current VS Code extension](../../src/vscode-extension/src/extension.ts)
- [Current Windows packaging documentation](../../scripts/README.md)
