using Microsoft.Extensions.Logging;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Captures log entries so tests can assert what an operator would be able to see — and, just as
/// importantly, what must never appear there.
/// </summary>
public sealed class CapturingLoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    public IEnumerable<string> Messages => _entries.Select(entry => entry.Message);

    public ILogger<T> CreateLogger<T>() => new CapturingLogger<T>(_entries);

    public sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T>(List<LogEntry> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
