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
}
