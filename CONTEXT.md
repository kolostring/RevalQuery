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

**Retry**:
One further attempt after a failed one. A retry count never includes the first attempt,
so zero retries still calls the handler once.
_Avoid_: Attempt, max attempts

**Prefetch**:
Populating a query's data without any component subscribing to it.
_Avoid_: Warm, preload, eager fetch

**Cancel**:
Stopping a query's in-flight fetch and discarding whatever it would have produced. The
query keeps the data it already had and records no error, because a cancelled fetch
never learned anything. Distinct from abandoning a wait, where the caller stops
listening and the fetch runs on.
_Avoid_: Abort, stop, kill, interrupt

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
data, so a query can be fetching and resolved at the same time. Narrower than loading:
a restore is not a fetch.
_Avoid_: Busy, in-flight

**Loading**:
A query has no data yet and work is in flight to get some, whether that work is a fetch
or a restore. What a component checks to decide between a spinner and an empty state.
_Avoid_: Fetching, pending, busy

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

**Restore**:
Answering a query's data question from persistence rather than from its real source. It
begins when the query is created and ends once the query knows what follows it: either
the stored data stands, or a fetch has started because there was none or it was stale.
Reading the store is the first part of a restore, not the whole of it. A restore never
makes data look newer than it was.
_Avoid_: Load, hydrate, rehydrate, warm
