using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeroWiki.Content;

/// <summary>
/// D16's single cross-process write lock: an advisory <c>flock(2)</c> lock on
/// <see cref="ContentPaths.LockFilePath"/>, held on a raw POSIX file descriptor obtained by P/Invoking
/// <c>libc</c> directly — never <see cref="FileStream"/> or
/// <see cref="File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)"/>. Both of
/// those go through .NET's own Unix open-time locking emulation, which itself calls <c>flock()</c> on
/// every open regardless of the requested <see cref="FileShare"/> value — so they throw
/// <see cref="IOException"/> the instant another process already holds the lock, before this type's own
/// <c>flock()</c> call would ever run (verified by execution; recorded in design.md D16). Serializes the
/// app's own commit path (§5.2) against a concurrent git push — locked, for the duration of the whole
/// push, by the app itself wrapping the <c>git http-backend</c> invocation (§7.5), not by a hook: a
/// <c>flock</c> held inside <c>pre-receive</c> or <c>post-receive</c> would be released when that hook
/// exits, before git updates the working tree, so no hook can be the one holding this lock (design.md
/// D3, D16). The generated hooks must never call <c>flock</c> on this file themselves — see
/// <see cref="GitHookInstaller"/>'s remarks for why doing so would deadlock against their own parent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acquisition is a poll loop, not a single blocking call.</b> <c>flock(2)</c> has no bounded-wait
/// mode — block forever (<c>LOCK_EX</c>) or fail instantly (<c>LOCK_EX|LOCK_NB</c>) — and a blocked
/// <c>flock(2)</c> call cannot be cancelled from .NET without leaking the native thread it runs on.
/// <see cref="AcquireAsync"/> therefore polls <c>LOCK_EX|LOCK_NB</c> on a fixed interval against a
/// wall-clock deadline, composing with the caller's own <see cref="CancellationToken"/> via
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> rather than blocking a thread-pool thread for
/// the whole wait. The push side has no such constraint — the app's own wrapper around
/// <c>git http-backend</c> (§7.5) acquires this same lock with a plain blocking <c>LOCK_EX</c>, a
/// genuine kernel-level unbounded block, taken directly by the app rather than by a hook (design.md D16).
/// </para>
/// <para>
/// <b>Releasing is closing the file descriptor, not a separate unlock call.</b> The kernel drops an
/// advisory <c>flock(2)</c> automatically when the last descriptor referencing it closes — including on
/// process exit or crash — so there is no stale-lock cleanup path either side of this lock needs to
/// remember to run. <see cref="Dispose"/> only closes the descriptor for exactly this reason: making
/// release hard to get wrong is worth more than a symmetrical, separately-callable <c>LOCK_UN</c>.
/// </para>
/// <para>
/// <b>File creation deliberately avoids <c>open(2)</c>'s variadic <c>mode</c> argument.</b> POSIX
/// declares <c>open</c> as <c>int open(const char *pathname, int flags, ...)</c> — the third argument is
/// only read by the callee when <c>O_CREAT</c> is set, and it is variadic. A fixed three-parameter
/// P/Invoke declaration (<c>open(path, flags, mode)</c>) happens to work on Linux, where the AAPCS64 and
/// x86-64 SysV ABIs pass the first several integer arguments in registers whether or not the callee is
/// variadic — but it is silently wrong on Apple Silicon (arm64 macOS), where Apple's ABI requires every
/// variadic argument to be passed on the stack instead. .NET's P/Invoke marshalling has no concept of
/// "this call is variadic" and passes <c>mode</c> the same way as any other fixed argument, so on
/// macOS/arm64 the callee reads garbage off the stack in its place — confirmed by execution: a lockfile
/// created this way came back with permissions <c>r-xr-xr-x</c> instead of the requested <c>0644</c>.
/// This type sidesteps the ABI question rather than depending on it: <c>creat(2)</c> — <c>int
/// creat(const char *pathname, mode_t mode)</c> — is POSIX-standard, available on both platforms, and
/// genuinely non-variadic, so it is used to ensure the lockfile exists. It is called unconditionally on
/// every acquisition; truncating an already-existing zero-byte lockfile is harmless and, also confirmed
/// by execution, does not disturb a lock another process already holds on it. The file is then opened
/// for locking with the two-argument <c>open(path, flags)</c> overload, which needs no mode argument
/// because <c>O_CREAT</c> is never in its flags.
/// </para>
/// </remarks>
public sealed partial class RepositoryWriteLock : IDisposable
{
    private const int LockExclusive = 2; // LOCK_EX
    private const int LockNonBlocking = 4; // LOCK_NB
    private const int ReadWrite = 0x0002; // O_RDWR — identical value on Linux and macOS.
    private const int OwnerReadWriteGroupOtherRead = 420; // 0644, decimal to sidestep C#'s lack of octal literals.

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly int _fileDescriptor;
    private bool _disposed;

