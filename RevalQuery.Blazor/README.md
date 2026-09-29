# RevalQuery

Data fetching library for Blazor. Built on RevalQuery.Core.

Inspired by TanStack Query, RevalQuery provides type-safe async data fetching, caching, and state management for Blazor WebAssembly and Blazor Server.

## Quick Start

```razor
@using CachingDemo.Client.Services
@using RevalQuery.Core
@rendermode InteractiveWebAssembly
@inherits RevalQuery.Blazor.QueryComponentBase

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

    @if (Suggestions.Error is not null)
    {
        <p style="color:red">Error: @Suggestions.Error.Message</p>
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
    else if (!Suggestions.IsFetching)
    {
        <p><em>Nothing to show</em></p>
    }
</div>

@code {
    private string SearchTerm { get; set; } = string.Empty;

    IQueryState<List<string>> Suggestions => UseQuery(
        key: ("search", SearchTerm),
        handler: async static ctx =>
        {
            var res = await SearchService.SearchAsync(ctx.Key.SearchTerm);
            return QueryResult.Success(res);
        },
        options => options
            .ConfigureFetch(fetch => fetch
                .StaleTime(TimeSpan.FromMinutes(5))
            )
    );
}
```

## Table of Contents

- [Installation](#installation)
- [Component Integration](#component-integration)
- [UseQuery](#usequery)
- [UseMutation](#usemutation)
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

Inherit from `QueryComponentBase`:

```razor
@inherit QueryComponentBase
```

---

## UseQuery

Subscribe to a query - observes data and manages lifecycle automatically.

```csharp
IQueryState<User[]> Users => UseQuery(
    key: ("users",),
    handler: async static ctx =>
        await ctx.ServiceProvider.GetRequiredService<IUserService>().GetAll()
);
```

**Static handlers:** Handlers must be `static`. Using `static` ensures compilation error if the handler accidentally captures component state. This guarantees pure, stateless functions that won't cause memory leaks or stale closures.

```csharp
// Correct - static handler
handler: async static ctx => ...

// Compilation error with static - captures state
handler: async static ctx => someComponentField  // Compile error
```

---

## UseMutation

Execute write operations (Create/Update/Delete). Supports callbacks.

```csharp
MutationState<CreateUserRequest, User> CreateUserMutation => UseMutation(
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
IQueryState<User> User => UseQuery(
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
IQueryState<User> User => UseQuery(
    UserQueries.GetUserOptions(userId)
        .ConfigureRetry(r => r.Retry(2))    // up to three calls
);
```

---

## Reactive options

Options are rebuilt on every render and applied on every render. `Enabled`, `StaleTime`,
`RefetchInterval`, retry and cache options all take effect the moment a re-render changes
them; only the key decides which query the call watches.

That is what makes the dependent-query pattern work. Render once disabled, and again enabled
once the value the key depends on arrives:

```csharp
IQueryState<Order[]> Orders => UseQuery(
    key: ("orders", UserId),
    handler: async static ctx =>
        await ctx.ServiceProvider.GetRequiredService<IOrderService>().ForUser(ctx.Key.Item2),
    options => options.Enabled(UserId is not null)
);
```

Enabling a query fetches it if its data is stale, exactly as a query gaining its first
subscriber does. Disabling one stops its polling and leaves its cached data alone.

Fetch, retry and cache options belong to the query rather than to the component, so where two
components watch one key, the most recent render wins.

---

## Prerender state transfer

With prerendering on, which is the default for every interactive render mode, the server
renders your components and fetches their queries, its scope then ends, and the browser starts
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