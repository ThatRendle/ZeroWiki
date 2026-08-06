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
/// </remarks>
public readonly record struct EncodedRoute
{
    /// <summary>The still percent-encoded route text.</summary>
    public string Value { get; }

    internal EncodedRoute(string value) => Value = value;

    public override string ToString() => Value;
}
