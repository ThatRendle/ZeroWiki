namespace ZeroWiki.Components.Pages;

/// <summary>
/// The browser editing surface's form (D17, §6 block D4): the page's Markdown together with the base
/// revision the edit started from, so a save can declare both back in one explicit post.
/// </summary>
/// <remarks>
/// No <see cref="System.ComponentModel.DataAnnotations"/> here, unlike <see cref="LoginInput"/> and
/// <see cref="BootstrapInput"/> — nothing about a page's Markdown is invalid on shape alone; an empty
/// file is a legitimate save. What actually matters here — whether the address still identifies
/// exactly one file, whether the base revision is still current — is
/// <see cref="ZeroWiki.Content.PageSaveService.SaveAsync"/>'s own job under the write lock, not a
/// client-visible shape check this model could express.
/// </remarks>
public sealed class PageEditorInput
{
    /// <summary>The page's Markdown, exactly as the member typed it.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The declared base revision's blob sha, or the empty string sentinel for
    /// <see cref="ZeroWiki.Content.PageBaseRevision.AbsentAtHead"/>. A real blob sha is never empty
    /// (<see cref="ZeroWiki.Content.PageBaseRevision.ForBlob"/> rejects that), so the empty string is
    /// an unambiguous sentinel. Carried as a hidden field rather than re-derived at submit time,
    /// because the CAS this protects (D4) must compare against exactly what
    /// <see cref="ZeroWiki.Content.PageSaveService.LoadForEditAsync"/> declared when the editor opened,
    /// not a value recomputed when the save lands.
    /// </summary>
    public string BaseRevisionToken { get; set; } = string.Empty;
}
