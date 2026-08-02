namespace ZeroWiki.Tests.Web;

/// <summary>
/// Proves <see cref="ServiceCallSweep"/> reads what it claims to read, against known-present and
/// known-absent markup, before <see cref="RequestCancellationSweepTests"/> trusts it against the
/// real pages — CLAUDE.md's standing rule, in a codebase that has already once shipped two agents
/// corroborating each other on a regex that matched neither <c>href=""</c> case it was meant to
/// tell apart.
/// </summary>
public sealed class ServiceCallSweepInstrumentTests
{
    [Fact]
    public void An_explicit_CancellationToken_None_argument_is_read_verbatim()
    {
        const string Source =
            "_revoked = await GitTokenService.RevokeAsync(CallerAccountId, RevokeInput.TokenId, CancellationToken.None);";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("GitTokenService", call.Service);
        Assert.Equal("RevokeAsync", call.Method);
        Assert.Equal("CancellationToken.None", call.TokenArgument);
    }

    [Fact]
    public void A_RequestAborted_argument_is_read_verbatim()
    {
        const string Source =
            "var issued = await GitTokenService.IssueAsync(CallerAccountId, Context.RequestAborted);";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("Context.RequestAborted", call.TokenArgument);
    }

    [Fact]
    public void A_call_inside_an_if_condition_without_its_own_trailing_semicolon_is_still_read_correctly()
    {
        // Bootstrap.razor's actual shape. The call's own closing paren is immediately followed by
        // the if-statement's outer closing paren, not a semicolon — a `);`-anchored sweep would
        // run past this call and swallow the next statement (the redirect) as if it belonged to
        // this call's own argument list.
        const string Source = """
            if (!await BootstrapService.IsAvailableAsync(Context.RequestAborted))
            {
                Navigation.NavigateTo("/", replace: true);
            }
            """;

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("BootstrapService", call.Service);
        Assert.Equal("IsAvailableAsync", call.Method);
        Assert.Equal("Context.RequestAborted", call.TokenArgument);
    }

    [Fact]
    public void An_argument_list_split_across_lines_is_still_read_as_one_call()
    {
        const string Source = """
            var outcome = await BootstrapService.CreateFirstAdministratorAsync(
                Input.Username, Input.Password, Context.RequestAborted);
            """;

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("BootstrapService", call.Service);
        Assert.Equal("CreateFirstAdministratorAsync", call.Method);
        Assert.Equal("Context.RequestAborted", call.TokenArgument);
    }

    [Fact]
    public void The_null_tolerant_fallback_spelling_is_read_as_containing_RequestAborted_not_as_None()
    {
        // RedeemInvitation.razor's own spelling (N4): the fallback reads `default`, not the
        // literal text `CancellationToken.None` its own comment names. A sweep that only searched
        // for that literal string would misclassify this correct §2 site as an omission. This
        // instrument reads the argument expression verbatim instead of pattern-matching one
        // specific spelling of "not cancelled".
        const string Source =
            "_outcome = await InvitationService.ValidateAsync(Token, HttpContext?.RequestAborted ?? default);";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("HttpContext?.RequestAborted ?? default", call.TokenArgument);
        Assert.Contains("RequestAborted", call.TokenArgument, StringComparison.Ordinal);
        Assert.NotEqual("CancellationToken.None", call.TokenArgument);
    }

    [Fact]
    public void An_omitted_token_argument_is_visible_as_absent_rather_than_silently_passed()
    {
        // The regression this whole change fixed: a call that compiles and behaves correctly only
        // by accident of the service's own `= default` parameter, indistinguishable at a glance
        // from an oversight. The instrument must not read a token out of thin air here.
        const string Source =
            "_revoked = await GitTokenService.RevokeAsync(CallerAccountId, RevokeInput.TokenId);";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("RevokeInput.TokenId", call.TokenArgument);
        Assert.NotEqual("CancellationToken.None", call.TokenArgument);
        Assert.DoesNotContain("RequestAborted", call.TokenArgument, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doc_comment_reference_to_the_method_does_not_match()
    {
        const string Source =
            """/// See <see cref="GitTokenService.RevokeAsync"/> and GitTokenService.RevokeAsync's remarks (D1).""";

        Assert.Empty(ServiceCallSweep.ExtractServiceCalls(Source));
    }

    [Fact]
    public void Nested_parentheses_within_the_argument_list_do_not_confuse_the_matching_close_paren()
    {
        // No real call site nests a parenthesised expression in its own argument list, but the
        // depth-counting scan must not rely on that — a false negative here would be silent.
        const string Source =
            "await InvitationService.IssueAsync(Uri.EscapeDataString(x), CancellationToken.None);";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("CancellationToken.None", call.TokenArgument);
    }

    [Fact]
    public void A_final_argument_that_is_itself_a_call_containing_a_comma_is_extracted_whole()
    {
        // Block B remediation, the reviewer's exact case: a final argument that is itself a call
        // with its own comma. Splitting the argument list on the textually last comma — rather
        // than the last comma at paren depth 0 — would read this as "y)", because that comma sits
        // inside Combine's own parens, not at the call's own top level.
        const string Source = "await GitTokenService.RevokeAsync(accountId, TokenSource.Combine(x, y));";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.Equal("TokenSource.Combine(x, y)", call.TokenArgument);
    }

    [Fact]
    public void A_final_argument_that_is_itself_a_call_is_not_truncated_to_a_fragment_of_itself()
    {
        // Known-absent counterpart to the case above, pinning the same shape in the other
        // direction: the extracted token argument must not be the fragment a textual
        // last-comma split would have produced, at either of the two ways it could be wrong.
        const string Source = "await GitTokenService.RevokeAsync(accountId, TokenSource.Combine(x, y));";

        var call = Assert.Single(ServiceCallSweep.ExtractServiceCalls(Source));

        Assert.NotEqual("y)", call.TokenArgument);
        Assert.NotEqual("y", call.TokenArgument);
    }

    [Fact]
    public void Two_calls_in_the_same_source_are_both_read_and_kept_distinct()
    {
        const string Source = """
            var a = await GitTokenService.ListAsync(CallerAccountId, Context.RequestAborted);
            var b = await BootstrapService.IsAvailableAsync(context.RequestAborted);
            """;

        var calls = ServiceCallSweep.ExtractServiceCalls(Source);

        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c is { Service: "GitTokenService", Method: "ListAsync" });
        Assert.Contains(calls, c => c is { Service: "BootstrapService", Method: "IsAvailableAsync" });
    }
}
