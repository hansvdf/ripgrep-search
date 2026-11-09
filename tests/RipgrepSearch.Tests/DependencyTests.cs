using System.Diagnostics;
using System.Reflection;
using System.Collections;
using RipgrepSearch;

internal static class DependencyTests
{
    private static int passed;
    private static string fixtures = string.Empty;

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception("FAIL: " + message);
        passed++;
    }

    public static async Task<int> RunAsync()
    {
        passed = 0;
        fixtures = Path.Combine(AppContext.BaseDirectory, "dependency-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string both = CreateTools("both tools & spaces", true, true);
        string onlyRg = CreateTools("only rg", true, false);
        string neither = Path.Combine(fixtures, "neither");
        Directory.CreateDirectory(neither);

        var results = await DependencyChecker.CheckAllAsync(both, TimeSpan.FromSeconds(5));
        Check(results.Length == 2 && results.All(r => r.Status == DependencyStatus.Installed), "both executable dependencies verified");
        Check(results[0].Version == "99.1.2-test+fixture" && results[1].Version == "98.2.1", "versions come from executable output");
        foreach (DependencyCheckResult result in results)
        {
            Check(result.ExecutablePath == ExecutableResolver.ResolveSearchTool(result.Dependency.UseRipgrepAll, both), "checker uses same path resolver as searches");
            Check(Path.IsPathFullyQualified(result.ExecutablePath!), "dependency path is fully resolved");
        }
        results = await DependencyChecker.CheckAllAsync(onlyRg, TimeSpan.FromSeconds(5));
        Check(results[0].Status == DependencyStatus.Installed && results[1].Status == DependencyStatus.NotFound, "only rg installed");
        Check(results[1].ExecutablePath is null && results[1].Version is null && results[1].Details.Contains("Install"), "missing rga has installation explanation and no stale values");
        results = await DependencyChecker.CheckAllAsync(neither, TimeSpan.FromSeconds(5));
        Check(results.All(r => r.Status == DependencyStatus.NotFound), "neither dependency installed");
        results = await DependencyChecker.CheckAllAsync("", TimeSpan.FromSeconds(5));
        Check(results.All(r => r.Status == DependencyStatus.NotFound), "empty PATH handled");

        string corrupt = Path.Combine(fixtures, "corrupt");
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, "rg.exe"), "This file is not executable.");
        results = await DependencyChecker.CheckAllAsync(corrupt, TimeSpan.FromSeconds(5));
        Check(results[0].Status == DependencyStatus.Error && results[0].ExecutablePath == Path.Combine(corrupt, "rg.exe"), "present but unexecutable is Error, with its path retained");
        results = await DependencyChecker.CheckAllAsync(corrupt + Path.PathSeparator + both, TimeSpan.FromSeconds(5));
        Check(results[0].Status == DependencyStatus.Error && results[0].ExecutablePath!.StartsWith(corrupt), "invalid first executable is not silently replaced by a later PATH candidate");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, both);
        Check(ExecutableResolver.ResolveSearchTool(false, relative) == Path.Combine(both, "rg.exe"), "relative PATH entry resolves to absolute executable path");
        string fallback = Path.Combine(fixtures, "fallback");
        Directory.CreateDirectory(fallback);
        await File.WriteAllTextAsync(Path.Combine(fallback, "rg"), "extensionless fallback");
        Check(ExecutableResolver.ResolveSearchTool(false, fallback) == Path.Combine(fallback, "rg"), "existing extensionless resolution fallback preserved");
        Check(ExecutableResolver.ResolveSearchTool(false, fallback + Path.PathSeparator + both) == Path.Combine(both, "rg.exe"), "existing exe-first resolution priority preserved");
        Check(ExecutableResolver.ResolveSearchTool(false, "invalid\0entry" + Path.PathSeparator + both) == Path.Combine(both, "rg.exe"), "malformed PATH entry ignored");

        foreach (string mode in new[] { "exit-error", "invalid", "empty", "wrong-tool", "flood" })
        {
            string directory = CreateTools(mode, true, false, mode);
            results = await DependencyChecker.CheckAllAsync(directory, TimeSpan.FromSeconds(5));
            Check(results[0].Status == DependencyStatus.Error && results[0].Version is null, "version command error: " + mode);
            Check(results[0].ExecutablePath == Path.Combine(directory, "rg.exe"), "error path retained: " + mode);
            if (mode == "exit-error")
                Check(results[0].Details.Contains("7") && results[0].Details.Contains("simulated"), "nonzero exit preserves diagnostics even with version output");
            if (mode == "flood")
                Check(results[0].Details.Length < 2200, "large stdout and stderr drained with bounded diagnostics");
        }
        string stderr = CreateTools("stderr", true, true, "stderr");
        results = await DependencyChecker.CheckAllAsync(stderr, TimeSpan.FromSeconds(5));
        Check(results.All(r => r.Status == DependencyStatus.Installed && r.Version is not null), "recognizable successful version on stderr accepted");
        string timeout = CreateTools("timeout", true, false, "timeout");
        var elapsed = Stopwatch.StartNew();
        results = await DependencyChecker.CheckAllAsync(timeout, TimeSpan.FromMilliseconds(500));
        Check(results[0].Status == DependencyStatus.Error && results[0].Details.Contains("timed out"), "version command timeout reported as Error");
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "version timeout terminates promptly");
        Check(ExitedVersionProcess(timeout), "timed-out version process terminated");
        using (var cancellation = new CancellationTokenSource(300))
        {
            bool cancelled = false;
            try { await DependencyChecker.CheckAllAsync(timeout, TimeSpan.FromSeconds(10), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "dependency checks support caller cancellation");
        }
        Check(ExitedVersionProcess(timeout), "cancelled version process terminated");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { await DependencyChecker.CheckAllAsync(both, TimeSpan.FromSeconds(5), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "pre-cancelled check handled");
        }

        await PathRefreshTests();

        // Validate real installed tools as well as the isolated dependency scenarios.
        results = await WindowsDependencies.CheckAllAsync();
        foreach (DependencyCheckResult result in results)
        {
            Check(result.ExecutablePath == ExecutableResolver.ResolveSearchTool(result.Dependency.UseRipgrepAll), "real checker reports actual search executable");
            Check(result.Status == DependencyStatus.Installed && !string.IsNullOrWhiteSpace(result.Version), "real rg/rga version probe: " + result.Details);
        }
        return passed;
    }

    private static async Task PathRefreshTests()
    {
        var process = new Hashtable
        {
            ["Path"] = @"C:\stale-tools", ["SystemRoot"] = @"C:\Windows",
            ["USERPROFILE"] = fixtures, ["USER_TOOLS"] = @"C:\stale-user-tools"
        };
        var machine = new Hashtable
        {
            ["pAtH"] = @"%SystemRoot%\System32;%TOOLS%",
            ["TOOLS"] = @"%TOOLS_ROOT%\bin", ["TOOLS_ROOT"] = @"C:\fresh-system-tools"
        };
        var user = new Hashtable { ["PATH"] = @"%userprofile%\tools;%USER_TOOLS%", ["USER_TOOLS"] = @"C:\fresh-user-tools" };
        string rebuilt = WindowsPathEnvironment.BuildPath(machine, user, process);
        Check(rebuilt == @"C:\Windows\System32;C:\fresh-system-tools\bin;" + fixtures + @"\tools;C:\fresh-user-tools", "fresh machine/user variables expanded in Windows PATH order");
        Check(!rebuilt.Contains("stale"), "inherited PATH and old variable values are not resurrected");
        Check(WindowsPathEnvironment.BuildPath(new Hashtable(), new Hashtable(), process) == "", "missing saved PATH does not retain inherited entries");
        var cycle = new Hashtable { ["PATH"] = "%LOOP_A%", ["LOOP_A"] = "%LOOP_B%", ["LOOP_B"] = "%LOOP_A%" };
        Check(WindowsPathEnvironment.BuildPath(cycle, new Hashtable(), process).Contains("%LOOP_A%"), "cyclic environment references do not loop forever");
        Check(WindowsPathEnvironment.BuildPath(new Hashtable(), new Hashtable { ["PATH"] = "%UNDEFINED_VARIABLE%" }, new Hashtable()) == "%UNDEFINED_VARIABLE%", "unknown references remain unresolved");
        Check(!WindowsPathEnvironment.BuildPath(new Hashtable { ["PATH"] = "fresh-system" }, new Hashtable { ["PATH"] = "%PATH%;fresh-user" }, process).Contains("stale"), "PATH self reference uses fresh machine path");
        Check(WindowsPathEnvironment.BuildPath(new Hashtable(), new Hashtable { ["PATH"] = "%USER_TOOLS%" }, process) == "%USER_TOOLS%",
            "deleted saved user variable cannot fall back to its inherited value");
        Check(WindowsPathEnvironment.BuildPath(new Hashtable { ["PATH"] = "%USER_TOOLS%" }, new Hashtable(), process) == "%USER_TOOLS%",
            "deleted saved system variable cannot fall back to its inherited value");
        var nestedUser = new Hashtable { ["PATH"] = "%PARENT_TOOLS%", ["PARENT_TOOLS"] = "%USER_TOOLS%\\bin" };
        Check(WindowsPathEnvironment.BuildPath(new Hashtable(), nestedUser, process) == @"%USER_TOOLS%\bin",
            "nested references do not restore deleted variables");
        Check(WindowsPathEnvironment.BuildPath(new Hashtable { ["PATH"] = "%SystemRoot%\\System32" },
            new Hashtable { ["PATH"] = "%userprofile%\\tools" }, process) == @"C:\Windows\System32;" + fixtures + @"\tools",
            "Windows-generated variables remain available without saved registry entries");
        Check(WindowsPathEnvironment.BuildPath(new Hashtable(), new Hashtable { ["PATH"] = "%USERPROFILE%\\tools", ["USERPROFILE"] = @"C:\saved-profile" }, process) ==
            @"C:\saved-profile\tools", "current saved values take precedence over inherited Windows defaults");

        string refreshedTools = CreateTools("refreshed tools", true, true, "record-path");
        string neither = Path.Combine(fixtures, "neither");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        const string toolVariable = "RIPGREP_SEARCH_TEST_TOOLS";
        string? originalToolVariable = Environment.GetEnvironmentVariable(toolVariable);
        try
        {
            var machineValues = new Hashtable { ["PATH"] = neither };
            var userValues = new Hashtable();
            Task<DependencyCheckResult[]> Refresh() => DependencyChecker.CheckAllWithPathProviderAsync(
                () => WindowsPathEnvironment.RefreshProcessPath(machineValues, userValues, Environment.GetEnvironmentVariables()),
                TimeSpan.FromSeconds(5));

            Environment.SetEnvironmentVariable("PATH", refreshedTools, EnvironmentVariableTarget.Process);
            var result = await Refresh();
            Check(result.All(item => item.Status == DependencyStatus.NotFound), "refresh replaces stale process-only PATH entries");
            userValues["PATH"] = "%UPDATED_TOOLS%";
            userValues["UPDATED_TOOLS"] = refreshedTools;
            result = await Refresh();
            Check(result.All(item => item.Status == DependencyStatus.Installed), "newly saved user PATH detected without restarting: " + string.Join("; ", result.Select(item => item.Dependency.ExecutableName + ": " + item.Details)));
            Check(ExecutableResolver.ResolveSearchTool(false) == Path.Combine(refreshedTools, "rg.exe") &&
                ExecutableResolver.ResolveSearchTool(true) == Path.Combine(refreshedTools, "rga.exe"), "subsequent searches use the newly refreshed path");
            Check(new[] { "ripgrep", "ripgrep-all" }.All(name => File.ReadAllText(Path.Combine(refreshedTools, name + "-inherited-path.txt")) == Environment.GetEnvironmentVariable("PATH")), "version child processes inherit refreshed PATH");

            userValues["PATH"] = Path.Combine(fixtures, "only rg") + Path.PathSeparator + refreshedTools;
            result = await Refresh();
            Check(result[0].ExecutablePath == Path.Combine(fixtures, "only rg", "rg.exe"), "refreshed PATH ordering changes selected executable");
            userValues["PATH"] = refreshedTools + Path.PathSeparator + Path.Combine(fixtures, "only rg");
            result = await Refresh();
            Check(result[0].ExecutablePath == Path.Combine(refreshedTools, "rg.exe"), "reordering PATH is picked up on next refresh");
            userValues.Clear();
            result = await Refresh();
            Check(result.All(item => item.Status == DependencyStatus.NotFound) && ExecutableResolver.ResolveSearchTool(false) is null, "removed Windows PATH entries are no longer used");

            // Simulate a variable inherited at launch, then deleted from Windows settings.
            // Only this test process is changed; no user/machine settings are written.
            Environment.SetEnvironmentVariable(toolVariable, refreshedTools, EnvironmentVariableTarget.Process);
            foreach (var savedValues in new[] { userValues, machineValues })
            {
                savedValues["PATH"] = "%" + toolVariable + "%";
                savedValues[toolVariable] = refreshedTools;
                result = await Refresh();
                Check(result.All(item => item.Status == DependencyStatus.Installed), "saved tool variable is used before deletion");
                savedValues.Remove(toolVariable);
                result = await Refresh();
                Check(result.All(item => item.Status == DependencyStatus.NotFound), "deleting a saved tool variable removes both dependencies on refresh");
                Check(ExecutableResolver.ResolveSearchTool(false) is null && ExecutableResolver.ResolveSearchTool(true) is null &&
                    !Environment.GetEnvironmentVariable("PATH")!.Contains(refreshedTools), "subsequent searches cannot use the deleted variable's directory");
                Check(Environment.GetEnvironmentVariable(toolVariable) == refreshedTools,
                    "refresh ignores the stale inherited variable without modifying unrelated process variables");
                savedValues.Clear();
            }
            string? previousPath = Environment.GetEnvironmentVariable("PATH");
            bool rejected = false;
            try { WindowsPathEnvironment.RefreshProcessPath(new Hashtable { ["PATH"] = new string('x', 32_767) }, new Hashtable(), process); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && Environment.GetEnvironmentVariable("PATH") == previousPath, "failed PATH rebuild keeps prior process PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(toolVariable, originalToolVariable, EnvironmentVariableTarget.Process);
        }
    }

    public static int RunUiTests()
    {
        int startCount = passed;
        string path = Path.Combine(fixtures, "both tools & spaces");
        int calls = 0;
        using var dialog = new DependencyDialog(async token =>
        {
            calls++;
            await Task.Delay(50, token);
            return await DependencyChecker.CheckAllAsync(path, TimeSpan.FromSeconds(5), token);
        });
        _ = dialog.Handle;
        var grid = Field<DataGridView>(dialog, "dependencyGrid");
        _ = grid.Handle;
        var refresh = Field<Button>(dialog, "refreshButton");
        Check(dialog.Text == "Check Dependencies" && grid.ReadOnly, "dependency dialog title and read-only status table");
        int ticks = 0;
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 5 };
        heartbeat.Tick += (_, _) => ticks++;
        heartbeat.Start();
        Task check = dialog.RefreshDependenciesAsync();
        Check(!refresh.Enabled && Field<Button>(dialog, "closeButton").Enabled, "Refresh disabled during check while Close remains available");
        Pump(dialog.RefreshDependenciesAsync());
        Check(calls == 1, "overlapping refresh prevented");
        Pump(check);
        heartbeat.Stop();
        Check(ticks > 0 && refresh.Enabled, "dependency UI remains responsive and Refresh is restored");
        Check(grid.Rows.Count == 2 && grid.Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Installed"), "dialog displays both successful statuses");
        Check((string)grid.Rows[0].Cells["Version"].Value == "99.1.2-test+fixture", "dialog displays detected version");
        grid.ClearSelection();
        grid.CurrentCell = grid.Rows[0].Cells["ExecutablePath"];
        grid.CurrentCell.Selected = true;
        string copied = grid.GetClipboardContent()?.GetText() ?? string.Empty;
        Check(copied == Path.Combine(path, "rg.exe"), "selected executable path can be copied without changing the clipboard");

        path = Path.Combine(fixtures, "only rg");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(refresh, [EventArgs.Empty]);
        PumpUntil(() => refresh.Enabled);
        Check(calls == 2 && (string)grid.Rows[1].Cells["Status"].Value == "Not found", "Refresh button reruns real dependency probes");
        Check((string)grid.Rows[1].Cells["Version"].Value == "—" && (string)grid.Rows[1].Cells["ExecutablePath"].Value == "—", "refresh clears stale version and path");
        Check(Field<TextBox>(dialog, "detailsTextBox").Text.Contains("Install ripgrep-all"), "missing dependency explanation available in diagnostics");
        path = Path.Combine(fixtures, "neither");
        Pump(dialog.RefreshDependenciesAsync());
        Check(grid.Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Not found"), "dialog refresh shows neither installed");
        path = Path.Combine(fixtures, "corrupt");
        Pump(dialog.RefreshDependenciesAsync());
        Check((string)grid.Rows[0].Cells["Status"].Value == "Error" && (string)grid.Rows[1].Cells["Status"].Value == "Not found", "dialog distinguishes corrupt executable from missing executable");
        Check((string)grid.Rows[0].Cells["ExecutablePath"].Value == Path.Combine(path, "rg.exe"), "failed executable path remains selectable");

        using (var failure = new DependencyDialog(_ => Task.FromException<DependencyCheckResult[]>(new IOException("simulated failure"))))
        {
            _ = failure.Handle;
            Pump(failure.RefreshDependenciesAsync());
            Check(Field<DataGridView>(failure, "dependencyGrid").Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Error"), "unexpected checker error is displayed");
        }
        bool cancellationObserved = false;
        using (var closing = new DependencyDialog(async token =>
        {
            try { await Task.Delay(10_000, token); }
            catch (OperationCanceledException) { cancellationObserved = true; throw; }
            return [];
        }))
        {
            _ = closing.Handle;
            Task pending = closing.RefreshDependenciesAsync();
            closing.Close();
            Pump(pending);
            Check(cancellationObserved && closing.IsDisposed, "closing dialog cancels work without updating disposed controls");
        }

        PathRefreshUiTests();
        SummaryUiTests();
        DiagnosticsUiTests();
        DiagnosticRedactionTests();
        KeyboardUiTests();

        using (var main = new MainForm(fixtures, Path.Combine(fixtures, "launch-settings")))
        {
            main.StartPosition = FormStartPosition.Manual;
            main.Location = new System.Drawing.Point(-32000, -32000);
            main.ShowInTaskbar = false;
            main.Show();
            Application.DoEvents();
            var help = main.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "&Help");
            Check(help.DropDownItems.OfType<ToolStripMenuItem>().Any(item => item.Text?.Replace("&", "") == "Check Dependencies"), "launched application exposes Help Check Dependencies");
            Check(help.DropDownItems.OfType<ToolStripMenuItem>().Any(item => item.Text?.Replace("&", "") == "About"), "existing About menu retained");
            main.Close();
        }
        return passed - startCount;
    }

    private static void PathRefreshUiTests()
    {
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        var machine = new Hashtable { ["PATH"] = Path.Combine(fixtures, "neither") };
        var user = new Hashtable();
        try
        {
            using var dialog = new DependencyDialog(token => DependencyChecker.CheckAllWithPathProviderAsync(
                () => WindowsPathEnvironment.RefreshProcessPath(machine, user, Environment.GetEnvironmentVariables()),
                TimeSpan.FromSeconds(5), token));
            _ = dialog.Handle;
            var grid = Field<DataGridView>(dialog, "dependencyGrid");
            _ = grid.Handle;
            var refresh = Field<Button>(dialog, "refreshButton");
            Pump(dialog.RefreshDependenciesAsync());
            Check(grid.Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Not found"), "dialog starts with current saved Windows PATH");
            user["PATH"] = Path.Combine(fixtures, "refreshed tools");
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(refresh, [EventArgs.Empty]);
            PumpUntil(() => refresh.Enabled);
            Check(grid.Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Installed"), "Refresh button detects Windows PATH changes while dialog stays open");
            Check((string)grid.Rows[0].Cells["ExecutablePath"].Value == ExecutableResolver.ResolveSearchTool(false), "refreshed dialog path matches next search path");
            user.Clear();
            Pump(dialog.RefreshDependenciesAsync());
            Check(grid.Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Not found"), "Refresh button removes dependencies deleted from saved PATH");
            string? previousPath = Environment.GetEnvironmentVariable("PATH");
            using var failure = new DependencyDialog(token => DependencyChecker.CheckAllWithPathProviderAsync(
                () => throw new UnauthorizedAccessException("simulated Windows PATH read failure"), TimeSpan.FromSeconds(5), token));
            _ = failure.Handle;
            Pump(failure.RefreshDependenciesAsync());
            Check(Field<DataGridView>(failure, "dependencyGrid").Rows.Cast<DataGridViewRow>().All(row => (string)row.Cells["Status"].Value == "Error") &&
                Environment.GetEnvironmentVariable("PATH") == previousPath, "registry refresh failure displayed without replacing prior PATH");
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process); }
    }

    private static void SummaryUiTests()
    {
        DependencyCheckResult[] installed = DependencyChecker.Dependencies.Select(dependency =>
            new DependencyCheckResult(dependency, DependencyStatus.Installed, "1.2.3",
                Path.Combine(fixtures, "both tools & spaces", dependency.ExecutableName + ".exe"),
                "The executable ran successfully and returned its version.")).ToArray();
        DependencyCheckResult[] results = installed;
        using var dialog = new DependencyDialog(_ => Task.FromResult(results));
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-32000, -32000);
        dialog.ShowInTaskbar = false;
        dialog.Show();
        Application.DoEvents();
        var summary = Field<Label>(dialog, "summaryLabel");
        var toggle = Field<LinkLabel>(dialog, "detailsToggle");
        var rgLink = Field<LinkLabel>(dialog, "rgLink");
        var rgaLink = Field<LinkLabel>(dialog, "rgaLink");
        var details = Field<TextBox>(dialog, "detailsTextBox");
        Size compactSize = dialog.ClientSize;
        var grid = Field<DataGridView>(dialog, "dependencyGrid");
        Check(grid.DisplayedRowCount(includePartialRow: false) == 2, "both dependency rows are fully visible with padded cell styling");
        void ToggleDetails() => typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(toggle, [new LinkLabelLinkClickedEventArgs(toggle.Links[0])]);

        Check(summary.Visible && summary.Text == "All dependencies are available.", "successful checks show a single availability summary");
        Check(!toggle.Visible && !details.Visible && details.Text.Length == 0 && !rgLink.Visible && !rgaLink.Visible,
            "successful checks have no redundant diagnostics or installation links");

        results = [installed[0], new DependencyCheckResult(DependencyChecker.Dependencies[1],
            DependencyStatus.NotFound, null, null, "Install ripgrep-all and click Refresh.")];
        Pump(dialog.RefreshDependenciesAsync());
        Check(summary.Text.Contains("missing") && toggle.Visible && !details.Visible && !rgLink.Visible && rgaLink.Visible,
            "missing tools offer collapsed diagnostics and only their corresponding installation link");
        Check(details.Text.Contains("Install ripgrep-all") && !details.Text.Contains("ran successfully"), "diagnostics contain only dependencies with problems");
        ToggleDetails();
        Check(details.Visible && dialog.ClientSize.Height > compactSize.Height && toggle.Text == "Hide details", "expanding diagnostics enlarges the dialog");
        Check(details.Bottom < dialog.Controls["dependencyNote"]!.Top, "expanded diagnostics do not overlap the dependency note");
        ToggleDetails();
        Check(!details.Visible && dialog.ClientSize == compactSize && toggle.Text == "Show details", "collapsing diagnostics restores compact layout");
        ToggleDetails();
        results = installed;
        Pump(dialog.RefreshDependenciesAsync());
        Check(summary.Text.Contains("All dependencies") && !toggle.Visible && !details.Visible && details.Text.Length == 0 &&
            dialog.ClientSize == compactSize && !rgLink.Visible && !rgaLink.Visible,
            "successful refresh clears old diagnostics, collapses the section, and hides installation links");

        results = [new DependencyCheckResult(DependencyChecker.Dependencies[0], DependencyStatus.Error,
            null, installed[0].ExecutablePath, "The version check timed out."), installed[1]];
        Pump(dialog.RefreshDependenciesAsync());
        Check(summary.Text.Contains("could not be checked") && toggle.Visible && !details.Visible && !rgLink.Visible && !rgaLink.Visible,
            "execution errors have a distinct summary and expandable details without installation links");
        ToggleDetails();
        Check(details.Visible && details.Text.Contains("timed out") && !details.Text.Contains("ran successfully"), "error diagnostics remain available for copying");
        dialog.Close();
    }

    private static void DiagnosticsUiTests()
    {
        int checks = 0;
        int writes = 0;
        string? copied = null;
        bool clipboardBusy = false;
        var pending = new TaskCompletionSource<DependencyCheckResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dialog = new DependencyDialog(token =>
        {
            checks++;
            return pending.Task.WaitAsync(token);
        }, text =>
        {
            writes++;
            if (clipboardBusy)
                throw new System.Runtime.InteropServices.ExternalException("Simulated busy clipboard.");
            copied = text;
        });
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-32000, -32000);
        dialog.ShowInTaskbar = false;
        dialog.Show();
        Application.DoEvents();
        var copy = Field<Button>(dialog, "copyDiagnosticsButton");
        var refresh = Field<Button>(dialog, "refreshButton");
        var feedback = Field<Label>(dialog, "copyFeedbackLabel");
        var tooltip = Field<ToolTip>(dialog, "refreshToolTip");
        void ClickCopy() => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(copy, [EventArgs.Empty]);
        Check(!copy.Enabled && checks == 1, "copy disabled until initial dependency results are available");
        ClickCopy();
        Check(writes == 0, "copy cannot publish an unchecked report");
        Check(copy.Text == "Copy Diagnostics" && copy.Left == 15 && copy.Top == refresh.Top && copy.Height == refresh.Height,
            "Copy Diagnostics is aligned with the bottom action buttons");
        Check(tooltip.GetToolTip(copy) == "Copy dependency and system information for troubleshooting.", "copy button has requested tooltip");

        DependencyCheckResult[] installed = DependencyChecker.Dependencies.Select(dependency =>
            new DependencyCheckResult(dependency, DependencyStatus.Installed, "99.1.2",
                Path.Combine(fixtures, "both tools & spaces", dependency.ExecutableName + ".exe"),
                "The executable ran successfully and returned its version.")).ToArray();
        pending.SetResult(installed);
        PumpUntil(() => copy.Enabled && refresh.Enabled);
        ClickCopy();
        Check(writes == 1 && checks == 1 && copied is not null, "copy uses completed results without rechecking dependencies");
        Check(copied!.StartsWith("Ripgrep Search — Dependency Diagnostics\r\n") &&
            copied.Contains("Application version: " + Application.ProductVersion.Split('+')[0]) &&
            copied.Contains("Windows version: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription),
            "report identifies the application version and Windows version");
        Check(installed.All(result => copied.Contains(result.Dependency.DisplayName + "\r\nStatus: Installed\r\nVersion: 99.1.2\r\nExecutable path: " + result.ExecutablePath)),
            "plain-text report has separate dependency sections, status, version, and full executable path");
        Check(!copied.Contains("ran successfully") && !copied.Contains("PATH=") && !copied.Contains("USERPROFILE="),
            "report omits redundant success messages and environment dumps");
        Check(feedback.Text == "Diagnostics copied to clipboard.", "successful copy gives nonmodal feedback");

        string firstReport = copied;
        installed[0] = installed[0] with { Version = "not a checked version" };
        ClickCopy();
        Check(copied == firstReport, "copied results retain the last completed snapshot");
        pending = new TaskCompletionSource<DependencyCheckResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refreshing = dialog.RefreshDependenciesAsync();
        Check(checks == 2 && copy.Enabled && !refresh.Enabled, "last completed results remain available during refresh");
        ClickCopy();
        Check(copied == firstReport && checks == 2, "copy during refresh uses the previous completed results without another check");
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string privateExecutablePath = Path.Combine(userProfile, "scoop", "shims", "rg.exe");
        pending.SetResult([
            new DependencyCheckResult(DependencyChecker.Dependencies[0], DependencyStatus.Error, null,
                privateExecutablePath, $"The version check timed out. Executable: \"{privateExecutablePath}\"."),
            new DependencyCheckResult(DependencyChecker.Dependencies[1], DependencyStatus.NotFound, null, null,
                "rga was not found. Install ripgrep-all and click Refresh.")
        ]);
        Pump(refreshing);
        ClickCopy();
        Check(checks == 2 && copied != firstReport && copied!.Contains("Status: Error") && copied.Contains("Status: Not found") &&
            copied.Contains("Version: Not available") && copied.Contains("Executable path: Not available"),
            "next copy uses the latest completed statuses and marks unavailable fields clearly");
        Check(copied!.Contains("Diagnostics:\r\nThe version check timed out.") &&
            copied.Contains("Diagnostics:\r\nrga was not found."), "report includes error and missing-tool diagnostics");
        Check(copied.Contains(@"Executable path: %USERPROFILE%\scoop\shims\rg.exe") &&
            copied.Contains("Executable: \"%USERPROFILE%\\scoop\\shims\\rg.exe\".") &&
            !copied.Contains(userProfile, StringComparison.OrdinalIgnoreCase),
            "copied reports redact profile paths in executable fields and error messages");
        Check((string)Field<DataGridView>(dialog, "dependencyGrid").Rows[0].Cells["ExecutablePath"].Value == privateExecutablePath &&
            Field<TextBox>(dialog, "detailsTextBox").Text.Contains(privateExecutablePath),
            "redaction leaves the displayed executable path and original diagnostic messages intact");
        var rgLink = Field<LinkLabel>(dialog, "rgLink");
        var rgaLink = Field<LinkLabel>(dialog, "rgaLink");
        Check((!rgLink.Visible || rgLink.Bottom <= copy.Top) && rgaLink.Visible && rgaLink.Bottom <= copy.Top,
            $"installation links do not overlap Copy Diagnostics (link: {rgaLink.Bounds}, button: {copy.Bounds})");

        clipboardBusy = true;
        ClickCopy();
        Check(feedback.Text == "Could not copy diagnostics. Please try again." && copy.Enabled && checks == 2,
            "clipboard failure gives nonmodal feedback and permits retry without rechecking");
        clipboardBusy = false;
        ClickCopy();
        Check(feedback.Text == "Diagnostics copied to clipboard.", "copy recovers after a clipboard failure");
        var timer = Field<System.Windows.Forms.Timer>(dialog, "copyFeedbackTimer");
        timer.Interval = 1;
        PumpUntil(() => feedback.Text.Length == 0);
        Check(!timer.Enabled, "copy feedback disappears automatically and stops its timer");
        dialog.Close();
    }

    private static void DiagnosticRedactionTests()
    {
        const string profile = @"C:\Users\Test User";
        Check(DependencyDiagnostics.RedactUserProfile(@"C:\Users\Test User\scoop\shims\rg.exe", profile) ==
            @"%USERPROFILE%\scoop\shims\rg.exe", "profile prefix is replaced while preserving the executable location");
        Check(DependencyDiagnostics.RedactUserProfile(@"c:\USERS\TEST USER\rg.exe", profile + "\\") ==
            @"%USERPROFILE%\rg.exe", "profile matching handles Windows casing and trailing separators");
        Check(DependencyDiagnostics.RedactUserProfile("C:/Users/Test User/tools/rg.exe", profile) ==
            "%USERPROFILE%/tools/rg.exe", "forward-slash paths are also redacted");
        Check(DependencyDiagnostics.RedactUserProfile(@"Failed to execute C:\\Users\\Test User\\tools\\rg.exe", profile) ==
            @"Failed to execute %USERPROFILE%\\tools\\rg.exe", "escaped paths in tool error output are redacted");
        Check(DependencyDiagnostics.RedactUserProfile(@"C:\Users\Zoë [test]\rg.exe", @"C:\Users\Zoë [test]") ==
            @"%USERPROFILE%\rg.exe", "Unicode and pattern characters in profile names are literal");
        const string otherPaths = @"C:\Users\Test User2\rg.exe; C:\Users\Test User.old\rga.exe; C:\Tools\rg.exe";
        Check(DependencyDiagnostics.RedactUserProfile(otherPaths, profile) == otherPaths,
            "similar profile prefixes and paths outside the current profile are preserved");
        Check(DependencyDiagnostics.RedactUserProfile($"\"{profile}\"; ({profile})\r\n{profile}", profile) ==
            "\"%USERPROFILE%\"; (%USERPROFILE%)\r\n%USERPROFILE%", "quoted paths, punctuation, and exact profile roots are redacted");
        Check(DependencyDiagnostics.RedactUserProfile(otherPaths, null) == otherPaths &&
            DependencyDiagnostics.RedactUserProfile(otherPaths, "") == otherPaths,
            "an unavailable profile never replaces empty strings");
    }

    private static void KeyboardUiTests()
    {
        DependencyCheckResult[] installed = DependencyChecker.Dependencies.Select(dependency =>
            new DependencyCheckResult(dependency, DependencyStatus.Installed, "1.2.3",
                Path.Combine(fixtures, dependency.ExecutableName + ".exe"), string.Empty)).ToArray();
        void ShowOffscreen(DependencyDialog dialog)
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new System.Drawing.Point(-32000, -32000);
            dialog.ShowInTaskbar = false;
            dialog.Show();
            Application.DoEvents();
        }
        // Route the key through the focused control, as the dialog-key pipeline does.
        // Sending it straight to the Form would miss the grid consuming Enter.
        bool PressDialogKey(DependencyDialog dialog, Keys key) =>
            (bool)typeof(Control).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog.ActiveControl ?? dialog, [key])!;

        foreach (Keys key in new[] { Keys.Enter, Keys.Escape })
        {
            using var dialog = new DependencyDialog(_ => Task.FromResult(installed));
            ShowOffscreen(dialog);
            Check(dialog.ActiveControl == Field<Button>(dialog, "closeButton"), "Close has initial keyboard focus after checks: " + key);
            Check(PressDialogKey(dialog, key) && dialog.IsDisposed, "focused dialog key closes the dialog: " + key);
        }

        bool cancelled = false;
        using (var checking = new DependencyDialog(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return [];
        }))
        {
            ShowOffscreen(checking);
            Check(checking.ActiveControl == Field<Button>(checking, "closeButton") &&
                !Field<Button>(checking, "refreshButton").Enabled, "Close has focus while checks are still running");
            Check(PressDialogKey(checking, Keys.Enter) && checking.IsDisposed, "Enter closes immediately during dependency checks");
            PumpUntil(() => cancelled);
            Check(cancelled, "closing with Enter cancels in-progress checks");
        }

        var pending = new TaskCompletionSource<DependencyCheckResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var navigating = new DependencyDialog(token => pending.Task.WaitAsync(token)))
        {
            ShowOffscreen(navigating);
            var grid = Field<DataGridView>(navigating, "dependencyGrid");
            navigating.ActiveControl = grid;
            pending.SetResult(installed);
            PumpUntil(() => Field<Button>(navigating, "refreshButton").Enabled);
            Check(navigating.ActiveControl == grid, "completed checks do not steal focus after the user selects the table");
            grid.CurrentCell = grid.Rows[0].Cells["ExecutablePath"];
            grid.CurrentCell.Selected = true;
            Check(grid.GetClipboardContent()?.GetText() == installed[0].ExecutablePath, "keyboard focus changes preserve path selection and copying");
            Check(PressDialogKey(navigating, Keys.Escape) && navigating.IsDisposed, "Escape still closes when the table has focus");
        }
    }

    private static T Field<T>(Form form, string name) =>
        (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> finished)
    {
        var elapsed = Stopwatch.StartNew();
        while (!finished())
        {
            Application.DoEvents();
            Thread.Sleep(1);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("Dependency dialog test timed out.");
        }
        Application.DoEvents();
    }

    private static string CreateTools(string name, bool rg, bool rga, string mode = "valid")
    {
        string directory = Path.Combine(fixtures, name);
        Directory.CreateDirectory(directory);
        foreach (string file in new[] { "RipgrepSearch.Tests.dll", "RipgrepSearch.Tests.deps.json", "RipgrepSearch.Tests.runtimeconfig.json", "RipgrepSearch.dll", "RipgrepSearch.Core.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(directory, file));
        if (rg) File.Copy(Path.Combine(AppContext.BaseDirectory, "RipgrepSearch.Tests.exe"), Path.Combine(directory, "rg.exe"));
        if (rga) File.Copy(Path.Combine(AppContext.BaseDirectory, "RipgrepSearch.Tests.exe"), Path.Combine(directory, "rga.exe"));
        File.WriteAllText(Path.Combine(directory, "version-mode.txt"), mode);
        return directory;
    }

    private static bool ExitedVersionProcess(string directory)
    {
        string path = Path.Combine(directory, "version.pid");
        if (!File.Exists(path)) return true; // Cancellation can precede process entry.
        try
        {
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(path)));
            return process.HasExited || process.WaitForExit(2000);
        }
        catch (ArgumentException) { return true; }
    }

    public static int FakeVersion()
    {
        string directory = Path.GetDirectoryName(Environment.ProcessPath!)!;
        string mode = File.ReadAllText(Path.Combine(directory, "version-mode.txt"));
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("rga", StringComparison.OrdinalIgnoreCase) ? "ripgrep-all" : "ripgrep";
        string version = name == "ripgrep" ? "99.1.2-test+fixture" : "98.2.1";
        if (mode == "record-path")
            File.WriteAllText(Path.Combine(directory, name + "-inherited-path.txt"), Environment.GetEnvironmentVariable("PATH") ?? "");
        switch (mode)
        {
            case "timeout":
                File.WriteAllText(Path.Combine(directory, "version.pid"), Environment.ProcessId.ToString());
                Thread.Sleep(15_000);
                return 0;
            case "exit-error":
                Console.WriteLine($"{name} {version}");
                Console.Error.WriteLine("simulated version failure");
                return 7;
            case "invalid": Console.WriteLine("unrecognized version output"); return 0;
            case "empty": return 0;
            case "wrong-tool": Console.WriteLine("another-tool 1.2.3"); return 0;
            case "stderr": Console.Error.WriteLine($"{name} {version}"); return 0;
            case "flood":
                Console.WriteLine(new string('x', 100_000));
                Console.Error.WriteLine(new string('y', 100_000));
                return 0;
            default: Console.WriteLine($"{name} {version}"); return 0;
        }
    }
}
