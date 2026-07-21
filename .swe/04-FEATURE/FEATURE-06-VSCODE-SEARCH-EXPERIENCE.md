# Feature: VS Code Search Experience

Links:  
Plan: [.swe/03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)  
Modules: `src/vscode-extension/src`, extension contributions/assets/tests, REST search/context contracts  
ADRs: No standalone ADR exists; DESIGN.md Sections 3.3 and 5.1 govern this draft. View architecture and state-retention decisions must be approved before Ready.

---

## Implementation plan (step-by-step)

- [ ] Approve view architecture, interaction/accessibility behavior, persisted preference schema, and acceptance mock/flow.
- [ ] Extend additive REST client contracts for modes, filters, context, diagnostics, and authorization errors.
- [ ] Add `localRag.search`, sidebar view contributions, view/provider modules, and compiled assets.
- [ ] Implement authorized source selection, mode/language/path filters, loading/empty/truncated/error states, and cancellation.
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

**Negative scenarios**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| NEG-06-001 | Backend returns denied/invalid/timeout | UI | Typed safe state, no stale result/content leak | Client error fixtures |
| NEG-06-002 | Result tries absolute/out-of-source path | UI/Security | Navigation rejected and safe warning shown | Malicious response fixture |
| NEG-06-003 | Backend/host missing | UI | Repair/start guidance; view remains usable for retry | Existing installer seam |

**Edge cases**

| ID | Description | Level (Unit / Int / API / UI) | Expected result | Data / Notes |
| -- | ----------- | ----------------------------- | --------------- | ------------ |
| EDGE-06-001 | Indexed file missing/changed | UI/E2E | Stale warning; no wrong file opened | Delete/mutate fixture |
| EDGE-06-002 | Rapid superseding searches | UI | Prior request cancelled; newest response wins | Deferred fetches |
| EDGE-06-003 | Multi-root/empty/truncated/degraded state | UI | Correct source mapping and explicit state | Workspace matrix |

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

---

## Definition of Done

- Behaviour matches R2.6 rules and approved interaction/accessibility design.
- Positive, negative, edge, multi-root, stale, cancellation, and packaged-VSIX tests pass.
- Static analysis passes with no unresolved issue.
- Extension/host build/test/live E2E/package smoke commands pass.
- Commands, view contributions/assets, REST contracts, privacy, settings, and user docs are updated.
- FEATURE-06 and PLAN-02 evidence is recorded and reviewed.

---

## References

- Plan: [.swe/03-PLAN/PLAN-02](../03-PLAN/PLAN-02-PHASE2-RETRIEVAL-QUALITY-OPERATIONAL-HARDENING.md)
- Architecture: [.swe/01-DESIGN/DESIGN.md](../01-DESIGN/DESIGN.md)
- Code: `src/vscode-extension/src/extension.ts`, `sourceState.ts`, `package.json`, host REST contracts

