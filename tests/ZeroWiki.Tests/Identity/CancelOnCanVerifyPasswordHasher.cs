using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Cancels a linked <see cref="CancellationTokenSource"/> the instant <see cref="CanVerify"/> is
/// invoked, landing the cancellation exactly where <c>LoginService.VerifyCredentialsAsync</c>'s
/// account lookup has already completed but its Argon2id <see cref="Verify"/> call has not yet
/// run (F2).
/// </summary>
/// <remarks>
/// EF Core gives no clean hook onto "the lookup awaited without a query-level interceptor"; this
/// uses a seam that already exists — <c>IPasswordHasher</c> is injected — rather than reaching for
/// one that does not, and it lands the cancellation on the caller-visible boundary the fix actually
/// checks against, not merely somewhere inside the method.
/// </remarks>
public sealed class CancelOnCanVerifyPasswordHasher(
    IPasswordHasher inner,
    CancellationTokenSource cancellationTokenSource) : IPasswordHasher
{
    /// <summary>How many times <see cref="Verify"/> was called.</summary>
    public int VerifyCallCount { get; private set; }

    public string Hash(string password) => inner.Hash(password);

    public bool CanVerify(string? storedHash)
    {
        cancellationTokenSource.Cancel();

        return inner.CanVerify(storedHash);
    }

    public bool Verify(string password, string? storedHash)
    {
        VerifyCallCount++;

        return inner.Verify(password, storedHash);
    }
}
