namespace ZeroWiki.Identity;

/// <summary>What became of a request to associate a git email with an account.</summary>
public enum GitEmailAddOutcome
{
    /// <summary>The address is now associated with the caller's account.</summary>
    Added,

    /// <summary>The address was already on the caller's own account. Nothing changed.</summary>
    AlreadyOnThisAccount,

    /// <summary>
    /// The address is associated with a different account. <c>GitEmail.Email</c> is unique
    /// across every account (a <c>NOCASE</c> index), so the caller is told the real reason
    /// (AD24) — bounded to <em>that</em> it is taken, never to whom.
    /// </summary>
    TakenByAnotherAccount,

    /// <summary>The address does not have the shape of an email address (AD26).</summary>
    Malformed,
}
