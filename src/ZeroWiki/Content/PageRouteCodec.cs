using System.Text;

namespace ZeroWiki.Content;

/// <summary>
/// Encodes a working-tree-relative Markdown path into a page route, and decodes a route back into a
/// path (D12): a space becomes <c>_</c>, a literal <c>_</c> becomes <c>__</c>, then whatever remains
/// URL-significant (<c>%</c>, <c>#</c>, <c>?</c>, control characters) is percent-encoded. Directory
/// separators stay separators; each path segment is encoded independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Encode is not injective</b> — <c>a_ b.md</c> and <c>a _b.md</c> both encode to <c>a___b</c>, and
/// more generally <c>Chapter  1.md</c> (two spaces) and <c>Chapter_1.md</c> collide the same way, because
/// the escape character (<c>_</c>) is also the substitute character for a space. D12's invariant — a route
/// is servable only when exactly one file claims it <i>and</i> decoding that route reproduces that file's
/// own path — is checked during enumeration (see <see cref="PageEnumerationService"/>), not here: this
/// class only ever encodes or decodes one path/route at a time and has no way to know whether any file, or
/// which one, claims the same route. <see cref="TryDecode"/> is therefore total on every syntactically
/// valid route it is given, but is deliberately not the answer to "which file does this route serve" —
/// that answer belongs to the enumeration index, which is the only place that has seen every file.
/// </para>
/// <para>
/// <b>Decoding is a path-traversal surface, and also a robustness surface.</b> A route is
/// attacker-controlled — it arrives from a browser URL or, eventually, a save request choosing where to
/// write. <see cref="TryDecode"/> refuses a route whose decoded form contains <c>.</c>, <c>..</c>, an
/// empty segment, a control character (including a percent-encoded NUL, <c>%00</c> — passing one through
/// to <see cref="Path.GetFullPath(string)"/> throws an unhandled <see cref="ArgumentException"/> rather
/// than refusing cleanly, which is exactly the failure this check exists to prevent), or a segment that
/// decodes to contain a fresh path separator (e.g. a segment <c>a%2fb</c> percent-decodes to <c>a/b</c>,
/// which would otherwise let one encoded segment smuggle in an extra directory level).
/// <see cref="TryResolveWorkingTreePath"/> adds a second, independent layer on top: it canonicalizes the
/// result with <see cref="Path.GetFullPath(string)"/> and verifies containment against
/// <see cref="ContentPaths.WorkingTree"/> plus a trailing directory separator — a bare <c>StartsWith</c>
/// against the working tree alone would let a sibling directory whose name happens to start with the same
/// prefix (e.g. <c>docs-evil</c> against <c>docs</c>) pass as contained. Both layers refuse rather than
/// clamp: a hostile route is never silently rewritten into something safe, and never crashes the caller —
/// it is rejected outright.
/// </para>
/// <para>
/// <b>There are two decode entry points, and they are not interchangeable</b> (3b): <see cref="TryDecode"/>
/// expects a route that has not yet been percent-decoded — the canonical, <see cref="Encode"/>d form — and
/// <see cref="TryDecodeRouteValue"/> expects one ASP.NET Core routing has already percent-decoded once, as
/// a <c>/wiki/{*Route}</c> catch-all parameter's value always has been by the time a page sees it. Applying
/// the wrong one double-decodes or under-decodes; see <see cref="TryDecodeRouteValue"/>'s remarks for the
/// empirical evidence. <see cref="TryResolveWorkingTreePath"/> and
/// <see cref="TryResolveWorkingTreePathFromRouteValue"/> mirror this same split one layer up, sharing one
/// private containment check so the security-critical part of that class exists exactly once regardless
/// of which decode contract a caller starts from.
/// </para>
/// <para>
/// <b>This split is enforced by naming and documentation, not by the type system</b> (a reviewer/architect
/// finding, block 3b round 2) — nothing stops a caller from passing an already-decoded value to
/// <see cref="TryDecode"/>/<see cref="TryResolveWorkingTreePath"/> and having it silently accept a
/// syntactically-valid-but-wrong string. A durable fix would give the two decode states distinct types
/// (e.g. an <c>EncodedRoute</c>/<c>DecodedRoutePath</c> pair) so the mix-up is a compile error rather than
/// a documentation duty. Deliberately not built here — this class's callers so far are all internal to
/// §3, and the redesign is §6's to make when the save path exists and the real shape of that caller is
/// known, not to guess at in advance.
/// </para>
/// </remarks>
public static class PageRouteCodec
{
    private const string MarkdownExtension = ".md";
    private const char RouteSeparator = '/';

