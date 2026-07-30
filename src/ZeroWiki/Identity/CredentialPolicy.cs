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

    /// <summary>Matches the <c>Accounts.Username</c> column width.</summary>
    public const int MaximumUsernameLength = 64;

    public const string MaximumUsernameLengthRuleDescription = "A username can be at most 64 characters.";

    /// <summary>
    /// ASCII letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, with at least one alphanumeric
    /// (AD11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constraint is technical rather than stylistic: the git remote presents the username
    /// as the Basic-auth userid, where RFC 7617 makes a colon structurally illegal, and
    /// whitespace or control characters in a credential are a correctness hazard. This is the
    /// minimum charset that rules those out — tightening it later stays backward-compatible in a
    /// way that loosening a username someone already holds does not.
    /// </para>
    /// <para>
    /// Both quantifiers are bounded, and that is what keeps matching constant-time. The obvious
    /// unbounded form (<c>[…]*[…][…]*</c>) is quadratic, because every split point either side of
    /// the required alphanumeric has to be tried; a timeout does not fix that, it only converts
    /// an unbounded burn into a bounded burn plus an exception. The literal <c>63</c> is
    /// <see cref="MaximumUsernameLength"/> minus the one required character — it cannot be
    /// composed from the constant because an attribute argument must itself be a compile-time
    /// constant string, so a test holds the two in step.
    /// </para>
    /// <para>
    /// <c>\z</c>, never <c>$</c>: <c>$</c> also matches immediately before a trailing newline, so
    /// <c>"admin\n"</c> would satisfy a bare <c>Regex.IsMatch</c>. This pattern is public
    /// precisely so other callers reuse it, and it has to be correct without depending on any
    /// particular caller adding a length check of its own.
    /// </para>
    /// </remarks>
    public const string UsernamePattern = @"^[A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z";

    /// <summary>A belt only — the bounded quantifiers above are what keep the work constant.</summary>
    public const int UsernamePatternTimeoutMilliseconds = 250;

    public const string UsernameRuleDescription =
        "A username can use letters, digits, dots, hyphens and underscores, and must contain at least one letter or digit.";

    /// <summary>
    /// <see cref="UsernamePattern"/> for callers outside DataAnnotations, so nobody has to
    /// hand-roll a <see cref="Regex"/> over the raw constant and pick their own options.
    /// </summary>
    [GeneratedRegex(UsernamePattern, RegexOptions.None, UsernamePatternTimeoutMilliseconds)]
    public static partial Regex UsernameMatcher();
}
