namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// What a query knows about the age of its data: when the data arrived, and whether it has
/// been invalidated since.
/// </summary>
/// <remarks>
/// <para>Carried as one value wherever data crosses a boundary — a snapshot, the prerender
/// transfer, a persistence adapter — so that the two halves of a staleness decision cannot
/// become separated on the way. They were, once: invalidation used to move
/// <see cref="LastUpdatedAt"/> to MinValue instead of being carried, which let a timestamp
/// stand in for a flag and made a restore silently erase it. See docs/adr/0008.</para>
/// <para>Both fields are intrinsic to the data rather than to anything observing it, which is
/// what lets a restored or transferred query be correctly stale on arrival.</para>
/// </remarks>
/// <param name="LastUpdatedAt">
/// When the data was fetched. Written down and read back verbatim, never refreshed on arrival,
/// so data that was already stale when it was captured is still stale where it lands.
/// </param>
/// <param name="IsInvalidated">
/// Whether the query had been invalidated with no successful fetch since. Not derivable from
/// <see cref="LastUpdatedAt"/>, which is the whole reason it travels separately.
/// </param>
public readonly record struct QueryFreshness(DateTimeOffset LastUpdatedAt, bool IsInvalidated = false);
