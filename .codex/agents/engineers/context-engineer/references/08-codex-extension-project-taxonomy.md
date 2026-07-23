---
title: Codex Extension Project Taxonomy
owner: Starlinx LLC
status: active
last_updated: 2026-06-30
artifact_type: rag_knowledge_base
audience: Codex meta-agents, skill authors, subagent authors, repository maintainers
runtime_target: codex_cli, codex_subagents, chatgpt_agent_runtime
scope: taxonomy for .codex directories and Codex extension projects
freshness: update when local project conventions or Codex extension structure changes
retrieval_guidance: retrieve when designing, auditing, or generating .codex directories, Codex skills, Codex subagents, AGENTS.md files, or supporting knowledge-base files
placement_guidance: store in RAG; use as source material for generated files under a VS Code project's .codex/ directory
---

# Purpose

This taxonomy helps Codex meta-agents design `.codex/` directories that extend Codex with reusable skills, focused subagents, and supporting knowledge. The resulting agents and skills may write software, research, review code, generate documents, validate outputs, or coordinate other agents across languages and architectures.

# Primary Project Types

## Meta-Agent Projects

Projects that create or improve Codex itself through prompts, agent definitions, skills, plugins, AGENTS.md files, or validation workflows.

Typical files:

- `.codex/agents/*.toml`
- `.codex/skills/*/SKILL.md`
- `.codex/skills/*/references/*.md`
- `.codex/kb/*.md`
- `AGENTS.md`

## Software Delivery Projects

Projects where Codex agents implement, test, review, and ship application or infrastructure changes.

Common extension needs:

- repo mapper subagent,
- test planner skill,
- implementation worker subagent,
- PR reviewer subagent,
- CI debugger skill,
- release note skill,
- framework-specific reference files.

## Research and Analysis Projects

Projects where Codex gathers evidence, summarizes sources, compares options, or prepares technical recommendations.

Common extension needs:

- research planner skill,
- source evaluator subagent,
- citation validator skill,
- synthesis subagent,
- topic-specific knowledge files.

## Documentation and Knowledge Projects

Projects where Codex creates or maintains specs, docs, runbooks, tutorials, decision records, and context packages.

Common extension needs:

- docs architect subagent,
- style-guide skill,
- migration-note skill,
- document validator skill,
- terminology and audience reference files.

# .codex Directory Taxonomy

Recommended structure:

```text
.codex/
  agents/
    *.toml
  skills/
    skill-name/
      SKILL.md
      references/
      scripts/
      assets/
      agents/
  kb/
    *.md
  evals/
    *.md
    fixtures/
  templates/
    *.md
    *.toml
  config.toml
```

Use only the directories that add real value. Do not create empty structure for its own sake.

# Agent Categories

## Explorer Agents

Purpose: read-heavy codebase or document investigation.

Default permissions: read-only.

Expected output:

- relevant files,
- entry points,
- data/control flow,
- risks,
- unanswered questions,
- recommended next agent or action.

## Worker Agents

Purpose: implement bounded changes after scope is clear.

Default permissions: workspace-write when edits are required.

Expected output:

- files changed,
- behavior changed,
- validation performed,
- remaining risks.

Rules:

- own a disjoint write scope when used in parallel,
- do not revert others' work,
- make the smallest defensible change,
- validate the changed behavior.

## Reviewer Agents

Purpose: find correctness, security, regression, architecture, test, or maintainability risks.

Default permissions: read-only.

Expected output:

- findings first,
- severity,
- file references,
- reproduction or reasoning summary,
- missing tests,
- residual risk.

## Research Agents

Purpose: gather and validate external or internal evidence.

Default permissions: read-only plus approved research tools.

Expected output:

- sources used,
- confirmed facts,
- assumptions,
- uncertainty,
- recommendation.

## Context and Prompt Agents

Purpose: author, audit, migrate, and validate instruction artifacts.

Default permissions: read-only unless file creation is requested.

Expected output:

- production artifact,
- assumptions,
- validation checklist,
- migration note when replacing prior prompts.

## Verification Agents

Purpose: independently test or inspect the result of another agent's work.

Default permissions: read-only or command execution if tests are required.

Expected output:

- commands or checks run,
- pass/fail result,
- observed evidence,
- gaps in verification.

# Skill Categories

## Workflow Skills

Package repeatable procedures such as fixing CI, preparing a PR, performing a release, generating a spec, or migrating prompts.

Use when the steps are stable across projects.

## Domain Skills

Package domain expertise such as React UI QA, database migration review, API design, security triage, PDF processing, or OpenAI docs lookup.

Use when the agent needs specialized knowledge and validation norms.

## Tool Skills

Package safe, repeatable use of a tool or external system.

Use when tool arguments, side effects, permissions, or failure modes need guardrails.

## Artifact Skills

Package creation of durable outputs such as DOCX, PPTX, XLSX, PDFs, specs, diagrams, code mods, or generated assets.

Use when output quality requires a workflow and verification loop.

# Supporting Knowledge Categories

Use `.codex/kb/*.md` or RAG knowledge-base files for durable reference material that should not be loaded every turn.

Recommended categories:

- project architecture map,
- framework conventions,
- test strategy,
- release process,
- prompt standards,
- safety and permissions policy,
- terminology,
- product/domain context,
- eval cases,
- known failure modes.

# Design Rules for Extensible Codex Projects

- Put always-on repository rules in `AGENTS.md`.
- Put reusable procedures in skills.
- Put specialist behavior in subagents.
- Put large or optional reference material in `references/` or `.codex/kb/`.
- Prefer read-only agents for exploration, review, research, and prompt work.
- Give write access only to agents that must edit files.
- Keep subagents narrow and opinionated.
- Keep skills scoped to one repeatable job.
- Define trigger conditions and non-trigger conditions.
- Include stop rules and validation expectations in every production agent or skill.
- Avoid recursive delegation unless explicitly needed.
- Use disjoint write ownership for parallel worker agents.
- Use structured outputs when a supervisor agent or script consumes results.

# Handoff Contract

When a Codex meta-agent creates an extension set, it should return:

- file tree,
- purpose of each file,
- installation or placement notes,
- invocation examples,
- validation checklist,
- known assumptions,
- unresolved risks.

# Anti-Patterns

- A subagent that is just another general assistant.
- A skill that repeats generic prompting advice without a reusable workflow.
- AGENTS.md files full of one-off task context.
- Large copied docs in always-loaded instructions.
- Broad write permissions for agents that only need to inspect.
- Multiple agents editing the same files in parallel.
- Tool policies without failure and retry rules.
- Validation claims without commands, evidence, or inspection.
