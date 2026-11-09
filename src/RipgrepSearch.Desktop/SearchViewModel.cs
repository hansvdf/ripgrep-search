using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Input;
using Avalonia.Threading;

namespace RipgrepSearch.Desktop;

public sealed class SearchViewModel : INotifyPropertyChanged
{
    private string folder = string.Empty;
    private string pattern = string.Empty;
    private string status = "Loading preferences…";
    private bool isSearching;
    private bool isCancelling;
    private int matchingLines;
    private CancellationTokenSource? cancellation;
    private Task activeSearch = Task.CompletedTask;
    private readonly Func<string?> resolveExecutable;
    private readonly DesktopEnvironment environment;
    private readonly string? initialFolder;
    private readonly Func<string, AppSettings> loadSettings;
    private AppSettings settings = new();
    private Task? initialization;
    private Task pendingSave = Task.CompletedTask;
    private bool initialized;
    private bool closing;
    private IReadOnlyList<string> folderHistory = [];
    private IReadOnlyList<string> patternHistory = [];

    public SearchViewModel(string? initialFolder = null) : this(initialFolder,
        () => ExecutableResolver.ResolveSearchTool(false), DesktopEnvironment.Current()) { }

    internal SearchViewModel(string? initialFolder, Func<string?> resolveExecutable, DesktopEnvironment environment,
        Func<string, AppSettings>? loadSettings = null)
    {
        this.initialFolder = initialFolder;
        this.environment = environment;
        this.loadSettings = loadSettings ?? AppSettingsStore.Load;
        this.resolveExecutable = resolveExecutable;
        SearchCommand = new ActionCommand(async () => await SearchAsync(), () => CanSearch);
        CancelCommand = new ActionCommand(Cancel, () => IsSearching && !isCancelling);
    }

    public string Folder { get => folder; set => Set(ref folder, value ?? string.Empty); }
    public string Pattern { get => pattern; set => Set(ref pattern, value ?? string.Empty); }
    public IReadOnlyList<string> FolderHistory => folderHistory;
    public IReadOnlyList<string> PatternHistory => patternHistory;
    public string Status { get => status; private set => Set(ref status, value); }
    public int MatchingLines { get => matchingLines; private set => Set(ref matchingLines, value); }
    public bool IsSearching => isSearching;
    public bool CanSearch => initialized && !isSearching && !closing;
    public ResultsDocument Results { get; } = new();
    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal double? FirstBatchMilliseconds { get; private set; }
    internal int BatchCount { get; private set; }
    internal double MaxAppendMilliseconds { get; private set; }
    internal AppSettings Preferences => settings;

    public Task InitializeAsync() => initialization ??= InitializeInternalAsync();

    private async Task InitializeInternalAsync()
    {
        var loaded = await Task.Run(() =>
        {
            AppSettings preferences = loadSettings(environment.SettingsFile)
                .WithRecentSearch(null, null, environment.FolderComparer);
            // Match WinForms' UI normalization without changing the shared store
            // or activating Phase 5.3 options in the current search pipeline.
            preferences.BeforeContext = Math.Clamp(preferences.BeforeContext, 0, 100);
            preferences.AfterContext = Math.Clamp(preferences.AfterContext, 0, 100);
            if (preferences.SortBy is not ("path" or "modified" or "accessed" or "created")) preferences.SortBy = "path";
            return (Settings: preferences, Folder: environment.InitialFolder(initialFolder, preferences.FolderHistory));
        });
        settings = loaded.Settings;
        Folder = loaded.Folder;
        RefreshHistories();
        // Like WinForms, make an explicit startup folder available in the list
        // without persisting it or promoting existing MRUs during initialization.
        if (!string.IsNullOrWhiteSpace(Folder) && !folderHistory.Contains(Folder, environment.FolderComparer))
        {
            folderHistory = new[] { Folder }.Concat(folderHistory).Take(10).ToArray();
            PropertyChanged?.Invoke(this, new(nameof(FolderHistory)));
            PropertyChanged?.Invoke(this, new(nameof(Folder)));
        }
        initialized = true;
        Status = "Choose a folder and enter a search pattern.";
        RefreshCommands();
    }

    private void RefreshHistories()
    {
        string currentFolder = Folder, currentPattern = Pattern;
        folderHistory = settings.FolderHistory.ToArray();
        patternHistory = settings.SearchHistory.ToArray();
        PropertyChanged?.Invoke(this, new(nameof(FolderHistory)));
        PropertyChanged?.Invoke(this, new(nameof(PatternHistory)));
        // Editable ComboBoxes may reset their Text when ItemsSource changes.
        Folder = currentFolder;
        Pattern = currentPattern;
        PropertyChanged?.Invoke(this, new(nameof(Folder)));
        PropertyChanged?.Invoke(this, new(nameof(Pattern)));
    }

