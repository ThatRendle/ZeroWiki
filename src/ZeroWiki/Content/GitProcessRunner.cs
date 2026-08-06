using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZeroWiki.Content;

/// <summary>
/// Runs <c>git</c> as a subprocess against a working directory, capturing stdout/stderr rather than
/// inheriting the parent's console. The single place every content-repository git invocation goes
/// through — repository bootstrap here, and later <c>log</c>/<c>blame</c>, commit-on-save, and the
/// Smart HTTP remote — so a process is only ever started one way.
/// </summary>
public sealed class GitProcessRunner
{
    /// <summary>
    /// How long <see cref="KillProcessTreeAsync"/> waits for the kernel to confirm a killed tree is
    /// actually gone before giving up and letting the caller's cancellation propagate anyway. Not a
    /// configuration option (an Architect decision, §6 block D2 remediation) — a process stuck in an
    /// uninterruptible kernel wait cannot be preempted by any timeout, and the wait exists only to
    /// confirm the common case promptly, not to guarantee reaping in the uncommon one. A few seconds
    /// comfortably covers ordinary scheduler contention (SIGKILL against an unstuck process was
    /// observed, during this block's review, to reap in tens of milliseconds) without hanging a
    /// request thread indefinitely on the rare process this can never confirm.
    /// </summary>
    private static readonly TimeSpan PostKillWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<GitProcessRunner> _logger;

    /// <summary>
    /// The actual OS-level kill call, substitutable only for tests: <c>Process.Kill(entireProcessTree:
    /// true)</c> throwing its documented failure (a permission-denied descendant, the only reproduction
    /// found — <c>Process.GetProcessById(1).Kill(entireProcessTree: true)</c> as an unprivileged user)
    /// is not something a test can safely provoke against a process it does not own without either
    /// requiring root or risking a real system process. Injecting the call itself lets a test exercise
    /// the real <see cref="RunAsync"/> path end to end -- the drains, the logging, the rethrown
    /// <see cref="OperationCanceledException"/> -- while substituting only the one step neither this
    /// process nor CI is safe to fake by actually running it.
    /// </summary>
    private readonly Action<Process> _killEntireProcessTree;

    public GitProcessRunner(ILogger<GitProcessRunner>? logger = null, Action<Process>? killEntireProcessTree = null)
    {
        _logger = logger ?? NullLogger<GitProcessRunner>.Instance;
        _killEntireProcessTree = killEntireProcessTree ?? (process => process.Kill(entireProcessTree: true));
    }

    /// <summary>
    /// Starts <c>git</c> with <paramref name="arguments"/> as a genuine argument array — never a
    /// concatenated string a shell would re-split — and waits for it to exit.
    /// </summary>
    /// <param name="environmentVariables">
    /// Variables added on top of the inherited environment, e.g. <c>GIT_AUTHOR_*</c>/
    /// <c>GIT_COMMITTER_*</c> so a commit's identity never depends on ambient git configuration.
    /// </param>
    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // §6 obligation 8: `using` above only releases this type's own handle when the method
            // returns -- it does not signal the OS process, so leaving it at that orphans a live
            // `git` (verified by execution: a disposed Process whose wait was cancelled reports
            // "still alive: True"). Kill the whole tree, not just this one process: git forks
            // children of its own (pack helpers, hooks, and §7's `http-backend`), and killing only
            // the parent would just move the orphan one level down.
            await KillProcessTreeAsync(process, arguments).ConfigureAwait(false);

            // These reads share this call's own cancellationToken, so they are already cancelled or
            // about to be -- observed and discarded here so neither is left referencing a stream on
            // a `Process` the `using` above is about to dispose. The caller asked to stop, not to
            // see whatever partial output git had produced.
            await ObserveAsync(standardOutputTask).ConfigureAwait(false);
            await ObserveAsync(standardErrorTask).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        return new GitProcessResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    /// Kills <paramref name="process"/> and every process it spawned, then waits, bounded by
    /// <see cref="PostKillWaitTimeout"/>, for the kernel to finish tearing the tree down. Never lets
    /// anything it does here surface as a different exception than the
    /// <see cref="OperationCanceledException"/> the caller is already propagating -- a kill failure or
    /// a reap that never completes is logged, not thrown, because an orphaned process is a strictly
    /// better outcome to report than replacing the cancellation the caller is waiting on.
    /// </summary>
    private async Task KillProcessTreeAsync(Process process, IReadOnlyList<string> arguments)
    {
        try
        {
            if (!process.HasExited)
            {
                _killEntireProcessTree(process);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or Win32Exception)
        {
            // Block B's exit-code posture, transposed from git's exit codes to .NET's exception
            // contract: trust an API only for what it documents, not for what was expected. Two
            // documented shapes, not one: `InvalidOperationException` when the process already exited
            // (the ordinary "lost the race" case between the `HasExited` check above and the call
            // below, or between the caller's cancellation firing and this method running at all --
            // nothing left to kill, not a fault); and, verified by execution
            // (`Process.GetProcessById(1).Kill(entireProcessTree: true)` as an unprivileged user), an
            // `AggregateException` wrapping one `Win32Exception` per descendant the tree walk could not
            // signal -- reachable in production from a hook that drops privilege or a child reparented
            // under a policy this process cannot touch, never from the save path (D2's other half
            // always passes `CancellationToken.None`, so this catch is never entered for a save's own
            // git calls). Logged, not swallowed: a descendant may now be orphaned, and that is
            // operationally significant even though the caller's own cancellation still holds.
            _logger.LogError(
                ex,
                "Failed to kill the git subprocess tree (pid {ProcessId}, arguments '{Arguments}') " +
                "after cancellation; a descendant may still be running.",
                TryGetProcessId(process),
                FormatArguments(arguments));
        }

        // Bounded, not the caller's token: the token that governed this invocation is the one that
        // just fired, so waiting on it again here would throw immediately without ever confirming the
        // process is actually gone. A process stuck in an uninterruptible kernel wait cannot be
        // preempted by SIGKILL or by this timeout -- the bound exists to stop confirming, not to make
        // the process exit, so on expiry this reports the orphan and returns rather than hanging the
        // caller's request thread forever.
        using var timeoutCancellation = new CancellationTokenSource(PostKillWaitTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            _logger.LogError(
                "git subprocess (pid {ProcessId}, arguments '{Arguments}') did not exit within " +
                "{Timeout} after being killed; it may be stuck (e.g. an uninterruptible kernel wait) " +
                "and is now orphaned.",
                TryGetProcessId(process),
                FormatArguments(arguments),
                PostKillWaitTimeout);
        }
    }

    /// <summary><see cref="Process.Id"/> throws once a process is far enough gone; this call site never
    /// needs the pid badly enough to fail the logging it is decorating.</summary>
    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string FormatArguments(IReadOnlyList<string> arguments) => string.Join(' ', arguments);

    /// <summary>
    /// Awaits <paramref name="task"/> and discards any exception. Used only on the cancellation path
    /// above, where the stdout/stderr reads have already been cancelled (or are about to be) by the
    /// same token that cancelled the wait -- this exists solely to prevent an unobserved task
    /// exception, not to report anything the caller would act on.
    /// </summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>Runs git and throws <see cref="GitProcessException"/> if it exits non-zero.</summary>
    public async Task<GitProcessResult> RunOrThrowAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingDirectory, arguments, environmentVariables, cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitProcessException(arguments, result.ExitCode, result.StandardError);
        }

        return result;
    }
}
