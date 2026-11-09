using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RipgrepSearch.Desktop;

internal sealed class ResultColorizer(ResultsDocument results) : DocumentColorizingTransformer
{
    private static readonly IReadOnlyDictionary<ResultColor, IBrush> BrushesByColor =
        Enum.GetValues<ResultColor>().ToDictionary(color => color,
            color => (IBrush)new SolidColorBrush(Color.FromUInt32(0xFF000000u | (uint)color)).ToImmutable());

    protected override void ColorizeLine(DocumentLine line)
    {
        // Binary search into the offset index, then visit only visible runs.
        foreach (StyledRange range in results.Intersecting(line.Offset, line.EndOffset))
        {
            int start = Math.Max(range.Start, line.Offset);
            int end = Math.Min(range.End, line.EndOffset);
            if (start == end) continue;
            ChangeLinePart(start, end, element =>
            {
                var properties = element.TextRunProperties;
                properties.SetForegroundBrush(BrushesByColor[range.Color]);
                if (range.Style.HasFlag(ResultTextStyle.Bold))
                    properties.SetTypeface(new Typeface(properties.Typeface.FontFamily,
                        properties.Typeface.Style, FontWeight.Bold, properties.Typeface.Stretch));
                if (range.Style.HasFlag(ResultTextStyle.Underline))
                    properties.SetTextDecorations(TextDecorations.Underline);
            });
        }
    }
}
