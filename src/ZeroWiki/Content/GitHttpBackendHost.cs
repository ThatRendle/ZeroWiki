using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// D18 §3: the byte-stream subprocess host for <c>git http-backend</c> — a distinct type, never an
/// overload of <see cref="GitProcessRunner"/>. <see cref="GitProcessRunner.RunAsync"/> reads stdout
/// through a <see cref="TextReader"/> (<c>ReadToEndAsync</c>) and never redirects stdin at all, both
/// fatal for a CGI backend that streams a binary packfile in either direction — measured by execution
/// (design.md D18 §3): a captured <c>git-upload-pack</c> response failed UTF-8 decode at a fixed byte
/// offset and contained a NUL byte, and a push has no channel to send its packfile through at all
/// without a redirected stdin.
/// </summary>
/// <remarks>
/// <para>
/// <b>No text decoding on either stream.</b> The request body is copied to the subprocess's raw stdin
/// byte stream and its raw stdout byte stream is copied to the response body, both as bytes — the CGI
/// response header block (D18 §2's blank-line rule) is peeled off that same raw byte stream by
/// pattern-matching the literal bytes <c>\r\n\r\n</c>, never by decoding the whole stream to a
/// <see cref="string"/> and re-encoding it. Only the header block itself — once correctly delimited at
/// the byte level — is decoded, because CGI headers are inherently textual; the packfile body that
/// follows never is.
/// </para>
/// <para>
/// <b>Cancellation kills the whole process tree.</b> Not a new obligation — §6 obligation 8's already-
/// paid lesson (<see cref="GitProcessRunner"/>'s own remarks): <c>http-backend</c> forks
/// <c>git-upload-pack</c>/<c>git-receive-pack</c>, which itself forks hooks, so killing only the
/// immediate child would just move the orphan one level down.
/// </para>
/// <para>
/// <b>Stdin is closed once the request-body copy finishes — success or failure — unconditionally,
/// regardless of whether <c>CONTENT_LENGTH</c> was present.</b> Measured (D18 §2): a complete request is
/// self-delimiting (git's own wire framing — a flush-pkt, or the packfile's own trailing checksum — tells
/// the backend it has read a whole request, independent of the pipe) and needs no EOF to finish
/// promptly; a body truncated by a dropped client connection, with stdin left open, hangs
/// <c>git-receive-pack</c> indefinitely. Closing stdin unconditionally is what turns that hang into a
/// clean git-level failure instead of holding §7.5's write lock forever.
/// </para>
/// </remarks>
public sealed class GitHttpBackendHost(ContentPaths paths, ILogger<GitHttpBackendHost> logger)
{
    /// <summary>Mirrors <see cref="GitProcessRunner"/>'s own bound (see its remarks) for the same reason.</summary>
    private static readonly TimeSpan PostKillWaitTimeout = TimeSpan.FromSeconds(3);

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// Runs one <c>git http-backend</c> invocation for <paramref name="request"/>, streaming its result
    /// onto <paramref name="response"/>. Never throws for anything the CGI protocol itself reports — a
    /// refusal (<c>Status: 404</c>/<c>403</c>) is translated onto <paramref name="response"/> exactly
    /// like a success; an exception here means the host itself, not <c>git-http-backend</c>, failed.
    /// </summary>
    public async Task InvokeAsync(GitHttpBackendRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("http-backend");

        // §7 remediation (supervisor blocker 1): ProcessStartInfo.Environment starts as a COPY of the
        // *current* process's entire environment -- assigning keys onto it, as this method used to,
        // is an overlay, not a replacement, and the subprocess inherited everything the app itself had
        // (HOME included; measured at 108 ambient variables). Clear() is what actually excludes
        // anything the block below does not explicitly re-add.
        //
        // PATH is the one deliberate exception, forwarded from this process's own environment rather
        // than hardcoded: ProcessStartInfo("git") resolves the `git` binary itself via PATH, so
        // clearing without restoring it breaks process startup outright. Nothing else ambient is
        // needed -- measured by execution inside the shipped mcr.microsoft.com/dotnet/aspnet:10.0
        // image, not assumed: a git-http-backend invocation given only PATH plus
        // BuildEnvironmentVariables' own list, under a fully cleared environment (`env -i`), produced
        // output identical in shape to one with this process's full ambient environment attached, for
        // both info/refs and a real push exercising the installed no-op hooks (DEVLOG §7 remediation
        // thread). No `LANG`/`TZ`/locale variable is read anywhere in this call chain.
        startInfo.Environment.Clear();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            startInfo.Environment["PATH"] = path;
        }

