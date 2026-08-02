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

        return services;
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
