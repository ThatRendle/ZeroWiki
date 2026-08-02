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
}
