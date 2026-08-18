using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Task 10.3, <c>content-store</c>'s <i>Index rebuilt from repository</i> scenario: when the index is
/// absent at startup, the <em>application's own start</em> rebuilds it in full from the repository —
/// the working tree <b>and</b> history — and stamps it with the commit it was built from (D6, D14,
/// D15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a restart is "the index is deleted or absent".</b> The index is never persisted (D15): the
/// <see cref="PageIndex"/> singleton is the only place a snapshot exists between rebuilds, so a fresh
/// process <em>is</em> the absent-index case, exactly and without simulation.
/// <see cref="ZeroWikiAppFactory.RestartedFrom"/> gives a new DI container — hence a new, empty
/// <see cref="PageIndex"/> — over the same data root, so the repository it must rebuild from is one
/// this test built with the app's own write path rather than hand-rolled <c>git commit</c>s.
/// </para>
/// <para>
/// <b>Why this asserts on <see cref="PageIndex.Current"/> and not on a rendered page.</b>
/// <see cref="PageIndex.GetCurrentAsync"/> self-heals a missing startup snapshot — an empty snapshot's
/// stamp never matches live <c>HEAD</c>, so the first read rebuilds in full. That is correct, required
/// behaviour (D15's freshness obligation), but it means <em>any</em> assertion made through a rendered
/// page passes whether or not startup built anything, and is therefore blind to the property this
/// scenario is about. <see cref="PageIndex.Current"/> is the plain, un-refreshed read of what was
/// installed, and is read here before this instance has served a single request.
/// </para>
/// <para>
/// <b>Why two accounts and two commits.</b> Last-edit metadata is the half of this scenario that can
/// only come from history, and a rebuild that ignored history entirely — or that attributed every page
/// to <c>HEAD</c>'s own author, the plausible wrong answer — would still produce a complete-looking
/// page list. Attributing the <em>first</em> page to the account that saved it, while <c>HEAD</c>
/// belongs to the other, is what separates those cases. The frontmatter title covers the working-tree
/// half in the same way: it exists only in the file's bytes on disk, never in git's commit metadata.
/// </para>
/// </remarks>
public sealed class PageIndexStartupRebuildTests
{
    private const string AlphaContent = "---\ntitle: Alpha's frontmatter title\n---\n\n# Alpha\n";
    private const string BetaContent = "---\ntitle: Beta's frontmatter title\n---\n\n# Beta\n";

    private readonly GitProcessRunner _git = new();

    [Fact]
    public async Task Starting_over_a_repository_with_no_index_rebuilds_it_from_the_working_tree_and_history()
    {
        using var app = new ZeroWikiAppFactory();
        var saveService = app.Services.GetRequiredService<PageSaveService>();

        await SaveOrThrowAsync(saveService, "alpha", AlphaContent, "alice");
        await SaveOrThrowAsync(saveService, "beta", BetaContent, "bob");

        var repositoryRoot = Path.Combine(app.DataRoot, "wiki");
        var head = await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "HEAD"]);

        using var restarted = ZeroWikiAppFactory.RestartedFrom(app);

        // Resolving the singleton is what starts the host, hence what runs Program.cs's
        // BuildPageIndexAsync -- nothing has served a request against this instance yet.
        var index = restarted.Services.GetRequiredService<PageIndex>();
        var snapshot = index.Current;

        Assert.Equal(head.StandardOutput.Trim(), snapshot.CommitSha);

        var pages = snapshot.Pages.OrderBy(page => page.RelativePath, StringComparer.Ordinal).ToList();
        Assert.Equal(["alpha.md", "beta.md"], pages.Select(page => page.RelativePath));

        // Working-tree half: frontmatter exists only in the file's bytes.
        Assert.Equal("Alpha's frontmatter title", pages[0].Title);
        Assert.Equal("Beta's frontmatter title", pages[1].Title);

        // History half: each page attributed to the commit that last touched *it*, not to HEAD.
        Assert.Equal("alice", pages[0].LastEdit?.AuthorName);
        Assert.Equal("bob", pages[1].LastEdit?.AuthorName);
    }

    private static async Task SaveOrThrowAsync(
        PageSaveService saveService,
        string route,
        string content,
        string username)
    {
        var author = new AuthenticatedAccount(Guid.NewGuid(), username, IsAdministrator: false);
        var result = await saveService.SaveAsync(
            new RouteValue(route),
            content,
            PageBaseRevision.AbsentAtHead,
            author,
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
    }
}
