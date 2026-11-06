using System.Buffers;
using System.Text;
using System.Text.Json;

namespace RipgrepSearch;

internal sealed class RipgrepJsonParser(string workingDirectory)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public bool HasSummary { get; private set; }

    public IReadOnlyList<SearchResultLine> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (type == "summary")
            {
                root.GetProperty("data");
                HasSummary = true;
                return [];
            }
            if (type is not ("match" or "context"))
                return []; // File lifecycle, statistics, and future message types.

            JsonElement data = root.GetProperty("data");
            byte[] pathBytes = ReadBytes(data.GetProperty("path"));
            string filePath = Encoding.UTF8.GetString(pathBytes);
            string? navigationPath = null;
            try
            {
                navigationPath = Path.GetFullPath(StrictUtf8.GetString(pathBytes), workingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid path bytes can be displayed but must not open a different file.
            }

            long lineNumber = data.GetProperty("line_number").GetInt64();
            if (lineNumber < 1)
                throw new InvalidDataException("Invalid result line number.");

            byte[] bytes = ReadBytes(data.GetProperty("lines"));
            var (text, floor, ceiling) = Decode(bytes);
            var ranges = new List<MatchRange>();
            int previousStart = -1;
            foreach (JsonElement match in data.GetProperty("submatches").EnumerateArray())
            {
                int start = match.GetProperty("start").GetInt32();
                int end = match.GetProperty("end").GetInt32();
                if (start < previousStart || start < 0 || end < start || end > bytes.Length)
                    throw new InvalidDataException("Invalid match byte offsets.");
                previousStart = start;
                int characterStart = floor[start];
                int characterEnd = start == end ? characterStart : ceiling[end];
                ranges.Add(new MatchRange(characterStart, characterEnd - characterStart));
            }

            var lines = new List<SearchResultLine>();
            int lineStart = 0;
            int rangeStart = 0;
            do
            {
                int newline = text.IndexOf('\n', lineStart);
                int lineEnd = newline < 0 ? text.Length : newline;
                if (newline >= 0 && lineEnd > lineStart && text[lineEnd - 1] == '\r')
                    lineEnd--;

                while (rangeStart < ranges.Count && ranges[rangeStart].Start + ranges[rangeStart].Length < lineStart)
                    rangeStart++;
                var lineRanges = new List<MatchRange>();
                for (int i = rangeStart; i < ranges.Count && ranges[i].Start <= lineEnd; i++)
                {
                    MatchRange range = ranges[i];
                    int start = Math.Max(lineStart, range.Start);
                    int end = Math.Min(lineEnd, range.Start + range.Length);
                    if (end > start || (range.Length == 0 && range.Start >= lineStart && range.Start <= lineEnd))
                        lineRanges.Add(new MatchRange(start - lineStart, end - start));
                }

                lines.Add(new SearchResultLine(filePath, navigationPath, lineNumber++,
                    text[lineStart..lineEnd], type == "match", lineRanges));
                if (newline < 0)
                    break;
                lineStart = newline + 1;
            } while (lineStart < text.Length);
            return lines;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new InvalidDataException("Malformed JSON search result.", ex);
        }
    }

    private static byte[] ReadBytes(JsonElement value)
    {
        if (value.TryGetProperty("text", out JsonElement text))
            return Encoding.UTF8.GetBytes(text.GetString() ?? throw new InvalidDataException("Null result text."));
        if (value.TryGetProperty("bytes", out JsonElement bytes))
            return Convert.FromBase64String(bytes.GetString() ?? throw new InvalidDataException("Null result bytes."));
        throw new InvalidDataException("Result text has neither text nor bytes.");
    }

    private static (string Text, int[] Floor, int[] Ceiling) Decode(byte[] bytes)
    {
        // Decode once, including invalid UTF-8. Interior byte offsets round outwards
        // to the containing Unicode scalar rather than splitting surrogate pairs.
        var text = new StringBuilder();
        var floor = new int[bytes.Length + 1];
        var ceiling = new int[bytes.Length + 1];
        Span<char> characters = stackalloc char[2];
        for (int offset = 0; offset < bytes.Length;)
        {
            OperationStatus status = Rune.DecodeFromUtf8(bytes.AsSpan(offset), out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
                rune = Rune.ReplacementChar;
            consumed = Math.Max(1, consumed);
            int start = text.Length;
            int length = rune.EncodeToUtf16(characters);
            text.Append(characters[..length]);
            for (int i = 0; i < consumed; i++)
            {
                floor[offset + i] = start;
                ceiling[offset + i] = i == 0 ? start : start + length;
            }
            offset += consumed;
            floor[offset] = ceiling[offset] = text.Length;
        }
        return (text.ToString(), floor, ceiling);
    }
}
