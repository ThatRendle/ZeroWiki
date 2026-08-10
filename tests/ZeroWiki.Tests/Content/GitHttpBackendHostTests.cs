using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="GitHttpBackendHost"/> (D18 §2/§3) against a real, bootstrapped content
/// repository and a real <c>git http-backend</c> subprocess — the CGI response-translation rule and the
/// byte-stream guarantees, never through the full HTTP pipeline (that is §7 block C's job: a real git
/// client against a really-listening Kestrel, tasks 7.3/7.4). Mutation testing does not apply to this
/// file (block B brief): it covers general CGI-plumbing correctness, not an auth or lock-acquisition
/// path.
/// </summary>
public sealed class GitHttpBackendHostTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-git-http-backend-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    private ContentPaths Paths => new(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InfoRefs_ForUploadPack_ReturnsTheServiceAdvertisementAsRawBytes()
    {
        var paths = await InitializeRepositoryWithOneCommitAsync();
        var host = new GitHttpBackendHost(paths, NullLogger<GitHttpBackendHost>.Instance);
        var httpContext = NewHttpContext();

        var request = new GitHttpBackendRequest
        {
            PathInfo = "/info/refs",
            RequestMethod = "GET",
            QueryString = "service=git-upload-pack",
            RemoteUser = "alice",
            RequestBody = Stream.Null,
        };

        await host.InvokeAsync(request, httpContext.Response, CancellationToken.None);

        Assert.Equal(200, httpContext.Response.StatusCode);
        // D18 §2: a successful call carries no Status: line at all -- that must never surface as a
        // literal forwarded response header.
        Assert.False(httpContext.Response.Headers.ContainsKey("Status"));
        Assert.Equal("application/x-git-upload-pack-advertisement", httpContext.Response.Headers.ContentType);

        var body = ReadResponseBody(httpContext);
        var expectedPrefix = "001e# service=git-upload-pack\n"u8.ToArray();
        Assert.Equal(expectedPrefix, body.Take(expectedPrefix.Length));
    }

    [Fact]
    public async Task ARefusal_TranslatesTheStatusLineOntoTheResponseInsteadOfDefaultingTo200()
    {
        // D18 §2's translation rule, the negative case: git-http-backend answers a genuine refusal (an
        // unresolvable PATH_INFO -- verified directly against the real binary before writing this test)
        // with a literal "Status: 404 Not Found" line, which this host must carry onto the response as
        // StatusCode 404, not the default 200 an absent Status: line means.
        var paths = await InitializeRepositoryWithOneCommitAsync();
        var host = new GitHttpBackendHost(paths, NullLogger<GitHttpBackendHost>.Instance);
        var httpContext = NewHttpContext();

        var request = new GitHttpBackendRequest
        {
            PathInfo = "/does-not-exist",
            RequestMethod = "GET",
            QueryString = string.Empty,
            RemoteUser = "alice",
            RequestBody = Stream.Null,
        };

        await host.InvokeAsync(request, httpContext.Response, CancellationToken.None);

        Assert.Equal(404, httpContext.Response.StatusCode);
        Assert.False(httpContext.Response.Headers.ContainsKey("Status"));
    }

    [Fact]
    public async Task InvokeAsync_ABodyThatNeverCompletesTheGitProtocol_StillCompletesPromptly()
    {
        // Regression guard for D18 §2/§3(d): stdin is closed unconditionally once the request-body copy
        // finishes, which is what turns an incomplete/invalid upload into a clean git-level failure
        // instead of git-upload-pack blocking forever waiting for more input that will never arrive.
        var paths = await InitializeRepositoryWithOneCommitAsync();
        var host = new GitHttpBackendHost(paths, NullLogger<GitHttpBackendHost>.Instance);
        var httpContext = NewHttpContext();

        var garbage = "not a valid pkt-line stream"u8.ToArray();
        var request = new GitHttpBackendRequest
        {
            PathInfo = "/git-upload-pack",
            RequestMethod = "POST",
            QueryString = string.Empty,
            ContentType = "application/x-git-upload-pack-request",
            RemoteUser = "alice",
            RequestBody = new MemoryStream(garbage),
        };

        var invokeTask = host.InvokeAsync(request, httpContext.Response, CancellationToken.None);
        var completed = await Task.WhenAny(invokeTask, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(invokeTask, completed);
        await invokeTask;
    }

    [Fact]
    public async Task InvokeAsync_Cancelled_KillsTheSubprocessRatherThanLeavingItOrphaned()
    {
        var paths = await InitializeRepositoryWithOneCommitAsync();
        var host = new GitHttpBackendHost(paths, NullLogger<GitHttpBackendHost>.Instance);
        var httpContext = NewHttpContext();

        // A request body that never finishes writing -- InvokeAsync's own copy to stdin blocks on it
        // until cancellation fires, giving the subprocess time to actually start before it is killed.
        var slowBody = new NeverEndingStream();

        var request = new GitHttpBackendRequest
        {
            PathInfo = "/git-upload-pack",
            RequestMethod = "POST",
            QueryString = string.Empty,
            ContentType = "application/x-git-upload-pack-request",
            RemoteUser = "alice",
            RequestBody = slowBody,
        };

        using var cts = new CancellationTokenSource();
        var invokeTask = host.InvokeAsync(request, httpContext.Response, cts.Token);

        // Give the subprocess a moment to actually start before cancelling.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotLeakThisProcessAmbientEnvironmentToTheSubprocess()
    {
        // §7 remediation, regression guard for supervisor blocker 1: InvokeAsync used to overlay its
        // own keys onto ProcessStartInfo.Environment, which starts as a COPY of the current process's
        // entire environment -- the subprocess inherited everything, GIT_TRACE included. GIT_TRACE is
        // git's own opt-in trace switch: pointed at a file path, ANY git invocation that inherits it
        // writes trace lines there.
        //
        // Run in a genuine second OS process (EnvironmentIsolationHarnessProcess), not by setting
        // GIT_TRACE on this shared xUnit test host process directly -- that was tried first and
        // produced a real false positive under the full parallel suite (an unrelated, concurrently
        // running test's own git subprocess inherited the ambient value and wrote the marker file too),
        // the exact hazard ReconcileHarnessProcess's own remarks already document for a different
        // ambient variable. Mirrors that harness's shape rather than inventing a new one.
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-git-http-backend-envcheck-{Guid.NewGuid():n}");
        var traceMarkerPath = Path.Combine(Path.GetTempPath(), $"zerowiki-git-trace-leak-{Guid.NewGuid():n}");
        try
        {
            var (leaked, statusCode) = await EnvironmentIsolationHarnessProcess.RunAsync(dataRoot, traceMarkerPath);

            Assert.Equal(200, statusCode);
            Assert.False(
                leaked,
                "The subprocess wrote a GIT_TRACE marker file, which git only does when it inherits " +
                "that environment variable -- proof the harness process's own ambient environment " +
                "reached the subprocess rather than only the constructed set (plus PATH).");
        }
        finally
        {
            if (File.Exists(traceMarkerPath))
            {
                File.Delete(traceMarkerPath);
            }

            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenTheRequestCarriesGitProtocol_NegotiatesTheRequestedProtocolVersion()
    {
        // §7 remediation, regression guard for supervisor blocker 2: HTTP_GIT_PROTOCOL was never
        // forwarded at all, so every clone/fetch/push silently ran protocol v0 -- invisible to every
        // prior test, since every real git client falls back to v0 transparently. Byte-exact prefixes
        // measured against the real binary (DEVLOG §7 remediation thread, same repository, same
        // request, only GitProtocol added) rather than merely asserting "the bytes differ", which a
        // mutant that forwards the value under the wrong variable name could still satisfy by
        // accident.
        var paths = await InitializeRepositoryWithOneCommitAsync();
        var host = new GitHttpBackendHost(paths, NullLogger<GitHttpBackendHost>.Instance);

        var v0Context = NewHttpContext();
        await host.InvokeAsync(
            new GitHttpBackendRequest
            {
                PathInfo = "/info/refs",
                RequestMethod = "GET",
                QueryString = "service=git-upload-pack",
                RemoteUser = "alice",
                RequestBody = Stream.Null,
            },
            v0Context.Response,
            CancellationToken.None);

        var v2Context = NewHttpContext();
        await host.InvokeAsync(
            new GitHttpBackendRequest
            {
                PathInfo = "/info/refs",
                RequestMethod = "GET",
                QueryString = "service=git-upload-pack",
                RemoteUser = "alice",
                RequestBody = Stream.Null,
                GitProtocol = "version=2",
            },
            v2Context.Response,
            CancellationToken.None);

        var v0Body = ReadResponseBody(v0Context);
        var v2Body = ReadResponseBody(v2Context);

        var v0Prefix = "001e# service=git-upload-pack\n"u8.ToArray();
        var v2Prefix = "000eversion 2\n"u8.ToArray();

        Assert.Equal(v0Prefix, v0Body.Take(v0Prefix.Length));
        Assert.Equal(v2Prefix, v2Body.Take(v2Prefix.Length));

        // Not merely "different bytes" -- specifically that neither response carries the other's own
        // opening line, ruling out a mutant that forwards the value under a different variable name and
        // still happens to change the response length for an unrelated reason.
        var v0Head = Encoding.ASCII.GetString(v0Body, 0, Math.Min(v0Body.Length, 32));
        var v2Head = Encoding.ASCII.GetString(v2Body, 0, Math.Min(v2Body.Length, 32));
        Assert.DoesNotContain("version 2", v0Head, StringComparison.Ordinal);
        Assert.DoesNotContain("service=git-upload-pack", v2Head, StringComparison.Ordinal);
    }

    private async Task<ContentPaths> InitializeRepositoryWithOneCommitAsync()
    {
        var paths = Paths;
        var repository = new ContentRepositoryService(
            paths,
            _git,
            new GitHookInstaller(_git),
            NullLogger<ContentRepositoryService>.Instance,
            Options.Create(new ContentStorageOptions { DataRoot = _dataRoot }));

        await repository.EnsureRepositoryAsync();

        var pagePath = Path.Combine(paths.WorkingTree, "page.md");
        await File.WriteAllTextAsync(pagePath, "hello");
        await _git.RunOrThrowAsync(paths.RepositoryRoot, ["add", "docs/page.md"]);
        await _git.RunOrThrowAsync(
            paths.RepositoryRoot,
            ["commit", "-m", "seed"],
            new GitAuthor("Seed", "seed@zerowiki.example").ToEnvironmentVariables());

        return paths;
    }

    private static DefaultHttpContext NewHttpContext() => new()
    {
        Response = { Body = new MemoryStream() },
    };

    private static byte[] ReadResponseBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new MemoryStream();
        httpContext.Response.Body.CopyTo(reader);
        return reader.ToArray();
    }

    /// <summary>A request-body stand-in that never signals end-of-stream on its own, so
    /// <see cref="GitHttpBackendHost.InvokeAsync"/>'s copy to stdin blocks until cancelled.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }
    }
}
