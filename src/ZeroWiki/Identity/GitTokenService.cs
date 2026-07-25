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
    /// Resolves a presented git token to its owning account, or <see langword="null"/> when
    /// the token is missing, unknown, or revoked.
    /// </summary>
    /// <remarks>
    /// The presented value is only ever hashed and looked up among issued git tokens, which
    /// is why a login password can never authenticate here — there is no password path to
    /// exclude, only a token path that a password cannot enter.
    /// </remarks>
    public async Task<Account?> VerifyAsync(string? presentedToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return null;
        }

        var tokenHash = tokenGenerator.ComputeHash(presentedToken);

        return await db.GitTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null)
            .Select(t => t.Account)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Lists an account's git tokens, newest first, including revoked ones so the owner can
    /// see the full history.
    /// </summary>
    /// <remarks>
    /// The ordering is applied client-side: SQLite cannot ORDER BY a <see cref="DateTimeOffset"/>,
    /// and an account holds a handful of tokens at most.
    /// </remarks>
    public async Task<IReadOnlyList<GitTokenSummary>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var tokens = await db.GitTokens
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => new GitTokenSummary(t.Id, t.CreatedAt, t.RevokedAt))
            .ToListAsync(cancellationToken);

        return [.. tokens.OrderByDescending(t => t.CreatedAt)];
    }

    /// <summary>
    /// Revokes one of the account's own tokens. Idempotent — revoking an already-revoked
    /// token leaves the original revocation time in place and succeeds. Returns
    /// <see langword="false"/> only when the account has no token with that id.
    /// </summary>
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
