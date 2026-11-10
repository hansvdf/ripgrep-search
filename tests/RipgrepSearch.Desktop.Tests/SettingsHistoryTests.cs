using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RipgrepSearch;
using RipgrepSearch.Desktop;

internal static partial class Tests
{
    private static SearchViewModel CreateViewModel(string? folder = null, Func<string?>? resolver = null,
        DesktopEnvironment? environment = null, Func<string, AppSettings>? loader = null, Func<bool, string?>? engineResolver = null) => new(folder,
            engineResolver ?? (useRga => resolver is null ? ExecutableResolver.ResolveSearchTool(useRga) : resolver()), environment ?? TestEnvironment(), loader);

    private static DesktopEnvironment TestEnvironment(DesktopPlatform? platform = null)
    {
        var current = OperatingSystem.IsWindows() ? DesktopPlatform.Windows
            : OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS : DesktopPlatform.Linux;
        return new(platform ?? current, Root, Root,
            Path.Combine(Root, "settings", Guid.NewGuid().ToString("N"), "settings.json"));
    }

    private static async Task SettingsHistoryTests()
    {
        SettingsLocationTests();
        await StartupTests();
        await PersistenceTests();
        await CorruptedSettingsTests();
        await HistoryInteractionTests();
        await SlowStartupCloseTest();
    }

    private static void SettingsLocationTests()
    {
        string home = Path.Combine(Root, "home"), roaming = Path.Combine(Root, "roaming"), xdg = Path.Combine(Root, "xdg");
        string windows = DesktopEnvironment.SettingsPath(DesktopPlatform.Windows, home, roaming, xdg);
        Check(windows == Path.Combine(roaming, "RipgrepSearch.Desktop", "settings.json") &&
            windows != Path.Combine(roaming, "RipgrepSearch", "settings.json"), "Windows Desktop profile is separate from stable WinForms");
        Check(DesktopEnvironment.SettingsPath(DesktopPlatform.MacOS, home, roaming, xdg) ==
            Path.Combine(home, "Library", "Application Support", "RipgrepSearch.Desktop", "settings.json"), "macOS Application Support convention");
        Check(DesktopEnvironment.SettingsPath(DesktopPlatform.Linux, home, roaming, xdg) ==
            Path.Combine(xdg, "RipgrepSearch.Desktop", "settings.json"), "Linux absolute XDG_CONFIG_HOME respected");
        foreach (string? value in new[] { null, "", "relative/config" })
            Check(DesktopEnvironment.SettingsPath(DesktopPlatform.Linux, home, roaming, value) ==
                Path.Combine(home, ".config", "RipgrepSearch.Desktop", "settings.json"), "Linux unset/empty/relative XDG falls back to home .config");
        Check(DesktopEnvironment.SettingsPath(DesktopPlatform.Linux, "", "", null) == "", "missing absolute config root never writes to cwd");
    }

