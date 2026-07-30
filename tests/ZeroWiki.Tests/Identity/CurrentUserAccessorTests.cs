using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Exercises <see cref="CurrentUserAccessor"/> against a bare <see cref="ClaimsPrincipal"/> — the
/// principal-only handle §8.3 is (PO decision 2026-07-30): no database, no HttpContext beyond the
/// claims login already mints.
/// </summary>
public sealed class CurrentUserAccessorTests
{
    [Fact]
    public void Anonymous_visitor_has_no_current_user()
    {
        var accessor = new CurrentUserAccessor(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        Assert.Null(accessor.GetCurrent());
    }

    [Fact]
    public void Outside_a_request_there_is_no_current_user()
    {
        var accessor = new CurrentUserAccessor(new HttpContextAccessor());

        Assert.Null(accessor.GetCurrent());
    }

    [Fact]
    public void Signed_in_administrator_resolves_id_username_and_the_administrator_flag()
    {
        var id = Guid.NewGuid();
        var accessor = new CurrentUserAccessor(
            new HttpContextAccessor { HttpContext = ContextFor(id, "alice", isAdministrator: true) });

        var current = accessor.GetCurrent();

        Assert.NotNull(current);
        Assert.Equal(id, current.Id);
        Assert.Equal("alice", current.Username);
        Assert.True(current.IsAdministrator);
    }

    [Fact]
    public void Signed_in_ordinary_member_reports_not_an_administrator()
    {
        var accessor = new CurrentUserAccessor(
            new HttpContextAccessor { HttpContext = ContextFor(Guid.NewGuid(), "bob", isAdministrator: false) });

        Assert.False(accessor.GetCurrent()!.IsAdministrator);
    }

    private static DefaultHttpContext ContextFor(Guid id, string username, bool isAdministrator)
    {
        // The exact claim shape Login.razor mints — the administrator claim is present only when
        // true, never present-with-value-"false".
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Name, username),
        ];

        if (isAdministrator)
        {
            claims.Add(new Claim(ZeroWikiClaims.IsAdministrator, ZeroWikiClaims.AdministratorClaimValue));
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "TestScheme");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }
}
