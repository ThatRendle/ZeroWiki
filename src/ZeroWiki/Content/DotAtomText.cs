using System.Text.RegularExpressions;

namespace ZeroWiki.Content;

/// <summary>
/// RFC 5322 <c>dot-atom-text = 1*atext *("." 1*atext)</c> legality, shared verbatim between
/// <see cref="AccountGitAuthorFactory"/> (outbound: which localpart shape a commit is authored with)
/// and <see cref="GitIdentityResolver"/> (inbound: which localpart shape a pushed commit's author
/// email is matched against) so the two directions cannot independently drift into disagreement
/// (D19 §5) -- there is exactly one place this grammar is encoded, not two copies that could diverge.
/// </summary>
internal static partial class DotAtomText
{
    /// <summary>
    /// Whether <paramref name="value"/> satisfies RFC 5322 <c>dot-atom-text</c> in full -- no leading,
    /// trailing, or doubled dot, and every character either <c>atext</c> or a separating dot.
    /// </summary>
    public static bool IsLegal(string value) => Pattern().IsMatch(value);

    // atext = ALPHA / DIGIT / "!" / "#" / "$" / "%" / "&" / "'" / "*" / "+" / "-" / "/" / "=" / "?" /
    // "^" / "_" / "`" / "{" / "|" / "}" / "~" (RFC 5322 §3.2.3). \z, never $, for the same reason
    // CredentialPolicy.UsernamePattern uses it: $ also matches immediately before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+\-/=?^_`{|}~]+(\.[A-Za-z0-9!#$%&'*+\-/=?^_`{|}~]+)*\z")]
    private static partial Regex Pattern();
}
