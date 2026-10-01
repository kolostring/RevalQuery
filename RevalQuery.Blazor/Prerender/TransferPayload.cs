using System.Text.Json.Serialization;
using RevalQuery.Core.Abstractions.Query;

namespace RevalQuery.Blazor.Prerender;

/// <summary>
/// One transferred query: its data as JSON, the type that JSON was written for, and what the
/// prerender knew about that data's age.
/// </summary>
/// <param name="Type">
/// The query's result type, as Type.ToString writes it. Checked on the way back in, so a key
/// reused for a different result type produces no data rather than a mis-shaped object.
/// ToString rather than FullName because FullName stamps a generic type's arguments with their
/// assembly version, which would stop matching the moment the two hosts ran different patch
/// releases and quietly turn the transfer off.
/// </param>
/// <param name="Json">The query's data, already serialised by the consumer's resolver.</param>
/// <param name="Freshness">
/// When the data was fetched, and whether the prerender invalidated it. Carried whole rather
/// than picked apart: an invalidation used to cross this boundary only by accident, encoded in
/// a timestamp moved to MinValue, and stopped crossing at all once that move was dropped. See
/// docs/adr/0008.
/// </param>
internal sealed record TransferEntry(string Type, string Json, QueryFreshness Freshness);

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
