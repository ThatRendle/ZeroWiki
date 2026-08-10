using Microsoft.Extensions.Options;

namespace ZeroWiki.Content;

public static class ContentStorageStartupExtensions
{
    /// <summary>
    /// Binds <see cref="ContentStorageOptions"/> from the <c>ContentStorage</c> configuration
    /// section — populated from <c>appsettings.*.json</c> and, in the container, the
    /// <c>ContentStorage__DataRoot</c> environment variable — and registers the derived
    /// <see cref="ContentPaths"/> as a singleton, computed once rather than per caller.
    /// </summary>
    public static IServiceCollection AddContentStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ContentStorageOptions>()
            .Bind(configuration.GetSection(ContentStorageOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.DataRoot),
                "ContentStorage:DataRoot must not be empty.")
            .Validate(
                options => options.WriteLockTimeout > TimeSpan.Zero,
                "ContentStorage:WriteLockTimeout must be greater than zero.")
            .Validate(
                options => options.SaveWriteLockTimeout > TimeSpan.Zero,
                "ContentStorage:SaveWriteLockTimeout must be greater than zero.")
            .ValidateOnStart();

        services
            .AddOptions<ContentAuthorshipOptions>()
            .Bind(configuration.GetSection(ContentAuthorshipOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.HostDomain),
                "ContentAuthorship:HostDomain must not be empty.")
            .ValidateOnStart();

        services.AddSingleton(sp =>
            new ContentPaths(sp.GetRequiredService<IOptions<ContentStorageOptions>>().Value.DataRoot));

        services.AddSingleton<GitProcessRunner>();
        services.AddSingleton<GitHookInstaller>();
        services.AddSingleton<ContentRepositoryService>();

        // §7: the Smart HTTP git remote's CGI subprocess host (D18 §3) -- stateless beyond
        // ContentPaths, already a singleton, so this is one too.
        services.AddSingleton<GitHttpBackendHost>();

        // §3: page enumeration/routing and frontmatter parsing (D12, D14). The Markdown pipeline is
        // built once here — raw HTML already disabled per D13 — and shared with body rendering in 3b.
        services.AddSingleton(MarkdownPipelineFactory.Create());
        services.AddSingleton<IFrontmatterParser, SharpYamlFrontmatterParser>();
        services.AddSingleton<PageFrontmatterExtractor>();
        services.AddSingleton<PageEnumerationService>();

        // 3b: git-derived authorship/last-edit (D5). Stateless beyond ContentPaths/GitProcessRunner,
        // both already singletons, so this is one too — no caching (§4.1 owns the index for that).
        services.AddSingleton<PageHistoryService>();

        // §4.1-4.2: the derived index (D6, D15) — a builder that composes the services registered above
        // rather than duplicating any of them, and the process-memory singleton it fills at startup
        // (BuildPageIndexAsync, called after EnsureContentRepositoryAsync below).
        services.AddSingleton<PageIndexBuilder>();
        // PageIndex depends on the interface, not the concrete type, so a test can substitute a fake
        // (§6 block C2 remediation) — production resolves the same singleton either way.
        services.AddSingleton<IPageIndexBuilder>(sp => sp.GetRequiredService<PageIndexBuilder>());
        services.AddSingleton<PageIndex>();
        // Same pattern one layer up: PageSaveService depends on IPageIndex, not the concrete type, so a
        // test can substitute a fake Invalidate() that observes ordering (§6 block C2 remediation).
        services.AddSingleton<IPageIndex>(sp => sp.GetRequiredService<PageIndex>());

        // §6 block C1: builds the synthetic author identity a save is committed under (D10, D17).
        services.AddSingleton<AccountGitAuthorFactory>();

        // §6 block C2: the save path itself (D4, D9, D16, D17) — no UI caller yet, that is block D.
        services.AddSingleton<PageSaveService>();

        // §6 block D4 continuation: the Post/Redirect/Get draft carrier for a failed save. Depends on
        // the already-singleton ISecretTokenGenerator and TimeProvider rather than either being
        // re-resolved per request.
        services.AddSingleton<EditDraftStore>();

        return services;
    }

    /// <summary>
    /// Detects or initializes the content git repository (D1, D8) before the request pipeline is
    /// configured — an operator learning about a broken volume from a 500 on page one is the wrong
    /// failure mode. Resolves <see cref="ContentRepositoryService"/> — and, through it, the
    /// DI-registered <see cref="ContentPaths"/> singleton — rather than deriving paths a second way.
    /// </summary>
    public static async Task EnsureContentRepositoryAsync(this IHost app, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ContentRepositoryService>();
        await repository.EnsureRepositoryAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the initial <see cref="PageIndexSnapshot"/> and installs it into the <see cref="PageIndex"/>
    /// singleton (D15, §4.1-4.2). Must run after <see cref="EnsureContentRepositoryAsync"/> — building the
    /// index assumes the repository (and its <c>docs/</c> working tree) already exists.
    /// </summary>
    public static async Task BuildPageIndexAsync(this IHost app, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var builder = scope.ServiceProvider.GetRequiredService<PageIndexBuilder>();
        var index = scope.ServiceProvider.GetRequiredService<PageIndex>();
        var snapshot = await builder.BuildAsync(cancellationToken);
        index.InstallStartupSnapshot(snapshot);
    }

    /// <summary>
    /// Derives <see cref="ContentPaths"/> straight from <paramref name="configuration"/>, without a
    /// built <see cref="IServiceProvider"/>. Some startup wiring — the DataProtection key ring
    /// (<c>Program.cs</c>) — needs the data root before <c>WebApplicationBuilder.Build()</c> runs,
    /// so it cannot resolve the DI-registered <see cref="ContentPaths"/> singleton. This reads the
    /// same <c>ContentStorage</c> section through the same <see cref="ContentStorageOptions"/> type
    /// as <see cref="AddContentStorage"/>, so there is one definition of the data root, not two.
    /// </summary>
    public static ContentPaths ResolveContentPaths(IConfiguration configuration)
    {
        var options = configuration.GetSection(ContentStorageOptions.SectionName).Get<ContentStorageOptions>()
            ?? new ContentStorageOptions();

        return new ContentPaths(options.DataRoot);
    }
}
