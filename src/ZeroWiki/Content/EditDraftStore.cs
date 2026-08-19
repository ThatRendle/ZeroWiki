using ZeroWiki.Security;

namespace ZeroWiki.Content;

/// <summary>
/// A short-TTL, in-memory, per-account draft of a page edit that a failed save could not commit — the
/// carrier that lets the browser editing surface's failure path be Post/Redirect/Get (D17, §6 block D4
/// continuation, Product Owner decision) without the POST response itself becoming the page a member
/// might reload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a token, not a slot keyed by (account, route).</b> The scenario this exists for — a
/// stale-base conflict — <i>is</i> a two-tab scenario: a member with the same page open in two tabs
/// would clobber one tab's draft with the other's under a slot keyed only by account and route. The
/// unguessable token this class hands back from <see cref="Save"/> keeps concurrent drafts of the same
/// page distinct.
/// </para>
/// <para>
/// <b>Why in-memory and not SQLite.</b> Wider scope than §6 has taken — this class's whole reason to
/// exist is a redirect that lands within the same process a moment later, not durability across a
/// restart. The Product Owner accepted, at decision time, that a draft does not survive a restart or a
/// deploy, and that this is per-instance state (a concern for a future multi-instance deployment, not
/// this one).
/// </para>
/// <para>
/// <b>Ownership, not mere possession, gates a read.</b> A token travels in a redirect URL, which can
/// leak through server logs, a `Referer` header, or someone reading over a shoulder. <see cref="TryGet"/>
/// therefore takes the caller's own account id and refuses a token that resolves to a draft belonging to
/// a different account — reported identically to a missing or expired token (never "found, but not
/// yours"), the same uniform-failure posture <see cref="Identity.LoginService"/> already uses for
/// "unknown username" versus "wrong password": telling the two apart would hand an attacker a free
/// existence oracle for no benefit to a legitimate caller.
/// </para>
/// <para>
/// <b>The bound's actual guarantee — corrected after an executed, not reasoned-about, finding
/// (D17, §6 block D4 continuation round two).</b> An earlier version of this class capped the store at
/// <see cref="MaxEntries"/> globally and, once full, evicted whichever entry — across every account —
/// was soonest to expire. Since every entry shares one <see cref="Ttl"/>, "soonest to expire" is simply
/// "oldest, store-wide," which is never an attacker's own freshly-created entries for as long as the
/// attacker keeps attacking: a reviewer built the exact attacker sequence against this class (roughly
/// <see cref="MaxEntries"/> of one account's own failing saves) and confirmed, by running it, that it
/// deterministically evicted a victim account's older draft while every one of the attacker's own drafts
/// survived. The doc comment this replaces claimed the opposite — that a looping member "degrades their
/// own oldest drafts" — which is exactly the reassuring reading you get from tracing the loop's intent
/// rather than running it against an adversarial sequence. <b>The actual, now-verified invariant this
/// class enforces is: any eviction caused by account A's own activity falls only on account A's own
/// drafts.</b> <see cref="Save"/> enforces a <see cref="MaxDraftsPerAccount"/> cap per account — at that
/// cap, adding one more evicts that same account's own oldest entry, so an account can never grow past a
/// small, fixed footprint no matter how many failing saves it produces. <see cref="MaxEntries"/> remains
/// only as a whole-store backstop against many *distinct* accounts being active at once; reaching it
/// reclaims already-expired entries first (harmless to any account, since they are already gone by
/// definition) and, if nothing is reclaimable, <see cref="Save"/> returns <see langword="null"/> rather
/// than evicting a live entry belonging to an account other than the caller. A caller that gets
/// <see langword="null"/> back has a failed save whose text could not be preserved as a draft — worse for
/// that one member than losing their own oldest tab's draft, but never worse for anyone else, and never
/// silently indistinguishable from success (<c>PageEditor.razor</c> reports it as its own distinct
/// outcome rather than treating a refusal as an ordinary recoverable draft).
/// </para>
/// </remarks>
public sealed class EditDraftStore
{
    /// <summary>
    /// How long a draft survives before <see cref="TryGet"/> can no longer recover it. Minutes, not
    /// hours: a draft exists to survive one redirect and whatever the member does immediately
    /// afterward, not a lunch break — the Product Owner's own framing of the decision. Public so a test
    /// can advance a <see cref="TimeProvider"/> by exactly this much rather than duplicating the value.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bounds how many live drafts a single account may hold at once — the mechanism that makes the
    /// class's central invariant true (see this class's own remarks): once an account reaches this many,
    /// <see cref="Save"/> evicts that same account's own oldest entry before adding a new one, so an
    /// account's footprint in the store can never grow past this regardless of how many failing saves it
    /// produces, and it can therefore never need to reach into another account's entries to make room for
    /// its own. Set to 8 — a member legitimately editing in a handful of tabs at once, not hundreds; wide
    /// enough that no plausible legitimate workflow hits it, narrow enough that even a fully compromised
    /// account can never hold more than a small, fixed number of stale drafts. Public for the same reason
    /// as <see cref="Ttl"/> and <see cref="MaxEntries"/>.
    /// </summary>
    public const int MaxDraftsPerAccount = 8;

