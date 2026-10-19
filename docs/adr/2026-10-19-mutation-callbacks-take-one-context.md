# Mutation callbacks take one context, carrying the client and the OnMutate result

Status: Accepted
Date: 2026-10-19

## Context
Mutation callbacks receive positional data and parameters only:
`OnMutate(TParams)`, `OnResolved(TRes, TParams)`, `OnException(Exception, TParams)`,
`OnSettled(TRes?, Exception?, TParams)`. Two things are missing.

- **The client.** Invalidating, cancelling and setting all go through `RevalClient`, so a
  callback reaches it only by capturing a component's injected field. A mutation defined once in
  a static factory, as queries are, has nothing to capture.
- **A value from `OnMutate`.** `OnMutate` returns nothing, so the optimistic recipe keeps its
  rollback value in a component field. Two runs of one mutation share that field, and a failure
  in the first restores the second's value.

TanStack v5 passes `onMutate`'s return value to the other callbacks as `onMutateResult`, and
passes a `MutationFunctionContext` (`client`, `meta`, `mutationKey`) as the last argument to
`mutationFn` and every callback.

## Decision
- Every mutation callback, in the options and in per-call `MutateOptions`, takes one context
  object. It carries the params, the result or exception where the callback has one, the
  `OnMutate` result, and the `RevalClient`.
- The handler context (`MutationHandlerExecutionContext`) gains `Client`.
- `.OnMutate<TOnMutateResult>(...)` returns a builder that knows `TOnMutateResult`, so later
  callbacks receive it typed. It is stored per run. `MutationState<TParams, TRes>` keeps two
  type parameters.
- Naming follows TanStack: the value is `OnMutateResult`. "Context" means only the object passed
  to the handler and callbacks.
- A callback chained onto a builder replaces the one already there. A factory returns the
  builder and defines what the mutation always does. A component that wants extras adds them
  as per-call callbacks, which run after the options' callbacks and only for the latest run.

## Considered options
- **TanStack's positional shape, with the context last**: rejected. C# lambdas cannot name or
  destructure positional arguments, so `(_, _, _, _, ctx) =>` replaces what `ctx =>` says.
- **A third type parameter on `MutationOptions` and `MutationState`**: rejected because every
  mutation would name a type it rarely uses, including in the component's property type.
- **`OnMutate` returning `object?`**: rejected because every callback would cast.
- **Chained callbacks run alongside the base**: rejected. A component could not drop a base
  callback, and the order would become part of the API. TanStack splits the same way, options
  callbacks and per-call callbacks.

## Consequences
- Breaking on 0.x: every callback signature changes.
- Mutations can be defined in factories that invalidate and set data without injection.
- Per-run storage of the `OnMutate` result fixes the shared-rollback defect.
- A component overriding a factory callback takes over everything that callback did. The
  factory recipe in the README says so.
