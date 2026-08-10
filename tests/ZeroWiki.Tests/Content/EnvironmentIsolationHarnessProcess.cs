using System.Diagnostics;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Spawns <c>ZeroWiki.LockTestHarness</c> in its <c>check-git-http-backend-environment-isolation</c>
/// mode (§7 remediation, supervisor blocker 1) — a genuine second OS process, so <c>GIT_TRACE</c> can be
/// used as a leak detector without ever touching the xUnit test host's own process-wide environment.
/// Setting it via <see cref="Environment.SetEnvironmentVariable(string, string?)"/> in this test process
/// was tried first and is exactly the hazard <see cref="ReconcileHarnessProcess"/>'s own remarks already
/// document: it leaked into an unrelated, concurrently-running test's own git subprocess under the full
/// suite's parallelism and produced a false positive there — reproduced live, not hypothesised, which is
/// why this harness exists rather than a shared-process shortcut.
/// </summary>
internal static class EnvironmentIsolationHarnessProcess
{
    /// <summary>
    /// Runs one <see cref="ZeroWiki.Content.GitHttpBackendHost.InvokeAsync"/> call against a repository
    /// initialized under <paramref name="dataRoot"/>, in a fresh process with <c>GIT_TRACE</c> set to
    /// <paramref name="traceMarkerPath"/> as that process's own environment. Returns whether the
    /// subprocess wrote the marker file (leaked) and the response status code the harness observed.
    /// </summary>
    public static async Task<(bool Leaked, int StatusCode)> RunAsync(string dataRoot, string traceMarkerPath)
    {
        var harnessAssemblyPath = LockHarnessProcess.ResolveHarnessAssemblyPath();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(harnessAssemblyPath);
        startInfo.ArgumentList.Add("check-git-http-backend-environment-isolation");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add(traceMarkerPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ZeroWiki.LockTestHarness.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(exitTimeout.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ZeroWiki.LockTestHarness's environment-isolation mode exited unexpectedly " +
                $"({process.ExitCode}). Stdout: {stdout} Stderr: {stderr}");
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || (lines[0] != "LEAKED" && lines[0] != "NOT_LEAKED") || !int.TryParse(lines[1], out var statusCode))
        {
            throw new InvalidOperationException(
                $"ZeroWiki.LockTestHarness's environment-isolation mode produced unexpected output: '{stdout}'. Stderr: {stderr}");
        }

        return (lines[0] == "LEAKED", statusCode);
    }
}
