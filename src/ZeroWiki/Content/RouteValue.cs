namespace ZeroWiki.Content;

/// <summary>
/// A page route value exactly as ASP.NET Core routing has already percent-decoded it once — a
/// <c>/wiki/{*Route}</c> catch-all parameter's bound value.
/// </summary>
/// <remarks>
/// Distinct from <see cref="EncodedRoute"/> for the same reason that type exists: applying
/// <see cref="PageRouteCodec.TryDecode"/> to a value routing has already decoded once decodes it a
/// second time and can resolve the wrong file (D17, <see cref="PageRouteCodec.TryDecodeRouteValue"/>'s
/// remarks carry the empirical evidence). Construct
/// it only at the point a route value is actually read off a request — never by re-encoding or
/// otherwise deriving one from something else.
/// </remarks>
public readonly record struct RouteValue(string Value)
{
    public override string ToString() => Value;
}
