# RevalQuery.Core

Type-safe async data fetching and caching library. Used by RevalQuery.Blazor.

## Installation

```csharp
// Server/Program.cs (for Blazor WebAssembly with prerendering)
builder.Services.AddRevalQuery();

// Client/Program.cs
builder.Services.AddRevalQuery();
```

## Table of Contents

- [QueryClient](#queryclient)
- [QueryOptions](#queryoptions)
- [MutationOptions](#mutationoptions)
- [QueryFactory Pattern](#queryfactory-pattern)
- [Configuration](#configuration)
- [Plugin System](#plugin-system)

---

## QueryClient

Main entry point for query management.

```csharp
public sealed class QueryClient
{
    // Subscribe component to query - returns observer. Call observer.SetOptions(options) on
    // every render and the observer follows the key.
    public QueryObserver<TKey, TRes> Subscribe<TKey, TRes>(
        QueryOptions<TKey, TRes> options,
        Action onStateHasChanged
    );

    // Subscribe the first time, SetOptions every time after. Returns the state to read from.
    public IQueryState<TRes> Observe<TKey, TRes>(
        ref QueryObserver<TKey, TRes>? slot,
        QueryOptions<TKey, TRes> options,
        Action onStateHasChanged
    );

    // The whole imperative surface. Serves fresh cached data without calling the handler,
    // fetches otherwise, throws on error. The token abandons the wait, not the fetch.
    public async Task<TRes> QueryAsync<TKey, TRes>(
        QueryOptions<TKey, TRes> options,
        CancellationToken cancellationToken = default
    );

    // Invalidate cache
    public void Invalidate(ITuple key);
    public void Invalidate(string key);

    // Cancel in-flight fetches under a key prefix, completing once they have unwound
    public Task CancelAsync(ITuple key);
    public Task CancelAsync(string key);
}
```

### Observing from your own component

An observer is one component's subscription to whichever query its current options name.
`QueryObserver.SetOptions` is called on every render: against the same key it re-applies
`Enabled`, `StaleTime`, `RefetchInterval`, retry and cache options, and against a new key it
moves the observer to that query. The old query is released the way a disposal releases it, so
it stays cached for its `GcTime`, and a fetch it had in flight is left to finish. The observer
changes, `observer.Query` is a different object afterwards, and the caller holds one slot for
the life of the component.

`Observe` is the whole render step. Outside `QueryComponentBase`, keep a field per query as the
slot, read it through a property, and dispose the slots with the component:

```csharp
public sealed class ProductPage : ComponentBase, IDisposable
{
    [Inject] QueryClient Client { get; set; } = null!;
    [Parameter] public int CategoryId { get; set; }

    private QueryObserver<(string, int), List<Product>>? _products;

    // ByCategory returns the built QueryOptions<(string, int), List<Product>>
    IQueryState<List<Product>> ProductList =>
        Client.Observe(ref _products, ProductQueries.ByCategory(CategoryId), Rerender);

    private void Rerender() => _ = InvokeAsync(StateHasChanged);

    public void Dispose() => _products?.Dispose();
}
```

When `CategoryId` changes the next read of `ProductList` switches the slot to the new category's
query and returns its state, fetching if its data is stale. `Dispose` is safe to call more
than once. `SetOptions` after the observer or the client is disposed throws
`ObjectDisposedException`, and a key already registered with another result type throws
`InvalidOperationException` with the observer left on the query it was on.

### Cancelling

`CancelAsync` stops the in-flight fetch of every query under the key prefix and completes once
they have unwound. It matches by prefix, the same set as `Invalidate`, so the two can be used
on the same key. A cancelled query keeps the data it already had and records no error: a fetch
that was stopped never learned anything. A result the handler produces anyway, because it never
read its `CancellationToken`, is discarded rather than applied.

Awaiting it is what makes an optimistic update safe. A refetch started before the mutation would
otherwise land after it and overwrite the optimistic value:

```csharp
// Stop anything in flight first, and wait for it to unwind. Once this returns, no result
// from a cancelled fetch can still reach the query.
await client.CancelAsync(("todos",));

var todos = client.FindQuery<List<Todo>>(("todos",))!;
var previous = todos.Data;
todos.Data = [.. previous!, newTodo];

try
{
    await SaveAsync(newTodo);
}
catch
{
    todos.Data = previous;
    throw;
}
finally
{
    client.Invalidate(("todos",));
}
```

`IQueryState.Cancel()` is the low-level trigger for one query. It returns immediately without
waiting for the fetch to unwind.

The `CancellationToken` on `QueryAsync` does something different: it abandons the wait, not
the fetch. Cancelling it ends the await with an `OperationCanceledException` while the fetch runs
on, and its result still lands in the registry. That is deliberate. The fetch is shared with
every other caller joined to it, and an autocomplete that abandons a request on the next
keystroke still wants the answer cached for the backspace that follows. Use `CancelAsync` when
you mean to stop the fetch itself.

---

## QueryOptions

Immutable query configuration.

```csharp
// Create with fluent builder
var options = QueryOptions.Create(
    key: ("users",),
    handler: async static ctx => await ctx.ServiceProvider.GetRequiredService<IUserService>().GetAll()
);

// Extend configuration
options.ConfigureFetch(f => f.StaleTime(TimeSpan.FromMinutes(5)));
options.ConfigureFetch(f => f.NeverStale()); // or: never refetch for age, see Freshness
options.ConfigureRetry(r => r.Retry(3));   // three retries, so up to four calls
options.ConfigureCache(c => c.GcTime(TimeSpan.FromMinutes(10)));
options.Enabled(true);
```

---

## MutationOptions

Configuration for write operations. Supports callbacks.

```csharp
var mutationOptions = MutationOptions.Create<CreateUserRequest, User>(
    async static ctx => await ctx.ServiceProvider.GetRequiredService<IUserService>().CreateAsync(ctx.Params)
)
.OnResolved(async (user, req) => Console.WriteLine($"Created {user.Name}"))
.OnException(async (ex, req) => Console.WriteLine($"Error: {ex.Message}"))
.OnSettled(async (data, ex, req) => Console.WriteLine("Mutation complete"))
.ConfigureRetry(r => r.Retry(3));
```

---

## QueryFactory Pattern

Standardized way to organize and reuse queries (ADR-016).

```csharp
public static class UserQueries
{
    private const string Token = "users";

    // For invalidation
    public static (string, int) GetKey(int userId) => (Token, userId);

    // Returns builder - can be extended in components
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

**Usage:**
```csharp
// In component or QueryClient
var observer = client.Subscribe(UserQueries.GetUserOptions(userId).Build(), OnStateChanged);

// For invalidation
client.Invalidate(UserQueries.GetKey(userId));
```

---

## Configuration

Global options via `RevalQueryOptions`.

```csharp
services.AddRevalQuery(options =>
{
    // Default fetch options (RefetchInterval, StaleTime, Static)
    options.FetchOptions = new CoreFetchOptions(
        RefetchInterval: TimeSpan.FromMinutes(5),
        StaleTime: TimeSpan.FromMinutes(1),
        Static: false
    );

    // Default retry options: three retries after a failure, so up to four calls
    options.RetryOptions = new CoreRetryOptions(3, attempt => TimeSpan.FromSeconds(attempt));

    // Cache TTL options
    options.CacheOptions = new CoreCacheOptions(
        GcTime: TimeSpan.FromMinutes(10),
        GcInterval: TimeSpan.FromMinutes(2)
    );

    // Add plugins
    options.QueryPluginsPipeline.Add(new MyPlugin());
});
```

---

## Freshness

`StaleTime` is how long data is reused without refetching. It defaults to **zero**, so data is
stale the moment it lands and the next subscriber refetches.

Staleness belongs to the data, not to whether anything is observing it, so every entry point
asks the same question and gets the same answer. A component subscribing and a `QueryAsync`
call looking at one key agree about whether it needs refetching.

A query is stale when any of these holds, asked in this order:

1. It has never successfully fetched.
2. It is **not** static, and it was invalidated.
3. It is **not** static, and its data is older than `StaleTime`.

### Static queries

`NeverStale()` declares that data never goes stale. It is not the same as a very long
`StaleTime`, and the difference is the ordering above: a static query also ignores
`Invalidate`, which no duration achieves.

```csharp
options.ConfigureFetch(f => f.NeverStale());
```

Use it for data that cannot change within a session — a currency list, a country table, a
feature flag snapshot pinned at login. A static query still fetches once, because having no
data is checked before staleness, and a polling `RefetchInterval` still drives it if one is
set.

What it costs you is the ability to refresh that key on demand. `Invalidate` will not move it,
deliberately, and it will go on reporting `IsInvalidated` as true after one. To refresh a
static query, subscribe to it with options that do not declare it static, or change its key.

Which options are in force is worth being precise about. A subscriber's are adopted by the
query when it subscribes and again on every re-render, so the most recent render wins. A
`QueryAsync` call's are adopted only if that call creates the query; against one that already
exists they decide that call alone. A loader can therefore read a key `NeverStale()` without
making it static for the components that subscribe to it.

### GcTime bounds all of this

`GcTime` is how long a query with no observers survives in the registry. If it is shorter than
the window you expect to serve from cache, the entry is evicted before it is ever reused.

The defaults do not collide — `StaleTime` zero against a `GcTime` of five minutes — but they
do not protect you either. Raise `StaleTime` towards `GcTime` and the two start racing, and a
query reached only through `QueryAsync` is the worst case: nothing subscribes to it, so it
joins the eviction list the moment its fetch settles.

A static query has no freshness window to compare against, so `GcTime` is the only thing
bounding its cache lifetime. Raise it to match how long you actually want the data kept.

```csharp
options
    .ConfigureFetch(f => f.NeverStale())
    .ConfigureCache(c => c.GcTime(TimeSpan.FromHours(1)));
```

---

## Retry

`Retry` counts retries, not attempts. A retry is one further attempt after a failed one, so it
never includes the first: `Retry(0)` still calls the handler once and surfaces its exception,
and `Retry(3)` calls it up to four times. Queries default to 3 retries, mutations to none.

The delay between retries comes from `RetryDelay`, which maps a retry's one-based number to the
wait before it. The default backs off exponentially, capped at 30 seconds.

---

## Persistence

Query data lives in memory and disappears with the session. Implement
`IQueryPersistence` and register it to keep a durable copy. A query loads from the
store when it is created and saves to it after every successful fetch.

```csharp
public class LocalStoragePersistence : IQueryPersistence
{
    public ValueTask<PersistedQuery<TRes>?> LoadAsync<TRes>(ITuple key, CancellationToken ct = default) { ... }

    public ValueTask SaveAsync<TRes>(ITuple key, PersistedQuery<TRes> entry, CancellationToken ct = default) { ... }
}

services.AddScoped<IQueryPersistence, LocalStoragePersistence>();
```

An adapter must complete or fault, and must never hang. The library awaits a load with no
timeout by decision, so an adapter that never returns stalls its query for the life of the
process. Apply your own timeout inside the adapter and fault instead: a faulted load leaves the
query to fetch as though nothing was stored, which is recoverable, and a hung one is not.

While a load is outstanding the query reports `IsRestoring`, and `IsLoading` covers it, so a
component waiting on storage renders a spinner rather than an empty state. `IsFetching` stays
false: a restore is not a fetch.

Adapters own serialisation and their own key encoding; the library never
serialises. `LastUpdatedAt` is stored and restored verbatim, so data that was
already stale when the process stopped refetches on the next start rather than
appearing fresh.

Several stores may be registered. A load takes the first one holding the key, so
registration order is preference order, and a save goes to all of them. That is what
lets the Blazor prerender state transfer sit alongside a durable store of your own
instead of replacing it.

---

## Lifetimes and threads

`AddRevalQuery` registers `QueryClient` and the eviction policy as scoped, and they
share one registry. That is deliberate: one registry per user session is what keeps
one user's data out of another's. Registering either as a singleton in a server
process leaks data between users.

`QueryClient` is safe to call from any thread. Observer callbacks are not
synchronised, because `QueryComponentBase` already routes them through
`InvokeAsync`.

Disposal is enforced asymmetrically. Once the client is disposed, anything a live render
reaches reports `ObjectDisposedException`: `Subscribe`, `QueryAsync`, `GetOrCreateQuery`,
`Observe` and `QueryObserver.SetOptions`. `QueryAsync` is async, so it faults its task rather than throwing from the
call. Tearing down stays silent: unsubscribing, cancelling
and disposing an observer are no-ops. Blazor does not specify whether component disposal
runs before or after the DI scope that owns the client, so a component tearing down second
must not throw.

---

## Plugin System

Extensibility via `IQueryPlugin` middleware.

```csharp
public class MyPlugin : IQueryPlugin
{
    public QueryOptions<TKey, TRes> OnQueryInitialize<TKey, TRes>(
        QueryOptions<TKey, TRes> options,
        Func<QueryOptions<TKey, TRes>, QueryOptions<TKey, TRes>> next
    )
    {
        // Transform or validate options
        return next(options);
    }
}
```

Built-in validation plugin:
```csharp
// In development - enforces static handlers
services.AddSingleton<IQueryPlugin, QueryPluginHandlersStatelessValidation>();
```

---

## License

MIT