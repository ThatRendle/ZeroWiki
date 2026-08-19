namespace ZeroWiki.Content;

/// <summary>
/// The subset of <see cref="PageIndexBuilder"/> that <see cref="PageIndex"/> depends on — extracted so a
/// test can substitute a fake whose <see cref="RefreshAsync"/> drives <see cref="PageIndex.Invalidate"/>
/// mid-flight, exercising the CAS race <see cref="PageIndex.GetCurrentAsync"/> exists to survive
/// deterministically and through the real public API, rather than by winning a timing race or by
/// granting test code access to <see cref="PageIndex"/>'s own internals (§6 block C2 remediation,
/// reviewer adjudication). <see cref="PageIndexBuilder.BuildAsync"/> is deliberately not part of this
/// interface: <see cref="ContentStorageStartupExtensions"/> is its only caller and can keep resolving the
/// concrete type.
/// </summary>
public interface IPageIndexBuilder
{
    /// <inheritdoc cref="PageIndexBuilder.ProbeCurrentHeadShaAsync"/>
    Task<string?> ProbeCurrentHeadShaAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="PageIndexBuilder.RefreshAsync"/>
    Task<PageIndexSnapshot> RefreshAsync(PageIndexSnapshot current, string? currentHeadSha, CancellationToken cancellationToken);
}
