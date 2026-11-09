using System.Reflection;
using System.Text.Json;
using RipgrepSearch;

internal static partial class Tests
{
    private sealed record SpanSnapshot(string Text, string Color, string Style, string? FilePath, bool IsFileHeading);

    private static void PresentationTests()
    {
        string path = Path.GetFullPath("first [#] café.txt");
        var lines = new[]
        {
            new SearchResultLine("first [#] café.txt", path, 1, "café 😀 needle needle <>& `", true, [new(8, 6), new(15, 6)]),
            new SearchResultLine("first [#] café.txt", path, 2, "context\t{} " + '\\', false, []),
            new SearchResultLine("first [#] café.txt", path, 5, "a\0b", false, []),
            new SearchResultLine("first [#] café.txt", path, 6, "abc", true, [new(2, 0)]),
            new SearchResultLine("unopenable.txt", null, 1, "```", true, [new(0, 3)])
        };
        var report = new SearchReport("folder <&>", "needle|😀", "rg --json \"needle|😀\" folder",
            DateTimeOffset.Parse("2026-01-02T03:04:05+02:00"))
        {
            Status = "Search cancelled. 3 matching line(s) shown; results are incomplete."
        };
        var formatter = new SearchResultFormatter();
        report.Results.AddRange(lines);
        foreach (var line in lines)
            report.Spans.AddRange(formatter.Format(line));

        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var expectedSpans = JsonSerializer.Deserialize<SpanSnapshot[]>(File.ReadAllText(Path.Combine(fixtures, "v0.10.0-spans.json")))!;
        var actualSpans = report.Spans.Select(span => new SpanSnapshot(span.Text, span.Color.ToString(),
            span.Style.ToString(), span.FilePath is null ? null : "<FIRST_PATH>", span.IsFileHeading)).ToArray();
        Check(actualSpans.Length == expectedSpans.Length, "formatted span count matches captured v0.10.0 output");
        for (int i = 0; i < expectedSpans.Length; i++)
            Check(actualSpans[i] == expectedSpans[i], "text/style/grouping/navigation span matches v0.10.0: " + i);
        string uri = new Uri(path).AbsoluteUri;
        foreach (var (extension, actual) in new[] { ("html", ResultsExporter.ToHtml(report)), ("md", ResultsExporter.ToMarkdown(report)) })
        {
            string expected = File.ReadAllText(Path.Combine(fixtures, "v0.10.0-results." + extension));
            Check(actual.Replace(uri, "file:///fixtures/first.txt").Replace("\r\n", "\n") == expected.Replace("\r\n", "\n"),
                "full " + extension + " export matches v0.10.0 (only native newlines and absolute fixture URI normalized)");
        }
        foreach (var (color, rgb) in new[]
        {
            (ResultColor.White, (255, 255, 255)), (ResultColor.DeepSkyBlue, (0, 191, 255)),
            (ResultColor.LimeGreen, (50, 205, 50)), (ResultColor.DarkGray, (169, 169, 169)),
            (ResultColor.OrangeRed, (255, 69, 0)), (ResultColor.Gray, (128, 128, 128))
        })
        {
            var actual = ResultPalette.GetRgb(color);
            Check(((int)actual.R, (int)actual.G, (int)actual.B) == rgb, "legacy RGB palette retained: " + color);
        }
        var adjacent = new SearchResultFormatter().Format(new SearchResultLine("adjacent", null, 1, "abcd", true, [new(0, 2), new(2, 2)]));
        Check(adjacent.Count(span => span.Color == ResultColor.OrangeRed) == 1 &&
            adjacent.Single(span => span.Color == ResultColor.OrangeRed).Text == "abcd", "adjacent highlights stay coalesced");
        Check(report.Results[2].Text.Contains('\0') && !report.Spans.Any(span => span.Text.Contains('\0')), "presentation retains raw results while substituting visible NUL markers");
        Check(new[] { typeof(SearchReport), typeof(AppSettings), typeof(DependencyChecker), typeof(ResultsExporter), typeof(ExecutableResolver) }
            .All(type => type.Assembly == typeof(SearchRunner).Assembly), "extracted types live in Core");
        Check(!typeof(SearchRunner).Assembly.GetReferencedAssemblies().Any(name =>
            name.Name?.StartsWith("System.Drawing", StringComparison.Ordinal) == true || name.Name == "Microsoft.Win32.Registry"),
            "Core does not depend on drawing or Windows registry assemblies");
    }

