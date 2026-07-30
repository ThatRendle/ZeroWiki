using System.Security.Claims;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Holds the administrator check to matching the claim's <em>value</em>. Degrading it to a
/// presence check — <c>HasClaim(type)</c>, or a policy built with the bare <c>RequireClaim(type)</c>
/// overload — is a one-token edit that grants administrator rights to a claim of <c>"false"</c>,
/// and every other test in this change would stay green through it.
/// </summary>
public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void A_claim_of_true_grants_administrator_rights() =>
        Assert.True(PrincipalWithAdministratorClaim("true").IsAdministrator());

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData(" true ")]
    [InlineData("0")]
    [InlineData("yes")]
    // Case-sensitive on purpose. ClaimsPrincipal.HasClaim compares the value ordinally, so a
    // differently-cased emitter would lose rights rather than gain them — the safe direction, and
    // pinned here so a change to that direction has to be deliberate.
    [InlineData("True")]
    public void Any_other_claim_value_grants_nothing(string value) =>
        Assert.False(PrincipalWithAdministratorClaim(value).IsAdministrator());

    [Fact]
    public void An_authenticated_principal_without_the_claim_is_not_an_administrator()
    {
        // The shape a signed-in member actually has: Login.razor adds the claim only for
        // administrators, so a member's principal carries no claim of that type at all.
        var member = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")],
            authenticationType: "Test"));

        Assert.False(member.IsAdministrator());
    }

    [Fact]
    public void An_anonymous_principal_is_not_an_administrator() =>
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).IsAdministrator());

    [Fact]
    public void A_claim_of_the_right_value_but_the_wrong_type_grants_nothing()
    {
        var impostor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("is_administrator", ZeroWikiClaims.AdministratorClaimValue)],
            authenticationType: "Test"));

        Assert.False(impostor.IsAdministrator());
    }

    private static ClaimsPrincipal PrincipalWithAdministratorClaim(string value) =>
        new(new ClaimsIdentity(
            [new Claim(ZeroWikiClaims.IsAdministrator, value)],
            authenticationType: "Test"));
}
