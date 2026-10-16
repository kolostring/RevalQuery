# RevalQuery

## Agent skills

### Issue tracker

GitHub Issues on kolostring/RevalQuery, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: glossary in `CONTEXT.md`, ADRs in `docs/adr/`. See `docs/agents/domain.md`.

## Context-first

- Orient from `.claude/STATUS.local.md`, the ADRs in `docs/adr/` and `docs/CONVENTIONS.md`. Read code to answer a question they leave open, or to change a file.
- Code is the truth about what the system does; ADRs are the truth about why. When they disagree, stop and flag it.
- A decision that constrains future code gets an ADR (`cfd:adr` format) before the code. Mid-implementation, stop and propose the ADR before writing that code.
- As a subagent, never make such a decision: stop and report it, with options and a recommendation, to the parent, which asks the user.
- When corrected on a pattern, record the rule: a check if mechanical, `docs/CONVENTIONS.md` if a judgement call.
- Sessions: `/cfd:start` to begin, `/cfd:close` to end.