    /// <summary>
    /// Caps the whole store at roughly this many pages' worth of text, across every account — a backstop
    /// against many distinct accounts being active at once, not a per-account bound (see
    /// <see cref="MaxDraftsPerAccount"/> for that). Reaching it reclaims already-expired entries first;
    /// if none are reclaimable, <see cref="Save"/> refuses rather than evicting a live entry belonging to
    /// an account other than the caller — see this class's own remarks for why an earlier version of this
    /// bound did not have that property. Public for the same reason as <see cref="Ttl"/>: a test
    /// exercising the cap should drive the real value, not a second copy of it.
    /// </summary>
    public const int MaxEntries = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, DraftEntry> _entries = new(StringComparer.Ordinal);
    private readonly ISecretTokenGenerator _tokenGenerator;
    private readonly TimeProvider _timeProvider;

    public EditDraftStore(ISecretTokenGenerator tokenGenerator, TimeProvider timeProvider)
    {
        _tokenGenerator = tokenGenerator;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Stashes a failed save's submitted content, its declared base revision, and the message to show
    /// for it, keyed by a freshly generated token. <paramref name="route"/> must be the same,
    /// already-canonicalized form the follow-up GET's own route parameter will carry — comparable with
    /// ordinal equality by <see cref="TryGet"/>, not re-derived there.
    /// </summary>
    /// <returns>
    /// The token to carry in the redirect, or <see langword="null"/> if the store is at
    /// <see cref="MaxEntries"/> with nothing expired to reclaim and <paramref name="accountId"/> is not
    /// itself the reason (its own per-account cap already keeps its own contribution small) — see this
    /// class's own remarks. A caller that gets <see langword="null"/> must not treat it as success; it
    /// means this one save's text could not be preserved as a recoverable draft.
    /// </returns>
    public string? Save(Guid accountId, RouteValue route, string content, string baseRevisionToken, string message)
    {
        var token = _tokenGenerator.Generate().Plaintext;
        var entry = new DraftEntry(accountId, route, content, baseRevisionToken, message, _timeProvider.GetUtcNow() + Ttl);

        lock (_gate)
        {
            // Reclaiming expired entries first is never a cross-account cost -- they are already gone
            // by definition, for whichever account they belonged to.
            EvictExpiredLocked();

            // The per-account cap: if accountId is already at its own limit, it pays for its own new
            // draft out of its own oldest one. This runs unconditionally, not only when the store is
            // globally full, so an account's own footprint is bounded at all times, not merely when
            // capacity pressure happens to make it visible.
            EvictAccountOldestIfAtCapLocked(accountId);

            if (_entries.Count >= MaxEntries)
            {
                // Every entry still here belongs to some account's own within-cap, unexpired draft --
                // accountId's own contribution is already bounded by the eviction above, so if the
                // store is still full, the only entries left to remove belong to other accounts.
                // Refuse rather than pay another account's cost for this one's activity.
                return null;
            }

            _entries[token] = entry;
        }

        return token;
    }

    /// <summary>
    /// Looks up <paramref name="token"/>'s draft for <paramref name="accountId"/> — deliberately not
    /// removing it (a reload of the failure page must keep showing the member's text; consuming on read
    /// would make a reload silently lose it, the same defect one level down from the one this class
    /// exists to fix). Returns <see langword="null"/> uniformly for a token that does not exist, has
    /// expired, or belongs to a different account — see this class's own remarks for why those three are
    /// not distinguished.
    /// </summary>
    public EditDraft? TryGet(Guid accountId, string token)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _entries.Remove(token);
                return null;
            }

            if (entry.AccountId != accountId)
            {
                return null;
            }

            return new EditDraft(entry.Route, entry.Content, entry.BaseRevisionToken, entry.Message);
        }
    }

    private void EvictExpiredLocked()
    {
        var now = _timeProvider.GetUtcNow();
        List<string>? expired = null;

        foreach (var (token, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                (expired ??= []).Add(token);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var token in expired)
        {
            _entries.Remove(token);
        }
    }

    /// <summary>
    /// If <paramref name="accountId"/> already holds <see cref="MaxDraftsPerAccount"/> or more entries,
    /// evicts that same account's own soonest-to-expire one (equivalently, since every entry shares one
    /// <see cref="Ttl"/>, its own oldest one) to make room. Scoped to <paramref name="accountId"/>'s own
    /// entries only, by construction — the fix for the vulnerability this class's remarks describe:
    /// the account paying for its own new draft is the same account whose activity created the pressure,
    /// never a different one.
    /// </summary>
    private void EvictAccountOldestIfAtCapLocked(Guid accountId)
    {
        var count = 0;
        string? oldestToken = null;
        var oldestExpiry = DateTimeOffset.MaxValue;

        foreach (var (token, entry) in _entries)
        {
            if (entry.AccountId != accountId)
            {
                continue;
            }

            count++;

            if (entry.ExpiresAt < oldestExpiry)
            {
                oldestExpiry = entry.ExpiresAt;
                oldestToken = token;
            }
        }

        if (count >= MaxDraftsPerAccount && oldestToken is not null)
        {
            _entries.Remove(oldestToken);
        }
    }

    private sealed record DraftEntry(
        Guid AccountId,
        RouteValue Route,
        string Content,
        string BaseRevisionToken,
        string Message,
        DateTimeOffset ExpiresAt);
}

/// <summary>A draft <see cref="EditDraftStore.TryGet"/> recovered for its owning account.</summary>
public sealed record EditDraft(RouteValue Route, string Content, string BaseRevisionToken, string Message);
