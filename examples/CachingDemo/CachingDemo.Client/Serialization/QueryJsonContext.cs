using System.Text.Json.Serialization;

namespace CachingDemo.Client.Serialization;

/// <summary>
/// Metadata for every query result type this app transfers from a prerender to the client.
/// </summary>
/// <remarks>
/// A source-generated context rather than reflection, because the client half of the transfer
/// runs in a trimmed WebAssembly publish where reflection-based serialisation finds nothing and
/// fails silently. AddRevalQueryPrerenderTransfer refuses options without a resolver for that
/// reason.
/// </remarks>
[JsonSerializable(typeof(List<string>))]
public partial class QueryJsonContext : JsonSerializerContext;
