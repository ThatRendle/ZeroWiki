namespace ZeroWiki.Identity;

/// <summary>The result of attempting the one-time first-administrator bootstrap.</summary>
public enum BootstrapOutcome
{
    /// <summary>The store was empty and the first administrator account was created.</summary>
    Created,

    /// <summary>
    /// An account already existed, so nothing was created. The bootstrap path is inert from
    /// the moment the first account exists.
    /// </summary>
    AlreadyBootstrapped,
}