    // Called at search startup and close, as in WinForms. Writes are ordered and
    // use independent Core snapshots, so older async saves cannot win a race.
    public Task SaveAsync()
    {
        if (!initialized) return Task.CompletedTask;
        settings = settings.WithRecentSearch(environment.FolderInput(Folder), Pattern, environment.FolderComparer);
        AppSettings snapshot = settings;
        Task previous = pendingSave;
        pendingSave = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            AppSettingsStore.Save(environment.SettingsFile, snapshot);
        });
        return pendingSave;
    }

    public async Task CloseAsync()
    {
        closing = true;
        RefreshCommands();
        await CancelAndWaitAsync();
        await InitializeAsync();
        await SaveAsync();
    }

    public Task SearchAsync()
    {
        if (CanSearch) activeSearch = SearchInternalAsync();
        return activeSearch;
    }

    public void Cancel()
    {
        if (cancellation is null || isCancelling) return;
        isCancelling = true;
        Status = "Cancelling search…";
        ((ActionCommand)CancelCommand).Refresh();
        cancellation.Cancel();
    }

    public async Task CancelAndWaitAsync()
    {
        Cancel();
        await activeSearch;
    }

    public void ReportError(string message) => Status = message;
    internal string ResolveFolderInput() => environment.ResolveFolder(Folder);

    private async Task SearchInternalAsync()
    {
        string absoluteFolder;
        string? executable;
        string searchPattern = Pattern;
        try
        {
            if (string.IsNullOrWhiteSpace(Folder))
            {
                Status = "Enter a folder path or use Browse.";
                return;
            }
            absoluteFolder = environment.ResolveFolder(Folder);
            if (!Directory.Exists(absoluteFolder))
            {
                Status = "The selected folder does not exist or cannot be accessed.";
                return;
            }
            if (string.IsNullOrWhiteSpace(searchPattern))
            {
                Status = "Enter a search word, phrase, or regular expression.";
                return;
            }
            executable = resolveExecutable();
            if (executable is null)
            {
                Status = "ripgrep (rg) was not found on PATH. Install rg and launch from a terminal whose PATH includes it.";
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = $"Cannot start search: {ex.Message}";
            return;
        }

        _ = SaveAsync();
        RefreshHistories();

        using var source = new CancellationTokenSource();
        cancellation = source;
        SetSearching(true);
        Results.Clear();
        MatchingLines = 0;
        BatchCount = 0;
        MaxAppendMilliseconds = 0;
        FirstBatchMilliseconds = null;
        Status = "Searching…";
        var elapsed = Stopwatch.StartNew();
        var channel = Channel.CreateBounded<IReadOnlyList<SearchResultLine>>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        var arguments = SearchRunner.BuildArguments(searchPattern, absoluteFolder, 0, 0, null, false);
        Task<SearchOutcome> runner = Task.Run(() => SearchRunner.RunAsync(executable, arguments,
            absoluteFolder, channel.Writer, source.Token));

        SearchOutcome outcome;
        try
        {
            // Format on a worker, and await one low-priority UI append per Core
            // batch. This propagates backpressure all the way to rg's stdout.
            await Task.Run(async () =>
            {
                var formatter = new SearchResultFormatter();
                await foreach (var batch in channel.Reader.ReadAllAsync(source.Token))
                {
                    source.Token.ThrowIfCancellationRequested();
                    ResultBatch formatted = ResultBatch.Format(batch.SelectMany(formatter.Format));
                    int matches = batch.Count(line => line.IsMatch);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (source.IsCancellationRequested) return;
                        var append = Stopwatch.StartNew();
                        Results.Append(formatted);
                        MaxAppendMilliseconds = Math.Max(MaxAppendMilliseconds, append.Elapsed.TotalMilliseconds);
                        FirstBatchMilliseconds ??= elapsed.Elapsed.TotalMilliseconds;
                        BatchCount++;
                        MatchingLines += matches;
                        Status = $"Searching… {MatchingLines:N0} matching line(s) shown.";
                    }, DispatcherPriority.Background);
                }
            });
            outcome = await runner;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            outcome = await runner;
        }
        catch (Exception ex)
        {
            source.Cancel();
            await runner;
            outcome = new SearchOutcome(SearchCompletion.Failed, null, string.Empty,
                $"Could not display search results: {ex.Message}");
        }

        // rg may already have finished while the UI is still consuming queued
        // batches. Cancelling display must never claim a complete result total.
        if (source.IsCancellationRequested && outcome.Completion != SearchCompletion.Failed)
            outcome = outcome with { Completion = SearchCompletion.Cancelled, Reason = "Search cancelled." };

        try
        {
            Status = DescribeOutcome(outcome, MatchingLines);
            var diagnostics = new List<ResultSpan>();
            if (outcome.Completion == SearchCompletion.Completed && MatchingLines == 0)
                diagnostics.Add(new ResultSpan("No matches found.\r\n", ResultColor.White, ResultTextStyle.Regular));
            if (!string.IsNullOrWhiteSpace(outcome.Reason))
                diagnostics.Add(new ResultSpan($"\r\n{outcome.Reason}\r\n", ResultColor.Gray, ResultTextStyle.Regular));
            if (!string.IsNullOrWhiteSpace(outcome.Diagnostics))
                diagnostics.Add(new ResultSpan($"\r\nDiagnostics:\r\n{outcome.Diagnostics.TrimEnd()}\r\n",
                    ResultColor.Gray, ResultTextStyle.Regular));
            if (diagnostics.Count > 0) Results.Append(ResultBatch.Format(diagnostics));
        }
        finally
        {
            cancellation = null;
            isCancelling = false;
            SetSearching(false);
        }
    }

    private static string DescribeOutcome(SearchOutcome outcome, int count)
    {
        string message = outcome.Completion switch
        {
            SearchCompletion.Completed => $"Search completed. {count:N0} matching line(s) found.",
            SearchCompletion.Cancelled => $"Search cancelled. {count:N0} matching line(s) shown; results are incomplete.",
            SearchCompletion.Limited => $"Display limit reached. {count:N0} matching line(s) shown; search stopped.",
            _ => $"Search failed. {count:N0} matching line(s) shown; results may be incomplete."
        };
        return outcome.Completion == SearchCompletion.Completed && !string.IsNullOrWhiteSpace(outcome.Diagnostics)
            ? message + " Warnings reported; see results." : message;
    }

    private void SetSearching(bool value)
    {
        isSearching = value;
        PropertyChanged?.Invoke(this, new(nameof(IsSearching)));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanSearch)));
        ((ActionCommand)SearchCommand).Refresh();
        ((ActionCommand)CancelCommand).Refresh();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    private sealed class ActionCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