    private static string NewFixture(string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shared-fixtures", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void SettingsTests()
    {
        string directory = NewFixture("settings");
        string path = Path.Combine(directory, "nested", "settings.json");
        var defaults = AppSettingsStore.Load(path);
        Check(defaults.ShowSearchOptions && !defaults.ShowCommand && !defaults.UseRipgrepAll && !defaults.SortEnabled &&
            !defaults.SortDescending && defaults.SortBy == "path" && defaults.BeforeContext == 0 && defaults.AfterContext == 0 &&
            defaults.FolderHistory.Count == 0 && defaults.SearchHistory.Count == 0, "all v0.10.0 defaults retained");
        Check(!Directory.Exists(Path.GetDirectoryName(path)), "loading missing settings never creates files or directories");
        var settings = new AppSettings
        {
            FolderHistory = ["folder café", "folder 😀"], SearchHistory = ["needle", "(?i)word"],
            ShowSearchOptions = false, ShowCommand = true, BeforeContext = 2, AfterContext = 3,
            UseRipgrepAll = true, SortEnabled = true, SortBy = "modified", SortDescending = true
        };
        AppSettingsStore.Save(path, settings);
        string json = File.ReadAllText(path);
        Check(json == JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), "saved JSON content and indentation retain v0.10.0 serialization");
        using (var document = JsonDocument.Parse(json))
            Check(document.RootElement.EnumerateObject().Select(property => property.Name).SequenceEqual(new[]
            {
                "FolderHistory", "SearchHistory", "ShowCommand", "ShowSearchOptions", "BeforeContext", "AfterContext",
                "UseRipgrepAll", "SortEnabled", "SortBy", "SortDescending"
            }), "JSON property names and order remain compatible");
        var restored = AppSettingsStore.Load(path);
        Check(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(settings), "all preferences and Unicode histories round-trip");
        Check(File.ReadAllText(path) == json, "loading an existing file never rewrites or migrates it");
        foreach (string invalid in new[] { "{", "null", "[]" })
        {
            File.WriteAllText(path, invalid);
            Check(AppSettingsStore.Load(path).ShowSearchOptions && File.ReadAllText(path) == invalid, "invalid/null settings fall back without overwriting input");
        }
        File.WriteAllText(path, "{\"ShowCommand\":true,\"FutureSetting\":123}");
        Check(AppSettingsStore.Load(path).ShowCommand && AppSettingsStore.Load(path).ShowSearchOptions, "missing properties use defaults and unknown properties are tolerated");
        Check(AppSettingsStore.Load(directory).SortBy == "path", "unreadable settings path uses defaults");
        AppSettingsStore.Save(directory, settings);
        Check(Directory.Exists(directory), "settings write failure remains nonfatal");
        var firstDefaults = AppSettingsStore.Load(Path.Combine(directory, "missing1"));
        firstDefaults.FolderHistory.Add("changed");
        Check(AppSettingsStore.Load(Path.Combine(directory, "missing2")).FolderHistory.Count == 0, "fallback settings do not share mutable histories");
    }

