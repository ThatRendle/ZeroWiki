namespace ZeroWiki.Identity;

/// <summary>
/// The rules an invitation is issued under. Kept beside <see cref="CredentialPolicy"/> and for
/// the same reason: the numbers appear in the store, in the user-facing copy and in the tests,
/// and nothing but a shared constant stops the three drifting apart.
/// </summary>
public static class InvitationPolicy
{
    /// <summary>
    /// How long an invitation stays redeemable (AD14). A week survives a weekend and a missed
    /// message, while a link that leaks into a chat backlog does not stay live indefinitely.
    /// </summary>
    /// <remarks>
    /// The expiry is computed from this <em>once, at issue</em>, and persisted. Re-deriving it at
    /// redemption would silently re-date every outstanding invitation the day this number changes.
    /// </remarks>
    public const int LifetimeDays = 7;

    /// <summary>The same bound as <see cref="LifetimeDays"/>, in the form the service adds.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(LifetimeDays);

    /// <summary>
    /// Kept beside the number it quotes, because an interpolated <c>const</c> cannot embed an
    /// <c>int</c>. A test asserts the two still agree.
    /// </summary>
    public const string LifetimeRuleDescription = "An invitation link expires 7 days after it is issued.";

    /// <summary>
    /// Route prefix of the redemption page, which the shown-once link points at. The page itself
    /// is Block 4b's; the link is built here, so the path has to be stated in one place both can
    /// read rather than spelled out twice.
    /// </summary>
    public const string RedemptionPath = "/invite";
}