    private static async Task StartupTests()
    {
        var env = TestEnvironment(DesktopPlatform.MacOS);
        var fresh = CreateViewModel(environment: env);
        Check(!fresh.CanSearch && !File.Exists(env.SettingsFile), "construction does no load/write and initially disables search");
        await fresh.InitializeAsync();
        Check(fresh.CanSearch && fresh.Folder == Root && fresh.Pattern == "" && !fresh.IsSearching && fresh.BatchCount == 0,
            "new-user startup uses home, empty pattern and no automatic search");
        Check(fresh.Preferences.ShowSearchOptions && !fresh.Preferences.ShowCommand && !fresh.Preferences.UseRipgrepAll &&
            fresh.Preferences.BeforeContext == 0 && fresh.Preferences.AfterContext == 0 && !fresh.Preferences.SortEnabled &&
            fresh.Preferences.SortBy == "path" && !fresh.Preferences.SortDescending, "all shared preference defaults retained");
        Check(!Directory.Exists(Path.GetDirectoryName(env.SettingsFile)), "initialization of missing settings creates no directory or file");

        string history = Path.Combine(Root, "remembered folder"), cwd = Path.Combine(Root, "launch folder"), explicitFolder = Path.Combine(cwd, "café 😀");
        Directory.CreateDirectory(history);
        Directory.CreateDirectory(explicitFolder);
        foreach (var platform in Enum.GetValues<DesktopPlatform>())
        {
            var context = TestEnvironment(platform) with { WorkingDirectory = cwd };
            AppSettingsStore.Save(context.SettingsFile, new AppSettings { FolderHistory = [history], SearchHistory = ["saved pattern"] });
            string original = File.ReadAllText(context.SettingsFile);
            var restored = CreateViewModel(environment: context);
            await restored.InitializeAsync();
            Check(restored.Folder == (platform == DesktopPlatform.Windows ? cwd : history) && restored.Pattern == "" &&
                restored.PatternHistory.SequenceEqual(new[] { "saved pattern" }), "startup cwd/history precedence and pattern dropdown on " + platform);
            Check(File.ReadAllText(context.SettingsFile) == original, "startup does not rewrite preferences on " + platform);
            var argument = CreateViewModel("\"café 😀\"", environment: context);
            await argument.InitializeAsync();
            Check(argument.Folder == explicitFolder && argument.FolderHistory[0] == explicitFolder &&
                argument.Preferences.FolderHistory.SequenceEqual(new[] { history }), "relative quoted explicit argument wins without persisting/promoting startup MRU on " + platform);
            var invalid = CreateViewModel(Path.Combine(Root, "absent argument"), environment: context);
            await invalid.InitializeAsync();
            Check(invalid.Folder == restored.Folder, "invalid explicit argument uses normal fallback on " + platform);
            var noCwd = CreateViewModel(environment: context with { WorkingDirectory = Path.Combine(Root, "missing cwd") });
            await noCwd.InitializeAsync();
            Check(noCwd.Folder == history, "history fallback when cwd is unavailable on " + platform);
            Check(context.ResolveFolder("café 😀") == explicitFolder, "relative input resolves against captured launch directory on " + platform);
        }
        Check((env with { WorkingDirectory = "/" }).InitialFolder(null, []) == Root, "Unix GUI root cwd is not a default search folder");
        Check(env.FolderInput(" spaced folder ") == " spaced folder " &&
            (env with { Platform = DesktopPlatform.Windows }).FolderInput(" folder ") == "folder", "Unix literal whitespace and Windows trim policy");
    }

    private static async Task PersistenceTests()
    {
        string profile = Path.Combine(Root, "isolation", Guid.NewGuid().ToString("N"));
        string stableFile = Path.Combine(profile, "RipgrepSearch", "settings.json");
        string desktopFile = Path.Combine(profile, "RipgrepSearch.Desktop", "settings.json");
        AppSettingsStore.Save(stableFile, new AppSettings { FolderHistory = ["stable folder"], SearchHistory = ["stable pattern"] });
        string stableBytes = File.ReadAllText(stableFile);
        var env = TestEnvironment() with { SettingsFile = desktopFile, Platform = DesktopPlatform.Linux };
        var vm = CreateViewModel(environment: env);
        await vm.InitializeAsync();
        Check(!vm.PatternHistory.Contains("stable pattern") && !vm.FolderHistory.Contains("stable folder"), "Desktop does not import stable Windows history");
        for (int i = 0; i < 25; i++)
        {
            vm.Folder = Path.Combine(Root, "folder " + i);
            vm.Pattern = "pattern " + i;
            _ = vm.SaveAsync();
        }
        await vm.CloseAsync();
        var restarted = CreateViewModel(environment: env);
        await restarted.InitializeAsync();
        Check(restarted.Folder == Path.Combine(Root, "folder 24") && restarted.Pattern == "", "folder restored but current pattern stays empty after restart");
        Check(restarted.FolderHistory.Count == 10 && restarted.PatternHistory.Count == 20 &&
            restarted.PatternHistory[0] == "pattern 24" && restarted.PatternHistory[^1] == "pattern 5", "queued saves persist bounded MRUs in latest-first order across restart");
        Check(File.ReadAllText(stableFile) == stableBytes, "Desktop saves leave stable settings byte-for-byte unchanged");
        AppSettingsStore.Save(stableFile, new AppSettings { SearchHistory = ["new stable value"] });
        Check(AppSettingsStore.Load(desktopFile).SearchHistory[0] == "pattern 24", "stable app writes do not affect separate Desktop profile");

        foreach (var platform in new[] { DesktopPlatform.Windows, DesktopPlatform.MacOS })
        {
            var context = TestEnvironment(platform);
            var history = CreateViewModel(environment: context);
            await history.InitializeAsync();
            history.Folder = Path.Combine(Root, "Case Folder"); history.Pattern = "Needle";
            _ = history.SaveAsync();
            history.Folder = Path.Combine(Root, "case folder"); history.Pattern = "needle";
            await history.SaveAsync();
            var data = AppSettingsStore.Load(context.SettingsFile);
            Check(data.FolderHistory.Count == (platform == DesktopPlatform.Windows ? 1 : 2) &&
                data.SearchHistory.SequenceEqual(new[] { "needle", "Needle" }), "platform folder duplicates and exact-case patterns persist on " + platform);
        }

        var preferences = new AppSettings { ShowSearchOptions = false, ShowCommand = true, BeforeContext = 9, AfterContext = 4,
            UseRipgrepAll = true, SortEnabled = true, SortBy = "modified", SortDescending = true };
        var optionsEnv = TestEnvironment();
        AppSettingsStore.Save(optionsEnv.SettingsFile, preferences);
        string expected = JsonSerializer.Serialize(preferences);
        var preserve = CreateViewModel(environment: optionsEnv);
        await preserve.InitializeAsync();
        await preserve.CloseAsync();
        var options = AppSettingsStore.Load(optionsEnv.SettingsFile);
        options.FolderHistory.Clear();
        Check(JsonSerializer.Serialize(options) == expected, "future option values survive load/save without being reset or activated by Phase 5.2");
        Check(File.ReadAllText(optionsEnv.SettingsFile).Contains("\n  \"FolderHistory\""), "existing indented JSON format reused");
    }