    private RepositoryWriteLock(int fileDescriptor)
    {
        _fileDescriptor = fileDescriptor;
    }

    /// <summary>
    /// Opens (creating if absent) and exclusively locks <paramref name="lockFilePath"/>, polling
    /// <c>LOCK_EX|LOCK_NB</c> every 50ms until it succeeds, <paramref name="timeout"/> elapses, or
    /// <paramref name="cancellationToken"/> is cancelled. Always attempts at least once, even when
    /// <paramref name="timeout"/> is <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <exception cref="RepositoryLockTimeoutException">
    /// The lock was not acquired within <paramref name="timeout"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the lock was acquired.
    /// </exception>
    public static async Task<RepositoryWriteLock> AcquireAsync(
        string lockFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must not be negative.");
        }

        var fileDescriptor = OpenLockFile(lockFilePath);

        try
        {
            // Measures real elapsed time, not a sum of requested Task.Delay durations: Task.Delay only
            // guarantees a *minimum* wait, not an exact one, so accumulating the requested durations
            // instead of the deadline drifts under scheduler pressure — precisely the condition a
            // contended lock produces. Re-reading the Stopwatch on every iteration also makes the loop
            // self-correcting: an overrun on one Task.Delay call shortens the next iteration's computed
            // remaining time, rather than compounding across iterations.
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryAcquire(fileDescriptor))
                {
                    return new RepositoryWriteLock(fileDescriptor);
                }

                var elapsed = stopwatch.Elapsed;
                if (elapsed >= timeout)
                {
                    throw new RepositoryLockTimeoutException(lockFilePath, timeout);
                }

                var remaining = timeout - elapsed;
                var delay = PollInterval <= remaining ? PollInterval : remaining;

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            CloseIfOpen(fileDescriptor);
            throw;
        }
    }

    /// <summary>Releases the lock by closing the file descriptor (see remarks above).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseIfOpen(_fileDescriptor);
    }

    private static bool TryAcquire(int fileDescriptor) =>
        Flock(fileDescriptor, LockExclusive | LockNonBlocking) == 0;

    private static int OpenLockFile(string lockFilePath)
    {
        // creat(2) is non-variadic, unlike open(2)'s O_CREAT form — see this type's remarks.
        var createDescriptor = Creat(lockFilePath, OwnerReadWriteGroupOtherRead);
        if (createDescriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new IOException($"Failed to create the write lock file '{lockFilePath}' (errno {errno}).");
        }

        CloseIfOpen(createDescriptor);

        var fileDescriptor = Open(lockFilePath, ReadWrite);
        if (fileDescriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new IOException($"Failed to open the write lock file '{lockFilePath}' (errno {errno}).");
        }

        return fileDescriptor;
    }

    private static void CloseIfOpen(int fileDescriptor)
    {
        if (fileDescriptor >= 0)
        {
            Close(fileDescriptor);
        }
    }

    [LibraryImport("libc", EntryPoint = "creat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Creat(string pathname, int mode);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int fileDescriptor, int operation);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fileDescriptor);
}
