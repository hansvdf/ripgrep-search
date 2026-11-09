using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Input;
using Avalonia.Threading;

namespace RipgrepSearch.Desktop;

public sealed class SearchViewModel : INotifyPropertyChanged
{
    private string folder;
    private string pattern = string.Empty;
    private string status = "Choose a folder and enter a search pattern.";
    private bool isSearching;
    private bool isCancelling;
    private int matchingLines;
    private CancellationTokenSource? cancellation;
    private Task activeSearch = Task.CompletedTask;
    private readonly Func<string?> resolveExecutable;

    public SearchViewModel(string? initialFolder = null) : this(initialFolder,
        () => ExecutableResolver.ResolveSearchTool(false)) { }

    internal SearchViewModel(string? initialFolder, Func<string?> resolveExecutable)
    {
        // GUI launches may inherit '/' or an app-bundle directory. Start in the
        // user's home unless an explicit absolute folder argument is supplied.
        folder = initialFolder is not null && Path.IsPathFullyQualified(initialFolder)
            ? initialFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.resolveExecutable = resolveExecutable;
        SearchCommand = new ActionCommand(async () => await SearchAsync(), () => CanSearch);
        CancelCommand = new ActionCommand(Cancel, () => IsSearching && !isCancelling);
    }

    public string Folder { get => folder; set => Set(ref folder, value); }
    public string Pattern { get => pattern; set => Set(ref pattern, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public int MatchingLines { get => matchingLines; private set => Set(ref matchingLines, value); }
    public bool IsSearching => isSearching;
    public bool CanSearch => !isSearching;
    public ResultsDocument Results { get; } = new();
    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal double? FirstBatchMilliseconds { get; private set; }
    internal int BatchCount { get; private set; }
    internal double MaxAppendMilliseconds { get; private set; }

    public Task SearchAsync()
    {
        if (!IsSearching) activeSearch = SearchInternalAsync();
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

    private async Task SearchInternalAsync()
    {
        string absoluteFolder;
        string? executable;
        string searchPattern = Pattern;
        try
        {
            if (string.IsNullOrWhiteSpace(Folder) || !Path.IsPathFullyQualified(Folder))
            {
                Status = "Enter an absolute folder path or use Browse.";
                return;
            }
            absoluteFolder = Path.GetFullPath(Folder);
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
