namespace ZeroWiki.Content;

/// <summary>
/// A page route in its canonical, still percent-encoded form (D12) — what
/// <see cref="PageRouteCodec.Encode"/> produces and what <see cref="EnumeratedPage.Route"/>,
/// <see cref="PageIndexEntry.Route"/> and <see cref="AmbiguousPageRoute.Route"/> all store.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="RouteValue"/> so a value ASP.NET Core routing has already percent-decoded
/// once cannot be passed where this contract is required. Before this type existed the split was
/// enforced only by naming and documentation, and the block-3b reviewer established that a wrong
/// pairing cannot be detected at runtime: an encoded route and a decoded route value containing no
/// <c>%</c> are the same string. In §6 that mix-up stops being a wrong <em>read</em> and becomes a
/// wrong-file <em>write</em> (D17) — this type makes the mix-up a compile error instead.
/// </para>
/// <para>
/// <b>Constructible with a chosen value only from within this assembly</b> (D17, third and fourth
/// passes) — not, precisely, "no caller can obtain an instance": every construction of
/// <see cref="EnumeratedPage"/>, <see cref="PageIndexEntry"/> and <see cref="AmbiguousPageRoute"/> traces
/// back to a fresh <see cref="PageRouteCodec.Encode"/> call — nothing reads a route back from storage,
/// since <c>PageIndex</c> lives in process memory only and is never deserialised — so
/// <see cref="PageRouteCodec.Encode"/> is the one legitimate producer, and the constructor is
/// <see langword="internal"/> rather than <see langword="public"/> so the compiler enforces that single
/// origin instead of a reviewer having to re-enumerate every call site by hand each time this class is
/// touched. The one honest limit: C# gives every struct a public <c>default</c> regardless of an
/// <see langword="internal"/> constructor, so <c>default(EncodedRoute)</c> is available to any caller and
/// carries a <see langword="null"/> <see cref="Value"/> — harmless here because
/// <see cref="PageRouteCodec.TryDecodeCore(string, bool, out string)"/>'s empty/null check refuses it
/// before anything else looks at it, the same refusal that has guarded this path since §3.
/// </para>
/// <para>
/// §12 (<c>12.3</c>, second pass): this type deliberately carries <b>no</b> <c>System.Text.Json</c>
/// converter and no <c>[JsonConverter]</c> attribute. A first attempt added one, reasoning that an
/// <see langword="internal"/> converter living in this assembly could only ever be reached from inside
/// it — that reasoning was wrong. <c>System.Text.Json</c> resolves an attribute-declared converter by
/// reflection at the point it needs one, regardless of which assembly's code called
/// <c>JsonSerializer.Deserialize</c>; a standalone console app referencing only the built
/// <c>ZeroWiki.dll</c>, with no <c>InternalsVisibleTo</c> grant, could call
/// <c>JsonSerializer.Deserialize&lt;EncodedRoute&gt;(json)</c> and get back a fully populated instance
/// for any string. That is exactly the construction surface D17's <see langword="internal"/>
/// constructor exists to deny external callers — the converter would have handed it back through a
/// side door the compiler cannot see. <b>The guarantee this type actually offers, stated precisely:</b>
/// no code outside this assembly can construct a populated <see cref="EncodedRoute"/>, compiler-checked;
/// code inside this assembly can, because <see langword="internal"/> has always permitted that and a
/// reviewer can enumerate every such call site by hand, the same way D17 always intended. The one
/// production boundary this type needs to cross — an <c>InteractiveServer</c> component parameter over
/// the Static SSR→circuit boundary — is handled by <c>ChangedOnDiskIndicator</c> taking the route as a
/// plain <see cref="string"/> parameter and reconstructing the <see cref="EncodedRoute"/> itself, from
/// inside this assembly, rather than by making this type itself serializable.
/// </para>
/// </remarks>
public readonly record struct EncodedRoute
{
    /// <summary>The still percent-encoded route text.</summary>
    public string Value { get; }

    internal EncodedRoute(string value) => Value = value;

    public override string ToString() => Value;
}
