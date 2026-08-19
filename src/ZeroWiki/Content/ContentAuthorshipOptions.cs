namespace ZeroWiki.Content;

/// <summary>
/// The domain <see cref="AccountGitAuthorFactory"/> uses to build the synthetic git author address a
/// browser save is authored with (D10). Bound from the <c>ContentAuthorship</c> configuration section,
/// the same way <see cref="ContentStorageOptions.DataRoot"/> is bound from <c>ContentStorage</c>, so it
/// can be overridden by <c>appsettings.*.json</c> or a <c>ContentAuthorship__HostDomain</c> environment
/// variable.
/// </summary>
/// <remarks>
/// D10 is explicit that this is configuration, never the request: the <c>Host</c> header varies per
/// request and is client-supplied, so deriving from it would both split one person's history between
/// environments and let a spoofed header write an attacker-chosen author line into history this design
/// never rewrites.
/// </remarks>
public sealed class ContentAuthorshipOptions
{
    public const string SectionName = "ContentAuthorship";

    /// <summary>
    /// The unconfigured default (Product Owner decision — design.md's D10, "The domain defaults to
    /// <c>zerowiki.org</c>"): the Product Owner's own domain, the same namespace
    /// <see cref="GitAuthor.System"/> already stamps on D9 recovery commits, so one authorship convention
    /// covers both the software's identity and its members'. Accepted cost: a domain that genuinely
    /// resolves looks deliverable and could be harvested from a public remote; D10's "synthetic, need not
    /// be deliverable" still governs.
    /// </summary>
    public const string DefaultHostDomain = "zerowiki.org";

    /// <summary>The domain. Defaults to <see cref="DefaultHostDomain"/>.</summary>
    public string HostDomain { get; set; } = DefaultHostDomain;
}
