namespace ZeroWiki.Content;

/// <summary>
/// Thrown by <see cref="RepositoryWriteLock.AcquireAsync"/> when D16's single cross-process write lock
/// could not be acquired within the caller's bound — distinct from <see cref="OperationCanceledException"/>,
/// which means the caller's own <see cref="CancellationToken"/> fired, not that the wait bound elapsed.
/// </summary>
public sealed class RepositoryLockTimeoutException : Exception
{
    public RepositoryLockTimeoutException(string lockFilePath, TimeSpan timeout)
        : base(
            $"Timed out after {timeout} waiting to acquire the repository write lock at " +
            $"'{lockFilePath}'.")
    {
        LockFilePath = lockFilePath;
        Timeout = timeout;
    }

    /// <summary>The lockfile that could not be acquired in time (<see cref="ContentPaths.LockFilePath"/>).</summary>
    public string LockFilePath { get; }

    /// <summary>The bound that elapsed (<see cref="ContentStorageOptions.WriteLockTimeout"/>).</summary>
    public TimeSpan Timeout { get; }
}
