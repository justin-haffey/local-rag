---
name: orchestrate
description: Plan and execute simple or complex multi-step tasks by creating an in-memory orchestration that uses /goal, selects the best subagents, assigns each subagent 1-3 scoped tasks, names helpful skills each subagent should invoke, and runs parallel, sequential, review, fan-out, or hybrid workflows. Use when the user invokes $orchestrate, orchestrate, orchestrate -complex, asks to spawn or coordinate subagents, asks for a symbolic planner-like workflow, or wants an intuitive repeatable plan that explicitly references agents with @agent-name. Do not use for trivial one-step answers.
---
# Orchestrate

## Purpose

Turn a user request into a compact executable orchestration for Codex. The orchestration must create or update a `/goal`, choose subagents, assign tasks and useful skills, run the workflow, integrate results, and stop cleanly.

This skill is a meta-planner. It must not create task-planning files, scratch plans, task manifests, or orchestration artifacts on disk. Keep the plan in the conversation unless the user's actual task requires file output.

## Invocation Modes

Parse the user's prompt before planning.

- `orchestrate <prompt>`: use simple mode.
- `orchestrate -complex <prompt>`: use complex mode.
- If the user asks for subagents, parallel work, delegation, orchestration, or an explicit `/goal`, treat that as an implicit invocation.
- If the task is obviously trivial, skip subagents and answer directly with a short note that orchestration is unnecessary.

Simple mode:

- Use at most 1-3 subagent tasks total.
- Prefer one workflow shape.
- Prefer a configured custom agent from `.codex/config.toml` when its description clearly fits; otherwise use the appropriate default agent.
- Keep the visible plan under 12 bullets.

Complex mode:

- Use multiple workflow stages when useful.
- Combine parallel discovery, sequential implementation, independent worker slices, verification, and synthesis as needed.
- Spawn no more agents than the work can justify. Prefer 3-6 active subagents. Exceed 6 concurrent agents only for batch-style tasks with independent items and explicit value.
- Use `/goal` to track the main objective and major phases.

## Operating Rules

Follow these rules in order.

1. Preserve the user's actual task. Planning must accelerate execution, not replace it.
2. Use the smallest useful team. Subagents cost tokens and coordination.
3. Spawn subagents only for work that is self-contained, materially useful, and either parallelizable or specialized.
4. Do not spawn multiple agents to do the same analysis.
5. Do not use parallel write-heavy workers unless their ownership scopes are disjoint.
6. Tell write-capable workers they are not alone in the codebase, must not revert others' changes, and must adapt to concurrent edits.
7. Require each subagent to return a concise handoff, not raw logs.
8. Ensure subagents invoke relevant skills explicitly with `$skill-name` when a skill is needed.
9. Keep task-planning state in memory. Do not create files solely for the orchestration.
10. Stop when the goal is satisfied, validation is complete, or additional delegation is unlikely to improve the result.

## Discovery Pass

Before spawning, perform a short routing pass in memory.

Identify:

- objective: the user-visible outcome;
- constraints: files, repo scope, deadlines, safety limits, no-file rules, output format;
- unknowns: critical facts that must be discovered;
- work types: research, code exploration, implementation, validation, docs, design, data, review, synthesis;
- required skills: available skills that should be invoked by the parent or subagents;
- available agents: custom agents configured in `.codex/config.toml`, plus built-in `@default`, `@explorer`, and `@worker` as fallbacks;
- side effects: whether tasks may read, edit, test, browse, call connectors, or only advise.

Ask for clarification only when a missing detail materially changes safety, destination, or output format and no safe assumption exists.

## Agent Selection

Choose agents by task shape.

- First use a custom agent configured in `.codex/config.toml` when its name or description directly matches the need.
- If no configured custom agent is a clear fit, use `@explorer` for specific read-only codebase questions, source mapping, dependency tracing, existing behavior, and evidence gathering.
- If no configured custom agent is a clear fit, use `@worker` for bounded implementation, fixes, test updates, document generation, and production changes.
- If no configured custom agent is a clear fit, use `@default` for synthesis, broad judgment, ambiguous tasks, and when no specialized role is justified.
- Use the parent agent for orchestration, final integration, user-facing decisions, and conflict resolution.

For each selected subagent define:

- role name using `@agent-name`;
- unique label when multiple agents share the same base role, such as `@worker-performance`, `@worker-tests`, or `@explorer-api`;
- purpose in one sentence;
- 1-3 concrete tasks;
- ownership scope, especially files/modules for write work;
- skills to invoke, written as `$skill-name`;
- tools or permissions allowed;
- output contract;
- stop condition.

## Workflow Shapes

Select one or more workflow shapes. In complex mode, compose shapes as phases.

### Solo Plan

Use when the task is small, low-risk, or mostly conversational.

Process:

1. Create `/goal`.
2. Execute directly.
3. Validate.
4. Report.

### Parallel Recon

Use when independent facts can be gathered faster in parallel.

Process:

1. Create `/goal`.
2. Spawn 2-4 `@explorer` or specialist agents with disjoint questions.
3. Continue parent work that does not depend on them.
4. Wait when their results block the next phase.
5. Merge findings into one decision.

### Sequential Relay

Use when each phase depends on the prior phase.

