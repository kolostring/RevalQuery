# RevalQuery

Type-safe async data fetching and caching library for .NET. Inspired by TanStack Query.

## Packages

| Package | Description |
|---------|-------------|
| [RevalQuery.Core](./RevalQuery.Core) | Core library with QueryClient, QueryOptions, MutationOptions |
| [RevalQuery.Blazor](./RevalQuery.Blazor) | Blazor integration with UseQuery, UseMutation |

## Installation

Register in both client and server Program.cs:

```csharp
// Server/Program.cs
builder.Services.AddRevalQuery();

// Client/Program.cs
builder.Services.AddRevalQuery();
```

## Quick Start (Blazor)

```razor
@using RevalQuery.Blazor
@inherits QueryComponentBase

@code {
    IQueryState<User[]> Users => UseQuery(
        key: ("users",),
        handler: async static ctx =>
            await ctx.ServiceProvider.GetRequiredService<IUserService>().GetAll()
    );
}

@if (Users.IsResolved)
{
    <ul>
        @foreach (var user in Users.Data)
        {
            <li>@user.Name</li>
        }
    </ul>
}
@else if (Users.IsFetching)
{
    <p>Loading...</p>
}
```

## Features

- **Type-safe queries** - ITuple-based keys, compile-time type safety
- **Automatic caching** - Trie-based hierarchical cache with TTL eviction
- **Query factory pattern** - Centralized query definitions for reuse and easy invalidation
- **Static handler enforcement** - Compilation error if handlers capture component state
- **Plugin system** - Middleware-style extensibility for validation, logging, metrics
- **Concurrent mutations** - Multiple mutations run in parallel, latest result wins
- **Query toggling** - Enable/disable queries without losing cached data
- **Polling support** - Automatic refetch at configurable intervals
- **Retry with backoff** - Configurable exponential backoff retry policy

## Documentation

- [RevalQuery.Blazor README](./RevalQuery.Blazor/README.md)
- [RevalQuery.Core README](./RevalQuery.Core/README.md)

## License

MIT