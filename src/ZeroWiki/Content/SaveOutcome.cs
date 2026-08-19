namespace ZeroWiki.Content;

/// <summary>The outcome of a <see cref="PageSaveService.SaveAsync"/> call (D17, §6 block C2).</summary>
public enum SaveOutcome
{
    /// <summary>
    /// The save was written and committed, or — for content byte-identical to what was already
    /// committed — recorded as successful with no commit created.
    /// <see cref="SavePageResult.CommitSha"/> distinguishes the two.
    /// </summary>
    Saved,

    /// <summary>
    /// D4: the declared <see cref="PageBaseRevision"/> no longer matches the file's revision at
    /// <c>HEAD</c>, re-read under the write lock. Nothing was written.
    /// </summary>
    Conflict,

    /// <summary>
    /// D16/D17: the save's bounded wait for the write lock elapsed before it was acquired. Nothing was
    /// written.
    /// </summary>
    RepositoryBusy,

    /// <summary>
    /// The route does not identify a single, safe file this class will write to — an unresolvable or
    /// non-canonical route value (S2), or a path this class refuses to write through even though it
    /// resolved cleanly (a symlink sitting at the resolved path — D17 — or a base-revision probe git
    /// itself cannot answer with a blob/absent state, e.g. a tree or gitlink occupying that path).
    /// Nothing was written.
    /// </summary>
    Refused,

    /// <summary>
    /// The write reached the working tree and was staged, but the commit itself failed (or something
    /// past the write threw rather than merely exiting non-zero). The working tree was successfully
    /// restored to its previously committed state (or, for a page that did not exist at the declared
    /// base, deleted) and the page index invalidated before this result was returned — D17's
    /// transactional-save guarantee. Distinct from <see cref="Refused"/>: content was momentarily
    /// written to disk here, unlike any of the cases above.
    /// </summary>
    Failed,

    /// <summary>
    /// The write reached the working tree and was staged, the commit (or an earlier step) failed, AND
    /// the rollback meant to restore the working tree to its previously committed state also failed.
    /// The page index was still invalidated — that does not depend on the restore succeeding (D17) —
    /// but the working tree itself may now be dirty outside a lock-held save, which is otherwise made
    /// unreachable by D9/D16. Recoverable only by D9's startup reconciliation on the next restart, or by
    /// an operator's own intervention; distinct from <see cref="Failed"/> because that recoverability
    /// gap is exactly what a caller needs to know to report this differently than an ordinary failed
    /// save.
    /// </summary>
    RollbackFailed,
}