        foreach (var (key, value) in BuildEnvironmentVariables(request))
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Kicked off before awaiting WaitForExitAsync below, so both directions are drained
        // concurrently with the process's own lifetime -- the same shape GitProcessRunner.RunAsync
        // already uses for stdout/stderr. A large push's packfile (stdin) and a large clone's response
        // (stdout) both exceed the OS pipe buffer; writing one to completion before ever reading the
        // other would deadlock the moment either side blocks on a full pipe.
        var writeStdinTask = CopyRequestBodyToStdinAsync(request.RequestBody, process, cancellationToken);
        var relayResponseTask = RelayResponseAsync(process.StandardOutput.BaseStream, response, cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await ObserveAsync(writeStdinTask).ConfigureAwait(false);
            await ObserveAsync(relayResponseTask).ConfigureAwait(false);
            await ObserveAsync(standardErrorTask).ConfigureAwait(false);
            throw;
        }

        // The process has exited; both tasks were already running concurrently with it, so this only
        // surfaces whichever has not yet finished draining its own stream -- never blocks the process
        // itself, which is already gone.
        await writeStdinTask.ConfigureAwait(false);
        await relayResponseTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "git http-backend (PATH_INFO '{PathInfo}') exited {ExitCode}: {StandardError}",
                request.PathInfo,
                process.ExitCode,
                standardError.Trim());
        }
    }

    /// <summary>
    /// D18 §2's environment set — the entire deliberate set the subprocess sees, together with the
    /// forwarded <c>PATH</c> that <see cref="InvokeAsync"/> adds separately (see its own remarks).
    /// <c>GIT_PROJECT_ROOT</c> and <c>GIT_HTTP_EXPORT_ALL</c> are fixed facts of this deployment, never
    /// derived from <paramref name="request"/>. <c>HOME</c> is never set — <see cref="InvokeAsync"/>
    /// clears the subprocess's environment before this method's values (and <c>PATH</c>) are the only
    /// things added back, so there is no ambient value left for it to inherit — and the
    /// <c>Dockerfile</c>'s system-scope <c>git config --system --add safe.directory '*'</c> is what
    /// makes that safe rather than merely convenient.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildEnvironmentVariables(GitHttpBackendRequest request)
    {
        var variables = new Dictionary<string, string>
        {
            ["GIT_PROJECT_ROOT"] = paths.RepositoryRoot,

            // Presence, not value, is what git-http-backend checks (D18 §2) -- set unconditionally, on
            // every invocation. Never a per-repo `git-daemon-export-ok` marker file: for a non-bare
            // repository that marker has to live inside .git/, not at the repo root next to docs/ (tried
            // and 404s), and GIT_HTTP_EXPORT_ALL needs no marker file anywhere.
            ["GIT_HTTP_EXPORT_ALL"] = string.Empty,

            ["PATH_INFO"] = request.PathInfo,
            ["REQUEST_METHOD"] = request.RequestMethod,
            ["QUERY_STRING"] = request.QueryString,
            ["REMOTE_USER"] = request.RemoteUser,
        };

        if (request.ContentType is not null)
        {
            variables["CONTENT_TYPE"] = request.ContentType;
        }

        if (request.ContentLength is not null)
        {
            variables["CONTENT_LENGTH"] = request.ContentLength.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (request.ContentEncoding is not null)
        {
            // Forwarded verbatim, never decoded here -- git-http-backend already inflates a
            // gzip-compressed body itself (D18 §2); decoding it a second time here would double-decode
            // or race which layer does it, for no benefit.
            variables["HTTP_CONTENT_ENCODING"] = request.ContentEncoding;
        }

        if (request.GitProtocol is not null)
        {
            // §7 remediation (supervisor blocker 2): forwarded verbatim, never invented -- a client
            // that sends no `Git-Protocol` header gets no `HTTP_GIT_PROTOCOL` variable, and
            // git-http-backend answers protocol v0 exactly as it always has. `git-http-backend` itself
            // reads this variable and re-exports it as `GIT_PROTOCOL` to the child it execs
            // (`upload-pack`/`receive-pack`); this host does not construct `GIT_PROTOCOL` directly, so
            // there is exactly one place a client's requested protocol version can come from.
            variables["HTTP_GIT_PROTOCOL"] = request.GitProtocol;
        }

        return variables;
    }

    private static async Task CopyRequestBodyToStdinAsync(Stream requestBody, Process process, CancellationToken cancellationToken)
    {
        try
        {
            await requestBody.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The subprocess closed its own stdin (or exited outright) before this copy finished --
            // reachable, not hypothetical: git-http-backend can decide a request is malformed from its
            // first pkt-line alone and stop reading before the client has sent the rest. That is the
            // subprocess's own answer, already on its way out via the concurrent stdout relay
            // (RelayResponseAsync); a write racing a pipe the reader on the other end has already
            // closed is not a fault of this host's own making and must not fail the whole invocation
            // out from under a response that is otherwise being served correctly.
        }
        finally
        {
            // Always -- success, failure, or cancellation, regardless of whether CONTENT_LENGTH was
            // set (D18 §2/§3(d)). This is what turns a body truncated by a dropped client connection
            // into a clean git-level failure instead of an indefinite hang holding §7.5's write lock.
            process.StandardInput.BaseStream.Close();
        }
    }

    /// <summary>
    /// Reads the CGI header block off <paramref name="stdout"/> at the byte level, applies it to
    /// <paramref name="response"/> (D18 §2's translation rule), then streams everything after it as the
    /// response body unmodified.
    /// </summary>
    private static async Task RelayResponseAsync(Stream stdout, HttpResponse response, CancellationToken cancellationToken)
    {
        var headerBlock = await ReadCgiHeaderBlockAsync(stdout, cancellationToken).ConfigureAwait(false);

        response.StatusCode = headerBlock.StatusCode;
        foreach (var (name, value) in headerBlock.Headers)
        {
            response.Headers[name] = value;
        }

        if (headerBlock.BodyPrefix.Length > 0)
        {
            await response.Body.WriteAsync(headerBlock.BodyPrefix, cancellationToken).ConfigureAwait(false);
        }

        await stdout.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CgiHeaderBlock> ReadCgiHeaderBlockAsync(Stream stdout, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var index = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(HeaderTerminator);
            if (index >= 0)
            {
                var headerBytes = buffer.GetBuffer()[..index];
                var bodyStart = index + HeaderTerminator.Length;
                var bodyPrefix = buffer.GetBuffer()[bodyStart..(int)buffer.Length];
                return ParseHeaderBlock(headerBytes, bodyPrefix);
            }

            var read = await stdout.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // git-http-backend closed stdout before completing a header block -- no headers to
                // translate. A well-behaved invocation never does this (D18 §2); this path only
                // guarantees the host still returns something coherent (200, no headers, whatever
                // bytes did arrive) rather than hanging or throwing.
                return ParseHeaderBlock(buffer.ToArray(), []);
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>
    /// D18 §2's translation rule: split the header block on <c>\r\n</c>; a <c>Status:</c> line's
    /// leading token supplies the numeric code; its absence means 200; every other line is forwarded as
    /// a response header unchanged.
    /// </summary>
    private static CgiHeaderBlock ParseHeaderBlock(byte[] headerBytes, byte[] bodyPrefix)
    {
        var statusCode = 200;
        var headers = new List<(string Name, string Value)>();

        if (headerBytes.Length > 0)
        {
            foreach (var line in Encoding.ASCII.GetString(headerBytes).Split("\r\n"))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex < 0)
                {
                    continue;
                }

                var name = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();

                if (string.Equals(name, "Status", StringComparison.OrdinalIgnoreCase))
                {
                    var token = value.Split(' ', 2)[0];
                    if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedStatus))
                    {
                        statusCode = parsedStatus;
                    }

                    continue;
                }

                headers.Add((name, value));
            }
        }

        return new CgiHeaderBlock(statusCode, headers, bodyPrefix);
    }

    /// <summary>Mirrors <see cref="GitProcessRunner"/>'s own kill-the-whole-tree handling (see its
    /// remarks for why each documented exception type is caught rather than any exception).</summary>
    private async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or Win32Exception)
        {
            logger.LogError(
                ex,
                "Failed to kill the git http-backend subprocess tree (pid {ProcessId}) after " +
                "cancellation; a descendant may still be running.",
                TryGetProcessId(process));
        }

        using var timeoutCancellation = new CancellationTokenSource(PostKillWaitTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            logger.LogError(
                "git http-backend subprocess (pid {ProcessId}) did not exit within {Timeout} after " +
                "being killed; it may be stuck and is now orphaned.",
                TryGetProcessId(process),
                PostKillWaitTimeout);
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private readonly record struct CgiHeaderBlock(int StatusCode, IReadOnlyList<(string Name, string Value)> Headers, byte[] BodyPrefix);
}
