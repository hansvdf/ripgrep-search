using System.Drawing;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Reflection;
using System.IO.Compression;
using RipgrepSearch;

internal static class Tests
{
    private static int passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake")
            return FakeProcess(args[1], args.Length > 2 ? args[2] : null);
        try
        {
            if (args.Contains("--ui-only"))
            {
                UiTests();
                Console.WriteLine($"PASS: {passed} UI checks.");
                return 0;
            }
            ParserTests();
            Console.WriteLine($"Parser: {passed} checks passed. Running engine integration tests...");
            RunnerTests().GetAwaiter().GetResult();
            Console.WriteLine($"Parser/engines: {passed} checks passed. Running offscreen UI tests...");
            UiTests();
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
        string invalidPath = "{\"type\":\"match\",\"data\":{\"path\":{\"bytes\":\"/w==\"},\"line_number\":1,\"lines\":{\"text\":\"a\"},\"submatches\":[{\"start\":0,\"end\":1}]}}";
        var unopenable = parser.Parse(invalidPath)[0];
        Check(unopenable.NavigationPath is null && unopenable.FilePath == "�", "invalid path bytes do not navigate to a replacement path");
        Check(new SearchResultFormatter().Format(unopenable)[0].IsFileHeading, "unopenable file retains heading semantics");
        var random = new Random(1729);
        for (int i = 0; i < 100; i++)
        {
            var bytes = new byte[64];
            random.NextBytes(bytes);
            // Exclude line terminators so the decoded text remains a single row.
            for (int j = 0; j < bytes.Length; j++)
                if (bytes[j] is 10 or 13) bytes[j] = 32;
            var decoded = parser.Parse(Message(bytes, [], true))[0];
            Check(decoded.Text == Encoding.UTF8.GetString(bytes), "invalid UTF-8 decoding agrees with .NET");
        }
    }

    private static async Task<(SearchOutcome Outcome, List<SearchResultLine> Lines)> Run(
        string executable, IReadOnlyList<string> arguments, SearchLimits? limits = null,
        CancellationToken token = default, Action? onBatch = null)
    {
        var channel = Channel.CreateBounded<IReadOnlyList<SearchResultLine>>(2);
        Task<SearchOutcome> run = Task.Run(() => SearchRunner.RunAsync(executable, arguments,
            Environment.CurrentDirectory, channel.Writer, token, limits));
        var lines = new List<SearchResultLine>();
        await foreach (var batch in channel.Reader.ReadAllAsync())
        {
            lines.AddRange(batch);
            onBatch?.Invoke();
        }
        return (await run.WaitAsync(TimeSpan.FromSeconds(15)), lines);
    }

    private static async Task<string> PlainOutput(string engine, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string error = await stderr;
        if (process.ExitCode > 1) throw new Exception(error);
        return await stdout;
    }

