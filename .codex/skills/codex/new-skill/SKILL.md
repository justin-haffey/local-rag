---
name: new-skill
description: Create, upgrade, migrate, or repair Codex agent skills. Use when the user asks to create a new skill, improve an existing SKILL.md, convert a workflow into a reusable skill, scaffold a project-local skill, add agents/openai.yaml metadata, add scripts/references/assets, validate skill structure, modernize old /new-skill behavior, or maintain a repository's checked-in skill catalog. Prefer project-local skills under the repository's established skill root, and use official Codex skill conventions when no repo convention exists.
---

# New Skill

Create or improve Codex-compatible skills that are focused, discoverable, maintainable, and validated. Favor the simplest production-grade skill that preserves the intended behavior.

## Core Contract

Always produce a real skill bundle, not just advice, unless the user explicitly asks for a draft only.

A finished Codex skill may include:

- `SKILL.md`: required instructions with YAML frontmatter containing only `name` and `description`.
- `agents/openai.yaml`: optional Codex UI metadata, invocation policy, and tool dependencies.
- `scripts/`: optional executable helpers for deterministic repeated operations.
- `references/`: optional detailed guidance loaded only when needed.
- `assets/`: optional templates, media, boilerplate, or other files used by the skill.

Do not add auxiliary documentation such as `README.md`, `CHANGELOG.md`, or installation guides inside a skill unless the repository already requires them for every nearby skill.

## Workflow

### 1. Resolve the Skill Root

Determine where the skill should live before editing.

Priority:

1. Use the exact path requested by the user.
2. If editing an existing skill, use that skill's current folder.
3. If the repository has one established skill root, use it.
4. If several roots exist, prefer the one containing the nearest relevant sibling skills.
5. If no repo convention exists, default to Codex's project skill root `.agents/skills/`.
6. Use `.codex/skills/` only when the repository already uses it, the user explicitly requests it, or existing repo instructions require it.

Project-root guard:

- Distinguish the skill being used as guidance from the destination project.
- Do not create a new skill inside this skill's own folder merely because this skill was invoked by path.
- Treat the current working directory, user-specified repository path, or discovered repository root as the destination project.
- If a prompt says "the repo has no existing skill root," choose `<repo>/.agents/skills/`, not `<path-to-this-skill>/.agents/skills/`.

Common roots to recognize:

- `.agents/skills/`: official Codex project-local skill root.
- `$HOME/.agents/skills/`: official Codex personal skill root.
- `$CODEX_HOME/skills/` or `$HOME/.codex/skills/`: common local/personal Codex skill roots and legacy setups.
- `.codex/skills/`: repository-specific convention; preserve when already established.

If discovery is ambiguous but safe, choose the best-supported root and state the assumption in the final response.

### 2. Inspect Only Immediate Skill Neighbors

Inspect repository conventions narrowly.

After resolving the skill root:

- Inspect only files in the resolved skill root and its immediate child skill folders.
- Read the target skill's `SKILL.md` if it exists.
- Read a root-level skill catalog such as `README.md` only if it is inside the resolved skill root.
- Sample 2-4 sibling skills from the same skill root when they exist.
- Inspect each sampled sibling's `SKILL.md` and `agents/openai.yaml` only.
- Do not inspect unrelated skills in other roots, package caches, user-global folders, or system folders unless the user explicitly asks.

Use neighbor inspection to infer:

- preferred heading structure;
- resource folder conventions;
- whether `agents/openai.yaml` is required;
- README/catalog update expectations;
- naming patterns;
- validation commands;
- whether skills are intentionally compact or reference-heavy.

If no immediate neighbors exist, follow the standard structure in this skill.

### 3. Understand the Requested Skill

Identify:

- skill name and aliases;
- trigger phrases and non-trigger cases;
- target runtime: Codex CLI, ChatGPT Codex, IDE extension, plugin, cross-platform Agent Skills, or repository-specific workflow;
- expected inputs and outputs;
- whether the skill should inspect, draft, patch, validate, execute, or only advise;
- tools, connectors, permissions, and side effects;
- durable instructions versus task-specific context;
- whether scripts, references, or assets would materially improve repeatability.

Ask at most 1-3 clarification questions only when the missing answer changes the skill name, destination, safety boundary, or required output. Otherwise make the narrowest reasonable assumption and continue.

### 4. Design the Skill Bundle

Choose the minimal bundle that can perform the job reliably.

Use `SKILL.md` only when:

- the workflow is mostly procedural guidance;
- the skill can rely on existing tools and the agent's reasoning;
- long reference material is unnecessary.

Add `references/` when:

- detailed domain guidance would bloat `SKILL.md`;
- multiple variants exist and only one should be loaded per task;
- examples, schemas, policies, or API notes are useful but conditional.

Add `scripts/` when:

- deterministic behavior matters;
- the same code would otherwise be rewritten often;
- validation, conversion, parsing, or formatting should be repeatable;
- the script can be tested in the current environment.

Add `assets/` when:

- the skill needs templates, starter files, images, fonts, boilerplate, or reusable output resources.

Avoid adding resources just to look complete. Every resource must have a clear use path from `SKILL.md`.

### 5. Create or Update the Skill

For a new skill:

1. Normalize the name to lowercase hyphen-case.
2. Keep the name under 64 characters.
3. Create a folder whose basename exactly matches the `name` frontmatter.
4. Prefer using the system `$skill-creator` scaffolding workflow when available.
5. Replace all scaffold TODOs before finishing.

For an existing skill:

1. Preserve useful behavior.
2. Remove stale, duplicated, or process-heavy instructions.
3. Separate dynamic examples from durable rules.
4. Keep frontmatter limited to `name` and `description`.
5. Improve trigger precision in `description`; this is the main discovery surface.
6. Keep `SKILL.md` under 500 lines unless the task genuinely requires more.

