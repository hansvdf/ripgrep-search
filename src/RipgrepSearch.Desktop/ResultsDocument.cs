using System.Text;
using AvaloniaEdit.Document;

namespace RipgrepSearch.Desktop;

internal readonly record struct StyledRange(int Start, int Length, ResultColor Color, ResultTextStyle Style,
    string? FilePath = null)
{
    public int End => Start + Length;
}

internal sealed record ResultBatch(string Text, IReadOnlyList<StyledRange> Ranges)
{
    // Runs on the search consumer's worker, not the UI thread. Keep CRLF and UTF-16
    // offsets exactly as supplied by Core; TextDocument understands both CRLF and LF.
    public static ResultBatch Format(IEnumerable<ResultSpan> spans)
    {
        var text = new StringBuilder();
        var ranges = new List<StyledRange>();
        foreach (ResultSpan span in spans)
        {
            if (span.Text.Length == 0) continue;
            ranges.Add(new StyledRange(text.Length, span.Text.Length, span.Color, span.Style,
                span.IsFileHeading ? span.FilePath : null));
            text.Append(span.Text);
        }
        return new ResultBatch(text.ToString(), ranges);
    }
}

// One append-only editor document and a compact offset index, not one control per
// line. The view owns rendering; this adapter owns only text and Core metadata.
public sealed class ResultsDocument
{
    private readonly List<StyledRange> ranges = [];
    public TextDocument Document { get; } = new();

    public ResultsDocument() => Document.UndoStack.SizeLimit = 0;

    internal void Clear()
    {
        ranges.Clear();
        Document.Text = string.Empty;
    }

    internal void Append(ResultBatch batch)
    {
        int offset = Document.TextLength;
        foreach (StyledRange range in batch.Ranges)
            ranges.Add(range with { Start = offset + range.Start });
        // Exactly one document mutation per batch. Existing selection/caret and
        // viewport are left alone so users can read/copy while results stream.
        Document.Insert(offset, batch.Text);
    }

    internal IEnumerable<StyledRange> Intersecting(int start, int end)
    {
        for (int i = FindRange(start); i < ranges.Count && ranges[i].Start < end; i++)
            yield return ranges[i];
    }

    internal string? NavigationPathAt(int offset)
    {
        int index = FindRange(offset);
        return index < ranges.Count && ranges[index].Start <= offset ? ranges[index].FilePath : null;
    }

    private int FindRange(int offset)
    {
        int low = 0, high = ranges.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (ranges[middle].End <= offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
