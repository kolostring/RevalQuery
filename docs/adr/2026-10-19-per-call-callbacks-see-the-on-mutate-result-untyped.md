# Per-call callbacks see the OnMutate result untyped

Status: Accepted
Date: 2026-10-19

## Context
`mutation-callbacks-take-one-context` gives the options callbacks a typed `OnMutateResult`
through the builder `.OnMutate<TOnMutateResult>(...)` returns, and says per-call callbacks take
the same context. Per-call `MutateOptions<TParams, TRes>` is not built from that builder, and
has no way to know the type.

## Decision
- Per-call callbacks receive `OnMutateResult` as `object?`.
- A factory that returns the typed builder can be passed straight to `Reval.Mutation`, which
  has an overload for it, so a component never calls `.Build()` itself.

## Considered options
- **A third type parameter on `MutateOptions`**: rejected for the reason the parent ADR rejects
  it on `MutationOptions`: every per-call site would name a type it rarely uses.
- **A separate typed per-call options type**: rejected for now. Rollback belongs in the
  factory's callbacks, and per-call extras (navigation, toasts) rarely read the result.

## Consequences
- A per-call callback that does read the result casts it.
