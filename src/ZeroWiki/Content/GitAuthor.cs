namespace ZeroWiki.Content;

/// <summary>
/// A git commit author/committer identity, passed explicitly on every commit
/// (<c>GIT_AUTHOR_*</c>/<c>GIT_COMMITTER_*</c>) rather than left to ambient git configuration: the
/// container has none, so a commit would simply fail, and a developer's laptop has its own, so the
/// same commit would silently be attributed to the developer instead.
/// </summary>
public sealed record GitAuthor(string Name, string Email)
{
    /// <summary>
    /// The software's own identity — used for commits that belong to ZeroWiki rather than to a
    /// member: the repository's initial commit (D9, extended) and, later, startup reconciliation of
    /// orphaned changes.
    /// </summary>
    public static readonly GitAuthor System = new("System", "system@zerowiki.org");

    /// <summary>The environment variables that make this identity the author and committer of a commit.</summary>
    public IReadOnlyDictionary<string, string> ToEnvironmentVariables() => new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = Name,
        ["GIT_AUTHOR_EMAIL"] = Email,
        ["GIT_COMMITTER_NAME"] = Name,
        ["GIT_COMMITTER_EMAIL"] = Email,
    };
}
