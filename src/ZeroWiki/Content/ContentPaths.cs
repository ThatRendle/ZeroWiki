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
        LockFilePath = Path.Combine(DataRoot, "wiki.lock");
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

    /// <summary>
    /// D16's single cross-process write-lock file — <c>&lt;DataRoot&gt;/wiki.lock</c>, following
    /// <see cref="KeysDirectory"/>'s exact precedent: a fixed, <see cref="DataRoot"/>-relative sibling
    /// of <see cref="RepositoryRoot"/>, computed once, not derived from git. Deliberately outside
    /// <see cref="RepositoryRoot"/> entirely, not merely outside <see cref="WorkingTree"/> — reconciling
    /// the working tree runs <c>git add -A</c>/<c>git status --porcelain</c> unscoped at
    /// <see cref="RepositoryRoot"/>, so anything beneath it (including a file placed directly alongside
    /// <c>docs/</c>) would be staged, committed, and pushed to every Obsidian vault, and would keep the
    /// tree dirty between acquisitions. Nothing in git dictates where a lockfile of our own invention
    /// lives, unlike the hooks directory (see <see cref="GitHookInstaller"/>'s remarks), so — unlike
    /// that path — this one does not need to be resolved dynamically via a git subprocess call. Both
    /// sides that acquire this lock — the app's own commit path (§5.2) and the wrapper around
    /// <c>git http-backend</c> for pushes (§7.5) — do so in C#, so this path is never baked into a
    /// generated hook's <c>#!/bin/sh</c> text; the generated hooks must never acquire this lock at all
    /// (see <see cref="GitHookInstaller"/>'s remarks for why).
    /// </summary>
    public string LockFilePath { get; }
}
