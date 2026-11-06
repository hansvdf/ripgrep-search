using System.Drawing;
using System.Text;
using System.Text.Json;
using RipgrepSearch;

internal static class Tests
{
    private static int passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            ParserTests();
            Console.WriteLine($"PASS: {passed} checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAIL: " + name);
        passed++;
    }

    private static string Message(byte[] bytes, (int Start, int End)[] ranges,
        bool base64 = false, string type = "match", long lineNumber = 1)
    {
        object lines = base64 ? new { bytes = Convert.ToBase64String(bytes) } : new { text = Encoding.UTF8.GetString(bytes) };
        return JsonSerializer.Serialize(new
        {
            type,
            data = new
            {
                path = new { text = @"C:\fixtures\café [test].txt" },
                line_number = lineNumber,
                lines,
                submatches = ranges.Select(r => new { start = r.Start, end = r.End })
            }
        });
    }

    private static void ParserTests()
    {
        var parser = new RipgrepJsonParser(Environment.CurrentDirectory);
        string text = "café 😀 needle needle\r\n";
        var line = parser.Parse(Message(Encoding.UTF8.GetBytes(text), [(11, 17), (18, 24)]))[0];
        Check(line.Text == text.TrimEnd('\r', '\n'), "CRLF preserved correctly");
        Check(line.Matches.SequenceEqual(new[] { new MatchRange(8, 6), new MatchRange(15, 6) }), "byte to UTF-16 offsets");
        Check(line.Matches.All(m => line.Text.Substring(m.Start, m.Length) == "needle"), "multiple Unicode-adjacent matches");
        Check(parser.Parse(Message(Encoding.UTF8.GetBytes(text), [(11, 17)], true))[0].Text == line.Text, "base64 valid UTF-8");
        var invalid = parser.Parse(Message([0xff, 0xe2, 0x82, 0x20, 0x61, 0x0a], [(4, 5)], true))[0];
        Check(invalid.Text == Encoding.UTF8.GetString(new byte[] { 0xff, 0xe2, 0x82, 0x20, 0x61 }), "invalid UTF-8 replacement");
        Check(invalid.Text.Substring(invalid.Matches[0].Start, invalid.Matches[0].Length) == "a", "invalid UTF-8 offset alignment");
        var inside = parser.Parse(Message(Encoding.UTF8.GetBytes("😀\n"), [(1, 3)]))[0];
        Check(inside.Matches[0] == new MatchRange(0, 2), "byte matches cannot split a surrogate pair");
        var empty = parser.Parse(Message(Encoding.UTF8.GetBytes("abc\n"), [(0, 0), (3, 3)]))[0];
        Check(empty.IsMatch && empty.Matches.All(m => m.Length == 0), "zero-length ranges retained");
        var formatter = new SearchResultFormatter();
        var spans = formatter.Format(empty);
        Check(string.Concat(spans.Select(s => s.Text)).EndsWith("  1: abc\r\n"), "zero-length matches do not drop content");
        Check(!spans.Any(s => s.Color == Color.OrangeRed), "zero-length matches do not invent a colored character");
        Check(spans[0].FilePath == empty.NavigationPath && spans[0].IsFileHeading, "navigation heading metadata");
        var context = parser.Parse(Message(Encoding.UTF8.GetBytes("\n"), [], type: "context", lineNumber: 3))[0];
        spans = formatter.Format(context);
        Check(string.Concat(spans.Select(s => s.Text)) == "  --\r\n  3- \r\n", "context gaps and blank context lines");
        Check(spans.Any(s => s.Color == Color.DarkGray), "context number color");
        var multiline = parser.Parse(Message(Encoding.UTF8.GetBytes("ab\r\ncd\n"), [(1, 5)]));
        Check(multiline.Count == 2 && multiline[1].LineNumber == 2, "multiline record physical lines");
        Check(multiline[0].Matches[0] == new MatchRange(1, 1) && multiline[1].Matches[0] == new MatchRange(0, 1), "multiline ranges clipped to content");
        Check(parser.Parse(Message([], [(0, 0)]))[0].Text == "", "empty matched line");
        foreach (string broken in new[] { "not json", "{}", Message([97], [(0, 2)]), Message([97], [(1, 0)]), Message([97], [], lineNumber: 0), "{\"type\":\"match\",\"data\":{}}" })
        {
            bool rejected = false;
            try { parser.Parse(broken); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "malformed result rejected");
        }
        Check(parser.Parse("{\"type\":\"future-message\",\"data\":{}}").Count == 0, "future events ignored");
        parser.Parse("{\"type\":\"summary\",\"data\":{\"stats\":{\"matches\":999}}}");
        Check(parser.HasSummary, "summary completion detected without treating matches as line count");
    }
}
