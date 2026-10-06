# RevalQuery

Data fetching library for Blazor. Built on RevalQuery.Core.

Inspired by TanStack Query, RevalQuery provides type-safe async data fetching, caching, and state management for Blazor WebAssembly and Blazor Server.

## Quick Start

```razor
@using CachingDemo.Client.Services
@using RevalQuery.Blazor
@using RevalQuery.Core
@using RevalQuery.Core.Query.Options
@using RevalQuery.Core.Hooks
@rendermode InteractiveWebAssembly
@inject RevalHooks Reval

<RevalRenderer Component="this" Hooks="Reval" />

<PageTitle>Search Bar Example</PageTitle>

<div class="search-box">
    <div style="display: flex; gap: 8px;">
        <input @bind="SearchTerm" @bind:event="oninput" placeholder="Type to search..."/>

        @if (Suggestions.IsFetching)
        {
            <div>
                Loading...
            </div>
        }
    </div>

    @if (Suggestions.Exception is not null)
    {
        <p style="color:red">Error: @Suggestions.Exception.Message</p>
    }
    else if (Suggestions.Data is not null)
    {
        <ul>
            @foreach (var item in Suggestions.Data)
            {
                <li>@item</li>
            }
        </ul>
    }
    else if (!Suggestions.IsLoading)
    {
        <p><em>Nothing to show</em></p>
    }
</div>

@code {
    private string SearchTerm { get; set; } = string.Empty;

    IQueryState<List<string>> Suggestions => Reval.Query(
        QueryOptions
            .Create(
                ("search", SearchTerm),
                async static ctx => await SearchService.SearchAsync(ctx.Key.Item2))
            .ConfigureFetch(fetch => fetch
                .StaleTime(TimeSpan.FromMinutes(5))
            )
    );
}
```

## Table of Contents