    private static async Task CorruptedSettingsTests()
    {
        foreach (string contents in new[] { "{", "null", "[]", "{\"FolderHistory\":42}",
            "{\"FolderHistory\":null,\"SearchHistory\":[null,\"\",\"ok\",\"ok\"],\"BeforeContext\":-1,\"AfterContext\":999,\"SortBy\":null}" })
        {
            var env = TestEnvironment();
            Directory.CreateDirectory(Path.GetDirectoryName(env.SettingsFile)!);
            File.WriteAllText(env.SettingsFile, contents);
            var vm = CreateViewModel(environment: env);
            await vm.InitializeAsync();
            Check(vm.CanSearch && vm.Pattern == "" && vm.Preferences.FolderHistory is not null &&
                vm.Preferences.SearchHistory.All(value => !string.IsNullOrWhiteSpace(value)), "corrupted/null settings recover without blocking startup");
            Check(File.ReadAllText(env.SettingsFile) == contents, "loading corrupt settings never rewrites input");
            if (contents.Contains("999")) Check(vm.Preferences.BeforeContext == 0 && vm.Preferences.AfterContext == 100 &&
                vm.Preferences.SortBy == "path" && vm.PatternHistory.SequenceEqual(new[] { "ok" }), "restoration clamps numeric options and sanitizes null/duplicate histories");
        }
        var inaccessible = TestEnvironment() with { SettingsFile = Root }; // Directory, not a writable JSON file.
        var fallback = CreateViewModel(environment: inaccessible);
        await fallback.InitializeAsync();
        fallback.Pattern = "nonfatal save";
        await fallback.CloseAsync();
        Check(Directory.Exists(Root), "unreadable/unwritable settings target does not crash load or close");
    }

