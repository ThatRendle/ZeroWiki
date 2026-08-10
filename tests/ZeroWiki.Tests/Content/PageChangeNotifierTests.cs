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

    [Fact]
    public async Task NotifyChangedAsync_RouteWithNoSubscribers_IsANoOp()
    {
        var route = PageRouteCodec.Encode("nobody-home.md");

        // No Subscribe call at all -- must not throw and must not need a subscriber to exist.
        await _notifier.NotifyChangedAsync([route], CancellationToken.None);
    }

    [Fact]
    public async Task NotifyChangedAsync_EmptyRouteSet_IsANoOp()
    {
        await _notifier.NotifyChangedAsync([], CancellationToken.None);
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

        await _notifier.NotifyChangedAsync([throwingRoute, okRoute], CancellationToken.None);

        Assert.True(okInvoked, "a throwing subscriber must not prevent another route's subscriber from being notified.");
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
