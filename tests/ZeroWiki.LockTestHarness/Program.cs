using ZeroWiki.Content;

namespace ZeroWiki.LockTestHarness;

/// <summary>
/// A genuine second OS process running <see cref="RepositoryWriteLock"/>'s real production code (D16)
/// — not a fake or an in-process <see cref="Task"/> — so tests can prove the lock actually excludes
/// across process boundaries. Explicitly namespaced (not a top-level-statement <c>Program</c>) because
/// <c>ZeroWiki.Tests</c> also references <c>ZeroWiki</c>'s own top-level-statement <c>Program</c>
/// (<c>WebApplicationFactory&lt;Program&gt;</c>) — two implicit global-namespace <c>Program</c> types
/// from two referenced assemblies collide.
/// </summary>
/// <remarks>
/// Usage: <c>ZeroWiki.LockTestHarness &lt;lockFilePath&gt; &lt;acquireTimeoutMs&gt; &lt;holdMs&gt;</c>.
/// Prints <c>ACQUIRED</c> (and flushes) the instant the lock is held, so a caller can synchronize on it
/// rather than guessing with a fixed delay; holds it for <c>holdMs</c>, then releases and prints
/// <c>RELEASED</c>. Prints <c>TIMEOUT</c> and exits 1 if the lock could not be acquired within
/// <c>acquireTimeoutMs</c>.
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync("usage: ZeroWiki.LockTestHarness <lockFilePath> <acquireTimeoutMs> <holdMs>");
            return 2;
        }

        var lockFilePath = args[0];
        var acquireTimeout = TimeSpan.FromMilliseconds(double.Parse(args[1]));
        var holdMs = int.Parse(args[2]);

        try
        {
            using var writeLock = await RepositoryWriteLock.AcquireAsync(lockFilePath, acquireTimeout);
            Console.WriteLine("ACQUIRED");
            Console.Out.Flush();

            await Task.Delay(holdMs);

            Console.WriteLine("RELEASED");
            Console.Out.Flush();
            return 0;
        }
        catch (RepositoryLockTimeoutException)
        {
            Console.WriteLine("TIMEOUT");
            Console.Out.Flush();
            return 1;
        }
    }
}
