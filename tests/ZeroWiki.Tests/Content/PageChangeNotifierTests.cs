using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// D19 §3, §8 block B: the pub/sub layer itself, isolated from the git plumbing that drives it —
/// <see cref="PushReactionServiceTests"/> proves the routes it is called with are the right ones; this
/// file proves the notifier does the right thing with whatever routes it is given.
/// </summary>
public sealed class PageChangeNotifierTests
{
    private readonly PageChangeNotifier _notifier = new(NullLogger<PageChangeNotifier>.Instance);

    [Fact]
    public async Task NotifyChangedAsync_InvokesOnlySubscribersOfTheNotifiedRoutes()
    {
        var routeA = PageRouteCodec.Encode("a.md");
        var routeB = PageRouteCodec.Encode("b.md");
        var aInvoked = false;
        var bInvoked = false;

        using var subscriptionA = _notifier.Subscribe(routeA, () => { aInvoked = true; return Task.CompletedTask; });
        using var subscriptionB = _notifier.Subscribe(routeB, () => { bInvoked = true; return Task.CompletedTask; });

        await _notifier.NotifyChangedAsync([routeA], CancellationToken.None);

        Assert.True(aInvoked, "the subscriber for the notified route should have been invoked.");
        Assert.False(bInvoked, "a viewer on a route the push never touched must never be invoked at all.");
    }

    /// <summary>
    /// §12.1's falsifier: a run that delivers and a run that matches nobody must not produce the same
    /// observable outcome. This is the structured assertion the spec asks for -- on the returned counts
    /// themselves, not on whether a log line happens to exist.
    /// </summary>
    [Fact]
    public async Task NotifyChangedAsync_DeliveredRun_AndZeroMatchRun_AreDistinguishableByTheReturnedCounts()
    {
        var subscribedRoute = PageRouteCodec.Encode("subscribed.md");
        var unsubscribedRoute = PageRouteCodec.Encode("nobody-subscribes-to-this.md");
        using var subscription = _notifier.Subscribe(subscribedRoute, () => Task.CompletedTask);

        var delivered = await _notifier.NotifyChangedAsync([subscribedRoute], CancellationToken.None);
        var zeroMatch = await _notifier.NotifyChangedAsync([unsubscribedRoute], CancellationToken.None);

        Assert.Equal(1, delivered.SubscribersMatched);
        Assert.Equal(1, delivered.CallbacksInvoked);
        Assert.Null(delivered.SubscribedRoutesAtZeroMatch);

        Assert.Equal(0, zeroMatch.SubscribersMatched);
        Assert.Equal(0, zeroMatch.CallbacksInvoked);
        Assert.NotEqual(delivered, zeroMatch);
    }

    [Fact]
    public async Task NotifyChangedAsync_RouteWithNoSubscribers_IsANoOp()
    {
        var route = PageRouteCodec.Encode("nobody-home.md");

        // No Subscribe call at all -- must not throw and must not need a subscriber to exist.
        var result = await _notifier.NotifyChangedAsync([route], CancellationToken.None);

        Assert.Equal(0, result.SubscribersMatched);
        Assert.Equal(0, result.CallbacksInvoked);
    }

    /// <summary>
    /// The diagnostic (§12.1's architect addition): "diffed X, subscribed []" -- no circuit ever
    /// subscribed to anything -- is a different defect from "diffed X, subscribed [Y]" below, and a
    /// bare zero cannot tell them apart.
    /// </summary>
    [Fact]
    public async Task NotifyChangedAsync_RouteWithNoSubscribers_SubscribedRoutesAtZeroMatchIsEmpty()
    {
        var route = PageRouteCodec.Encode("nobody-home.md");

        var result = await _notifier.NotifyChangedAsync([route], CancellationToken.None);

        Assert.NotNull(result.SubscribedRoutesAtZeroMatch);
        Assert.Empty(result.SubscribedRoutesAtZeroMatch);
    }

    /// <summary>
    /// The other half of the diagnostic: something is subscribed, just not under the route the diff
    /// named -- the subscription table is not empty, so the record must say what it actually held.
    /// </summary>
    [Fact]
    public async Task NotifyChangedAsync_SubscriberOnADifferentRoute_SubscribedRoutesAtZeroMatchNamesIt()
    {
        var subscribedRoute = PageRouteCodec.Encode("subscribed.md");
        var diffedRoute = PageRouteCodec.Encode("diffed-but-nobody-subscribes-to-this.md");
        using var subscription = _notifier.Subscribe(subscribedRoute, () => Task.CompletedTask);

        var result = await _notifier.NotifyChangedAsync([diffedRoute], CancellationToken.None);

        Assert.Equal(0, result.SubscribersMatched);
        var namedRoute = Assert.Single(result.SubscribedRoutesAtZeroMatch!);
        Assert.Equal(subscribedRoute, namedRoute);
    }

    [Fact]
    public async Task NotifyChangedAsync_EmptyRouteSet_IsANoOp()
    {
        var result = await _notifier.NotifyChangedAsync([], CancellationToken.None);

        Assert.Equal(0, result.SubscribersMatched);
        Assert.Equal(0, result.CallbacksInvoked);
        Assert.Empty(result.UnmatchedRoutes);
        Assert.Null(result.SubscribedRoutesAtZeroMatch);
    }

