# Examples

Two runnable apps. Both reference the projects in this repository rather than a published
package, so building them is a smoke test of the working tree.

## CachingDemo

A Blazor Web App with interactive WebAssembly components, prerendered on the server. Start it
with:

```
dotnet run --project examples/CachingDemo/CachingDemo
```

Its home page lists everything below.

### What 0.4.0 changed

| Page | What it shows |
| --- | --- |
| `/retry-policy` | `Retry(n)` means n retries after the first attempt, so the handler runs up to n + 1 times. `Retry(0)` still calls it once. |
| `/loading-and-restoring` | A restore is not a fetch. `IsRestoring` is new, `IsFetching` stays false during one, and `IsLoading` covers both. |
| `/dependent-queries` | `Enabled` is read on every render, so a query can start disabled and enable itself once the value its key needs arrives. |
| `/mutations` | A mutation invalidates the key it wrote to, and the query reading that key refetches. Mutations retry zero times by default, queries three. |

`/loading-and-restoring` needs a persistence adapter with a visible delay, which
`SlowDemoPersistence` supplies. It answers only for keys beginning with `persisted`, so the
other pages are unaffected by a store that exists to make one page interesting.

### The same search box, three ways

`/search-bar`, `/search-bar-debounced` and `/search-bar-reval-query` solve one problem by hand,
by hand with a debounce, and with RevalQuery. Type in all three and watch the console for the
requests each one makes.

## MudBlazorDemo

A standalone WebAssembly app using MudBlazor for its UI. Start it with:

```
dotnet run --project examples/MudBlazorDemo
```

| Page | What it shows |
| --- | --- |
| `/products` | A query's `IsLoading` drives `MudTable.Loading`, and selecting a row enables a detail query that was disabled until then. |
| `/autocomplete` | `MudAutocomplete` wants a search function rather than a render loop, so this one calls `QueryAsync` and nothing else. It consults the query's stale time, so a term typed again is answered from the registry without reaching the service, and the token it takes abandons the wait rather than the fetch. |
| `/reviews` | A `MudForm` posts through `Q.Mutation`, which invalidates the reviews key on success. |

Integrating with a component library needs nothing special: `AddRevalQuery()` in `Program.cs`,
a `<QueryRenderer Component="this" Tracker="Q" />` on the pages that query, and the query
state's flags bound to whichever properties the components expose.
