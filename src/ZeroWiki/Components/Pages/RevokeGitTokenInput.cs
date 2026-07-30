namespace ZeroWiki.Components.Pages;

/// <summary>
/// Which git access token a revoke submission names. The value arrives from the browser, so it
/// grants nothing on its own — <see cref="ZeroWiki.Identity.GitTokenService.RevokeAsync"/> re-scopes
/// it to the tokens the signed-in account owns.
/// </summary>
public sealed class RevokeGitTokenInput
{
    public Guid TokenId { get; set; }
}
