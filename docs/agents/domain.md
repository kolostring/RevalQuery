# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root: the glossary.
- **`docs/adr/`**: read ADRs that touch the area you're about to work in.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill (reached via `/cfd:grill` and `/improve-codebase-architecture`) creates them lazily when terms or decisions actually get resolved.

## File structure

Single-context repo:

```
/
├── CONTEXT.md                  ← the glossary
├── docs/adr/
│   ├── 2026-09-28-persistence-as-an-interface-not-hybridcache-as-the-store.md
│   └── 2026-10-06-reval-prefix-for-types-that-span-queries-and-mutations.md
├── RevalQuery.Core/
└── RevalQuery.Blazor/
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `CONTEXT.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts the `cancelling-a-fetch-and-abandoning-a-wait-are-different-operations` ADR, but worth reopening because…_

## Context-first overrides

- Before exploring code, read the ADRs touching the area and `docs/CONVENTIONS.md`; explore code only for what they leave open.
- New ADRs are named `YYYY-MM-DD-<slug>.md` and follow the `cfd:adr` skill, which takes precedence over `domain-modeling`'s `ADR-FORMAT.md`. Cite an ADR by its slug in prose (the `<slug>` ADR) and by its full path in code comments.
