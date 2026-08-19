namespace ZeroWiki.Content;

/// <summary>
/// A route that fails D12's per-route invariant — "the route identifies exactly this file" — either
/// because two or more files claim it (e.g. <c>a_ b.md</c> and <c>a _b.md</c>, which both encode to
/// <c>a___b</c>), or because its sole claimant's own route does not decode back to that claimant's path
/// (e.g. <c>Chapter  1.md</c>, two spaces, whose route <c>Chapter__1</c> decodes to <c>Chapter_1.md</c>).
/// Both are the same failure from a save's perspective: the route cannot be trusted to name one specific
/// file. The route serves no page; <see cref="RelativePaths"/> names every file implicated — the two
/// claimants in the first case, the single file in the second — sorted for a deterministic error message.
/// Every other route is unaffected.
/// </summary>
public sealed record AmbiguousPageRoute(EncodedRoute Route, IReadOnlyList<string> RelativePaths);
