using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// The process-memory holder for the wiki's current <see cref="PageIndexSnapshot"/> (D15). Nothing is
/// persisted — this singleton is the only place a snapshot exists between rebuilds, installed once at
/// startup (<see cref="ContentStorageStartupExtensions.BuildPageIndexAsync"/>) and kept fresh on every
/// later read via <see cref="GetCurrentAsync"/> (4.3), with <see cref="Invalidate"/> giving §6's save
/// path a way to disown a rollback's dirty bytes before its next read (D17).
/// </summary>
/// <remarks>
/// <para>
/// <b>Generation and snapshot are held as one immutable state, installed by compare-and-swap (D17).</b>
/// <c>Replace</c> used to be an unconditional <c>Volatile.Write</c> — a last-writer-wins swap. That is
/// unsafe once two different writers can both produce a snapshot to install: a reader already inside
/// <see cref="IPageIndexBuilder.RefreshAsync"/>, having already read a save's dirty bytes off disk, can
/// install its stale result <em>after</em> a rollback's <see cref="Invalidate"/> has disowned it — its
/// <c>CommitSha</c> still equals live <c>HEAD</c>, because a rollback never advances <c>HEAD</c>, so
/// every later freshness check reports fresh and the poisoned entry is never revisited. Holding
/// <c>(Generation, Snapshot)</c> as one immutable state object and installing by
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> — comparing the whole state object's
/// identity, not a captured generation number compared separately from the write — closes that: the
/// compare and the install are one atomic operation, so an invalidation landing between "capture the
/// state a refresh started from" and "install what it built" is never silently lost to a check-then-act
/// window. <b>The invariant this preserves is "never serve a discarded snapshot", not "always
/// succeed"</b> — <see cref="GetCurrentAsync"/> discards and retries, bounded, rather than trading the
/// bound away for a nicer success rate.
/// </para>
/// <para>
/// <b>Testability seam (reviewer adjudication, §6 block C2 remediation):</b> this type depends on
/// <see cref="IPageIndexBuilder"/>, not the concrete <see cref="PageIndexBuilder"/>, precisely so a test
/// can substitute a fake whose <see cref="IPageIndexBuilder.RefreshAsync"/> calls <see cref="Invalidate"/>
/// mid-flight — driving the exact race this class exists to survive deterministically, through the real
/// public <see cref="GetCurrentAsync"/> path, rather than by winning a timing race or by testing the CAS
/// primitive in isolation (which proves the primitive correct but nothing about whether
/// <see cref="GetCurrentAsync"/> actually calls it in the right place, at the right time, with the right
/// arguments). An earlier revision of this type exposed <c>internal</c> test-only members
/// (<c>TryInstallRefreshedSnapshot</c>, <c>CurrentStateForTesting</c>) behind
/// <c>InternalsVisibleTo</c> instead; removed because this seam buys strictly more test coverage for
/// comparable cost, with no visibility grant and no test-only production member.
/// </para>
/// </remarks>
public sealed class PageIndex : IPageIndex, IDisposable
{
    /// <summary>
    /// Bounds how many times <see cref="GetCurrentAsync"/> retries a refresh whose install was discarded
    /// by a concurrent <see cref="Invalidate"/> before it gives up and serves the empty snapshot. Small,
    /// deliberately: a rollback (the only caller of <see cref="Invalidate"/>, D17) is an exceptional
    /// path, so more than a couple of installs racing one in a row already means invalidations are
    /// arriving faster than a refresh can complete, not ordinary contention.
    /// </summary>
    private const int MaxDiscardedRefreshAttempts = 3;

    private readonly IPageIndexBuilder _builder;
    private readonly ILogger<PageIndex> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private PageIndexState _state = new(Generation: 0, PageIndexSnapshot.Empty);

    public PageIndex(IPageIndexBuilder builder, ILogger<PageIndex> logger)
    {
        _builder = builder;
        _logger = logger;
    }

    /// <summary>The most recently installed snapshot, without checking whether it is still fresh.</summary>
    public PageIndexSnapshot Current => CurrentState.Snapshot;

    /// <summary>
    /// The one unconditional install this type exposes to a caller — used exactly once, at startup
    /// (<see cref="ContentStorageStartupExtensions.BuildPageIndexAsync"/>), before anything could have
    /// called <see cref="GetCurrentAsync"/> or <see cref="Invalidate"/> (<c>Program.cs</c>'s ordering
    /// makes this unraceable, D16). Named for that one caller rather than left as a general-purpose
    /// setter: a future <em>refresher</em> or <em>reader</em> reaching for this instead of
    /// <see cref="GetCurrentAsync"/>'s own CAS would bypass the safety property this type exists to
    /// provide as silently as skipping a gate would (D17, round three).
    /// </summary>
    public void InstallStartupSnapshot(PageIndexSnapshot snapshot) =>
        Volatile.Write(ref _state, new PageIndexState(Generation: 0, snapshot));

