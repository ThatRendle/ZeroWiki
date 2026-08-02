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
        // bound (126) is deliberately looser than MaximumUsernameLength rather than derived from
        // it. The invariant that survives is the direction of the slack — the pattern admits at
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
    // D11: the first and last characters must be alphanumeric, because D10 makes the username the
    // localpart of the commit-author address and a leading or trailing dot is not a dot-atom.
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("_abc")]
    [InlineData("abc_")]
    [InlineData("_x_")]
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

    [Fact]
    public void Username_pattern_rejects_a_very_long_input_without_doing_the_work()
    {
        // The unbounded form of this pattern was quadratic: ~2.3 s at 64 K characters, and a
        // match timeout would only have converted that into a bounded burn plus an exception.
        // Validation is attacker-reachable code on an anonymous route, so the bound is the fix.
        var hostile = new string('a', 1_000_000) + "!";

        var stopwatch = Stopwatch.StartNew();
        var matched = CredentialPolicy.UsernameMatcher().IsMatch(hostile);
        stopwatch.Stop();

        Assert.False(matched);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 100,
            $"Matching a {hostile.Length:N0}-character input took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
