namespace ZeroWiki.Content;

/// <summary>
/// The subset of <see cref="PageIndex"/> that <see cref="PageSaveService"/> depends on — extracted so a
/// test can substitute a fake <see cref="Invalidate"/> that observes the working tree's actual on-disk
/// state at the exact moment it is called, proving D17's restore-then-invalidate ordering through the
/// real public <see cref="PageSaveService.SaveAsync"/> path rather than by reasoning about a
/// <c>try</c>/<c>finally</c> shape or granting test code access to either type's internals (§6 block C2
/// remediation, mirroring <see cref="IPageIndexBuilder"/>'s own reason for existing).
/// </summary>
public interface IPageIndex
{
    /// <inheritdoc cref="PageIndex.Current"/>
    PageIndexSnapshot Current { get; }

    /// <inheritdoc cref="PageIndex.Invalidate"/>
    void Invalidate();
}
