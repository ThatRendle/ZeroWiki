using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Cancels a linked <see cref="CancellationTokenSource"/> the instant a query's
/// <see cref="DbDataReader"/> is disposed — after the row (or its absence) has already been
/// resolved, but before control returns to the awaiting caller. Lets a test land a cancellation on
/// the unknown-username path of <c>LoginService.VerifyCredentialsAsync</c>, where
/// <c>CanVerify</c> never runs (short-circuited) and so cannot be used as a hook.
/// </summary>
/// <remarks>
/// <para>
/// <c>DataReaderDisposing</c>, not <c>ReaderExecutedAsync</c>, is load-bearing here — verified
/// empirically in a throwaway spike before this was written, not assumed. Cancelling at
/// <c>ReaderExecutedAsync</c> lands the token before EF's own subsequent internal
/// <c>ReadAsync</c>, whose default <see cref="DbDataReader"/> implementation checks
/// <c>IsCancellationRequested</c> before reading and throws — so <c>SingleOrDefaultAsync</c>
/// itself throws before <c>candidate</c> is ever assigned, regardless of where any downstream
/// check sits. That would pass whether or not a caller's own check exists at all, proving
/// nothing. By the time the reader is disposed, the row has already been read (or the query has
/// already determined there is none), and nothing downstream re-checks the token on the way out —
/// so a query on an empty result set completes normally with the token left cancelled, exactly the
/// window a test needs to land a cancellation in.
/// </para>
/// </remarks>
public sealed class CancelOnReaderDisposingInterceptor(CancellationTokenSource cancellationTokenSource)
    : DbCommandInterceptor
{
    public override InterceptionResult DataReaderDisposing(
        DbCommand command,
        DataReaderDisposingEventData eventData,
        InterceptionResult interceptionResult)
    {
        cancellationTokenSource.Cancel();

        return base.DataReaderDisposing(command, eventData, interceptionResult);
    }
}
