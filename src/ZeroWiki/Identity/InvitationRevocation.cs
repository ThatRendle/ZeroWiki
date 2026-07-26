namespace ZeroWiki.Identity;

/// <summary>
/// What became of a revocation request. A <see cref="bool"/> would have to conflate
/// "there is nothing here for you" with "this one has already been redeemed and revoking it
/// would undo nothing" — and the spec allows revocation only <em>before</em> redemption, so
/// reporting the second as success would claim something that did not happen.
/// </summary>
public enum InvitationRevocation
{
    /// <summary>
    /// The invitation is revoked and can no longer be redeemed. Also the answer for one that was
    /// already revoked: revocation is idempotent and keeps the original revocation time.
    /// </summary>
    Revoked,

    /// <summary>
    /// No invitation with that identifier is visible to the caller — either it does not exist or
    /// it belongs to someone else. Deliberately one answer, so this cannot be used to discover
    /// that another member's invitation exists.
    /// </summary>
    NotFound,

    /// <summary>
    /// The invitation has already created an account. Revoking it would not un-create that
    /// account, so nothing is changed and the caller is told so.
    /// </summary>
    AlreadyRedeemed,
}
