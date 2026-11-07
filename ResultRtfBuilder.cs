using System.Drawing;
using System.Globalization;
using System.Text;

namespace RipgrepSearch;

internal static class ResultRtfBuilder
{
    public static string Build(IReadOnlyList<ResultSpan> spans)
    {
        Color[] colors = spans.Select(span => span.Color).Distinct().ToArray();
        var rtf = new StringBuilder("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Consolas;}}{\\colortbl ;");
        foreach (Color color in colors)
            rtf.Append($"\\red{color.R}\\green{color.G}\\blue{color.B};");
        rtf.Append("}\\f0\\fs20 ");
        Color? previousColor = null;
        FontStyle? previousStyle = null;
        foreach (ResultSpan span in spans)
        {
            if (previousColor != span.Color || previousStyle != span.Style)
            {
                rtf.Append("\\cf").Append(Array.IndexOf(colors, span.Color) + 1)
                    .Append(span.Style.HasFlag(FontStyle.Bold) ? "\\b" : "\\b0")
                    .Append(span.Style.HasFlag(FontStyle.Underline) ? "\\ul " : "\\ulnone ");
                previousColor = span.Color;
                previousStyle = span.Style;
            }
            AppendText(rtf, span.Text);
        }
        return rtf.Append('}').ToString();
    }

    private static void AppendText(StringBuilder rtf, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            switch (character)
            {
                case '\\': case '{': case '}': rtf.Append('\\').Append(character); break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    rtf.Append("\\par\n");
                    break;
                case '\n': rtf.Append("\\par\n"); break;
                case '\t': rtf.Append("\\tab "); break;
                // Defensive handling for callers that supply spans directly.
                case '\0': rtf.Append("\\u9216?"); break;
                default:
                    if (character >= 32 && character < 127)
                        rtf.Append(character);
                    else
                        rtf.Append("\\u").Append(unchecked((short)character).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
    }
}
