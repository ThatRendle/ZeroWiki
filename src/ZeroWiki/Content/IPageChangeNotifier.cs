namespace ZeroWiki.Content;

/// <summary>
/// D19 §3: the publish/subscribe layer riding on D7's <c>InteractiveServer</c> circuit — keyed by page
/// route, not a bespoke <c>Hub</c>. A component registers a callback under its own route when it
/// activates and disposes the registration when its circuit ends; <see cref="NotifyChangedAsync"/> looks
/// up only the routes a push's diff named and invokes exactly those callbacks. A viewer on a route the
/// push never touched has no callback registered under any of the notified routes, so it is never
/// invoked at all — not told and then filtering client-side.
/// </summary>
public interface IPageChangeNotifier
{
    /// <summary>
    /// Registers <paramref name="onChanged"/> to run whenever <paramref name="route"/> is later named by
    /// <see cref="NotifyChangedAsync"/>. Dispose the returned handle to unregister — a component's own
    /// circuit ending is the only caller that ever needs to.
    /// </summary>
    IDisposable Subscribe(EncodedRoute route, Func<Task> onChanged);

    /// <summary>
    /// Invokes every callback currently registered under any of <paramref name="routes"/>. A route with
    /// no subscriber is a no-op for that route, not an error. One subscriber's callback throwing is
    /// caught and logged (D19's own "cannot throw its way out of the whole reaction") rather than
    /// preventing the remaining subscribers — for this route or any other in <paramref name="routes"/> —
    /// from being notified.
    /// </summary>
    /// <returns>
    /// The outcome actually observed (§12.1) — see <see cref="PageChangeNotificationResult"/>. This is
    /// what makes a broadcast reaching no viewer distinguishable from a delivered one, rather than both
    /// producing identical (i.e. absent) output.
    /// </returns>
    Task<PageChangeNotificationResult> NotifyChangedAsync(
        IReadOnlyCollection<EncodedRoute> routes, CancellationToken cancellationToken);
}
