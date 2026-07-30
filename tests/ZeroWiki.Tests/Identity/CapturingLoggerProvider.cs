using Microsoft.Extensions.Logging;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Captures log entries so tests can assert what an operator would be able to see — and, just as
/// importantly, what must never appear there.
/// </summary>
/// <remarks>
/// <b>This must not implement <c>ISupportExternalScope</c>, and that is not an oversight.</b> A
/// provider that implements it is handed the logging factory's shared scope provider, and the
/// factory then stops routing <see cref="ILogger.BeginScope"/> through the provider's own loggers
/// altogether — so <see cref="Written"/> would no longer see a single scope value. That is the exact
/// class of leak this helper exists to catch: measured on live code in §7, a credential passed to
/// <c>BeginScope</c> reaches a structured sink while appearing in no rendered message, so
/// <see cref="Messages"/> passes the whole suite and only <see cref="Written"/> fails. Adding the
/// interface as a tidy-up would make that leak invisible again with every test still green.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// Guards both lists. They are written from whatever thread logged, and this helper is now used
    /// by tests that deliberately run several of those at once — an unsynchronised
    /// <see cref="List{T}.Add"/> from two threads corrupts the list or throws, and it would surface
    /// as a flaky failure somewhere else entirely.
    /// </summary>
    private readonly Lock _guard = new();

    private readonly List<LogEntry> _entries = [];

    /// <summary>
    /// Every open scope in the provider, not just those on the logging thread — so an entry records
    /// scopes another request had open at the time. Sound for "this string reached no sink", which is
    /// what this helper is for; not sound for asserting that a given entry carried a given scope.
    /// </summary>
    private readonly List<object?> _scopes = [];

    /// <summary>A snapshot, so a reader cannot enumerate a list another thread is appending to.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_guard)
            {
                return [.. _entries];
            }
        }
    }

    public IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

    /// <summary>
    /// Everything an entry could put in front of a sink: the rendered message, every structured
    /// value, and every value carried by a scope that was open at the time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A "no secrets in the log" assertion has to read this rather than <see cref="Messages"/>,
    /// because the two are not the same set. Measured, rather than assumed, against
    /// <c>Microsoft.Extensions.Logging</c>:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// An argument <em>beyond</em> the template's placeholders is dropped from the message
    /// <em>and</em> from the structured values — it reaches no sink at all, so it is not a leak.
    /// </item>
    /// <item>
    /// A value in a placeholder appears in both, so the message alone would have caught it.
    /// </item>
    /// <item>
    /// A value carried by <see cref="ILogger.BeginScope"/> appears in <em>neither</em> — it reaches
    /// a structured sink while leaving no trace in any message. That is the shape a message-only
    /// assertion genuinely misses, and it is why scopes are captured here.
    /// </item>
    /// </list>
    /// </remarks>
    public IEnumerable<string> Written => Entries.SelectMany(entry =>
        entry.Values.Concat(entry.Scopes)
            .Select(value => $"{value.Key}={value.Value}")
            .Prepend(entry.Message));

    public ILogger<T> CreateLogger<T>() => new CapturingLogger<T>(this);

    /// <summary>Plugs the same capture into a running application's logging.</summary>
    /// <remarks>
    /// <para>
    /// Handing a logger to one service only sweeps what that service wrote. The secret this is used
    /// to hunt for is one that reached <em>any</em> sink, and the sink most likely to receive one
    /// nobody meant to write is the request log, which prints the URL — so a credential that leaked
    /// into a query string or a redirect target is visible here and nowhere else.
    /// </para>
    /// <para>
    /// The category is not recorded, deliberately: the question this helper answers is whether a
    /// string reached a sink, which no category changes the answer to.
    /// </para>
    /// </remarks>
    ILogger ILoggerProvider.CreateLogger(string categoryName) => new CapturingLogger<CapturingLoggerProvider>(this);

    /// <remarks>Nothing is held open; the entries outlive the host so a test can still read them.</remarks>
    public void Dispose()
    {
    }

    public sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Values,
        IReadOnlyList<KeyValuePair<string, object?>> Scopes);

    /// <summary>Flattens a scope's state the way a structured sink would read it.</summary>
    private static IEnumerable<KeyValuePair<string, object?>> Flatten(object? scope) =>
        scope switch
        {
            null => [],
            IEnumerable<KeyValuePair<string, object?>> values => values,
            _ => [new KeyValuePair<string, object?>("Scope", scope)],
        };

    private sealed class CapturingLogger<T>(CapturingLoggerProvider provider) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            lock (provider._guard)
            {
                provider._scopes.Add(state);
            }

            return new Scope(provider, state);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Rendered outside the lock: a formatter is caller-supplied and could log again, and
            // re-entering the lock from inside it would be the deadlock this helper least deserves.
            var message = formatter(state, exception);
            var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.ToArray()
                : [];

            lock (provider._guard)
            {
                provider._entries.Add(new LogEntry(
                    logLevel,
                    message,
                    values,
                    [.. provider._scopes.SelectMany(Flatten)]));
            }
        }

        private sealed class Scope(CapturingLoggerProvider provider, object? state) : IDisposable
        {
            public void Dispose()
            {
                lock (provider._guard)
                {
                    provider._scopes.Remove(state);
                }
            }
        }
    }
}