    /// <summary>
    /// §12 remediation (supervisor finding): a push that touches several routes and reaches viewers on
    /// only some of them must be distinguishable from a fully healthy delivery, not merely from a
    /// fully-missed one. Before this fix, <see cref="PageChangeNotificationResult.SubscribedRoutesAtZeroMatch"/>
    /// stayed <see langword="null"/> whenever the *global* matched count was non-zero, so this exact
    /// scenario -- one route delivered, one silently missed -- rendered no differently from two-for-two.
    /// </summary>
    [Fact]
    public async Task NotifyChangedAsync_PartialMatchAcrossMultipleRoutes_NamesExactlyTheUnmatchedOnes()
    {
        var deliveredRoute = PageRouteCodec.Encode("delivered.md");
        var missedRoute = PageRouteCodec.Encode("missed.md");
        using var subscription = _notifier.Subscribe(deliveredRoute, () => Task.CompletedTask);

        var result = await _notifier.NotifyChangedAsync([deliveredRoute, missedRoute], CancellationToken.None);

        Assert.Equal(1, result.SubscribersMatched);
        Assert.Equal(1, result.CallbacksInvoked);
        var unmatched = Assert.Single(result.UnmatchedRoutes);
        Assert.Equal(missedRoute, unmatched);

        // The pre-existing all-zero diagnostic is a different field and must stay unset here -- this
        // delivery was not a global zero-match, only a partial one.
        Assert.Null(result.SubscribedRoutesAtZeroMatch);
    }

    /// <summary>
    /// The full-match end of the same property: every diffed route reaching at least one subscriber
    /// must render as genuinely empty, so a reader (or <see cref="PushReactionService.LogReactionOutcome"/>)
    /// can tell "nothing missed" apart from "something missed" on this field alone.
    /// </summary>
    [Fact]
    public async Task NotifyChangedAsync_EveryRouteMatched_UnmatchedRoutesIsEmpty()
    {
        var routeA = PageRouteCodec.Encode("a.md");
        var routeB = PageRouteCodec.Encode("b.md");
        using var subscriptionA = _notifier.Subscribe(routeA, () => Task.CompletedTask);
        using var subscriptionB = _notifier.Subscribe(routeB, () => Task.CompletedTask);

        var result = await _notifier.NotifyChangedAsync([routeA, routeB], CancellationToken.None);

        Assert.Empty(result.UnmatchedRoutes);
    }

    [Fact]
    public async Task Subscribe_DisposedSubscription_IsNeverInvokedAgain()
    {
        var route = PageRouteCodec.Encode("page.md");
        var invocationCount = 0;
        var subscription = _notifier.Subscribe(route, () => { invocationCount++; return Task.CompletedTask; });

        subscription.Dispose();
        await _notifier.NotifyChangedAsync([route], CancellationToken.None);

        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task NotifyChangedAsync_MultipleSubscribersOnTheSameRoute_AllInvoked()
    {
        var route = PageRouteCodec.Encode("page.md");
        var firstInvoked = false;
        var secondInvoked = false;

        using var first = _notifier.Subscribe(route, () => { firstInvoked = true; return Task.CompletedTask; });
        using var second = _notifier.Subscribe(route, () => { secondInvoked = true; return Task.CompletedTask; });

        await _notifier.NotifyChangedAsync([route], CancellationToken.None);

        Assert.True(firstInvoked);
        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task NotifyChangedAsync_OneSubscriberThrows_TheOthersAreStillNotified()
    {
        // D19 decision 3: a subscriber's own callback throwing (a disposed component racing its own
        // circuit teardown) must not throw its way out of the whole broadcast and lose the other
        // routes'/subscribers' notifications with it.
        var throwingRoute = PageRouteCodec.Encode("throws.md");
        var okRoute = PageRouteCodec.Encode("ok.md");
        var okInvoked = false;

        using var throwing = _notifier.Subscribe(throwingRoute, () => throw new InvalidOperationException("boom"));
        using var ok = _notifier.Subscribe(okRoute, () => { okInvoked = true; return Task.CompletedTask; });

        var result = await _notifier.NotifyChangedAsync([throwingRoute, okRoute], CancellationToken.None);

        Assert.True(okInvoked, "a throwing subscriber must not prevent another route's subscriber from being notified.");

        // §12.1: matched and invoked are genuinely two numbers once a callback can fail -- the throwing
        // subscriber is counted as matched (it was under a notified route) but not as invoked (its
        // callback never completed).
        Assert.Equal(2, result.SubscribersMatched);
        Assert.Equal(1, result.CallbacksInvoked);
    }

    [Fact]
    public async Task NotifyChangedAsync_SubscribersDisposedAfterNotification_AreNotInvokedOnASecondBroadcast()
    {
        var route = PageRouteCodec.Encode("page.md");
        var invocationCount = 0;
        var subscription = _notifier.Subscribe(route, () => { invocationCount++; return Task.CompletedTask; });

        await _notifier.NotifyChangedAsync([route], CancellationToken.None);
        subscription.Dispose();
        await _notifier.NotifyChangedAsync([route], CancellationToken.None);

        Assert.Equal(1, invocationCount);
    }
}
