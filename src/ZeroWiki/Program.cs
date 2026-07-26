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

// Establishes HttpContext.User for every request. Locking routes down is §6's job — nothing
// here denies anything yet.
app.UseAuthentication();

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
