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
/// <see cref="TryResolveWorkingTreePathFromRouteValue"/> adds a second, independent layer on top: it
/// canonicalizes the result with <see cref="Path.GetFullPath(string)"/> and verifies containment against
/// <see cref="ContentPaths.WorkingTree"/> plus a trailing directory separator — a bare <c>StartsWith</c>
/// against the working tree alone would let a sibling directory whose name happens to start with the same
/// prefix (e.g. <c>docs-evil</c> against <c>docs</c>) pass as contained. Both layers refuse rather than
/// clamp: a hostile route is never silently rewritten into something safe, and never crashes the caller —
/// it is rejected outright.
/// </para>
/// <para>
/// <b>There are two decode entry points, and they are not interchangeable</b> (3b): <see cref="TryDecode"/>
/// expects a route that has not yet been percent-decoded — the canonical, <see cref="Encode"/>d form,
/// carried by the <see cref="EncodedRoute"/> type — and <see cref="TryDecodeRouteValue"/> expects one
/// ASP.NET Core routing has already percent-decoded once, as a <c>/wiki/{*Route}</c> catch-all parameter's
/// value always has been by the time a page sees it, carried by <see cref="RouteValue"/>. Applying the
/// wrong decode pass to the wrong contract double-decodes or under-decodes; see
/// <see cref="TryDecodeRouteValue"/>'s remarks for the empirical evidence.
/// <see cref="TryResolveWorkingTreePathFromRouteValue"/> is the one resolver this class exposes one layer
/// up — it and <see cref="TryDecodeRouteValue"/> are §6's only entry points for a route arriving off an
/// HTTP request; a canonical <see cref="EncodedRoute"/> has no resolver of its own because no caller
/// needs one (§3's read path resolves a canonical route to a file via the enumerated index, never via the
/// filesystem directly).
/// </para>
/// <para>
/// <b>The split is enforced by the type system, not merely by naming and documentation</b> (closing a
/// finding from block 3b round 2 and §6's own D17). <see cref="EncodedRoute"/> and <see cref="RouteValue"/>
/// are distinct types precisely so a caller cannot pass an already-decoded value to
/// <see cref="TryDecode"/>, or a still-encoded one to <see cref="TryDecodeRouteValue"/> or
/// <see cref="TryResolveWorkingTreePathFromRouteValue"/>, and have it silently accept a
/// syntactically-valid-but-wrong string — the compiler refuses the call outright. Before this, an encoded
/// route and a decoded route value containing no <c>%</c> were the same <see cref="string"/> and the mix-up
/// was undetectable at runtime; in §6 that mix-up is a wrong-file <em>write</em>, not merely a wrong read.
/// </para>
/// <para>
/// <b>Decoding a route value is not the same question as whether it is the *right* route value for a
/// page a caller has already found</b> (S2, block 3 remediation) — <see cref="Encode"/>'s
/// non-injectivity means a decode-then-re-<see cref="Encode"/> round trip can land on the same canonical
/// route two different request strings would never both legitimately produce, re-opening D12's own
/// collision through whichever caller resolves a route value to a specific page.
/// <see cref="IsCanonicalRouteValue"/> is that check, and it is the one every such caller must reuse
/// rather than re-derive; see its own remarks for why re-encoding is not an adequate substitute.
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
    /// <remarks>
    /// This is the one production place an <see cref="EncodedRoute"/> is ever constructed (D17, §6 block
    /// C1 delta) — its constructor is <see langword="internal"/> precisely so that is true by construction
    /// rather than by convention.
    /// </remarks>
    public static EncodedRoute Encode(string workingTreeRelativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingTreeRelativePath);

        var normalized = workingTreeRelativePath.Replace('\\', RouteSeparator);
        if (normalized.EndsWith(MarkdownExtension, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^MarkdownExtension.Length];
        }

        var segments = normalized.Split(RouteSeparator);
        return new EncodedRoute(string.Join(RouteSeparator, segments.Select(EncodeSegment)));
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
    /// <see cref="TryResolveWorkingTreePathFromRouteValue"/> instead of refusing cleanly.
    /// </summary>
    /// <remarks>
    /// <b>This method expects a route that has not yet been percent-decoded</b> — carried by the
    /// <see cref="EncodedRoute"/> type, not a bare <see cref="string"/>, so a caller cannot pass it a
    /// value from the other contract by mistake. It is what <see cref="PageEnumerationService"/> uses to
    /// check a freshly-<see cref="Encode"/>d route's own round trip, and it is safe there because that
    /// route is the canonical, still-encoded form. A route arriving through ASP.NET Core routing (a
    /// <c>/wiki/{*Route}</c> catch-all parameter's value) has already been percent-decoded once by the
    /// framework — <see cref="TryDecodeRouteValue"/> is the entry point for that value; calling this
    /// method on it instead double-decodes. See <see cref="TryDecodeRouteValue"/>'s remarks for the
    /// empirical evidence and the failure this causes.
    /// </remarks>
    public static bool TryDecode(EncodedRoute route, out string workingTreeRelativePath) =>
        TryDecodeCore(route.Value, decodePercentEncoding: true, out workingTreeRelativePath);

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
    /// <para>
    /// <paramref name="routeValue"/> is carried by the <see cref="RouteValue"/> type rather than a bare
    /// <see cref="string"/>, so a caller cannot pass it a still-encoded <see cref="EncodedRoute"/> by
    /// mistake — the compiler refuses the call instead of silently under-decoding.
    /// </para>
    /// </remarks>
    public static bool TryDecodeRouteValue(RouteValue routeValue, out string workingTreeRelativePath) =>
        TryDecodeCore(routeValue.Value, decodePercentEncoding: false, out workingTreeRelativePath);

    /// <summary>
    /// Whether <paramref name="routeValue"/> — a value ASP.NET Core routing has already percent-decoded
    /// once — is exactly the received form of <paramref name="canonicalRoute"/> (a still-encoded,
    /// canonical route such as <see cref="EnumeratedPage.Route"/>): the request-side twin of
    /// <see cref="PageEnumerationService"/>'s enumeration-side "the route identifies exactly this file"
    /// check (D12, S2 — block 3 remediation).
    /// </summary>
    /// <remarks>
    /// <b>Why a decode-then-<see cref="Encode"/> round trip is not this check.</b> <see cref="Encode"/> is
    /// not injective (this class's own remarks, above) — a request that decodes to a relative path other
    /// than the one that produced <paramref name="canonicalRoute"/> can still re-<see cref="Encode"/> to
    /// the identical string, which is exactly how the read path re-admitted D12's own collision through
    /// the request door (a route value carrying two literal spaces re-encodes to the same route a single
    /// literal underscore does). Comparing the *request* against <c>Uri.UnescapeDataString</c> of the
    /// *canonical route* — the value the framework would have delivered for the one true URL of that page —
    /// sidesteps the many-to-one collapse entirely rather than trying to detect it after the fact: no
    /// re-encoding happens on this path at all, so there is nothing for two different requests to collide
    /// into. Every caller that resolves a route value to a specific, already-identified page (the read path
    /// here; a save path, later) MUST apply this check before treating the match as trustworthy — reuse it
    /// rather than re-deriving the same comparison. Both parameters are the <see cref="RouteValue"/>/
    /// <see cref="EncodedRoute"/> types rather than bare <see cref="string"/>s so the two cannot be
    /// swapped at a call site — <see cref="Uri.UnescapeDataString(string)"/> is applied to
    /// <paramref name="canonicalRoute"/> only, and passing the arguments in the wrong order would silently
    /// change which side gets decoded if both were plain strings.
    /// </remarks>
    public static bool IsCanonicalRouteValue(RouteValue routeValue, EncodedRoute canonicalRoute) =>
        string.Equals(Uri.UnescapeDataString(canonicalRoute.Value), routeValue.Value, StringComparison.Ordinal);

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
        // Passing a NUL through to Path.GetFullPath (in TryResolveWorkingTreePathFromRouteValue) throws an unhandled
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
    /// Decodes <paramref name="routeValue"/> — the value ASP.NET Core routing has already percent-decoded
    /// once, the same contract <see cref="TryDecodeRouteValue"/> takes, carried by <see cref="RouteValue"/>
    /// — and resolves it to an absolute path guaranteed to sit inside <paramref name="paths"/>'s
    /// <see cref="ContentPaths.WorkingTree"/>, or refuses. This is the containment-checked resolver §6's
    /// save path uses: its route always comes from an HTTP request, so this is the only working-tree
    /// resolver this class exposes — there is deliberately no canonical-route counterpart, because no
    /// caller has ever needed one (§3's read path resolves a canonical route to a file through the
    /// enumerated index, not the filesystem). The trailing-separator containment check below is
    /// security-critical and exists exactly once here.
    /// </summary>
    public static bool TryResolveWorkingTreePathFromRouteValue(ContentPaths paths, RouteValue routeValue, out string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        absolutePath = string.Empty;

        if (!TryDecodeCore(routeValue.Value, decodePercentEncoding: false, out var relativePath))
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
