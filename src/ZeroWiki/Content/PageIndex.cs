namespace ZeroWiki.Content;

/// <summary>
/// The process-memory holder for the wiki's current <see cref="PageIndexSnapshot"/> (D15). Nothing is
/// persisted — this singleton is the only place a snapshot exists between rebuilds, installed once at
/// startup (<see cref="ContentStorageStartupExtensions.BuildPageIndexAsync"/>) and kept fresh on every
/// later read via <see cref="GetCurrentAsync"/> (4.3).
/// </summary>
/// <remarks>
/// <see cref="Current"/>/<see cref="Replace"/> are the low-level, unconditional accessors block 4.1–4.2
/// shipped (startup install, and tests that just need to assert a swap happened). <see cref="GetCurrentAsync"/>
/// is what a request path should call: it compares the installed snapshot's stamp against the
/// repository's live <c>HEAD</c> and refreshes through <see cref="PageIndexBuilder"/> when they differ —
/// the mechanism that makes the index correct for a writer that never notifies the app (an
/// <c>updateInstead</c> push, or an operator committing on the volume) exactly as it is for a browser save.
/// </remarks>
public sealed class PageIndex : IDisposable
{
    private readonly PageIndexBuilder _builder;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private PageIndexSnapshot _current = PageIndexSnapshot.Empty;

    public PageIndex(PageIndexBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>The most recently installed snapshot, without checking whether it is still fresh.</summary>
    public PageIndexSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically installs <paramref name="snapshot"/> as <see cref="Current"/>. A reference swap, not a
    /// mutation — a caller reading <see cref="Current"/> mid-rebuild sees either the old snapshot
    /// wholesale or the new one, never a torn mix of the two.
    /// </summary>
    public void Replace(PageIndexSnapshot snapshot) => Volatile.Write(ref _current, snapshot);

    /// <summary>
    /// Returns a snapshot guaranteed fresh as of the moment this call started — refreshing first if the
    /// installed one's stamp no longer matches the repository's current <c>HEAD</c> (D15's freshness
    /// obligation; the scenario <i>Content changed by an unannounced writer is still reflected</i>).
    /// </summary>
    /// <remarks>
    /// Single-flighted: a stale stamp is cheap to detect (one <c>git rev-parse HEAD</c>, D15) and every
    /// concurrent caller does that for itself, but only one of them performs the actual refresh — the
    /// rest either observe the fast path once someone else has already refreshed, or queue behind
    /// <see cref="_refreshGate"/> and re-check after acquiring it rather than each racing their own
    /// <see cref="PageIndexBuilder.RefreshAsync"/>. <see cref="Replace"/> only ever installs one fully-built
    /// snapshot at a time, so no caller can observe a half-updated index either way.
    /// </remarks>
    public async Task<PageIndexSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var snapshot = Current;
        var headSha = await _builder.ProbeCurrentHeadShaAsync(cancellationToken);
        if (string.Equals(snapshot.CommitSha, headSha, StringComparison.Ordinal))
        {
            return snapshot;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Re-read and re-probe: another caller may already have refreshed while this one was
            // waiting for the gate, or HEAD may have moved again since the check above.
            snapshot = Current;
            headSha = await _builder.ProbeCurrentHeadShaAsync(cancellationToken);
            if (string.Equals(snapshot.CommitSha, headSha, StringComparison.Ordinal))
            {
                return snapshot;
            }

            var refreshed = await _builder.RefreshAsync(snapshot, headSha, cancellationToken);
            Replace(refreshed);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose() => _refreshGate.Dispose();
}
