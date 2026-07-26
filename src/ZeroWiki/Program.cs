using Microsoft.AspNetCore.Authentication.Cookies;
using ZeroWiki.Components;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents();
builder.Services.AddIdentityDb(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddSingleton<ISecretTokenGenerator, SecretTokenGenerator>();
builder.Services.AddScoped<GitTokenService>();
builder.Services.AddScoped<BootstrapService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<InvitationService>();

// Cookie authentication only — deliberately not ASP.NET Core Identity, whose deferred surface
// (email confirmation, 2FA, external logins, role UI) is dead weight for an invite-only wiki.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ZeroWiki.Authentication";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // Always outside development. Development serves plain HTTP, where Always would mean the
        // browser silently never returns the cookie — a symptom indistinguishable from a wrong
        // password, and one that would cost somebody an afternoon.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ReturnUrlParameter = "returnUrl";
    });

// Required, not decorative: without it every request to a page carrying [Authorize] fails with
// "Unable to find the required services. Please add all the required services by calling
// 'IServiceCollection.AddAuthorization'". (Removing this *and* the UseAuthorization() call below
// gives the different, more familiar "Endpoint ... contains authorization metadata, but a
// middleware was not found that supports authorization" — a distinct experiment, quoted here only
// so the two are not confused.) Locking the site down by default is §6's job, not this.
builder.Services.AddAuthorization();

// Supplies the cascading AuthenticationState an AuthorizeView or AuthorizeRouteView reads. Nothing
// renders one yet; §6 and §7 will.
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

await app.MigrateIdentityDbAsync();
await app.LogBootstrapStateAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Establishes HttpContext.User, then enforces the [Authorize] attributes individual pages carry.
//
// UseAuthorization() is load-bearing here and its position is the whole point — do not delete it as
// redundant with AddAuthorization(). WebApplication auto-inserts the authorization middleware at the
// *front* of the pipeline, ahead of this UseAuthentication() call, where it evaluates [Authorize]
// against a User that has not been authenticated yet: every signed-in member is then bounced to
// /login and no authenticated request can ever reach a guarded page. Naming it here, after
// authentication, is what puts it in the right place.
//
// Measured by removing this one line: nine of the eleven invitations page tests fail, all of them
// authenticated requests getting 302 instead of 200 — while *both* anonymous tests stay green. The
// failure hides behind exactly the tests you would expect to catch it.
//
// Denying anonymous access site-wide is §6's job.
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.Run();

/// <summary>
/// Named so the integration tests can boot this application through
/// <c>WebApplicationFactory&lt;Program&gt;</c>. Top-level statements otherwise generate an
/// internal entry-point class the test project cannot name.
/// </summary>
public partial class Program;
