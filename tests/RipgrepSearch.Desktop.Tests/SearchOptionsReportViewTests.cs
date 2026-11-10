using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using RipgrepSearch;
using RipgrepSearch.Desktop;

internal static partial class Tests
{
    private static async Task SearchOptionsReportViewTests()
    {
        await OptionPreviewTests();
        await RealOptionsAndReportTests();
        await ViewMenuTests();
        await PartialReportTests();
        await RgaIntegrationTests();
    }

    private static void AssertReportMatchesDocument(SearchViewModel vm) =>
        Check(vm.CurrentReport is not null && string.Concat(vm.CurrentReport.Spans.Select(span => span.Text)) == vm.Results.Document.Text,
            "report spans exactly equal the displayed document");

    private static async Task OptionPreviewTests()
    {
        bool? selectedEngine = null;
        var vm = CreateViewModel(Root, engineResolver: useRga => { selectedEngine = useRga; return FakeTool("capture"); });
        await vm.InitializeAsync();
        Check(vm.BeforeContext == 0 && vm.AfterContext == 0 && !vm.UseRipgrepAll && !vm.SortEnabled &&
            vm.SortBy == "path" && !vm.SortDescending && vm.ShowSearchOptions && !vm.ShowCommand,
            "option defaults match v0.10.0");
        Check(vm.SortFields.SequenceEqual(new[] { "path", "modified", "accessed", "created" }) &&
            vm.SortOrders.SequenceEqual(new[] { "Ascending", "Descending" }), "only baseline sort choices are exposed");
        vm.Pattern = "-needle & \"café 😀\" \\ path";
        foreach (bool rga in new[] { false, true })
        foreach (string field in vm.SortFields)
        foreach (bool descending in new[] { false, true })
        {
            vm.UseRipgrepAll = rga;
            vm.BeforeContext = 2;
            vm.AfterContext = 3;
            vm.SortEnabled = true;
            vm.SortBy = field;
            vm.SortDescending = descending;
            string preview = vm.CommandPreview;
            await vm.SearchAsync();
            var received = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Root, "capture.args.json")))!;
            Check(received.SequenceEqual(SearchRunner.BuildArguments(vm.Pattern, Root, 2, 3, field, descending)),
                "real child receives exact Core arguments for " + rga + "/" + field + "/" + descending);
            Check(selectedEngine == rga && vm.CurrentReport!.Command == preview &&
                preview == SearchCommandFormatter.Format(rga ? "rga" : "rg", received), "preview/report correspond to actually transported arguments and selected engine");
            AssertReportMatchesDocument(vm);
        }
        vm.SortEnabled = false;
        await vm.SearchAsync();
        var unsorted = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Root, "capture.args.json")))!;
        Check(!unsorted.Contains("--sort") && !unsorted.Contains("--sortr") && vm.SortBy == "created" && vm.SortDescending,
            "disabling sort removes arguments without losing field/order");
        vm.BeforeContext = -2; vm.AfterContext = 500; vm.SortBy = "invalid";
        Check(vm.BeforeContext == 0 && vm.AfterContext == 100 && vm.SortBy == "path", "option boundary validation/clamping");
        vm.BeforeContext = 0; vm.AfterContext = 0;
        Check(!vm.CommandPreview.Contains("--before-context") && !vm.CommandPreview.Contains("--after-context"), "zero context omits flags");
        await vm.CloseAsync();
    }

    private static async Task RealOptionsAndReportTests()
    {
        string directory = Path.Combine(Root, "options café 😀 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a café.txt"), "before\n😀 needle needle\nafter\nfar\nfar\nneedle\nend\n");
        File.WriteAllText(Path.Combine(directory, "b 😀.txt"), "before\nneedle\nafter\n");
        var vm = CreateViewModel(directory);
        await vm.InitializeAsync();
        vm.Pattern = "needle"; vm.BeforeContext = 1; vm.AfterContext = 1;
        vm.SortEnabled = true; vm.SortBy = "path"; vm.SortDescending = true;
        DateTimeOffset start = DateTimeOffset.Now;
        await vm.SearchAsync();
        var report = vm.CurrentReport!;
        AssertReportMatchesDocument(vm);
        Check(report.Folder == directory && report.Pattern == "needle" && report.StartedAt >= start && report.StartedAt <= DateTimeOffset.Now,
            "report captures folder/pattern/start metadata for actual execution");
        Check(report.Results.Count(line => line.IsMatch) == 3 && report.Results.Any(line => !line.IsMatch) && vm.MatchingLines == 3,
            "real context is retained but matching-line count excludes context and duplicate occurrences");
        Check(report.Results.Select(line => Path.GetFileName(line.FilePath)).Distinct().SequenceEqual(new[] { "b 😀.txt", "a café.txt" }),
            "report preserves engine descending path order");
        Check(report.Spans.Any(span => span.Text == "  --\r\n") && vm.Results.Document.Text.Contains("  1- before"),
            "real context prefixes and group separators use shared formatting");
        string rendered = vm.Results.Document.Text, command = report.Command, finalStatus = report.Status;
        vm.Pattern = "no such pattern"; vm.BeforeContext = 0; vm.SortDescending = false;
        Check(report.Pattern == "needle" && report.Command == command && report.Status == finalStatus && vm.Results.Document.Text == rendered,
            "editing controls does not mutate the captured report or rebuild output");
        vm.Folder = Path.Combine(directory, "missing");
        await vm.SearchAsync();
        Check(ReferenceEquals(vm.CurrentReport, report) && vm.Results.Document.Text == rendered,
            "validation failure retains previous report and display without relabelling its metadata");
        vm.Folder = directory;
        await vm.SearchAsync();
        Check(!ReferenceEquals(vm.CurrentReport, report) && vm.CurrentReport!.Results.Count == 0 &&
            vm.CurrentReport.Status.Contains("No matches") && vm.Results.Document.Text == "No matches found.",
            "new no-match execution replaces old report with an exportable no-match presentation");
        AssertReportMatchesDocument(vm);
        Check(report.Results.Count(line => line.IsMatch) == 3 && report.Command == command, "successive search never appends to the old report");
        vm.Pattern = "[";
        await vm.SearchAsync();
        Check(vm.CurrentReport!.Pattern == "[" && vm.CurrentReport.Results.Count == 0 && vm.CurrentReport.Status.Contains("exit code 2") &&
            vm.CurrentReport.Spans.Any(span => span.Color == ResultColor.Gray && span.Text.Contains("regex parse error")),
            "regex failure gets a new report with exit-code status and diagnostics");
        AssertReportMatchesDocument(vm);
        vm.Pattern = "needle"; vm.BeforeContext = 0; vm.AfterContext = 0;
        foreach (string field in new[] { "modified", "accessed", "created" })
        foreach (bool descending in new[] { false, true })
        {
            string[] files = Directory.GetFiles(directory);
            for (int i = 0; i < files.Length; i++)
            {
                File.SetLastWriteTimeUtc(files[i], DateTime.UtcNow.AddDays(-4 + i));
                File.SetLastAccessTimeUtc(files[i], DateTime.UtcNow.AddDays(-8 + i));
            }
            var times = files.ToDictionary(path => path, path => field switch
            {
                "modified" => File.GetLastWriteTimeUtc(path), "accessed" => File.GetLastAccessTimeUtc(path),
                _ => File.GetCreationTimeUtc(path)
            });
            vm.SortBy = field; vm.SortDescending = descending;
            await vm.SearchAsync();
            DateTime[] actual = vm.CurrentReport!.Results.Select(line => line.FilePath).Distinct().Select(path => times[path]).ToArray();
            Check(vm.CurrentReport.Status.StartsWith("Search completed") && actual.Length == files.Length &&
                actual.SequenceEqual(descending ? actual.OrderByDescending(time => time) : actual.OrderBy(time => time)),
                "actual macOS rg timestamp ordering for " + field + "/" + descending);
            AssertReportMatchesDocument(vm);
        }
        await vm.CloseAsync();
    }

    private static async Task ViewMenuTests()
    {
        var env = TestEnvironment();
        AppSettingsStore.Save(env.SettingsFile, new AppSettings { BeforeContext = 2, AfterContext = 3, UseRipgrepAll = true,
            SortEnabled = true, SortBy = "accessed", SortDescending = true, ShowSearchOptions = false, ShowCommand = true });
        string original = File.ReadAllText(env.SettingsFile);
        var vm = CreateViewModel(Root, environment: env);
        var window = new MainWindow(vm);
        window.Show();
        await vm.InitializeAsync();
        Render(window);
        var options = window.FindControl<StackPanel>("SearchOptions")!;
        var command = window.FindControl<StackPanel>("CommandSection")!;
        var optionsItem = window.FindControl<MenuItem>("ShowOptionsMenuItem")!;
        var commandItem = window.FindControl<MenuItem>("ShowCommandMenuItem")!;
        var preview = window.FindControl<TextBox>("CommandInput")!;
        var editor = window.FindControl<TextEditor>("ResultsEditor")!;
        Check(File.ReadAllText(env.SettingsFile) == original && vm.BeforeContext == 2 && vm.AfterContext == 3 &&
            vm.UseRipgrepAll && vm.SortBy == "accessed" && vm.SortDescending && !options.IsVisible && command.IsVisible,
            "all persisted controls and visibility restore without initialization write/reset");
        Check(window.FindControl<NumericUpDown>("BeforeInput")!.Value == 2 &&
            window.FindControl<ComboBox>("SortFieldInput")!.SelectedItem?.ToString() == "accessed" &&
            window.FindControl<ComboBox>("SortOrderInput")!.SelectedIndex == 1, "compiled option bindings restore widgets");
        Check(!optionsItem.IsChecked && commandItem.IsChecked && preview.IsReadOnly && preview.Text == vm.CommandPreview,
            "View checked states and selectable read-only preview bind correctly");
        var nativeMenu = NativeMenu.GetMenu(window)!;
        var nativeView = (NativeMenuItem)nativeMenu.Items.Single();
        var nativeOptions = (NativeMenuItem)nativeView.Menu!.Items[0];
        var nativeCommand = (NativeMenuItem)nativeView.Menu.Items[1];
        Check(nativeView.Header == "View" && !nativeOptions.IsChecked && nativeCommand.IsChecked && nativeView.IsEnabled,
            "native View menu restores the same enabled and checked states");
        Check(window.FindControl<Menu>("MainMenu")!.IsVisible == !NativeMenu.GetIsNativeMenuExported(window),
            "in-window menu is the fallback only when no native menu is exported");
        ((INativeMenuItemExporterEventsImplBridge)nativeOptions).RaiseClicked();
        Check(vm.ShowSearchOptions && optionsItem.IsChecked && options.IsVisible && nativeOptions.IsChecked,
            "native menu activation changes the same persisted View property and fallback state");
        ((INativeMenuItemExporterEventsImplBridge)nativeOptions).RaiseClicked();
        Check(!vm.ShowSearchOptions && !nativeOptions.IsChecked && !options.IsVisible,
            "native checkbox activation toggles once and synchronizes back to the model");
        vm.Pattern = "needle";
        string activePreview = vm.CommandPreview;
        vm.Results.Append(ResultBatch.Format(new SearchResultFormatter().Format(new SearchResultLine("café 😀.txt", null, 1,
            "café 😀 needle needle", true, [new MatchRange(8, 6), new MatchRange(15, 6)]))));
        editor.Select(0, editor.Document.TextLength);
        string selected = editor.SelectedText;
        var heights = new Dictionary<(bool, bool), double>();
        var document = editor.Document;
        foreach (bool showOptions in new[] { false, true })
        foreach (bool showCommand in new[] { false, true })
        {
            vm.ShowSearchOptions = showOptions; vm.ShowCommand = showCommand;
            Render(window);
            Check(options.IsVisible == showOptions && command.IsVisible == showCommand &&
                optionsItem.IsChecked == showOptions && commandItem.IsChecked == showCommand, "all four View states reflect actual sections");
            Check(vm.CommandPreview == activePreview && ReferenceEquals(editor.Document, document) && editor.SelectedText == selected,
                "View changes preserve active options, document and cross-line selection");
            heights[(showOptions, showCommand)] = editor.Bounds.Height;
        }
        Check(heights[(false, false)] > heights[(true, false)] && heights[(false, false)] > heights[(false, true)] &&
            heights[(true, false)] > heights[(true, true)], "hidden sections reclaim result space in every combination");
        vm.ShowCommand = false;
        vm.BeforeContext = 4; vm.Pattern = "edited café 😀";
        Check(preview.Text == vm.CommandPreview && vm.CommandPreview.Contains("--before-context 4") &&
            vm.CommandPreview.Contains("edited café 😀"), "preview continues updating while hidden");
        vm.SortEnabled = false;
        Check(!window.FindControl<ComboBox>("SortFieldInput")!.IsEnabled && !window.FindControl<ComboBox>("SortOrderInput")!.IsEnabled,
            "sort field/order widgets disabled when sorting is off");
        // Exercise a real headless pointer activation, not merely a property assignment.
        var view = (MenuItem)window.FindControl<Menu>("MainMenu")!.Items[0]!;
        view.IsSubMenuOpen = true;
        Render(window);
        ClickControl(commandItem);
        Render(window);
        Check(vm.ShowCommand && commandItem.IsChecked && command.IsVisible, "View menu pointer action toggles bound state");
        Check(nativeCommand.IsChecked, "fallback menu activation also synchronizes the native checked state");
        await vm.SaveAsync();
        var reloaded = CreateViewModel(environment: env);
        await reloaded.InitializeAsync();
        Check(reloaded.BeforeContext == 4 && !reloaded.SortEnabled && reloaded.SortBy == "accessed" &&
            reloaded.SortDescending && reloaded.ShowCommand, "option/View change state persists with retained disabled sort choices");
        Check(reloaded.Preferences.SearchHistory[0] == "edited café 😀", "option/View saves preserve WinForms current-text history admission");
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        Render(window);
        Check(editor.Bounds.Height > 0 && editor.Bounds.Width > 0, "results remain allocated at minimum window size with both sections visible");
        using (var frame = window.CaptureRenderedFrame())
            frame?.Save(Path.Combine(AppContext.BaseDirectory, "phase5.3-view.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
        await WaitUntil(() => !window.IsVisible);
        Check(!nativeView.IsEnabled && !nativeOptions.IsEnabled && !nativeCommand.IsEnabled, "native menu disables with window shutdown");
    }

    private static void ClickControl(Control control)
    {
        var top = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(point, MouseButton.Left);
        top.MouseUp(point, MouseButton.Left);
    }

    private static async Task PartialReportTests()
    {
        foreach (string mode in new[] { "partial-error", "warning" })
        {
            var vm = CreateViewModel(Root, () => FakeTool(mode));
            await vm.InitializeAsync(); vm.Pattern = "needle";
            await vm.SearchAsync();
            AssertReportMatchesDocument(vm);
            Check(vm.CurrentReport!.Results.Count == 1 && vm.CurrentReport.Spans.Any(span => span.Text.Contains("simulated")),
                "raw results and diagnostic spans retained for " + mode);
            Check(mode == "warning" ? vm.CurrentReport.Status.Contains("completed") && vm.CurrentReport.Status.Contains("Warnings")
                : vm.CurrentReport.Status.Contains("exit code 2") && vm.CurrentReport.Status.Contains("incomplete"), "accurate report outcome for " + mode);
            await vm.CloseAsync();
        }
        var launchFailure = CreateViewModel(Root, () => Path.Combine(Root, "nonexistent-executable"));
        await launchFailure.InitializeAsync(); launchFailure.Pattern = "needle";
        await launchFailure.SearchAsync();
        AssertReportMatchesDocument(launchFailure);
        Check(launchFailure.CurrentReport!.Results.Count == 0 && launchFailure.CurrentReport.Status.Contains("failed") &&
            launchFailure.CurrentReport.Spans.Count > 0, "found/resolved but unlaunchable engine produces a fresh failed report");
        await launchFailure.CloseAsync();
        var quiet = CreateViewModel(Root, () => FakeTool("quiet"));
        var window = new MainWindow(quiet);
        window.Show(); await quiet.InitializeAsync();
        quiet.Pattern = "needle"; quiet.BeforeContext = 1;
        Task search = quiet.SearchAsync();
        await WaitUntil(() => quiet.BatchCount > 0);
        Check(ReferenceEquals(search, quiet.SearchAsync()) && !quiet.CanSearch && !window.FindControl<StackPanel>("SearchOptions")!.IsEnabled,
            "all search options disabled and overlapping search rejected");
        var report = quiet.CurrentReport!;
        string originalCommand = report.Command;
        quiet.ShowSearchOptions = false; quiet.ShowCommand = true;
        quiet.Pattern = "edited during search"; quiet.BeforeContext = 9;
        Check(quiet.CanChangeView && report.Pattern == "needle" && report.Command == originalCommand && originalCommand.Contains("--before-context 1"),
            "View remains usable during search; active report metadata stays frozen despite later input changes");
        quiet.Cancel(); await search;
        AssertReportMatchesDocument(quiet);
        Check(report.Status.Contains("incomplete") && quiet.CanSearch && ReferenceEquals(report, quiet.CurrentReport), "cancelled current report finalizes and controls restore");
        window.Close(); await WaitUntil(() => !window.IsVisible);
    }

    private static async Task RgaIntegrationTests()
    {
        var missing = CreateViewModel(Root, engineResolver: _ => null);
        await missing.InitializeAsync(); missing.Pattern = "needle"; missing.UseRipgrepAll = true;
        await missing.SearchAsync();
        Check(missing.Status.Contains("rga") && missing.Status.Contains("not found") && missing.CurrentReport is null && missing.CanSearch,
            "missing selected rga is clear, retains no stale report and never falls back");
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: real rga integration wrapper is Unix-specific."); return; }
        string? rga = ExecutableResolver.ResolveSearchTool(true);
        if (rga is null) { Console.WriteLine("SKIP: real rga archive integration (rga not installed)."); return; }
        string directory = Path.Combine(Root, "rga archive " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "café 😀.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("nested/Unicode café.txt").Open()))
            writer.WriteLine("archive needle café 😀");
        // Isolate rga's normal conversion cache under ignored test output.
        string wrapper = Path.Combine(Root, "installed-rga-isolated");
        File.WriteAllText(wrapper, $"#!/bin/sh\nexec {Quote(rga)} {Quote("--rga-cache-path=" + Path.Combine(Root, "rga-cache"))} \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var vm = CreateViewModel(directory, engineResolver: useRga => useRga ? wrapper : ExecutableResolver.ResolveSearchTool(false));
        await vm.InitializeAsync();
        vm.UseRipgrepAll = true; vm.Pattern = "needle"; vm.BeforeContext = 1; vm.AfterContext = 1; vm.SortEnabled = true;
        await vm.SearchAsync();
        AssertReportMatchesDocument(vm);
        Check(vm.CurrentReport!.Status.StartsWith("Search completed") && vm.MatchingLines == 1 && vm.CurrentReport.Command.StartsWith("rga ") &&
            vm.CurrentReport.Results.Any(line => line.NavigationPath == archive && line.Text.Contains("archive needle")),
            "installed macOS rga searches actual ZIP content with context/sort and correct navigation/report metadata");
        await vm.CloseAsync();
    }
}
