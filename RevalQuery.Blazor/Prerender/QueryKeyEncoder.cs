using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace RevalQuery.Blazor.Prerender;

internal static class QueryKeyEncoder
{
    private const char SegmentSeparator = '/';
    private const char TypeSeparator = ':';
    private const char Escape = '~';
    private const string NullSegment = "null";

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
