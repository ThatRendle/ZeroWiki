using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Identity;

/// <summary>
/// Issues, verifies, lists, and revokes the per-user git access tokens that serve as the
/// credential for the git remote. Only hashes are persisted.
/// </summary>
public sealed class GitTokenService(
    IdentityDbContext db,
    ISecretTokenGenerator tokenGenerator,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Issues a new git access token for an account. The returned plaintext is the only
    /// copy in existence; the store holds nothing but its hash.
    /// </summary>
    public async Task<IssuedGitToken> IssueAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var secret = tokenGenerator.Generate();

        var token = new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TokenHash = secret.Hash,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.GitTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return new IssuedGitToken(token.Id, secret.Plaintext, token.CreatedAt);
    }

    /// <summary>
    /// Resolves a git-remote credential — a username plus a presented token — to the account
    /// that owns both, or <see langword="null"/> when the username is missing, the token is
    /// missing/unknown/revoked, or the token belongs to a different account than the username
    /// names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The presented value is only ever hashed and looked up among issued git tokens, which
    /// is why a login password can never authenticate here — there is no password path to
    /// exclude, only a token path that a password cannot enter.
    /// </para>
    /// <para>
    /// The username comparison relies on <c>Accounts.Username</c>'s <c>NOCASE</c> collation
    /// rather than a C#-side <c>ToLower()</c> — the same comparison <see cref="LoginService"/>
    /// uses — so this stays the single case-insensitive rule the column already enforces.
    /// Projected into <see cref="AuthenticatedAccount"/> rather than materialised as an
    /// <see cref="Account"/>: the entity carries value-converted timestamps, so a single corrupt
    /// row would turn git authentication into a 500 instead of a clean rejection (AD7).
    /// </para>
    /// </remarks>
    public async Task<AuthenticatedAccount?> VerifyAsync(
        string? username,
        string? presentedToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(presentedToken))
        {
            return null;
        }

        var tokenHash = tokenGenerator.ComputeHash(presentedToken);

        return await db.GitTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null && t.Account!.Username == username)
            .Select(t => new AuthenticatedAccount(t.Account!.Id, t.Account!.Username, t.Account!.IsAdministrator))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Lists an account's git tokens, newest first, including revoked ones so the owner can
    /// see the full history.
    /// </summary>
    public async Task<IReadOnlyList<GitTokenSummary>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        await db.GitTokens
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new GitTokenSummary(t.Id, t.CreatedAt, t.RevokedAt))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Revokes one of the account's own tokens. Idempotent — revoking an already-revoked
    /// token leaves the original revocation time in place and succeeds. Returns
    /// <see langword="false"/> only when the account has no token with that id.
    /// </summary>
    /// <remarks>
    /// Callers must pass <see cref="CancellationToken.None"/> here, not the request's own token
    /// (D1): a revocation abandoned on disconnect would leave the owner believing a git token they
    /// think compromised is dead, while it stays live and able to authenticate.
    /// </remarks>
    public async Task<bool> RevokeAsync(
        Guid accountId,
        Guid tokenId,
        CancellationToken cancellationToken = default)
    {
        var token = await db.GitTokens
            .SingleOrDefaultAsync(t => t.Id == tokenId && t.AccountId == accountId, cancellationToken);

        if (token is null)
        {
            return false;
        }

        if (token.RevokedAt is null)
        {
            token.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
