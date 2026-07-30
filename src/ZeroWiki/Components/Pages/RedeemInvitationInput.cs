using System.ComponentModel.DataAnnotations;
using ZeroWiki.Identity;

namespace ZeroWiki.Components.Pages;

/// <summary>
/// The credentials an invitee chooses when redeeming an invitation. The rules and their wording
/// come from <see cref="CredentialPolicy"/>, which is the whole reason it is a shared constant:
/// this and the first-administrator bootstrap are the only two paths where a person picks their
/// own password, and AD10 exists so they cannot diverge.
/// </summary>
public sealed class RedeemInvitationInput
{
    private string _username = string.Empty;

    /// <summary>
    /// Trimmed on the way in, so validation and the store agree on what a username <em>is</em> —
    /// see <see cref="BootstrapInput.Username"/> for why the untrimmed value would produce a
    /// message about the character set that never mentions spaces.
    /// </summary>
    [Required(ErrorMessage = "Choose a username.")]
    [StringLength(
        CredentialPolicy.MaximumUsernameLength,
        ErrorMessage = CredentialPolicy.MaximumUsernameLengthRuleDescription)]
    [RegularExpression(
        CredentialPolicy.UsernamePattern,
        MatchTimeoutInMilliseconds = CredentialPolicy.UsernamePatternTimeoutMilliseconds,
        ErrorMessage = CredentialPolicy.UsernameRuleDescription)]
    public string Username
    {
        get => _username;
        set => _username = (value ?? string.Empty).Trim();
    }

    /// <summary>Never trimmed: leading and trailing spaces are part of a password.</summary>
    [Required(ErrorMessage = "Choose a password.")]
    [MinLength(
        CredentialPolicy.MinimumPasswordLength,
        ErrorMessage = CredentialPolicy.MinimumPasswordLengthRuleDescription)]
    [StringLength(
        CredentialPolicy.MaximumPasswordLength,
        ErrorMessage = CredentialPolicy.MaximumPasswordLengthRuleDescription)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm the password.")]
    [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
