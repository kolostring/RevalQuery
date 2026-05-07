namespace RevalQuery.Core.Abstractions.Query;

/// <summary>
/// Interface for a query observer subscription.
/// Represents a single component's subscription to a query.
/// </summary>
public interface IQueryObserver
{
    /// <summary>
    /// Gets or sets whether this observer's subscription is enabled.
    /// When disabled, the query will fetch if all observers are disabled.
    /// Query toggling allows pausing queries without losing cached data.
    /// </summary>
    bool Enabled { get; set; }
}