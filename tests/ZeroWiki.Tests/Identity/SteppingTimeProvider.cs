namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Returns one instant on the first read of the clock and another on every read after it, so a test
/// can put a controlled gap between two clock reads inside a single call.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> cannot express this: it is moved
/// by the test, from outside, and the two reads being separated here happen inside one
/// <c>RedeemAsync</c> — before the Argon2id derivation and after the write lock is finally granted.
/// The gap is real (~93 ms of key derivation plus up to SQLite's 30 s busy timeout); this makes it
/// large enough to assert on.
/// </remarks>
public sealed class SteppingTimeProvider(DateTimeOffset first, DateTimeOffset afterwards) : TimeProvider
{
    private int _reads;

    /// <summary>How many times the clock has been read.</summary>
    public int Reads => _reads;

    public override DateTimeOffset GetUtcNow() =>
        Interlocked.Increment(ref _reads) == 1 ? first : afterwards;
}