- [Installation](#installation)
- [Component Integration](#component-integration)
- [Reval.Query](#revalquery)
- [Reval.Mutation](#revalmutation)
- [Known limitations](#known-limitations)
- [Migrating from QueryComponentBase](#migrating-from-querycomponentbase)
- [Optimistic updates](#optimistic-updates)
- [QueryFactory Pattern](#queryfactory-pattern)
- [QueryState Properties](#querystate-properties)
- [Reactive options](#reactive-options)
- [Prerender state transfer](#prerender-state-transfer)

---

## Installation

Register in both client and server Program.cs:

```csharp
// Server/Program.cs
builder.Services.AddRevalQuery();

// Client/Program.cs  
builder.Services.AddRevalQuery();
```

Both hosts need it: with prerendering on, your components run on the server first and in the
browser afterwards. See [Prerender state transfer](#prerender-state-transfer) for stopping the
browser refetching what the server already fetched.

---

## Component Integration

A component reads through a `RevalHooks`: one per component, injected because `AddRevalQuery`
registers it as transient, and handed to a `RevalRenderer` in the markup. The component inherits from nothing, so it can keep whatever base
class it already has.

```razor
@using RevalQuery.Blazor
@using RevalQuery.Core.Hooks
@inject RevalHooks Reval

<RevalRenderer Component="this" Hooks="Reval" />
```

`Component="this"` names the component to re-render, which is any `IHandleEvent`. The
`RevalRenderer` renders nothing. It re-renders the component when a query or mutation the hooks
read changes, reports each finished render to the hooks, and releases the hooks when the
component is disposed. The owner is passed explicitly because Blazor gives a child no reliable
reference to the component whose markup it is in. A forgotten `<RevalRenderer>` means the page
never re-renders and the hooks are never released.

Place the `RevalRenderer` once, at the top level of the component's markup, outside any `@if`,
`@foreach`, `AuthorizeView`, `ErrorBoundary`, tab panel or other wrapper component. Removing it
releases the hooks for good, and nothing re-attaches them. Razor allows several root nodes, so
this is always possible. A wrapper that always renders its child content still works, but is not
recommended.

Read through the hooks in a property or in the markup, on every render, and read the state it
returns afterwards. The hooks subscribe on the first read of a key, hands the options of every
later read to the same observer, and releases the query when a render stops reading it.

A read is identified by its call site, so a loop, or a helper method called with different keys,
holds one query per key. Two reads of one key share one observer. See
[Reval.Query](#revalquery) for the rules, and [Known limitations](#known-limitations) for what the
release rule cannot see.

---

## Reval.Query

Read a query - subscribes on the first read, re-applies the options on every later one, and
releases it when a render stops reading it.

```csharp
IQueryState<User[]> Users => Reval.Query(
    QueryOptions.Create<User[]>(
        "users",
        async static ctx =>
            await ctx.ServiceProvider.GetRequiredService<IUserService>().GetAll()
    )
);
```

It takes `QueryOptions` or a `QueryOptionsBuilder`, so everything from the
[QueryFactory pattern](#queryfactory-pattern) works unchanged.

**Release.** When a render completes, the hooks look at each call site that was read since the
previous render and releases the keys at that site that were not read. A key switch is the
common case: the old key is released after the render that read the new one, and its cached
data stays for `GcTime`. A call site that was not read at all releases nothing. Reads from event
handlers, or from content that renders in a later batch such as a popover, count towards the next
render's sweep, so such a read can delay a release by one render.

**Explicit slot.** `Reval.Query(slot, options)` identifies the read by a value of your choosing in
place of the call site, for the case in [Known limitations](#known-limitations). Any value compared
by value works, for example `"rows"` or `("rows", 0)`.

**Static handlers:** Handlers must be `static`. Using `static` ensures compilation error if the handler accidentally captures component state. This guarantees pure, stateless functions that won't cause memory leaks or stale closures.

```csharp
// Correct - static handler
handler: async static ctx => ...

// Compilation error with static - captures state
handler: async static ctx => someComponentField  // Compile error
```

---

## Reval.Mutation

Execute write operations (Create/Update/Delete). Supports callbacks.

```csharp
MutationState<CreateUserRequest, User> CreateUserMutation => Reval.Mutation(
    MutationOptions.Create<CreateUserRequest, User>(
        async static ctx =>
            await ctx.ServiceProvider.GetRequiredService<IUserService>().CreateAsync(ctx.Params)
    )
    .OnResolved(async (user, _) => Console.WriteLine($"Created {user.Name}"))
    .OnException(async (ex, _) => Console.WriteLine($"Error: {ex.Message}"))
);

// Trigger mutation
await CreateUserMutation.ExecuteAsync(new CreateUserRequest { Name = "John" });
```

The mutation is created on the first read, and every later render hands its options to the same
observer through `MutationObserver.SetOptions`. A mutation is never released by a render: it lives
until the hooks are released, so a run that outlasts the render that started it keeps its state.
Two mutations on one line, or one in a loop, share a call site and so share one mutation; use
`Reval.Mutation(key, options)` where each needs its own. A callback that
closes over something the render changed therefore sees the new value: the latest run reads the
new handler on its next retry attempt and the new callbacks when each fires. See
[Reactive options](#reactive-options).

---

## Known limitations

The release rule is a function of which call sites were read and when renders completed, and
nothing else: it reads no clock. That is why it cannot tell some things apart, and these two stand
until an alternative is found.

1. **A hidden branch keeps its queries.** A call site that is not read releases nothing, because
   the hooks cannot tell a branch that is hidden from one that has not rendered yet. Queries in
   an `@if` that turns false stay subscribed, and keep polling, until the hooks are released or
   the branch renders again. Put `Enabled(isVisible)` in the options so a hidden query stops
   fetching meanwhile:

   ```csharp
   Reval.Query(UserQueries.GetUserOptions(id).Enabled(isVisible))
   ```

2. **A call site read both in the page and inside an asynchronously loading child, with
   different keys, can thrash.** Each side's render releases what the other read, so the query
   is released and recreated. Give the child's read its own getter, or an explicit slot:

   ```csharp
   Reval.Query(("rows", 0), options)
   ```

---

## Migrating from QueryComponentBase

`QueryComponentBase`, `UseQuery` and `UseMutation` are gone.

| Before | After |
|--------|-------|
| `@inherits QueryComponentBase` | `@inject RevalHooks Reval` and `<RevalRenderer Component="this" Hooks="Reval" />` |
| `UseQuery(key: k, handler: h, o => o.Enabled(x))` | `Reval.Query(QueryOptions.Create(k, h).Enabled(x))` |
| `UseQuery(options)` | `Reval.Query(options)` |
| `UseMutation(options)` | `Reval.Mutation(options)` |
| `override void Dispose()` calling `base.Dispose()` | implement `IDisposable` and drop the `base` call; the renderer releases the hooks |

`Client` was an injected property of the base class. Keep `@inject RevalClient Client` where the page still calls `Client.Invalidate...` or `Client.QueryAsync`. `ServiceProvider` is gone: handlers receive one in their context.

`QueryClient` is now `RevalClient`: rename it in `@inject` lines and constructors. `RevalClient` handles queries and
mutations alike, which is why it takes the `Reval` prefix; see ADR 0011. The per-component type is `RevalHooks`
in `RevalQuery.Core.Hooks`.

---

## Optimistic updates

Writing the expected result into the query before the server confirms it. `IQueryState<T>.Data`
has a setter, so the write itself is a plain assignment. The part that needs care is the
refetch that may already be in flight: started before the mutation, it lands after it and
overwrites what you just wrote.

`RevalClient.CancelAsync` is the answer. It stops the in-flight fetch of every query under the
key prefix and completes once they have unwound, so once it returns nothing a cancelled fetch
produces can still reach the query. A handler that ignored its `CancellationToken` and returned
a value anyway has that value discarded.

```csharp
MutationState<Todo, Todo> AddTodoMutation => Reval.Mutation(
    MutationOptions.Create<Todo, Todo>(
        async static ctx => await ctx.ServiceProvider
            .GetRequiredService<ITodoService>().AddAsync(ctx.Params, ctx.CancellationToken))
    .OnMutate(async todo =>
    {
        // Nothing in flight can overwrite the write below once this returns.
        await Client.CancelAsync(TodoQueries.Key);

        var todos = Client.FindQuery<List<Todo>>(TodoQueries.Key)!;
        _rollback = todos.Data;
        todos.Data = [.. todos.Data ?? [], todo];
    })
    .OnException(async (_, _) =>
    {
        Client.FindQuery<List<Todo>>(TodoQueries.Key)!.Data = _rollback;
    })
    .OnSettled(async (_, _, _) => Client.Invalidate(TodoQueries.Key))
);
```

A cancelled query keeps the data it already had and records no error, so the optimistic value
stands until the `Invalidate` in `OnSettled` brings the real one back.

---

## QueryFactory Pattern

For reusability across components, define queries in static classes. This keeps query definitions centralized, makes keys consistent, and simplifies invalidation.

```csharp
public static class UserQueries
{
    private const string Token = "users";

    // For invalidation - centralized key definition
    public static (string, int) GetKey(int userId) => (Token, userId);

    // Returns builder - components can extend configuration
    public static QueryOptionsBuilder<(string, int), User> GetUserOptions(int userId)
    {
        return QueryOptions.Create(
            key: (Token, userId),
            handler: async static ctx =>
                await ctx.ServiceProvider.GetRequiredService<IUserService>().GetByIdAsync(ctx.Key.Item2)
        );
    }
}
```

**Usage in component:**

```csharp
IQueryState<User> User => Reval.Query(
    UserQueries.GetUserOptions(userId)
        .ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)))
        .ConfigureRetry(r => r.Retry(3))
);
```

**For invalidation:**

```csharp
Client.Invalidate(UserQueries.GetKey(userId));
```

Benefits:
- Single source of truth for query definitions
- Consistent key format across the app
- Easy bulk invalidation
- Components extend factory options via builder pattern

---

## QueryState Properties

Access query state via `IQueryState<T>`:

| Property | Description |
|----------|-------------|
| `Data` | The fetched data (null if pending/error) |
| `Exception` | The error if query failed |
| `IsPending` | No data yet |
| `IsResolved` | Data available |
| `IsException` | Query failed |
| `IsFetching` | Currently fetching. A restore is not a fetch, so this stays false during one |
| `IsRestoring` | Currently loading from persistence |
| `IsLoading` | No data yet and work in flight, fetch or restore: `(IsFetching \|\| IsRestoring) && IsPending` |
| `IsIdle` | Not fetching |
| `LastUpdatedAt` | Timestamp of last successful fetch |

`IsLoading` is the one to branch on when deciding between a spinner and an empty state. It
covers a load from persistence as well as a fetch, so a query waiting on storage renders a
spinner rather than an empty state it is about to replace. `IsRestoring` is there only to tell
waiting on storage apart from waiting on the network.

---

## Retry

`Retry(n)` means n further attempts after a failure, not n attempts in total. `Retry(0)` still
calls the handler once, and `Retry(3)`, the default for queries, calls it up to four times.
Mutations default to no retries.

```csharp
IQueryState<User> User => Reval.Query(
    UserQueries.GetUserOptions(userId)
        .ConfigureRetry(r => r.Retry(2))    // up to three calls
);
```

---

## Reactive options

Options are rebuilt on every render and handed to the key's observer on every read, through
`QueryObserver.SetOptions`. `Enabled`, `StaleTime`, `RefetchInterval`, retry and cache options
all take effect the moment a re-render changes them. The key decides which query the call
watches, and the observer follows it: when the key changes, `SetOptions` moves the observer to
the new key's query, releases the old one to the cache for its `GcTime`, and leaves any fetch
the old one had in flight to finish. There is nothing to dispose or resubscribe.

That is what makes the dependent-query pattern work. Render once disabled, and again enabled
once the value the key depends on arrives:

```csharp
IQueryState<Order[]> Orders => Reval.Query(
    QueryOptions
        .Create(
            ("orders", UserId),
            async static ctx =>
                await ctx.ServiceProvider.GetRequiredService<IOrderService>().ForUser(ctx.Key.Item2))
        .Enabled(UserId is not null)
);
```

Enabling a query fetches it if its data is stale, exactly as a query gaining its first
subscriber does. Disabling one stops its polling and leaves its cached data alone.

Fetch, retry and cache options belong to the query rather than to the component, so where two
components watch one key, the most recent render wins.

Mutations are re-optioned the same way. `Reval.Mutation` creates the mutation on the first read
and calls `SetOptions` on every read after. Runs that are already in flight are affected only
if they are the latest: it picks up the new handler for any further retry attempt and the new
callbacks as each fires, while its retry count stays what it was at the start. An older run still
running keeps the options it began with.

---

## Prerender state transfer

With prerendering on, which is the default for every interactive render mode, the server
renders your components and fetches their queries, its DI scope then ends, and the browser starts
with an empty cache and fetches the same keys over again. The transfer carries what the
prerender resolved across, so the browser renders from it instead.

Register it in **both** hosts, after `AddRevalQuery`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

// Metadata for every query result type you want transferred.
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(User[]))]
public partial class QueryJsonContext : JsonSerializerContext;
```

```csharp
builder.Services.AddRevalQuery();
builder.Services.AddRevalQueryPrerenderTransfer(
    new JsonSerializerOptions { TypeInfoResolver = QueryJsonContext.Default });
```

A source-generated context is required, not a suggestion. `AddRevalQueryPrerenderTransfer`
throws on options without a `TypeInfoResolver`, because the alternative is reflection-based
serialisation, which is trimmed out of a published WebAssembly app and fails there silently
after working perfectly in development. [ADR
0004](../docs/adr/0004-prerender-state-transfer-serialises-its-own-payload.md) has the detail.

What travels and what does not:

- Resolved queries travel. Pending and failed ones do not, so a failure is never handed to the
  client as though it were a result.
- `LastUpdatedAt` travels with the data. Data that was already stale when the prerender
  captured it is still stale on arrival and refetches immediately.
- With the default `StaleTime` of zero, every query is stale the moment it lands, so the
  transfer removes the loading flash but still revalidates. Set a `StaleTime` for the queries
  whose transferred data should be reused as is.
- A query key whose segments have no distinct string form is not transferred.

The transfer joins any `IQueryPersistence` you have registered rather than replacing it. A
load takes the first store holding the key, so register the transfer first to prefer data
fetched during this very request over a durable store's older copy.

---

## Configuration

Query lifecycle callbacks (onSuccess, onError, onSettled) are **NOT** supported on queries. Use `IQueryState` properties directly in components:

```csharp
@if (Users.IsResolved)
{
    <p>Loaded @Users.Data?.Length users</p>
}
@else if (Users.IsLoading)
{
    <p>Loading...</p>
}
@else if (Users.IsException)
{
    <p>Error: @Users.Exception.Message</p>
}
```

---

## License

MIT