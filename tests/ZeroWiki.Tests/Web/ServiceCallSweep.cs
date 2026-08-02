using System.Text.RegularExpressions;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Extracts identity-service call expressions from Razor page source text. Built for §4.5's
/// sweep: §3 replaced an omitted cancellation-token argument with the same value spelled out
/// explicitly, so no runtime behaviour distinguishes pre- and post-§3 code, and this text-level
/// read is the only mechanical evidence the call-site work exists at all.
/// </summary>
/// <remarks>
/// <para>
/// A call is found by its opening <c>Service.Method(</c> alone — a doc-comment reference such as
/// <c>&lt;see cref="GitTokenService.RevokeAsync"/&gt;</c>, or prose naming a method with no open
/// paren directly after it, never matches, because the pattern requires the literal <c>(</c>
/// immediately following the method name.
/// </para>
/// <para>
/// The matching close paren is then found by depth-counting from there, not by assuming the call
/// ends in <c>);</c>. That assumption is false for a real site in this codebase:
/// <c>Bootstrap.razor</c>'s <c>BootstrapService.IsAvailableAsync(Context.RequestAborted)</c> sits
/// inside an <c>if</c> condition, so its own closing paren is immediately followed by the
/// <c>if</c>'s outer closing paren, not a semicolon. A <c>);</c>-anchored pattern would run past
/// this call entirely and swallow the next statement as if it were this call's own arguments.
/// </para>
/// <para>
/// The token argument is the last <em>top-level</em> argument, found the same depth-aware way —
/// not the text after the last comma in the argument list, textually. Splitting on the last
/// comma is wrong the moment a final argument is itself a call containing one, e.g.
/// <c>RevokeAsync(accountId, TokenSource.Combine(x, y))</c>: the last comma in that text sits
/// inside <c>Combine</c>'s own parens, so a textual split would read the token argument as the
/// fragment <c>"y)"</c> rather than the whole nested call. None of the fifteen known call sites
/// hits this today, but the parser must not rely on that — it has already been wrong once in
/// exactly this class of way (the <c>);</c>-anchor above), and this sweep is the only mechanical
/// evidence §3's call-site work exists at all, so a silently wrong extraction here is worse than
/// no sweep.
/// </para>
/// </remarks>
internal static class ServiceCallSweep
{
    private static readonly Regex CallStart = new(
        @"(?<service>GitTokenService|GitEmailService|InvitationService|LoginService|BootstrapService)"
            + @"\.(?<method>[A-Za-z]+)\(",
        RegexOptions.Compiled);

    public static IReadOnlyList<ServiceCall> ExtractServiceCalls(string source)
    {
        var calls = new List<ServiceCall>();

        foreach (Match match in CallStart.Matches(source))
        {
            var argsStart = match.Index + match.Length;
            var argsEnd = FindMatchingCloseParen(source, argsStart);
            var args = source[argsStart..argsEnd];
            var tokenArgument = LastTopLevelArgument(args);

            calls.Add(new ServiceCall(match.Groups["service"].Value, match.Groups["method"].Value, tokenArgument));
        }

        return calls;
    }

    /// <summary>
    /// Finds the index of the <c>)</c> that closes the <c>(</c> already consumed by the caller,
    /// counting depth so a nested call inside the argument list does not fool the scan.
    /// </summary>
    private static int FindMatchingCloseParen(string source, int contentStart)
    {
        var depth = 1;

        for (var i = contentStart; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        throw new FormatException($"Unbalanced parentheses scanning from index {contentStart}.");
    }

    /// <summary>
    /// The text after the last comma that sits at paren depth 0 within a call's own argument-list
    /// text — i.e. the call's last top-level argument, whatever it contains. A comma inside a
    /// nested call in that final argument is at depth 1 or deeper and is not treated as an
    /// argument separator, so a final argument like <c>TokenSource.Combine(x, y)</c> comes back
    /// whole rather than as the fragment after its own last comma.
    /// </summary>
    private static string LastTopLevelArgument(string args)
    {
        var depth = 0;
        var lastTopLevelComma = -1;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    lastTopLevelComma = i;
                    break;
            }
        }

        return args[(lastTopLevelComma + 1)..].Trim();
    }
}
