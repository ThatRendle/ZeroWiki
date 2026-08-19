using System.Text.Json;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Verifies the two properties the cascade through <c>EnumeratedPage</c>, <c>PageIndexEntry</c>,
/// <c>AmbiguousPageRoute</c>, <c>PageEnumerationService</c> and <c>PageIndexBuilder</c> depends on
/// (D17, §6 block C1 delta) rather than assuming them: that <see cref="EncodedRoute"/>'s synthesized
/// equality is ordinal — so it is a drop-in replacement for the <c>StringComparer.Ordinal</c> those
/// classes used before this type existed — and that it behaves correctly as a <c>Dictionary</c>/
/// <c>HashSet</c> key, which several of them use it as.
/// </summary>
public sealed class EncodedRouteTests
{
    [Fact]
    public void Equality_IsValueBased_NotReferenceBased()
    {
        var a = PageRouteCodec.Encode("page.md");
        var b = PageRouteCodec.Encode("page.md");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("Page", "page")]
    [InlineData("PAGE", "page")]
    public void Equality_IsOrdinal_NotCaseInsensitive(string first, string second)
    {
        // The property PageEnumerationService's claimsByRoute dictionary and PageIndexBuilder's
        // affectedRoutes/affectedRoutesSet both depend on: default equality must behave exactly like the
        // StringComparer.Ordinal those collections were keyed on before EncodedRoute existed, not like a
        // case- or culture-insensitive comparison string.Equals can also mean elsewhere in .NET.
        var a = PageRouteCodec.Encode($"{first}.md");
        var b = PageRouteCodec.Encode($"{second}.md");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void UsableAsADictionaryKey_WithNoExplicitComparer()
    {
        var route = PageRouteCodec.Encode("page.md");
        var lookup = new Dictionary<EncodedRoute, int> { [route] = 1 };

        Assert.True(lookup.TryGetValue(PageRouteCodec.Encode("page.md"), out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void UsableAsAHashSetMember_WithNoExplicitComparer()
    {
        var routes = new HashSet<EncodedRoute> { PageRouteCodec.Encode("page.md") };

        Assert.Contains(PageRouteCodec.Encode("page.md"), routes);
    }

    [Fact]
    public void ToString_ReturnsTheRawValue()
    {
        var route = PageRouteCodec.Encode("Project Notes.md");

        Assert.Equal("Project_Notes", route.ToString());
        Assert.Equal(route.Value, route.ToString());
    }

    [Fact]
    public void JsonDeserialization_CannotProduceAPopulatedInstance()
    {
        // §12 (12.3, second pass): a JSON converter was tried and reverted -- System.Text.Json
        // resolves an attribute-declared converter by reflection regardless of the calling assembly, so
        // an `internal` converter would have let any external caller construct a populated
        // EncodedRoute, defeating D17's compile-time single-producer invariant. This test project holds
        // no InternalsVisibleTo grant to ZeroWiki (see the note below), so this exercises exactly the
        // same view of the type an external caller has: default reflection-based deserialization
        // default-constructs the struct (always possible) and has no public constructor or setter to
        // bind Value to, so it silently stays null rather than throwing. A regression that reintroduces
        // a JSON converter would make this assertion fail.
        var json = JsonSerializer.Serialize(new { Value = "page" });

        var result = JsonSerializer.Deserialize<EncodedRoute>(json);

        Assert.Null(result.Value);
    }

    // ConstructibleWithinThisAssembly_ForFixturesEncodeCanNeverProduce was deleted here (reviewer
    // recommendation, §6 block C1 delta, taken before this block commits) along with the
    // InternalsVisibleTo grant it depended on and documented: EncodedRoute's constructor is internal, and
    // ZeroWiki.Tests no longer holds assembly-level access to it. Every state that grant let a hostile
    // fixture reach was already independently exercised through the public RouteValue/
    // TryDecodeRouteValue path (see PageRouteCodecTests.cs's own note at the deletion site), so paying a
    // permanent, assembly-wide grant to keep this one redundant construction was the wrong trade.
}
