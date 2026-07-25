using System.Net;
using System.Text.RegularExpressions;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Posts a Static SSR form the way a browser does: fetch the page, carry back the hidden fields
/// Blazor emits (<c>_handler</c> and the antiforgery token), and submit under the field names
/// actually rendered.
/// </summary>
/// <remarks>
/// Taking the field names from the rendered markup rather than restating them is the point — a
/// form whose rendered names have drifted from its binder prefix fails here, which is exactly the
/// failure no unit test can see.
/// </remarks>
public static partial class StaticSsrForm
{
    /// <summary>Reads the hidden inputs a rendered form carries.</summary>
    public static async Task<Dictionary<string, string>> GetHiddenFieldsAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);

        return HiddenInput().Matches(html).ToDictionary(
            match => match.Groups["name"].Value,
            match => WebUtility.HtmlDecode(match.Groups["value"].Value),
            StringComparer.Ordinal);
    }

    /// <summary>Names of the inputs the form renders, hidden and visible alike.</summary>
    public static async Task<IReadOnlyCollection<string>> GetFieldNamesAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);

        return AnyInputName().Matches(html)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        IEnumerable<KeyValuePair<string, string>> fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(fields));

    [GeneratedRegex("""<input\s+type="hidden"\s+name="(?<name>[^"]+)"\s+value="(?<value>[^"]*)"\s*/?>""")]
    private static partial Regex HiddenInput();

    // The closing quote is implied by the negated character class, and leaving it off keeps the
    // raw string literal from ending in a quote.
    [GeneratedRegex("""<input\b[^>]*\bname="(?<name>[^"]+)""")]
    private static partial Regex AnyInputName();
}