### 6. Use the Standard Structure

Use this structure unless immediate sibling skills use a stronger local convention:

```markdown
---
name: skill-name
description: [what it does, when to use it, and important non-trigger cases]
---

# Skill Title

[One short paragraph stating the skill's purpose.]

## Core Contract

[Durable behavior and boundaries.]

## Workflow

[Ordered steps the agent should follow.]

## Resource Routing

[When to read references, run scripts, or use assets.]

## Safety and Permissions

[Allowed and forbidden side effects.]

## Validation

[Concrete checks before completion.]

## Output

[Final response or artifact contract.]
```

Use a different structure only when the skill is better expressed as:

- task menu: multiple independent operations;
- decision tree: routing across variants;
- checklist: fragile deterministic process;
- compact prompt: a small single-purpose skill.

### 7. Write Strong Frontmatter

The frontmatter must be valid YAML and contain only:

- `name`
- `description`

Description rules:

- Write in third person.
- Front-load the task category and trigger words.
- Include explicit "Use when..." cases.
- Include "Do not use..." cases when confusion is likely.
- Mention important file types, commands, platforms, or workflows.
- Keep it concise enough to survive description truncation.
- Do not rely on body sections for trigger behavior; the body loads only after activation.

Good pattern:

```yaml
---
name: api-migration
description: Migrate API client code between versions with compatibility checks, tests, and rollback notes. Use when the user asks to upgrade an SDK, replace deprecated endpoints, update generated clients, or validate API migration risk. Do not use for general feature work unrelated to API version changes.
---
```

### 8. Make Resource Routing Explicit

If the skill includes resources, `SKILL.md` must say exactly when to use them.

Good routing:

- "For OAuth flows, read `references/oauth.md` before editing."
- "When changing generated clients, run `scripts/validate_codegen.py` after regeneration."
- "Use `assets/template.pptx` only when creating a new presentation."

Bad routing:

- "See references for more."
- "Use scripts as needed."
- "Check assets."

Keep reference nesting one level deep from `SKILL.md`.

### 9. Configure `agents/openai.yaml`

Create or update `agents/openai.yaml` when the repo convention includes it or the skill should be polished for Codex UI surfaces.

Use only supported fields unless the repository already uses a known extension:

```yaml
interface:
  display_name: "Human Name"
  short_description: "25-64 character summary"
  default_prompt: "Use $skill-name to ..."

policy:
  allow_implicit_invocation: true

dependencies:
  tools: []
```

Rules:

- Quote all string values.
- `default_prompt` must explicitly mention `$skill-name`.
- Use `allow_implicit_invocation: false` only for skills that should be manually invoked.
- Declare MCP dependencies only when the skill actually requires them.
- Do not use blanket dependencies.

### 10. Update Catalogs Only When Local Convention Requires It

Update a skill-root catalog only when it exists in the resolved skill root or immediate neighbors demonstrate that every skill is listed there.

If updating a catalog:

- add a short entry using the skill name and one-line purpose;
- preserve existing ordering when obvious;
- avoid duplicating the full `description`.

Do not create a new catalog unless the user asks.

### 11. Validate

Before finalizing:

1. Check the folder basename matches `name`.
2. Check frontmatter parses as YAML.
3. Check frontmatter contains `name` and `description`.
4. Check no scaffold TODO text remains.
5. Check all referenced resources exist.
6. Check scripts are executable or have clear invocation commands.
7. Run any added scripts' representative tests.
8. Run the Codex skill validator when available.

Validator guidance:

- Prefer the system `skill-creator` validator already available in the environment.
- Do not hard-code machine-specific validator paths in the skill.
- If a validator path is unknown, locate it from the available `$skill-creator` skill or use a local equivalent.
- If validation cannot be run, state that clearly and report the manual checks performed.

### 12. Forward-Test Complex Skills

Forward-test when the skill is complex, high-impact, tool-heavy, or likely to be reused often.

Use a fresh subagent or fresh task if available. The test prompt should look like a real user request:

```text
Use $skill-name at <path> to solve: <realistic task>.
```

Do not leak the intended answer. Review whether the agent:

- triggered the skill for the right reason;
- followed resource routing;
- respected safety boundaries;
- produced the requested output;
- avoided unnecessary files, tools, or broad scans.

Patch the skill if the forward-test exposes friction.

## Safety and Permissions

Default to safe skill authoring.

- Do not delete user files unless explicitly requested.
- Do not overwrite an existing skill without reading it first.
- Do not inspect unrelated skill roots when neighbor sampling is requested.
- Do not add network-dependent scripts unless the skill's purpose requires them.
- Do not add dependency installs or upgrades unless requested.
- Do not print secrets, tokens, or private config values.
- Do not claim validation, tests, or research were performed unless they were.
- Prefer proposed content over direct mutation when write access is not clearly allowed.

For skills that can mutate code, data, external services, or repositories, include explicit permission and destructive-action boundaries in the generated skill.

## Quality Checklist

A finished skill should satisfy:

- single clear purpose;
- precise discovery `description`;
- no unsupported frontmatter keys;
- durable rules separated from examples and task context;
- explicit tool-use and tool-non-use rules;
- bounded retry and stop behavior when tools are involved;
- resource routing only where useful;
- no hard-coded personal paths;
- no hidden chain-of-thought requirements;
- no unbounded search or retry loops;
- validation instructions that can actually be executed;
- final output contract that is easy to check.

## Output

When finished, report:

- skill path;
- what changed;
- resources added or removed;
- `agents/openai.yaml` status;
- catalog update status, if applicable;
- validation performed;
- assumptions or remaining risks.

Keep the final response concise. Include file links when available.