    private static async Task HistoryInteractionTests()
    {
        var env = TestEnvironment(DesktopPlatform.MacOS);
        AppSettingsStore.Save(env.SettingsFile, new AppSettings { FolderHistory = [Root, Path.GetDirectoryName(Root)!],
            SearchHistory = ["needle", "(?i)café 😀"] });
        var vm = CreateViewModel(environment: env);
        var window = new MainWindow(vm);
        window.Show();
        await vm.InitializeAsync();
        Render(window);
        var pattern = window.FindControl<ComboBox>("PatternInput")!;
        var folder = window.FindControl<ComboBox>("FolderInput")!;
        Check(pattern.IsEditable && folder.IsEditable && pattern.Items.Count == 2 && pattern.Text == "", "actual view binds editable histories without selecting a startup pattern");
        pattern.Focus();
        window.KeyTextInput("my literal regex.*");
        Check(vm.Pattern == "my literal regex.*", "typing in editable history does not autocomplete or change regex text");
        ChooseHistoryWithMouse(window, pattern, 1);
        Check(vm.Pattern == "(?i)café 😀" && !vm.IsSearching && vm.BatchCount == 0, "mouse selection loads history text without starting a search");
        ChooseHistoryWithMouse(window, folder, 1);
        Check(vm.Folder == Path.GetDirectoryName(Root) && !vm.IsSearching, "mouse folder selection updates input without searching");
        pattern.Focus();
        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        Render(window);
        Check(pattern.IsDropDownOpen, "Alt+Down opens editable history dropdown");
        var popup = TopLevel.GetTopLevel(pattern.ContainerFromIndex(1)!)!;
        popup.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        popup.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        popup.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        popup.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Check(vm.Pattern == "needle" && !pattern.IsDropDownOpen && vm.BatchCount == 0 && !vm.IsSearching,
            $"keyboard history navigation/selection: text={vm.Pattern}, open={pattern.IsDropDownOpen}, index={pattern.SelectedIndex}, busy={vm.IsSearching}, batches={vm.BatchCount}");
        pattern.Focus();
        window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
        Check(pattern.IsDropDownOpen, "F4 opens history from the editable text input");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Check(!pattern.IsDropDownOpen && !vm.IsSearching, "Escape dismisses history without starting a search or closing the window");

        string relativeFolder = Path.Combine(Root, "history relative café");
        Directory.CreateDirectory(relativeFolder);
        File.WriteAllText(Path.Combine(relativeFolder, "file.txt"), "needle needle\n");
        vm.Folder = "history relative café";
        vm.Pattern = "needle";
        await vm.SearchAsync();
        Check(vm.MatchingLines == 1 && vm.FolderHistory[0] == "history relative café" && folder.Text == vm.Folder,
            "relative search executes against captured cwd and retains user spelling in MRU");
        string[] recorded = vm.PatternHistory.ToArray();
        vm.Folder = Path.Combine(Root, "nonexistent folder");
        vm.Pattern = "not admitted";
        await vm.SearchAsync();
        Check(vm.PatternHistory.SequenceEqual(recorded) && !vm.Preferences.SearchHistory.Contains("not admitted"),
            "failed input validation does not enter search-start history");
        var missingEnv = TestEnvironment();
        var missing = CreateViewModel(Root, () => null, missingEnv);
        await missing.InitializeAsync();
        missing.Pattern = "tool not found";
        await missing.SearchAsync();
        Check(missing.Preferences.SearchHistory.Count == 0 && !File.Exists(missingEnv.SettingsFile),
            "missing executable does not save or admit a search history entry");
        vm.Folder = Root;
        vm.Pattern = "["; // Validation passes, then rg reports an invalid regex.
        await vm.SearchAsync();
        Check(vm.PatternHistory[0] == "[" && pattern.Text == "[" && vm.Folder == Root && folder.Text == Root,
            "search-start history includes later failures and repopulation preserves typed values");
        vm.Pattern = "typed on close";
        window.Close();
        await WaitUntil(() => !window.IsVisible);
        Check(AppSettingsStore.Load(env.SettingsFile).SearchHistory[0] == "typed on close", "normal window close saves unsearched current text");
    }

    private static void ChooseHistoryWithMouse(Window window, ComboBox combo, int index)
    {
        combo.IsDropDownOpen = true;
        Render(window);
        var item = combo.ContainerFromIndex(index)!;
        var top = TopLevel.GetTopLevel(item)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
        top.MouseDown(point, MouseButton.Left);
        top.MouseUp(point, MouseButton.Left);
        Render(window);
    }

    private static async Task SlowStartupCloseTest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int uiThread = Environment.CurrentManagedThreadId, loaderThread = uiThread;
        var env = TestEnvironment();
        var vm = CreateViewModel(environment: env, loader: _ =>
        {
            loaderThread = Environment.CurrentManagedThreadId;
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Settings test loader was not released.");
            return new AppSettings();
        });
        var window = new MainWindow(vm);
        window.Show();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool dispatched = false;
        await Dispatcher.UIThread.InvokeAsync(() => dispatched = true);
        Check(dispatched && loaderThread != uiThread && !vm.CanSearch && window.IsVisible, "slow settings I/O runs off UI thread while window remains responsive");
        window.Close();
        Check(window.IsVisible, "close during startup waits for settings initialization");
        release.Set();
        await WaitUntil(() => !window.IsVisible);
        Check(File.Exists(env.SettingsFile) && !vm.CanSearch, "close awaits final settings save and cannot re-enable Search");
    }
}
