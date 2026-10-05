using System.Text.Json.Serialization;

namespace CachingDemo.Client.Serialization;

[JsonSerializable(typeof(List<string>))]
public partial class QueryJsonContext : JsonSerializerContext;
