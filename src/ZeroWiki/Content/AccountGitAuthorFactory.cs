using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ZeroWiki.Identity;

namespace ZeroWiki.Content;

/// <summary>
/// Builds the synthetic git author identity a browser save is authored with (D10, D17's authorship
/// paragraph). <see cref="GitAuthor.Email"/> is <c>&lt;localpart&gt;@&lt;<see
/// cref="ContentAuthorshipOptions.HostDomain"/>&gt;</c>; <see cref="GitAuthor.Name"/> is always the raw
/// <see cref="AuthenticatedAccount.Username"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The localpart is constructed in two cases, not one</b> — D10's <em>Consequence binding §6</em>
/// requires a well-formed address for <em>every</em> account, including one whose username predates
/// <see cref="CredentialPolicy.UsernamePattern"/> (D11) and never will be subject to it:
/// <c>LoginServiceTests.cs</c> pins <c>ab</c>, <c>_legacy_</c> and <c>.old.name.</c> still
/// authenticating. A username that already satisfies RFC 5322 <c>dot-atom-text</c> —
/// <c>1*atext *("." 1*atext)</c> — is used directly as the localpart. One that does not (typically a
/// leading or trailing separator, since D11 already refuses the interior cases a dot-atom would also
/// refuse) gets the deterministic fallback in <see cref="SyntheticLocalPartPrefix"/>, disambiguated by
/// the account's own <see cref="AuthenticatedAccount.Id"/> so two legacy usernames can never collapse
/// onto one address.
/// </para>
/// <para>
/// <b>This check is against RFC 5322 <c>dot-atom-text</c> directly, not against
/// <see cref="CredentialPolicy.UsernamePattern"/>.</b> D11's pattern accepts a strict subset of what a
/// dot-atom allows (<c>accepted ⊆ legal</c>, deliberately not the other direction) — <c>_legacy_</c> is a
/// perfectly legal dot-atom localpart and would be wrongly routed to the fallback if this reused D11's
/// pattern as the legality test instead of RFC 5322's own grammar.
/// </para>
/// </remarks>
public sealed partial class AccountGitAuthorFactory(IOptions<ContentAuthorshipOptions> options)
{
    /// <summary>
    /// The fallback localpart prefix for an account whose username is not itself a legal RFC 5322
    /// <c>dot-atom-text</c> localpart. Built around <c>+</c>, which is <c>atext</c> — legal inside a
    /// dot-atom — and excluded from <see cref="CredentialPolicy.UsernamePattern"/>'s charset
    /// (<c>[A-Za-z0-9_.-]</c>) in <em>every</em> version that pattern has ever had, back to the very first
    /// commit that let a person choose a username (<c>d90b00e</c>, predating D11's own tightening).
    /// </summary>
    /// <remarks>
    /// The unreachability is load-bearing, not tidiness: D10's <em>Consequence binding §8.3</em> has the
    /// inbound git-identity resolver match this synthetic form <em>first</em>, ahead of registered
    /// <c>GitEmails</c> rows, so a squatted row can never capture another member's attribution. A fallback
    /// a member could reproduce by <em>choosing</em> a username would reopen that hole from the other
    /// side — which is exactly why this is checked against the charset a username has actually ever been
    /// allowed to hold, not merely against D11's current prose.
    /// </remarks>
    internal const string SyntheticLocalPartPrefix = "account+";

    /// <summary>Builds the author identity for a commit made on <paramref name="account"/>'s behalf.</summary>
    public GitAuthor CreateAuthor(AuthenticatedAccount account)
    {
        var localPart = IsLegalDotAtomText(account.Username)
            ? account.Username
            : $"{SyntheticLocalPartPrefix}{account.Id:N}";

        return new GitAuthor(account.Username, $"{localPart}@{options.Value.HostDomain}");
    }

    /// <summary>
    /// Whether <paramref name="value"/> satisfies RFC 5322 <c>dot-atom-text = 1*atext *("." 1*atext)</c>
    /// in full — no leading, trailing, or doubled dot, and every character either <c>atext</c> or a
    /// separating dot.
    /// </summary>
    private static bool IsLegalDotAtomText(string value) => DotAtomTextPattern().IsMatch(value);

    // atext = ALPHA / DIGIT / "!" / "#" / "$" / "%" / "&" / "'" / "*" / "+" / "-" / "/" / "=" / "?" /
    // "^" / "_" / "`" / "{" / "|" / "}" / "~" (RFC 5322 §3.2.3). \z, never $, for the same reason
    // CredentialPolicy.UsernamePattern uses it: $ also matches immediately before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+\-/=?^_`{|}~]+(\.[A-Za-z0-9!#$%&'*+\-/=?^_`{|}~]+)*\z")]
    private static partial Regex DotAtomTextPattern();
}
