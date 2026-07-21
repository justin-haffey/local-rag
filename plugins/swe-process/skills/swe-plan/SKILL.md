---
name: swe-plan
description: Creates a governed phase implementation plan and its associated feature plans from approved SWE design and ADR artifacts. Use when the user asks to plan a delivery phase, release increment, or multiple related features. Do not use when only one standalone feature plan is needed or to implement code.
---
# SWE Phase Planning

Translate design and decisions into a bounded phase plan with independently reviewable feature plans and verification gates.

## Core Contract

- Work from the repository root containing `.swe/`; record links with repository-relative forward-slash paths.
- Templates: copy `references/PLAN-##-SHORT-NAME.md` to `.swe/03-PLAN/PLAN-##-SHORT-NAME.md` and `references/FEATURE-##-TEMPLATE.md` to each `.swe/04-FEATURE/FEATURE-##-feature-name.md`; treat both source templates as immutable.
- Canonical outputs: save the phase plan only as `.swe/03-PLAN/PLAN-##-SHORT-NAME.md` and each feature plan only as `.swe/04-FEATURE/FEATURE-##-FEATURE-NAME.md`.
- A plan is a review candidate, not authorization to write implementation code, modify infrastructure, deploy, or release.
- Use only supported facts from the design, ADRs, user request, and existing repository evidence. Mark incomplete requirements and unresolved decisions rather than guessing.

## Resource Routing

- Before drafting, read `references/PLAN-##-SHORT-NAME-TEMPLATE.md` and `references/FEATURE-##-TEMPLATE.md` in full.
- Copy only these skill-local, immutable templates to their canonical `.swe/` outputs. Do not edit the reference files or place deliverables beside them.

## Workflow

1. Read `.codex/AGENTS.md`, `.swe/01-DESIGN/DESIGN.md`, and all relevant accepted or proposed ADRs under `.swe/02-ADR/`. If the design is missing or unsuitable for the requested scope, stop and request or recommend `$swe-design` first.
2. Inspect `.swe/03-PLAN/` and `.swe/04-FEATURE/`; choose the next unused plan and feature IDs without renumbering existing artifacts. If the requested destination already exists, stop and ask before revising it.
3. Define the phase boundary, measurable objectives, non-goals, dependencies, delivery sequence, risks, and release gate. Split the requested work into independently verifiable features; do not use one feature row for an entire phase.
4. Copy `references/PLAN-##-SHORT-NAME-TEMPLATE.md` to `.swe/03-PLAN/PLAN-##-SHORT-NAME.md`. Replace applicable placeholders and template-only notes. Populate the feature registry, feature blocks, stable requirement IDs, cross-cutting requirements, test commands, scenario matrix, and acceptance criteria.
5. For every feature in the phase registry, copy `references/FEATURE-##-TEMPLATE.md` to `.swe/04-FEATURE/FEATURE-##-FEATURE-NAME.md`. Populate its purpose, scope, rules, flows, touchpoints, positive/negative/edge verification, Definition of Done, and links to its phase plan and ADRs.
6. Cross-link plan, features, design, and ADRs. The canonical directories are `.swe/03-PLAN/` and `.swe/04-FEATURE/`.
7. Check that each requirement maps to verification, that each feature is independently implementable, and that no plan claims testing, approval, or implementation which did not occur.

## Path and File Rules

- Inputs: `.swe/01-DESIGN/DESIGN.md`, relevant `.swe/02-ADR/*.md`, and task-specific repository evidence.
- Template sources: `references/PLAN-##-PHASE#-TEMPLATE.md` and `references/FEATURE-##-TEMPLATE.md`; never edit them.
- Outputs: one `.swe/03-PLAN/PLAN-##-PHASE#-short-name.md` plus multiple `.swe/04-FEATURE/FEATURE-##-feature-name.md` per registered feature.
- Never overwrite artifacts. Keep stable IDs and permit gaps. Use forward-slash, repository-relative links.

## Validation and Output

Confirm the plan and each feature plan use the correct template, reside in canonical folders, contain no unaddressed template placeholders, link upstream sources, and map requirements to POS/NEG/EDGE verification. Report the created paths, feature IDs, unresolved decisions, assumptions, and review gate. State that implementation did not occur.
