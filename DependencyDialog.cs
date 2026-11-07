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
    private readonly Font dependencyNameFont;
    private DependencyCheckResult[] lastResults = [];
    private DependencyStatus? summaryStatus;
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
        dependencyNameFont = new Font(Font, FontStyle.Bold);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
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
        dependencyGrid.BorderStyle = BorderStyle.None;
        dependencyGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        dependencyGrid.GridColor = SystemInformation.HighContrast ? SystemColors.ControlDark : Color.FromArgb(232, 232, 232);
        dependencyGrid.EnableHeadersVisualStyles = false;
        dependencyGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        dependencyGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        dependencyGrid.ColumnHeadersHeight = 32;
        dependencyGrid.ColumnHeadersDefaultCellStyle.BackColor = BackColor;
        dependencyGrid.ColumnHeadersDefaultCellStyle.ForeColor = SystemInformation.HighContrast
            ? ForeColor : SystemColors.GrayText;
        dependencyGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = BackColor;
        dependencyGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
        dependencyGrid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
        dependencyGrid.DefaultCellStyle.BackColor = BackColor;
        dependencyGrid.DefaultCellStyle.ForeColor = ForeColor;
        dependencyGrid.DefaultCellStyle.Padding = new Padding(8, 9, 8, 9);
        dependencyGrid.DefaultCellStyle.SelectionBackColor = SystemInformation.HighContrast
            ? SystemColors.Highlight : Color.FromArgb(235, 242, 250);
        dependencyGrid.DefaultCellStyle.SelectionForeColor = SystemInformation.HighContrast
            ? SystemColors.HighlightText : ForeColor;
        dependencyGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        dependencyGrid.Font = Font;
        dependencyGrid.CellPainting += PaintStatusCell;
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dependency", HeaderText = "Dependency", Width = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
        dependencyGrid.Columns["Dependency"]!.DefaultCellStyle.Font = dependencyNameFont;
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 95, SortMode = DataGridViewColumnSortMode.NotSortable });
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Version", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        dependencyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExecutablePath", HeaderText = "Executable path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 250, SortMode = DataGridViewColumnSortMode.NotSortable });

        foreach (DependencyDefinition dependency in DependencyChecker.Dependencies)
            dependencyGrid.Rows.Add(dependency.DisplayName, "Checking…", "—", "—");
        // Pre-handle row metrics omit some padding; measure using the actual control.
        _ = dependencyGrid.Handle;
        dependencyGrid.AutoResizeRows();
        dependencyGrid.Height = dependencyGrid.ColumnHeadersHeight +
            dependencyGrid.Rows.GetRowsHeight(DataGridViewElementStates.Visible);
        int contentOffset = dependencyGrid.Height - 100;
        ClientSize = new Size(900, 305 + contentOffset);
        MinimumSize = new Size(780, Height);

        summaryLabel.Location = new Point(15, 130 + contentOffset);
        summaryLabel.Size = new Size(750, 25);
        summaryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        summaryLabel.AccessibleName = "Dependency summary";
        summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        summaryLabel.Text = "Checking dependencies…";
        summaryLabel.Paint += PaintSummaryIcon;
        detailsToggle.Text = "Show details";
        detailsToggle.Location = new Point(785, 135 + contentOffset);
        detailsToggle.AutoSize = true;
        detailsToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        detailsToggle.Visible = false;
        detailsToggle.LinkClicked += (_, _) => SetDetailsExpanded(!detailsExpanded);

        detailsTextBox.Location = new Point(15, 165 + contentOffset);
        detailsTextBox.Size = new Size(870, 85);
        detailsTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        detailsTextBox.Multiline = true;
        detailsTextBox.ReadOnly = true;
        detailsTextBox.BackColor = BackColor;
        detailsTextBox.ScrollBars = ScrollBars.Vertical;
        detailsTextBox.AccessibleName = "Dependency check details";
        detailsTextBox.Visible = false;

        var noteLabel = new Label
        {
            Name = "dependencyNote",
            Text = "rg provides text searches and rga's search backend. rga adds document conversion; converters are not verified here.",
            Location = new Point(15, 165 + contentOffset),
            Size = new Size(870, 30),
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        rgLink = InstallationLink("Install ripgrep", 15, DependencyChecker.Dependencies[0].InstallationUrl);
        rgaLink = InstallationLink("Install ripgrep-all", 125, DependencyChecker.Dependencies[1].InstallationUrl);
        rgLink.Top += contentOffset;
        rgaLink.Top += contentOffset;

        refreshButton.Text = "Refresh";
        refreshButton.Location = new Point(675, 250 + contentOffset);
        refreshButton.Size = new Size(100, 29);
        refreshButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        refreshToolTip.SetToolTip(refreshButton,
            "Recheck dependencies and detect PATH changes without restarting.");
        refreshButton.Click += async (_, _) => await RefreshDependenciesAsync();
        closeButton.Text = "Close";
        closeButton.Location = new Point(785, 250 + contentOffset);
        closeButton.Size = new Size(100, 29);
        closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        closeButton.DialogResult = DialogResult.Cancel;
        closeButton.Click += (_, _) => Close();
        copyDiagnosticsButton.Text = "Copy Diagnostics";
        copyDiagnosticsButton.Location = new Point(15, 250 + contentOffset);
        copyDiagnosticsButton.Size = new Size(140, 29);
        copyDiagnosticsButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        copyDiagnosticsButton.Enabled = false;
        copyDiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        refreshToolTip.SetToolTip(copyDiagnosticsButton,
            "Copy dependency and system information for troubleshooting.");
        copyFeedbackLabel.Location = new Point(170, 250 + contentOffset);
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
        // Optional button redesign: uncomment this block and StyleActionButton below to reenable.
        /*
        copyDiagnosticsButton.Height = 34;
        refreshButton.Height = 34;
        closeButton.Height = 34;
        copyFeedbackLabel.Height = 34;
        closeButton.Font = dependencyNameFont;
        StyleActionButton(copyDiagnosticsButton);
        StyleActionButton(refreshButton);
        StyleActionButton(closeButton, primary: true);
        */
        var footerDivider = new Panel
        {
            Location = new Point(15, 232 + contentOffset),
            Size = new Size(870, 1),
            BackColor = dependencyGrid.GridColor,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.AddRange([dependencyGrid, summaryLabel, detailsToggle, detailsTextBox, noteLabel,
            rgLink, rgaLink, footerDivider, copyDiagnosticsButton, copyFeedbackLabel, refreshButton, closeButton]);
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
            dependencyNameFont.Dispose();
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
        summaryStatus = null;
        summaryLabel.Padding = Padding.Empty;
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
            dependencyGrid.Rows[index].Cells["ExecutablePath"].ToolTipText = result.ExecutablePath ?? result.Details;
        }
        DependencyCheckResult[] problems = results.Where(result => result.Status != DependencyStatus.Installed).ToArray();
        summaryLabel.Text = problems.Length == 0
            ? "All dependencies are available."
            : problems.Any(result => result.Status == DependencyStatus.Error)
                ? "Some dependencies could not be checked."
                : "Some dependencies are missing.";
        summaryStatus = problems.Length == 0 ? DependencyStatus.Installed
            : problems.Any(result => result.Status == DependencyStatus.Error) ? DependencyStatus.Error : DependencyStatus.NotFound;
        summaryLabel.Padding = new Padding(24 * DeviceDpi / 96, 0, 0, 0);
        summaryLabel.Invalidate();
        detailsToggle.Visible = problems.Length > 0;
        detailsTextBox.Text = string.Join("\r\n\r\n", problems.Select(result =>
            $"{result.Dependency.DisplayName}: {result.Details}"));
        dependencyGrid.ClearSelection();
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
        int spacing = Math.Max(3, 6 * dependencyGrid.DeviceDpi / 96);
        int inset = Math.Max(4, 8 * dependencyGrid.DeviceDpi / 96);
        var iconBounds = new Rectangle(e.CellBounds.Left + inset,
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

    private void PaintSummaryIcon(object? sender, PaintEventArgs e)
    {
        if (summaryStatus is not DependencyStatus status)
            return;
        int size = 16 * DeviceDpi / 96;
        GraphicsState savedState = e.Graphics.Save();
        try { DrawStatusIcon(e.Graphics, new Rectangle(0, (summaryLabel.Height - size) / 2, size, size), status); }
        finally { e.Graphics.Restore(savedState); }
    }

    // Optional button redesign; see the matching constructor block above.
    /*
    private static void StyleActionButton(Button button, bool primary = false)
    {
        if (SystemInformation.HighContrast)
            return;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
        button.ForeColor = primary ? Color.White : SystemColors.WindowText;
        button.FlatAppearance.BorderColor = primary ? button.BackColor : Color.FromArgb(210, 210, 210);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(60, 60, 60) : Color.FromArgb(245, 245, 245);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.Black : Color.FromArgb(232, 232, 232);
    }
    */

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
            using var greenPen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.ForestGreen,
                Math.Max(1.5F, size / 8));
            graphics.DrawEllipse(greenPen, x, y, size, size);
            graphics.DrawLines(greenPen, [At(0.23F, 0.5F), At(0.43F, 0.7F), At(0.77F, 0.3F)]);
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
