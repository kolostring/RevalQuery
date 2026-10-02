---
status: accepted
---

# The imperative API is one method, and staleness travels with the request

`QueryClient` offered `FetchQueryAsync` and `PrefetchQuery`, and neither consulted
`StaleTime`. They collapse into one `QueryAsync` that does. Cache-first is an option on the
query, `NeverStale()`, not a second method and not a magic `TimeSpan`.

The reason is that staleness was never a property of the data here. It was a property of
having an observer. `StaleTime` is read in exactly one method, `QueryWorker.RunIfStale`,
reachable from exactly two callers: `QueryClient.Subscribe` and a worker being re-enabled.
Both are the subscription path. `FetchQueryAsync` and `PrefetchQuery` went straight to
`RunAndReleaseAsync` and never asked, so a caller without an observer could not ask whether
data was stale and got no answer from the library.

`CONTEXT.md` had already said otherwise: stale is "data old enough to be refetched", with no
mention of observers. `LastUpdatedAt` is likewise intrinsic, saved and restored verbatim so
restored data is correctly stale (ADR 0001). The timestamp lived on the data and its
interpretation lived on the subscription. One concept, two homes.

The cost landed on consumers. `examples/MudBlazorDemo/Pages/Autocomplete.razor` had to
reimplement staleness in userland, with a `Freshness` constant and a hand-written
`DateTimeOffset.UtcNow - cached.LastUpdatedAt` comparison copied out of `RunIfStaleNow`, plus
a warning box explaining why. The same problem solved through the subscription path,
`SearchBarRevalQuery.razor`, is one line.

## Considered options

Keeping the two methods and teaching both to respect `StaleTime` was the obvious smaller
change. TanStack rejected the equivalent shape and their argument applies here unchanged. On
naming:

> `queryClient.fetchQuery`, despite the name, might not invoke the `queryFn`. If data in the
> cache is **fresh**, it will just give you that.

> `queryClient.prefetchQuery` has a similar naming problem: the `pre` in `prefetchQuery`
> indicates that something is done once, _before_ it's needed / available. But did you know
> that calling `prefetchQuery` will also fetch _every time_ when data is stale?

And on the surface being unteachable:

> For route loaders, we recommend `ensureQueryData`. In the SSR docs, we recommend
> `prefetchQuery`. The functions are so close in functionality that it mostly doesn't matter,
> so why is it two functions? We get so many questions around 'what should I use where?',
> which is a good indicator that the APIs are not intuitive.

They shipped the collapse in `@tanstack/query-core` 5.102.0: one `queryClient.query(options)`,
with `prefetchQuery` expressed as `void query(o).catch(noop)` and `ensureQueryData` as
`query({...o, staleTime: 'static'})`. The three were never different semantics. They were
different staleness and error policies, pre-baked into names, and the caller is better placed
to choose them. RevalQuery had the identical shape: `PrefetchCoreAsync` is `FetchQueryAsync`
plus a swallow, and its own comment admits as much — "Nobody is waiting for this".

Expressing cache-first as a sentinel `TimeSpan` was rejected. `TimeSpan.MaxValue` would not
reproduce it: `RunIfStaleNow` tests `IsInvalidated` before the clock, so an invalidated query
refetches at any `StaleTime`. That is TanStack's `Infinity`, which is the exact case their
`'static'` exists to beat —

> The difference to `staleTime: Infinity` is that `Infinity` is still just a number, which
> means queries that are invalidated with `queryClient.invalidateQueries` would get refetched,
> even if they have an infinite `staleTime`

— and a `TimeSpan` whose one magic value skips a check no other value skips is a flag wearing
a duration's type. `NeverStale()` is a separate option because never-stale is not a duration.

A per-call argument, `QueryAsync(options, cacheFirst: true)`, was rejected for splitting
staleness across options and argument immediately after this record put it in one place.

