using System.Text.Json.Serialization;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Blazor.Prerender;

internal sealed record TransferEntry(string Type, string Json, QueryFreshness Freshness);

internal sealed record TransferPayload(Dictionary<string, TransferEntry> Queries);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(TransferPayload))]
internal sealed partial class TransferSerializerContext : JsonSerializerContext;
