using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RipgrepSearch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? startupFolder = ResolveStartupFolder(args);
        Application.Run(new MainForm(startupFolder));
    }

    private static string? ResolveStartupFolder(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            try
            {
                string argumentFolder = Path.GetFullPath(args[0].Trim('"'));
                if (Directory.Exists(argumentFolder))
                    return argumentFolder;
            }
            catch
            {
                // Ignore an invalid startup argument and try the working directory.
            }
        }

        try
        {
            string workingDirectory = Path.GetFullPath(Environment.CurrentDirectory);
            if (Directory.Exists(workingDirectory))
                return workingDirectory;
        }
        catch
        {
            // The form will fall back to folder history.
        }

        return null;
    }
}

internal sealed class AppSettings
{
    public List<string> FolderHistory { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    public bool ShowCommand { get; set; } = true;
    public bool ShowSearchOptions { get; set; } = true;
    public int BeforeContext { get; set; } = 0;
    public int AfterContext { get; set; } = 0;
    public bool UseRipgrepAll { get; set; } = false;
    public bool SortEnabled { get; set; } = false;
    public string SortBy { get; set; } = "path";
    public bool SortDescending { get; set; } = false;
}

internal sealed class SortGroupBox : GroupBox
{
    public SortGroupBox()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var captionBounds = new Rectangle(
            new Point(LogicalToDeviceUnits(8), 0),
            TextRenderer.MeasureText(e.Graphics, Text, Font));
        int borderTop = captionBounds.Height / 2;
        int gap = LogicalToDeviceUnits(2);

        using var borderPen = new Pen(Color.DarkGray);
        e.Graphics.DrawLines(borderPen, new Point[]
        {
            new Point(captionBounds.Left - gap, borderTop),
            new Point(0, borderTop),
            new Point(0, Height - 1),
            new Point(Width - 1, Height - 1),
            new Point(Width - 1, borderTop),
            new Point(captionBounds.Right + gap, borderTop)
        });
        TextRenderer.DrawText(e.Graphics, Text, Font, captionBounds, ForeColor);
    }
}

internal sealed class MainForm : Form
{
    private const int MenuBarHeight = 24;
    private const int SearchOptionsHeight = 90;

    private readonly ComboBox folderComboBox = new();
    private readonly ComboBox searchComboBox = new();
    private readonly Button browseButton = new();
    private readonly Button searchButton = new();
    private readonly NumericUpDown beforeNumeric = new();
    private readonly NumericUpDown afterNumeric = new();
    private readonly CheckBox showCommandCheckBox = new();
    private readonly CheckBox showSearchOptionsCheckBox = new();
    private readonly Panel searchOptionsPanel = new();
    private readonly CheckBox useRipgrepAllCheckBox = new();
    private readonly CheckBox sortCheckBox = new();
    private readonly ComboBox sortByComboBox = new();
    private readonly ComboBox sortOrderComboBox = new();
    private readonly Label commandLabel = new();
    private readonly TextBox commandTextBox = new();
    private readonly Label outputLabel = new();
    private readonly RichTextBox outputTextBox = new();
    private readonly Label statusLabel = new();
    private readonly ToolStripMenuItem exportMenuItem = new("&Export Results...") { Enabled = false };
    private SearchReport? searchReport;
    private CancellationTokenSource? searchCancellation;
    private bool closeAfterSearch;

    private readonly Dictionary<int, string> fileLineMap = new();
    private readonly string settingsFolder;
    private readonly string settingsFile;
    private readonly AppSettings settings;
    private readonly string? startupFolder;

