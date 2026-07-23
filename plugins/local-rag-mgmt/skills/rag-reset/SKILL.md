---
name: rag-reset
description: Irreversibly resets all owned Local RAG SQLite state and the ownership-verified local Weaviate collection through a two-phase confirmation challenge. Use only when the user explicitly requests a full Local RAG reset. Do not use for one-folder removal, routine recovery, or remote/shared Weaviate administration.
---

# Reset Owned Local RAG Index Data

Use `mcp__local_rag_mgmt__rag_reset` in exactly two phases and pause for explicit confirmation before the destructive phase.

## Core Contract

- Reset is irreversible and clears all registered sources, indexed file/chunk/job/recovery/profile state, and all objects/schema in the configured ownership-verified Local RAG Weaviate collection.
- Reset does not delete source files, model assets, host configuration, or unrelated Weaviate collections.
- The host must verify loopback scope and collection ownership, acquire exclusive maintenance, complete reinitialization, and recover readiness. This skill must not bypass or reproduce those checks.
- A typed phrase without a host-issued challenge is not sufficient confirmation.

## Two-Phase Workflow

1. Call `mcp__local_rag_mgmt__rag_reset` once without `confirmationToken`.
2. Continue only if the tool returns `ConfirmationRequired`, a one-use token, an expiry, and a fixed warning. Do not invent missing confirmation data.
3. Show the fixed warning and expiry to the user. Explicitly summarize the full blast radius and the items that remain untouched. Ask the user to confirm this irreversible reset, then pause.
4. If the user declines or does not confirm before expiry, stop without a second call.
5. After explicit confirmation, call the same tool once with the returned `confirmationToken`.
6. Report only the operation ID, state, bounded counts, timestamps, and safe error code returned by the tool. Report success only when the tool returns a completed terminal state with readiness recovered.

## Safety and Stop Rules

- Never reveal, log, persist, transform, or ask the user to repeat the confirmation token.
- Never call the confirmed phase without a fresh challenge from the current task and explicit user confirmation after its warning.
- Never retry either phase automatically for any reason. After an expiry, rejection, timeout, transport failure, replay error, ownership refusal, maintenance failure, partial reset, or uncertain result, stop.
- A later recovery/reset attempt requires a new explicit user request, a new phase-one challenge, and a new confirmation. Never reuse a token or silently resume after restart.
- Do not start or stop Docker or Weaviate, delete arbitrary collections, access SQLite directly, or broaden the reset scope.
- Never expose management tokens, ownership identifiers, raw errors, source content, canonical paths, collection credentials, or connection strings.

## Validation

- Confirm explicit user approval occurred between the challenge and destructive calls.
- Confirm the second call used the exact returned token and no other target data.
- Distinguish ownership refusal, `ResetInProgress`, partial/failed reset, unhealthy readiness, and completed reset exactly as returned.

## Output

Before confirmation, return only the host warning, expiry, blast-radius summary, preserved items, and confirmation question. Afterward, return the safe terminal result and state whether readiness recovered; never describe a partial or failed reset as successful.