    /// <summary>
    /// D17: disowns whatever the currently installed snapshot claims, without needing to know whether
    /// anything was ever wrong with it — the save path's rollback calls this because the rollback path
    /// is the one content-changing event that does not advance <c>HEAD</c>, so the stamp mechanism alone
    /// cannot detect that the index may be holding an abandoned save's metadata (D15's freshness
    /// invariant: every content-changing event advances <c>HEAD</c>; a rollback is the exception).
    /// Installs a new state with the generation bumped and the snapshot <see cref="PageIndexSnapshot.Empty"/>,
    /// so the next <see cref="GetCurrentAsync"/> call takes D15's no-previous-stamp branch and rebuilds
    /// in full rather than trusting an incremental diff that could never have named the abandoned path.
    /// </summary>
    /// <remarks>
    /// Deliberately an unconditional <see cref="Volatile.Write{T}(ref T, T)"/>, not a CAS loop. This is
    /// not an exception to the CAS rule above — that rule constrains what a <em>refresh install</em> may
    /// reach for; the safety property it protects is that no snapshot computed <em>before</em> an
    /// invalidation can be installed <em>after</em> one, and an unconditional bump always wins over an
    /// in-flight refresh's own CAS attempt (whose captured state object is, by construction, no longer
    /// the current one once this write lands) — so it always must win, and a CAS loop here would only
    /// make this method retry until it won, which the unconditional write already achieves in one step.
    /// The deeper reason: <see cref="PageIndexSnapshot.Empty"/> makes no claim about any page, so
    /// installing it can never be wrong, only wasteful — unlike a losing refresh install (which costs a
    /// rebuild), a losing invalidation would cost correctness, and that asymmetry is what makes the
    /// unconditional write the safe side to be on. Concurrent calls to this method may race each other
    /// harmlessly: whichever write lands last wins, and both compute the same <see cref="PageIndexSnapshot.Empty"/>
    /// snapshot, so nothing is lost by not serializing them.
    /// </remarks>
    public void Invalidate()
    {
        var current = CurrentState;
        Volatile.Write(ref _state, new PageIndexState(current.Generation + 1, PageIndexSnapshot.Empty));
    }

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
    /// <see cref="IPageIndexBuilder.RefreshAsync"/>. Single-flighting bounds concurrent refreshes to one
    /// at a time; it does not by itself bound the race against a concurrent <see cref="Invalidate"/>,
    /// which never takes this gate (D17) — that race is what the CAS below, not the gate, closes.
    /// <para>
    /// The state is captured (<c>state = CurrentState</c>) <em>before</em> <see cref="IPageIndexBuilder.RefreshAsync"/>
    /// begins its disk reads, and the install below only succeeds if the state is still that same object.
    /// If it is not — a concurrent <see cref="Invalidate"/> landed in between — the refreshed snapshot is
    /// discarded (it was computed over bytes an invalidation has since disowned) and this method retries
    /// from a fresh read, bounded by <see cref="MaxDiscardedRefreshAttempts"/>. On exhausting the bound it
    /// serves <see cref="PageIndexSnapshot.Empty"/> and logs: at that point invalidations are arriving
    /// faster than a refresh can install, which means repeated commit failures, and reporting the page as
    /// missing is the honest degradation where serving metadata already known to be contaminated is not.
    /// </para>
    /// </remarks>
    public async Task<PageIndexSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var state = CurrentState;
        var headSha = await _builder.ProbeCurrentHeadShaAsync(cancellationToken);
        if (string.Equals(state.Snapshot.CommitSha, headSha, StringComparison.Ordinal))
        {
            return state.Snapshot;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                // Re-read and re-probe on every attempt: another caller may already have refreshed
                // while this one was waiting for the gate (or between retries), or HEAD may have moved
                // again since the check above.
                state = CurrentState;
                headSha = await _builder.ProbeCurrentHeadShaAsync(cancellationToken);
                if (string.Equals(state.Snapshot.CommitSha, headSha, StringComparison.Ordinal))
                {
                    return state.Snapshot;
                }

                var refreshed = await _builder.RefreshAsync(state.Snapshot, headSha, cancellationToken);

                // The one CAS install site: succeeds only if _state is still exactly the `state` object
                // captured above, before the refresh's disk reads began.
                var installed = Interlocked.CompareExchange(
                    ref _state,
                    new PageIndexState(state.Generation + 1, refreshed),
                    state);

                if (installed == state)
                {
                    return refreshed;
                }

                if (attempt >= MaxDiscardedRefreshAttempts)
                {
                    _logger.LogWarning(
                        "The page index discarded a refreshed snapshot {Attempts} times in a row " +
                        "(each raced a concurrent rollback invalidation) and is serving the empty " +
                        "index rather than a snapshot already known to be contaminated.",
                        attempt);
                    return PageIndexSnapshot.Empty;
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose() => _refreshGate.Dispose();

    private PageIndexState CurrentState => Volatile.Read(ref _state);

    /// <summary>
    /// The generation and snapshot D17 requires be held and installed as one unit. A <c>record</c> for
    /// its free deconstruction/<c>ToString</c>; <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
    /// compares this type by reference identity regardless of its record-synthesized value equality — it
    /// never calls <c>Equals</c>/<c>==</c>, so two value-equal-but-distinct instances are never confused
    /// with one another in <see cref="GetCurrentAsync"/>'s CAS.
    /// </summary>
    private sealed record PageIndexState(int Generation, PageIndexSnapshot Snapshot);
}
