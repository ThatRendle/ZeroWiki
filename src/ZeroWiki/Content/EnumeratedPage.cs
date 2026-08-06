namespace ZeroWiki.Content;

/// <summary>
/// A Markdown file found under <see cref="ContentPaths.WorkingTree"/> whose route identifies exactly this
/// file (D12): it is the route's sole claimant, <i>and</i> <see cref="PageRouteCodec.TryDecode"/> applied
/// to <see cref="Route"/> reproduces <see cref="RelativePath"/>. Both hold for every instance of this
/// type — <see cref="PageEnumerationService"/> never constructs one otherwise — so a caller (e.g. a future
/// §6 save) can invert <see cref="Route"/> and trust the result names this exact file.
/// <see cref="RelativePath"/> and <see cref="AbsolutePath"/> use the platform directory separator;
/// <see cref="Route"/> uses <c>/</c> and does not include the <c>/wiki/</c> prefix.
/// </summary>
public sealed record EnumeratedPage(EncodedRoute Route, string RelativePath, string AbsolutePath);
