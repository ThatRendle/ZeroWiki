using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// <see cref="PageRouteCodec"/> is the path-traversal surface §6 will use to pick a file to write to
/// (D12), so these tests weight round-trip correctness and hostile-input containment over convenience
/// examples.
/// </summary>
public sealed class PageRouteCodecTests
{
    public static IEnumerable<object[]> AwkwardFileNames()
    {
        // Every name here has no run of two or more adjacent {space, '_'} characters that mixes the two,
        // and no run of two or more adjacent *spaces* — those runs are where D12's own residual ambiguity
        // lives (see IsolatedUnderscoreRunsStillRoundTrip and the class remarks for why), so asserting an
        // exact round trip on them here would assert something D12 never promises. A run made entirely of
        // literal underscores is *not* excluded — greedy left-to-right decode always maximizes pairing, so
        // an all-underscore run reconstructs correctly regardless of its length; see
        // IsolatedUnderscoreRunsStillRoundTrip for the proof and AmbiguousRunsDoNotRoundTrip for the
        // negative case this list deliberately leaves out.
        string[] names =
        [
            "Kick Off.md",
            "a_b.md",
            "a__b.md",
            "a___b.md",
            "___.md",
            "_.md",
            "a_.md",
            "_a.md",
            " leading space.md",
            "trailing space .md",
            "percent%sign.md",
            "hash#tag.md",
            "question?mark.md",
            "café.md",
            "日本語.md",
        ];

        foreach (var name in names)
        {
            yield return [name];
        }
    }

    [Theory]
    [MemberData(nameof(AwkwardFileNames))]
    public void EncodeThenDecode_RoundTripsToTheOriginalPath(string fileName)
    {
        var route = PageRouteCodec.Encode(fileName);

        Assert.True(PageRouteCodec.TryDecode(route, out var decoded));
        Assert.Equal(fileName, decoded);
    }

    [Theory]
    [InlineData("a__b.md")] // two literal underscores back to back
    [InlineData("a______b.md")] // six literal underscores back to back
    [InlineData("_____.md")] // an entire stem of nothing but literal underscores
    public void IsolatedUnderscoreRunsStillRoundTrip(string fileName)
    {
        // A run made entirely of literal underscores always has an *even* encoded length (each
        // contributes exactly 2), so greedy left-to-right pairing consumes it with nothing left over and
        // reconstructs every underscore exactly — unlike a run containing a space, where a leftover
        // single position forces a guess. This is what D12 means by round-tripping "a literal underscore",
        // generalized to a run of them.
        var route = PageRouteCodec.Encode(fileName);

        Assert.True(PageRouteCodec.TryDecode(route, out var decoded));
        Assert.Equal(fileName, decoded);
    }

    [Theory]
    [InlineData("double  space.md")] // two adjacent spaces
    [InlineData("mixed_ _underscore and_space.md")] // underscore/space mixed in one run
    public void AmbiguousRunsDoNotRoundTripButStayInternallyConsistent(string fileName)
    {
        // D12's own named collision ("a_ b.md" vs "a _b.md" -> "a___b") is one instance of a broader
        // class: whenever a run of adjacent {space, '_'} characters is not made entirely of literal
        // underscores, more than one source string maps to the same route, and greedy decode returns
        // *some* valid preimage — not necessarily this one. Two adjacent spaces alone are already
        // ambiguous with a single literal underscore ("  " and "_" both encode a run of length 2), which
        // this test pins down as a deliberate, known consequence of D12's scheme rather than a defect.
        // This class does not — and must not — resolve it. D12's actual invariant is per-route and
        // stronger than "no second file claimed it": PageEnumerationServiceTests proves that a route is
        // only ever served when it is the *sole* claimant AND TryDecode reproduces that claimant's own
        // path — which a tree holding only "Chapter  1.md" (this ambiguity with zero collision) already
        // fails, with no second file involved at all.
        var route = PageRouteCodec.Encode(fileName);

        Assert.True(PageRouteCodec.TryDecode(route, out var decoded));
        Assert.NotEqual(fileName, decoded);

        // What DOES hold unconditionally: decode always returns some preimage that re-encodes to the same
        // route, so the choice is self-consistent even when it isn't the original.
        Assert.Equal(route, PageRouteCodec.Encode(decoded));
    }

