namespace ZeroWiki.Identity;

/// <summary>
/// What became of an attempt to redeem an invitation, and — for the failures — what the invitee is
/// told.
/// </summary>
/// <remarks>
/// <para>
/// AD17 draws the line these values sit either side of. <see cref="NotValid"/> is the single answer
/// for a token that resolves to no stored row; every other rejection names a reason, and every one
/// of them is reachable <em>only after</em> the presented token has matched a stored hash. That is
/// what keeps this from being the enumeration oracle §5 closed on the login form: a caller shown a
/// reason has already proved possession of a 256-bit secret, so there is nothing left to enumerate.
/// </para>
/// <para>
/// The corollary binds anyone extending this enum: a new member must be derivable only from a row
/// the caller's own token matched, never from anything an anonymous caller can supply on its own.
/// </para>
/// </remarks>
public enum InvitationRedemption
{
    /// <summary>The account exists and the invitation is consumed.</summary>
    Redeemed,

    /// <summary>
    /// The presented token matches no invitation. Deliberately one uniform answer covering an
    /// unknown token, a mistyped one and a malformed one alike.
    /// </summary>
    NotValid,

    /// <summary>The invitation was issued too long ago to still be redeemable.</summary>
    Expired,

    /// <summary>The invitation has already created an account and cannot create a second.</summary>
    AlreadyRedeemed,

    /// <summary>The issuer (or an administrator) withdrew the invitation before it was used.</summary>
    Revoked,

    /// <summary>
    /// The invitation is good but the chosen username is taken. The invitation is <em>not</em>
    /// consumed — the invitee picks another name and tries again with the same link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This value is a username-enumeration oracle, and it is accepted knowingly.</strong>
    /// A holder of a live, unredeemed invitation can resubmit with different names and learn which
    /// usernames exist — the very thing §5's uniform login failure spends a dummy hash and a
    /// three-way private log to close, reached from a direction §5 does not cover. Saying so here
    /// rather than leaving a future reader to discover it.
    /// </para>
    /// <para>
    /// Why it is accepted, in terms that do not generalise. The prober must <em>possess a live
    /// invitation</em> — they are someone the system is actively granting membership to, not an
    /// anonymous stranger, which is exactly what §5's oracle did not require. And user-chosen
    /// unique usernames cannot be built without telling the user their choice was taken: a uniform
    /// message would leave a genuine invitee unable to get in and unable to learn why. Neither
    /// reason survives being carried to another surface, so do not cite this as precedent.
    /// </para>
    /// <para>
    /// What is <em>not</em> conceded: the invitation survives the clash. Consuming it would punish
    /// the invitee for a collision they had no way to predict.
    /// </para>
    /// </remarks>
    UsernameTaken,
}