    public MainForm(string? startupFolder = null, string? settingsDirectory = null)
    {
        this.startupFolder = startupFolder;
        settingsFolder = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RipgrepSearch");
        settingsFile = Path.Combine(settingsFolder, "settings.json");
        settings = LoadSettings();

        Text = "Ripgrep Search";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 710 + MenuBarHeight);
        MinimumSize = new Size(780, 600 + MenuBarHeight);
        Font = new Font("Segoe UI", 9F);

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            Icon = SystemIcons.Application;
        }

        BuildInterface();
        LoadHistory();
        UpdateSearchVisibility();

        AcceptButton = searchButton;
        FormClosing += (_, e) =>
        {
            if (searchCancellation is not null)
            {
                e.Cancel = true;
                closeAfterSearch = true;
                searchCancellation.Cancel();
            }
            else
            {
                SaveCurrentSettings();
            }
        };
        Resize += (_, _) => UpdateSearchVisibility();
    }

    private void BuildInterface()
    {
        var menuStrip = new MenuStrip { AutoSize = false, Height = MenuBarHeight };
        var fileMenu = new ToolStripMenuItem("&File");
        exportMenuItem.ShortcutKeys = Keys.Control | Keys.S;
        exportMenuItem.Click += (_, _) => ExportResults();
        fileMenu.DropDownItems.Add(exportMenuItem);
        menuStrip.Items.Add(fileMenu);
        var helpMenu = new ToolStripMenuItem("&Help");
        var checkDependenciesMenuItem = new ToolStripMenuItem("Check &Dependencies");
        checkDependenciesMenuItem.Click += (_, _) =>
        {
            using var dialog = new DependencyDialog { Icon = Icon };
            dialog.ShowDialog(this);
        };
        helpMenu.DropDownItems.Add(checkDependenciesMenuItem);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        var aboutMenuItem = new ToolStripMenuItem("&About");
        aboutMenuItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.DropDownItems.Add(aboutMenuItem);
        menuStrip.Items.Add(helpMenu);
        MainMenuStrip = menuStrip;

        var folderLabel = new Label
        {
            Text = "Folder:",
            Location = new Point(15, 20),
            AutoSize = true
        };

        folderComboBox.Location = new Point(80, 16);
        folderComboBox.Size = new Size(695, 27);
        folderComboBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        folderComboBox.DropDownStyle = ComboBoxStyle.DropDown;

        browseButton.Text = "Browse...";
        browseButton.Location = new Point(785, 15);
        browseButton.Size = new Size(100, 29);
        browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browseButton.Click += BrowseButton_Click;

        var searchLabel = new Label
        {
            Text = "Search:",
            Location = new Point(15, 60),
            AutoSize = true
        };

        searchComboBox.Location = new Point(80, 56);
        searchComboBox.Size = new Size(695, 27);
        searchComboBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        searchComboBox.DropDownStyle = ComboBoxStyle.DropDown;

        searchButton.Text = "Search";
        searchButton.Location = new Point(785, 55);
        searchButton.Size = new Size(100, 29);
        searchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        searchButton.Click += async (_, _) =>
        {
            if (searchCancellation is not null)
                searchCancellation.Cancel();
            else
                await RunSearchAsync();
        };

        showSearchOptionsCheckBox.Text = "Show search options";
        showSearchOptionsCheckBox.Location = new Point(15, 100);
        showSearchOptionsCheckBox.AutoSize = true;
        showSearchOptionsCheckBox.Checked = settings.ShowSearchOptions;
        showSearchOptionsCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateSearchVisibility();
            SaveCurrentSettings();
        };

        searchOptionsPanel.Location = new Point(0, 136);
        searchOptionsPanel.Size = new Size(900, SearchOptionsHeight);
        searchOptionsPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        searchOptionsPanel.AccessibleName = "Search options";

        var beforeLabel = new Label
        {
            Text = "Before:",
            Location = new Point(15, 7),
            AutoSize = true
        };

        beforeNumeric.Location = new Point(80, 2);
        beforeNumeric.Size = new Size(65, 27);
        beforeNumeric.Minimum = 0;
        beforeNumeric.Maximum = 100;
        beforeNumeric.Value = Math.Clamp(settings.BeforeContext, (int)beforeNumeric.Minimum, (int)beforeNumeric.Maximum);

        var afterLabel = new Label
        {
            Text = "After:",
            Location = new Point(165, 7),
            AutoSize = true
        };

        afterNumeric.Location = new Point(215, 2);
        afterNumeric.Size = new Size(65, 27);
        afterNumeric.Minimum = 0;
        afterNumeric.Maximum = 100;
        afterNumeric.Value = Math.Clamp(settings.AfterContext, (int)afterNumeric.Minimum, (int)afterNumeric.Maximum);

        showCommandCheckBox.Text = "Show command";
        showCommandCheckBox.Location = new Point(180, 100);
        showCommandCheckBox.AutoSize = true;
        showCommandCheckBox.Checked = settings.ShowCommand;
        showCommandCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateSearchVisibility();
            SaveCurrentSettings();
        };

        useRipgrepAllCheckBox.Text = "Search Office/PDF files with rga";
        useRipgrepAllCheckBox.Location = new Point(310, 4);
        useRipgrepAllCheckBox.AutoSize = true;
        useRipgrepAllCheckBox.Checked = settings.UseRipgrepAll;
        useRipgrepAllCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateCommandLabels();
            SaveCurrentSettings();
        };

        var sortingGroup = new SortGroupBox
        {
            Text = "Sort",
            Location = new Point(15, 30),
            Size = new Size(430, 50),
            AccessibleName = "Sorting options"
        };

        sortCheckBox.Text = string.Empty;
        sortCheckBox.AccessibleName = "Enable sorting";
        sortCheckBox.Location = new Point(10, 23);
        sortCheckBox.AutoSize = true;
        sortCheckBox.Checked = settings.SortEnabled;

        sortByComboBox.Location = new Point(64, 19);
        sortByComboBox.Size = new Size(80, 27);
        sortByComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        sortByComboBox.AccessibleName = "Sort by";
        sortByComboBox.Items.AddRange(["path", "modified", "accessed", "created"]);
        int sortByIndex = sortByComboBox.Items.IndexOf(settings.SortBy);
        sortByComboBox.SelectedIndex = sortByIndex >= 0 ? sortByIndex : 0;

        var sortOrderLabel = new Label
        {
            Text = "Order:",
            Location = new Point(150, 26),
            AutoSize = true
        };
        sortOrderComboBox.Location = new Point(200, 19);
        sortOrderComboBox.Size = new Size(90, 27);
        sortOrderComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        sortOrderComboBox.AccessibleName = "Sort order";
        sortOrderComboBox.Items.AddRange(["Ascending", "Descending"]);
        sortOrderComboBox.SelectedIndex = settings.SortDescending ? 1 : 0;
        sortingGroup.Controls.AddRange([
            sortCheckBox, sortByComboBox, sortOrderLabel, sortOrderComboBox
        ]);
        searchOptionsPanel.Controls.AddRange([
            beforeLabel, beforeNumeric, afterLabel, afterNumeric,
            useRipgrepAllCheckBox, sortingGroup
        ]);
        UpdateSortControls();

        // Restore all sorting values before attaching handlers that save settings.
        sortCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateSortControls();
            UpdateCommandPreview();
            SaveCurrentSettings();
        };
        sortByComboBox.SelectedIndexChanged += (_, _) =>
        {
            UpdateCommandPreview();
            SaveCurrentSettings();
        };
        sortOrderComboBox.SelectedIndexChanged += (_, _) =>
        {
            UpdateCommandPreview();
            SaveCurrentSettings();
        };

        commandLabel.Text = "rg command:";
        commandLabel.AutoSize = true;

        commandTextBox.Size = new Size(870, 50);
        commandTextBox.Multiline = true;
        commandTextBox.ReadOnly = true;
        commandTextBox.ScrollBars = ScrollBars.Vertical;
        commandTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        outputLabel.Text = "Output (double-click a blue filename to open it):";
        outputLabel.AutoSize = true;

        outputTextBox.Size = new Size(870, 380);
        outputTextBox.ReadOnly = true;
        outputTextBox.WordWrap = false;
        outputTextBox.DetectUrls = false;
        outputTextBox.Font = new Font("Consolas", 10F);
        outputTextBox.BackColor = Color.Black;
        outputTextBox.ForeColor = Color.White;
        outputTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        outputTextBox.MouseDoubleClick += OutputTextBox_MouseDoubleClick;
        outputTextBox.MouseMove += OutputTextBox_MouseMove;

        statusLabel.Text = "Ready";
        statusLabel.Location = new Point(15, 682);
        statusLabel.AutoSize = true;
        statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        Controls.AddRange([
            folderLabel, folderComboBox, browseButton,
            searchLabel, searchComboBox, searchButton,
            showSearchOptionsCheckBox, showCommandCheckBox, searchOptionsPanel,
            commandLabel, commandTextBox,
            outputLabel, outputTextBox, statusLabel
        ]);

        foreach (Control control in Controls)
            control.Top += MenuBarHeight;

        Controls.Add(menuStrip);

        folderComboBox.TextChanged += (_, _) => UpdateCommandPreview();
        searchComboBox.TextChanged += (_, _) => UpdateCommandPreview();
        beforeNumeric.ValueChanged += (_, _) =>
        {
            UpdateCommandPreview();
            SaveCurrentSettings();
        };
        afterNumeric.ValueChanged += (_, _) =>
        {
            UpdateCommandPreview();
            SaveCurrentSettings();
        };
    }

    private void ExportResults()
    {
        if (searchReport is null || searchReport.Spans.Count == 0)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Export search results",
            Filter = "HTML report (*.html)|*.html|Markdown report (*.md)|*.md",
            FileName = "RipgrepSearch-results",
            AddExtension = true,
            OverwritePrompt = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            string extension = Path.GetExtension(dialog.FileName);
            bool markdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
            if (!markdown && !extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Please use .html or .md for the exported report.");
                return;
            }

            string report = markdown
                ? ResultsExporter.ToMarkdown(searchReport)
                : ResultsExporter.ToHtml(searchReport);
            File.WriteAllText(dialog.FileName, report, new UTF8Encoding(false));
            MessageBox.Show(this, $"Results exported to:\r\n{dialog.FileName}",
                "Export Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError($"The results could not be exported:\r\n{ex.Message}");
        }
    }

    private void ShowAboutDialog()
    {
        using var iconImage = (Icon ?? SystemIcons.Application).ToBitmap();
        using var dialog = new Form
        {
            Text = "About",
            Icon = Icon,
            Font = Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(180, 120),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var iconPictureBox = new PictureBox
        {
            Image = iconImage,
            Location = new Point(10, 10),
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.Zoom,
            AccessibleName = "Ripgrep Search icon",
            TabStop = false
        };
        var versionLabel = new Label
        {
            Text = $"Ripgrep Search\r\nVersion {Application.ProductVersion.Split('+')[0]}",
            Location = new Point(85, 15),
            AutoSize = true
        };
        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(100, 75),
            Size = new Size(70, 29),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        dialog.Controls.AddRange([iconPictureBox, versionLabel, okButton]);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = okButton;
        dialog.ShowDialog(this);
    }

    private void LoadHistory()
    {
        foreach (string folder in settings.FolderHistory.Take(10))
            folderComboBox.Items.Add(folder);

        if (!string.IsNullOrWhiteSpace(startupFolder) && Directory.Exists(startupFolder))
        {
            folderComboBox.Text = startupFolder;

            if (!settings.FolderHistory.Any(item =>
                    string.Equals(item, startupFolder, StringComparison.OrdinalIgnoreCase)))
            {
                folderComboBox.Items.Insert(0, startupFolder);
            }
        }
        else if (folderComboBox.Items.Count > 0)
        {
            folderComboBox.Text = folderComboBox.Items[0]?.ToString() ?? string.Empty;
        }

        foreach (string search in settings.SearchHistory.Take(20))
            searchComboBox.Items.Add(search);

        UpdateCommandLabels();
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder to search",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(folderComboBox.Text)
                ? folderComboBox.Text
                : string.Empty
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            folderComboBox.Text = dialog.SelectedPath;
    }

    private async Task RunSearchAsync()
    {
        if (searchCancellation is not null)
            return;
        string folder = folderComboBox.Text.Trim();
        string searchPattern = searchComboBox.Text;

        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowError("Please select a folder.");
            return;
        }

        if (!Directory.Exists(folder))
        {
            ShowError("The selected folder does not exist.");
            return;
        }

        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            ShowError("Please enter a search word, phrase, or regular expression.");
            return;
        }

        bool useRipgrepAll = useRipgrepAllCheckBox.Checked;
        string? searchExecutablePath = ExecutableResolver.ResolveSearchTool(useRipgrepAll);

        if (searchExecutablePath is null)
        {
            ShowError(useRipgrepAll
                ? "ripgrep-all (rga.exe) was not found. Install ripgrep-all and add rga.exe to your PATH. Some document formats also require their external converter tools."
                : "ripgrep (rg.exe) was not found. Install ripgrep or add rg.exe to your PATH.");
            return;
        }

        List<string> arguments = BuildCurrentArguments();
        UpdateCommandPreview();
        searchReport = new SearchReport(
            Path.GetFullPath(folder), searchPattern, commandTextBox.Text, DateTimeOffset.Now);

        UpdateHistory(folder, searchPattern);
        SaveCurrentSettings();

        outputTextBox.Clear();
        fileLineMap.Clear();
        statusLabel.Text = "Searching...";
        using var cancellation = new CancellationTokenSource();
        searchCancellation = cancellation;
        SetBusy(true);

        var channel = Channel.CreateBounded<IReadOnlyList<SearchResultLine>>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        Task<SearchOutcome> runTask = Task.Run(() => SearchRunner.RunAsync(
            searchExecutablePath, arguments, Environment.CurrentDirectory, channel.Writer, cancellation.Token));
        SearchOutcome outcome;
        try
        {
            await RenderResultsAsync(channel.Reader, cancellation.Token);
            outcome = await runTask;
            if (cancellation.IsCancellationRequested && outcome.Completion != SearchCompletion.Failed)
                outcome = outcome with { Completion = SearchCompletion.Cancelled, Reason = "Search cancelled." };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            outcome = await runTask;
            if (outcome.Completion != SearchCompletion.Failed)
                outcome = outcome with { Completion = SearchCompletion.Cancelled, Reason = "Search cancelled." };
        }
        catch (Exception ex)
        {
            cancellation.Cancel();
            await runTask;
            outcome = new SearchOutcome(SearchCompletion.Failed, null, string.Empty,
                $"Could not display search results: {ex.Message}");
        }

        try
        {
            int matchingLines = searchReport.Results.Count(line => line.IsMatch);
            statusLabel.Text = DescribeOutcome(outcome, matchingLines);
            if (searchReport.Results.Count == 0 && outcome.Completion == SearchCompletion.Completed)
                WritePlainOutput("No matches found.");
            if (outcome.Completion != SearchCompletion.Completed)
                AppendColoredText($"\r\n{outcome.Reason}\r\n", Color.Gray);
            if (!string.IsNullOrWhiteSpace(outcome.Diagnostics))
                AppendColoredText($"\r\nDiagnostics:\r\n{outcome.Diagnostics.TrimEnd()}\r\n", Color.Gray);
            searchReport.Status = statusLabel.Text;
            outputTextBox.Select(0, 0);
            outputTextBox.ScrollToCaret();
        }
        finally
        {
            searchCancellation = null;
            SetBusy(false);
            if (closeAfterSearch)
                BeginInvoke(new Action(Close));
        }
    }

    internal static string DescribeOutcome(SearchOutcome outcome, int matchingLines)
    {
        string status = outcome.Completion switch
        {
            SearchCompletion.Completed when matchingLines == 0 => "Search completed. No matches found.",
            SearchCompletion.Completed => $"Search completed. {matchingLines} matching line(s) found.",
            SearchCompletion.Cancelled => $"Search cancelled. {matchingLines} matching line(s) shown; results are incomplete.",
            SearchCompletion.Limited => $"Display limit reached. {matchingLines} matching line(s) shown; search stopped.",
            _ => $"Search failed{(outcome.ExitCode is null ? "" : $" with exit code {outcome.ExitCode}")}. {matchingLines} matching line(s) shown; results may be incomplete."
        };
        return outcome.Completion == SearchCompletion.Completed && !string.IsNullOrWhiteSpace(outcome.Diagnostics)
            ? status + " Warnings reported."
            : status;
    }

    private List<string> BuildCurrentArguments()
    {
        return SearchRunner.BuildArguments(
            searchComboBox.Text,
            folderComboBox.Text.Trim(),
            (int)beforeNumeric.Value,
            (int)afterNumeric.Value,
            sortCheckBox.Checked ? sortByComboBox.Text : null,
            sortOrderComboBox.SelectedIndex == 1);
    }

    private static string BuildDisplayCommand(
        string executableName,
        IEnumerable<string> arguments)
    {
        return executableName + " " + string.Join(" ", arguments.Select(QuoteForDisplay));
    }

    private static string QuoteForDisplay(string argument)
    {
        bool needsQuotes = argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"');
        if (!needsQuotes)
            return argument;

        return '"' + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
    }

    private async Task RenderResultsAsync(ChannelReader<IReadOnlyList<SearchResultLine>> reader,
        CancellationToken cancellationToken)
    {
        var formatter = new SearchResultFormatter();
        int matchingLines = 0;
        await foreach (IReadOnlyList<SearchResultLine> batch in reader.ReadAllAsync(cancellationToken))
        {
            var formatted = await Task.Run(() =>
            {
                var spans = new List<ResultSpan>();
                foreach (SearchResultLine line in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    spans.AddRange(formatter.Format(line));
                }
                return (Spans: spans, Rtf: ResultRtfBuilder.Build(spans));
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int insertionStart = outputTextBox.TextLength;
            outputTextBox.Select(outputTextBox.TextLength, 0);
            outputTextBox.SelectedRtf = formatted.Rtf;
            int textOffset = 0;
            foreach (ResultSpan span in formatted.Spans)
            {
                if (span.IsFileHeading && span.FilePath is not null)
                    fileLineMap[outputTextBox.GetLineFromCharIndex(insertionStart + textOffset)] = span.FilePath;
                // RichEdit stores each CRLF paragraph terminator as one character.
                textOffset += span.Text.Length;
                for (int i = 0; i + 1 < span.Text.Length; i++)
                    if (span.Text[i] == '\r' && span.Text[i + 1] == '\n')
                        textOffset--;
            }
            searchReport!.Results.AddRange(batch);
            searchReport.Spans.AddRange(formatted.Spans);
            matchingLines += batch.Count(line => line.IsMatch);
            statusLabel.Text = $"Searching... {matchingLines} matching line(s) shown.";
        }
    }

    private void WritePlainOutput(string text)
    {
        outputTextBox.Clear();
        fileLineMap.Clear();
        searchReport?.Spans.Clear();
        AppendColoredText(text, Color.White);
    }

    private void AppendColoredText(
        string text,
        Color color,
        FontStyle style = FontStyle.Regular,
        string? filePath = null)
    {
        if (filePath is not null)
        {
            try
            {
                filePath = Path.GetFullPath(filePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Keep the displayed heading even when its path cannot be resolved.
            }
        }
        searchReport?.Spans.Add(new ResultSpan(text, color, style, filePath));
        int start = outputTextBox.TextLength;
        outputTextBox.AppendText(text);
        outputTextBox.Select(start, text.Length);
        outputTextBox.SelectionColor = color;
        using var font = new Font(outputTextBox.Font, style);
        outputTextBox.SelectionFont = font;
        outputTextBox.Select(outputTextBox.TextLength, 0);
    }

    private void OutputTextBox_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        int characterIndex = outputTextBox.GetCharIndexFromPosition(e.Location);
        int lineIndex = outputTextBox.GetLineFromCharIndex(characterIndex);

        if (!fileLineMap.TryGetValue(lineIndex, out string? filePath))
            return;

        if (!File.Exists(filePath))
        {
            ShowError($"The file could not be found:\r\n{filePath}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"The file could not be opened:\r\n{filePath}\r\n\r\n{ex.Message}");
        }
    }

    private void OutputTextBox_MouseMove(object? sender, MouseEventArgs e)
    {
        int characterIndex = outputTextBox.GetCharIndexFromPosition(e.Location);
        int lineIndex = outputTextBox.GetLineFromCharIndex(characterIndex);
        outputTextBox.Cursor = fileLineMap.ContainsKey(lineIndex)
            ? Cursors.Hand
            : Cursors.IBeam;
    }

    private void UpdateHistory(string folder, string searchPattern)
    {
        settings.FolderHistory = [
            folder,
            .. settings.FolderHistory
                .Where(item => !string.Equals(item, folder, StringComparison.OrdinalIgnoreCase))
                .Take(9)
        ];

        settings.SearchHistory = [
            searchPattern,
            .. settings.SearchHistory
                .Where(item => !string.Equals(item, searchPattern, StringComparison.Ordinal))
                .Take(19)
        ];

        RepopulateComboBox(folderComboBox, settings.FolderHistory, folder);
        RepopulateComboBox(searchComboBox, settings.SearchHistory, searchPattern);
    }

    private static void RepopulateComboBox(
        ComboBox comboBox,
        IEnumerable<string> items,
        string currentText)
    {
        comboBox.BeginUpdate();
        comboBox.Items.Clear();
        foreach (string item in items)
            comboBox.Items.Add(item);
        comboBox.Text = currentText;
        comboBox.EndUpdate();
    }

    private void UpdateCommandLabels()
    {
        string executableName = useRipgrepAllCheckBox.Checked ? "rga" : "rg";
        commandLabel.Text = $"{executableName} command:";
        UpdateCommandPreview();
    }

    private void UpdateCommandPreview()
    {
        string executableName = useRipgrepAllCheckBox.Checked ? "rga" : "rg";
        commandTextBox.Text = BuildDisplayCommand(executableName, BuildCurrentArguments());
    }

    private void UpdateSortControls()
    {
        bool enabled = sortCheckBox.Enabled && sortCheckBox.Checked;
        sortByComboBox.Enabled = enabled;
        sortOrderComboBox.Enabled = enabled;
    }

    private void UpdateSearchVisibility()
    {
        SuspendLayout();
        try
        {
            searchOptionsPanel.Visible = showSearchOptionsCheckBox.Checked;
            commandLabel.Visible = showCommandCheckBox.Checked;
            commandTextBox.Visible = showCommandCheckBox.Checked;

            int nextTop = searchOptionsPanel.Top;
            if (showSearchOptionsCheckBox.Checked)
                nextTop += searchOptionsPanel.Height;

            commandLabel.Location = new Point(15, nextTop);
            commandTextBox.Location = new Point(15, nextTop + 22);
            if (showCommandCheckBox.Checked)
                nextTop += 85;

            outputLabel.Location = new Point(15, nextTop);
            int outputTop = nextTop + 22;
            outputTextBox.Location = new Point(15, outputTop);
            outputTextBox.Size = new Size(
                Math.Max(200, ClientSize.Width - 30),
                Math.Max(150, ClientSize.Height - outputTop - 41));
        }
        finally
        {
            ResumeLayout();
        }
    }

    private void SetBusy(bool busy)
    {
        searchButton.Enabled = true;
        searchButton.Text = busy ? "Cancel" : "Search";
        browseButton.Enabled = !busy;
        folderComboBox.Enabled = !busy;
        searchComboBox.Enabled = !busy;
        beforeNumeric.Enabled = !busy;
        afterNumeric.Enabled = !busy;
        useRipgrepAllCheckBox.Enabled = !busy;
        sortCheckBox.Enabled = !busy;
        exportMenuItem.Enabled = !busy && searchReport is { Spans.Count: > 0 };
        UpdateSortControls();
        outputTextBox.UseWaitCursor = busy;
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsFile))
                return new AppSettings();

            string json = File.ReadAllText(settingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveCurrentSettings()
    {
        settings.ShowCommand = showCommandCheckBox.Checked;
        settings.ShowSearchOptions = showSearchOptionsCheckBox.Checked;
        settings.BeforeContext = (int)beforeNumeric.Value;
        settings.AfterContext = (int)afterNumeric.Value;
        settings.UseRipgrepAll = useRipgrepAllCheckBox.Checked;
        settings.SortEnabled = sortCheckBox.Checked;
        settings.SortBy = sortByComboBox.Text;
        settings.SortDescending = sortOrderComboBox.SelectedIndex == 1;

        string currentFolder = folderComboBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(currentFolder))
        {
            settings.FolderHistory = [
                currentFolder,
                .. settings.FolderHistory
                    .Where(item => !string.Equals(item, currentFolder, StringComparison.OrdinalIgnoreCase))
                    .Take(9)
            ];
        }

        string currentSearch = searchComboBox.Text;
        if (!string.IsNullOrWhiteSpace(currentSearch))
        {
            settings.SearchHistory = [
                currentSearch,
                .. settings.SearchHistory
                    .Where(item => !string.Equals(item, currentSearch, StringComparison.Ordinal))
                    .Take(19)
            ];
        }

        try
        {
            Directory.CreateDirectory(settingsFolder);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(settingsFile, json);
        }
        catch
        {
            // Settings persistence should never prevent the search tool from working.
        }
    }

    private void ShowError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Ripgrep Search",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
