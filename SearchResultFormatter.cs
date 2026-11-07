using System.Drawing;

namespace RipgrepSearch;

internal sealed class SearchResultFormatter
{
    private string? previousFile;
    private long previousLine;

    public IReadOnlyList<ResultSpan> Format(SearchResultLine line)
    {
        // Keep raw result data intact. A one-character visible NUL marker is
        // safe for RichEdit and exports, without shifting UTF-16 match offsets.
        string text = line.Text.Replace('\0', '\u2400');
        var spans = new List<ResultSpan>();
        if (!string.Equals(previousFile, line.FilePath, StringComparison.Ordinal))
        {
            if (previousFile is not null)
                spans.Add(new ResultSpan("\r\n", Color.White, FontStyle.Regular));
            spans.Add(new ResultSpan(line.FilePath.Replace('\0', '\u2400') + "\r\n", Color.DeepSkyBlue,
                FontStyle.Bold | FontStyle.Underline, line.NavigationPath, IsFileHeading: true));
        }
        else if (line.LineNumber > previousLine + 1)
        {
            spans.Add(new ResultSpan("  --\r\n", Color.Gray, FontStyle.Regular));
        }
        previousFile = line.FilePath;
        previousLine = line.LineNumber;

        spans.Add(new ResultSpan($"  {line.LineNumber}{(line.IsMatch ? ':' : '-')} ",
            line.IsMatch ? Color.LimeGreen : Color.DarkGray, FontStyle.Regular));
        int position = 0;
        if (line.IsMatch)
        {
            for (int i = 0; i < line.Matches.Count; i++)
            {
                MatchRange match = line.Matches[i];
                // Empty matches still count as matching lines, but have no glyph to color.
                if (match.Length == 0)
                    continue;
                int start = Math.Max(position, match.Start);
                int end = match.Start + match.Length;
                // Adjacent matches share a style; emit one run instead of thousands
                // of tiny spans for patterns such as '.' on a long line.
                while (i + 1 < line.Matches.Count && line.Matches[i + 1].Start <= end)
                {
                    MatchRange next = line.Matches[++i];
                    end = Math.Max(end, next.Start + next.Length);
                }
                if (end <= start)
                    continue;
                if (start > position)
                    spans.Add(new ResultSpan(text[position..start], Color.White, FontStyle.Regular));
                spans.Add(new ResultSpan(text[start..end], Color.OrangeRed, FontStyle.Bold));
                position = end;
            }
        }
        if (position < text.Length)
            spans.Add(new ResultSpan(text[position..], Color.White, FontStyle.Regular));
        spans.Add(new ResultSpan("\r\n", Color.White, FontStyle.Regular));
        return spans;
    }
}
