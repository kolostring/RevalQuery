---
status: accepted
---

# The prerender transfer serialises its own payload and hands the framework a string

A prerendering server fetches every query on the page, its scope then dies, and the
WebAssembly client boots with an empty registry and fetches the same keys again. The
transfer moves that work across, the way TanStack's `dehydrate` and `hydrate` do.

`PersistentComponentState` is the framework's channel for state crossing that boundary, and
it is the right one. The problem is its API. It offers `PersistAsJson<TValue>` and
`TryTakeFromJson<TValue>` and **no `JsonTypeInfo` or `JsonSerializerContext` overload**, so
there is no way to hand it a source-generated context. It falls back to reflection.

Blazor WebAssembly trims on publish. Reflection-based serialisation does not fail loudly
there; it finds no metadata and produces nothing. So the naive implementation works all the
way through development and every test, and goes quiet after the first `dotnet publish`.
That is the trap this record exists to prevent. Framework issue: dotnet/aspnetcore#69325.

## Decision

Query data is serialised here, using the consumer's `JsonSerializerOptions.TypeInfoResolver`,
and what reaches the framework is a **`string`**, which it handles without reflected
metadata. The envelope holding those strings is the library's own type, so the library ships
its own source-generated context for it.

`AddRevalQueryPrerenderTransfer` requires `JsonSerializerOptions` and throws when they have
no `TypeInfoResolver`. Failing at registration is the whole point: the failure it replaces is
invisible until after a publish.

Do not "simplify" this to `PersistAsJson<TRes>`.

## Consequences

The transfer reaches the client through `IQueryPersistence`. That interface already restores
data at the one moment a query enters the registry, and already keeps the timestamp the data
was originally fetched at, so data that was stale when captured is still stale on arrival and
refetches. Reusing it meant no new interface and no second restore path to keep correct.

So that registering the transfer does not cost a consumer their own store, several
`IQueryPersistence` registrations are now composed rather than the last one winning: a load
takes the first store holding the key, and a save goes to all of them.

Keys are encoded losslessly, with each segment contributing its type name and its invariant
string form. This follows ADR 0003 for the same reason: a collision would hand one query
another query's data, and no hash width makes that impossible. A segment whose type does not
override `ToString` has no distinct string form, and a key containing one is not transferred.

Result types are identified by `Type.ToString` rather than `Type.FullName`, because
`FullName` stamps a generic type's arguments with their assembly version. `List<string>`
would cross as `List\`1[[String, ..., Version=10.0.0.0, ...]]` and stop matching the moment
the two hosts ran different patch releases, silently turning the transfer off.

Everything the transfer cannot carry is skipped rather than thrown for: a pending or failed
query, a key the encoder cannot address, a type the consumer's resolver does not know. The
client fetches those for itself. Throwing inside the persisting callback would fail the whole
render, which is a bad trade for an optimisation. Skipping a failed query is also the correct
semantic, and the one `dehydrate` uses: a failure carried across would look like a result.

## Considered options

An `IQuerySerializer` interface was rejected as over-engineering. `JsonSerializerOptions`
already is the abstraction, every consumer already has one, and adding an interface later is
non-breaking while removing one is not.

Persisting one framework entry per query, rather than one envelope, was rejected because
every one of them would have to be a `string` anyway, and the per-key addressing would move
into framework state keys where nothing validates them.
