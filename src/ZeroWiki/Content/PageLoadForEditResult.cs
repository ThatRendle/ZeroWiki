namespace ZeroWiki.Content;

/// <summary>The result of a <see cref="PageSaveService.LoadForEditAsync"/> call.</summary>
public sealed record PageLoadForEditResult(PageLoadForEditOutcome Outcome, string? Content, PageBaseRevision BaseRevision)
{
    /// <summary>
    /// The address does not identify exactly one file. <see cref="Content"/> and <see cref="BaseRevision"/>
    /// carry no meaning here — no editor should open for this address at all.
    /// </summary>
    public static readonly PageLoadForEditResult Refused = new(PageLoadForEditOutcome.Refused, null, default);

    /// <summary>
    /// The address identifies no existing file yet: an empty editor declaring
    /// <see cref="PageBaseRevision.AbsentAtHead"/>, so saving it creates the page.
    /// </summary>
    public static readonly PageLoadForEditResult New = new(PageLoadForEditOutcome.New, string.Empty, PageBaseRevision.AbsentAtHead);

    /// <param name="content">The page's current Markdown, read from the working tree.</param>
    /// <param name="baseRevision">
    /// The blob <paramref name="content"/> corresponds to — the same <see cref="PageBaseRevision"/> a
    /// subsequent <see cref="PageSaveService.SaveAsync"/> call must declare back, for its own CAS to
    /// compare against what it reads at that later time.
    /// </param>
    public static PageLoadForEditResult ExistingPage(string content, PageBaseRevision baseRevision) =>
        new(PageLoadForEditOutcome.Found, content, baseRevision);
}
