namespace ZeroWiki.Content;

/// <summary>
/// Installs the two Smart HTTP git hooks (<c>pre-receive</c>, <c>post-receive</c>) into the content
/// repository's own hooks directory on every application start (Product Owner decision, §2). Both
/// hooks ship with no-op bodies today and are rewritten unconditionally on every call — an upgraded
/// image ships an updated hook body rather than inheriting whatever the volume happens to hold, and a
/// hand-edit is discarded on the next start rather than silently kept. Each hook's own header comment
/// says so, so an operator who edits one directly learns it from the file rather than from a silently
/// reverted change.
/// </summary>
/// <remarks>
/// <para>
/// The hooks directory is located via <c>git rev-parse --git-path hooks</c> rather than assumed to be
/// <c>&lt;repositoryRoot&gt;/.git/hooks</c> — correct whether <c>.git</c> is an ordinary directory or a
/// gitfile (worktree/submodule layout), and always outside <see cref="ContentPaths.WorkingTree"/>, so
/// installing a hook can never dirty the tree the working-tree-clean invariant governs. This form also
/// honours <c>core.hooksPath</c> when a repository sets one — verified by execution — which matters
/// because a hook installed at the wrong path is one git silently never runs at all.
/// </para>
/// <para>
/// <b>Neither generated hook may acquire the repository write lock.</b> The lock (D16) is instead held
/// by the app itself around the whole <c>git http-backend</c> invocation (§7.5) — the process both hooks
/// run as children of. A hook that calls <c>flock</c> on the same lockfile would therefore block on its
/// own parent, which cannot release the lock while it is itself waiting on the hook to exit:
/// <c>flock(2)</c> is per-open-file-description, so a separate process gets no re-entrancy, and the
/// wait is unbounded by design (D16) — the result is a push that never returns rather than a slow one.
/// See the hook bodies' own comments and design.md D3/D16 for the full reasoning.
/// </para>
/// </remarks>
public sealed class GitHookInstaller
{
    /// <summary>
    /// Filename of the pre-receive hook. No-op, with no task currently planned to fill it in — do not
    /// use it to acquire the repository write lock (see <see cref="PreReceiveHookBody"/> and this
    /// type's remarks); the app already holds it around the whole <c>http-backend</c> invocation this
    /// hook runs inside of (§7.5, D16).
    /// </summary>
    public const string PreReceiveHookName = "pre-receive";

    /// <summary>
    /// Filename of the hook that will re-index and broadcast changes (§8.1-§8.2). Do not use it to
    /// acquire the repository write lock either (see <see cref="PostReceiveHookBody"/> and this type's
    /// remarks) — same reason as <see cref="PreReceiveHookName"/>.
    /// </summary>
    public const string PostReceiveHookName = "post-receive";

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const string PreReceiveHookBody =
        "#!/bin/sh\n" +
        "# Managed by ZeroWiki. Rewritten unconditionally every time the application starts - a\n" +
        "# hand-edit to this file is discarded on the next start, not preserved. Treat this file as\n" +
        "# generated, not as operator configuration.\n" +
        "#\n" +
        "# No-op today, with no task currently planned to fill it in.\n" +
        "#\n" +
        "# WARNING, if you are the one adding to this hook: do NOT acquire the repository write\n" +
        "# lock here. The app already holds it for the whole 'git http-backend' invocation this\n" +
        "# hook runs as a child of (design.md D16, task 7.5) - taking the same lock here would\n" +
        "# block this process against its own parent, and flock(2) gives a separate process no\n" +
        "# re-entrancy. The wait is unbounded by design, so the result is not a slow push but one\n" +
        "# that never returns, with an operator's only recourse being to restart the app.\n" +
        "exit 0\n";

    private const string PostReceiveHookBody =
        "#!/bin/sh\n" +
        "# Managed by ZeroWiki. Rewritten unconditionally every time the application starts - a\n" +
        "# hand-edit to this file is discarded on the next start, not preserved. Treat this file as\n" +
        "# generated, not as operator configuration.\n" +
        "#\n" +
        "# No-op today. Filled in by sections 8.1-8.2: re-index the changed pages and broadcast the\n" +
        "# change to connected browsers.\n" +
        "#\n" +
        "# WARNING, if you are the one filling this in: do NOT acquire the repository write lock\n" +
        "# here. The app already holds it for the whole 'git http-backend' invocation this hook\n" +
        "# runs as a child of (design.md D16, task 7.5) - taking the same lock here would block\n" +
        "# this process against its own parent, and flock(2) gives a separate process no\n" +
        "# re-entrancy. The wait is unbounded by design, so the result is not a slow push but one\n" +
        "# that never returns, with an operator's only recourse being to restart the app. The\n" +
        "# re-index/broadcast work below needs none of this: it can safely assume the push has\n" +
        "# already finished landing under the app's own lock by the time this hook runs.\n" +
        "exit 0\n";

    private readonly GitProcessRunner _git;

    public GitHookInstaller(GitProcessRunner git)
    {
        _git = git;
    }

    /// <summary>
    /// Writes both hooks into <paramref name="repositoryRoot"/>'s hooks directory, overwriting
    /// whatever is already there, and marks them executable.
    /// </summary>
    public async Task InstallHooksAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var hooksDirectory = await ResolveHooksDirectoryAsync(repositoryRoot, cancellationToken);
        Directory.CreateDirectory(hooksDirectory);

        await WriteHookAsync(hooksDirectory, PreReceiveHookName, PreReceiveHookBody, cancellationToken);
        await WriteHookAsync(hooksDirectory, PostReceiveHookName, PostReceiveHookBody, cancellationToken);
    }

    private async Task<string> ResolveHooksDirectoryAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await _git.RunOrThrowAsync(
            repositoryRoot,
            ["rev-parse", "--git-path", "hooks"],
            cancellationToken: cancellationToken);

        var hooksPath = result.StandardOutput.Trim();
        return Path.IsPathRooted(hooksPath) ? hooksPath : Path.Combine(repositoryRoot, hooksPath);
    }

    private static async Task WriteHookAsync(string hooksDirectory, string name, string body, CancellationToken cancellationToken)
    {
        var path = Path.Combine(hooksDirectory, name);
        await File.WriteAllTextAsync(path, body, cancellationToken);

        // A git hook's executable bit is a POSIX filesystem concept with no Windows equivalent, and
        // ZeroWiki ships only as a Linux container (CLAUDE.md) — this guard is a genuine platform
        // check the analyzer recognises, not a suppression of a real cross-platform concern.
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Installing git hooks requires a POSIX filesystem for executable-bit support.");
        }

        File.SetUnixFileMode(path, ExecutableMode);
    }
}
