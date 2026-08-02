using System.Text.RegularExpressions;

namespace ZeroWiki.Identity;

/// <summary>
/// The rules a user-chosen credential must satisfy. Shared so that the two paths where a
/// person picks their own username and password — the first-administrator bootstrap and
/// invitation redemption — cannot drift apart.
/// </summary>
/// <remarks>
/// These rules run on anonymously reachable routes, so their <em>cost</em> is part of their
/// specification: validation is attacker-reachable code and it executes before any of the cheap
/// pre-filters deeper in. Anything added here has to be constant-time in the length of the input
/// it is handed, which is not the same as being bounded by a timeout.
/// </remarks>
public static partial class CredentialPolicy
{
    /// <summary>
    /// Minimum password length (AD10). Length only: no composition rules, no strength meter.
    /// Argon2id makes offline cracking expensive, but a very short password is guessable online
    /// in a handful of requests and there is no rate limit in front of it.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>
    /// Kept beside the number it quotes, because an interpolated <c>const</c> cannot embed an
    /// <c>int</c>. A test asserts the two still agree.
    /// </summary>
    public const string MinimumPasswordLengthRuleDescription = "A password must be at least 12 characters.";

    public const int MaximumPasswordLength = 256;

    public const string MaximumPasswordLengthRuleDescription = "A password can be at most 256 characters.";

    /// <summary>
    /// Minimum username length (D11). A length rule rather than part of the pattern, for the same
    /// reason <see cref="MinimumPasswordLength"/> is: a username that is merely too short should
    /// be told so, not told its character set is wrong.
    /// </summary>
    public const int MinimumUsernameLength = 3;

    /// <summary>
    /// Kept beside the number it quotes, because an interpolated <c>const</c> cannot embed an
    /// <c>int</c>. A test asserts the two still agree.
    /// </summary>
    public const string MinimumUsernameLengthRuleDescription = "A username must be at least 3 characters.";

    /// <summary>Matches the <c>Accounts.Username</c> column width.</summary>
    public const int MaximumUsernameLength = 64;

    public const string MaximumUsernameLengthRuleDescription = "A username can be at most 64 characters.";

