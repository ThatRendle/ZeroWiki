namespace ZeroWiki.Identity;

public static class BootstrapStartupExtensions
{
    /// <summary>
    /// Reports at startup whether the store is empty, so a first-run operator is told where to
    /// go instead of having to guess.
    /// </summary>
    /// <remarks>
    /// Signalling only. The answer is logged and discarded — nothing caches it, and no
    /// authorization decision reads it. The gate that actually protects the bootstrap path is
    /// re-evaluated per request in <see cref="BootstrapService.IsAvailableAsync"/>; a cached
    /// startup answer would keep a privileged path open for the life of the process even after
    /// an account exists.
    /// </remarks>
    public static async Task LogBootstrapStateAsync(this IHost app, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<BootstrapService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(BootstrapStartupExtensions).FullName!);

        if (await bootstrap.IsAvailableAsync(cancellationToken))
        {
            logger.LogWarning(
                "The identity store has no accounts. Visit /bootstrap to create the first administrator account.");
        }
        else
        {
            logger.LogInformation(
                "The identity store already has at least one account; the first-administrator bootstrap path is inert.");
        }
    }
}
