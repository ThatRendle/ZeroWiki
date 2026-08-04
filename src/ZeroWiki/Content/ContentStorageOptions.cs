namespace ZeroWiki.Content;

/// <summary>
/// The mounted data volume root (D8): production is a single Docker volume at <c>/data</c>,
/// holding <c>identity.db</c> beside the <c>wiki</c> content git repository. Bound from the
/// <c>ContentStorage</c> configuration section, so it can be overridden by
/// <c>appsettings.*.json</c> or, as the container does, by the <c>ContentStorage__DataRoot</c>
/// environment variable.
/// </summary>
public sealed class ContentStorageOptions
{
    public const string SectionName = "ContentStorage";

    /// <summary>The data volume root. Defaults to <c>/data</c>, the container mount point.</summary>
    public string DataRoot { get; set; } = "/data";

    /// <summary>
    /// How long the app-side write path (a browser save's commit, §5.2) waits to acquire D16's single
    /// cross-process write lock before giving up. Defaults to 10 seconds — comfortably covers an
    /// ordinary commit-on-save plus contention from a concurrent push of realistic size, while still
    /// failing well inside typical browser/reverse-proxy request timeouts. Bound the same way as
    /// <see cref="DataRoot"/>. Applies only to a save's acquisition; a git push's wait for the same lock
    /// — held by the app's own wrapper around the whole <c>git http-backend</c> invocation (§7.5), not
    /// by a hook — is deliberately unbounded and is not configured here (design.md D16, Product Owner
    /// decision).
    /// </summary>
    public TimeSpan WriteLockTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
