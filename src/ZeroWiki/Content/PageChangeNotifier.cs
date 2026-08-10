using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// The in-process singleton implementation of <see cref="IPageChangeNotifier"/> (D19 §3). Every
/// subscription lives in one flat table keyed by a fresh <see cref="Guid"/> per registration, not a
/// per-route dictionary of dictionaries — membership for a notification is decided by a simple
/// route-set lookup rather than by mutating nested collections, which sidesteps a real remove-vs-add
/// race a nested structure would otherwise need its own synchronization to close (an inner dictionary
/// emptied by one unsubscribe while another subscribe is landing on the same route). The cost is an
/// O(open circuits) scan per push instead of O(routes touched); for a self-hosted wiki's subscriber
/// count — one entry per open browser tab, not per page in the wiki — that trade is the right one.
/// </summary>
public sealed class PageChangeNotifier : IPageChangeNotifier
{
    private readonly ILogger<PageChangeNotifier> _logger;
    private readonly ConcurrentDictionary<Guid, (EncodedRoute Route, Func<Task> OnChanged)> _subscriptions = new();

    public PageChangeNotifier(ILogger<PageChangeNotifier> logger)
    {
        _logger = logger;
    }

    public IDisposable Subscribe(EncodedRoute route, Func<Task> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var id = Guid.NewGuid();
        _subscriptions[id] = (route, onChanged);
        return new Subscription(this, id);
    }

    public async Task NotifyChangedAsync(IReadOnlyCollection<EncodedRoute> routes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (routes.Count == 0)
        {
            return;
        }

        var routeSet = routes as HashSet<EncodedRoute> ?? new HashSet<EncodedRoute>(routes);

        // A snapshot of the current subscriptions, not a live enumeration -- ConcurrentDictionary's own
        // enumerator tolerates concurrent mutation, but a snapshot means a subscriber that registers or
        // disposes mid-broadcast is simply not part of *this* broadcast, never a torn read of one.
        foreach (var (route, onChanged) in _subscriptions.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!routeSet.Contains(route))
            {
                // A viewer on a route this push never touched is never invoked at all (D19 §3) -- not
                // told and then filtered client-side.
                continue;
            }

            try
            {
                await onChanged().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // D19's "cannot throw its way out of the whole reaction": one subscriber's callback
                // failing (a disposed component racing its own circuit teardown, a JS interop fault)
                // must not stop the remaining subscribers -- for this route or any other in this
                // broadcast -- from being notified.
                _logger.LogWarning(
                    ex,
                    "A subscriber for page route '{Route}' threw while being notified that the page " +
                    "changed on disk; the remaining subscribers were still notified.",
                    route);
            }
        }
    }

    private void Unsubscribe(Guid id) => _subscriptions.TryRemove(id, out _);

    private sealed class Subscription : IDisposable
    {
        private readonly PageChangeNotifier _owner;
        private readonly Guid _id;
        private int _disposed;

        public Subscription(PageChangeNotifier owner, Guid id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unsubscribe(_id);
            }
        }
    }
}
