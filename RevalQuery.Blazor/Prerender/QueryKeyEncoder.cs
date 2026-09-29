using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace RevalQuery.Blazor.Prerender;

/// <summary>
/// Turns a query key into the string the transfer addresses it by.
/// </summary>
/// <remarks>
/// <para>The encoding is lossless rather than hashed, for the reason ADR 0003 gives for
/// comparing keys by value: a collision here would hand one query another query's data, and no
/// hash width makes that impossible.</para>
/// <para>Each segment contributes its type name and its invariant string form, so ("a", 1) and
/// ("a", "1") encode differently. A segment whose type does not override ToString has no
/// distinct string form, and a key containing one cannot be encoded.</para>
/// </remarks>
internal static class QueryKeyEncoder
{
    private const char SegmentSeparator = '/';
    private const char TypeSeparator = ':';
    private const char Escape = '~';
    private const string NullSegment = "null";

    /// <summary>
    /// Encodes a key, or returns null when a segment has no distinct string form.
    /// </summary>
    public static string? TryEncode(ITuple key)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0) builder.Append(SegmentSeparator);

            var segment = key[i];

            if (segment is null)
            {
                builder.Append(NullSegment);
                continue;
            }

            var type = segment.GetType();
            var text = segment is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : segment.ToString();

            // The default object.ToString returns the type name, which is the same for every
            // instance. Encoding that would give two different keys the same address.
            if (text is null || text == type.ToString()) return null;

            builder.Append(EscapeText(type.FullName ?? type.Name))
                .Append(TypeSeparator)
                .Append(EscapeText(text));
        }

        return builder.ToString();
    }

    private static string EscapeText(string value)
    {
        if (value.IndexOfAny([Escape, SegmentSeparator, TypeSeparator]) < 0) return value;

        var builder = new StringBuilder(value.Length + 8);

        foreach (var character in value)
        {
            switch (character)
            {
                case Escape: builder.Append(Escape).Append(Escape); break;
                case SegmentSeparator: builder.Append(Escape).Append('s'); break;
                case TypeSeparator: builder.Append(Escape).Append('t'); break;
                default: builder.Append(character); break;
            }
        }

        return builder.ToString();
    }
}
