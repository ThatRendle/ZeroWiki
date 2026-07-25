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
