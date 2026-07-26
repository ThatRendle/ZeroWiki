using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Identity;

/// <summary>
/// Verifies a submitted username and password against the store. Every rejection returns the
/// same <see langword="null"/> and costs the same work; the reason is recorded in the log, where
/// an operator can see it and a visitor cannot.
/// </summary>
public sealed class LoginService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    ILogger<LoginService> logger)
{
    /// <summary>
    /// A well-formed Argon2id hash carrying the same cost parameters as live hashes, of a random
    /// preimage that was discarded when it was generated.
    /// </summary>
    /// <remarks>
    /// Verifying against this is what makes an unknown username cost the same as a known one.
    /// It is a <em>precomputed constant</em> on purpose: deriving a throwaway hash per request
    /// would cost a hash <em>and</em> a verify, making the miss path slower than the hit path —
    /// the same oracle, inverted.
    /// </remarks>
    private const string DummyPasswordHash =
        "$argon2id$v=19$m=65536,t=3,p=1$N1ary1Ow2xV54Re3E9zwaA$539C+wuF/uAd4MQ8oC/xatYuoQynN6b3Zlgm6ORzF68";

    /// <summary>
    /// Resolves a username and password to an account, or <see langword="null"/> if the
    /// credentials are not valid for any reason.
    /// </summary>
    /// <remarks>
    /// Exactly one <see cref="IPasswordHasher.Verify"/> call happens on every path through this
    /// method, including the ones that have already decided to fail. That is the requirement:
    /// an unknown username, a known username with an unusable stored hash, and a known username
    /// with the wrong password must be indistinguishable in time as well as in the response.
    /// </remarks>
    public async Task<AuthenticatedAccount?> VerifyCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Projected, never materialised as an Account: the entity carries value-converted
        // timestamps, so a single corrupt column would throw here and turn one account's login
        // into a 500 while every other failure returns the uniform rejection. A projection
        // cannot do that, and a password check has no business reading timestamps anyway.
        var candidate = await db.Accounts
            .AsNoTracking()
            .Where(a => a.Username == username)
            .Select(a => new
            {
                a.Id,
                a.Username,
                a.PasswordHash,
                a.IsAdministrator,
            })
            .SingleOrDefaultAsync(cancellationToken);

        var storedHashIsUsable = candidate is not null && passwordHasher.CanVerify(candidate.PasswordHash);
        var hashToVerify = storedHashIsUsable && candidate is not null
            ? candidate.PasswordHash
            : DummyPasswordHash;

        var verified = passwordHasher.Verify(password, hashToVerify);

        if (candidate is null)
        {
            logger.LogInformation(
                "Login rejected: no account with username {Username}.",
                username);
            return null;
        }

        if (!storedHashIsUsable)
        {
            // Otherwise permanently silent: the account holder reports "I cannot log in" and
            // every diagnostic looks exactly like a wrong password.
            logger.LogError(
                "Login rejected: the stored password hash for account {AccountId} is unusable and cannot be verified against. The account must be re-provisioned.",
                candidate.Id);
            return null;
        }

        if (!verified)
        {
            logger.LogInformation(
                "Login rejected: wrong password for account {AccountId}.",
                candidate.Id);
            return null;
        }

        logger.LogInformation("Login accepted for account {AccountId}.", candidate.Id);

        return new AuthenticatedAccount(candidate.Id, candidate.Username, candidate.IsAdministrator);
    }
}
