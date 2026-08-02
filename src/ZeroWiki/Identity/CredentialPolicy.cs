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
    /// alphanumeric (D11).
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
    /// The alphanumeric ends are what D10 needs: a browser save is authored
    /// <c>username@&lt;host domain&gt;</c>, and a leading or trailing dot is not a legal RFC 5322
    /// dot-atom. Fixing the shape where a username is <em>chosen</em> is what stops that being
    /// patched at every point of use.
    /// </para>
    /// <para>
    /// This pattern governs <strong>shape only</strong>, and its bound is deliberately looser than
    /// <see cref="MaximumUsernameLength"/> — <c>126</c> admits up to 128 characters, so an
    /// over-long username fails the length check and is reported as a length problem rather than
    /// also being told its charset is wrong. The literal is therefore <em>not</em> derived from
    /// the length cap and must not be made to track it; a test pins the direction of the slack
    /// (the pattern admits at least the maximum length), which is the property that matters.
    /// </para>
    /// <para>
    /// The one quantifier is bounded, and that is what keeps matching constant-time. The obvious
    /// unbounded form (<c>[…]*[…][…]*</c>) is quadratic, because every split point either side of
    /// the required alphanumeric has to be tried; a timeout does not fix that, it only converts an
    /// unbounded burn into a bounded burn plus an exception.
    /// </para>
    /// <para>
    /// <c>\z</c>, never <c>$</c>: <c>$</c> also matches immediately before a trailing newline, so
    /// <c>"admin\n"</c> would satisfy a bare <c>Regex.IsMatch</c>. This pattern is public
    /// precisely so other callers reuse it, and it has to be correct without depending on any
    /// particular caller adding a length check of its own.
    /// </para>
    /// </remarks>
    public const string UsernamePattern = @"^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z";

    /// <summary>A belt only — the bounded quantifier above is what keeps the work constant.</summary>
    public const int UsernamePatternTimeoutMilliseconds = 250;

    public const string UsernameRuleDescription =
        "A username can use letters, digits, dots, hyphens and underscores, and must begin and end with a letter or digit.";

    /// <summary>
    /// <see cref="UsernamePattern"/> for callers outside DataAnnotations, so nobody has to
    /// hand-roll a <see cref="Regex"/> over the raw constant and pick their own options.
    /// </summary>
    [GeneratedRegex(UsernamePattern, RegexOptions.None, UsernamePatternTimeoutMilliseconds)]
    public static partial Regex UsernameMatcher();
}
