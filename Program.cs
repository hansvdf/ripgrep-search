using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public bool UseRipgrepAll { get; set; } = false;
}

internal sealed class MainForm : Form
{
    private readonly ComboBox folderComboBox = new();
    private readonly ComboBox searchComboBox = new();
    private readonly Button browseButton = new();
    private readonly Button searchButton = new();
    private readonly NumericUpDown beforeNumeric = new();
    private readonly NumericUpDown afterNumeric = new();
    private readonly CheckBox showCommandCheckBox = new();
    private readonly CheckBox useRipgrepAllCheckBox = new();
    private readonly Label commandLabel = new();
    private readonly TextBox commandTextBox = new();
    private readonly Label outputLabel = new();
    private readonly RichTextBox outputTextBox = new();
    private readonly Label statusLabel = new();

    private readonly Dictionary<int, string> fileLineMap = new();
    private readonly string settingsFolder;
    private readonly string settingsFile;
    private readonly AppSettings settings;
    private readonly string? startupFolder;

    public MainForm(string? startupFolder = null)
    {
        this.startupFolder = startupFolder;
        settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RipgrepSearch");
        settingsFile = Path.Combine(settingsFolder, "settings.json");
        settings = LoadSettings();

        Text = "Ripgrep Search v8";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 670);
        MinimumSize = new Size(780, 560);
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
        UpdateCommandVisibility();

