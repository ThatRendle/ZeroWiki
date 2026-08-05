namespace ZeroWiki.Content;

/// <summary>
/// The mounted data volume root (D8): production is a single Docker volume at <c>/data</c>,
/// holding <c>identity.db</c> beside the <c>wiki</c> content git repository. Bound from the
/// <c>ContentStorage</c> configuration section, so it can be overridden by
/// <c>appsettings.*.json</c> or, as the container does, by the <c>ContentStorage__DataRoot</c>
/// environment variable.
/// </summary>
public sealed class ContentStorageOptions
{
    public const string SectionName = "ContentStorage";

    /// <summary>The data volume root. Defaults to <c>/data</c>, the container mount point.</summary>
    public string DataRoot { get; set; } = "/data";

    /// <summary>
    /// How long an acquisition of D16's single cross-process write lock waits before giving up. Defaults
    /// to 10 seconds — comfortably covers ordinary contention (e.g. a concurrent push of realistic size)
    /// while still failing well inside typical request/operator timeouts. Bound the same way as
    /// <see cref="DataRoot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Today's only consumer is a startup acquisition, not a save's.</b>
    /// <see cref="ContentRepositoryService.EnsureRepositoryAsync"/>'s accept phase acquires the lock
    /// bounded by this value; if it expires there, that is a <b>fatal refusal to start</b>
    /// (<see cref="InvalidOperationException"/>), not a per-request failure — there is no browser save
    /// path yet to fail cleanly instead (§6, not yet built; see <c>## NEXT</c> for the forward
    /// obligation §6.6 owes here). Earlier revisions of this doc described a save's acquisition; no such
    /// acquisition exists in code, so that description was false against this type's only caller.
    /// </para>
    /// <para>
    /// <b>Operator consequence — lowering and raising this value trade off in opposite directions on the
    /// startup path that exists today.</b> Lowering it makes a genuinely stuck startup fail faster, but
    /// also shortens how long a rolling deploy's overlapping instances (D3's own justification for this
    /// lock, and <c>## NEXT</c> obligation 16's case) are tolerated before the incoming instance refuses
    /// to boot on ordinary, non-buggy contention. Raising it tolerates a longer overlap (or a longer-held
    /// lock from a stuck first instance) before refusing, at the cost of a slower failure signal when
    /// startup really is stuck. Whether this same value should also gate a browser save's acquisition
    /// (and, if so, whether the two should split into separately configurable values given they trade off
    /// in opposite directions) is undecided — that decision belongs to §6.6, not to this doc comment.
    /// </para>
    /// <para>
    /// A git push's wait for the same lock — held by the app's own wrapper around the whole
    /// <c>git http-backend</c> invocation (§7.5), not by a hook — is deliberately unbounded and is not
    /// configured here (design.md D16, Product Owner decision).
    /// </para>
    /// </remarks>
    public TimeSpan WriteLockTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
