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
    }

    [Fact]
    public void Username_pattern_admits_exactly_the_maximum_length()
    {
        // The pattern's bounded quantifiers are 63 + 1 + 63, a literal that has to track
        // MaximumUsernameLength. If the constant is raised without widening them, a legal
        // username starts being rejected by the pattern instead of accepted.
        var longest = new string('a', CredentialPolicy.MaximumUsernameLength);

        Assert.Matches(CredentialPolicy.UsernameMatcher(), longest);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("a.b-c_1")]
    [InlineData("_x_")]
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
    public void Username_pattern_rejects_disallowed_values(string username) =>
        Assert.DoesNotMatch(CredentialPolicy.UsernameMatcher(), username);

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
