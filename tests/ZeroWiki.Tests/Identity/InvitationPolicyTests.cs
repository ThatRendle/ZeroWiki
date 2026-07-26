using System.Globalization;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Holds the invitation lifetime to the number the page tells people it is. The value is quoted
/// in three places — the store, the copy, and the expiry check — and only a shared constant plus
/// this test keeps them saying the same thing.
/// </summary>
public sealed class InvitationPolicyTests
{
    [Fact]
    public void The_lifetime_rule_states_the_number_it_is_paired_with() =>
        Assert.Contains(
            InvitationPolicy.LifetimeDays.ToString(CultureInfo.InvariantCulture),
            InvitationPolicy.LifetimeRuleDescription,
            StringComparison.Ordinal);

    [Fact]
    public void The_lifetime_is_the_stated_number_of_days() =>
        Assert.Equal(TimeSpan.FromDays(InvitationPolicy.LifetimeDays), InvitationPolicy.Lifetime);

    [Fact]
    public void The_redemption_path_is_a_site_relative_route() =>
        Assert.StartsWith("/", InvitationPolicy.RedemptionPath, StringComparison.Ordinal);
}
