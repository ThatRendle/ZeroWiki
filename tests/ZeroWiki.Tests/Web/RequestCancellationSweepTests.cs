namespace ZeroWiki.Tests.Web;

/// <summary>
/// §4.5 — the only mechanical evidence §3's call-site work exists, since §3 changed no runtime
/// behaviour: the omitted arguments it replaced already bound to
/// <see cref="CancellationToken.None"/> via each service's own default parameter, so pre- and
/// post-§3 code are behaviourally identical and no behavioural test at any level can tell them
/// apart. Reads the six pages' source text directly through <see cref="ServiceCallSweep"/> —
/// proven against known-present and known-absent markup in
/// <see cref="ServiceCallSweepInstrumentTests"/> — and asserts both directions of D1/D2 at all
/// fifteen known call sites: no de-authorisation call carries a request-scoped token, and no read
/// or create call omits one.
/// </summary>
public sealed class RequestCancellationSweepTests
{
    private enum SiteKind
    {
        ReadOrCreate,
        DeAuthorisation,
    }

    // The fifteen call sites the §1 supervisor counted (12 read/create, 3 de-authorisation) and
    // §2/§3 filled in, one entry each. A call site going missing, or a sixteenth appearing, fails
    // Every_page_contains_exactly_the_fifteen_known_calls_and_no_others below rather than being
    // silently skipped by the per-site theories, which only ever look at sites named here.
    private static readonly (string File, string Service, string Method, SiteKind Kind)[] KnownSites =
    [
        ("Bootstrap.razor", "BootstrapService", "IsAvailableAsync", SiteKind.ReadOrCreate),
        ("Bootstrap.razor", "BootstrapService", "CreateFirstAdministratorAsync", SiteKind.ReadOrCreate),
        ("BootstrapComplete.razor", "BootstrapService", "IsAvailableAsync", SiteKind.ReadOrCreate),
        ("Login.razor", "LoginService", "VerifyCredentialsAsync", SiteKind.ReadOrCreate),
        ("RedeemInvitation.razor", "InvitationService", "ValidateAsync", SiteKind.ReadOrCreate),
        ("RedeemInvitation.razor", "InvitationService", "RedeemAsync", SiteKind.ReadOrCreate),
        ("Invitations.razor", "InvitationService", "IssueAsync", SiteKind.ReadOrCreate),
        ("Invitations.razor", "InvitationService", "RevokeAsync", SiteKind.DeAuthorisation),
        ("Invitations.razor", "InvitationService", "ListAsync", SiteKind.ReadOrCreate),
        ("Account.razor", "GitTokenService", "IssueAsync", SiteKind.ReadOrCreate),
        ("Account.razor", "GitTokenService", "RevokeAsync", SiteKind.DeAuthorisation),
        ("Account.razor", "GitTokenService", "ListAsync", SiteKind.ReadOrCreate),
        ("Account.razor", "GitEmailService", "AddAsync", SiteKind.ReadOrCreate),
        ("Account.razor", "GitEmailService", "RemoveAsync", SiteKind.DeAuthorisation),
        ("Account.razor", "GitEmailService", "ListAsync", SiteKind.ReadOrCreate),
    ];

    public static IEnumerable<object[]> DeAuthorisationSites() =>
        KnownSites
            .Where(s => s.Kind == SiteKind.DeAuthorisation)
            .Select(s => new object[] { s.File, s.Service, s.Method });

    public static IEnumerable<object[]> ReadOrCreateSites() =>
        KnownSites
            .Where(s => s.Kind == SiteKind.ReadOrCreate)
            .Select(s => new object[] { s.File, s.Service, s.Method });

    [Fact]
    public void Every_page_contains_exactly_the_fifteen_known_calls_and_no_others()
    {
        var foundKeys = ReadAllServiceCalls()
            .Select(c => (c.File, c.Call.Service, c.Call.Method))
            .OrderBy(k => k)
            .ToList();

        var knownKeys = KnownSites
            .Select(s => (s.File, s.Service, s.Method))
            .OrderBy(k => k)
            .ToList();

        Assert.Equal(knownKeys, foundKeys);
    }

    [Theory]
    [MemberData(nameof(DeAuthorisationSites))]
    public void De_authorisation_calls_pass_CancellationToken_None_explicitly(string file, string service, string method)
    {
        // Would fail if `CancellationToken.None` were changed back to `Context.RequestAborted`,
        // or if the argument were omitted to fall back on the service's own default parameter —
        // both read as something other than the literal text `CancellationToken.None`.
        var call = FindCall(file, service, method);

        Assert.Equal("CancellationToken.None", call.TokenArgument);
    }

    [Theory]
    [MemberData(nameof(ReadOrCreateSites))]
    public void Read_and_create_calls_flow_a_request_scoped_token(string file, string service, string method)
    {
        // Contains rather than an exact match against `Context.RequestAborted` /
        // `context.RequestAborted`, on purpose (N4): RedeemInvitation.razor's two sites read
        // `HttpContext?.RequestAborted ?? default`, a correct §2 site whose null-tolerant fallback
        // is spelled `default` rather than the literal text `CancellationToken.None` its own
        // comment names. An exact-match sweep would misclassify those two as omissions; this one
        // still catches the real regression this exists to guard against — omitting the token
        // entirely, or writing `CancellationToken.None` on a read/create site — because neither
        // contains "RequestAborted".
        var call = FindCall(file, service, method);

        Assert.Contains("RequestAborted", call.TokenArgument, StringComparison.Ordinal);
        Assert.NotEqual("CancellationToken.None", call.TokenArgument);
    }

    private static ServiceCall FindCall(string file, string service, string method)
    {
        var matches = ReadAllServiceCalls()
            .Where(c => c.File == file && c.Call.Service == service && c.Call.Method == method)
            .Select(c => c.Call)
            .ToList();

        return Assert.Single(matches);
    }

    private static IReadOnlyList<(string File, ServiceCall Call)> ReadAllServiceCalls()
    {
        var pagesDirectory = FindPagesDirectory();
        var results = new List<(string, ServiceCall)>();

        foreach (var file in KnownSites.Select(s => s.File).Distinct())
        {
            var source = File.ReadAllText(Path.Combine(pagesDirectory, file));

            foreach (var call in ServiceCallSweep.ExtractServiceCalls(source))
            {
                results.Add((file, call));
            }
        }

        return results;
    }

    /// <summary>
    /// Walks up from the test binary's own directory looking for
    /// <c>src/ZeroWiki/Components/Pages</c> — the six pages this sweep reads directly, rather than
    /// through any build output. There is no fallback: a sweep that silently skipped this on
    /// failure to locate the source would read as coverage while checking nothing.
    /// </summary>
    private static string FindPagesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ZeroWiki", "Components", "Pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate src/ZeroWiki/Components/Pages by walking up from {AppContext.BaseDirectory}.");
    }
}
