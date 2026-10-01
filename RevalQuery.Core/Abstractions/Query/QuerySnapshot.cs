using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// One query's data, captured out of the registry at a moment in time.
/// </summary>
/// <remarks>
/// Only queries that hold data produce a snapshot, so there is no status to carry: a snapshot
/// is always a resolved query. <see cref="Freshness"/> travels with the data rather than being
/// recomputed on arrival, because data that was already stale when it was captured must still
/// be stale wherever it lands, and a query that was invalidated must still be invalidated.
/// </remarks>
/// <param name="Key">The query key.</param>
/// <param name="DataType">The query's result type, which <see cref="Data"/> is an instance of.</param>
/// <param name="Data">The data.</param>
/// <param name="Freshness">When the data was fetched, and whether it was invalidated since.</param>
public sealed record QuerySnapshot(ITuple Key, Type DataType, object Data, QueryFreshness Freshness);
