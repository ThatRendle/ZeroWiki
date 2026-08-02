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
/// The hooks directory is located via <c>git rev-parse --git-path hooks</c> rather than assumed to be
/// <c>&lt;repositoryRoot&gt;/.git/hooks</c> — correct whether <c>.git</c> is an ordinary directory or a
/// gitfile (worktree/submodule layout), and always outside <see cref="ContentPaths.WorkingTree"/>, so
/// installing a hook can never dirty the tree the working-tree-clean invariant governs.
/// </remarks>
public sealed class GitHookInstaller
{
    /// <summary>Filename of the hook that will take the repository's write lock (§5.3).</summary>
    public const string PreReceiveHookName = "pre-receive";

    /// <summary>Filename of the hook that will re-index and broadcast changes (§8.1-§8.2).</summary>
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
        "# No-op today. Filled in by section 5.3: acquire the repository's single write lock before\n" +
        "# any ref is updated, so a browser save and an incoming push can never race.\n" +
        "exit 0\n";

    private const string PostReceiveHookBody =
        "#!/bin/sh\n" +
        "# Managed by ZeroWiki. Rewritten unconditionally every time the application starts - a\n" +
        "# hand-edit to this file is discarded on the next start, not preserved. Treat this file as\n" +
        "# generated, not as operator configuration.\n" +
        "#\n" +
        "# No-op today. Filled in by sections 8.1-8.2: re-index the changed pages and broadcast the\n" +
        "# change to connected browsers.\n" +
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
