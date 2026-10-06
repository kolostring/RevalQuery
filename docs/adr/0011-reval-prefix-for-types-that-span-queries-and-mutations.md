---
status: accepted
---

# Types that span queries and mutations take the Reval prefix

The per-component type went through three names: `QueryScope`, then `QueryTracker`, then
`QuerySubscriber`. Each carried a `Query` prefix, and each read wrong the moment it was used for a
mutation: `Queries.Mutation(...)` says "a query's mutation", and the injected variable name made it
worse. The type handles queries and mutations alike, and so does `QueryClient`, so a `Query`
prefix on either is a half-truth that every call site repeats.

## Decision

The prefix says what a type covers.

- `Reval...` for types that span queries and mutations: `RevalClient`, `RevalHooks`,
  `RevalRenderer`.
- `Query...` and `Mutation...` for types about only one kind: `QueryObserver`, `QueryOptions`,
  `MutationState`, and the rest.

`QueryClient` becomes `RevalClient`, following the precedent of Apollo's `ApolloClient`, a client
named for the library and not for one of the things it does.

The per-component type is `RevalHooks`, in `RevalQuery.Core.Hooks`, created with
`client.CreateHooks()`. The name is literal: it is hooks without React. State is per component and
identified by call site, it is read on every render, and it is released when nothing reads it any
more. React supplies the lifecycle that makes that work; `RevalRenderer`, the Blazor adapter, supplies
it here by re-rendering the component, reporting each finished render, and releasing the hooks with
the component.

The documentation names the injected variable `Reval`:

```razor
@inject RevalHooks Reval
<RevalRenderer Component="this" Hooks="Reval" />

@code {
    IQueryState<List<Product>> Products => Reval.Query(ProductQueries.All());
}
```

so a read is `Reval.Query(...)` and a write is `Reval.Mutation(...)`. Services and imperative code
inject `RevalClient` and call `Client.QueryAsync(...)`.

## Considered options

`QueryScope` was rejected because a scope suggests something disposable that you open and close, and
the type is deliberately not `IDisposable`, so that DI does not hold every instance for the
container's lifetime. `QueryTracker` and `QuerySubscriber` were the next names and failed the same
test as the first: a `Query` prefix on something that handles mutations. `QueriesObserver` was
rejected because "observer" is reserved for the single-query and single-mutation observers; hooks
hold many of them, and an observer is not a hook.

Making `QueryClient` itself per-component was rejected. The client holds the one registry per user
session and must be shared, while the per-component type must be transient and released with the
component. One type cannot be both without disposal and DI rules that depend on how it was
resolved, and it would put the per-render surface next to the whole imperative API.

A static, ambient `Query(...)` available on every component was rejected because it needs runtime
subclassing or a base class, which ADR 0010 removed.

## Consequences

`QueryClient` becoming `RevalClient` is a breaking rename for existing users, and goes in the
migration notes. Anyone coming from TanStack Query loses the familiar `QueryClient` name; the
cost is accepted for names that stay accurate at the call site. ADRs 0001 to 0010 keep
`QueryClient` where they use it, as history.
