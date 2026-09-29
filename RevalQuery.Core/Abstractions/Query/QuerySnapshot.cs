using System.Runtime.CompilerServices;

namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// One query's data, captured out of the registry at a moment in time.
/// </summary>
/// <remarks>
/// Only queries that hold data produce a snapshot, so there is no status to carry: a snapshot
/// is always a resolved query. <see cref="LastUpdatedAt"/> travels with the data, because data
/// that was already stale when it was captured must still be stale wherever it lands.
/// </remarks>
/// <param name="Key">The query key.</param>
/// <param name="DataType">The query's result type, which <see cref="Data"/> is an instance of.</param>
/// <param name="Data">The data.</param>
/// <param name="LastUpdatedAt">When the data was fetched.</param>
public sealed record QuerySnapshot(ITuple Key, Type DataType, object Data, DateTimeOffset LastUpdatedAt);
