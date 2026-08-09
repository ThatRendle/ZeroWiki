using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Content;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="EditDraftStore"/> directly (D17, §6 block D4 continuation) — the ownership
/// check, the no-consume-on-read guarantee, the TTL, and the eviction bounds, each of which the
/// HTTP-level <c>WikiPageEditorTests</c> would need real elapsed time or hundreds of requests to drive.
/// </summary>
public sealed class EditDraftStoreTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    [Fact]
    public void A_saved_draft_is_returned_to_its_owning_account()
    {
        var store = new EditDraftStore(new SecretTokenGenerator(), new FakeTimeProvider());

        var token = RequireToken(store.Save(Alice, new RouteValue("page"), "My text.", "sha123", "A message."));
        var draft = store.TryGet(Alice, token);

        Assert.NotNull(draft);
        Assert.Equal(new RouteValue("page"), draft.Route);
        Assert.Equal("My text.", draft.Content);
        Assert.Equal("sha123", draft.BaseRevisionToken);
        Assert.Equal("A message.", draft.Message);
    }

    [Fact]
    public void A_token_belonging_to_a_different_account_is_reported_the_same_as_a_missing_one()
    {
        var store = new EditDraftStore(new SecretTokenGenerator(), new FakeTimeProvider());
        var token = RequireToken(store.Save(Alice, new RouteValue("page"), "Alices text.", "sha123", "A message."));

        Assert.Null(store.TryGet(Bob, token));
    }

    [Fact]
    public void An_unknown_token_returns_null()
    {
        var store = new EditDraftStore(new SecretTokenGenerator(), new FakeTimeProvider());

        Assert.Null(store.TryGet(Alice, "this-token-was-never-issued"));
    }

    [Fact]
    public void Reading_a_draft_does_not_consume_it()
    {
        var store = new EditDraftStore(new SecretTokenGenerator(), new FakeTimeProvider());
        var token = RequireToken(store.Save(Alice, new RouteValue("page"), "My text.", "sha123", "A message."));

        var first = store.TryGet(Alice, token);
        var second = store.TryGet(Alice, token);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public void A_draft_is_no_longer_recoverable_once_its_ttl_has_elapsed()
    {
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);
        var token = RequireToken(store.Save(Alice, new RouteValue("page"), "My text.", "sha123", "A message."));

        clock.Advance(EditDraftStore.Ttl + TimeSpan.FromSeconds(1));

        Assert.Null(store.TryGet(Alice, token));
    }

    [Fact]
    public void A_draft_survives_right_up_to_but_not_including_its_ttl_boundary()
    {
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);
        var token = RequireToken(store.Save(Alice, new RouteValue("page"), "My text.", "sha123", "A message."));

        clock.Advance(EditDraftStore.Ttl - TimeSpan.FromSeconds(1));

        Assert.NotNull(store.TryGet(Alice, token));
    }

    [Fact]
    public void When_a_single_accounts_saves_exceed_its_own_cap_its_own_oldest_entry_is_evicted()
    {
        // Renamed and re-scoped from a pre-existing test that looped MaxEntries times against one
        // account — that loop could never observe the per-account cap firing first, which is exactly
        // the blind spot that let a real cross-account eviction vulnerability through review (D17, §6
        // block D4 continuation round two). This version drives the constant actually responsible,
        // MaxDraftsPerAccount, not MaxEntries.
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);

        var oldestToken = RequireToken(store.Save(Alice, new RouteValue("page-0"), "oldest", "sha", "m"));
        for (var i = 1; i < EditDraftStore.MaxDraftsPerAccount; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Save(Alice, new RouteValue($"page-{i}"), $"text-{i}", "sha", "m");
        }

        // Alice is now exactly at her own cap, and "oldest" is her own soonest to expire. One more of
        // Alice's own saves must evict it rather than grow her own footprint past the cap.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var newestToken = RequireToken(store.Save(Alice, new RouteValue("page-newest"), "newest", "sha", "m"));

        Assert.Null(store.TryGet(Alice, oldestToken));
        Assert.NotNull(store.TryGet(Alice, newestToken));
    }

    [Fact]
    public void Two_drafts_of_the_same_route_from_two_saves_stay_distinct_by_token()
    {
        // The two-tab conflict scenario the token exists for: two failed saves of the same page must
        // not clobber each other the way a slot keyed only by (account, route) would.
        var store = new EditDraftStore(new SecretTokenGenerator(), new FakeTimeProvider());

        var tokenA = store.Save(Alice, new RouteValue("page"), "Tab A's text.", "sha-a", "message-a");
        var tokenB = store.Save(Alice, new RouteValue("page"), "Tab B's text.", "sha-b", "message-b");

        Assert.NotEqual(tokenA, tokenB);

        var draftA = store.TryGet(Alice, tokenA!);
        var draftB = store.TryGet(Alice, tokenB!);

        Assert.Equal("Tab A's text.", draftA!.Content);
        Assert.Equal("Tab B's text.", draftB!.Content);
    }

    [Fact]
    public void A_flooding_account_can_never_evict_a_different_accounts_draft_the_reviewers_attacker_sequence()
    {
        // The exact attacker sequence the reviewer built against the pre-fix class and confirmed by
        // execution (D17, §6 block D4 continuation round two): a victim saves one draft; an attacker
        // then floods the store with roughly MaxEntries of their own failing saves. Before the fix,
        // every entry shared one Ttl, so "soonest to expire" meant "oldest, store-wide" — never the
        // attacker's own fresh entries for as long as the attacker kept attacking — and the overflow
        // deterministically evicted the victim's draft while every one of the attacker's own survived.
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);

        var victimToken = RequireToken(store.Save(Bob, new RouteValue("victim-page"), "Victims work.", "sha", "m"));
        Assert.NotNull(store.TryGet(Bob, victimToken)); // recoverable before the flood

        string? attackerToken = null;
        for (var i = 0; i < EditDraftStore.MaxEntries; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            attackerToken = store.Save(Alice, new RouteValue($"attacker-page-{i}"), $"attacker-{i}", "sha", "m");
        }

        Assert.NotNull(store.TryGet(Bob, victimToken)); // survives the flood -- the fix under test
        Assert.NotNull(store.TryGet(Alice, attackerToken!)); // the attacker's own newest still works too
    }

    [Fact]
    public void No_amount_of_one_accounts_activity_can_touch_another_accounts_earlier_draft()
    {
        // The invariant itself, stated directly and driven well past either cap from a single account
        // — not merely the reviewer's exact replay of the original bug.
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);

        var bobToken = RequireToken(store.Save(Bob, new RouteValue("bobs-page"), "Bobs work.", "sha", "m"));

        for (var i = 0; i < EditDraftStore.MaxEntries * 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Save(Alice, new RouteValue($"alice-page-{i}"), $"alice-{i}", "sha", "m");
        }

        Assert.NotNull(store.TryGet(Bob, bobToken));
    }

    [Fact]
    public void When_many_distinct_accounts_fill_the_global_backstop_a_new_draft_is_refused_not_stolen()
    {
        // The global MaxEntries bound reached honestly -- by MaxEntries distinct accounts each holding
        // one live draft, never by one account's own per-account cap -- so this exercises the backstop
        // in isolation. The correct outcome is refusal, not evicting one of those MaxEntries accounts'
        // own drafts to make room for account MaxEntries+1.
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);

        var firstAccount = Guid.NewGuid();
        var firstToken = RequireToken(store.Save(firstAccount, new RouteValue("page-0"), "text-0", "sha", "m"));

        for (var i = 1; i < EditDraftStore.MaxEntries; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Save(Guid.NewGuid(), new RouteValue($"page-{i}"), $"text-{i}", "sha", "m");
        }

        var refused = store.Save(Guid.NewGuid(), new RouteValue("one-too-many"), "text", "sha", "m");

        Assert.Null(refused);
        Assert.NotNull(store.TryGet(firstAccount, firstToken)); // nobody's draft was sacrificed for it
    }

    [Fact]
    public void The_global_backstop_reclaims_expired_entries_before_refusing()
    {
        var clock = new FakeTimeProvider();
        var store = new EditDraftStore(new SecretTokenGenerator(), clock);

        for (var i = 0; i < EditDraftStore.MaxEntries; i++)
        {
            store.Save(Guid.NewGuid(), new RouteValue($"page-{i}"), $"text-{i}", "sha", "m");
        }

        clock.Advance(EditDraftStore.Ttl + TimeSpan.FromSeconds(1)); // everything above is now expired

        var token = store.Save(Guid.NewGuid(), new RouteValue("fresh"), "fresh text", "sha", "m");

        Assert.NotNull(token);
    }

    private static string RequireToken(string? token)
    {
        Assert.NotNull(token);
        return token;
    }
}
