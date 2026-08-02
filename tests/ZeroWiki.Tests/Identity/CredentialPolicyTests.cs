using System.Diagnostics;
using System.Globalization;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Holds the shared credential rules to their stated numbers, and holds the username pattern to
/// being constant-time — it runs on an anonymously reachable route before any cheaper check.
/// </summary>
public sealed class CredentialPolicyTests
{
    [Fact]
    public void Rule_messages_state_the_numbers_they_are_paired_with()
    {
        // An interpolated const cannot embed an int, so the numbers appear twice. This is what
        // stops raising one and leaving the other telling users something untrue.
        Assert.Contains(
            CredentialPolicy.MinimumPasswordLength.ToString(CultureInfo.InvariantCulture),
            CredentialPolicy.MinimumPasswordLengthRuleDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            CredentialPolicy.MaximumPasswordLength.ToString(CultureInfo.InvariantCulture),
            CredentialPolicy.MaximumPasswordLengthRuleDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            CredentialPolicy.MaximumUsernameLength.ToString(CultureInfo.InvariantCulture),
            CredentialPolicy.MaximumUsernameLengthRuleDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            CredentialPolicy.MinimumUsernameLength.ToString(CultureInfo.InvariantCulture),
            CredentialPolicy.MinimumUsernameLengthRuleDescription,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Username_pattern_admits_more_than_the_maximum_length()
    {
        // D11 severs the coupling this test used to guard: the pattern governs shape only, and its
        // bound (125, admitting 127 unbroken alphanumerics) is deliberately looser than
        // MaximumUsernameLength rather than derived from it. The invariant that survives is the
        // direction of the slack — the pattern admits at
        // least the maximum length, so an over-long name always fails the length check and is
        // reported as a length problem, never as a shape one, and raising the column width can
        // never turn a length fault into a shape fault.
        var longest = new string('a', CredentialPolicy.MaximumUsernameLength);
        var overlong = new string('a', CredentialPolicy.MaximumUsernameLength + 1);

        Assert.Matches(CredentialPolicy.UsernameMatcher(), longest);
        Assert.Matches(CredentialPolicy.UsernameMatcher(), overlong);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("a.b-c_1")]
    [InlineData("A1")]
    [InlineData("x")]
    [InlineData("1")]
    // Single dots between runs of other characters are what a dot-atom is made of, so forbidding
    // *consecutive* dots must not have cost these. Every one of them was accepted before the
    // consecutive-dot rule and still is.
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData("a-_-b")]
    [InlineData("a.-.b")]
    [InlineData("a._.b")]
    public void Username_pattern_accepts_permitted_values(string username) =>
        Assert.Matches(CredentialPolicy.UsernameMatcher(), username);

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData(":")]
    [InlineData("admin:")]
    [InlineData(":admin")]
    [InlineData("___")]
    [InlineData("...")]
    [InlineData("café")]
    [InlineData("admin@host")]
    [InlineData("admin/../x")]
    [InlineData("admin\t")]
    [InlineData("\nadmin")]
    // A trailing newline is the case a `$`-anchored pattern would wrongly accept.
    [InlineData("admin\n")]
    // D11: D10 makes the username the localpart of the commit-author address, and RFC 5322
    // `dot-atom-text` is `1*atext *("." 1*atext)` where `.` is not `atext` — so a dot is legal
    // only *between* runs of other characters. The first and last characters must therefore be
    // alphanumeric...
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("_abc")]
    [InlineData("abc_")]
    [InlineData("_x_")]
    // ...and no two dots may be adjacent, which is the same grammar rule applied to the middle
    // rather than the ends. `a..b` is exactly as illegal a dot-atom as `.ab`; the original D11
    // pattern fixed the ends and said nothing about the middle, so these were accepted.
    [InlineData("a..b")]
    [InlineData("a...b")]
    [InlineData("ab..cd")]
    [InlineData("a..")]
    [InlineData("..a")]
    [InlineData("a.b..c")]
    public void Username_pattern_rejects_disallowed_values(string username) =>
        Assert.DoesNotMatch(CredentialPolicy.UsernameMatcher(), username);

    [Theory]
    [InlineData("ab")]
    [InlineData("a")]
    public void Username_pattern_says_nothing_about_a_username_that_is_merely_too_short(string username) =>
        // The floor is a length rule, not part of the pattern, so that "ab" is told it is too
        // short rather than told its character set is wrong. If the minimum ever migrates into the
        // pattern, this test fails and the two messages have quietly merged again.
        Assert.Matches(CredentialPolicy.UsernameMatcher(), username);

    /// <summary>
    /// Three character shapes, each a million characters and each refused. What this theory pins
    /// is that no input makes matching <em>catastrophic</em> — not that matching is constant, and
    /// not that any particular rewrite of the pattern would be rejected. See the remarks on
    /// <see cref="CredentialPolicy.UsernamePattern"/> for what is deliberately left unguarded.
    /// </summary>
    public static TheoryData<string, string> HostileInputs() => new()
    {
        // The shape that defeated the unbounded ancestor of this pattern, which had two free runs
        // either side of a required alphanumeric and so had to try every split point between them.
        // On the [GeneratedRegex] engine this suite actually runs, that form takes ~23 s on this
        // row — over 200x the limit below, so it is caught with room to spare. (Quoted on the
        // shipping engine deliberately: the same measurement interpreted reads ~2.5 s at 64 K
        // characters, and mixing the two engines' figures is what made the claim this comment
        // replaces wrong.)
        { "unbroken run", new string('a', 1_000_000) + "!" },
        // Dotted shapes, which the unbroken run does not exercise at all — the consecutive-dot
        // rule is expressed by alternation, so these are the inputs that walk both branches. They
        // pin the pattern's cost on dotted input; they do not discriminate between candidate
        // rewrites. Measured on the shipping engine, a `(?!.*\.\.)` lookahead form passes every
        // row here (~0.1 ms) and an unbounded dot branch passes every row too, its *worst* row
        // being the unbroken run above rather than either of these. Neither is refused by any test
        // in this suite.
        { "alternating dots", "a" + string.Concat(Enumerable.Repeat(".a", 500_000)) + "!" },
        { "every separator", "a" + string.Concat(Enumerable.Repeat("a-_.", 250_000)) + "!" },
    };

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void Username_pattern_rejects_a_very_long_input_without_doing_the_work(string shape, string hostile)
    {
        // A match timeout would only have converted an unbounded burn into a bounded burn plus an
        // exception. Validation is attacker-reachable code on an anonymous route, so bounding
        // every quantifier is the fix and this is what holds it bounded.
        var stopwatch = Stopwatch.StartNew();
        var matched = CredentialPolicy.UsernameMatcher().IsMatch(hostile);
        stopwatch.Stop();

        Assert.False(matched);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 100,
            $"Matching a {hostile.Length:N0}-character {shape} input took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
