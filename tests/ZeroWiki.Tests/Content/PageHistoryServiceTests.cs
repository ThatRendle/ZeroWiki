using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageHistoryService"/> against a real git repository — the real <c>git</c>
/// binary, not a fake — so "authorship comes from git" (D5) is proven against the actual history git
/// records, not against a hand-built stand-in for it.
/// </summary>
public sealed class PageHistoryServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-page-history-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    private ContentPaths Paths => new(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private PageHistoryService CreateService() =>
        new(Paths, _git, NullLogger<PageHistoryService>.Instance);

    /// <summary>Bootstraps a non-bare repository with a <c>docs/</c> working tree, matching D8/§2.</summary>
    private async Task InitializeRepositoryAsync()
    {
        var repositoryRoot = Paths.RepositoryRoot;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));

        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.name", "placeholder"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.email", "placeholder@example.invalid"]);
    }

    /// <summary>Writes <paramref name="relativePath"/> under <c>docs/</c> and commits it as <paramref name="author"/>.</summary>
    private async Task CommitPageAsync(string relativePath, GitAuthor author, DateTimeOffset authoredAt, string message)
    {
        var repositoryRoot = Paths.RepositoryRoot;
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, message);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);

        var env = new Dictionary<string, string>(author.ToEnvironmentVariables())
        {
            ["GIT_AUTHOR_DATE"] = authoredAt.ToString("O"),
            ["GIT_COMMITTER_DATE"] = authoredAt.ToString("O"),
        };

        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", message], env);
    }

    [Fact]
    public async Task PageWithHistory_ReturnsTheMostRecentAuthorAndDate()
    {
        await InitializeRepositoryAsync();
        var firstEdit = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var secondEdit = new DateTimeOffset(2026, 2, 3, 14, 30, 0, TimeSpan.Zero);

        await CommitPageAsync("page.md", new GitAuthor("Alice", "alice@zerowiki.example"), firstEdit, "first");
        await CommitPageAsync("page.md", new GitAuthor("Bob", "bob@zerowiki.example"), secondEdit, "second");

        var lastEdit = await CreateService().GetLastEditAsync("page.md", CancellationToken.None);

        Assert.NotNull(lastEdit);
        Assert.Equal("Bob", lastEdit.AuthorName);
        Assert.Equal(secondEdit, lastEdit.EditedAt);
    }

    [Fact]
    public async Task PageInASubdirectory_IsFoundByItsWorkingTreeRelativePath()
    {
        await InitializeRepositoryAsync();
        var editedAt = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);
        var nested = Path.Combine("Project Notes", "Kick Off.md");

        await CommitPageAsync(nested, new GitAuthor("Alice", "alice@zerowiki.example"), editedAt, "nested");

        var lastEdit = await CreateService().GetLastEditAsync(nested, CancellationToken.None);

        Assert.NotNull(lastEdit);
        Assert.Equal("Alice", lastEdit.AuthorName);
    }

    [Fact]
    public async Task PageWithNoCommitHistory_ReturnsNullRatherThanThrowing()
    {
        await InitializeRepositoryAsync();
        Directory.CreateDirectory(Paths.WorkingTree);
        await File.WriteAllTextAsync(Path.Combine(Paths.WorkingTree, "untracked.md"), "content");

        var lastEdit = await CreateService().GetLastEditAsync("untracked.md", CancellationToken.None);

        Assert.Null(lastEdit);
    }

    [Fact]
    public async Task GitSubprocessFailure_ReturnsNullRatherThanThrowing()
    {
        // RepositoryRoot exists but was never `git init`ed, so `git log` itself fails ("not a git
        // repository") rather than merely returning no history — a different failure mode than
        // PageWithNoCommitHistory above, and this is the one GitProcessException actually covers.
        // Not reachable in production (EnsureContentRepositoryAsync guarantees a real repository
        // before anything else runs), but PageHistoryService must still degrade rather than sink the
        // whole page render over metadata that is not essential to it.
        Directory.CreateDirectory(Paths.RepositoryRoot);

        var lastEdit = await CreateService().GetLastEditAsync("page.md", CancellationToken.None);

        Assert.Null(lastEdit);
    }

    [Fact]
    public async Task OnlyTheMostRecentCommitIsConsulted_NotTheFullHistory()
    {
        await InitializeRepositoryAsync();
        var firstEdit = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var secondEdit = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await CommitPageAsync("page.md", new GitAuthor("Old Author", "old@zerowiki.example"), firstEdit, "first");
        await CommitPageAsync("page.md", new GitAuthor("New Author", "new@zerowiki.example"), secondEdit, "second");
        await CommitPageAsync("other.md", new GitAuthor("Irrelevant", "irrelevant@zerowiki.example"), secondEdit, "unrelated");

        var lastEdit = await CreateService().GetLastEditAsync("page.md", CancellationToken.None);

        Assert.NotNull(lastEdit);
        Assert.Equal("New Author", lastEdit.AuthorName);
        Assert.NotEqual("Old Author", lastEdit.AuthorName);
    }
}
