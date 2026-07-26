namespace ZeroWiki.Components.Pages;

/// <summary>
/// Which invitation a revoke submission names. The value arrives from the browser, so it decides
/// nothing on its own — <see cref="ZeroWiki.Identity.InvitationService.RevokeAsync"/> re-scopes it
/// to what the caller may act on.
/// </summary>
public sealed class RevokeInvitationInput
{
    public Guid InvitationId { get; set; }
}
