---
status: accepted
---

# Persistence is an interface we define, not HybridCache underneath the fetch path

.NET ships `HybridCache` as its general caching abstraction, so the obvious move is to
store query data in it and let `QueryWorker` call `GetOrCreateAsync`. We rejected that
and instead defined our own `IQueryPersistence`, which `HybridCache` and FusionCache can
both implement, sitting beside the registry rather than underneath the fetch path.

The reason is that `HybridCache` is a cache and this library needs an observable store.
A cache answers "give me a value". We need to tell three mounted components that
`IsFetching` just flipped, and `HybridCache` has no change notification of any kind. It
therefore cannot own query state; it can only hold a second copy of the data that
nothing is able to keep in sync.

## Considered options

Making `HybridCache` the store under `QueryWorker` was the option we rejected. Beyond the
missing notification, four things conflicted concretely:

- Its entries expire under their own `Expiration` and `LocalCacheExpiration`, separate
  from our eviction policy, with no event when they do. A query would keep reporting
  resolved with data the cache had already dropped, while a newly mounting component
  missed and refetched. Two components, same key, different values, undetectable.
- `GetOrCreateAsync` cannot express stale-while-revalidate. A hit returns cached data, a
  miss blocks on the factory. Showing stale data while a refetch runs has no expression
  in that API.
- Retry has no correct placement. Inside the factory, the per-key lock is held through
  the whole exponential backoff. Outside it, the stampede protection that motivated
  using it is gone.
- Cancellation changes meaning. The factory token cancels only when every caller
  awaiting the shared operation has cancelled, so a single component cancelling its own
  query no longer cancels the fetch.

Dropping the idea entirely was the other option. We kept an interface because the
underlying wish was reasonable, and this shape satisfies it without the conflicts.

## Consequences

Stampede protection has to be built rather than inherited. A dictionary of in-flight
tasks keyed by query key covers it.

Persistence adapters serialise; the library never does. That keeps `System.Text.Json`
and its trimming constraints out of the core and out of WebAssembly payloads.

Query data is loaded lazily when a query is created, where the result type is known,
rather than rehydrated in bulk at startup, which would require the library to track
types it has no reason to know.

Freshness is saved and restored verbatim, so restored data is correctly stale and
refetches immediately instead of appearing fresh. `PersistedQuery<TRes>` carries a
`QueryFreshness` for this, holding the last-updated timestamp alongside the invalidation
flag; it was a bare `DateTimeOffset` when this record was written, and only the name of
the field has moved since.