        AcceptButton = searchButton;
        FormClosing += (_, _) => SaveCurrentSettings();
        Resize += (_, _) => UpdateCommandVisibility();
    }

    private void BuildInterface()
    {
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
        searchButton.Click += async (_, _) => await RunSearchAsync();

        var beforeLabel = new Label
        {
            Text = "Before:",
            Location = new Point(15, 103),
            AutoSize = true
        };

        beforeNumeric.Location = new Point(80, 98);
        beforeNumeric.Size = new Size(65, 27);
        beforeNumeric.Minimum = 0;
        beforeNumeric.Maximum = 100;

        var afterLabel = new Label
        {
            Text = "After:",
            Location = new Point(165, 103),
            AutoSize = true
        };

        afterNumeric.Location = new Point(215, 98);
        afterNumeric.Size = new Size(65, 27);
        afterNumeric.Minimum = 0;
        afterNumeric.Maximum = 100;

        showCommandCheckBox.Text = "Show command";
        showCommandCheckBox.Location = new Point(310, 100);
        showCommandCheckBox.AutoSize = true;
        showCommandCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateCommandVisibility();
            SaveCurrentSettings();
        };

        useRipgrepAllCheckBox.Text = "Search Office/PDF files with rga";
        useRipgrepAllCheckBox.Location = new Point(445, 100);
        useRipgrepAllCheckBox.AutoSize = true;
        useRipgrepAllCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateCommandLabels();
            SaveCurrentSettings();
        };

        commandLabel.Text = "rg command:";
        commandLabel.Location = new Point(15, 142);
        commandLabel.AutoSize = true;

        commandTextBox.Location = new Point(15, 164);
        commandTextBox.Size = new Size(870, 50);
        commandTextBox.Multiline = true;
        commandTextBox.ReadOnly = true;
        commandTextBox.ScrollBars = ScrollBars.Vertical;
        commandTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        outputLabel.Text = "Output (double-click a blue filename to open it):";
        outputLabel.Location = new Point(15, 227);
        outputLabel.AutoSize = true;

        outputTextBox.Location = new Point(15, 249);
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
        statusLabel.Location = new Point(15, 642);
        statusLabel.AutoSize = true;
        statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        Controls.AddRange([
            folderLabel, folderComboBox, browseButton,
            searchLabel, searchComboBox, searchButton,
            beforeLabel, beforeNumeric, afterLabel, afterNumeric,
            showCommandCheckBox, useRipgrepAllCheckBox, commandLabel, commandTextBox,
            outputLabel, outputTextBox, statusLabel
        ]);
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

        showCommandCheckBox.Checked = settings.ShowCommand;
        useRipgrepAllCheckBox.Checked = settings.UseRipgrepAll;
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
        string executableName = useRipgrepAll ? "rga" : "rg";
        string? searchExecutablePath = useRipgrepAll
            ? FindExecutable("rga.exe") ?? FindExecutable("rga")
            : FindExecutable("rg.exe") ?? FindExecutable("rg");

        if (searchExecutablePath is null)
        {
            ShowError(useRipgrepAll
                ? "ripgrep-all (rga.exe) was not found. Install ripgrep-all and add rga.exe to your PATH. Some document formats also require their external converter tools."
                : "ripgrep (rg.exe) was not found. Install ripgrep or add rg.exe to your PATH.");
            return;
        }

        int before = (int)beforeNumeric.Value;
        int after = (int)afterNumeric.Value;
        List<string> arguments = BuildArguments(searchPattern, folder, before, after);
        commandTextBox.Text = BuildDisplayCommand(executableName, arguments);

        UpdateHistory(folder, searchPattern);
        SaveCurrentSettings();

        outputTextBox.Clear();
        fileLineMap.Clear();
        statusLabel.Text = "Searching...";
        SetBusy(true);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = searchExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            string output = await outputTask;
            string error = await errorTask;

            switch (process.ExitCode)
            {
                case 0:
                    WriteSearchResults(output, searchPattern);
                    int matchCount = Regex.Matches(output, @"(?m)^\d+:").Count;
                    statusLabel.Text = $"Search completed. {matchCount} matching line(s) found.";
                    break;
                case 1:
                    outputTextBox.ForeColor = Color.White;
                    outputTextBox.Text = "No matches found.";
                    statusLabel.Text = "Search completed. No matches found.";
                    break;
                default:
                    outputTextBox.ForeColor = Color.White;
                    outputTextBox.Text = string.IsNullOrWhiteSpace(error)
                        ? $"Search failed with exit code {process.ExitCode}."
                        : error;
                    statusLabel.Text = $"Search failed with exit code {process.ExitCode}.";
                    break;
            }
        }
        catch (Exception ex)
        {
            outputTextBox.ForeColor = Color.White;
            outputTextBox.Text = ex.Message;
            statusLabel.Text = "Search failed.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static List<string> BuildArguments(
        string searchPattern,
        string folder,
        int before,
        int after)
    {
        var arguments = new List<string>
        {
            "--heading",
            "--line-number",
            "--color",
            "never"
        };

        if (before > 0)
        {
            arguments.Add("--before-context");
            arguments.Add(before.ToString());
        }

        if (after > 0)
        {
            arguments.Add("--after-context");
            arguments.Add(after.ToString());
        }

        arguments.Add("--");
        arguments.Add(searchPattern);
        arguments.Add(folder);
        return arguments;
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

    private void WriteSearchResults(string output, string searchPattern)
    {
        outputTextBox.Clear();
        fileLineMap.Clear();

        Color fileColor = Color.DeepSkyBlue;
        Color matchingLineNumberColor = Color.LimeGreen;
        Color contextLineNumberColor = Color.DarkGray;
        Color textColor = Color.White;
        Color matchColor = Color.OrangeRed;
        Color separatorColor = Color.Gray;

        bool firstFile = true;
        string[] lines = Regex.Split(output, "\\r?\\n");

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line == "--")
            {
                AppendColoredText("  --\r\n", separatorColor);
                continue;
            }

            Match resultLine = Regex.Match(line, @"^(\d+)([:-])(.*)$");
            if (!resultLine.Success)
            {
                if (!firstFile)
                    AppendColoredText("\r\n", textColor);

                int richTextLine = outputTextBox.GetLineFromCharIndex(outputTextBox.TextLength);
                fileLineMap[richTextLine] = line;
                AppendColoredText(
                    line + "\r\n",
                    fileColor,
                    FontStyle.Bold | FontStyle.Underline);
                firstFile = false;
                continue;
            }

            string lineNumber = resultLine.Groups[1].Value;
            string separator = resultLine.Groups[2].Value;
            string content = resultLine.Groups[3].Value;

            AppendColoredText(
                $"  {lineNumber}{separator} ",
                separator == ":" ? matchingLineNumberColor : contextLineNumberColor);

            if (separator == ":")
                WriteHighlightedContent(content, searchPattern, textColor, matchColor);
            else
                AppendColoredText(content + "\r\n", textColor);
        }

        outputTextBox.Select(0, 0);
        outputTextBox.ScrollToCaret();
    }

    private void WriteHighlightedContent(
        string content,
        string searchPattern,
        Color textColor,
        Color matchColor)
    {
        MatchCollection matches;
        try
        {
            matches = Regex.Matches(content, searchPattern);
        }
        catch (ArgumentException)
        {
            AppendColoredText(content + "\r\n", textColor);
            return;
        }

        if (matches.Count == 0)
        {
            AppendColoredText(content + "\r\n", textColor);
            return;
        }

        int position = 0;
        foreach (Match match in matches)
        {
            if (match.Length == 0)
                continue;

            if (match.Index > position)
                AppendColoredText(content[position..match.Index], textColor);

            AppendColoredText(match.Value, matchColor, FontStyle.Bold);
            position = match.Index + match.Length;
        }

        if (position < content.Length)
            AppendColoredText(content[position..], textColor);

        AppendColoredText("\r\n", textColor);
    }

    private void AppendColoredText(
        string text,
        Color color,
        FontStyle style = FontStyle.Regular)
    {
        int start = outputTextBox.TextLength;
        outputTextBox.AppendText(text);
        outputTextBox.Select(start, text.Length);
        outputTextBox.SelectionColor = color;
        outputTextBox.SelectionFont = new Font(outputTextBox.Font, style);
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
    }

    private void UpdateCommandVisibility()
    {
        bool visible = showCommandCheckBox.Checked;
        commandLabel.Visible = visible;
        commandTextBox.Visible = visible;

        int outputTop = visible ? 249 : 164;
        outputLabel.Location = new Point(15, visible ? 227 : 142);
        outputTextBox.Location = new Point(15, outputTop);
        outputTextBox.Size = new Size(
            Math.Max(200, ClientSize.Width - 30),
            Math.Max(150, ClientSize.Height - outputTop - 41));
    }

    private void SetBusy(bool busy)
    {
        searchButton.Enabled = !busy;
        browseButton.Enabled = !busy;
        folderComboBox.Enabled = !busy;
        searchComboBox.Enabled = !busy;
        beforeNumeric.Enabled = !busy;
        afterNumeric.Enabled = !busy;
        useRipgrepAllCheckBox.Enabled = !busy;
        UseWaitCursor = busy;
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
        settings.UseRipgrepAll = useRipgrepAllCheckBox.Checked;

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

    private static string? FindExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) && File.Exists(executable))
            return executable;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
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
