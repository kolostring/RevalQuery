# RevalQuery

Type-safe async data fetching and caching library for .NET. Inspired by TanStack Query.

## Packages

| Package | Description |
|---------|-------------|
| [RevalQuery.Core](./RevalQuery.Core) | Core library with RevalClient, QueryOptions, MutationOptions |
| [RevalQuery.Blazor](./RevalQuery.Blazor) | Blazor integration: `RevalRenderer` and an injected `RevalHooks` |

## Installation

Register in both client and server Program.cs:

```csharp
// Server/Program.cs
builder.Services.AddRevalQuery();

// Client/Program.cs
builder.Services.AddRevalQuery();
```

Both hosts need it: with prerendering on, components run on the server first and in the
browser afterwards. To stop the browser refetching what the server already fetched, add the
[prerender state transfer](./RevalQuery.Blazor/README.md#prerender-state-transfer).

## Quick Start (Blazor)

```razor
@using RevalQuery.Blazor
@using RevalQuery.Core.Hooks
@inject RevalHooks Reval

<RevalRenderer Component="this" Hooks="Reval" />

@code {
    IQueryState<User[]> Users => Reval.Query(
        QueryOptions.Create<User[]>(
            "users",
            async static ctx =>
                await ctx.ServiceProvider.GetRequiredService<IUserService>().GetAll())
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
@else if (Users.IsLoading)
{
    <p>Loading...</p>
}
```

A component reads through an injected `RevalHooks` and inherits from nothing. A hidden branch keeps its
queries until it renders again or the hooks are released, and a call site read by a page and by an asynchronously loading
child with different keys can thrash: see the
[known limitations](./RevalQuery.Blazor/README.md#known-limitations) and their workarounds.
Upgrading from `QueryComponentBase`? See the
[migration table](./RevalQuery.Blazor/README.md#migrating-from-querycomponentbase).

## Features

- **Type-safe queries** - ITuple-based keys, compile-time type safety
- **Hierarchical registry** - Queries organised by key path, with TTL eviction of the ones nobody is watching
- **Query factory pattern** - Centralized query definitions for reuse and easy invalidation
- **Static handler enforcement** - Compilation error if handlers capture component state
- **Plugin system** - Middleware-style extensibility for validation, logging, metrics
- **Concurrent mutations** - Multiple mutations run in parallel, latest result wins
- **Query toggling** - Enable/disable queries without losing cached data
- **Polling support** - Automatic refetch at configurable intervals
- **Retry with backoff** - Configurable exponential backoff, counted as retries after the first attempt
- **Optional persistence** - Point `IQueryPersistence` at a durable store to survive restarts, with `IsLoading` covering the restore
- **Prerender state transfer** - The browser renders what the prerender fetched instead of fetching it again

## Documentation

- [RevalQuery.Blazor README](./RevalQuery.Blazor/README.md)
- [RevalQuery.Core README](./RevalQuery.Core/README.md)
- [Glossary](./CONTEXT.md)
- [Architecture decision records](./docs/adr)
- [Examples](./examples) - runnable, and referencing the projects in this repo rather than a published version

## License

MIT