    private static async Task RunnerTests()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string file = Path.Combine(fixtures, "café [#] & 😀.txt");
        await File.WriteAllTextAsync(file, "before\r\nNeedle needle café 😀\r\nword.word\r\nwordXword\r\n\r\nafter\r\n", new UTF8Encoding(false));
        foreach (string engine in new[] { "rg", "rga" })
        {
            foreach (var (pattern, flags) in new[]
            {
                ("needle", Array.Empty<string>()),
                ("(?i)needle", Array.Empty<string>()),
                ("word.word", new[] { "--fixed-strings" }),
                ("needle", new[] { "--ignore-case" }),
                ("word\\.word", Array.Empty<string>()),
                ("^", Array.Empty<string>()),
                ("^$", Array.Empty<string>()),
                ("Needle|after", Array.Empty<string>()),
                ("(?-u:\\x61)", Array.Empty<string>()),
                ("(?s:Needle.*after)", new[] { "--multiline" })
            })
            {
                var arguments = SearchRunner.BuildArguments(pattern, file, 1, 1, null, false);
                arguments.InsertRange(0, flags);
                var result = await Run(engine, arguments);
                Check(result.Outcome.Completion == SearchCompletion.Completed, $"{engine}: search completed: {pattern}");
                var legacy = arguments.Where(a => a != "--json").ToList();
                legacy.Insert(0, "--heading");
                string plain = await PlainOutput(engine, legacy);
                int legacyCount = Regex.Matches(plain, @"(?m)^\d+:").Count;
                Check(result.Lines.Count(l => l.IsMatch) == legacyCount, $"{engine}: matching-line count equals legacy output: {pattern}");
                if (pattern == "(?i)needle")
                {
                    Check(result.Lines.Single(l => l.IsMatch).Matches.Count == 2, $"{engine}: two matches still one matching line");
                    Check(result.Lines.Any(l => !l.IsMatch), $"{engine}: before/after context");
                }
                if (flags.Contains("--fixed-strings"))
                    Check(result.Lines.Single(l => l.IsMatch).Text == "word.word", $"{engine}: literal highlights follow engine");
            }

            var unicode = await Run(engine, SearchRunner.BuildArguments("café|😀", file, 0, 0, null, false));
            var unicodeLine = unicode.Lines.Single();
            Check(unicodeLine.Matches.Select(m => unicodeLine.Text.Substring(m.Start, m.Length)).SequenceEqual(new[] { "café", "😀" }), $"{engine}: accented and emoji matches");
            Check(unicodeLine.NavigationPath == file, $"{engine}: Unicode file navigation");

            string invalidFile = Path.Combine(fixtures, "invalid.txt");
            await File.WriteAllBytesAsync(invalidFile, [0xff, 0x20, 0x61, 0x0a]);
            var invalid = await Run(engine, SearchRunner.BuildArguments("a", invalidFile, 0, 0, null, false));
            Check(invalid.Lines.Single().Matches.Single() == new MatchRange(2, 1), $"{engine}: base64 byte offsets");
            string utf16File = Path.Combine(fixtures, "utf16.txt");
            await File.WriteAllTextAsync(utf16File, "café 😀 needle\r\n", Encoding.Unicode);
            var utf16 = await Run(engine, SearchRunner.BuildArguments("needle", utf16File, 0, 0, null, false));
            Check(utf16.Lines.Single().Text == "café 😀 needle" && utf16.Lines.Single().Matches.Single() == new MatchRange(8, 6), $"{engine}: automatic UTF-16 source transcoding");

            foreach (string field in new[] { "path", "modified", "accessed", "created" })
            foreach (bool descending in new[] { false, true })
            {
                string sortFolder = Path.Combine(fixtures, engine + "-" + field + "-" + descending);
                Directory.CreateDirectory(sortFolder);
                foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
                    await File.WriteAllTextAsync(Path.Combine(sortFolder, name), "needle\n");
                foreach (var item in new[] { ("a.txt", 3), ("b.txt", 1), ("c.txt", 2) })
                {
                    string path = Path.Combine(sortFolder, item.Item1);
                    var time = new DateTime(2024, 1, item.Item2, 0, 0, 0, DateTimeKind.Utc);
                    File.SetCreationTimeUtc(path, time);
                    File.SetLastWriteTimeUtc(path, time);
                    File.SetLastAccessTimeUtc(path, time);
                }
                var result = await Run(engine, SearchRunner.BuildArguments("needle", sortFolder, 0, 0, field, descending));
                string[] expected = field == "path" ? ["a.txt", "b.txt", "c.txt"] : ["b.txt", "c.txt", "a.txt"];
                if (descending) Array.Reverse(expected);
                string actualOrder = string.Join(",", result.Lines.Select(l => Path.GetFileName(l.FilePath)));
                Check(result.Outcome.Completion == SearchCompletion.Completed && result.Lines.Select(l => Path.GetFileName(l.FilePath)).SequenceEqual(expected), $"{engine}: {field} {(descending ? "descending" : "ascending")}: {actualOrder}; {result.Outcome}");
                VerifyExports(result.Lines);
            }

            var badRegex = await Run(engine, SearchRunner.BuildArguments("[", file, 0, 0, null, false));
            Check(badRegex.Outcome.Completion == SearchCompletion.Failed && badRegex.Outcome.ExitCode == 2 && badRegex.Outcome.Diagnostics.Length > 0, $"{engine}: invalid regex diagnostics");
            var absent = await Run(engine, SearchRunner.BuildArguments("a", Path.Combine(fixtures, "not-present"), 0, 0, null, false));
            Check(absent.Outcome.Completion == SearchCompletion.Failed, $"{engine}: inaccessible path");
            string large = Path.Combine(fixtures, "large.txt");
            await File.WriteAllTextAsync(large, string.Concat(Enumerable.Repeat("needle needle\n", 10_100)));
            var limited = await Run(engine, SearchRunner.BuildArguments("needle", large, 0, 0, null, false));
            Check(limited.Outcome.Completion == SearchCompletion.Limited && limited.Lines.Count == 10_000, $"{engine}: bounded large result set: {limited.Lines.Count} lines; {limited.Outcome}");
            Check(limited.Lines.Count(l => l.IsMatch) == 10_000, $"{engine}: limited counts remain matching lines, not 20,000 matches");
            await File.WriteAllTextAsync(large, new string('x', 20_000));
            var longLine = await Run(engine, SearchRunner.BuildArguments("x", large, 0, 0, null, false));
            Check(longLine.Outcome.Completion == SearchCompletion.Limited && longLine.Lines.Count == 0, $"{engine}: long-line limit");
        }