    /// <summary>
    /// ASCII letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, beginning and ending with an
    /// alphanumeric, with no two dots in a row (D11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The charset constraint is technical rather than stylistic: the git remote presents the
    /// username as the Basic-auth userid, where RFC 7617 makes a colon structurally illegal, and
    /// whitespace or control characters in a credential are a correctness hazard. This is the
    /// minimum charset that rules those out — tightening it later stays backward-compatible in a
    /// way that loosening a username someone already holds does not.
    /// </para>
    /// <para>
    /// The dot rules are what D10 needs: a browser save is authored
    /// <c>username@&lt;host domain&gt;</c>, and RFC 5322 <c>dot-atom-text</c> is
    /// <c>1*atext *("." 1*atext)</c> — a dot may only appear <em>between</em> runs of
    /// <c>atext</c>, and <c>.</c> is not itself <c>atext</c>. So a leading dot, a trailing dot and
    /// a <em>doubled</em> dot are all equally illegal: <c>a..b</c> is no more a dot-atom than
    /// <c>.ab</c> is. Each alternation branch below that starts with a dot also consumes the
    /// character after it, and that character is never a dot — which is how a doubled dot is
    /// excluded without a lookahead. Fixing the shape where a username is <em>chosen</em> is what
    /// stops it being patched at every point of use.
    /// </para>
    /// <para>
    /// What this pattern accepts is a <strong>strict subset</strong> of what a dot-atom allows,
    /// and that is deliberate. The property D10 needs is that everything accepted is legal, not
    /// that everything legal is accepted: <c>_legacy_</c> is a perfectly good localpart and is
    /// refused anyway, because a rule protecting a permanent artifact is allowed to be narrower
    /// than the grammar it protects. Do not read the subset relation as an equality and "fix" the
    /// ends rule to match.
    /// </para>
    /// <para>
    /// This pattern governs <strong>shape only</strong>, and its bound is deliberately looser than
    /// <see cref="MaximumUsernameLength"/> — <c>125</c> admits 127 characters of unbroken
    /// alphanumerics (253 with a dot between every pair), so an over-long username fails the
    /// length check and is reported as a length problem rather than also being told its charset is
    /// wrong. The literal is therefore <em>not</em> derived from the length cap and must not be
    /// made to track it; a test pins the direction of the slack (the pattern admits at least the
    /// maximum length), which is the property that matters.
    /// </para>
    /// <para>
    /// Every quantifier is bounded, and the two branches inside the bounded run are disjoint on
    /// their first character (dot versus not), so the run never branches. Measured on the
    /// <c>[GeneratedRegex]</c> engine this type actually uses: a one-million-character hostile
    /// input costs no more than a one-thousand-character one — the measured ratio sits at the
    /// stopwatch's noise floor rather than above it — so the work really is independent of the
    /// input's length. The obvious unbounded form (<c>[…]*[…][…]*</c>) is
    /// quadratic instead, because every split point either side of the required alphanumeric has
    /// to be tried; a timeout does not fix that, it only converts an unbounded burn into a bounded
    /// burn plus an exception.
    /// </para>
    /// <para>
    /// What the test suite pins is the weaker property that no input makes matching
    /// <em>catastrophic</em> — not the stronger one that its cost is constant. At least two
    /// rewrites are correct, slower, and refused by nothing: expressing the consecutive-dot rule
    /// as a <c>(?!.*\.\.)</c> lookahead over the older pattern, and leaving this pattern's bounded
    /// run unbounded. Measured on the <c>[GeneratedRegex]</c> engine that ships (not the
    /// interpreted one — the two disagree by orders of magnitude here), against a
    /// million-character input: the lookahead stays well under a hundredth of the limit the timing
    /// test asserts, and the unbounded form under a fifth of it. Both are stated against that
    /// limit rather than against this pattern's own cost, which is too small to divide by. So
    /// neither is a denial-of-service risk and neither fails a test. Prefer the bounded form; do
    /// not assume a test will stop you replacing it.
    /// </para>
    /// <para>
    /// <c>\z</c>, never <c>$</c>: <c>$</c> also matches immediately before a trailing newline, so
    /// <c>"admin\n"</c> would satisfy a bare <c>Regex.IsMatch</c>. This pattern is public
    /// precisely so other callers reuse it, and it has to be correct without depending on any
    /// particular caller adding a length check of its own.
    /// </para>
    /// </remarks>
    public const string UsernamePattern =
        @"^[A-Za-z0-9](([A-Za-z0-9_-]|\.[A-Za-z0-9_-]){0,125}([A-Za-z0-9]|\.[A-Za-z0-9]))?\z";

    /// <summary>A belt only — the bounded quantifier above is what keeps the work constant.</summary>
    public const int UsernamePatternTimeoutMilliseconds = 250;

    /// <summary>
    /// The single message every shape fault reports. It has to name each rule the pattern
    /// enforces, or a name refused for one of them is told about a rule it did not break.
    /// Deliberately free of characters HTML-encoding would alter, because the form surfaces it
    /// verbatim and tests assert it against the rendered body.
    /// </summary>
    public const string UsernameRuleDescription =
        "A username can use letters, digits, dots, hyphens and underscores, must begin and end with a letter or digit, and cannot contain two dots in a row.";

    /// <summary>
    /// <see cref="UsernamePattern"/> for callers outside DataAnnotations, so nobody has to
    /// hand-roll a <see cref="Regex"/> over the raw constant and pick their own options.
    /// </summary>
    [GeneratedRegex(UsernamePattern, RegexOptions.None, UsernamePatternTimeoutMilliseconds)]
    public static partial Regex UsernameMatcher();
}