Keeping a sanctioned swallow helper for the discarded case was rejected. C# has no `noop`
idiom and a bare `_ = client.QueryAsync(...)` leaves a faulted task unobserved, so the
temptation is real. It is the family growing back under a new name. Callers write the discard
and the `try`.

## Static beats invalidation

`NeverStale()` makes a query static, and a static query is not refetched even when
invalidated. This is the one decision here most likely to be revisited, and it is the reason
this record exists as much as the collapse is.

The staleness decision takes TanStack's ordering, because the ordering is the mechanism:

```
never fetched -> stale
static        -> fresh      (tested before invalidation, deliberately)
invalidated   -> stale
otherwise     -> compare LastUpdatedAt against StaleTime
```

The first rung is where this parts from TanStack, who test `state.data === undefined` rather
than the timestamp. Reading it as "never fetched" rather than "holds no data" is deliberate.
`IQueryState<TRes>.Data` is publicly settable and the optimistic-update pattern writes it
without touching the clock, so the two rungs disagree for exactly one query: a static one
holding an optimistic value and no fetch. It is stale here and fresh there. Refetching an
unconfirmed value is the better answer, and the mutation invalidates on settle regardless.

Testing the status instead — `Status != Resolved` — was rejected. `ApplyFailed` leaves the
timestamp alone, so a query that succeeded and then failed still knows when its data arrived
and should fall through to the clock. The status test would have forced it stale.

This qualifies a claim ADR 0005 makes without exceptions. That record treats a dropped
invalidation as a defect: "the restore lands... nothing is stale any more, and the query never
refetches. The user is left on stale data with no way to tell." That is still true of the bug
0005 describes. What is new is a second, deliberate way for an invalidation not to produce a
refetch, chosen by the consumer rather than suffered. ADR 0005 is left as written; this is the
record that qualifies it.

## Invalidation stops moving the clock

`NotifyInvalidated` set `_lastUpdatedAt` to `DateTimeOffset.MinValue` alongside
`_isInvalidated`. The clock move is dropped. The flag already carries the decision —
`IsInvalidated` exists precisely because invalidation is "not derivable from `LastUpdatedAt`"
— and the same move made on its own was already `[Obsolete]` on `SetStale` for the same
reason. `SetStale` and `SetFresh` are deleted outright rather than left deprecated. Both wrote
the clock on its own, and once the ordering above reads `MinValue` as "never fetched" a public
method that writes it over a query holding data can break the invariant the record asserts.
Neither had a caller in the library, its tests or its examples.

TanStack never does it. Their reducer changes one field:

```ts
case 'invalidate':
  return { ...state, isInvalidated: true }
```

and `isStaleByTime` reads the flag as a separate short-circuit rather than through the
timestamp. That separation is what makes the four-way ordering above expressible at all: one
number cannot mean "never fetched", "invalidated" and "fetched long ago" while a fourth state
overrides the second.

The clock move also corrupted a public, persisted value. `LastUpdatedAt` is on `IQueryState`,
is read by consumers, and is written to persistence. An invalidated query reported data from
year one while holding data from a minute ago.

## The transfer carries state, not a hand-picked tuple

Dropping the clock move removes the only way invalidation crossed the prerender boundary.
`TransferEntry` carried `(Type, Json, LastUpdatedAt)` and `QuerySnapshot` carried
`(Key, DataType, Data, LastUpdatedAt)`; neither had an invalidation flag, so `MinValue` was
doing that work implicitly.

Both now carry the query's state record. This is TanStack's shape: `dehydrateQuery` spreads
`...query.state` wholesale and replaces only `data`, so `isInvalidated` crosses for free
because nothing was ever curated.

Dropping invalidated queries from the transfer instead was considered and is wrong. TanStack
does not: their default predicate filters on status alone,

```ts
export function defaultShouldDehydrateQuery(query: Query) {
  return query.state.status === 'success'
}
```

and invalidation is not a status, so an invalidated query is still successful and still
crosses. The filter and the payload shape are separate concerns and stay separate here too:
ADR 0004's refusal to carry pending or failed queries is unchanged.

