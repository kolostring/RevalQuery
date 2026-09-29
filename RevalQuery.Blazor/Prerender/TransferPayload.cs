using System.Text.Json.Serialization;

namespace RevalQuery.Blazor.Prerender;

/// <summary>
/// One transferred query: its data as JSON, the type that JSON was written for, and the moment
/// the data was fetched.
/// </summary>
/// <param name="Type">
/// The full name of the query's result type. Checked on the way back in, so a key reused for a
/// different result type produces no data rather than a mis-shaped object.
/// </param>
/// <param name="Json">The query's data, already serialised by the consumer's resolver.</param>
/// <param name="LastUpdatedAt">When the data was fetched.</param>
internal sealed record TransferEntry(string Type, string Json, DateTimeOffset LastUpdatedAt);

/// <summary>
/// Everything one prerender hands to the client, keyed by encoded query key.
/// </summary>
internal sealed record TransferPayload(Dictionary<string, TransferEntry> Queries);

/// <summary>
/// Source-generated serialisation for the envelope.
/// </summary>
/// <remarks>
/// The envelope is the library's own type, so the library can and must supply its metadata:
/// the client half of the transfer runs in trimmed WebAssembly, where reflection-based
/// serialisation fails. The consumer's own resolver covers the payload inside
/// <see cref="TransferEntry.Json"/>, which is opaque here.
/// </remarks>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(TransferPayload))]
internal sealed partial class TransferSerializerContext : JsonSerializerContext;
