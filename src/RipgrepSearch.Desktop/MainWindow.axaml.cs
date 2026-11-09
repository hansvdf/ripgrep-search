using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RipgrepSearch.Desktop;

public sealed partial class MainWindow : Window
{
    private bool closePending;
    private bool mayClose;
    public SearchViewModel ViewModel { get; }

    public MainWindow() : this(new SearchViewModel()) { }

    public MainWindow(SearchViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ResultsEditor.TextArea.TextView.LineTransformers.Add(new ResultColorizer(viewModel.Results));
        // Never turn arbitrary result text into clickable external URLs.
        ResultsEditor.Options.EnableHyperlinks = false;
        ResultsEditor.Options.EnableEmailHyperlinks = false;
        ResultsEditor.AddHandler(PointerPressedEvent, ResultsPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        Closing += WindowClosing;
        Opened += (_, _) => PatternInput.Focus();
    }

    private async void BrowseClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!StorageProvider.CanPickFolder)
            {
                ViewModel.ReportError("A folder picker is unavailable. Enter an absolute folder path.");
                return;
            }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder to search",
                AllowMultiple = false
            });
            foreach (var folder in folders)
            {
                using (folder)
                {
                    string? path = folder.TryGetLocalPath();
                    if (path is not null) ViewModel.Folder = path;
                    else ViewModel.ReportError("Select a folder on the local file system.");
                }
            }
        }
        catch (Exception ex) { ViewModel.ReportError($"Could not choose a folder: {ex.Message}"); }
    }

    private async void ResultsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(ResultsEditor).Properties.IsLeftButtonPressed) return;
        var position = ResultsEditor.GetPositionFromPoint(e.GetPosition(ResultsEditor));
        if (position is null) return;
        string? path = ViewModel.Results.NavigationPathAt(ResultsEditor.Document.GetOffset(position.Value.Location));
        if (path is null) return;
        e.Handled = true;
        await OpenFileAsync(path);
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel.IsSearching)
        {
            ViewModel.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.F4 && ResultsEditor.TextArea.IsKeyboardFocusWithin)
        {
            string? path = ViewModel.Results.NavigationPathAt(ResultsEditor.CaretOffset);
            if (path is not null) await OpenFileAsync(path);
            e.Handled = true;
        }
    }

    private async Task OpenFileAsync(string path)
    {
        try
        {
            using var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            if (file is null || !await Launcher.LaunchFileAsync(file))
                ViewModel.ReportError("Could not open the result file in its default application.");
        }
        catch (Exception ex) { ViewModel.ReportError($"Could not open the result file: {ex.Message}"); }
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (mayClose || !ViewModel.IsSearching) return;
        e.Cancel = true;
        if (closePending) return;
        closePending = true;
        // Keep pumping the UI while Core kills the process tree and drains pipes.
        await ViewModel.CancelAndWaitAsync();
        mayClose = true;
        Close();
    }
}