Process:

1. Discovery agent maps facts.
2. Planner or parent turns facts into an implementation path.
3. Worker executes.
4. Reviewer or tester validates.
5. Parent integrates and finalizes.

### Fork-Join Implementation

Use when changes can be split across disjoint ownership areas.

Process:

1. Parent defines shared constraints and disjoint write scopes.
2. Spawn workers in parallel.
3. Each worker edits only its owned scope.
4. Parent reviews changes, resolves integration, and runs final validation.

### Critic Loop

Use when quality, safety, correctness, or maintainability is high-stakes.

Process:

1. Worker or parent produces a candidate result.
2. Reviewer checks for concrete defects and missing validation.
3. Parent fixes or delegates a targeted repair.
4. Stop after one repair loop unless the remaining issue is blocking and clearly fixable.

### Research-Then-Build

Use when platform behavior, APIs, law, pricing, product specs, or current facts may have changed.

Process:

1. Spawn or run a research pass using official sources first.
2. Distinguish documented facts from assumptions.
3. Build only after the relevant facts are verified.
4. Cite sources in the final answer when the user-facing result depends on them.

### Batch Fan-Out

Use when many similar independent units must be processed.

Process:

1. Create a compact item list in memory when feasible.
2. Group small items into batches to reduce overhead.
3. Spawn workers by batch, not by every tiny item, unless a CSV fan-out tool is explicitly available and appropriate.
4. Require structured results.
5. Merge, deduplicate, and validate.

## Orchestration Template

Use this visible structure for non-trivial tasks. Keep it compact.

```markdown
/goal [objective]

Plan:
- Mode: [simple|complex]
- Workflow: [Solo Plan|Parallel Recon|Sequential Relay|Fork-Join Implementation|Critic Loop|Research-Then-Build|Batch Fan-Out|hybrid]
- Agents:
  - @agent-name: [purpose]. Tasks: [1-3 tasks]. Skills: [$skill-a, $skill-b, or none]. Output: [handoff shape].
  - @worker-scope-name: [use unique labels for repeated base roles].
- Execution:
  1. [spawn/call @agent-name ...]
  2. [continue parent work / wait / integrate]
  3. [validate]
- Stop: [clear stop conditions]
```

Then execute the plan. Do not leave the user with only a plan unless they explicitly asked for planning only.

## Subagent Prompt Template

When spawning a subagent, use concise prompts that include enough context to act without reading the whole parent thread.

```text
@agent-name

Objective: [specific delegated outcome]

Context: [minimum necessary facts, repo scope, constraints, current assumptions]

Use skills: Invoke [$skill-name] if relevant before doing that part of the work.

Tasks:
1. [task]
2. [task]
3. [task]

Boundaries:
- [read/edit/test/tool permissions]
- [ownership scope]
- Do not work outside this scope.
- If editing, do not revert or overwrite changes by others; adapt to concurrent work.

Return:
- Findings or changes in 3-7 bullets.
- File paths or source references when applicable.
- Validation performed, or why validation was not possible.
- Remaining risks or blockers.

Stop when [condition].
```

## Skill Routing

Name skills explicitly so subagents can trigger them.

- For document outputs, assign `$documents`, `$pdf`, `$Presentations`, or `$Spreadsheets` as applicable.
- For frontend apps, websites, prototypes, or games, assign `$frontend-skill`.
- For creating or revising skills, assign `$new-skill`.
- For OpenAI platform, Codex, Agents SDK, or API behavior, assign `$openai-docs`.
- For GitHub PRs, issues, CI, publishing, or review comments, assign the relevant GitHub skill if available.
- For image or audio generation, assign `$imagegen` or the appropriate speech tool only when requested.
- If no skill is relevant, write `Skills: none`.

Do not invent unavailable skill names. If a useful skill is not visible, describe the capability needed instead of pretending it exists.

## Token and Context Controls

Optimize for strong outcomes with low overhead.

- Keep the parent thread focused on decisions, task boundaries, and integrated results.
- Push noisy exploration, logs, and broad reading into subagents only when useful.
- Give each subagent the minimum context needed.
- Prefer structured handoffs over long summaries.
- Use parallelism for independent work, not for dramatic effect.
- Reuse a subagent thread for follow-up only when the follow-up depends on its local context.
- Close or stop subagents when their result is no longer needed.
- Do not wait repeatedly with short polling intervals; wait only when blocked.
- In complex mode, checkpoint after each major phase and prune stale assumptions.

## Validation

Before finalizing, verify:

- The `/goal` was created or clearly stated.
- Each spawned or proposed agent was referenced with `@agent-name`.
- Each subagent had 1-3 concrete tasks.
- Relevant skills were named with `$skill-name`.
- Workflow shape matched the actual dependency graph.
- Parallel write scopes were disjoint.
- No task-planning files were created.
- Tool, file, network, and destructive-action boundaries were respected.
- Tests, citations, or inspections were actually performed before claiming them.
- Remaining uncertainty is stated plainly.

## Final Response

When the task is complete, return:

- outcome first;
- subagents used and what each contributed, only if meaningful;
- validation performed;
- remaining risks or next action, if any.

Do not include hidden reasoning, raw tool logs, or the full orchestration prompt unless the user asks for it.
