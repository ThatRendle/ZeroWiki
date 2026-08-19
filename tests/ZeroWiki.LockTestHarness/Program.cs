using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZeroWiki.Content;

namespace ZeroWiki.LockTestHarness;

/// <summary>
/// A genuine second OS process running production code that needs process-wide state a single-process
/// xUnit test run cannot safely mutate — not a fake or an in-process <see cref="Task"/>. Explicitly
/// namespaced (not a top-level-statement <c>Program</c>) because <c>ZeroWiki.Tests</c> also references
/// <c>ZeroWiki</c>'s own top-level-statement <c>Program</c> (<c>WebApplicationFactory&lt;Program&gt;</c>)
/// — two implicit global-namespace <c>Program</c> types from two referenced assemblies collide.
/// </summary>
/// <remarks>
/// Two independent usages, dispatched on <c>args[0]</c>:
/// <list type="bullet">
/// <item>
/// <c>ZeroWiki.LockTestHarness &lt;lockFilePath&gt; &lt;acquireTimeoutMs&gt; &lt;holdMs&gt;</c> (D16) —
/// holds <see cref="RepositoryWriteLock"/>'s real lock across process boundaries. Prints
/// <c>ACQUIRED</c> (and flushes) the instant the lock is held, so a caller can synchronize on it rather
/// than guessing with a fixed delay; holds it for <c>holdMs</c>, then releases and prints
/// <c>RELEASED</c>. Prints <c>TIMEOUT</c> and exits 1 if the lock could not be acquired within
/// <c>acquireTimeoutMs</c>.
/// </item>
/// <item>
/// <c>ZeroWiki.LockTestHarness <see cref="ReconcileVerb"/> &lt;dataRoot&gt; &lt;gitConfigGlobalPath&gt;</c>
/// (D17, §6 block D4 continuation round three) — runs
/// <see cref="ContentRepositoryService.EnsureRepositoryAsync"/> against <c>dataRoot</c> with
/// <c>GIT_CONFIG_GLOBAL</c> set to <c>gitConfigGlobalPath</c> as <em>this process's own</em>
/// environment. This is the only safe way to exercise a hostile <em>inherited</em> git configuration:
/// setting that variable in the xUnit test host process itself would leak into every other
/// concurrently-running test class's own git subprocesses under xUnit's default parallelism (see
/// <c>ContentRepositoryServiceTests</c>'s own established rule against exactly that, on its
/// <c>Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch</c> test). Prints <c>RECONCILED</c>
/// and exits 0 on success; prints <c>REFUSED: &lt;message&gt;</c> (newlines flattened to spaces) and
/// exits 1 if <see cref="ContentRepositoryService.EnsureRepositoryAsync"/> throws
/// <see cref="InvalidOperationException"/> — its own fatal-refusal shape.
/// </item>
/// <item>
/// <c>ZeroWiki.LockTestHarness <see cref="EnvironmentIsolationVerb"/> &lt;dataRoot&gt;
/// &lt;traceMarkerPath&gt;</c> (§7 remediation, supervisor blocker 1) — sets <c>GIT_TRACE</c> to
/// <c>traceMarkerPath</c> as <em>this process's own</em> environment, then runs one
/// <see cref="GitHttpBackendHost.InvokeAsync"/> call. The same isolation reason as
/// <see cref="ReconcileVerb"/> above applies identically: <c>GIT_TRACE</c> is git's own opt-in trace
/// switch, and setting it in the shared xUnit test host process would risk an unrelated,
/// concurrently-running test's own git subprocess also inheriting it and writing to the same marker
/// file, a false positive this harness process cannot produce for anyone but itself. Prints
/// <c>LEAKED</c> if the subprocess wrote the marker file (proof the parent's ambient environment
/// reached it) or <c>NOT_LEAKED</c> if it did not, followed by the response status code on the same
/// line; exits 0 either way — the caller reads which word came back.
/// </item>
/// </list>
/// </remarks>
internal static class Program
{
    private const string ReconcileVerb = "reconcile-under-hostile-git-config";
    private const string EnvironmentIsolationVerb = "check-git-http-backend-environment-isolation";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == ReconcileVerb)
        {
            return await RunReconcileUnderHostileGitConfigAsync(args);
        }

        if (args.Length >= 1 && args[0] == EnvironmentIsolationVerb)
        {
            return await RunEnvironmentIsolationCheckAsync(args);
        }

        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync("usage: ZeroWiki.LockTestHarness <lockFilePath> <acquireTimeoutMs> <holdMs>");
            return 2;
        }

        var lockFilePath = args[0];
        var acquireTimeout = TimeSpan.FromMilliseconds(double.Parse(args[1]));
        var holdMs = int.Parse(args[2]);

        try
        {
            using var writeLock = await RepositoryWriteLock.AcquireAsync(lockFilePath, acquireTimeout);
            Console.WriteLine("ACQUIRED");
            Console.Out.Flush();

            await Task.Delay(holdMs);

            Console.WriteLine("RELEASED");
            Console.Out.Flush();
            return 0;
        }
        catch (RepositoryLockTimeoutException)
        {
            Console.WriteLine("TIMEOUT");
            Console.Out.Flush();
            return 1;
        }
    }

    private static async Task<int> RunReconcileUnderHostileGitConfigAsync(string[] args)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync(
                $"usage: ZeroWiki.LockTestHarness {ReconcileVerb} <dataRoot> <gitConfigGlobalPath>");
            return 2;
        }

        var dataRoot = args[1];
        var gitConfigGlobalPath = args[2];

        // Scoped to this process only: every subprocess GitProcessRunner spawns from here on inherits
        // this process's own environment, and this process exists for no other reason than to run
        // exactly one EnsureRepositoryAsync call.
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfigGlobalPath);

        var paths = new ContentPaths(dataRoot);
        var git = new GitProcessRunner();
        var service = new ContentRepositoryService(
            paths,
            git,
            new GitHookInstaller(git),
            NullLogger<ContentRepositoryService>.Instance,
            Options.Create(new ContentStorageOptions { DataRoot = dataRoot }));

        try
        {
            await service.EnsureRepositoryAsync();
            Console.WriteLine("RECONCILED");
            Console.Out.Flush();
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("REFUSED: " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
            Console.Out.Flush();
            return 1;
        }
    }

    private static async Task<int> RunEnvironmentIsolationCheckAsync(string[] args)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync(
                $"usage: ZeroWiki.LockTestHarness {EnvironmentIsolationVerb} <dataRoot> <traceMarkerPath>");
            return 2;
        }

        var dataRoot = args[1];
        var traceMarkerPath = args[2];

        var paths = new ContentPaths(dataRoot);
        var git = new GitProcessRunner();
        var repository = new ContentRepositoryService(
            paths,
            git,
            new GitHookInstaller(git),
            NullLogger<ContentRepositoryService>.Instance,
            Options.Create(new ContentStorageOptions { DataRoot = dataRoot }));
        await repository.EnsureRepositoryAsync();

        var pagePath = Path.Combine(paths.WorkingTree, "page.md");
        await File.WriteAllTextAsync(pagePath, "hello");
        await git.RunOrThrowAsync(paths.RepositoryRoot, ["add", "docs/page.md"]);
        await git.RunOrThrowAsync(
            paths.RepositoryRoot,
            ["commit", "-m", "seed"],
            new GitAuthor("Seed", "seed@zerowiki.example").ToEnvironmentVariables());

        // Set only now, immediately before the one call under test -- GitProcessRunner (used by
        // EnsureRepositoryAsync and the seed commit above) deliberately inherits this process's ambient
        // environment for its own git calls, so setting GIT_TRACE any earlier would have those calls
        // write to the same marker file too, corrupting the one signal this harness exists to isolate.
        Environment.SetEnvironmentVariable("GIT_TRACE", traceMarkerPath);

        var host = new GitHttpBackendHost(paths, NullLogger<GitHttpBackendHost>.Instance);
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var request = new GitHttpBackendRequest
        {
            PathInfo = "/info/refs",
            RequestMethod = "GET",
            QueryString = "service=git-upload-pack",
            RemoteUser = "alice",
            RequestBody = Stream.Null,
        };

        await host.InvokeAsync(request, httpContext.Response, CancellationToken.None);

        Console.WriteLine(File.Exists(traceMarkerPath) ? "LEAKED" : "NOT_LEAKED");
        Console.WriteLine(httpContext.Response.StatusCode);
        Console.Out.Flush();
        return 0;
    }
}