        string self = Environment.ProcessPath!;
        foreach (string mode in new[] { "malformed", "truncated", "exit-error", "stderr-flood", "huge-json", "false-stats" })
        {
            var result = await Run(self, ["--fake", mode], mode == "huge-json" ? new SearchLimits(MaxJsonMessageCharacters: 1024) : null);
            if (mode == "huge-json")
                Check(result.Outcome.Completion == SearchCompletion.Limited, "oversized JSON bounded before parsing");
            else if (mode is "stderr-flood" or "false-stats")
                Check(result.Outcome.Completion == SearchCompletion.Completed && result.Lines.Count(l => l.IsMatch) == 1, "ignore misleading stats; bounded stderr does not deadlock");
            else
                Check(result.Outcome.Completion == SearchCompletion.Failed && result.Lines.Count == 1, "retain partial results after " + mode);
            if (mode == "stderr-flood")
                Check(result.Outcome.Diagnostics.Length < 17_000 && result.Outcome.Diagnostics.Contains("omitted"), "bounded diagnostics");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var elapsed = Stopwatch.StartNew();
            var result = await Run(self, ["--fake", "slow"], token: cancellation.Token, onBatch: () => cancellation.Cancel());
            Check(result.Outcome.Completion == SearchCompletion.Cancelled && result.Lines.Count == 1, "incremental results before process exit and cancellation");
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "cancellation terminates process promptly");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            string pidPath = Path.Combine(fixtures, "child.pid");
            var result = await Run(self, ["--fake", "child-parent", pidPath], token: cancellation.Token, onBatch: () => cancellation.Cancel());
            int childId = int.Parse(File.ReadAllText(pidPath));
            bool exited;
            try { using var child = Process.GetProcessById(childId); exited = child.HasExited || child.WaitForExit(2000); }
            catch (ArgumentException) { exited = true; }
            Check(result.Outcome.Completion == SearchCompletion.Cancelled && exited, "cancellation terminates descendant converter process");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var result = await Run(self, ["--fake", "slow"], token: cancellation.Token);
            Check(result.Outcome.Completion == SearchCompletion.Cancelled && result.Lines.Count == 0, "pre-cancelled search");
        }
        var missing = await Run(Path.Combine(fixtures, "missing-engine.exe"), []);
        Check(missing.Outcome.Completion == SearchCompletion.Failed && missing.Lines.Count == 0, "process launch failure completes channel");
        var status = MainForm.DescribeOutcome(new SearchOutcome(SearchCompletion.Limited, null, "", "limit"), 7);
        Check(status.Contains("7 matching line(s) shown") && status.Contains("stopped") && !status.Contains("found"), "partial count is explicitly labelled");
        await DocumentTests(fixtures);
    }

    private static void VerifyExports(IReadOnlyList<SearchResultLine> lines)
    {
        var report = new SearchReport("folder", "pattern", "rg --json", DateTimeOffset.Now) { Status = "completed" };
        var formatter = new SearchResultFormatter();
        report.Results.AddRange(lines);
        foreach (var line in lines) report.Spans.AddRange(formatter.Format(line));
        string html = ResultsExporter.ToHtml(report);
        string markdown = ResultsExporter.ToMarkdown(report);
        Check(Regex.Matches(html, "<a href=").Count == lines.Select(l => l.FilePath).Distinct().Count(), "HTML links retained");
        Check(html.Contains("OrangeRed") && html.Contains("font-weight: bold"), "HTML match colors retained");
        Check(markdown.Contains("### [") && markdown.Contains("```text"), "Markdown filename links and code blocks retained");
        int htmlPosition = 0, mdPosition = 0;
        foreach (var path in lines.Select(l => l.FilePath).Distinct())
        {
            string uri = new Uri(path).AbsoluteUri;
            int nextHtml = html.IndexOf(WebUtility.HtmlEncode(uri), htmlPosition, StringComparison.Ordinal);
            int nextMd = markdown.IndexOf(uri, mdPosition, StringComparison.Ordinal);
            Check(nextHtml >= htmlPosition && nextMd >= mdPosition, "sort order retained in both exports");
            htmlPosition = nextHtml + 1;
            mdPosition = nextMd + 1;
        }
    }

    private static int FakeProcess(string mode, string? pidPath)
    {
        if (mode == "wait-child") { Thread.Sleep(60_000); return 0; }
        if (mode == "child-parent")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--fake");
            start.ArgumentList.Add("wait-child");
            using var child = Process.Start(start)!;
            File.WriteAllText(pidPath!, child.Id.ToString());
            Thread.Sleep(100);
        }
        if (mode == "huge-json")
        {
            Console.Write(new string('x', 100_000));
            Console.Out.Flush();
            Thread.Sleep(10_000);
            return 0;
        }
        if (mode == "slow") Thread.Sleep(100);
        Console.WriteLine(Message(Encoding.UTF8.GetBytes("needle needle\n"), [(0, 6), (7, 13)]));
        Console.Out.Flush();
        if (mode is "slow" or "child-parent") Thread.Sleep(60_000);
        if (mode == "malformed") Console.WriteLine("not json");
        if (mode == "exit-error") { Console.Error.WriteLine("Simulated converter failure"); return 2; }
        if (mode == "stderr-flood") Console.Error.WriteLine(new string('!', 100_000));
        if (mode is "stderr-flood" or "false-stats")
            Console.WriteLine("{\"type\":\"summary\",\"data\":{\"stats\":{\"matched_lines\":999,\"matches\":888}}}");
        return 0;
    }

    private static async Task DocumentTests(string fixtures)
    {
        string archivePath = Path.Combine(fixtures, "archive.zip");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("nested/café.txt").Open());
            writer.WriteLine("archive needle café 😀");
        }
        string docx = Path.Combine(fixtures, "document.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            void Entry(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            Entry("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Entry("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Entry("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>document needle café 😀</w:t></w:r></w:p></w:body></w:document>");
        }
        string pdf = Path.Combine(fixtures, "document.pdf");
        var pdfText = new StringBuilder("%PDF-1.4\n");
        string stream = "BT /F1 12 Tf 72 720 Td (PDF needle text) Tj ET\n";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}endstream"
        ];
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdfText.Length);
            pdfText.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdfText.Length;
        pdfText.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdfText.Append($"{offset:D10} 00000 n \n");
        pdfText.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        await File.WriteAllTextAsync(pdf, pdfText.ToString(), Encoding.ASCII);
        foreach (string file in new[] { archivePath, docx, pdf })
        {
            var arguments = SearchRunner.BuildArguments("needle", file, 1, 1, null, false);
            arguments.Insert(0, "--rga-cache-path=" + Path.Combine(fixtures, "rga-cache"));
            var result = await Run("rga", arguments);
            Check(result.Outcome.Completion == SearchCompletion.Completed && result.Lines.Any(l => l.IsMatch), "rga document search: " + Path.GetExtension(file) + ": " + result.Outcome);
            Check(result.Lines.Where(l => l.IsMatch).All(l => l.Matches.Any(m => l.Text.Substring(m.Start, m.Length) == "needle")), "rga document match positions: " + Path.GetExtension(file));
            Check(result.Lines.All(l => l.NavigationPath == file), "rga document navigation: " + Path.GetExtension(file));
            VerifyExports(result.Lines);
        }
    }

    private static T Field<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static Task StartSearch(MainForm form) =>
        (Task)typeof(MainForm).GetMethod("RunSearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;

    private static void Pump(Task task, MainForm? form = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(1);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("UI search did not finish within 30 seconds. " +
                    (form is null ? "" : Field<Label>(form, "statusLabel").Text));
        }
        task.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static void UiTests()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string fixtures = Path.Combine(AppContext.BaseDirectory, "ui-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string source = Path.Combine(fixtures, "input");
        Directory.CreateDirectory(source);
        foreach (string name in new[] { "a café.txt", "b 😀.txt" })
            File.WriteAllText(Path.Combine(source, name), "context\r\n" + string.Concat(Enumerable.Repeat("café 😀 needle needle {test} \\ path\r\n", 40)));
        string settingsDirectory = Path.Combine(fixtures, "settings");
        Directory.CreateDirectory(settingsDirectory);
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        string settings = "{\"ShowSearchOptions\":false,\"ShowCommand\":false,\"UseRipgrepAll\":true,\"SortEnabled\":true,\"SortBy\":\"path\",\"SortDescending\":true,\"BeforeContext\":1,\"AfterContext\":2}";
        File.WriteAllText(settingsPath, settings);
        var form = new MainForm(source, settingsDirectory);
        // Create controls offscreen; no window is shown and user settings are never used.
        _ = form.Handle;
        var output = Field<RichTextBox>(form, "outputTextBox");
        _ = output.Handle;
        Check(File.ReadAllText(settingsPath) == settings, "startup does not overwrite saved preferences");
        Check(Field<NumericUpDown>(form, "beforeNumeric").Value == 1 && Field<NumericUpDown>(form, "afterNumeric").Value == 2, "saved context restored");
        Check(!Field<CheckBox>(form, "showSearchOptionsCheckBox").Checked && !Field<CheckBox>(form, "showCommandCheckBox").Checked, "saved visibility restored");
        var pattern = Field<ComboBox>(form, "searchComboBox");
        pattern.Text = "needle";
        Pump(StartSearch(form));
        var report = Field<SearchReport>(form, "searchReport");
        Check(report.Results.Count(l => l.IsMatch) == 80, "rga UI counts matching lines, not 160 matches");
        Check(report.Results[0].FilePath.EndsWith("b 😀.txt"), "UI sorting preserved");
        Check(report.Status == "Search completed. 80 matching line(s) found.", "completed status retains original meaning");
        Check(output.Text.Replace("\r\n", "\n") == string.Concat(report.Spans.Select(s => s.Text)).Replace("\r\n", "\n"), "RTF batches retain exact displayed text across batches");
        int match = output.Text.IndexOf("needle", StringComparison.Ordinal);
        output.Select(match, 6);
        Check(output.SelectionColor.ToArgb() == Color.OrangeRed.ToArgb() && output.SelectionFont!.Bold, "RTF match color and bold");
        int accented = output.Text.IndexOf("café 😀 needle", StringComparison.Ordinal);
        output.Select(accented, 4);
        Check(output.SelectionColor.ToArgb() == Color.White.ToArgb() && !output.SelectionFont!.Bold, "UTF-16 Unicode preceding match remains white");
        var map = Field<Dictionary<int, string>>(form, "fileLineMap");
        Check(map.Count == 2 && map.All(item => output.Lines[item.Key] == item.Value), "filename map aligns with rendered lines");
        foreach (int index in map.Keys)
        {
            output.Select(output.GetFirstCharIndexFromLine(index), 1);
            Check(output.SelectionColor.ToArgb() == Color.DeepSkyBlue.ToArgb() && output.SelectionFont!.Underline, "file headings retain blue underline");
        }
        VerifyExports(report.Results);
        string exported = ResultsExporter.ToHtml(report);
        Check(WebUtility.HtmlDecode(Regex.Replace(exported, "<[^>]+>", "")).Contains("café 😀 needle needle {test} \\ path"), "HTML preserves escaped source and Unicode");
        pattern.Text = "something else";
        Check(report.Pattern == "needle" && ResultsExporter.ToHtml(report) == exported, "export snapshot independent of edited controls");

        Field<CheckBox>(form, "useRipgrepAllCheckBox").Checked = false;
        pattern.Text = "^";
        Pump(StartSearch(form));
        report = Field<SearchReport>(form, "searchReport");
        Check(report.Results.Count(l => l.IsMatch) == 82 && !report.Spans.Any(s => s.Color == Color.OrangeRed), "zero-length UI matches retain line counts without false highlighting");
        pattern.Text = "missing-word";
        Pump(StartSearch(form));
        Check(output.Text == "No matches found." && Field<SearchReport>(form, "searchReport").Results.Count == 0, "no-match UI output");
        Check(Field<ToolStripMenuItem>(form, "exportMenuItem").Enabled, "no-match report can still be exported");
        pattern.Text = "[";
        Pump(StartSearch(form));
        Check(Field<Label>(form, "statusLabel").Text.Contains("exit code 2") && output.Text.Contains("regex parse error"), "invalid regex displayed without crashing");

        string stressFolder = Path.Combine(fixtures, "nested-repeat");
        Directory.CreateDirectory(stressFolder);
        File.WriteAllText(Path.Combine(stressFolder, "stress.txt"), new string('a', 10_000) + "!\n");
        Field<ComboBox>(form, "folderComboBox").Text = stressFolder;
        pattern.Text = "(a+)+$|a";
        Pump(StartSearch(form));
        report = Field<SearchReport>(form, "searchReport");
        Check(report.Results.Single().Matches.Count == 10_000, "nested repeats with fallback use engine offsets without .NET backtracking");
        Check(report.Spans.Count(s => s.Color == Color.OrangeRed) == 1 && report.Spans.Single(s => s.Color == Color.OrangeRed).Text.Length == 10_000, "adjacent matches coalesce into one visual run");

        string largeFolder = Path.Combine(fixtures, "large");
        Directory.CreateDirectory(largeFolder);
        File.WriteAllText(Path.Combine(largeFolder, "large.txt"), string.Concat(Enumerable.Repeat("needle needle\n", 20_000)));
        Field<ComboBox>(form, "folderComboBox").Text = largeFolder;
        pattern.Text = "needle";
        int ticks = 0;
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 10 };
        heartbeat.Tick += (_, _) => ticks++;
        heartbeat.Start();
        Pump(StartSearch(form), form);
        heartbeat.Stop();
        report = Field<SearchReport>(form, "searchReport");
        Check(report.Results.Count is > 0 and <= 10_000 && report.Status.Contains("Display limit"), $"UI display limit retains explicitly partial report: {report.Results.Count}; {report.Status}");
        Check(ticks >= 3, "UI processes timer messages during large rendering");
        Check(ResultsExporter.ToMarkdown(report).Contains("search stopped"), "limited status retained in exports");

        using var cancel = new System.Windows.Forms.Timer { Interval = 10 };
        cancel.Tick += (_, _) =>
        {
            if (Field<SearchReport>(form, "searchReport").Results.Count == 0) return;
            Check(Field<Button>(form, "searchButton").Text == "Cancel", "search button becomes Cancel");
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Field<Button>(form, "searchButton"), [EventArgs.Empty]);
            cancel.Stop();
        };
        cancel.Start();
        Pump(StartSearch(form));
        Check(Field<SearchReport>(form, "searchReport").Status.Contains("cancelled"), "Cancel button stops active search");
        Check(Field<Button>(form, "searchButton").Text == "Search" && Field<ComboBox>(form, "folderComboBox").Enabled, "controls restored after cancellation");
        Check(Field<SearchReport>(form, "searchReport").Status.Contains("incomplete"), "cancelled count explicitly partial");
        Field<CheckBox>(form, "useRipgrepAllCheckBox").Checked = true;
        using var close = new System.Windows.Forms.Timer { Interval = 10 };
        close.Tick += (_, _) => { close.Stop(); form.Close(); };
        close.Start();
        Pump(StartSearch(form));
        Check(form.IsDisposed, "closing during rga search cancels, waits for cleanup, and closes");
        form.Dispose();
    }
}
