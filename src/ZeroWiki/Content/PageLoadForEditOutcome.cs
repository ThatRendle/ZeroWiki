namespace ZeroWiki.Content;

/// <summary>The outcome of a <see cref="PageSaveService.LoadForEditAsync"/> call (D17, §6 block D3).</summary>
public enum PageLoadForEditOutcome
{
    /// <summary>
    /// The address identifies exactly one existing file; its current Markdown and the
    /// <see cref="PageBaseRevision"/> that content corresponds to were read successfully.
    /// </summary>
    Found,

    /// <summary>
    /// The address identifies no existing file — the same "no page here yet" case
    /// <see cref="PageSaveService.SaveAsync"/> accepts via <see cref="PageBaseRevision.AbsentAtHead"/>, so
    /// the caller can open an empty editor and create the page by saving it (D17: "the edit surface
    /// creates as well as edits").
    /// </summary>
    New,

    /// <summary>
    /// The address does not identify exactly one file — unresolvable, non-canonical, ambiguous between two
    /// or more files, or occupying a path a base-revision probe cannot answer with a blob-or-absent state
    /// (a tree or a gitlink) — and is refused before an editor ever opens on it. Distinct from
    /// <see cref="New"/>: the <i>Editing surface refuses an address that identifies no single file</i>
    /// scenario (<c>specs/content-editing/spec.md</c>) is explicit that this is a different condition from
    /// a page that simply does not exist yet, and only <see cref="New"/> is that second condition.
    /// </summary>
    Refused,
}
