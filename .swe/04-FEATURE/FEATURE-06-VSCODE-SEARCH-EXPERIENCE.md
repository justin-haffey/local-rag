# Feature: VS Code Search Experience

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: `src/vscode-extension/src`, extension contributions/assets/tests, REST search/context contracts  
ADRs: [ADR-005: VS Code Explorer Source Controls](../02-ADR/ADR-005-vscode-explorer-source-controls.md) is Accepted for selected-folder commands, menu interaction, and non-secret UI state. On 2026-07-23 the user explicitly authorized a FEATURE-06 scope exception to implement search/context without ADR-004. This exception does not claim FEATURE-04's authorization migration is complete; the UI uses the current authenticated host token flow and its existing source visibility rules.

---

## Implementation plan (step-by-step)

- [x] Approve selected-folder Explorer interaction and bounded non-secret state retention through ADR-005.
- [x] Add the `Local RAG` Explorer submenu: Index Folder, Toggle Indexing, Refresh Index, and Source Status; preserve `localRag.markAsSource` as a compatibility alias.
- [x] Extend additive REST client contracts for search mode plus language and workspace-relative path filters; context continues to use the authenticated chunk contract. The extension maps safe typed authentication, authorization, invalid-request, timeout, and backend states without exposing response details.
- [x] Add `localRag.search`, sidebar view contributions, view/provider modules, and compiled assets.
- [x] Implement current-token source selection, mode/language/path filters, loading/empty/truncated/error states, and cancellation.
- [ ] Implement result provenance, open-at-line, bounded context, and copy actions without executing retrieved content.
- [ ] Add unit/component/client/navigation/state tests plus packaged-VSIX smoke tests.
- [ ] Run npm/dotnet verification and end-to-end search against live host/Weaviate; record evidence.
- [ ] Update extension commands, settings, privacy, accessibility, and user documentation.

---

## Purpose

Provide a rich in-editor retrieval workflow so developers can search authorized sources, inspect provenance/context, navigate to code, and copy evidence without leaving VS Code.

---

## Stakeholders (who needs this to be clear)

| Role | What they need from this spec |
| ---- | ----------------------------- |
| Product / Owner | Search/filter/navigation behavior and user-visible failure states |
| Engineering | View architecture, REST client/state boundaries, and package assets |
| DevOps / SRE | Backend/dependency status integration and repair guidance |
| QA | Accessible interaction, multi-root, stale-file, denied, timeout, and VSIX tests |

---

## Scope

### In scope

- Search sidebar/view, command, source/mode/language/path filters, results, context, provenance, navigation, copy, and status/error states.
- Explorer selected-folder controls: `Right click folder -> Local RAG -> Index Folder | Toggle Indexing | Refresh Index | Source Status`.
- Multi-root workspaces, authorized source lists, cancellation, bounded persisted non-secret preferences, and packaged assets.

### Out of scope

- LLM answer synthesis, editing/writing source files, executing retrieved instructions, content telemetry, or credential storage outside existing approved secret flow.

---

## Business Rules

- R2.6-search_view: only authorized sources and supported modes/filters are selectable; requests remain bounded.
- R2.6-navigation: open only a workspace/local file resolved from an authorized source plus returned relative path; never trust an arbitrary absolute result path.
- R2.6-content_actions: copied content includes provenance and is treated as data; the extension never executes it.
- Loading, empty, truncated, degraded, denied, invalid, timeout, cancelled, missing-backend, and stale-file states have distinct safe UI.
- UI preferences may include non-secret filters/layout only; no token, query history, or source content persists unless separately approved.
- Explorer menu contributions and handlers accept folders only; file selections are refused before a host mutation. Mutations resolve the selected workspace folder and freshly match its opaque root hash; refresh/status never falls back to a different Quick Pick source. Indexing has no cross-folder stale cleanup, and toggle removal requires explicit confirmation.
- Search cancellation aborts superseded requests and prevents stale response overwrite.
- Multi-root source mapping is deterministic and does not assume the first workspace folder.
- Extension telemetry must not contain source content, query text, absolute roots, or credentials.

---

## User Flows

### Primary flows

