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

---

## Installation

Register in both client and server Program.cs:

```csharp
// Server/Program.cs
builder.Services.AddRevalQuery();

// Client/Program.cs  
builder.Services.AddRevalQuery();
```

Required for Blazor WebAssembly prerendering support.

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
| `IsFetching` | Currently fetching |
| `IsLoading` | Fetching + no data (IsFetching && IsPending) |
| `IsIdle` | Not fetching |
| `LastUpdatedAt` | Timestamp of last successful fetch |

---

## Configuration

Query lifecycle callbacks (onSuccess, onError, onSettled) are **NOT** supported on queries. Use `IQueryState` properties directly in components:

```csharp
@if (Users.IsResolved)
{
    <p>Loaded @Users.Data?.Length users</p>
}
@else if (Users.IsFetching)
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