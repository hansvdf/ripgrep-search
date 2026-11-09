using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RipgrepSearch;

// Package-free portable tests. Child-process fixtures use this executable, so
// neither Windows Forms nor an installed rg/rga is required for this suite.
internal static partial class Tests
{
    private const string Summary = "{\"type\":\"summary\",\"data\":{\"stats\":{\"matches\":999}}}";
    private static int passed;

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
            return await FakeVersion();
        if (args.Length > 0 && args[0] == "--fake")
            return await FakeProcess(args[1], args.Skip(2).ToArray());
        try
        {
            ParserTests();
            ArgumentTests();
            PresentationTests();
            SettingsTests();
            HistoryTests();
            DiagnosticsTests();
            await DependencyProbeTests();
            await RunnerTests();
            Console.WriteLine($"PASS: {passed} portable Core checks ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception("FAIL: " + description);
        passed++;
    }

    private static string Message(byte[] bytes, (int Start, int End)[] ranges,
        bool base64 = false, string type = "match", long line = 1, bool invalidPath = false) =>
        JsonSerializer.Serialize(new
        {
            type,
            data = new
            {
                path = invalidPath ? (object)new { bytes = "/w==" } : new { text = "café [#] 😀.txt" },
                line_number = line,
                lines = base64 ? (object)new { bytes = Convert.ToBase64String(bytes) } : new { text = Encoding.UTF8.GetString(bytes) },
                submatches = ranges.Select(range => new { start = range.Start, end = range.End })
            }
        });

    private static void ParserTests()
    {
        var parser = new RipgrepJsonParser(Environment.CurrentDirectory);
        byte[] bytes = Encoding.UTF8.GetBytes("café 😀 needle needle\r\n");
        foreach (bool base64 in new[] { false, true })
        {
            SearchResultLine line = parser.Parse(Message(bytes, [(11, 17), (18, 24)], base64))[0];
            Check(line.Text == "café 😀 needle needle", "CRLF and Unicode preserved");
            Check(line.Matches.SequenceEqual(new[] { new MatchRange(8, 6), new MatchRange(15, 6) }), "multiple UTF-8 offsets become UTF-16 ranges");
            Check(line.NavigationPath == Path.GetFullPath("café [#] 😀.txt"), "relative navigation path uses working directory");
        }
        var invalid = parser.Parse(Message([0xff, 32, 97, 10], [(2, 3)], base64: true))[0];
        Check(invalid.Text == "� a" && invalid.Matches.Single() == new MatchRange(2, 1), "invalid UTF-8 does not shift later matches");
        var unopenable = parser.Parse(Message([97], [(0, 1)], invalidPath: true))[0];
        Check(unopenable.NavigationPath is null && unopenable.FilePath == "�", "invalid path bytes cannot navigate");
        var emoji = parser.Parse(Message(Encoding.UTF8.GetBytes("😀\n"), [(1, 3)]))[0];
        Check(emoji.Matches.Single() == new MatchRange(0, 2), "interior byte offsets do not split surrogate pairs");
        var empty = parser.Parse(Message(Encoding.UTF8.GetBytes("abc\n"), [(0, 0), (3, 3)]))[0];
        Check(empty.IsMatch && empty.Matches.All(range => range.Length == 0), "zero-length matches retain matching-line semantics");
        var context = parser.Parse(Message([10], [], type: "context", line: 4))[0];
        Check(!context.IsMatch && context.LineNumber == 4 && context.Text == "", "blank context line retained");
        var multiline = parser.Parse(Message(Encoding.UTF8.GetBytes("ab\r\ncd\n"), [(1, 5)]));
        Check(multiline.Count == 2 && multiline[1].LineNumber == 2 &&
            multiline[0].Matches.Single() == new MatchRange(1, 1) && multiline[1].Matches.Single() == new MatchRange(0, 1), "multiline ranges clip to physical lines");
        Check(parser.Parse(Message(Encoding.UTF8.GetBytes("a\0b\n"), [(2, 3)]))[0].Text == "a\0b", "raw NUL remains in structured text");
        foreach (string malformed in new[] { "not json", "{}", Message([97], [(0, 2)]), Message([97], [(1, 0)]), Message([97], [], line: 0) })
        {
            bool rejected = false;
            try { parser.Parse(malformed); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "malformed JSON or offsets rejected");
        }
        Check(parser.Parse("{\"type\":\"future-event\"}").Count == 0, "unknown event ignored");
        Check(parser.Parse(Summary).Count == 0 && parser.HasSummary, "summary detected without using occurrence statistics");
        bool cancelled = false;
        try { parser.Parse(Summary, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "parser observes cancellation");
        Check(!typeof(SearchRunner).Assembly.GetReferencedAssemblies().Any(name =>
            name.Name is "System.Windows.Forms" or "System.Drawing.Common" or "RipgrepSearch"), "Core has no UI/application dependency");
    }

    private static void ArgumentTests()
    {
        Check(SearchRunner.BuildArguments("-pattern & 😀", "folder with spaces", 0, 0, null, false)
            .SequenceEqual(new[] { "--json", "--line-number", "--color", "never", "--", "-pattern & 😀", "folder with spaces" }), "default arguments and option terminator preserved");
        foreach (string field in new[] { "path", "modified", "accessed", "created" })
        foreach (bool descending in new[] { false, true })
            Check(SearchRunner.BuildArguments("needle", "folder", 2, 3, field, descending).SequenceEqual(new[]
            {
                "--json", "--line-number", "--color", "never", "--before-context", "2", "--after-context", "3",
                descending ? "--sortr" : "--sort", field, "--", "needle", "folder"
            }), "context and sort arguments preserved");
        Check(new SearchLimits() == new SearchLimits(10_000, 2_000_000, 16_384, 4_194_304), "original default display limits");
    }

    private static async Task RunnerTests()
    {
        var normal = await Run("normal", extraArguments: ["spaces & quotes \" café 😀"]);
        Check(normal.Outcome.Completion == SearchCompletion.Completed && normal.Outcome.ExitCode == 0, "child process and Unicode argument transport succeed");
        Check(normal.Lines.Count == 2 && normal.Lines.Count(line => line.IsMatch) == 1 && normal.Lines[0].Matches.Count == 2, "matching-line count differs from occurrences and summary stats");
        var noMatches = await Run("no-matches");
        Check(noMatches.Outcome.Completion == SearchCompletion.Completed && noMatches.Outcome.ExitCode == 1 && noMatches.Lines.Count == 0, "exit 1 with summary is a completed empty search");
        var large = await Run("many");
        Check(large.Outcome.Completion == SearchCompletion.Completed && large.Lines.Count == 300 && large.BatchSizes.Count >= 3 && large.BatchSizes.All(size => size <= 128), "incremental batches retain every line");
        foreach (var limits in new[]
        {
            new SearchLimits(MaxLines: 3), new SearchLimits(MaxCharacters: 200),
            new SearchLimits(MaxLineCharacters: 2), new SearchLimits(MaxJsonMessageCharacters: 10)
        })
        {
            var limited = await Run("many", limits);
            Check(limited.Outcome.Completion == SearchCompletion.Limited, "line/character/long-line/JSON limits stop searches");
        }
        foreach (string mode in new[] { "malformed", "truncated", "error" })
        {
            var failed = await Run(mode);
            Check(failed.Outcome.Completion == SearchCompletion.Failed && failed.Lines.Count == 1, "partial result retained after " + mode);
            if (mode == "error")
                Check(failed.Outcome.ExitCode == 2 && failed.Outcome.Diagnostics.Contains("simulated error"), "nonzero exit and stderr retained");
        }
        using var cancellation = new CancellationTokenSource();
        var streamed = await Run("quiet", cancellation: cancellation, onBatch: () => cancellation.Cancel());
        Check(streamed.Lines.Count == 1 && streamed.Outcome.Completion == SearchCompletion.Cancelled, "quiet-period batch arrives before exit and allows cancellation");
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        Check((await Run("quiet", cancellation: preCancelled)).Outcome.Completion == SearchCompletion.Cancelled, "pre-cancelled process search");
        var missing = await Run("normal", executable: Path.Combine(AppContext.BaseDirectory, "missing-" + Guid.NewGuid().ToString("N")));
        Check(missing.Outcome.Completion == SearchCompletion.Failed && missing.Lines.Count == 0, "missing executable completes the channel");
    }

    private static async Task<(SearchOutcome Outcome, List<SearchResultLine> Lines, List<int> BatchSizes)> Run(
        string mode, SearchLimits? limits = null, string[]? extraArguments = null,
        CancellationTokenSource? cancellation = null, Action? onBatch = null, string? executable = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation?.Token ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string host = executable ?? Environment.ProcessPath!;
        var arguments = new List<string>();
        // Support both the platform apphost and `dotnet path/to/tests.dll`.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange(["--fake", mode]);
        arguments.AddRange(extraArguments ?? []);
        var channel = Channel.CreateBounded<IReadOnlyList<SearchResultLine>>(2);
        Task<SearchOutcome> runner = Task.Run(() => SearchRunner.RunAsync(host, arguments,
            Environment.CurrentDirectory, channel.Writer, timeout.Token, limits));
        var lines = new List<SearchResultLine>();
        var batchSizes = new List<int>();
        await foreach (var batch in channel.Reader.ReadAllAsync())
        {
            lines.AddRange(batch);
            batchSizes.Add(batch.Count);
            onBatch?.Invoke();
        }
        SearchOutcome outcome = await runner;
        if (timeout.IsCancellationRequested && cancellation?.IsCancellationRequested != true)
            throw new TimeoutException("Portable process scenario timed out: " + mode);
        return (outcome, lines, batchSizes);
    }

    private static async Task<int> FakeProcess(string mode, string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (mode == "normal" && !arguments.SequenceEqual(new[] { "spaces & quotes \" café 😀" }))
            return 2;
        if (mode != "no-matches")
        {
            int count = mode == "many" ? 300 : 1;
            for (int line = 1; line <= count; line++)
                Console.WriteLine(Message(Encoding.UTF8.GetBytes("café 😀 needle needle\n"), [(11, 17), (18, 24)], line: line));
            await Console.Out.FlushAsync();
        }
        switch (mode)
        {
            case "quiet": await Task.Delay(Timeout.Infinite); break;
            case "malformed": Console.WriteLine("invalid json"); return 0;
            case "truncated": return 0;
            case "error": Console.Error.WriteLine("simulated error"); return 2;
            case "normal": Console.WriteLine(Message(Encoding.UTF8.GetBytes("context\n"), [], type: "context", line: 2)); break;
        }
        Console.WriteLine(Summary);
        return mode == "no-matches" ? 1 : 0;
    }
}
