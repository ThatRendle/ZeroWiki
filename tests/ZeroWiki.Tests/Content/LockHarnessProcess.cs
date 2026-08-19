using System.Diagnostics;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Spawns <c>ZeroWiki.LockTestHarness</c> — a genuine second OS process running
/// <see cref="ZeroWiki.Content.RepositoryWriteLock"/>'s real production code, not a fake or an
/// in-process <see cref="Task"/> — and synchronizes on its own "ACQUIRED" line rather than guessing
/// with a fixed delay, so callers know the lock is actually held before they race it.
/// </summary>
internal sealed class LockHarnessProcess : IAsyncDisposable
{
    private readonly Process _process;

    private LockHarnessProcess(Process process)
    {
        _process = process;
    }

    /// <summary>
    /// Starts the harness, waits for it to report the lock acquired, and returns once it has. Throws if
    /// the harness could not acquire the lock within <paramref name="holdMs"/> plus a generous margin,
    /// or reports anything other than "ACQUIRED".
    /// </summary>
    public static async Task<LockHarnessProcess> StartHoldingAsync(string lockFilePath, int holdMs)
    {
        var harnessAssemblyPath = ResolveHarnessAssemblyPath();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(harnessAssemblyPath);
        startInfo.ArgumentList.Add(lockFilePath);
        startInfo.ArgumentList.Add("10000"); // The harness's own acquire timeout — generous; the test controls contention via holdMs.
        startInfo.ArgumentList.Add(holdMs.ToString());

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ZeroWiki.LockTestHarness.");

        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstLine = await process.StandardOutput.ReadLineAsync(readyTimeout.Token);

        if (firstLine != "ACQUIRED")
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"Lock harness did not report acquiring the lock (reported '{firstLine}' instead). Stderr: {stderr}");
        }

        return new LockHarnessProcess(process);
    }

    public async ValueTask DisposeAsync()
    {
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _process.WaitForExitAsync(exitTimeout.Token);
        _process.Dispose();
    }

    /// <summary>Internal so <see cref="ReconcileHarnessProcess"/> can reuse this path resolution
    /// rather than duplicating it for a second harness mode.</summary>
    internal static string ResolveHarnessAssemblyPath()
    {
        // AppContext.BaseDirectory: .../tests/ZeroWiki.Tests/bin/<Config>/<TFM>/ — reuse the actual
        // Config/TFM folder names found on disk rather than guessing Debug vs Release.
        var testsBinDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFrameworkFolder = testsBinDirectory.Name;
        var configurationFolder = testsBinDirectory.Parent
            ?? throw new InvalidOperationException($"Could not resolve the parent of '{testsBinDirectory}'.");
        var testsProjectDirectory = configurationFolder.Parent?.Parent
            ?? throw new InvalidOperationException($"Could not resolve the project directory above '{configurationFolder}'.");
        var testsDirectory = testsProjectDirectory.Parent
            ?? throw new InvalidOperationException($"Could not resolve the tests directory above '{testsProjectDirectory}'.");

        var harnessAssemblyPath = Path.Combine(
            testsDirectory.FullName,
            "ZeroWiki.LockTestHarness",
            "bin",
            configurationFolder.Name,
            targetFrameworkFolder,
            "ZeroWiki.LockTestHarness.dll");

        if (!File.Exists(harnessAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Lock test harness assembly not found at '{harnessAssemblyPath}'. Ensure " +
                "ZeroWiki.LockTestHarness is referenced by ZeroWiki.Tests.csproj so it builds alongside " +
                "the test project.");
        }

        return harnessAssemblyPath;
    }
}
