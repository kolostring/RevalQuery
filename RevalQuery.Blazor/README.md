# RevalQuery

**A high-performance, Result-oriented data fetching library for Blazor.**

Heavily inspired by **TanStack Query**, BlazorQ brings a professional-grade state management and caching layer to .NET
8+. It is designed for developers who value type safety, explicit error handling via the **Result Pattern**, and
architecture that works across both **Blazor WebAssembly** and **Blazor Server**.

---

## 📦 Installation

Register the core services in your `Program.cs`.

```csharp
//Program.cs

builder.Services.AddRevalQuery();
```

---

## 🏗 Component Integration

Before using the following functions, your components must inherit from the base class:

```razor
@inherits BlazorQ.QueryComponentBase
```

---

## 🔍 Using Queries (`UseQuery`)

The `UseQuery` method allows you to observe a piece of data.

### Stateless Handlers

Handlers **must be stateless**. Using the `static` keyword on your lambda is the best way to enforce this; it prevents
the closure from capturing component-level variables and ensures the compiler helps you stay safe.

```csharp
private IQueryState<BasketItem?> quantityQuery => UseQuery(
    key: ("basket-count", itemId),

    handler: static async ctx =>
        {
            // Access services via the context ServiceProvider
            var basketService = ctx.ServiceProvider.GetRequiredService<IBasketService>();
            var item = await basketService.GetItem(ctx.Key.itemId);
            return item;
        },
    
    configure: options => options
        .OnSuccess(
            async (basketItem) =>
            {
                Console.WriteLine($"Fetched quantity: {basketItem?.Quantity}");
            }
        )
        .Enabled(OperatingSystem.IsBrowser()) // Only fetch on the client side
);
```

> **💡 Pro Tip:** For better reusability and cleaner components, define your `QueryOptions` in a static factory class by using the `QueryOptionsFactory.Create` method(
> e.g., `BasketQueries.GetItemOptions(itemId)`).

### Stateless Validation (Highly Recommended)

To enforce architectural purity, use the stateless validation plugin during development. This ensures your handlers
don't accidentally capture external state, which can lead to memory leaks or stale closures.

```csharp
//Program.cs

if (builder.Environment.IsDevelopment())
{
    // Throws errors at runtime if handlers capture state
    builder.Services.AddSingleton<IQueryPlugin, QueryPluginHandlersStatelessValidation>();
}
```
---

## ⚡ Mutations

Unlike Queries, Mutations are used for server-side actions (Create/Update/Delete). These **can be stateful** and are the
recommended place to perform cache invalidation using the `QueryClient`.

```csharp
private IMutationState<string, Task> addItemMutation => UseMutation(
    MutationOptionsFactory.Create<string, Task>
    (
        handler: async (ctx) =>
        {
            var basketService = ctx.ServiceProvider.GetRequiredService<IBasketService>();
            var queryClient = ctx.ServiceProvider.GetRequiredService<QueryClient>();
    
                await basketService.AddItem(new(ctx.Params, 1));
                
                // Invalidate specific cache keys to trigger re-fetches
                queryClient.Invalidate(("basket-count", ctx.Params));
                
                return Task.CompletedTask;
        }
    ) 
);
```

---

## ⚠️ Dev Release Note

This is a development release. The API is subject to change.

---

## License

MIT