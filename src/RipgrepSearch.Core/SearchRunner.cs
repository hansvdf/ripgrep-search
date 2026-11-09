using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace RipgrepSearch;

/// <summary>Search safety budgets; MaxLines includes context and MaxCharacters includes path/formatting allowance.</summary>
public sealed record SearchLimits(
    int MaxLines = 10_000,
    int MaxCharacters = 2_000_000,
    int MaxLineCharacters = 16_384,
    int MaxJsonMessageCharacters = 4_194_304);

/// <summary>Whether a search completed or retained only partial output.</summary>
public enum SearchCompletion { Completed, Cancelled, Limited, Failed }

/// <summary>Completion status and bounded tool diagnostics; results arrive separately through the channel.</summary>
public sealed record SearchOutcome(SearchCompletion Completion, int? ExitCode, string Diagnostics, string? Reason);

/// <summary>Shell-free rg/rga argument construction and bounded, cancellable JSON search streaming.</summary>
public static class SearchRunner
{
    public static List<string> BuildArguments(string pattern, string folder, int before, int after,
        string? sortBy, bool descending)
    {
        var arguments = new List<string> { "--json", "--line-number", "--color", "never" };
        if (before > 0)
            arguments.AddRange(["--before-context", before.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (after > 0)
            arguments.AddRange(["--after-context", after.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (sortBy is not null)
            arguments.AddRange([descending ? "--sortr" : "--sort", sortBy]);
        arguments.AddRange(["--", pattern, folder]);
        return arguments;
    }

    // Called on a worker thread. The bounded channel provides backpressure while
    // the UI renders; stdout, stderr, and process lifetime are handled together.
    public static async Task<SearchOutcome> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, ChannelWriter<IReadOnlyList<SearchResultLine>> writer,
        CancellationToken cancellationToken, SearchLimits? limits = null)
    {
        limits ??= new SearchLimits();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
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
        Task<string>? errorTask = null;
        var parser = new RipgrepJsonParser(workingDirectory);
        var batch = new List<SearchResultLine>();
        var batchAge = Stopwatch.StartNew();
        int batchCharacters = 0;
        int totalCharacters = 0;
        int totalLines = 0;
        int? exitCode = null;
        string? reason = null;
        string diagnostics = string.Empty;
        SearchCompletion completion = SearchCompletion.Completed;
        bool started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            using var registration = cancellationToken.Register(() =>
            {
                // Cancellation can originate on the UI thread. Tree enumeration
                // and converter shutdown must not run synchronously on that thread.
                ThreadPool.QueueUserWorkItem(_ => StopProcess(process));
            });
            errorTask = ReadDiagnosticsAsync(process.StandardError);
            await using var messages = ReadMessagesAsync(process.StandardOutput,
                limits.MaxJsonMessageCharacters, cancellationToken).GetAsyncEnumerator();
            Task<bool>? nextMessage = null;
            try
            {
                while (true)
                {
                    nextMessage ??= messages.MoveNextAsync().AsTask();
                    if (batch.Count > 0 && !nextMessage.IsCompleted)
                    {
                        int remaining = (int)Math.Max(0, 50 - batchAge.ElapsedMilliseconds);
                        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        Task deadline = Task.Delay(remaining, timerCancellation.Token);
                        Task ready = await Task.WhenAny(nextMessage, deadline).ConfigureAwait(false);
                        timerCancellation.Cancel();
                        if (ready == deadline)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await FlushAsync().ConfigureAwait(false);
                            continue; // Reuse the same pending read; never read concurrently.
                        }
                    }
                    if (!await nextMessage.ConfigureAwait(false))
                        break;
                    nextMessage = null;

                    foreach (SearchResultLine line in parser.Parse(messages.Current, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (line.Text.Length > limits.MaxLineCharacters)
                            throw new DisplayLimitException($"A result line exceeds {limits.MaxLineCharacters:N0} characters.");
                        if (totalLines >= limits.MaxLines)
                            throw new DisplayLimitException($"The {limits.MaxLines:N0}-line display limit was reached.");
                        int size = line.Text.Length + line.FilePath.Length + 32;
                        if (size > limits.MaxCharacters - totalCharacters)
                            throw new DisplayLimitException($"The {limits.MaxCharacters:N0}-character display limit was reached.");
                        totalCharacters += size;
                        totalLines++;
                        batch.Add(line);
                        batchCharacters += size;
                        if (batch.Count >= 128 || batchCharacters >= 65_536)
                            await FlushAsync().ConfigureAwait(false);
                    }
                    if (batch.Count > 0 && batchAge.ElapsedMilliseconds >= 50)
                        await FlushAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // Async iterators cannot be disposed during an active MoveNext.
                // Cancellation also cancels that read; observe it before disposal.
                if (nextMessage is not null)
                {
                    try { await nextMessage.ConfigureAwait(false); }
                    catch (Exception) when (cancellationToken.IsCancellationRequested) { }
                }
            }
            await FlushAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
            if (exitCode is not (0 or 1))
            {
                completion = SearchCompletion.Failed;
                reason = $"Search failed with exit code {exitCode}.";
            }
            else if (!parser.HasSummary)
            {
                completion = SearchCompletion.Failed;
                reason = "Search output ended before its JSON summary. Results may be incomplete.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion = SearchCompletion.Cancelled;
            reason = "Search cancelled.";
        }
        catch (DisplayLimitException ex)
        {
            completion = SearchCompletion.Limited;
            reason = ex.Message;
        }
        catch (Exception ex)
        {
            completion = SearchCompletion.Failed;
            reason = ex is InvalidDataException
                ? $"Could not read JSON search output: {ex.Message}"
                : $"Search failed: {ex.Message}";
        }
        finally
        {
            try
            {
                if (started)
                {
                    StopProcess(process);
                    // Release unread output after stopping. Tool launchers and rga's
                    // converter processes may still hold inherited pipe handles.
                    if (completion != SearchCompletion.Completed)
                        process.StandardOutput.Dispose();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    exitCode ??= process.ExitCode;
                }
                if (!cancellationToken.IsCancellationRequested)
                    await FlushAsync().ConfigureAwait(false);
                if (errorTask is not null)
                    diagnostics = await errorTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion = SearchCompletion.Cancelled;
                reason = "Search cancelled.";
            }
            catch (Exception ex)
            {
                completion = SearchCompletion.Failed;
                reason = $"Could not finish reading search output: {ex.Message}";
            }
            finally
            {
                writer.TryComplete();
            }
        }
        return new SearchOutcome(completion, exitCode, diagnostics, reason);

        async Task FlushAsync()
        {
            if (batch.Count == 0)
                return;
            await writer.WriteAsync(batch.ToArray(), cancellationToken).ConfigureAwait(false);
            batch.Clear();
            batchCharacters = 0;
            batchAge.Restart();
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // Not started or already exited.
        catch (System.ComponentModel.Win32Exception) { } // Exit raced the kill request.
        catch (AggregateException) { } // A descendant exited or could not be terminated; cleanup is bounded.
    }

    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader)
    {
        const int maximum = 16_384;
        var diagnostics = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(count, maximum - diagnostics.Length);
            diagnostics.Append(buffer, 0, keep);
            truncated |= keep < count;
        }
        if (truncated)
            diagnostics.Append("\r\n[Additional diagnostics omitted.]");
        return diagnostics.ToString();
    }

    private static async IAsyncEnumerable<string> ReadMessagesAsync(StreamReader reader, int maximum,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var message = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] != '\n')
                    continue;
                Append(start, i - start);
                if (message.Length > 0)
                    yield return message.ToString().TrimEnd('\r');
                message.Clear();
                start = i + 1;
            }
            Append(start, count - start);
        }
        if (message.Length > 0)
            yield return message.ToString().TrimEnd('\r');

        void Append(int start, int length)
        {
            if (length > maximum - message.Length)
                throw new DisplayLimitException($"A JSON result exceeds the {maximum:N0}-character safety limit.");
            message.Append(buffer, start, length);
        }
    }

    private sealed class DisplayLimitException(string message) : Exception(message);
}
