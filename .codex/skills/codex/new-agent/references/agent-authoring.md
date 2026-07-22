# Agent Authoring Reference

This repository uses the current Codex custom-agent schema from the official Codex docs, plus the local registry pattern already established in `.codex/config.toml`.

## Official Sources

- https://developers.openai.com/codex/subagents
- https://developers.openai.com/codex/config-basic
- https://developers.openai.com/codex/config-reference
- https://developers.openai.com/codex/skills

## Repository Pattern

- `.codex/agents/swe/` is for coding agents.
- `.codex/agents/` is for application domain agents.
- `.codex/config.toml` is the root registry for agent entries and shared MCP inventory.
- `.codex/agents/copilot/AGENTS.override.md` routes the Copilot collection.

## Standard Custom-Agent Schema

The Codex docs require every standalone custom agent file to define:

- `name`
- `description`
- `developer_instructions`

Optional fields include:

- `nickname_candidates`
- `model`
- `model_reasoning_effort`
- `sandbox_mode`
- `mcp_servers`
- `skills.config`

The `name` field is the source of truth. Match the filename to the name when practical, but do not treat the filename as authoritative.

## Canonical Field Order

Use this order unless a local sample in the same folder clearly diverges:

1. `name`
2. `description`
3. `nickname_candidates` if needed
4. `model`
5. `model_reasoning_effort`
6. `sandbox_mode`
7. `developer_instructions`
8. `mcp_servers` if needed
9. `skills.config` if needed

## Canonical TOML Template

```toml
name = "agent-name"
description = "Use this agent for ..."
nickname_candidates = ["Display Name", "Alt Name"] # optional
model = "gpt-5.4" # use gpt-5.5 when the role needs deeper reasoning than the repo default
model_reasoning_effort = "medium"
sandbox_mode = "workspace-write"
developer_instructions = """
You are a focused <role> agent for Codex subagent workflows.

Primary responsibilities:
- <objective>
- <objective>

Operating rules:
- <rule>
- <rule>

Non-goals:
- <boundary>
- <boundary>

Output expectations:
- <expected result>
- <expected result>
"""

# Optional agent-local MCP servers only when they materially help this agent.
[mcp_servers.microsoft_learn]
url = "https://learn.microsoft.com/api/mcp"

# Optional skills dependencies only when this agent truly needs them.
[[skills.config]]
path = ".codex/skills/copilot-architecture-playbook/SKILL.md"
enabled = true
```

## Registry Entry Template

Add a matching root registry entry for the new agent:

```toml
[agents.my_agent]
description = "Use this agent for ..."
config_file = "agents/swe/my-agent.toml"
```

Use `agents/copilot/...` in `config_file` for agents that live in the Copilot collection. If the agent is in `.codex/agents/copilot/`, also update `.codex/agents/copilot/AGENTS.override.md`.

## MCP Server Rules

Start with the smallest working set.

- `microsoft_learn` for current Microsoft, OpenAI, and Copilot guidance.
- `azure_services` and `azure` for Azure infrastructure and operational work.
- `azure_databases` for Azure SQL and Cosmos DB work.
- `playwright` for browser validation and UI testing.
- `codebase-memory-mcp` for repo discovery and graph search when it is configured for the project and materially helpful.

Decision rules:

- Prefer one server at a time unless two are clearly complementary.
- Keep agent-local MCP wiring local when the server is only useful to one agent.
- Mirror a server into `.codex/config.toml` when the repo should expose it as a shared capability.
- Do not add a new server block just to make the file look more complete.

## Field Guidance

- Keep `description` concise, triggerable, and specific enough for implicit invocation.
- Keep `developer_instructions` imperative and structured by role, objectives, boundaries, tool use, and output expectations.
- Use `nickname_candidates` only when multiple spawned instances need distinct readable labels.
- Use `gpt-5.5` for demanding reasoning-heavy agents, `gpt-5.4` when the workflow is intentionally pinned to that model, and `gpt-5.4-mini` for lighter supporting work.
- Use `high` reasoning for complex or review-heavy agents, `medium` for the balanced default, and `low` only when speed matters more than depth.
- Use `workspace-write` as the default sandbox posture unless the role truly needs read-only or dangerous access.

## Validation Checklist

- The TOML parses cleanly.
- `name`, filename, and root registry key all correspond.
- The root registry points at the correct file path.
- Shared MCP servers are registered where the repo expects them.
- Agent-local MCP servers are justified and minimal.
- `skills.config` entries point at real skill paths.
- Copilot agents also update `AGENTS.override.md`.

## Reference Links

- https://developers.openai.com/codex/subagents
- https://developers.openai.com/codex/config-basic
- https://developers.openai.com/codex/config-reference
- https://developers.openai.com/codex/skills
