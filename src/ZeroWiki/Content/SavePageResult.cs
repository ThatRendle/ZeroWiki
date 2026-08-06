namespace ZeroWiki.Content;

/// <summary>The result of a <see cref="PageSaveService.SaveAsync"/> call.</summary>
public sealed record SavePageResult(SaveOutcome Outcome, string? CommitSha)
{
    public static readonly SavePageResult Conflict = new(SaveOutcome.Conflict, null);
    public static readonly SavePageResult RepositoryBusy = new(SaveOutcome.RepositoryBusy, null);
    public static readonly SavePageResult Refused = new(SaveOutcome.Refused, null);
    public static readonly SavePageResult Failed = new(SaveOutcome.Failed, null);
    public static readonly SavePageResult RollbackFailed = new(SaveOutcome.RollbackFailed, null);

    /// <param name="commitSha">
    /// The new commit's sha, or <see langword="null"/> when the save's content was byte-identical to
    /// what was already committed and no commit was created — still success (D17).
    /// </param>
    public static SavePageResult Saved(string? commitSha) => new(SaveOutcome.Saved, commitSha);
}
