namespace ZeroWiki.Content;

/// <summary>
/// The CGI inputs for one <c>git http-backend</c> invocation (design.md D18 §2) — everything the
/// subprocess needs to answer one Smart HTTP request, carried as plain values rather than an
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/> so <see cref="GitHttpBackendHost"/> never has to
/// reach back into ASP.NET Core's request model to build the environment it hands the subprocess.
/// </summary>
/// <remarks>
/// Deliberately carries no repository path. <c>GIT_PROJECT_ROOT</c> is fixed at
/// <see cref="ContentPaths.RepositoryRoot"/> and supplied by <see cref="GitHttpBackendHost"/> itself,
/// never derived from a request — D18 §1's decision, restated here as a type that structurally cannot
/// carry a request-supplied repository root for a caller to accidentally thread through.
/// </remarks>
public sealed class GitHttpBackendRequest
{
    /// <summary>
    /// <c>PATH_INFO</c> — one of <c>/info/refs</c>, <c>/git-upload-pack</c>, <c>/git-receive-pack</c>
    /// (D18 §1). A fixed literal per route, not derived from the incoming request path.
    /// </summary>
    public required string PathInfo { get; init; }

    /// <summary><c>REQUEST_METHOD</c> — <c>GET</c> for <c>info/refs</c>, <c>POST</c> for the two service endpoints.</summary>
    public required string RequestMethod { get; init; }

    /// <summary>
    /// <c>QUERY_STRING</c>, without the leading <c>?</c> — <c>service=git-upload-pack</c> (or
    /// <c>git-receive-pack</c>) on the <c>info/refs</c> GET, empty on the POSTs (D18 §2).
    /// </summary>
    public required string QueryString { get; init; }

    /// <summary><c>CONTENT_TYPE</c>, forwarded when the request carries one; absent on <c>info/refs</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// <c>CONTENT_LENGTH</c>, forwarded when the request carries one. Absent on <c>info/refs</c> and on
    /// a chunked-transfer POST — <c>git-http-backend</c> reads its own protocol framing to know when it
    /// has a complete request either way (D18 §2), so this is forwarded only when known, never guessed.
    /// </summary>
    public long? ContentLength { get; init; }

    /// <summary>
    /// The request's <c>Content-Encoding</c> header, forwarded verbatim as <c>HTTP_CONTENT_ENCODING</c>
    /// when present — never decoded by the host itself (D18 §2); <c>git-http-backend</c> already
    /// inflates a gzip-compressed body once this is set.
    /// </summary>
    public string? ContentEncoding { get; init; }

    /// <summary>
    /// The request's <c>Git-Protocol</c> header, forwarded verbatim as <c>HTTP_GIT_PROTOCOL</c> when
    /// present — never invented when absent (§7 remediation, supervisor blocker 2). Every git client
    /// since 2.26 sends <c>Git-Protocol: version=2</c> and falls back to protocol v0 transparently when
    /// the server does not answer in kind, which is why an absent forward is silent rather than a
    /// visible failure — and exactly why this field exists rather than being left unforwarded again.
    /// </summary>
    public string? GitProtocol { get; init; }

    /// <summary>
    /// <c>REMOTE_USER</c> — the authenticated account's username, for <c>git-receive-pack</c>'s reflog
    /// identity only (D18 §4). Plays no role in access control; the request already passed
    /// <see cref="ZeroWiki.Web.GitBasicAuthenticationFilter"/> by the time this is built.
    /// </summary>
    public required string RemoteUser { get; init; }

    /// <summary>The request body, copied to the subprocess's raw stdin byte stream, never decoded.</summary>
    public required Stream RequestBody { get; init; }
}
