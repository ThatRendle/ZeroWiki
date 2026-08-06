using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZeroWiki.Content;
using ZeroWiki.Tests.Identity;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="GitProcessRunner.RunAsync"/>'s cancellation behaviour (§6 block D2, obligation
/// 8). Before this block, execution proved a disposed <see cref="Process"/> whose wait had been
/// cancelled still reported its child "still alive: True" — <c>using</c> releases a .NET handle, it does
/// not signal the OS process. These tests use a real git subprocess that itself forks a child, because
/// the defect is about process lifetime and a mock has none.
/// </summary>
public sealed class GitProcessRunnerTests : IDisposable
{
    private readonly string _repositoryRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-git-runner-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    public GitProcessRunnerTests()
    {
        Directory.CreateDirectory(_repositoryRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CancelledWhileGitIsRunning_KillsGitAndEverythingItSpawned()
    {
        await _git.RunOrThrowAsync(_repositoryRoot, ["init", "--quiet"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.name", "Seed"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.email", "seed@zerowiki.example"]);

        var startedMarkerPath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-started-{Guid.NewGuid():n}");
        var childPidFilePath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-child-pid-{Guid.NewGuid():n}");
        InstallHookThatForksASleepingGrandchild(startedMarkerPath, childPidFilePath, sleepSeconds: 30);

        try
        {
            using var cts = new CancellationTokenSource();
            var runTask = _git.RunAsync(
                _repositoryRoot,
                ["commit", "--allow-empty", "-m", "x"],
                cancellationToken: cts.Token);

            // Proof the hook -- and therefore its own sleeping grandchild -- is actually running,
            // deep inside the git subprocess this test is about to cancel. Bounded poll, not a fixed
            // sleep: the exact scheduling delay before the hook runs is not something to guess at.
            await WaitForFileAsync(startedMarkerPath);
            var childPid = int.Parse((await File.ReadAllTextAsync(childPidFilePath)).Trim());
            Assert.True(IsProcessAlive(childPid), "Precondition failed: the hook's child was never alive to begin with.");

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            Assert.True(
                await WaitForProcessToExitAsync(childPid),
                $"The hook's sleeping grandchild (pid {childPid}) survived cancellation -- the process tree was not killed.");
        }
        finally
        {
            if (File.Exists(startedMarkerPath))
            {
                File.Delete(startedMarkerPath);
            }

            if (File.Exists(childPidFilePath))
            {
                File.Delete(childPidFilePath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_NotCancelled_StillReturnsOutputAndExitCodeNormally()
    {
        // Regression guard for the cancellation-path change above: an ordinary, uncancelled
        // invocation must still behave exactly as before -- RunOrThrowAsync's contract and
        // GitProcessResult are unchanged.
        var result = await _git.RunAsync(_repositoryRoot, ["--version"]);

        Assert.True(result.Succeeded);
        Assert.Contains("git version", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    // §6 block D2 remediation: `Process.Kill(entireProcessTree: true)` also documents failure --
    // verified by execution against `Process.GetProcessById(1)` as an unprivileged user, which threw
    // `AggregateException` wrapping a `Win32Exception`, not the `InvalidOperationException` the
    // original guard alone caught. Reproducing that against a process this test actually owns would
    // need root (or a real system process this suite must never touch), so these tests substitute the
    // kill call itself via `GitProcessRunner`'s constructor -- everything else (the drains, the
    // logging, the rethrown `OperationCanceledException`) runs for real through the public
    // `RunAsync` surface. This is the "test at the seam" the remediation brief asked for when a real
    // unkillable child cannot be safely constructed.

    [Fact]
    public async Task RunAsync_CancelledAndTheKillThrowsInvalidOperationException_StillPropagatesCancellationAndLogsIt() =>
        await AssertKillFailureIsHandledAsync(new InvalidOperationException("already exited, in this test's telling of it"));

    [Fact]
    public async Task RunAsync_CancelledAndTheKillThrowsAggregateExceptionWrappingWin32Exception_StillPropagatesCancellationAndLogsIt() =>
        await AssertKillFailureIsHandledAsync(new AggregateException(new Win32Exception(1, "Operation not permitted")));

    [Fact]
    public async Task RunAsync_CancelledAndTheKillThrowsWin32ExceptionDirectly_StillPropagatesCancellationAndLogsIt() =>
        await AssertKillFailureIsHandledAsync(new Win32Exception(1, "Operation not permitted"));

    [Fact]
    public async Task RunAsync_CancelledAndTheKillThrowsAnUndocumentedExceptionType_PropagatesItRatherThanSwallowingIt()
    {
        // The catch is scoped to what Process.Kill(entireProcessTree: true) actually documents, not
        // "any exception" -- a genuinely unexpected failure here is a bug this code should surface
        // loudly, not quietly convert into a benign log line the way the documented cases are.
        await _git.RunOrThrowAsync(_repositoryRoot, ["init", "--quiet"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.name", "Seed"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.email", "seed@zerowiki.example"]);

        var startedMarkerPath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-started-{Guid.NewGuid():n}");
        var pidFilePath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-pid-{Guid.NewGuid():n}");
        InstallHookThatSignalsThenSleeps(startedMarkerPath, pidFilePath, sleepSeconds: 1);

        try
        {
            var git = new GitProcessRunner(killEntireProcessTree: _ => throw new FormatException("not a documented kill failure"));
            using var cts = new CancellationTokenSource();
            var runTask = git.RunAsync(_repositoryRoot, ["commit", "--allow-empty", "-m", "x"], cancellationToken: cts.Token);

            await WaitForFileAsync(startedMarkerPath);
            cts.Cancel();

            await Assert.ThrowsAsync<FormatException>(() => runTask);
        }
        finally
        {
            if (File.Exists(startedMarkerPath))
            {
                File.Delete(startedMarkerPath);
            }

            if (File.Exists(pidFilePath))
            {
                File.Delete(pidFilePath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_CancelledAndTheKilledProcessNeverExitsWithinTheBound_LogsAndStillPropagatesCancellationPromptly()
    {
        await _git.RunOrThrowAsync(_repositoryRoot, ["init", "--quiet"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.name", "Seed"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.email", "seed@zerowiki.example"]);

        var startedMarkerPath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-started-{Guid.NewGuid():n}");
        var pidFilePath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-pid-{Guid.NewGuid():n}");
        // Longer than GitProcessRunner's internal post-kill wait bound (3s) -- standing in for a
        // process a real kill cannot reap in time (an uninterruptible kernel wait), which cannot be
        // safely constructed without privileges. The injected kill is a no-op so the process genuinely
        // does not exit within the bound; the leftover is force-killed for real in `finally`.
        InstallHookThatSignalsThenSleeps(startedMarkerPath, pidFilePath, sleepSeconds: 30);

        var loggerProvider = new CapturingLoggerProvider();
        var git = new GitProcessRunner(loggerProvider.CreateLogger<GitProcessRunner>(), killEntireProcessTree: _ => { });

        int? hookPid = null;
        try
        {
            using var cts = new CancellationTokenSource();
            var runTask = git.RunAsync(_repositoryRoot, ["commit", "--allow-empty", "-m", "x"], cancellationToken: cts.Token);

            await WaitForFileAsync(startedMarkerPath);
            hookPid = int.Parse((await File.ReadAllTextAsync(pidFilePath)).Trim());

            cts.Cancel();

            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            stopwatch.Stop();

            // Bounded by the internal post-kill wait timeout (3s), not by the hook's 30-second sleep --
            // proof the caller's cancellation is never held hostage by a process this code could not
            // confirm dead. Generous margin for CI scheduling noise, still an order of magnitude below
            // the 30-second sleep it must not wait out.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"RunAsync took {stopwatch.Elapsed} to return after cancellation -- expected it to be bounded by the internal timeout, not by the process it could not confirm dead.");

            Assert.Contains(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains("did not exit within", StringComparison.Ordinal));
        }
        finally
        {
            if (hookPid is int pid)
            {
                ForceKill(pid);
            }

            if (File.Exists(startedMarkerPath))
            {
                File.Delete(startedMarkerPath);
            }

            if (File.Exists(pidFilePath))
            {
                File.Delete(pidFilePath);
            }
        }
    }

    /// <summary>
    /// Shared body for the three documented-failure-type tests above: the kill throws
    /// <paramref name="killFailure"/>, and regardless of which documented shape it is, `RunAsync` must
    /// still propagate the original cancellation (not <paramref name="killFailure"/>) and must still log
    /// the failure at Error.
    /// </summary>
    private async Task AssertKillFailureIsHandledAsync(Exception killFailure)
    {
        await _git.RunOrThrowAsync(_repositoryRoot, ["init", "--quiet"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.name", "Seed"]);
        await _git.RunOrThrowAsync(_repositoryRoot, ["config", "user.email", "seed@zerowiki.example"]);

        var startedMarkerPath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-started-{Guid.NewGuid():n}");
        var pidFilePath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-pid-{Guid.NewGuid():n}");
        // Short: the injected kill throws instead of doing anything, so the real hook process is left
        // to exit on its own -- kept brief so it does so well within the post-kill wait bound, isolating
        // this test to the kill-failure log rather than the separate timeout one.
        InstallHookThatSignalsThenSleeps(startedMarkerPath, pidFilePath, sleepSeconds: 1);

        var loggerProvider = new CapturingLoggerProvider();
        var git = new GitProcessRunner(loggerProvider.CreateLogger<GitProcessRunner>(), killEntireProcessTree: _ => throw killFailure);

        try
        {
            using var cts = new CancellationTokenSource();
            var runTask = git.RunAsync(_repositoryRoot, ["commit", "--allow-empty", "-m", "x"], cancellationToken: cts.Token);

            await WaitForFileAsync(startedMarkerPath);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            Assert.Contains(
                loggerProvider.Entries,
                entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains("Failed to kill the git subprocess tree", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(startedMarkerPath))
            {
                File.Delete(startedMarkerPath);
            }

            if (File.Exists(pidFilePath))
            {
                File.Delete(pidFilePath);
            }
        }
    }

    /// <summary>
    /// A <c>pre-commit</c> hook that signals it has started, then <c>exec</c>s into <c>sleep</c> so the
    /// recorded pid stays valid for the process's whole lifetime (no separate child to track) -- used by
    /// the kill-failure tests above, where the injected kill is fake and a real leftover process must be
    /// force-killable by exactly the pid this method hands back.
    /// </summary>
    private void InstallHookThatSignalsThenSleeps(string startedMarkerPath, string pidFilePath, int sleepSeconds)
    {
        var hooksDirectory = Path.Combine(_repositoryRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        File.WriteAllText(
            hookPath,
            $"#!/bin/sh\n" +
            $"echo $$ > '{pidFilePath}'\n" +
            $"touch '{startedMarkerPath}'\n" +
            $"exec sleep {sleepSeconds}\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Real <c>kill -9</c> -- used only to clean up a process this test's own fake "kill" was
    /// deliberately built not to touch, never as part of the production behaviour under test.</summary>
    private static void ForceKill(int pid)
    {
        using var probe = Process.Start(new ProcessStartInfo("kill", ["-9", pid.ToString()])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        probe.WaitForExit();
    }

    /// <summary>
    /// A <c>pre-commit</c> hook that forks a long-sleeping child of its own, records that child's pid,
    /// then signals it has started -- giving the test a synchronization point strictly inside the git
    /// subprocess's own process tree, several levels deep (git -&gt; hook shell -&gt; sleep), so killing
    /// only the immediate git process (not the whole tree) would leave this survivor behind.
    /// </summary>
    private void InstallHookThatForksASleepingGrandchild(string startedMarkerPath, string childPidFilePath, int sleepSeconds)
    {
        var hooksDirectory = Path.Combine(_repositoryRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        File.WriteAllText(
            hookPath,
            $"#!/bin/sh\n" +
            $"sleep {sleepSeconds} &\n" +
            $"echo $! > '{childPidFilePath}'\n" +
            $"touch '{startedMarkerPath}'\n" +
            $"wait $!\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"'{path}' never appeared.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<bool> WaitForProcessToExitAsync(int pid, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(pid))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return !IsProcessAlive(pid);
    }

    /// <summary>
    /// <c>kill -0 pid</c> — the standard POSIX way to ask "does this process exist" without sending it
    /// a real signal (exit 0 if it does, non-zero/ESRCH if it does not). This project's own process
    /// primitives (<see cref="RepositoryWriteLock"/>) are already Unix-only, so this instrument matches
    /// the project's own supported platforms.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        using var probe = Process.Start(new ProcessStartInfo("kill", ["-0", pid.ToString()])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        probe.WaitForExit();
        return probe.ExitCode == 0;
    }
}