1. Search and navigate
   - Actor: Developer
   - Trigger: Open Local RAG search or run `localRag.search`.
   - Steps: Select sources/mode/filters; submit; inspect results/provenance; open result at line.
   - Result: Editor opens the correct authorized local file/range.
2. Expand/copy context
   - Actor: Developer
   - Trigger: Select result action.
   - Steps: Request bounded context; render provenance; copy selected data.
   - Result: Clipboard contains bounded evidence with source/line information.
3. Manage selected folder from Explorer
   - Actor: Developer
   - Trigger: Right-click a local workspace folder and open `Local RAG`.
   - Steps: Index, toggle, refresh, or inspect only the selected folder; commands refresh source summaries before a mutation.
   - Result: The requested action is scoped to the selected folder or is safely refused when no current match exists.

### Edge cases

- Backend/dependency degraded → preserve query/form, show actionable status/retry.
- Unauthorized source after grant change → clear stale selection/result and show safe denial.
- File missing or content changed since indexing → warn; never open a different arbitrary path.
- Rapid searches → cancel prior request and render only newest response.
- Large/truncated result → visibly indicate truncation and obey content limits.
- No workspace or remote-development context → explain supported behavior without unsafe path assumptions.

---

## System Behaviour

- Entry points: VS Code command, sidebar view activation, result/context actions.
- Reads from: extension settings/state, authorized source/status REST, search/context REST.
- Writes to: versioned non-secret UI preference state and clipboard/user navigation only on explicit action.
- Side effects / emitted events: backend requests, editor navigation, clipboard copy, safe extension logs.
- Idempotency: repeated searches are read-only; state updates replace by request generation.
- Error handling: typed client errors map to safe state-specific UI and retry/repair actions.
- Security / permissions: bearer token remains in current approved environment/SecretStorage flow; no token/content in webview messages/state/logs.
- Feature flags / toggles: search view contributes only when packaged; optional filters/context reflect reported capabilities.
- Performance / SLAs: responsive typing/navigation, cancelled superseded calls, backend search target remains under 300 ms p95.
- Observability: request duration/status/count/truncation only; no query/full content telemetry.

---

## Diagrams

~~~mermaid
flowchart LR
    A[Search view] --> B[Extension client]
    B --> C[Authorized REST search]
    C --> D[Result model]
    D --> E[Open file and line]
    D --> F[Expand or copy context]
~~~

---

## Verification

### Test environment

- Environment / stack: Node.js 22+, TypeScript, VS Code extension test seams, packaged VSIX, live LocalRAG host/Weaviate for E2E.
- Data and reset strategy: deterministic client responses for unit tests; disposable workspace/source/index for E2E; reset extension state.
- External dependencies: real host/ONNX/Weaviate for release smoke; mocked fetch only for component/client unit cases.

### Test commands

- build: from `src/vscode-extension`, `npm ci` then `npm run compile`; host: `dotnet build .\LocalRag.sln -c Release`
- test: from `src/vscode-extension`, `npm test`; host contracts: `dotnet test .\LocalRag.sln -c Release`
- format: `npm run lint` and `dotnet format .\LocalRag.sln --verify-no-changes`
- coverage: no extension coverage command currently exists; add before completion or record reviewer-approved omission.

### Test flows

**Positive scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| POS-06-001 | Search with sources/mode/language/path | UI/E2E | Authorized bounded results and provenance render | Live fixture repo |
| POS-06-002 | Open selected result | UI | Correct workspace file and line range opens | Multi-root fixture |
| POS-06-003 | Expand/copy selected context | UI/API | Bounded context with provenance copied as data | Clipboard seam |
| POS-06-004 | Index/reindex selected Explorer folder | UI | Only the selected matching folder is registered or queued | Multi-root command fixture |

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-06-001 | Backend returns denied/invalid/timeout | UI | Typed safe state, no stale result/content leak | Client error fixtures |
| NEG-06-002 | Result tries absolute/out-of-source path | UI/Security | Navigation rejected and safe warning shown | Malicious response fixture |
| NEG-06-003 | Backend/host missing | UI | Repair/start guidance; view remains usable for retry | Existing installer seam |
| NEG-06-004 | Toggle removal cancelled or selected folder is stale | UI | No arbitrary source deletion/action occurs | Confirmation and stale-state fixtures |
| NEG-06-005 | A file is selected in Explorer | UI | Folder-only message; no host mutation | File URI fixture |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-06-001 | Indexed file missing/changed | UI/E2E | Stale warning; no wrong file opened | Delete/mutate fixture |
| EDGE-06-002 | Rapid superseding searches | UI | Prior request cancelled; newest response wins | Deferred fetches |
| EDGE-06-003 | Multi-root/empty/truncated/degraded state | UI | Correct source mapping and explicit state | Workspace matrix |
| EDGE-06-004 | Source list changes between Explorer commands | UI | Fresh matching or safe refusal; no wrong source action | Deferred source-list fixture |