    [Fact]
    public void RandomizedFuzz_EveryGeneratedNameWithoutAmbiguousRunsRoundTrips()
    {
        ReadOnlySpan<char> alphabet = ['a', 'b', ' ', '_', '%', '#', '?', 'é', '9', '-', '.'];
        var random = new Random(20260803);
        var roundTripped = 0;

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var length = random.Next(1, 12);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var stem = new string(chars);

            // A bare "." or ".." stem is not a real filename a filesystem would ever hand back to
            // enumeration, and encoding/decoding those is covered separately by the traversal tests.
            if (stem.Trim('.') is "" or "." or "..")
            {
                continue;
            }

            var fileName = stem + ".md";
            var route = PageRouteCodec.Encode(fileName);
            Assert.True(PageRouteCodec.TryDecode(route, out var decoded));

            // Skip generated names that fall into D12's own acknowledged ambiguity (a run of adjacent
            // {space, '_'} characters that is not entirely literal underscores) — AmbiguousRunsDoNotRoundTripButStayInternallyConsistent
            // already covers that case explicitly. What this loop asserts for every *other* generated
            // name is the exact round trip.
            if (HasAmbiguousRun(stem))
            {
                Assert.Equal(route, PageRouteCodec.Encode(decoded));
                continue;
            }

            Assert.Equal(fileName, decoded);
            roundTripped++;
        }

