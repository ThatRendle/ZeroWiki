using ZeroWiki.Content;
using ZeroWiki.Identity;

namespace ZeroWiki.Web;

/// <summary>
/// D18: the three Smart HTTP git routes — <c>/git/info/refs</c> (GET), <c>/git/git-upload-pack</c>
/// (POST), <c>/git/git-receive-pack</c> (POST) — mapped onto <see cref="GitHttpBackendHost"/>. No
/// repository-name segment (D18 §1): ZeroWiki serves exactly one repository, at a fixed
/// <c>GIT_PROJECT_ROOT</c>.
/// </summary>
/// <remarks>
/// <c>.AllowAnonymous()</c> on the whole group opts every route in it out of both
/// <see cref="AnonymousGate"/> and the authorization fallback policy (<c>Program.cs</c>) —
/// <see cref="GitBasicAuthenticationFilter"/>, attached to the same group, is what stands in their
/// place, and it is attached here rather than as separate middleware precisely so the exempted set and
/// the authenticated set are the same routing decision (see its own remarks).
/// </remarks>
public static class GitSmartHttpEndpoints
{
    public static IEndpointRouteBuilder MapGitSmartHttp(this IEndpointRouteBuilder endpoints)
    {
        var git = endpoints.MapGroup("/git")
            .AllowAnonymous()
            .AddEndpointFilter(GitBasicAuthenticationFilter.InvokeAsync);

        git.MapGet("/info/refs", (HttpContext httpContext, GitHttpBackendHost host) =>
            InvokeGitHttpBackendAsync(httpContext, host, "/info/refs"));

        git.MapPost("/git-upload-pack", (HttpContext httpContext, GitHttpBackendHost host) =>
            InvokeGitHttpBackendAsync(httpContext, host, "/git-upload-pack"));

        git.MapPost("/git-receive-pack", HandleReceivePackAsync);

        return endpoints;
    }

    /// <summary>
    /// D18 §5, D16: the only one of the three routes that writes. Acquires
    /// <see cref="RepositoryWriteLock"/> around the <b>entire</b> <see cref="GitHttpBackendHost.InvokeAsync"/>
    /// call — <c>info/refs</c> and <c>git-upload-pack</c> never acquire it at all (D18 §5: git's own
    /// content-addressed, write-then-rename object store and atomic ref updates already give an
    /// unlocked reader a consistent, complete snapshot, measured against both a committing and a
    /// repacking/pruning concurrent writer).
    /// </summary>
    /// <remarks>
    /// <b>Acquisition is unbounded (D16, spec: "Push's wait for the lock has no ceiling") via
    /// <see cref="RepositoryWriteLock.AcquireAsync"/>'s existing poll loop with an effectively-infinite
    /// timeout, not a new true-blocking <c>LOCK_EX</c> path.</b> D16 left this open as §7.5's own
    /// decision; the poll-loop route was chosen and is justified in the DEVLOG (§7 thread), in short:
    /// it reuses the same, already-tested acquisition path §5.2's save uses rather than adding new
    /// P/Invoke surface, and — unlike a genuine blocking <c>flock(2)</c> call, which D16's own remarks
    /// note "cannot be cancelled from .NET without leaking the native thread it runs on" — it composes
    /// with <see cref="HttpContext.RequestAborted"/>, so a client that vanishes while queued for the
    /// lock does not sit there on behalf of a connection nobody is waiting on anymore. This does not
    /// narrow the spec's "no ceiling" guarantee: a push whose client is still connected never observes
    /// <see cref="HttpContext.RequestAborted"/> firing, so its wait is genuinely unbounded, exactly as
    /// the scenario requires.
    /// </remarks>
    private static async Task HandleReceivePackAsync(HttpContext httpContext, GitHttpBackendHost host, ContentPaths paths)
    {
        RepositoryWriteLock writeLock;
        try
        {
            writeLock = await RepositoryWriteLock.AcquireAsync(
                paths.LockFilePath, UnboundedWait, httpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client vanished while queued for the lock -- nothing was written, and there is no
            // longer a connection to answer.
            return;
        }

        try
        {
            await InvokeGitHttpBackendAsync(httpContext, host, "/git-receive-pack");
        }
        finally
        {
            writeLock.Dispose();
        }
    }

    /// <summary>Not <see cref="Timeout.InfiniteTimeSpan"/> (a negative duration <see cref="RepositoryWriteLock.AcquireAsync"/>
    /// rejects) -- the largest legal <see cref="TimeSpan"/>, which no real wait can ever reach, so the
    /// poll loop's own timeout check never trips in practice.</summary>
    private static readonly TimeSpan UnboundedWait = TimeSpan.MaxValue;

    private static async Task InvokeGitHttpBackendAsync(HttpContext httpContext, GitHttpBackendHost host, string pathInfo)
    {
        // The endpoint filter is what guarantees this cast never fails: a request reaching this method
        // has already had GitBasicAuthenticationFilter.InvokeAsync call `next`, which is the only path
        // that ever populates this key.
        var account = httpContext.Items[GitBasicAuthenticationFilter.AuthenticatedAccountKey] as AuthenticatedAccount
            ?? throw new InvalidOperationException(
                "A git route handler ran without an authenticated account in HttpContext.Items -- " +
                "GitBasicAuthenticationFilter should have refused the request before this method was reached.");

        var request = httpContext.Request;
        var gitRequest = new GitHttpBackendRequest
        {
            PathInfo = pathInfo,
            RequestMethod = request.Method,
            QueryString = request.QueryString.HasValue ? request.QueryString.Value!.TrimStart('?') : string.Empty,
            ContentType = request.ContentType,
            ContentLength = request.ContentLength,
            ContentEncoding = request.Headers.ContentEncoding is { Count: > 0 } encoding ? encoding.ToString() : null,
            RemoteUser = account.Username,
            RequestBody = request.Body,
        };

        await host.InvokeAsync(gitRequest, httpContext.Response, httpContext.RequestAborted);
    }
}
