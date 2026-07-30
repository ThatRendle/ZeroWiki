namespace ZeroWiki.Components.Pages;

/// <summary>
/// The address a member is asking to associate with their own account. The value arrives from
/// the browser and authorises nothing — <see cref="ZeroWiki.Identity.GitEmailService.AddAsync"/>
/// always associates it with the signed-in account, never with one named in the form.
/// </summary>
public sealed class AddGitEmailInput
{
    public string Email { get; set; } = string.Empty;
}
