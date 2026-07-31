using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Cancels a linked token the instant <c>SaveChangesAsync</c> finishes writing, landing the
/// cancellation in the window between the write and the transaction commit that follows it.
/// </summary>
/// <remarks>
/// That window is the one a pre-cancelled token cannot reach: production code checks the token on
/// its own earlier awaits first and throws there instead, so a caller who starts already
/// cancelled never gets as far as opening the transaction at all. Registering this on a test's own
/// <c>DbContextOptionsBuilder</c> is the only way to land the cancellation exactly where the
/// rollback the tests are exercising actually lives, without changing production code to make room
/// for it.
/// </remarks>
public sealed class CancelAfterSaveInterceptor(CancellationTokenSource cancellationTokenSource) : SaveChangesInterceptor
{
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        cancellationTokenSource.Cancel();

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}