## Consequences

`QueryAsync` throws on cancellation even when the query holds data. TanStack resolves with the
old data in that case and rejects only on a first fetch, and their test says why: "we have to
reject here because we can't resolve with `undefined`". Their rejection is forced by the type,
not chosen. `TRes?` is representable here and there is no such pressure, so one rule stands:
the caller asked for data this call fetched, and there is none.

A static query that is invalidated reports `IsInvalidated` true indefinitely, until some fetch
succeeds, while never refetching. That follows from where the check sits: the mark stays dumb
and records only that an invalidation happened, so the flag is honest about history and the
refetch decision is where the query is allowed to ignore it. Putting the check at the mark
instead would collapse invalidating and refetching into one act and leave the query claiming
it was never invalidated at all. This is TanStack's behaviour, for the same reason.

`QueryAsync` judges staleness against what the registry holds at the moment of the call, and
does not wait for a restore that is still outstanding. Waiting was tried and is wrong twice
over. ADR 0006 has restores never hold a fetch up, and an adapter that is slow or wedged would
hang every imperative caller behind it — `PersistenceTests` deadlocks on exactly that, because
its gate opens only after the fetch it is gating has returned. The race needs no handling here
anyway: `TryRestore` already refuses to overwrite a fetch that landed first. The cost is that
stored data misses the freshness test on the first call for a key, and that call fetches. That
is what both collapsed methods did unconditionally, so nothing regresses.

The discarding caller loses one thing the old `PrefetchQuery` had. That method was `void` and
ran its registration synchronously, so a key already held under another result type threw from
the call. `QueryAsync` is `async`, so the same mistake faults the task instead, and a caller
who discards it sees it only through the `catch` they write around the discard. One method now
means one error channel, which is the trade the collapse makes everywhere else too.

`PrefetchQuery` is removed and `FetchQueryAsync` is renamed. Both are breaking changes for
0.4.0, which already carries one in `QueryClient.Cancel` (ADR 0007).

ADR 0007 argued cancellation entirely in terms of `FetchQueryAsync`. Its reasoning is
untouched; only its vocabulary broke, and it has been amended in place to say `QueryAsync`
with no claim altered. ADR 0005 is amended in the same narrow way: it explained a dropped
invalidation through the `MinValue` write, which no longer exists. Its argument and its
conclusion stand; only the mechanism it named is gone. That is separate from the qualification
this record makes to 0005's claim about invalidations, which is recorded here and not there.

`CONTEXT.md` loses **Prefetch**. The activity survives as a way of calling `QueryAsync` and
discarding the task, but it is no longer a thing in the language, and the old definition was
already wrong in the direction this record exposes: "populating a query's data" implies it
stops when data is present, which it never did. **Static** enters the freshness cluster,
taking TanStack's word so the two libraries stay mappable. **Invalidate** gains the exception
it now has.

`GcTime` must exceed `StaleTime` or a cached entry can never serve a hit. The defaults do not
collide, `StaleTime` being zero against a `GcTime` of five minutes, but they do not protect
anyone either: an unobserved query written by `QueryAsync` becomes eligible for eviction the
instant its fetch settles, so a `StaleTime` raised towards `GcTime` leaves the entry racing
its own freshness window. This is not new and it matches TanStack, whose `scheduleGc` fires
on zero observers the same way, but it caught this repo's own example and so it is now
written down.

A caller's options are adopted by the query, and the last one in wins. That was already true
of a re-render, through `ApplyOptions`, but not of a first subscription: a component whose
key was created by a `QueryAsync` call ran on the loader's options until its second render,
which with `NeverStale()` meant permanently. `Subscribe` now adopts them the way a re-render
does. TanStack settles the same question the same way -- `QueryObserver.setOptions` calls
`query.setOptions` on mount, and `fetchQuery` writes its own options through `query.fetch` --
with the component still winning, because it writes on every render and the loader writes
once.
