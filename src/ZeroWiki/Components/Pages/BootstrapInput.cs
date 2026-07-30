using System.ComponentModel.DataAnnotations;
using ZeroWiki.Identity;

namespace ZeroWiki.Components.Pages;

/// <summary>
/// The first-administrator form. The rules and their wording come from
/// <see cref="CredentialPolicy"/> so that this path and invitation redemption cannot diverge;
/// nothing beyond them is invented here.
/// </summary>
public sealed class BootstrapInput
{
    private string _username = string.Empty;

    /// <summary>
    /// Trimmed on the way in, so that validation and the store agree on what a username
    /// <em>is</em>. Validating the untrimmed value while the service trims would let the form
    /// reject <c>"admin "</c> — what a paste routinely produces — with a message about the
    /// character set that never mentions spaces.
    /// </summary>
    /// <remarks>
    /// <see cref="RequiredAttribute"/> trims before its own emptiness test, so whitespace alone
    /// fails either way.
    /// </remarks>
    [Required(ErrorMessage = "Enter a username.")]
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
    [Required(ErrorMessage = "Enter a password.")]
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
