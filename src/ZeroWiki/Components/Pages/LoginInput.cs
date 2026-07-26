using System.ComponentModel.DataAnnotations;
using ZeroWiki.Identity;

namespace ZeroWiki.Components.Pages;

/// <summary>
/// The login form.
/// </summary>
/// <remarks>
/// Validation here is confined to rules whose rejection set provably contains no valid account:
/// an empty username and one longer than the column can hold. In particular the username is
/// <em>not</em> matched against the character set new accounts must satisfy. Doing so would put
/// a pattern match on the most anonymous route in the application, and — worse — a username
/// rejected on shape would fail without paying for a password verification, which rebuilds
/// exactly the "does this account exist" oracle the credential check exists to close. Anything
/// that could name an account is a candidate: let the lookup miss and pay the same cost.
/// </remarks>
public sealed class LoginInput
{
    private string _username = string.Empty;

    /// <summary>
    /// Trimmed on the way in, matching every path that writes a username. The trim happens
    /// before the lookup and applies to every input equally, so it cannot tell anyone whether an
    /// account exists — and without it, a pasted trailing space would fail to match an account
    /// whose name was trimmed when it was created.
    /// </summary>
    [Required(ErrorMessage = "Enter your username.")]
    [StringLength(
        CredentialPolicy.MaximumUsernameLength,
        ErrorMessage = CredentialPolicy.MaximumUsernameLengthRuleDescription)]
    public string Username
    {
        get => _username;
        set => _username = (value ?? string.Empty).Trim();
    }

    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(
        CredentialPolicy.MaximumPasswordLength,
        ErrorMessage = CredentialPolicy.MaximumPasswordLengthRuleDescription)]
    public string Password { get; set; } = string.Empty;
}
