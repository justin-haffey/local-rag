---
name: new-agent
description: Create, upgrade, migrate, or repair project-scoped Codex custom agents. Use when the user asks to scaffold a new Codex agent, write or improve a .codex/agents/*.toml file, convert a role prompt into a native custom agent, configure agent-local MCP servers or skills.config, align agent files with nearby repository conventions, add or update agent registry/config entries when the repo requires them, validate agent TOML, or modernize old /new-agent behavior. Do not use for Codex skills, normal feature work, or generic prompt writing unrelated to custom agents.
---

# New Agent

Create or improve native Codex custom agents that are narrow, safe, valid, and easy to spawn. Favor the simplest production-grade agent that preserves the intended behavior.

## Core Contract

Always produce a concrete custom-agent artifact unless the user explicitly asks for advice only.

A finished Codex custom agent is usually a standalone TOML file under:

- `.codex/agents/<agent-name>.toml` for project-scoped agents;
- `~/.codex/agents/<agent-name>.toml` for personal agents when explicitly requested.

Codex custom agents must define:

- `name`
- `description`
- `developer_instructions`

Optional fields such as `nickname_candidates`, `model`, `model_reasoning_effort`, `sandbox_mode`, `mcp_servers`, and `skills.config` should be included only when the agent needs behavior that should not be inherited from the parent session.

## Supported Invocation

Accept prompts such as:

```text
$new-agent -name security-reviewer -purpose "review auth changes for security risk"
$new-agent -folder core -instructions "create a testing agent that owns flaky test diagnosis"
$new-agent upgrade .codex/agents/reviewer.toml
```

Flag handling:

- `-name <name>`: preferred agent name; normalize to lowercase hyphen-case for `name` and filename unless local convention differs.
- `-folder <folder>`: subfolder under the resolved agent root. Use only if the repo convention supports nested agent folders.
- `-instructions`, `-purpose`, or `-description`: source material for the agent's scope and behavior.
- `-model <model>` and `-reasoning <level>`: include only if requested or justified by the agent's task.
- `-sandbox <mode>`: include only if requested or necessary; otherwise prefer inherited runtime permissions.
- `-mcp <server>`: configure only when the agent actually needs that MCP server.
- `-skills <skill-list>`: add `skills.config` only for skills the agent should reliably invoke.

If required details are missing, infer safe defaults when possible. Ask at most 1-3 questions only when the missing answer changes destination, permissions, external tools, or destructive capability.

## Workflow

### 1. Resolve the Agent Root

Determine where the custom agent should live before editing.

Priority:

1. Use the exact file or directory path requested by the user.
2. If editing an existing agent, use that agent's current file.
3. If the repository has one established custom-agent root, use it.
4. If several roots exist, use the one containing the nearest relevant sibling agents.
5. If no repo convention exists, default to `.codex/agents/` for project-scoped agents.
6. Use a personal `~/.codex/agents/` destination only when the user explicitly asks for a user-level agent.

Project-root guard:

- Distinguish this skill's folder from the destination project.
- Do not create a new agent inside this skill's own folder merely because this skill was invoked by path.
- Treat the current working directory, user-specified repository path, or discovered repository root as the destination project.
- If the prompt says "the repo has no existing agent root," choose `<repo>/.codex/agents/`.

### 2. Inspect Only Immediate Agent Neighbors

Inspect repository conventions narrowly.

After resolving the agent root:

- Read the target agent TOML if it exists.
- Inspect only files in the resolved agent root and its immediate child folders.
- Sample 2-4 sibling agent TOML files from the same agent root when they exist.
- If the repo has an adjacent `AGENTS.md`, `AGENTS.override.md`, or registry file in the same `.codex/` area, read only the relevant nearby sections.
- Do not inspect unrelated agent roots, global user agents, package caches, or broad prompt directories unless the user explicitly asks.

Use neighbor inspection to infer:

- filename convention;
- whether nested folders are used;
- TOML field order;
- default sandbox/model patterns;
- whether a registry in `.codex/config.toml` is used;
- whether `nickname_candidates` are common;
- how MCP servers and `skills.config` are represented;
- whether local override files such as `AGENTS.override.md` must be updated.

If no immediate neighbors exist, use the standard structure in this skill.

### 3. Understand the Requested Agent

Identify:

- role and purpose;
- exact tasks the agent should handle;
- non-scope tasks the agent should refuse or hand back;
- expected inputs and outputs;
- whether the agent should inspect, draft, patch, validate, execute, review, or only advise;
- read/write/tool/network permissions;
- MCP servers, skills, or repo references needed;
- validation expectations;
- stop, retry, and escalation conditions.

Prefer narrow agents. Do not create a second general-purpose assistant.

### 4. Choose Runtime Settings

Use inherited defaults unless there is a clear reason to override.

Include `model` only when:

- the user requested a model;
- the repo convention requires one;
- the agent's task demands a model different from the parent default.

Include `model_reasoning_effort` only when:

- the user requested it;
- the agent repeatedly performs complex review, planning, research, or architecture work;
- the repo's nearby agents consistently set it.

Include `sandbox_mode` only when:

- the agent must be safer than the parent session, such as `read-only` for auditors;
- the user explicitly requested a permission profile;
- the repo convention requires it.

Default safety posture:

- use read-only behavior in the instructions for review, planning, prompt, docs, and audit agents;
- allow edits only for implementation agents with clearly scoped write responsibility;
- avoid `danger-full-access` unless the user explicitly approves and the task truly needs it.

### 5. Design MCP and Tool Access

Configure tools narrowly.

MCP placement:

- Put a server in the agent TOML only when it is agent-specific.
- Put shared MCP servers in `.codex/config.toml` only when the repo already uses shared config and the user wants broad availability.
- Do not duplicate shared MCP server definitions into every agent.
- Do not add MCP servers just because they might be useful.

MCP safety:

- Do not include secrets, tokens, or private env values in agent files.
- Use environment variable names only when needed.
- Prefer read-only or documentation MCPs for research agents.
- Explicitly state whether the agent may call external systems, mutate issues/PRs, post comments, deploy, or only inspect.

### 6. Design Skill Access

Add `skills.config` only when the custom agent needs reliable access to specific skills.

Rules:

- Use repo-relative paths for checked-in project skills when nearby agents do so.
- Use absolute user-level paths only when local convention already uses them or the user requests a personal setup.
- Do not add a long skill list. Prefer 1-4 skills tied to the agent's job.
- In `developer_instructions`, explicitly say when the agent should invoke each skill.
- Do not invent unavailable skill names.

### 7. Write the Agent TOML

Use this minimal shape unless immediate neighbors require a different local pattern:

```toml
name = "agent-name"
description = "Narrow description of when Codex should use this agent."

developer_instructions = '''
# Role

You are agent-name.

# Goal

[Concrete outcome this agent should produce.]

# Scope

- [In scope]
- [In scope]

# Non-Scope

- [Out of scope]
- [Out of scope]

# Inputs

Expect:
- [input]

# Workflow

1. [step]
2. [step]
3. [step]

# Tool and File Rules

- [allowed tool/file behavior]
- [forbidden behavior]

# Output

Return:
- [handoff shape]

# Stop Rules

- Stop when [condition].
- Ask or hand back when [condition].

# Validation

- Check [item].
- Do not claim [item] unless verified.
'''
```

Optional fields go above `developer_instructions` in this order when used:

```toml
nickname_candidates = ["Name"]
model = "gpt-5.5"
model_reasoning_effort = "medium"
sandbox_mode = "read-only"
```

Avoid unknown native Codex keys unless the local runtime explicitly supports them. If metadata cannot be represented natively, encode it safely inside `developer_instructions` or comments.

### 8. Registry and Config Updates

Do not assume a registry is required.

Update `.codex/config.toml` only when:

- nearby agents are registered there;
- the user explicitly asks for registry wiring;
- the repo's own instructions require it.

If updating config:

- preserve existing formatting as much as practical;
- add the smallest necessary entry;
- avoid changing global agent limits unless requested;
- avoid provider, credentials, profile, telemetry, or notification keys in project config;
- validate TOML after editing.

Update local override files such as `AGENTS.override.md` only when:

- the target folder's existing convention requires it;
- the override file is in the immediate agent area;
- the new agent should be discoverable to humans through that file.

### 9. Validate

Before finalizing:

1. Check filename and `name` alignment.
2. Check required TOML fields exist.
3. Parse the TOML.
4. Check `developer_instructions` is non-empty, role-specific, and scope-bounded.
5. Check no scaffold TODOs remain.
6. Check MCP references are justified and contain no secrets.
7. Check `skills.config` paths exist or are clearly documented assumptions.
8. Check registry/config entries point at the correct file when used.
9. Check the agent does not duplicate a nearby built-in or existing custom agent unless explicitly replacing it.
10. Confirm validation commands actually ran before claiming them.

Useful validation commands:

```bash
python - <<'PY'
import pathlib, tomllib
path = pathlib.Path(".codex/agents/<agent-name>.toml")
data = tomllib.loads(path.read_text())
for key in ("name", "description", "developer_instructions"):
    assert key in data and data[key], f"missing {key}"
print("agent toml valid")
PY
```

Use the repo's existing validator if nearby agents or docs define one.

### 10. Forward-Test Complex Agents

Forward-test when the agent is high-impact, tool-heavy, permission-sensitive, or intended for frequent reuse.

Use a fresh subagent or separate task if available. The test prompt should look like a real invocation:

```text
Use @agent-name to solve: <realistic task>.
```

Do not leak the intended answer. Review whether the agent:

- activates for the right task;
- stays inside scope;
- uses only allowed tools and files;
- invokes required skills;
- asks before unsafe side effects;
- returns the requested handoff;
- stops instead of drifting into general assistance.

Patch the agent or this skill if forward-testing exposes friction.


## Safety and Permissions

Default to safe custom-agent authoring.

- Do not overwrite an existing agent without reading it first.
- Do not delete user files unless explicitly requested.
- Do not inspect unrelated agent roots when neighbor sampling is requested.
- Do not add broad MCP or network access without a specific need.
- Do not include secrets, tokens, or private local paths.
- Do not add dependency installs, provider changes, telemetry settings, or credential configuration unless requested.
- Do not grant destructive powers merely because the agent might be used for implementation.
- Do not claim tests, validation, docs inspection, or official research unless actually performed.

For agents that can mutate code, external systems, production data, GitHub issues/PRs, cloud resources, or databases, include explicit permission boundaries and stop-before-destructive-action rules.

## Quality Checklist

A finished custom agent should satisfy:

- narrow role and trigger;
- clear non-scope;
- valid TOML;
- required fields present;
- no unsupported config keys;
- minimal runtime overrides;
- narrow tool and MCP access;
- explicit skill invocation rules when skills are configured;
- bounded retry and stop rules;
- concrete output contract;
- validation checklist;
- no hidden chain-of-thought requirements;
- no personal machine paths or secrets;
- no unbounded delegation loops;
- maintainable instructions under one coherent hierarchy.

## Output

When finished, report:

- agent path;
- agent name and purpose;
- files changed or created;
- registry/config updates, if any;
- MCP and skills configuration, if any;
- validation performed;
- assumptions or remaining risks.

Keep the final response concise. Include file links when available.

## References

- `references/agent-authoring.md`
