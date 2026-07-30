using Microsoft.Data.Sqlite;

namespace ZeroWiki.Tests;

/// <summary>
/// Builds the connection string every file-backed test database is opened with, and removes the
/// file afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Pooling=False</c> is the whole point of this type, and removing it reintroduces an
/// intermittent, suite-wide failure.</b> The alternative — letting connections pool and calling
/// <c>SqliteConnection.ClearAllPools()</c> on the way out so the file could be deleted — is
/// process-global: it reaches into the pools of every <em>other</em> test class, and xUnit runs
/// collections in parallel, so one class finishing cleared pools three other classes were still
/// using.
/// </para>
/// <para>
/// That is not merely untidy, it is a live race in <c>Microsoft.Data.Sqlite</c> 10.0.10.
/// <c>SqliteConnectionInternal.Activate</c> sets <c>_active = true</c> <em>before</em> it sets the
/// weak reference to its outer connection, and <c>Leaked</c> is
/// <c>_active &amp;&amp; !_outerConnection.TryGetTarget(…)</c> — so between those two writes a
/// perfectly healthy connection reads as leaked. A concurrent <c>ClearAllPools()</c> reclaims and
/// disposes it, and the thread that was opening it proceeds onto a disposed <c>sqlite3</c> handle.
/// The observed symptom is <c>ObjectDisposedException: … 'SQLitePCL.sqlite3'</c> thrown out of an
/// ordinary <c>OpenAsync</c>, in a test class that never called <c>ClearAllPools</c> and does not
/// share a database with the class that did. Reproduced in isolation at roughly one failure per
/// 2,000 opens with pooling on, and zero in 72,000 with it off.
/// </para>
/// <para>
/// Turning pooling off also makes <see cref="Delete"/> stronger than clearing ever was: no sqlite
/// handle outlives the connection object that owns it, so by the time a test's teardown runs there
/// is nothing still holding the file — on any platform, not just the ones where unlinking an open
/// file happens to work.
/// </para>
/// <para>
/// <b>This is a deliberate divergence from production, which is pooled.</b> Nothing in the
/// application depends on handle reuse — pooling decides only whether an <c>sqlite3</c> handle is
/// recycled, never how SQLite arbitrates the write lock, which is held against the database
/// <em>file</em> across every connection to it. So the concurrency tests contend exactly as they did
/// before. What is genuinely no longer covered is the pooled open path itself; no test asserted
/// anything about it, and the alternative was a suite that failed roughly one run in eight.
/// </para>
/// <para>
/// The in-memory tests need none of this: <c>Data Source=:memory:</c> is never pooled.
/// </para>
/// </remarks>
public static class TestDatabase
{
    public static string ConnectionStringFor(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();

    /// <summary>Removes the database and its write-ahead log, if SQLite left one behind.</summary>
    public static void Delete(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            File.Delete(path);
        }
    }
}
