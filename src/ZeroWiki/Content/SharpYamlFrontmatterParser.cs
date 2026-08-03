using System.Text;
using SharpYaml;

namespace ZeroWiki.Content;

/// <summary>
/// Parses frontmatter YAML via SharpYaml (D14). The only type in this project permitted to reference a
/// SharpYaml type — everything else reaches this behind <see cref="IFrontmatterParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded before SharpYaml ever runs</b> — <see cref="MaxSizeBytes"/> is checked directly against the
/// raw text, and <see cref="MaxNestingDepth"/> is passed to SharpYaml's own
/// <see cref="YamlSerializerOptions.MaxDepth"/> rather than left at its library default. Both are set
/// here explicitly (D14: "cap... at the seam rather than trusting the library's defaults"), even though
/// SharpYaml 3.13.0 already defends itself reasonably — <c>MaxDepth</c> left unset behaves as an internal
/// default of 64, confirmed empirically (a 2,000-level block-style document and a 5,000-bracket flow-style
/// document both throw a catchable <see cref="YamlException"/> reporting "maximum nesting depth... has
/// been exceeded", never a <see cref="StackOverflowException"/>). This class does not rely on that
/// default holding across a future SharpYaml version; <see cref="MaxNestingDepth"/> is ours.
/// </para>
/// <para>
/// <b>The alias-expansion-bomb hazard closes as a consequence of the constrained shape, not a separate
/// check</b> — confirmed empirically, not assumed: deserializing into <c>Dictionary&lt;string, object&gt;</c>
/// makes SharpYaml itself throw a catchable <see cref="YamlException"/> ("Aliases are not supported when
/// deserializing into object unless ReferenceHandling is Preserve") the moment it meets a <c>&amp;anchor</c>
/// or <c>*alias</c> — including a classic "billion laughs" document that defines nine-fold nested list
/// aliasing five levels deep, which failed immediately rather than expanding. <see cref="YamlSerializerOptions.ReferenceHandling"/>
/// is still pinned to <see cref="YamlReferenceHandling.None"/> explicitly below, so a future default change
/// cannot silently reopen this by switching to <see cref="YamlReferenceHandling.Preserve"/> behind this
/// class's back.
/// </para>
/// <para>
/// <b>No type-resolving deserialization</b> — <see cref="YamlSerializerOptions.UnsafeAllowDeserializeFromTagTypeName"/>
/// stays at its safe default (<see langword="false"/>) and is pinned explicitly for the same reason as
/// above, so a <c>!!</c> tag can never select an arbitrary CLR type. Deserializing into the concrete,
/// non-polymorphic <c>Dictionary&lt;string, object&gt;</c> — rather than a type SharpYaml would need to
/// resolve polymorphically — is itself part of that defence: there is no abstract or open-ended target
/// type for a hostile tag to redirect.
/// </para>
/// </remarks>
public sealed class SharpYamlFrontmatterParser : IFrontmatterParser
{
    /// <summary>
    /// Frontmatter block size cap in UTF-8 bytes, checked before SharpYaml ever sees the text. Generous
    /// for any legitimate wiki page's title/tags/aliases while bounding worst-case parse cost regardless
    /// of nesting or alias handling.
    /// </summary>
    public const int MaxSizeBytes = 8 * 1024;

    /// <summary>
    /// Maximum YAML nesting depth (block or flow style), enforced by SharpYaml via
    /// <see cref="YamlSerializerOptions.MaxDepth"/>. Generous for any legitimate frontmatter shape; see
    /// the class remarks for why this is set explicitly rather than left at SharpYaml's own default.
    /// </summary>
    public const int MaxNestingDepth = 8;

    private static readonly YamlSerializerOptions Options = new()
    {
        MaxDepth = MaxNestingDepth,
        ReferenceHandling = YamlReferenceHandling.None,
        UnsafeAllowDeserializeFromTagTypeName = false,
    };

    /// <summary>
    /// Explicit interface implementation so <see cref="MaxSizeBytes"/> stays available as the class's own
    /// <see langword="const"/> (used directly below) while also being reachable, unqualified, through
    /// <see cref="IFrontmatterParser"/> for a caller — D15's index builder — that only knows the interface.
    /// </summary>
    int IFrontmatterParser.MaxSizeBytes => MaxSizeBytes;

    public PageFrontmatter Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return PageFrontmatter.Empty;
        }

        if (Encoding.UTF8.GetByteCount(yaml) > MaxSizeBytes)
        {
            return PageFrontmatter.Empty;
        }

        Dictionary<string, object>? raw;
        try
        {
            raw = YamlSerializer.Deserialize<Dictionary<string, object>>(yaml, Options);
        }
        catch (Exception)
        {
            // D14: failure is total. SharpYaml's own faults all derive from YamlException, but a boundary
            // whose entire contract is "never propagate a fault from untrusted, push-reachable input"
            // catches broadly and deliberately rather than trusting an exhaustive list of exception types.
            return PageFrontmatter.Empty;
        }

        if (raw is null)
        {
            return PageFrontmatter.Empty;
        }

        return new PageFrontmatter
        {
            Title = ExtractTitle(raw),
            Tags = ExtractTags(raw),
        };
    }

    private static string? ExtractTitle(Dictionary<string, object> raw) =>
        raw.TryGetValue("title", out var value) && value is string title && !string.IsNullOrWhiteSpace(title)
            ? title
            : null;

    private static IReadOnlyList<string> ExtractTags(Dictionary<string, object> raw)
    {
        if (!raw.TryGetValue("tags", out var value))
        {
            return [];
        }

        return value switch
        {
            List<object> list => list.OfType<string>().Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray(),
            string single when !string.IsNullOrWhiteSpace(single) => [single],
            _ => [],
        };
    }
}
