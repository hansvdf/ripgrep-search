using System.Diagnostics;
using System.Text;

namespace RipgrepSearch;

internal enum DependencyStatus { Installed, NotFound, Error }

internal sealed record DependencyDefinition(string Name, string ExecutableName, bool UseRipgrepAll, string InstallationUrl)
{
    public string DisplayName => $"{Name} ({ExecutableName})";
}

internal sealed record DependencyCheckResult(DependencyDefinition Dependency, DependencyStatus Status,
    string? Version, string? ExecutablePath, string Details);

internal static class DependencyChecker
{
    public static IReadOnlyList<DependencyDefinition> Dependencies { get; } = new[]
    {
        new DependencyDefinition("ripgrep", "rg", false, "https://github.com/BurntSushi/ripgrep#installation"),
        new DependencyDefinition("ripgrep-all", "rga", true, "https://github.com/phiresky/ripgrep-all#installation")
    };

    public static Task<DependencyCheckResult[]> CheckAllAsync(CancellationToken cancellationToken = default) =>
        CheckAllWithPathProviderAsync(() => Environment.GetEnvironmentVariable("PATH"), TimeSpan.FromSeconds(5), cancellationToken);

    internal static Task<DependencyCheckResult[]> CheckAllAsync(string? searchPath, TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        CheckAllWithPathProviderAsync(() => searchPath, timeout, cancellationToken);

    internal static Task<DependencyCheckResult[]> CheckAllWithPathProviderAsync(Func<string?> getSearchPath,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // The host supplies environment policy. Keep resolution and probes off the UI thread.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? searchPath = getSearchPath();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.WhenAll(Dependencies.Select(dependency =>
                CheckAsync(dependency, searchPath, timeout, cancellationToken)));
        }, cancellationToken);
    }

    private static async Task<DependencyCheckResult> CheckAsync(DependencyDefinition dependency,
        string? searchPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ExecutableResolver.ResolveSearchTool(dependency.UseRipgrepAll, searchPath);
        if (path is null)
        {
            return new DependencyCheckResult(dependency, DependencyStatus.NotFound, null, null,
                $"{dependency.ExecutableName} was not found on the application's PATH. Install {dependency.Name}, " +
                $"add the directory containing its executable to {(OperatingSystem.IsWindows() ? "Windows PATH" : "PATH")}, then click Refresh.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        bool started = false;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            using var registration = timeoutCancellation.Token.Register(() =>
            {
                ThreadPool.QueueUserWorkItem(_ => StopProcess(process));
            });
            outputTask = ReadOutputAsync(process.StandardOutput, timeoutCancellation.Token);
            errorTask = ReadOutputAsync(process.StandardError, timeoutCancellation.Token);
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            string[] output = await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
                return Error($"The version command exited with code {process.ExitCode}." + DescribeOutput(output[1], output[0]));

            string? version = ParseVersion(dependency, output[0]) ?? ParseVersion(dependency, output[1]);
            if (version is null)
                return Error("The command ran, but did not return recognizable version information for " +
                    dependency.Name + "." + DescribeOutput(output[1], output[0]));

            return new DependencyCheckResult(dependency, DependencyStatus.Installed, version, path,
                "The executable ran successfully and returned its version.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return Error($"The version check timed out after {timeout.TotalSeconds:0.##} seconds.");
        }
        catch (Exception ex)
        {
            return Error($"The executable was found but could not be checked: {ex.Message}");
        }
        finally
        {
            if (started)
            {
                StopProcess(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { /* Reported result remains an error if the command timed out or failed. */ }
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                if (outputTask is not null && errorTask is not null)
                {
                    // Observe reads cancelled by timeout or dialog closure.
                    try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
        }

        DependencyCheckResult Error(string details) =>
            new(dependency, DependencyStatus.Error, null, path, details.Replace('\0', '\u2400'));
    }

    private static string? ParseVersion(DependencyDefinition dependency, string output)
    {
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0] != dependency.Name)
                continue;
            string version = fields[1];
            if (version.Length > 128 || !version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+'))
                continue;
            string core = version.Split(['-', '+'])[0];
            if (System.Version.TryParse(core, out _))
                return version;
        }
        return null;
    }

    private static string DescribeOutput(string error, string output)
    {
        string detail = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        if (detail.Length == 0)
            return " No version output was received.";
        return "\r\n" + (detail.Length > 2000 ? detail[..2000] + "…" : detail);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximum = 16_384;
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            output.Append(buffer, 0, Math.Min(count, maximum - output.Length));
        return output.ToString();
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (AggregateException) { }
    }
}
