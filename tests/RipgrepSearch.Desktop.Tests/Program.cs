using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RipgrepSearch;
using RipgrepSearch.Desktop;

internal static partial class Tests
{
    private static int passed;
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "Phase 4 café 😀");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-rg") return await FakeRg(args[1], args.Skip(2).ToArray());
        if (args is ["--native-menu-smoke"]) return NativeMenuSmoke();
        try
        {
            Directory.CreateDirectory(Root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await using var session = HeadlessUnitTestSession.StartNew(typeof(Tests));
            await session.Dispatch(async () =>
            {
                try
                {
                    await SettingsHistoryTests();
                    await SearchOptionsReportViewTests();
                    await DocumentTests();
                    await SearchTests();
                    await LifetimeTests();
                    await PerformanceTests();
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
            }, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            Console.WriteLine($"PASS: {passed} Desktop checks (headless Skia; not native GUI acceptance).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        passed++;
    }

    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static async Task DocumentTests()
    {
        var vm = CreateViewModel(Root);
        var window = new MainWindow(vm);
        window.Show();
        await vm.InitializeAsync();
        var editor = window.FindControl<TextEditor>("ResultsEditor")!;
        var formatter = new SearchResultFormatter();
        const string text = "café 😀 needle\0 tail";
        var lines = new[]
        {
            new SearchResultLine("café 😀.txt", Path.Combine(Root, "café 😀.txt"), 1, text, true, [new MatchRange(5, 2), new MatchRange(8, 6)]),
            new SearchResultLine("café 😀.txt", Path.Combine(Root, "café 😀.txt"), 2, "context", false, []),
            new SearchResultLine("café 😀.txt", Path.Combine(Root, "café 😀.txt"), 6, "zero length", true, [new MatchRange(4, 0)]),
            new SearchResultLine("second.txt", null, 1, "next file", true, [new MatchRange(0, 4)])
        };
        vm.Results.Append(ResultBatch.Format(lines.Take(2).SelectMany(formatter.Format)));
        Render(window);
        int start = editor.Text.IndexOf("😀 needle", StringComparison.Ordinal);
        editor.Select(start, 18);
        string selected = editor.SelectedText;
        vm.Results.Append(ResultBatch.Format(lines.Skip(2).SelectMany(formatter.Format)));
        Render(window);
        Check(editor.Document == vm.Results.Document && editor.IsReadOnly, "real view binds the read-only document");
        Check(editor.SelectedText == selected && selected.Contains('\n'), "cross-line selection survives incremental append");
        Check(editor.Text.Contains("␀") && editor.Text.Contains("  --\r\n") && !editor.Text.Contains('\0'), "Core NUL substitution/grouping/context preserved");
        Check(vm.Results.NavigationPathAt(0) == lines[0].NavigationPath && vm.Results.NavigationPathAt(start) is null,
            "only decodable filename headings navigate");
        int emojiOffset = editor.Text.IndexOf("😀 needle", StringComparison.Ordinal);
        Check(vm.Results.Intersecting(emojiOffset, emojiOffset + 2).Single().Length == 2, "UTF-16 emoji range remains intact");
        Check(vm.Results.Intersecting(emojiOffset, emojiOffset + 2).Single().Color == ResultColor.OrangeRed, "match consumes Core style metadata");
        var elements = editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements).ToArray();
        Check(elements.Any(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush b && b.Color == Color.FromRgb(255, 69, 0)),
            "visible editor glyph runs are orange-red");
        Check(elements.Any(e => e.TextRunProperties.TextDecorations?.Count > 0 && e.TextRunProperties.Typeface.Weight == FontWeight.Bold),
            "filename glyph runs are bold and underlined");
        editor.TextArea.Focus();
        editor.Select(0, editor.Text.Length);
        // Exercise the editor's platform copy gesture and the headless clipboard.
        window.KeyPress(Key.C, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.C, "c");
        Check(await window.Clipboard!.TryGetTextAsync().WaitAsync(TimeSpan.FromSeconds(5)) == editor.Text.Replace("\r\n", Environment.NewLine), "editor copy gesture copies selection across lines");
        vm.Results.Clear();
        Check(editor.Document.TextLength == 0 && vm.Results.NavigationPathAt(0) is null, "clear removes text and navigation index");
        window.Close();
        await WaitUntil(() => !window.IsVisible);
    }

    private static async Task SearchTests()
    {
        string searchFolder = Path.Combine(Root, "functional");
        Directory.CreateDirectory(searchFolder);
        var vm = CreateViewModel(searchFolder);
        vm.Pattern = "needle";
        var window = new MainWindow(vm);
        window.Show();
        await vm.InitializeAsync();
        vm.Folder = Path.Combine(Root, "missing");
        await vm.SearchAsync();
        Check(vm.CanSearch && vm.Status.Contains("does not exist"), "invalid folder is recoverable");
        vm.Folder = searchFolder;
        vm.Pattern = " ";
        await vm.SearchAsync();
        Check(vm.Status.StartsWith("Enter a search"), "empty pattern validation");
        var missing = CreateViewModel(Root, () => null);
        await missing.InitializeAsync();
        missing.Pattern = "needle";
        await missing.SearchAsync();
        Check(missing.Status.Contains("was not found on PATH") && missing.CanSearch, "missing executable gives actionable error");
        File.WriteAllText(Path.Combine(searchFolder, "café 😀.txt"), "before\n😀 needle needle\nafter\n");
        vm.Pattern = "😀|needle";
        await vm.SearchAsync();
        Render(window);
        Check(vm.MatchingLines == 1 && vm.Status.StartsWith("Search completed"), "installed rg executes with Unicode folder, filename, pattern");
        Check(vm.Results.Document.Text.Contains("😀 needle needle") && vm.BatchCount > 0, "real search results render through Core");
        vm.Pattern = "nothing-matches-this";
        await vm.SearchAsync();
        Check(vm.MatchingLines == 0 && vm.Results.Document.Text.Contains("No matches found"), "no-match search completes and clears previous data");
        vm.Pattern = "[";
        await vm.SearchAsync();
        Check(vm.Status.StartsWith("Search failed") && vm.Results.Document.Text.Contains("regex parse error"), "rg regex error/diagnostics reach view");
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.MatchingLines) && vm.MatchingLines > 0) vm.Cancel();
        };
        vm.Pattern = "needle";
        await vm.SearchAsync();
        Check(vm.Status.StartsWith("Search cancelled") && vm.Status.Contains("incomplete"),
            "cancel during consumption stays partial even if rg has already completed");
        window.Close();
        await WaitUntil(() => !window.IsVisible);
    }

    private static string FakeTool(string mode)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These launcher fixtures target Unix; run Windows regressions separately.");
        string path = Path.Combine(Root, "fake-rg-" + mode);
        string host = Environment.ProcessPath!;
        string command = Path.GetFileNameWithoutExtension(host) == "dotnet"
            ? $"{Quote(host)} {Quote(typeof(Tests).Assembly.Location)}" : Quote(host);
        File.WriteAllText(path, $"#!/bin/sh\nexec {command} --fake-rg {Quote(mode)} \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static async Task LifetimeTests()
    {
        string tool = FakeTool("quiet");
        var vm = CreateViewModel(Root, () => tool);
        vm.Pattern = "needle";
        var window = new MainWindow(vm);
        window.Show();
        await vm.InitializeAsync();
        Task search = vm.SearchAsync();
        await WaitUntil(() => vm.BatchCount > 0);
        int pid = int.Parse(File.ReadAllText(Path.Combine(Root, "quiet.pid")));
        int descendant = int.Parse(File.ReadAllText(Path.Combine(Root, "descendant.pid")));
        Check(vm.IsSearching && !vm.SearchCommand.CanExecute(null) && vm.CancelCommand.CanExecute(null), "busy commands and streaming batch before process exit");
        Check(!window.FindControl<ComboBox>("PatternInput")!.IsEnabled, "inputs disabled while search runs");
        var watch = Stopwatch.StartNew();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await search;
        Check(vm.Status.StartsWith("Search cancelled") && vm.CanSearch, "Escape cancels and restores input state");
        Check(HasExited(pid) && HasExited(descendant), "cancellation terminates process and descendant");
        AssertReportMatchesDocument(vm);
        Check(vm.CurrentReport!.Status.Contains("incomplete") && vm.CurrentReport.Results.Count == vm.MatchingLines,
            "cancelled report captures only displayed results and partial completion");
        Console.WriteLine($"MEASURE quiet cancellation: {watch.Elapsed.TotalMilliseconds:F1} ms");
        search = vm.SearchAsync();
        await WaitUntil(() => vm.BatchCount > 0);
        pid = int.Parse(File.ReadAllText(Path.Combine(Root, "quiet.pid")));
        descendant = int.Parse(File.ReadAllText(Path.Combine(Root, "descendant.pid")));
        window.Close();
        await search;
        await WaitUntil(() => !window.IsVisible);
        Check(HasExited(pid) && HasExited(descendant) && !vm.IsSearching, "window close awaits process-tree cleanup before closing");
    }

    private static bool HasExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Waiting for UI/process state.");
            await Task.Delay(10);
        }
    }

    private static async Task PerformanceTests()
    {
        // Short temporary paths let the line cap be reached before Core's path-
        // inclusive character budget. Other fixtures/IDs stay in ignored output.
        string directory = Path.Combine(Path.GetTempPath(), "rgs4-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        for (int file = 0; file < 200; file++)
            File.WriteAllText(Path.Combine(directory, $"file {file:D3} café.txt"), string.Concat(Enumerable.Repeat("😀 needle needle\n", 55)));
        var vm = CreateViewModel(directory);
        vm.Pattern = "needle";
        var window = new MainWindow(vm);
        window.Show();
        await vm.InitializeAsync();
        Render(window);
        long before = GC.GetTotalMemory(true);
        var elapsed = Stopwatch.StartNew();
        double? firstFrame = null;
        bool frameQueued = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.MatchingLines) || vm.MatchingLines == 0 || frameQueued) return;
            frameQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                Render(window);
                firstFrame = elapsed.Elapsed.TotalMilliseconds;
            }, DispatcherPriority.Render);
        };
        int heartbeatCount = 0;
        double previousTick = 0, maxTickGap = 0;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) =>
        {
            double now = elapsed.Elapsed.TotalMilliseconds;
            maxTickGap = Math.Max(maxTickGap, now - previousTick);
            previousTick = now;
            heartbeatCount++;
        };
        heartbeat.Start();
        await vm.SearchAsync();
        heartbeat.Stop();
        Render(window);
        Check(vm.MatchingLines == 10_000 && vm.Status.StartsWith("Display limit reached"), "10,000-line cap retained with many matching files");
        Check(vm.BatchCount > 50, "large search rendered progressively in bounded batches");
        Check(vm.CurrentReport!.Results.Count == 10_000 && vm.CurrentReport.Status.Contains("search stopped"), "line-limited report remains bounded and explicitly incomplete");
        Console.WriteLine($"MEASURE 10k: first append={vm.FirstBatchMilliseconds:F1} ms, first headless frame={firstFrame:F1} ms, total+layout={elapsed.Elapsed.TotalMilliseconds:F1} ms, batches={vm.BatchCount}, max append={vm.MaxAppendMilliseconds:F2} ms, text={vm.Results.Document.TextLength:N0} UTF-16, managed delta={GC.GetTotalMemory(true) - before:N0} bytes, process working set={Environment.WorkingSet:N0} bytes; 16ms heartbeat ticks={heartbeatCount}, max gap={maxTickGap:F1} ms");
        // Compare full text after memory sampling: the assertion itself creates
        // full-document strings that production incremental rendering never needs.
        AssertReportMatchesDocument(vm);
        Check(firstFrame is not null && heartbeatCount > 0, "render and timer work run while search streams");
        var editor = window.FindControl<TextEditor>("ResultsEditor")!;
        var scrolling = Stopwatch.StartNew();
        for (int step = 0; step < 30; step++)
        {
            editor.ScrollToLine(1 + step * (editor.Document.LineCount - 1) / 29);
            Render(window);
        }
        Console.WriteLine($"MEASURE 30 viewport jumps+Skia frames: {scrolling.Elapsed.TotalMilliseconds:F1} ms");
        var selection = Stopwatch.StartNew();
        editor.Select(0, editor.Document.TextLength);
        Check(editor.SelectionLength == editor.Document.TextLength, "full-document cross-line selection on large output");
        Render(window);
        Console.WriteLine($"MEASURE select-all+Skia frame: {selection.Elapsed.TotalMilliseconds:F1} ms");
        string nearLimit = Path.Combine(Root, "character budget");
        Directory.CreateDirectory(nearLimit);
        File.WriteAllText(Path.Combine(nearLimit, "long.txt"), string.Concat(Enumerable.Repeat("needle " + new string('y', 15_993) + "\n", 130)));
        vm.Folder = nearLimit;
        await vm.SearchAsync();
        Render(window);
        Check(vm.Status.StartsWith("Display limit reached") && vm.Results.Document.TextLength is > 1_900_000 and < 2_010_000,
            "approximately 2M-character budget retained");
        Check(vm.CurrentReport!.Results.Count < 130 && vm.CurrentReport.Results.All(line => line.Text.Length == 16_000), "character-limited report retains only displayed raw lines");
        Console.WriteLine($"MEASURE near 2M: first append={vm.FirstBatchMilliseconds:F1} ms, max append={vm.MaxAppendMilliseconds:F2} ms, text={vm.Results.Document.TextLength:N0} UTF-16, working set={Environment.WorkingSet:N0} bytes");
        AssertReportMatchesDocument(vm);
        editor.ScrollToLine(2);
        Render(window);
        // AvaloniaEdit 12.0.0's ScrollToHorizontalOffset is a stub. Exercise
        // the actual ScrollViewer used by its scrollbar and trackpad handling.
        var scroller = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(5000, scroller.Offset.Y);
        Render(window);
        Check(editor.HorizontalOffset > 0, "long lines scroll horizontally without wrapping");
        string oversized = Path.Combine(Root, "oversized");
        Directory.CreateDirectory(oversized);
        File.WriteAllText(Path.Combine(oversized, "long.txt"), "needle " + new string('z', 16_385));
        vm.Folder = oversized;
        await vm.SearchAsync();
        Check(vm.Status.StartsWith("Display limit reached") && vm.Results.Document.Text.Contains("16,384"), "oversized line gives clear limit diagnostic");
        window.Close();
        await WaitUntil(() => !window.IsVisible);
        Directory.Delete(directory, recursive: true);
    }

    private static async Task<int> FakeRg(string mode, string[] arguments)
    {
        File.WriteAllText(Path.Combine(Root, mode + ".pid"), Environment.ProcessId.ToString());
        if (mode is "capture" or "partial-error" or "warning")
        {
            File.WriteAllText(Path.Combine(Root, mode + ".args.json"), JsonSerializer.Serialize(arguments));
            Console.WriteLine(JsonSerializer.Serialize(new { type = "match", data = new { path = new { text = "fixture.txt" }, line_number = 1,
                lines = new { text = "needle\n" }, submatches = new[] { new { start = 0, end = 6 } } } }));
            if (mode == "partial-error") { Console.Error.WriteLine("simulated partial failure"); return 2; }
            if (mode == "warning") Console.Error.WriteLine("simulated warning");
            Console.WriteLine("{\"type\":\"summary\",\"data\":{}}");
            return 0;
        }
        if (mode == "descendant")
        {
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet")
            childInfo.ArgumentList.Add(typeof(Tests).Assembly.Location);
        childInfo.ArgumentList.Add("--fake-rg");
        childInfo.ArgumentList.Add("descendant");
        using var child = Process.Start(childInfo)!;
        // Do not flush the parent's marker until the descendant has initialized.
        string pidPath = Path.Combine(Root, "descendant.pid");
        while (!File.Exists(pidPath) || File.ReadAllText(pidPath) != child.Id.ToString())
            await Task.Delay(10);
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine(JsonSerializer.Serialize(new { type = "match", data = new { path = new { text = "fixture.txt" }, line_number = 1, lines = new { text = "needle\n" }, submatches = new[] { new { start = 0, end = 6 } } } }));
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
