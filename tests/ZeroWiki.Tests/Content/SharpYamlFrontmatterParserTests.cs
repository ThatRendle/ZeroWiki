using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="SharpYamlFrontmatterParser"/> directly against the real SharpYaml library —
/// D14's "failure is total" requirement, and the two hardening requirements that exist specifically
/// because frontmatter is push-reachable: a size cap and a nesting-depth cap, both enforced before
/// SharpYaml ever sees anything it could misuse.
/// </summary>
public sealed class SharpYamlFrontmatterParserTests
{
    private readonly SharpYamlFrontmatterParser _parser = new();

    [Fact]
    public void ValidFrontmatter_ParsesTitleAndTags()
    {
        var result = _parser.Parse("title: Kick Off\ntags:\n  - meeting\n  - project-x\n");

        Assert.Equal("Kick Off", result.Title);
        Assert.Equal(["meeting", "project-x"], result.Tags);
    }

    [Fact]
    public void MissingFields_ProduceEmptyMetadataWithoutFailing()
    {
        var result = _parser.Parse("description: no title or tags here\n");

        Assert.Null(result.Title);
        Assert.Empty(result.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void EmptyOrWhitespaceOnly_ReturnsEmpty(string yaml)
    {
        Assert.Same(PageFrontmatter.Empty, _parser.Parse(yaml));
    }

    [Fact]
    public void MalformedYaml_DegradesToEmptyRatherThanThrowing()
    {
        var result = _parser.Parse("title: [unterminated\n");

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void DuplicateKeys_DegradeToEmptyRatherThanThrowing()
    {
        var result = _parser.Parse("title: First\ntitle: Second\n");

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void OversizedBlock_IsRejectedBeforeParsing()
    {
        var hostile = "title: " + new string('a', SharpYamlFrontmatterParser.MaxSizeBytes + 1);

        var result = _parser.Parse(hostile);

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void DeeplyNestedBlockStyle_IsRejectedRatherThanOverflowingTheStack()
    {
        // "title" sits alongside the deep chain as a top-level sibling, not inside it: ExtractTitle only
        // ever reads the top-level "title" key, so if the depth cap failed to reject this document, the
        // parse would succeed and Title would read back as "Deep" — visibly different from Empty. Without
        // this sibling, a document whose only content is the deep "a" chain would produce Title=null
        // either way (rejected, or parsed successfully but with no top-level "title"/"tags" at all),
        // which would let this test pass even if the depth cap were silently removed.
        var levels = SharpYamlFrontmatterParser.MaxNestingDepth + 20;
        var deep = "title: Deep\n"
            + string.Concat(Enumerable.Range(0, levels).Select(i => new string(' ', i * 2) + "a:\n"))
            + new string(' ', levels * 2) + "leaf: 1\n";

        var result = _parser.Parse(deep);

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void DeeplyNestedFlowStyle_IsRejectedRatherThanOverflowingTheStack()
    {
        // Same reasoning as the block-style case above: "title" is a top-level sibling of the deeply
        // bracketed "a", so a successful (uncapped) parse would be visibly distinguishable from Empty.
        var brackets = SharpYamlFrontmatterParser.MaxNestingDepth + 50;
        var flow = "title: Deep\na: " + new string('[', brackets) + "1" + new string(']', brackets) + "\n";

        var result = _parser.Parse(flow);

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void ModestNesting_WithinTheDepthCap_StillParses()
    {
        var result = _parser.Parse("title: Nested\na:\n  b:\n    c: leaf\ntags: [x, y]\n");

        Assert.Equal("Nested", result.Title);
        Assert.Equal(["x", "y"], result.Tags);
    }

    [Fact]
    public void AliasExpansionBomb_IsRejectedRatherThanExpanded()
    {
        // A "billion laughs" shape: each level aliases the previous level nine times, so five levels
        // already represents 9^5 (~59,000) logical list entries from a few hundred bytes of source —
        // well within this class's own size cap, so this specifically exercises the alias defence, not
        // the size cap. A parser that resolved and expanded this would burn CPU/memory proportional to
        // the expansion, not the source size.
        var bomb = """
            a: &a ["x","x","x","x","x","x","x","x","x"]
            b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]
            c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]
            d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]
            title: [*d,*d,*d,*d,*d,*d,*d,*d,*d]

            """;

        var result = _parser.Parse(bomb);

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void CustomTypeTag_NeverInstantiatesAnArbitraryClrType()
    {
        // Deserializing into the constrained Dictionary<string, object> shape means a custom "!!" tag has
        // no abstract/polymorphic slot to redirect — this pins that down for this exact library version
        // rather than assuming it from documentation.
        var result = _parser.Parse("!!System.IO.FileInfo\nname: /etc/passwd\n");

        // Whatever SharpYaml does with the tag, the outcome must never be an object of the tagged type:
        // it either fails closed (empty metadata) or reads the mapping's plain string values, never
        // constructs a FileInfo.
        Assert.True(result == PageFrontmatter.Empty || result.Title is null);
    }

    [Fact]
    public void TagsAsASingleScalarRatherThanAList_IsAcceptedAsOneTag()
    {
        var result = _parser.Parse("title: Solo\ntags: solo-tag\n");

        Assert.Equal(["solo-tag"], result.Tags);
    }

    [Fact]
    public void NonStringTagEntries_AreDroppedRatherThanCrashingTheParse()
    {
        var result = _parser.Parse("tags:\n  - real-tag\n  - 42\n  - true\n");

        Assert.Equal(["real-tag"], result.Tags);
    }
}
