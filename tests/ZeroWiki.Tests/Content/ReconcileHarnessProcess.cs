using System.Diagnostics;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Spawns <c>ZeroWiki.LockTestHarness</c> in its <c>reconcile-under-hostile-git-config</c> mode (D17,
/// §6 block D4 continuation round three) — a genuine second OS process, so a hostile
/// <c>GIT_CONFIG_GLOBAL</c> can be exercised without ever touching the xUnit test host's own
/// process-wide environment. Setting that variable via <see cref="Environment.SetEnvironmentVariable(string, string?)"/>
/// in this test process would leak into every other concurrently-running test class's own git
/// subprocesses under xUnit's default parallelism — exactly the hazard
/// <c>ContentRepositoryServiceTests.Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch</c>
/// already documents and avoids for a narrower, single-call case; this harness is the equivalent for a
/// whole <see cref="ZeroWiki.Content.ContentRepositoryService.EnsureRepositoryAsync"/> call, which has
/// no per-call environment override to hook into.
/// </summary>
internal static class ReconcileHarnessProcess
{
    /// <summary>
    /// Runs <c>ContentRepositoryService.EnsureRepositoryAsync()</c> against <paramref name="dataRoot"/>
    /// in a fresh process with <c>GIT_CONFIG_GLOBAL</c> set to <paramref name="gitConfigGlobalPath"/> as
    /// that process's own environment. Returns whether it completed without throwing, and its stdout
    /// (<c>RECONCILED</c>, or <c>REFUSED: &lt;message&gt;</c>) for a caller that wants to inspect the
    /// refusal.
    /// </summary>
    public static async Task<(bool Succeeded, string Output)> RunAsync(string dataRoot, string gitConfigGlobalPath)
    {
        var harnessAssemblyPath = LockHarnessProcess.ResolveHarnessAssemblyPath();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(harnessAssemblyPath);
        startInfo.ArgumentList.Add("reconcile-under-hostile-git-config");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add(gitConfigGlobalPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ZeroWiki.LockTestHarness.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(exitTimeout.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode is not 0 and not 1)
        {
            throw new InvalidOperationException(
                $"ZeroWiki.LockTestHarness's reconcile mode exited unexpectedly ({process.ExitCode}). " +
                $"Stdout: {stdout} Stderr: {stderr}");
        }

        return (process.ExitCode == 0, stdout.Trim());
    }
}
