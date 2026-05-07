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
    // Subscribe component to query - returns observer
    public QueryObserver<TRes> Subscribe<TKey, TRes>(
        QueryOptions<TKey, TRes> options,
        Action onStateHasChanged
    );

    // Fire-and-forget prefetch
    public void PrefetchQuery<TKey, TRes>(QueryOptions<TKey, TRes> options);

    // Awaitable fetch - throws on error
    public async Task<TRes> FetchQueryAsync<TKey, TRes>(QueryOptions<TKey, TRes> options);

    // Invalidate cache
    public void Invalidate(ITuple key);
    public void Invalidate(string key);

    // Cancel in-flight fetch
    public void Cancel(ITuple key);
}
```

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
options.ConfigureRetry(r => r.Retry(3));
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
var query = client.Subscribe(UserQueries.GetUserOptions(userId), OnStateChanged);

// For invalidation
client.Invalidate(UserQueries.GetKey(userId));
```

---

## Configuration

Global options via `RevalQueryOptions`.

```csharp
services.AddRevalQuery(options =>
{
    // Default fetch options (RefetchInterval, StaleTime)
    options.FetchOptions = new CoreFetchOptions(
        RefetchInterval: TimeSpan.FromMinutes(5),
        StaleTime: TimeSpan.FromMinutes(1)
    );

    // Default retry options
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