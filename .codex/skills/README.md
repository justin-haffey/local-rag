# Repo Skills

This repository stores project-local Codex skills in `.codex/skills/`.

This project keeps its checked-in skill assets under `.codex/skills/` so skills, commands, and agent assets live under the same top-level Codex directory.

## Directory Contract

```text
.codex/
  skills/
    <skill-name>/
      SKILL.md
      scripts/
      references/
      assets/
      agents/
        openai.yaml
```

- `SKILL.md` is required and must include `name` and `description` front matter.
- `scripts/`, `references/`, and `assets/` are optional.
- `agents/openai.yaml` is optional metadata for appearance, dependencies, and invocation policy.
- Folders whose names start with `_` are scaffolding folders, not runnable skills.

## Working Conventions

- Keep each skill focused on one repeatable workflow.
- Prefer instructions over scripts unless deterministic behavior is necessary.
- Write descriptions with clear trigger boundaries so implicit invocation stays predictable.
- Put reusable reference material in `references/` instead of overloading `SKILL.md`.
- Keep skill names lowercase and hyphenated unless an established external name is clearer.

## Available Project Skills

**`.codex/skills/`**

- `new-agent`: Create project-scoped Codex agents and register them in this repository.
- `new-skill`: Create project-local Codex skills under `.codex/skills/`.
- `orchestrate`: Plan and execute multi-step work with scoped subagents, explicit workflows, and validation.
- `analyze`: Route a document-analysis request to the smallest sufficient set of specialist methods.


## Creating Skills

- Use the built-in `$skill-creator` when you want Codex to interview you and draft a skill.
- Use the project-local `new-skill` skill or `/new-skill <skill-name>` when you want this repository to scaffold a project-local skill folder and template files.
- Copy from `.codex/skills/_templates/skill-template/` if you want to scaffold manually.

## Example Front Matter

```yaml
---
name: example-skill
description: Explain exactly when this skill should and should not trigger.
---
```