    private static void DiagnosticsTests()
    {
        const string profile = @"C:\Users\Test User";
        var results = new[]
        {
            new DependencyCheckResult(DependencyChecker.Dependencies[0], DependencyStatus.Error, null,
                profile + @"\tools\rg.exe", "Failed at \"" + profile + "\\tools\\rg.exe\"."),
            new DependencyCheckResult(DependencyChecker.Dependencies[1], DependencyStatus.NotFound, null, null, "rga missing")
        };
        string nl = Environment.NewLine;
        string expected = string.Join(nl, new[]
        {
            "Ripgrep Search — Dependency Diagnostics", "Application version: 0.10.0", "Windows version: Windows fixture", "",
            "ripgrep (rg)", "Status: Error", "Version: Not available", @"Executable path: %USERPROFILE%\tools\rg.exe",
            "Diagnostics:", "Failed at \"%USERPROFILE%\\tools\\rg.exe\".", "", "ripgrep-all (rga)", "Status: Not found",
            "Version: Not available", "Executable path: Not available", "Diagnostics:", "rga missing", ""
        });
        Check(DependencyDiagnostics.CreateReport(results, "0.10.0", "Windows fixture", profile, "Windows version") == expected,
            "complete diagnostic report matches the v0.10.0 layout and redaction");
        Check(DependencyDiagnostics.RedactUserProfile(@"c:\USERS\TEST USER\rg.exe", profile + "\\") == @"%USERPROFILE%\rg.exe", "Windows redaction preserves case-insensitive behavior");
        Check(DependencyDiagnostics.RedactUserProfile(@"C:\Users\Test User2\rg.exe", profile) == @"C:\Users\Test User2\rg.exe", "similar profile names are not redacted");
        Check(DependencyDiagnostics.RedactUserProfile(@"Failed at C:\\Users\\Test User\\rg.exe", profile) == @"Failed at %USERPROFILE%\\rg.exe", "escaped paths redact without changing separator spelling");
        Check(DependencyDiagnostics.RedactUserProfile("/Users/Zoë [test]/tools/rg", "/Users/Zoë [test]", "~", false) == "~/tools/rg", "hosts can supply Unix home redaction policy");
        Check(DependencyDiagnostics.RedactUserProfile("/Users/Other/rg", "/Users/other", "~", false) == "/Users/Other/rg", "case-sensitive redaction can be requested");
        var installed = results.Select(result => result with { Status = DependencyStatus.Installed, Version = "1.2.3", Details = "success detail" }).ToArray();
        string report = DependencyDiagnostics.CreateReport(installed, "0.10.0", "OS fixture", null);
        Check(report.Contains("OS version: OS fixture") && !report.Contains("success detail"), "portable OS label and redundant-success omission");
        Check(results[0].ExecutablePath!.StartsWith(profile), "diagnostics never mutate checked results");
    }

