namespace ZeroWiki.Components.Pages;

/// <summary>
/// Which git email a removal submission names. The value arrives from the browser, so it grants
/// nothing on its own — <see cref="ZeroWiki.Identity.GitEmailService.RemoveAsync"/> re-scopes it
/// to the emails the signed-in account owns.
/// </summary>
public sealed class RemoveGitEmailInput
{
    public Guid EmailId { get; set; }
}
