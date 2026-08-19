namespace ZeroWiki.Content;

/// <summary>
/// The observable outcome of one <see cref="IPageChangeNotifier.NotifyChangedAsync"/> call (§12.1,
/// <c>specs/git-sync/spec.md</c>'s <em>The reaction to a push is observable</em>) — the two counts the
/// spec names separately, plus the one diagnostic that turns "zero matched" into something a reader can
/// act on rather than just observe.
/// </summary>
/// <param name="SubscribersMatched">
/// How many currently-registered subscriptions were under one of the notified routes. This can exceed
/// <paramref name="CallbacksInvoked"/> — a matched subscriber whose callback throws is still counted as
/// matched (D19's own "cannot throw its way out of the whole reaction" already caught and logged it) but
/// not as invoked; the spec names these as two quantities and this type keeps them apart rather than
/// collapsing them into one "handled" count.
/// </param>
/// <param name="CallbacksInvoked">
/// How many matched callbacks ran to completion without throwing.
/// </param>
/// <param name="SubscribedRoutesAtZeroMatch">
/// The routes present in the subscription table at the moment this call matched none of them, or
/// <see langword="null"/> whenever at least one subscriber matched (a healthy delivery never needs this,
/// so it is never computed for one). Distinguishes "the diff named a route nothing is subscribed to" (an
/// empty list here) from "something is subscribed, but not under the route the diff named" (a non-empty
/// list that omits it) — two different defects a bare zero cannot tell apart.
/// </param>
public sealed record PageChangeNotificationResult(
    int SubscribersMatched,
    int CallbacksInvoked,
    IReadOnlyList<EncodedRoute>? SubscribedRoutesAtZeroMatch);