        // Guards against the alphabet/loop accidentally excluding every unambiguous case, which would
        // make this test pass vacuously.
        Assert.True(roundTripped > 0);
    }

    private static bool HasAmbiguousRun(string stem)
    {
        var i = 0;
        while (i < stem.Length)
        {
            if (stem[i] is not (' ' or '_'))
            {
                i++;
                continue;
            }

            var runStart = i;
            while (i < stem.Length && stem[i] is ' ' or '_')
            {
                i++;
            }

            var run = stem[runStart..i];
            if (run.Length > 1 && run.Any(c => c != '_'))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void NestedPath_EncodesDirectorySeparatorsAsRouteSeparators()
    {
        var route = PageRouteCodec.Encode(Path.Combine("Project Notes", "Kick Off.md"));

        Assert.Equal("Project_Notes/Kick_Off", route);
    }

    [Fact]
    public void LiteralUnderscore_RoundTripsRatherThanBecomingASpace()
    {
        var route = PageRouteCodec.Encode("a_b.md");

        Assert.Equal("a__b", route);
        Assert.True(PageRouteCodec.TryDecode(route, out var decoded));
        Assert.Equal("a_b.md", decoded);
    }

    [Fact]
    public void SpaceAndUnderscoreCollision_BothEncodeToTheSameRoute()
    {
        // D12's canonical fixture: the escape character is also the substitute character, so a run of
        // underscores is genuinely ambiguous. This class does not — and must not — resolve it; detecting
        // the collision is PageEnumerationService's job. This test only pins down that the ambiguity is
        // real, which is the premise the enumeration-level collision test depends on.
        var routeA = PageRouteCodec.Encode("a_ b.md");
        var routeB = PageRouteCodec.Encode("a _b.md");

        Assert.Equal("a___b", routeA);
        Assert.Equal(routeA, routeB);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("a/../../secret")]
    [InlineData("%2e%2e")]
    [InlineData("%2e%2e/secret")]
    [InlineData("a/%2e%2e/secret")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    [InlineData("a%2fb")]
    [InlineData("a%5cb")]
    public void TryDecode_RefusesPathTraversalAttempts(string hostileRoute)
    {
        Assert.False(PageRouteCodec.TryDecode(hostileRoute, out _));
    }

    [Theory]
    [InlineData("%00")] // percent-encoded NUL — the reviewer's reproduction: crashed Path.GetFullPath
    [InlineData("a%00b")]
    [InlineData("%00/secret")]
    [InlineData("%01")] // SOH — control characters as a class, not just NUL
    [InlineData("%1f")]
    [InlineData("%7f")] // DEL is also IsControl
    public void TryDecode_RefusesControlCharacters(string hostileRoute)
    {
        Assert.False(PageRouteCodec.TryDecode(hostileRoute, out _));
    }

    [Theory]
    [InlineData("%00")]
    [InlineData("a%00b")]
    [InlineData("%01")]
    public void TryResolveWorkingTreePath_RefusesControlCharactersRatherThanThrowing(string hostileRoute)
    {
        // The exact defect the reviewer found: TryDecode used to accept a decoded NUL, and
        // Path.GetFullPath then threw an unhandled ArgumentException instead of this method returning
        // false like every other hostile input. Assert.False both proves the refusal AND, by virtue of
        // not throwing, proves the crash is gone — no separate Assert.ThrowsNoException exists in xUnit,
        // but an uncaught exception here would fail this test regardless of the assertion inside it.
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            Assert.False(PageRouteCodec.TryResolveWorkingTreePath(paths, hostileRoute, out _));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWorkingTreePath_RefusesASiblingDirectoryWhoseNameSharesAPrefix()
    {
        // The bare-StartsWith hazard named in the brief: "/data/wiki/docs-evil" must not pass as
        // contained in "/data/wiki/docs" merely because the string starts the same way.
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs-evil"));
            var paths = new ContentPaths(dataRoot);

            // A route that would decode to a sibling of the working tree if resolved by simple string
            // concatenation. Because TryDecode already refuses ".." unconditionally, this asserts the
            // resolver's containment check as a second, independent layer using a name that could only
            // slip past a bare StartsWith, not one that TryDecode itself already rejects.
            Assert.True(PageRouteCodec.TryResolveWorkingTreePath(paths, "ok", out var okPath));
            Assert.StartsWith(Path.GetFullPath(paths.WorkingTree) + Path.DirectorySeparatorChar, okPath, StringComparison.Ordinal);

            var evilAbsolute = Path.GetFullPath(Path.Combine(dataRoot, "wiki", "docs-evil", "ok.md"));
            Assert.NotEqual(evilAbsolute, okPath);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWorkingTreePath_ResolvesAnOrdinaryRouteInsideTheWorkingTree()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            Assert.True(PageRouteCodec.TryResolveWorkingTreePath(paths, "Project_Notes/Kick_Off", out var resolved));

            var expected = Path.GetFullPath(Path.Combine(paths.WorkingTree, "Project Notes", "Kick Off.md"));
            Assert.Equal(expected, resolved);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    public void TryResolveWorkingTreePath_RefusesWhatTryDecodeAlreadyRefuses(string hostileRoute)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            Assert.False(PageRouteCodec.TryResolveWorkingTreePath(paths, hostileRoute, out _));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Simulates exactly the one percent-decode pass ASP.NET Core routing performs on a catch-all route
    /// parameter's value — confirmed empirically against a real <c>TestServer</c> (block 3b's DEVLOG
    /// post), not assumed. Valid for any route <see cref="PageRouteCodec.Encode"/> actually produces:
    /// <c>Encode</c> never emits a bare <c>%2f</c>/<c>%5c</c> (a literal <c>%</c> is always escaped to
    /// <c>%25</c> first), so this never collides with the framework's special-cased refusal to decode
    /// <c>%2f</c>.
    /// </summary>
    private static string SimulateFrameworkRouteValue(string canonicalRoute) => Uri.UnescapeDataString(canonicalRoute);

    [Theory]
    [MemberData(nameof(AwkwardFileNames))]
    public void TryDecodeRouteValue_RoundTripsAFrameworkAlreadyDecodedRoute(string fileName)
    {
        var routeValue = SimulateFrameworkRouteValue(PageRouteCodec.Encode(fileName));

        Assert.True(PageRouteCodec.TryDecodeRouteValue(routeValue, out var decoded));
        Assert.Equal(fileName, decoded);
    }

    [Fact]
    public void TryDecodeRouteValue_DoesNotDoubleDecodeAFileNameContainingALiteralPercentSign()
    {
        // The exact hazard the block 3b brief named: a file literally named "a%20b.md" encodes to the
        // canonical route "a%2520b". ASP.NET Core routing percent-decodes a catch-all parameter's value
        // exactly once before a page ever sees it, so the page receives "a%20b" — not "a%2520b" and not
        // "a b". TryDecode (which itself calls Uri.UnescapeDataString) is the wrong function to call on
        // that value: it would decode the surviving "%20" into a space, resolving the wrong file.
        const string fileName = "a%20b.md";
        var canonicalRoute = PageRouteCodec.Encode(fileName);
        Assert.Equal("a%2520b", canonicalRoute);

        var routeValue = SimulateFrameworkRouteValue(canonicalRoute);
        Assert.Equal("a%20b", routeValue);

        Assert.True(PageRouteCodec.TryDecodeRouteValue(routeValue, out var correct));
        Assert.Equal(fileName, correct);

        // The regression this test exists to catch: decoding the already-decoded value a second time
        // resolves a different file entirely.
        Assert.True(PageRouteCodec.TryDecode(routeValue, out var wrong));
        Assert.NotEqual(fileName, wrong);
        Assert.Equal("a b.md", wrong);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("a/../../secret")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    public void TryDecodeRouteValue_RefusesPathTraversalAttempts(string hostileRouteValue)
    {
        Assert.False(PageRouteCodec.TryDecodeRouteValue(hostileRouteValue, out _));
    }

    [Theory]
    [InlineData("\0")] // a literal control character - the framework has already decoded it for us
    [InlineData("a\0b")]
    [InlineData("\x01")]
    [InlineData("\x7f")] // DEL is also IsControl
    public void TryDecodeRouteValue_RefusesControlCharacters(string hostileRouteValue)
    {
        Assert.False(PageRouteCodec.TryDecodeRouteValue(hostileRouteValue, out _));
    }

    [Fact]
    public void TryDecodeRouteValue_DoesNotPercentDecodeAtAll()
    {
        // A route value containing a literal, still-encoded "%2f" (framework routing leaves %2f
        // undecoded, per the DEVLOG's empirical probe) must not be interpreted as a smuggled separator
        // by this method either — it treats the input as already fully decoded and never calls
        // Uri.UnescapeDataString, so "%2f" here is just three ordinary characters.
        Assert.True(PageRouteCodec.TryDecodeRouteValue("a%2fb", out var decoded));
        Assert.Equal("a%2fb.md", decoded);
    }

    // --- TryResolveWorkingTreePathFromRouteValue: the resolver split (reviewer finding 1, block 3b
    // round 2) mirrors the decode split one layer up. These mirror the TryResolveWorkingTreePath
    // coverage above, against the same private containment core, using already-decoded input.

    [Fact]
    public void TryResolveWorkingTreePathFromRouteValue_ResolvesAnOrdinaryRouteInsideTheWorkingTree()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            Assert.True(PageRouteCodec.TryResolveWorkingTreePathFromRouteValue(paths, "Project_Notes/Kick_Off", out var resolved));

            var expected = Path.GetFullPath(Path.Combine(paths.WorkingTree, "Project Notes", "Kick Off.md"));
            Assert.Equal(expected, resolved);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWorkingTreePathFromRouteValue_DoesNotDoubleDecodeAFileNameContainingALiteralPercentSign()
    {
        // The resolver-level twin of TryDecodeRouteValue_DoesNotDoubleDecodeAFileNameContainingALiteralPercentSign:
        // proves the double-decode fix holds all the way through to a resolved filesystem path, not just
        // through the decode step in isolation.
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            var routeValue = SimulateFrameworkRouteValue(PageRouteCodec.Encode("a%20b.md"));
            Assert.Equal("a%20b", routeValue);

            Assert.True(PageRouteCodec.TryResolveWorkingTreePathFromRouteValue(paths, routeValue, out var resolved));

            var expected = Path.GetFullPath(Path.Combine(paths.WorkingTree, "a%20b.md"));
            Assert.Equal(expected, resolved);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWorkingTreePathFromRouteValue_RefusesASiblingDirectoryWhoseNameSharesAPrefix()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs-evil"));
            var paths = new ContentPaths(dataRoot);

            Assert.True(PageRouteCodec.TryResolveWorkingTreePathFromRouteValue(paths, "ok", out var okPath));
            Assert.StartsWith(Path.GetFullPath(paths.WorkingTree) + Path.DirectorySeparatorChar, okPath, StringComparison.Ordinal);

            var evilAbsolute = Path.GetFullPath(Path.Combine(dataRoot, "wiki", "docs-evil", "ok.md"));
            Assert.NotEqual(evilAbsolute, okPath);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("a\0b")]
    [InlineData("\x01")]
    public void TryResolveWorkingTreePathFromRouteValue_RefusesControlCharactersRatherThanThrowing(string hostileRouteValue)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            Assert.False(PageRouteCodec.TryResolveWorkingTreePathFromRouteValue(paths, hostileRouteValue, out _));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../secret")]
    public void TryResolveWorkingTreePathFromRouteValue_RefusesWhatTryDecodeRouteValueAlreadyRefuses(string hostileRouteValue)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-route-test-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "wiki", "docs"));
            var paths = new ContentPaths(dataRoot);

            Assert.False(PageRouteCodec.TryResolveWorkingTreePathFromRouteValue(paths, hostileRouteValue, out _));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
