---
title: OpenAI Platform Notes
owner: Starlinx LLC
status: active
last_updated: 2026-06-30
verified_on: 2026-06-30
artifact_type: rag_knowledge_base
audience: OpenAI platform builders, Codex meta-agents, prompt engineers
runtime_target: chatgpt, responses_api, agents_sdk, codex_cli, codex_subagents
scope: compact map of current OpenAI and Codex platform concepts
freshness: re-verify before using for latest model, pricing, availability, SDK syntax, or newly released features
retrieval_guidance: retrieve for OpenAI API, Agents SDK, Codex, skills, subagents, structured outputs, prompt caching, tool-use, or prompt-upgrade work
placement_guidance: store as RAG reference; do not copy all links into runtime prompts
---

# Purpose

This file is a compact source map for OpenAI platform and Codex work. It is not a replacement for official docs. Re-check official sources for latest model names, pricing, parameters, or feature availability.

# Current Platform Anchors

## Responses API

Use the Responses API when a workflow can be handled by model calls plus tools and application-owned logic. The API supports stateful interactions, text and image inputs, text outputs, built-in tools, and function calling.

Source: https://developers.openai.com/api/reference/responses/overview

## Agents SDK

Use the Agents SDK when the application owns orchestration, tool execution, approvals, specialist collaboration, and state. The official overview describes agents as applications that plan, call tools, collaborate across specialists, and keep enough state for multi-step work.

Source: https://developers.openai.com/api/docs/guides/agents

## Tool Use

OpenAI tool-capable workflows may use built-in tools, function calling, tool search, and remote MCP servers. Tool definitions should include clear permissions, validation, side effects, retry behavior, and fallback behavior.

Source: https://developers.openai.com/api/docs/guides/tools

## Structured Outputs

Use Structured Outputs when downstream software requires schema adherence. Official docs distinguish two main uses: function calling for connecting models to system capabilities, and structured response formats for shaping model replies.

Source: https://developers.openai.com/api/docs/guides/structured-outputs

## Prompt Caching

Prompt caching rewards stable prompt prefixes. Keep durable instructions and examples early, and place variable user/task data later. Re-check docs for current retention and pricing behavior.

Source: https://developers.openai.com/api/docs/guides/prompt-caching

# Codex Anchors

## Codex CLI

Codex CLI is OpenAI's coding agent for local terminal workflows. It can inspect repositories, edit files, and run commands in the selected directory.

Source: https://developers.openai.com/codex/cli

## AGENTS.md

Use AGENTS.md for durable project guidance that should apply before work begins. Keep it small. Put rules close to the scope where they apply, and use it for recurring setup, test, review, convention, and directory guidance.

Source: https://developers.openai.com/codex/guides/agents-md

## Codex Customization Layers

Codex customization layers are complementary:

- `AGENTS.md` shapes repository behavior.
- Memories carry useful context forward.
- Skills package reusable workflows and domain expertise.
- MCP connects Codex to external systems.
- Subagents delegate work to specialists.

Source: https://developers.openai.com/codex/concepts/customization

## Skills

Use skills for reusable workflows. A skill is a directory with `SKILL.md` plus optional `scripts/`, `references/`, `assets/`, and `agents/`. Skills use progressive disclosure: Codex sees name, description, and path first, then reads the full skill only when selected.

Source: https://developers.openai.com/codex/skills

## Skill Design

Keep each skill scoped to one job. Start with a few concrete use cases, clear inputs and outputs, and trigger phrasing a user would actually say. Add scripts or assets only when they improve reliability.

Source: https://developers.openai.com/codex/learn/best-practices

## Subagents

Use subagents for complex work that benefits from parallel or specialized analysis. Custom agents live as standalone TOML files under `.codex/agents/` for project scope or `~/.codex/agents/` for personal scope. Required fields are `name`, `description`, and `developer_instructions`; optional fields can include model, reasoning effort, sandbox mode, MCP servers, and skill config.

Source: https://developers.openai.com/codex/subagents

# Stable Design Heuristics

- Use Responses API for simpler application-owned loops.
- Use Agents SDK when orchestration, approvals, state, tracing, or specialist collaboration are central.
- Use AGENTS.md for rules that should apply repeatedly in a repo.
- Use skills for repeatable workflows and domain-specific procedures.
- Use subagents for narrow specialist roles, parallel exploration, review, or isolated execution.
- Use MCP when the agent needs reliable access to external systems or shared tools.
- Use structured outputs when another program consumes the response.
- Keep prompt-cacheable material stable and early; put dynamic data later.

# Re-Verification Triggers

Re-check official docs before relying on this file for:

- latest model names,
- pricing,
- availability by plan,
- SDK install commands,
- exact request parameters,
- feature maturity,
- security and data retention behavior,
- newly released Codex configuration keys.
