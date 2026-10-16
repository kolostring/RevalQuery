# MutationState implements IMutationState, with the query side's member names

Status: Accepted
Date: 2026-10-16

## Context
`IMutationState` and `IMutationState<TResponse>` are public, but no type has implemented them
since the refactor in `3e8a650`. Their members (`IsError`, `IsSuccess`, `Error`) no longer
match `MutationState<TParams, TResponse>` (`IsException`, `IsResolved`, `Exception`). So the
library ships an abstraction for mutations that nothing can satisfy, while queries have a working
one (`IQueryState`, `IObservableQueryState`). Code that wants to wait on, render or log "any
mutation" has to name the concrete generic type with both type parameters.

## Decision
- `MutationState<TParams, TResponse>` implements `IMutationState<TResponse>`.
- The interface takes the concrete type's names, which are also the query side's:
  `IsIdle`, `IsFetching`, `IsResolved`, `IsException`, `Exception`, `Data`, `OnChanged`, `Reset()`.
- Its doc comments state the same contracts as `MutationState`.

## Considered options
- **Delete the interfaces**: rejected. Mutations would then lack the abstraction that queries
  have, and every helper that treats states uniformly (the test kit's `Signals`, a renderer, a
  logger) would need an overload per concrete generic.
- **Keep the interface's names (`IsError`, `IsSuccess`, `Error`) and rename `MutationState`**:
  rejected because `IQueryState` uses `IsException`, `IsResolved` and `Exception`. Mutations and
  queries should read alike, and renaming the concrete type would break every existing caller.

## Consequences
- Renaming the interface's members is a breaking change on paper only: no type implemented them,
  so no working code can have used them. It still goes in the migration notes.
- New members added to `MutationState` that describe its state belong on the interface too.
