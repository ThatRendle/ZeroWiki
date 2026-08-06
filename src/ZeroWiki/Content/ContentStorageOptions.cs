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
    /// How long the <b>startup accept phase</b>'s acquisition of D16's single cross-process write lock
    /// waits before giving up. Defaults to 10 seconds — comfortably covers ordinary contention (e.g. a
    /// concurrent push of realistic size, or a rolling deploy's brief instance overlap) while still
    /// failing well inside typical operator timeouts. Bound the same way as <see cref="DataRoot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This value's only consumer is <see cref="ContentRepositoryService.EnsureRepositoryAsync"/>'s
    /// accept phase.</b> If it expires there, that is a <b>fatal refusal to start</b>
    /// (<see cref="InvalidOperationException"/>), not a per-request failure. §6.6 settled (D17, Product
    /// Owner decision) that a browser save's own acquisition does <b>not</b> share this value — it uses
    /// <see cref="SaveWriteLockTimeout"/> instead, whose expiry is a per-request "repository busy" result
    /// rather than a fatal startup refusal. The two split because their failure costs are asymmetric: a
    /// stuck save costs nothing recoverable (nothing was written yet, the member just retries), while a
    /// stuck startup blocks the whole process from serving anything — see
    /// <see cref="SaveWriteLockTimeout"/>'s own remarks for the rest of that reasoning, including the
    /// laptop-suspension clock caveat that first surfaced it.
    /// </para>
    /// <para>
    /// <b>Operator consequence — lowering and raising this value trade off in opposite directions.</b>
    /// Lowering it makes a genuinely stuck startup fail faster, but also shortens how long a rolling
    /// deploy's overlapping instances (D3's own justification for this lock, and <c>## NEXT</c>
    /// obligation 16's case) are tolerated before the incoming instance refuses to boot on ordinary,
    /// non-buggy contention. Raising it tolerates a longer overlap (or a longer-held lock from a stuck
    /// first instance) before refusing, at the cost of a slower failure signal when startup really is
    /// stuck.
    /// </para>
    /// <para>
    /// A git push's wait for the same lock — held by the app's own wrapper around the whole
    /// <c>git http-backend</c> invocation (§7.5), not by a hook — is deliberately unbounded and is not
    /// configured here (design.md D16, Product Owner decision).
    /// </para>
    /// </remarks>
    public TimeSpan WriteLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a <b>browser save</b>'s acquisition of D16's single cross-process write lock waits
    /// before giving up (D17, §6.6, Product Owner decision) — deliberately its own, shorter-defaulted
    /// value rather than sharing <see cref="WriteLockTimeout"/>. Defaults to 5 seconds: a browser
    /// request budget, comfortably longer than an ordinary commit-on-save (sub-second for a small text
    /// file) plus realistic contention from a concurrent push, while still giving a member waiting on
    /// the response a clear "repository busy, try again" well inside typical browser/reverse-proxy
    /// request timeouts, rather than a hung tab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this does not share <see cref="WriteLockTimeout"/>:</b> the two expiries have materially
    /// different costs and would otherwise be tuned against each other. A save whose bound elapses fails
    /// cleanly with nothing written — the member just retries. A <em>startup</em> whose bound elapses
    /// refuses to boot outright, which is a far more expensive failure to trade against a save's request
    /// latency. One number cannot be both short enough for a responsive save and long enough to tolerate
    /// a rolling deploy's overlap.
    /// </para>
    /// <para>
    /// <b>The laptop-suspension case that first made the asymmetry concrete (design.md D17):</b>
    /// <see cref="RepositoryWriteLock.AcquireAsync"/> measures elapsed time with <see cref="System.Diagnostics.Stopwatch"/>,
    /// which — like every other .NET clock — counts host suspension. After a host resumes, a
    /// <em>save</em> whose bound elapsed while the lid was shut fails "repository busy" and the member
    /// retries: benign. A <em>startup</em> whose bound elapsed the same way refuses to boot: an outage,
    /// on a machine that did nothing wrong but sleep. This value's separate, shorter default does not
    /// make that scenario less likely to trigger a busy result — it makes triggering one cheap instead
    /// of expensive.
    /// </para>
    /// <para>
    /// On expiry the save fails outright with a distinct "repository busy" result (never D4's stale-
    /// revision conflict) — nothing is written, since the lock is acquired before the write begins.
    /// </para>
    /// </remarks>
    public TimeSpan SaveWriteLockTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
