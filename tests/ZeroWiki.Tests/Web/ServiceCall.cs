namespace ZeroWiki.Tests.Web;

/// <summary>
/// One identity-service call site found by <see cref="ServiceCallSweep"/>. <see cref="TokenArgument"/>
/// is the call's last <em>top-level</em> positional argument, read verbatim and unclassified —
/// every one of the fifteen call sites this change touches passes its cancellation token as the
/// last argument, so the last argument is always the one worth reading.
/// </summary>
internal sealed record ServiceCall(string Service, string Method, string TokenArgument);