    private static string CreateProbeTools(string mode, bool rg = true, bool rga = true)
    {
        string directory = NewFixture(mode);
        string assemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
        foreach (string file in new[] { assemblyName + ".dll", assemblyName + ".deps.json", assemblyName + ".runtimeconfig.json", "RipgrepSearch.Core.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(directory, file));
        foreach (string name in (rg ? new[] { "rg" } : Array.Empty<string>()).Concat(rga ? new[] { "rga" } : []))
        {
            string extension = OperatingSystem.IsWindows() ? ".exe" : "";
            string source = Path.Combine(AppContext.BaseDirectory, assemblyName + extension);
            string target = Path.Combine(directory, name + extension);
            File.Copy(source, target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        }
        File.WriteAllText(Path.Combine(directory, "version-mode.txt"), mode);
        return directory;
    }

    private static async Task DependencyProbeTests()
    {
        string tools = CreateProbeTools("valid");
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        string executable = Path.Combine(tools, "rg" + extension);
        var results = await DependencyChecker.CheckAllAsync(tools, TimeSpan.FromSeconds(5));
        Check(results.All(result => result.Status == DependencyStatus.Installed && result.Version == "1.2.3"), "both portable dependency probes execute and recognize versions");
        Check(results[0].ExecutablePath == executable && ExecutableResolver.ResolveSearchTool(false, tools) == executable, "checker and searches resolve the same executable");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tools);
            results = await DependencyChecker.CheckAllAsync();
            Check(results.All(result => result.Status == DependencyStatus.Installed) && Environment.GetEnvironmentVariable("PATH") == tools,
                "Core's default probe uses the process PATH without reading or changing Windows settings");
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        string onlyRg = CreateProbeTools("valid", rga: false);
        results = await DependencyChecker.CheckAllAsync(onlyRg, TimeSpan.FromSeconds(5));
        Check(results[0].Status == DependencyStatus.Installed && results[1].Status == DependencyStatus.NotFound, "optional rga may be absent");
        Check((await DependencyChecker.CheckAllAsync("", TimeSpan.FromSeconds(5))).All(result => result.Status == DependencyStatus.NotFound), "empty PATH returns missing tools");
        Check(ExecutableResolver.ResolveSearchTool(false, "bad\0path" + Path.PathSeparator + tools) == executable, "malformed PATH entries are ignored");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, tools);
        Check(ExecutableResolver.ResolveSearchTool(false, relative) == executable, "relative PATH resolves absolute executable path");
        string earlier = NewFixture("earlier & spaces");
        File.WriteAllText(Path.Combine(earlier, "rg"), "unusable extensionless candidate");
        if (OperatingSystem.IsWindows())
            Check(ExecutableResolver.ResolveSearchTool(false, earlier + Path.PathSeparator + tools) == executable, "Windows retains .exe-first precedence over earlier extensionless candidate");
        else
            Check(ExecutableResolver.ResolveSearchTool(false, earlier + Path.PathSeparator + tools) == Path.Combine(earlier, "rg"), "Unix retains first extensionless PATH candidate for execution diagnostics");
        File.WriteAllText(Path.Combine(earlier, "rg" + extension), "not an executable");
        results = await DependencyChecker.CheckAllAsync(earlier + Path.PathSeparator + tools, TimeSpan.FromSeconds(5));
        Check(results[0].Status == DependencyStatus.Error && results[0].ExecutablePath == Path.Combine(earlier, "rg" + extension), "unusable first candidate reports Error instead of silently falling back");
        foreach (string mode in new[] { "exit-error", "invalid", "timeout" })
        {
            results = await DependencyChecker.CheckAllAsync(CreateProbeTools(mode), mode == "timeout" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5));
            Check(results.All(result => result.Status == DependencyStatus.Error && result.Version is null && result.ExecutablePath is not null), "probe failures preserve paths: " + mode);
            if (mode == "exit-error") Check(results[0].Details.Contains("7") && results[0].Details.Contains("simulated error"), "probe exit code and diagnostics retained");
            if (mode == "timeout") Check(results[0].Details.Contains("timed out"), "probe timeout diagnostics retained");
        }
        int refreshes = 0;
        string activePath = "";
        Task<DependencyCheckResult[]> Refresh() => DependencyChecker.CheckAllWithPathProviderAsync(() => { refreshes++; return activePath; }, TimeSpan.FromSeconds(5));
        Check((await Refresh()).All(result => result.Status == DependencyStatus.NotFound), "host PATH policy used for each refresh");
        activePath = tools;
        Check((await Refresh()).All(result => result.Status == DependencyStatus.Installed) && refreshes == 2, "refresh sees updated host-supplied PATH without cached resolution");
        bool policyError = false;
        try { await DependencyChecker.CheckAllWithPathProviderAsync(() => throw new IOException("policy failure"), TimeSpan.FromSeconds(1)); }
        catch (IOException) { policyError = true; }
        Check(policyError, "host policy failure propagates to dialog error handling");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool observed = false;
        try { await DependencyChecker.CheckAllAsync(tools, TimeSpan.FromSeconds(5), cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        Check(observed, "dependency probes preserve caller cancellation");
    }

    private static async Task<int> FakeVersion()
    {
        string mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "version-mode.txt"));
        if (mode == "timeout") await Task.Delay(Timeout.Infinite);
        if (mode == "invalid") { Console.WriteLine("unknown output"); return 0; }
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "rga" ? "ripgrep-all" : "ripgrep";
        Console.WriteLine(name + " 1.2.3");
        if (mode == "exit-error") { Console.Error.WriteLine("simulated error"); return 7; }
        return 0;
    }
}