    /// <summary>
    /// Encodes <paramref name="workingTreeRelativePath"/> — a Markdown file's path relative to
    /// <see cref="ContentPaths.WorkingTree"/>, using either directory separator — into a route: the
    /// <c>.md</c> extension dropped, each path segment encoded independently per D12, segments rejoined
    /// with <c>/</c>. Does not include the <c>/wiki/</c> prefix; that belongs to whichever Razor
    /// component builds the final URL.
    /// </summary>
    public static string Encode(string workingTreeRelativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingTreeRelativePath);

        var normalized = workingTreeRelativePath.Replace('\\', RouteSeparator);
        if (normalized.EndsWith(MarkdownExtension, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^MarkdownExtension.Length];
        }

        var segments = normalized.Split(RouteSeparator);
        return string.Join(RouteSeparator, segments.Select(EncodeSegment));
    }

    private static string EncodeSegment(string segment)
    {
        // Layer 1: space -> '_', literal '_' -> '__'. Applied first so layer 2 can never disturb it —
        // '_' is RFC 3986 unreserved, so no percent-encoder ever touches it.
        var layer1 = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (c == ' ')
            {
                layer1.Append('_');
            }
            else if (c == '_')
            {
                layer1.Append('_', 2);
            }
            else
            {
                layer1.Append(c);
            }
        }

        // Layer 2: percent-encode what remains URL-significant. Escaping every literal '%' here is what
        // stops a filename like "a%20b.md" from producing a route that a URL decoder would turn back into
        // "a b" — see D12. Non-ASCII is left bare (survives the wire round trip via ordinary UTF-8 URL
        // encoding, transparent to this class).
        var layer2 = new StringBuilder(layer1.Length);
        foreach (var c in layer1.ToString())
        {
            if (c is '%' or '#' or '?' || char.IsControl(c))
            {
                foreach (var b in Encoding.UTF8.GetBytes([c]))
                {
                    layer2.Append('%').Append(b.ToString("X2"));
                }
            }
            else
            {
                layer2.Append(c);
            }
        }

        return layer2.ToString();
    }

    /// <summary>
    /// Decodes <paramref name="route"/> back into a working-tree-relative path (with <c>/</c> separators
    /// and the <c>.md</c> extension restored), reversing D12's two layers in the opposite order:
    /// percent-decode first, then <c>__</c> → <c>_</c> and <c>_</c> → space, scanned left to right.
    /// Refuses — returns <see langword="false"/> — rather than guess, on anything that is not a
    /// straightforward relative path once decoded: an empty route, an empty segment (a route with a
    /// leading, trailing, or doubled <c>/</c>), a segment that decodes to <c>.</c> or <c>..</c> (including
    /// the percent-encoded form <c>%2e%2e</c>, which decodes to <c>..</c> before this check runs), a segment
    /// whose decoded form contains a directory separator it did not have before decoding, or a segment
    /// whose decoded form contains a control character (including a percent-encoded NUL, <c>%00</c>) —
    /// no enumerated file could ever produce one, and passing it through would crash a caller such as
    /// <see cref="TryResolveWorkingTreePath"/> instead of refusing cleanly.
    /// </summary>
    /// <remarks>
    /// <b>This method expects a route that has not yet been percent-decoded.</b> It is what
    /// <see cref="PageEnumerationService"/> uses to check a freshly-<see cref="Encode"/>d route's own
    /// round trip, and it is safe there because that route is the canonical, still-encoded form. A route
    /// arriving through ASP.NET Core routing (a <c>/wiki/{*Route}</c> catch-all parameter's value) has
    /// already been percent-decoded once by the framework — <see cref="TryDecodeRouteValue"/> is the
    /// entry point for that value; calling this method on it instead double-decodes. See
    /// <see cref="TryDecodeRouteValue"/>'s remarks for the empirical evidence and the failure this causes.
    /// </remarks>
    public static bool TryDecode(string route, out string workingTreeRelativePath) =>
        TryDecodeCore(route, decodePercentEncoding: true, out workingTreeRelativePath);

    /// <summary>
    /// Decodes <paramref name="routeValue"/> — the value ASP.NET Core routing has already bound to a
    /// <c>/wiki/{*Route}</c> catch-all parameter (or any other route parameter carrying a page route) —
    /// back into a working-tree-relative path. Refuses on exactly the same inputs <see cref="TryDecode"/>
    /// refuses on; the two differ only in whether the input still needs percent-decoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The seam block 3b exists to get right, verified empirically rather than assumed or read from
    /// documentation.</b> A route's canonical form — <see cref="Encode"/>'s output, and what
    /// <see cref="EnumeratedPage.Route"/> stores — is still percent-encoded: <c>%</c>, <c>#</c>, <c>?</c>
    /// and control characters are escaped (D12). Probing the real routing pipeline directly (a
    /// <c>TestServer</c> reading <c>HttpContext.Request.RouteValues</c>, not reasoning about it) showed
    /// ASP.NET Core percent-decodes a catch-all parameter's value exactly once before a page ever sees
    /// it: requesting the canonical route for a file literally named <c>a%20b.md</c> — route
    /// <c>a%2520b</c> — arrives at the page as the string <c>a%20b</c>. Framework decoding already turned
    /// <c>%25</c> into <c>%</c> and left the remaining <c>20b</c> untouched: one decode pass, not two.
    /// The same probe showed the framework leaves <c>%2f</c>/<c>%2F</c> undecoded (so a catch-all segment
    /// can never smuggle in a fresh <c>/</c> this way), while an unescaped literal <c>%5c</c> <i>is</i>
    /// decoded into a real backslash — both already-established hazards this method's validation
    /// (unchanged from <see cref="TryDecode"/>'s) still catches.
    /// </para>
    /// <para>
    /// Calling <see cref="TryDecode"/> — which itself calls <see cref="Uri.UnescapeDataString(string)"/>
    /// — on an already-decoded route value decodes it a second time, turning the surviving <c>%20</c>
    /// into a space and resolving <c>a b.md</c>: the wrong file, through the same seam 3a's two blockers
    /// both lived in. This method shares <see cref="TryDecode"/>'s validation and layer-1 reverse but
    /// skips the percent-decode step, so the net effect across a whole request is exactly one
    /// percent-decode — ASP.NET Core's — never two. <see cref="TryDecode"/> and this method must never
    /// both be applied to the same string.
    /// </para>
    /// </remarks>
    public static bool TryDecodeRouteValue(string routeValue, out string workingTreeRelativePath) =>
        TryDecodeCore(routeValue, decodePercentEncoding: false, out workingTreeRelativePath);

    private static bool TryDecodeCore(string route, bool decodePercentEncoding, out string workingTreeRelativePath)
    {
        workingTreeRelativePath = string.Empty;

        if (string.IsNullOrEmpty(route))
        {
            return false;
        }

        var segments = route.Split(RouteSeparator);
        var decodedSegments = new string[segments.Length];

        for (var i = 0; i < segments.Length; i++)
        {
            if (!TryDecodeSegment(segments[i], decodePercentEncoding, out var decoded))
            {
                return false;
            }

            decodedSegments[i] = decoded;
        }

        workingTreeRelativePath = string.Join(RouteSeparator, decodedSegments) + MarkdownExtension;
        return true;
    }

    private static bool TryDecodeSegment(string segment, bool decodePercentEncoding, out string decoded)
    {
        decoded = string.Empty;

        if (segment.Length == 0)
        {
            // An empty segment can only come from a leading/trailing/doubled '/' in the route — never a
            // real filename component.
            return false;
        }

        // Uri.UnescapeDataString never throws on malformed %-sequences; it leaves them as literal text.
        // It can, however, decode a segment's own '%2f'/'%5c' into a fresh '/' or '\' that this segment
        // did not contain before decoding, which is exactly the smuggled-separator hazard checked below.
        // When the caller is TryDecodeRouteValue, this step is skipped entirely — ASP.NET Core routing
        // has already percent-decoded the value once, and decoding it again is the double-decode bug
        // this class exists to avoid (see TryDecodeRouteValue's remarks).
        var percentDecoded = decodePercentEncoding ? Uri.UnescapeDataString(segment) : segment;

        if (percentDecoded.Contains('/') || percentDecoded.Contains('\\'))
        {
            return false;
        }

        // Reject the whole class of control characters, not just '\0': EncodeSegment escapes every
        // control character on the way in (mirroring this check), and a route can carry one directly
        // (e.g. a percent-encoded NUL, '%00') without ever needing to have come from a real filename.
        // Passing a NUL through to Path.GetFullPath (in TryResolveWorkingTreePath) throws an unhandled
        // ArgumentException — this class's own contract is to refuse a hostile route outright, not let
        // one crash the caller.
        if (percentDecoded.Any(char.IsControl))
        {
            return false;
        }

        // Layer 1 reverse: '__' -> '_', a lone '_' -> space, scanned left to right per D12.
        var sb = new StringBuilder(percentDecoded.Length);
        var i = 0;
        while (i < percentDecoded.Length)
        {
            var c = percentDecoded[i];
            if (c == '_')
            {
                if (i + 1 < percentDecoded.Length && percentDecoded[i + 1] == '_')
                {
                    sb.Append('_');
                    i += 2;
                }
                else
                {
                    sb.Append(' ');
                    i += 1;
                }
            }
            else
            {
                sb.Append(c);
                i += 1;
            }
        }

        var result = sb.ToString();

        if (result is "." or "..")
        {
            return false;
        }

        decoded = result;
        return true;
    }

    /// <summary>
    /// Decodes <paramref name="route"/> — a canonical, still percent-encoded route, the same contract as
    /// <see cref="TryDecode"/> — and resolves it to an absolute path guaranteed to sit inside
    /// <paramref name="paths"/>'s <see cref="ContentPaths.WorkingTree"/>, or refuses.
    /// </summary>
    /// <remarks>
    /// <b>Input contract, stated because getting it wrong here is the sharpest hazard in this class</b>
    /// (3b): this method expects the same still-encoded form <see cref="TryDecode"/> does — e.g.
    /// <see cref="EnumeratedPage.Route"/>, or anything freshly produced by <see cref="Encode"/>. A route
    /// obtained from ASP.NET Core routing (a <c>/wiki/{*Route}</c> catch-all parameter's value, or
    /// anything else routing has already percent-decoded once) is the <i>other</i> contract —
    /// <see cref="TryResolveWorkingTreePathFromRouteValue"/> is that entry point. Calling this method on
    /// an already-decoded value double-decodes and can resolve the wrong file; see
    /// <see cref="TryDecodeRouteValue"/>'s remarks for the empirical evidence. A save path (§6) that gets
    /// its route from an HTTP request must use <see cref="TryResolveWorkingTreePathFromRouteValue"/>, not
    /// this one — <c>WikiPage.razor</c> (3b) makes the identical read-side distinction between the two
    /// decode entry points, for the identical reason.
    /// </remarks>
    public static bool TryResolveWorkingTreePath(ContentPaths paths, string route, out string absolutePath) =>
        TryResolveWorkingTreePathCore(paths, route, decodePercentEncoding: true, out absolutePath);

    /// <summary>
    /// Decodes <paramref name="routeValue"/> — the value ASP.NET Core routing has already percent-decoded
    /// once, the same contract as <see cref="TryDecodeRouteValue"/> — and resolves it to an absolute path
    /// guaranteed to sit inside <paramref name="paths"/>'s <see cref="ContentPaths.WorkingTree"/>, or
    /// refuses. This is the containment-checked resolver a save path (§6) needs when its route comes from
    /// an HTTP request: the trailing-separator containment check below is security-critical and must
    /// exist exactly once, shared with <see cref="TryResolveWorkingTreePath"/> via the same private core
    /// rather than reimplemented at each call site.
    /// </summary>
    public static bool TryResolveWorkingTreePathFromRouteValue(ContentPaths paths, string routeValue, out string absolutePath) =>
        TryResolveWorkingTreePathCore(paths, routeValue, decodePercentEncoding: false, out absolutePath);

    private static bool TryResolveWorkingTreePathCore(
        ContentPaths paths, string route, bool decodePercentEncoding, out string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        absolutePath = string.Empty;

        if (!TryDecodeCore(route, decodePercentEncoding, out var relativePath))
        {
            return false;
        }

        var workingTree = Path.GetFullPath(paths.WorkingTree);
        var containmentPrefix = workingTree.EndsWith(Path.DirectorySeparatorChar)
            ? workingTree
            : workingTree + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(
            Path.Combine(workingTree, relativePath.Replace(RouteSeparator, Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(containmentPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        absolutePath = candidate;
        return true;
    }
}
