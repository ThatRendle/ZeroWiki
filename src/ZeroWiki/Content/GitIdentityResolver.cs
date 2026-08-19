using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZeroWiki.Data;
using ZeroWiki.Identity;

namespace ZeroWiki.Content;

/// <summary>
/// D19 §5 / D10's <em>Consequence binding §8.3</em>: resolves a pushed commit's raw git author email
/// back to the ZeroWiki account it attributes to. This is <see cref="AccountGitAuthorFactory"/>'s
/// inbound mirror: that type builds one of two synthetic localpart shapes per account outbound, and
/// this type must recognise both of them before it ever consults a registered <see cref="GitEmail"/>
/// row.
/// </summary>
/// <remarks>
/// <b>Consumed at display time, not at index-build time.</b> <c>Components/Pages/WikiPage.razor</c> is
/// the only caller: it injects this type directly (it renders inside a request's own DI scope, so
/// there is no captive-dependency problem taking a Scoped dependency — unlike the long-lived Singleton
/// index/history services) and resolves each page's <c>LastEdit.AuthorEmail</c> once per render.
/// Deliberate — an account resolved into the long-lived <see cref="PageIndex"/> snapshot would be
/// cached against <em>content</em> (stamped to a commit) rather than against <em>identity</em>, and
/// would go stale the moment a member changes their registered <c>GitEmails</c> until something
/// unrelated rebuilt the index. The index keeps the raw git identity exactly as git reports it.
/// </remarks>
/// <remarks>
/// <para>
/// <b>Order: synthetic first (both shapes), <see cref="GitEmailService"/> second, the raw pushed
/// identity last — never the other way round.</b> <c>/account</c> lets any member register any
/// address as their own <see cref="GitEmail"/>, including another member's synthetic one
/// (<see cref="AccountGitAuthorFactory.SyntheticLocalPartPrefix"/>'s remarks). If a
/// <see cref="GitEmailService.FindByEmailAsync"/> lookup ran first, a member who squatted
/// <c>account+&lt;victim's id&gt;@&lt;domain&gt;</c> — or, for a victim whose username is itself a
/// legal dot-atom, the victim's own bare <c>&lt;username&gt;@&lt;domain&gt;</c> — would capture every
/// push attributed to that address on their own account instead. Matching the synthetic form first,
/// against the live account it actually names, closes that regardless of what any <c>GitEmails</c> row
/// claims to be. This defends against a squatted row; it does not and cannot defend against a pusher
/// self-asserting an arbitrary <c>GIT_AUTHOR_EMAIL</c> in the first place — accepted for an
/// invite-only trusted cast (D5), a different threat with a different attacker.
/// </para>
/// <para>
/// <b>The two synthetic shapes, mirroring <see cref="AccountGitAuthorFactory.CreateAuthor"/>
/// exactly.</b> <c>account+&lt;accountId:N&gt;@&lt;domain&gt;</c> is matched against a live account's
/// <see cref="Account.Id"/> directly. <c>&lt;localpart&gt;@&lt;domain&gt;</c> is matched against a
/// live account's <see cref="Account.Username"/> only when that localpart is itself a legal RFC 5322
/// <c>dot-atom-text</c> (<see cref="DotAtomText.IsLegal"/>) — the same test
/// <see cref="AccountGitAuthorFactory"/> applies outbound, not a second copy of it, so the two
/// directions cannot drift apart. Both shapes require the address's domain to equal the configured
/// <see cref="ContentAuthorshipOptions.HostDomain"/>; <c>account+&lt;id&gt;@elsewhere.example</c> is
/// not a synthetic address and is never treated as one.
/// </para>
/// </remarks>
public sealed class GitIdentityResolver(
    IOptions<ContentAuthorshipOptions> options,
    GitEmailService gitEmails,
    IdentityDbContext db)
{
    /// <summary>
    /// Resolves <paramref name="rawAuthorEmail"/> — a pushed commit's <c>GIT_AUTHOR_EMAIL</c>,
    /// self-asserted and untrusted — to the account it attributes to, or <see langword="null"/> when
    /// none of the three routes above claims it. A <see langword="null"/> result is the caller's
    /// signal to fall back to the raw pushed identity (spec: <em>Unknown git email is attributed to
    /// the raw identity</em>); this method never throws for a malformed, empty, or unrecognised
    /// address; attribution must never fail the push it is attributing.
    /// </summary>
    public async Task<GitEmailOwner?> ResolveAsync(
        string? rawAuthorEmail,
        CancellationToken cancellationToken = default)
    {
        var synthetic = await TryResolveSyntheticAsync(rawAuthorEmail, cancellationToken)
            .ConfigureAwait(false);
        if (synthetic is not null)
        {
            return synthetic;
        }

        return await gitEmails.FindByEmailAsync(rawAuthorEmail, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries both synthetic shapes, always ahead of <see cref="GitEmailService"/> (see this type's own
    /// remarks). The two are mutually exclusive by construction — no username D11 or any prior policy
    /// has ever let a member choose contains <c>+</c>
    /// (<see cref="AccountGitAuthorFactory.SyntheticLocalPartPrefix"/>'s own remarks), so a bare
    /// username can never collide with the <c>account+</c> prefix — but nothing here depends on that:
    /// each shape is checked and falls through to the next candidate independently.
    /// </summary>
    private async Task<GitEmailOwner?> TryResolveSyntheticAsync(
        string? rawAuthorEmail,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawAuthorEmail))
        {
            return null;
        }

        var at = rawAuthorEmail.LastIndexOf('@');
        if (at <= 0 || at == rawAuthorEmail.Length - 1)
        {
            return null;
        }

        var localPart = rawAuthorEmail[..at];
        var domain = rawAuthorEmail[(at + 1)..];
        if (!string.Equals(domain, options.Value.HostDomain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (localPart.StartsWith(AccountGitAuthorFactory.SyntheticLocalPartPrefix, StringComparison.Ordinal))
        {
            var idPart = localPart[AccountGitAuthorFactory.SyntheticLocalPartPrefix.Length..];
            if (Guid.TryParseExact(idPart, "N", out var accountId))
            {
                var byId = await db.Accounts
                    .AsNoTracking()
                    .Where(a => a.Id == accountId)
                    .Select(a => new GitEmailOwner(a.Id, a.Username))
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (byId is not null)
                {
                    return byId;
                }
            }
        }

        if (!DotAtomText.IsLegal(localPart))
        {
            return null;
        }

        return await db.Accounts
            .AsNoTracking()
            .Where(a => a.Username == localPart)
            .Select(a => new GitEmailOwner(a.Id, a.Username))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
