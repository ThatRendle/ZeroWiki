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
            .ValidateOnStart();

        services.AddSingleton(sp =>
            new ContentPaths(sp.GetRequiredService<IOptions<ContentStorageOptions>>().Value.DataRoot));

        services.AddSingleton<GitProcessRunner>();
        services.AddSingleton<GitHookInstaller>();
        services.AddSingleton<ContentRepositoryService>();

        // §3: page enumeration/routing and frontmatter parsing (D12, D14). The Markdown pipeline is
        // built once here — raw HTML already disabled per D13 — and shared with body rendering in 3b.
        services.AddSingleton(MarkdownPipelineFactory.Create());
        services.AddSingleton<IFrontmatterParser, SharpYamlFrontmatterParser>();
        services.AddSingleton<PageFrontmatterExtractor>();
        services.AddSingleton<PageEnumerationService>();

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
