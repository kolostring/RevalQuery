# The OnMutate result is default when OnMutate did not complete

Status: Accepted
Date: 2026-10-19

## Context
`mutation-callbacks-take-one-context` types the `OnMutate` result in the later callbacks once
`.OnMutate<TOnMutateResult>(...)` is set. `OnMutate` runs inside the run's `try`, so when it
throws, `OnException` and `OnSettled` still run, with no result. Casting the missing value to
`TOnMutateResult` throws for a value type, and hands a rollback a `null` its type says cannot be
there. TanStack passes `onMutateResult` as `undefined` to `onError` and `onSettled` in that case.

## Decision
- On the typed exception and settled contexts, `OnMutateResult` is `TOnMutateResult?`. It holds
  `default` when `OnMutate` threw, and the callbacks still run.
- On the typed resolved context it is `TOnMutateResult`. A run can only resolve after `OnMutate`
  completed, so the value is always there. TanStack types `onSuccess` the same way.
- A callback that rolls back checks for `default` first. The README recipe does.

## Considered options
- **Skip `OnException` and `OnSettled` when `OnMutate` failed**: rejected. When callbacks fire
  would depend on where the failure happened, and `OnSettled` would no longer always run.
- **A `HasOnMutateResult` flag beside the value**: rejected. For a reference type it repeats
  what `null` already says, and a value type that needs the distinction can be wrapped by the
  caller.

## Consequences
- A value-type result cannot tell "OnMutate returned default" from "OnMutate did not
  complete". A caller that needs that returns a reference type or a nullable.
