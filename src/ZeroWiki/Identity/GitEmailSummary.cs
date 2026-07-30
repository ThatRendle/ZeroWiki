namespace ZeroWiki.Identity;

/// <summary>A git email as its owning account sees it in a list.</summary>
/// <param name="Id">Identifier used to remove the association.</param>
/// <param name="Email">The address, as stored.</param>
public sealed record GitEmailSummary(Guid Id, string Email);
