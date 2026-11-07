using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RipgrepSearch;

internal sealed class DependencyDialog : Form
{
    private readonly DataGridView dependencyGrid = new();
    private readonly TextBox detailsTextBox = new();
    private readonly Label summaryLabel = new();
    private readonly LinkLabel detailsToggle = new();
    private readonly LinkLabel rgLink;
    private readonly LinkLabel rgaLink;
    private readonly Button refreshButton = new();
    private readonly Button closeButton = new();
    private readonly Button copyDiagnosticsButton = new();
    private readonly Label copyFeedbackLabel = new();
    private readonly System.Windows.Forms.Timer copyFeedbackTimer = new() { Interval = 5_000 };
    private readonly ToolTip refreshToolTip = new() { AutoPopDelay = 10_000 };
    private readonly Func<CancellationToken, Task<DependencyCheckResult[]>> checkDependencies;
    private readonly Action<string> copyToClipboard;
    private DependencyCheckResult[] lastResults = [];
    private CancellationTokenSource? activeCancellation;
    private bool closing;
    private bool detailsExpanded;

    public DependencyDialog(Func<CancellationToken, Task<DependencyCheckResult[]>>? checkDependencies = null,
        Action<string>? copyToClipboard = null)
    {
        this.checkDependencies = checkDependencies ?? DependencyChecker.CheckAllAsync;
        this.copyToClipboard = copyToClipboard ?? (text => Clipboard.SetText(text, TextDataFormat.UnicodeText));
        Text = "Check Dependencies";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        dependencyGrid.Location = new Point(15, 15);
        dependencyGrid.Size = new Size(870, 100);
        dependencyGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        dependencyGrid.AccessibleName = "Dependency status";
        dependencyGrid.ReadOnly = true;
        dependencyGrid.AllowUserToAddRows = false;
        dependencyGrid.AllowUserToDeleteRows = false;
        dependencyGrid.AllowUserToResizeRows = false;
        dependencyGrid.RowHeadersVisible = false;
        dependencyGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        dependencyGrid.MultiSelect = false;
        dependencyGrid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        dependencyGrid.BackgroundColor = SystemColors.Window;
        dependencyGrid.BorderStyle = BorderStyle.FixedSingle;
        dependencyGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        dependencyGrid.Font = Font;
        dependencyGrid.CellPainting += PaintStatusCell;
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dependency", HeaderText = "Dependency", Width = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 95, SortMode = DataGridViewColumnSortMode.NotSortable });
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Version", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExecutablePath", HeaderText = "Executable path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 250, SortMode = DataGridViewColumnSortMode.NotSortable });

        foreach (DependencyDefinition dependency in DependencyChecker.Dependencies)
            dependencyGrid.Rows.Add(dependency.DisplayName, "Checking…", "—", "—");
        dependencyGrid.AutoResizeRows();
        dependencyGrid.Height = dependencyGrid.ColumnHeadersHeight +
            dependencyGrid.Rows.GetRowsHeight(DataGridViewElementStates.Visible) + 2;
        int contentOffset = dependencyGrid.Height - 100;
        ClientSize = new Size(900, 275 + contentOffset);
        MinimumSize = new Size(780, 270 + contentOffset);

        summaryLabel.Location = new Point(15, 130 + contentOffset);
        summaryLabel.Size = new Size(750, 25);
        summaryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        summaryLabel.AccessibleName = "Dependency summary";
        summaryLabel.Text = "Checking dependencies…";
        detailsToggle.Text = "Show details";
        detailsToggle.Location = new Point(785, 130 + contentOffset);
        detailsToggle.AutoSize = true;
        detailsToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        detailsToggle.Visible = false;
        detailsToggle.LinkClicked += (_, _) => SetDetailsExpanded(!detailsExpanded);

        detailsTextBox.Location = new Point(15, 165 + contentOffset);
        detailsTextBox.Size = new Size(870, 85);
        detailsTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        detailsTextBox.Multiline = true;
        detailsTextBox.ReadOnly = true;
        detailsTextBox.ScrollBars = ScrollBars.Vertical;
        detailsTextBox.AccessibleName = "Dependency check details";
        detailsTextBox.Visible = false;

        var noteLabel = new Label
        {
            Name = "dependencyNote",
            Text = "rg provides text searches and rga's search backend. rga adds document conversion; converters are not verified here.",
            Location = new Point(15, 165 + contentOffset),
            Size = new Size(870, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        rgLink = InstallationLink("Install ripgrep", 15, DependencyChecker.Dependencies[0].InstallationUrl);
        rgaLink = InstallationLink("Install ripgrep-all", 125, DependencyChecker.Dependencies[1].InstallationUrl);
        rgLink.Top += contentOffset;
        rgaLink.Top += contentOffset;

        refreshButton.Text = "Refresh";
        refreshButton.Location = new Point(675, 230 + contentOffset);
        refreshButton.Size = new Size(100, 29);
        refreshButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        refreshToolTip.SetToolTip(refreshButton,
            "Recheck dependencies and detect PATH changes without restarting.");
        refreshButton.Click += async (_, _) => await RefreshDependenciesAsync();
        closeButton.Text = "Close";
        closeButton.Location = new Point(785, 230 + contentOffset);
        closeButton.Size = new Size(100, 29);
        closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        closeButton.DialogResult = DialogResult.Cancel;
        closeButton.Click += (_, _) => Close();
        copyDiagnosticsButton.Text = "Copy Diagnostics";
        copyDiagnosticsButton.Location = new Point(15, 230 + contentOffset);
        copyDiagnosticsButton.Size = new Size(140, 29);
        copyDiagnosticsButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        copyDiagnosticsButton.Enabled = false;
        copyDiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        refreshToolTip.SetToolTip(copyDiagnosticsButton,
            "Copy dependency and system information for troubleshooting.");
        copyFeedbackLabel.Location = new Point(170, 230 + contentOffset);
        copyFeedbackLabel.Size = new Size(485, 29);
        copyFeedbackLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        copyFeedbackLabel.TextAlign = ContentAlignment.MiddleLeft;
        copyFeedbackLabel.AccessibleName = "Clipboard feedback";
        copyFeedbackTimer.Tick += (_, _) =>
        {
            copyFeedbackTimer.Stop();
            copyFeedbackLabel.Text = string.Empty;
        };
        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.AddRange([dependencyGrid, summaryLabel, detailsToggle, detailsTextBox, noteLabel,
            rgLink, rgaLink, copyDiagnosticsButton, copyFeedbackLabel, refreshButton, closeButton]);
        Shown += async (_, _) => await RefreshDependenciesAsync();
        FormClosing += (_, _) =>
        {
            closing = true;
            activeCancellation?.Cancel();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            copyFeedbackTimer.Dispose();
            refreshToolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SetDetailsExpanded(bool expanded)
    {
        if (detailsExpanded == expanded)
            return;
        int spacing = detailsTextBox.Top - summaryLabel.Bottom;
        int heightChange = (detailsTextBox.Height + spacing) * (expanded ? 1 : -1);
        Size targetSize = new(ClientSize.Width, ClientSize.Height + heightChange);
        MinimumSize = new Size(MinimumSize.Width, MinimumSize.Height + heightChange);
        ClientSize = targetSize;
        detailsExpanded = expanded;
        detailsTextBox.Visible = expanded;
        detailsToggle.Text = expanded ? "Hide details" : "Show details";
    }

    internal async Task RefreshDependenciesAsync()
    {
        if (closing || activeCancellation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        activeCancellation = cancellation;
        refreshButton.Enabled = false;
        copyFeedbackTimer.Stop();
        copyFeedbackLabel.Text = string.Empty;
        SetDetailsExpanded(false);
        detailsToggle.Visible = false;
        rgLink.Visible = false;
        rgaLink.Visible = false;
        detailsTextBox.Clear();
        summaryLabel.Text = "Checking dependencies…";
        summaryLabel.ForeColor = SystemColors.ControlText;
        dependencyGrid.Rows.Clear();
        foreach (DependencyDefinition dependency in DependencyChecker.Dependencies)
            dependencyGrid.Rows.Add(dependency.DisplayName, "Checking…", "—", "—");
        try
        {
            DependencyCheckResult[] results = await checkDependencies(cancellation.Token);
            if (!closing && !IsDisposed)
                ShowResults(results);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed)
                ShowResults(DependencyChecker.Dependencies.Select(dependency =>
                    new DependencyCheckResult(dependency, DependencyStatus.Error, null, null,
                        $"Could not check dependencies: {ex.Message}")).ToArray());
        }
        finally
        {
            activeCancellation = null;
            if (!closing && !IsDisposed)
                refreshButton.Enabled = true;
        }
    }

    private void ShowResults(IReadOnlyList<DependencyCheckResult> results)
    {
        lastResults = results.ToArray();
        copyDiagnosticsButton.Enabled = lastResults.Length > 0;
        dependencyGrid.Rows.Clear();
        foreach (DependencyCheckResult result in results)
        {
            LinkLabel installationLink = result.Dependency.UseRipgrepAll ? rgaLink : rgLink;
            installationLink.Visible = result.Status == DependencyStatus.NotFound;
            string status = result.Status == DependencyStatus.NotFound ? "Not found" : result.Status.ToString();
            int index = dependencyGrid.Rows.Add(result.Dependency.DisplayName, status,
                result.Version ?? "—", result.ExecutablePath ?? "—");
            dependencyGrid.Rows[index].Cells["Status"].Tag = result.Status;
            dependencyGrid.Rows[index].Cells["Status"].Style.ForeColor = result.Status switch
            {
                DependencyStatus.Installed => Color.DarkGreen,
                DependencyStatus.NotFound => Color.DarkOrange,
                _ => Color.Firebrick
            };
            dependencyGrid.Rows[index].Cells["ExecutablePath"].ToolTipText = result.ExecutablePath ?? result.Details;
        }
        DependencyCheckResult[] problems = results.Where(result => result.Status != DependencyStatus.Installed).ToArray();
        summaryLabel.Text = problems.Length == 0
            ? "✅ All dependencies are available."
            : problems.Any(result => result.Status == DependencyStatus.Error)
                ? "⚠ Some dependencies could not be checked."
                : "⚠ Some dependencies are missing.";
        summaryLabel.ForeColor = problems.Length == 0 ? Color.DarkGreen : Color.Firebrick;
        detailsToggle.Visible = problems.Length > 0;
        detailsTextBox.Text = string.Join("\r\n\r\n", problems.Select(result =>
            $"{result.Dependency.DisplayName}: {result.Details}"));
    }

    private void CopyDiagnostics()
    {
        if (lastResults.Length == 0)
            return;
        copyFeedbackTimer.Stop();
        try
        {
            copyToClipboard(CreateDiagnosticsReport());
            copyFeedbackLabel.Text = "Diagnostics copied to clipboard.";
            copyFeedbackLabel.ForeColor = Color.DarkGreen;
        }
        catch (Exception)
        {
            copyFeedbackLabel.Text = "Could not copy diagnostics. Please try again.";
            copyFeedbackLabel.ForeColor = Color.Firebrick;
        }
        copyFeedbackTimer.Start();
    }

    private string CreateDiagnosticsReport()
    {
        var report = new StringBuilder();
        report.AppendLine("Ripgrep Search — Dependency Diagnostics");
        report.AppendLine($"Application version: {Application.ProductVersion.Split('+')[0]}");
        report.AppendLine($"Windows version: {RuntimeInformation.OSDescription}");
        foreach (DependencyCheckResult result in lastResults)
        {
            report.AppendLine();
            report.AppendLine(result.Dependency.DisplayName);
            string status = result.Status == DependencyStatus.NotFound ? "Not found" : result.Status.ToString();
            report.AppendLine($"Status: {status}");
            report.AppendLine($"Version: {result.Version ?? "Not available"}");
            report.AppendLine($"Executable path: {result.ExecutablePath ?? "Not available"}");
            if (result.Status != DependencyStatus.Installed && !string.IsNullOrWhiteSpace(result.Details))
            {
                report.AppendLine("Diagnostics:");
                report.AppendLine(result.Details);
            }
        }
        return RedactUserProfile(report.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static string RedactUserProfile(string text, string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile))
            return text;
        string profile = userProfile.TrimEnd('\\', '/');
        if (profile.Length == 0)
            return text;
        string pathPattern = Regex.Escape(profile.Replace('\\', '/')).Replace("/", @"[\\/]+");
        // Match whole directory names, allowing either separator and Windows casing.
        // Keep the following delimiter so quoted paths and messages retain their format.
        return Regex.Replace(text, pathPattern + @"([\\/\s""'<>)\]},;:]|$)",
            match => "%USERPROFILE%" + match.Groups[1].Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private void PaintStatusCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != dependencyGrid.Columns["Status"]!.Index ||
            dependencyGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag is not DependencyStatus status ||
            e.Graphics is null || e.CellStyle is null)
            return;

        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        int iconSize = Math.Min(16 * dependencyGrid.DeviceDpi / 96, e.CellBounds.Height - 4);
        int spacing = Math.Max(3, 4 * dependencyGrid.DeviceDpi / 96);
        var iconBounds = new Rectangle(e.CellBounds.Left + spacing,
            e.CellBounds.Top + (e.CellBounds.Height - iconSize) / 2, iconSize, iconSize);
        GraphicsState savedState = e.Graphics.Save();
        try
        {
            e.Graphics.SetClip(Rectangle.Intersect(e.ClipBounds, e.CellBounds), CombineMode.Intersect);
            DrawStatusIcon(e.Graphics, iconBounds, status);
            var textBounds = new Rectangle(iconBounds.Right + spacing, e.CellBounds.Top,
                Math.Max(0, e.CellBounds.Right - iconBounds.Right - spacing * 2), e.CellBounds.Height);
            Color textColor = (e.State & DataGridViewElementStates.Selected) != 0
                ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
            TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? string.Empty,
                e.CellStyle.Font ?? dependencyGrid.Font, textBounds, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
        finally { e.Graphics.Restore(savedState); }
        e.Handled = true;
    }

    private static void DrawStatusIcon(Graphics graphics, Rectangle bounds, DependencyStatus status)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float x = bounds.Left + 1;
        float y = bounds.Top + 1;
        float size = bounds.Width - 2;
        PointF At(float horizontal, float vertical) => new(x + size * horizontal, y + size * vertical);
        using var whitePen = new Pen(Color.White, Math.Max(1.5F, size / 7));
        using var errorPen = new Pen(Color.Firebrick, Math.Max(1.5F, size / 8));
        if (status == DependencyStatus.Installed)
        {
            using var greenBrush = new SolidBrush(Color.ForestGreen);
            graphics.FillRectangle(greenBrush, x, y, size, size);
            graphics.DrawLines(whitePen, [At(0.18F, 0.5F), At(0.4F, 0.73F), At(0.82F, 0.25F)]);
        }
        else if (status == DependencyStatus.NotFound)
        {
            using var orangeBrush = new SolidBrush(Color.DarkOrange);
            graphics.FillPolygon(orangeBrush, [At(0.5F, 0), At(1, 1), At(0, 1)]);
            graphics.DrawLine(whitePen, At(0.5F, 0.33F), At(0.5F, 0.63F));
            graphics.FillEllipse(Brushes.White, x + size * 0.44F, y + size * 0.76F, size * 0.12F, size * 0.12F);
        }
        else
        {
            graphics.DrawEllipse(errorPen, x, y, size, size);
            graphics.DrawLine(errorPen, At(0.3F, 0.3F), At(0.7F, 0.7F));
            graphics.DrawLine(errorPen, At(0.7F, 0.3F), At(0.3F, 0.7F));
        }
    }

    private LinkLabel InstallationLink(string text, int left, string url)
    {
        var link = new LinkLabel
        {
            Text = text, Location = new Point(left, 200), AutoSize = true, Visible = false,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The installation instructions could not be opened:\r\n{ex.Message}",
                    "Check Dependencies", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        return link;
    }
}
