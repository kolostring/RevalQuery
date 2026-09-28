# RevalQuery

A data fetching and caching library for .NET UI components, inspired by TanStack Query.
Components declare what data they need; the library fetches it, caches it, keeps it
current, and tells them when it changed.

## Language

### Reads

**Query**:
A keyed read operation whose result is cached and observed. The unit everything else
in this library is organised around.
_Avoid_: Request, fetch (as a noun), operation

**Query key**:
The tuple that identifies a query. Keys are hierarchical, so `("users", 1)` sits
beneath `("users")`.
_Avoid_: Cache key, id, tag

**Handler**:
The static function that produces a query's data. Static so it cannot capture
component state.
_Avoid_: Fetcher, callback, resolver, loader

**Prefetch**:
Populating a query's data without any component subscribing to it.
_Avoid_: Warm, preload, eager fetch

### Writes

**Mutation**:
A write operation. Unlike a query it has no key, is never cached, and is triggered
explicitly rather than by a component needing data.
_Avoid_: Command, action, write query

### State

**Query state**:
The observable record of one query: its data, its status, and its subscribers. This is
what a component reads from.
_Avoid_: Cache entry, result, model

**Observer**:
One component's subscription to a query state. A query with no observers is a
candidate for eviction.
_Avoid_: Subscriber, listener, watcher

**Query status**:
Whether a query has data. Either pending, resolved, or failed. Independent of whether
a fetch is currently running.
_Avoid_: State, phase

**Fetch status**:
Whether a fetch is currently running. Independent of whether the query already has
data, so a query can be fetching and resolved at the same time.
_Avoid_: Loading, busy

**Enabled**:
Whether an observer permits its query to fetch. A disabled observer keeps the cached
data and stops triggering work.
_Avoid_: Active, paused, on

### Freshness and removal

Three distinct ideas that are easy to conflate. A query can be stale without being
invalidated, and invalidated without being evicted.

**Fresh**:
Data young enough to be reused without refetching.
_Avoid_: Valid, current, hot

**Stale**:
Data old enough to be refetched, but still shown to components while the refetch runs.
_Avoid_: Expired, invalid, dirty

**Invalidate**:
Marking a query and everything beneath its key as stale, and telling subscribed
components to refetch now. The data survives.
_Avoid_: Refresh, clear, reset, purge

**Evict**:
Removing a query from the registry entirely, discarding its data. Only ever happens to
queries with no observers.
_Avoid_: Invalidate, expire, delete, collect

### Storage

Three layers that have all been called "the cache" at some point. They are different
things with different lifetimes.

**Registry**:
The in-memory structure holding every live query state, organised hierarchically by
query key. Scoped to one user's session, never shared between users.
_Avoid_: Cache, cache storage, store, query map

**Eviction policy**:
The rule deciding when an unobserved query leaves the registry.
_Avoid_: Garbage collector, expiry, reaper

**Persistence**:
An optional durable copy of query data outside the registry, surviving process
restarts. Supplied by the consuming application, not by this library.
_Avoid_: Cache, backing store, L2, distributed cache