### Test mapping

- Integration tests: extension client against live authorized REST contracts and packaged host.
- API tests: filters/context/error schema compatibility.
- UI / E2E tests: view lifecycle, accessibility, navigation, copy, multi-root, degraded/stale states, packaged VSIX.
- Unit tests: client, state reducer/provider, source mapping, path resolution, request generation/cancellation.
- Static analysis: TypeScript compile/lint and VSIX content inspection.

### Non-functional checks

- Performance / load: render maximum result/context payload without blocked UI; rapid-search cancellation.
- Security / privacy: state/log/telemetry/clipboard review, path resolution, webview message allowlist if a webview is selected.
- Observability: safe request status/duration/truncation assertions.

### Partial implementation evidence (2026-07-23)

- The Explorer context-menu slice is implemented and verified with `npm.cmd test` (22 passed, 0 failed) from `src/vscode-extension`.
- The user explicitly authorized a search/context exception without ADR-004. The extension now contributes the `Local RAG Search` Explorer view, performs cancellable authenticated `/api/v1/search` requests, renders provenance, opens only a matching real workspace-relative result path, and copies a 12,000-character-bounded `/api/v1/chunks/{chunkId}` context after source/path match validation.
- The host now validates and applies hybrid/lexical/vector mode plus language and workspace-relative path-prefix filters through the shared REST/application/Weaviate path. `RagSearchServiceTests` covers filter propagation, candidate widening, and unsafe path rejection; `dotnet test .\\LocalRag.sln -c Release --no-restore` passed 142 tests with 4 opt-in live-dependency skips.
- Static validation passed: `npm.cmd run lint`, `dotnet format .\\LocalRag.sln --verify-no-changes --no-restore`, and `npm.cmd run package`. The packaged VSIX includes the search view and controls; VSCE reports only missing repository/license metadata warnings.
- Live gate status (2026-07-23): the repository's `src/weaviate/docker-compose.yml` deployment was started after explicit user retry direction. `WEAVIATE_TEST_ENDPOINT=http://127.0.0.1:8080` passed `WeaviateIntegrationTests` (1/1). With `LOCALRAG_ONNX_TESTS=1`, `OnnxEmbeddingIntegrationTests` and `ReconciliationLiveIntegrationTests` passed (2/2).
- Final regression (2026-07-23): with the Weaviate and ONNX environment enabled, `dotnet test .\\LocalRag.sln -c Release --no-restore` passed 145 tests with one skipped structural-evaluation gate unrelated to FEATURE-06. Extension `npm.cmd test` passed 22 tests; lint, format verification, and VSIX packaging passed.
- Typed diagnostics/authorization errors, the FEATURE-04 authorization migration, live retrieval, and the full FEATURE-06 release checks remain pending.

---

## Definition of Done

- Behaviour matches R2.6 rules and approved interaction/accessibility design.
- Positive, negative, edge, multi-root, stale, cancellation, and packaged-VSIX tests pass.
- Static analysis passes with no unresolved issue.
- Extension/host build/test/live E2E/package smoke commands pass.
- Commands, Explorer submenu/view contributions/assets, REST contracts, privacy, settings, and user docs are updated.
- FEATURE-06 and PLAN-02 evidence is recorded and reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `src/vscode-extension/src/extension.ts`, `sourceState.ts`, `package.json`, host REST contracts
