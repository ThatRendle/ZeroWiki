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
