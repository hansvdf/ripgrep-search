using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RipgrepSearch.Desktop;

public sealed partial class MainWindow : Window
{
    private bool closePending;
    private bool mayClose;
    public SearchViewModel ViewModel { get; }
    internal bool IsCloseReady => mayClose;

    public MainWindow() : this(new SearchViewModel()) { }

    public MainWindow(SearchViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ConfigureNativeMenu();
        ResultsEditor.TextArea.TextView.LineTransformers.Add(new ResultColorizer(viewModel.Results));
        // Never turn arbitrary result text into clickable external URLs.
        ResultsEditor.Options.EnableHyperlinks = false;
        ResultsEditor.Options.EnableEmailHyperlinks = false;
        ResultsEditor.AddHandler(PointerPressedEvent, ResultsPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        FolderInput.AddHandler(KeyDownEvent, HistoryKeyDown, RoutingStrategies.Tunnel);
        PatternInput.AddHandler(KeyDownEvent, HistoryKeyDown, RoutingStrategies.Tunnel);
        Closing += WindowClosing;
        Opened += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            if (!closePending) PatternInput.Focus();
        };
    }

    private void ConfigureNativeMenu()
    {
        var options = new NativeMenuItem("Show Search Options") { ToggleType = MenuItemToggleType.CheckBox };
        var command = new NativeMenuItem("Show Command") { ToggleType = MenuItemToggleType.CheckBox };
        options.Bind(NativeMenuItem.IsCheckedProperty, new Binding(nameof(SearchViewModel.ShowSearchOptions)) { Source = ViewModel, Mode = BindingMode.OneWay });
        command.Bind(NativeMenuItem.IsCheckedProperty, new Binding(nameof(SearchViewModel.ShowCommand)) { Source = ViewModel, Mode = BindingMode.OneWay });
        options.Click += (_, _) => { if (ViewModel.CanChangeView) ViewModel.ShowSearchOptions = !ViewModel.ShowSearchOptions; };
        command.Click += (_, _) => { if (ViewModel.CanChangeView) ViewModel.ShowCommand = !ViewModel.ShowCommand; };
        var view = new NativeMenuItem("View") { Menu = new NativeMenu { options, command } };
        foreach (NativeMenuItem item in new[] { view, options, command })
            item.Bind(NativeMenuItem.IsEnabledProperty, new Binding(nameof(SearchViewModel.CanChangeView)) { Source = ViewModel, Mode = BindingMode.OneWay });

        // Only hide the in-window fallback once a platform exporter takes over.
        // Headless/Windows and desktops without global menus keep the regular menu.
        PropertyChanged += (_, change) =>
        {
            if (change.Property == NativeMenu.IsNativeMenuExportedProperty)
                MainMenu.IsVisible = !NativeMenu.GetIsNativeMenuExported(this);
        };
        NativeMenu.SetMenu(this, new NativeMenu { view });
        MainMenu.IsVisible = !NativeMenu.GetIsNativeMenuExported(this);
    }

    private static void HistoryKeyDown(object? sender, KeyEventArgs e)
    {
        // An editable ComboBox's inner TextBox can consume Alt+arrow on macOS.
        // Route the dropdown gesture before text editing handles it.
        if (sender is ComboBox combo && (e.Key == Key.F4 ||
            (e.Key is Key.Down or Key.Up && e.KeyModifiers.HasFlag(KeyModifiers.Alt))))
        {
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            e.Handled = true;
        }
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
            IStorageFolder? start = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.Folder))
                    start = await StorageProvider.TryGetFolderFromPathAsync(new Uri(ViewModel.ResolveFolderInput()));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            using var startingFolder = start;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder to search",
                AllowMultiple = false,
                SuggestedStartLocation = startingFolder
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
        var history = FolderInput.IsDropDownOpen ? FolderInput : PatternInput.IsDropDownOpen ? PatternInput : null;
        if (history is not null && e.Key == Key.Enter)
        {
            // Commit a history choice before the window's default Search button.
            if (history.SelectedItem is string value) history.Text = value;
            history.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if (history is not null && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Up or Key.Down)
        {
            // Editable dropdowns must navigate choices, not the inner TextBox's
            // caret, and selection must be committed before Enter reaches Search.
            if (history.ItemCount > 0)
            {
                int next = history.SelectedIndex < 0 ? (e.Key == Key.Down ? 0 : history.ItemCount - 1)
                    : Math.Clamp(history.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, history.ItemCount - 1);
                history.SelectedIndex = next;
                if (history.SelectedItem is string value) history.Text = value;
            }
            e.Handled = true;
        }
        else if (history is not null && e.Key == Key.Escape)
        {
            history.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ViewModel.IsSearching)
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
        if (mayClose) return;
        e.Cancel = true;
        if (closePending) return;
        closePending = true;
        // Keep pumping the UI during process cleanup and the final ordered settings write.
        await ViewModel.CloseAsync();
        mayClose = true;
        Close();
    }
}
