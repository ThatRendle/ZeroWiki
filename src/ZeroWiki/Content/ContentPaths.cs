namespace ZeroWiki.Content;

/// <summary>
/// Absolute content paths derived once from <see cref="ContentStorageOptions.DataRoot"/> (D8):
/// the repository root — <c>&lt;DataRoot&gt;/wiki</c>, where <c>.git</c> lives — and its
/// <c>docs</c> working tree, the directory the app actually renders.
/// </summary>
/// <remarks>
/// Registered as a singleton (see <see cref="ContentStorageStartupExtensions"/>) so every caller
/// shares one computation of these paths rather than re-deriving them per request.
/// </remarks>
public sealed class ContentPaths
{
    public ContentPaths(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root must not be empty.", nameof(dataRoot));
        }

        DataRoot = Path.GetFullPath(dataRoot);
        RepositoryRoot = Path.Combine(DataRoot, "wiki");
        WorkingTree = Path.Combine(RepositoryRoot, "docs");
        KeysDirectory = Path.Combine(DataRoot, "keys");
    }

    /// <summary>The mounted data volume root, e.g. <c>/data</c>.</summary>
    public string DataRoot { get; }

    /// <summary>The content git repository root — <c>&lt;DataRoot&gt;/wiki</c>; <c>.git</c> lives here.</summary>
    public string RepositoryRoot { get; }

    /// <summary>The working tree the app renders — <c>&lt;RepositoryRoot&gt;/docs</c>.</summary>
    public string WorkingTree { get; }

    /// <summary>
    /// Where the DataProtection key ring is persisted — <c>&lt;DataRoot&gt;/keys</c>, a sibling of
    /// <c>identity.db</c>. Deliberately outside <see cref="RepositoryRoot"/>: a key file written
    /// under the content repo would be picked up by commit-on-save and pushed to every Obsidian
    /// vault that clones the remote.
    /// </summary>
    public string KeysDirectory { get; }
}
