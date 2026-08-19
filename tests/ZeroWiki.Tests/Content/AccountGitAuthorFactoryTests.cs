using System.Net.Mail;
using Microsoft.Extensions.Options;
using ZeroWiki.Content;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// D10's <em>Consequence binding §6</em> and D17's authorship paragraph, gated by
/// <c>specs/content-editing/spec.md</c>'s scenario <i>An account predating the username rules still
/// commits a well-formed author</i>.
/// </summary>
public sealed class AccountGitAuthorFactoryTests
{
    private const string DefaultHostDomain = ContentAuthorshipOptions.DefaultHostDomain;

    /// <summary>
    /// The username-choosing pattern as it stood at <c>d90b00e</c>, the very first commit that let anyone
    /// choose a username — predating D11 entirely. Kept here, not reused from
    /// <see cref="CredentialPolicy.UsernamePattern"/> (which is D11's current, tighter pattern), so
    /// <see cref="TrailingDotOnlyUsername_WasGenuinelyChoosableBeforeD11"/> proves the trailing-dot
    /// fixture below against the rule that actually governed it at the time, rather than asserting the
    /// claim in prose only.
    /// </summary>
    private const string PreD11UsernamePattern = @"^[A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z";

    private static AccountGitAuthorFactory CreateFactory(string hostDomain = DefaultHostDomain) =>
        new(Options.Create(new ContentAuthorshipOptions { HostDomain = hostDomain }));

    [Fact]
    public void DefaultHostDomain_IsTheConfiguredConstant()
    {
        // Pins that the option's default tracks ContentAuthorshipOptions.DefaultHostDomain rather than a
        // second, independently-spelled literal — the specific value (design.md D10) is out of scope here.
        Assert.Equal(ContentAuthorshipOptions.DefaultHostDomain, new ContentAuthorshipOptions().HostDomain);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("a9")]
    public void OrdinaryUsername_IsUsedDirectlyAsTheLocalpart(string username)
    {
        var factory = CreateFactory();
        var account = new AuthenticatedAccount(Guid.NewGuid(), username, IsAdministrator: false);

        var author = factory.CreateAuthor(account);

        Assert.Equal(username, author.Name);
        Assert.Equal($"{username}@{DefaultHostDomain}", author.Email);
        AssertWellFormedAddress(author.Email);
    }

    [Theory]
    [InlineData("ab")] // too short for D11 (min length 3), but a legal RFC 5322 dot-atom
    [InlineData("_legacy_")] // begins/ends with a separator D11 refuses, but a legal dot-atom (no dots at all)
    public void LegacyUsernameThatIsStillALegalDotAtom_IsUsedDirectlyAsTheLocalpart(string username)
    {
        // LoginServiceTests.cs:74-89 pins these as still authenticating even though D11 would now refuse
        // them at choice time; the commit these accounts author must still be well-formed.
        var factory = CreateFactory();
        var account = new AuthenticatedAccount(Guid.NewGuid(), username, IsAdministrator: false);

        var author = factory.CreateAuthor(account);

        Assert.Equal($"{username}@{DefaultHostDomain}", author.Email);
        AssertWellFormedAddress(author.Email);
    }

    [Fact]
    public void TrailingDotOnlyUsername_WasGenuinelyChoosableBeforeD11()
    {
        // Executable proof, not a prose claim: "legacy." has a trailing dot and no leading one — it fails
        // today's CredentialPolicy.UsernamePattern (D11 requires an alphanumeric end) but matched the
        // pattern actually in force at d90b00e, before D11 existed. A member could therefore genuinely
        // have chosen it. If this assertion ever fails, the fixture below is not a real member of the
        // population obligation 7 covers and must be replaced, not kept.
        Assert.Matches(PreD11UsernamePattern, "legacy.");
        Assert.DoesNotMatch(CredentialPolicy.UsernameMatcher(), "legacy.");
    }

    [Fact]
    public void MailAddressOracle_AcceptsATrailingDotLocalpart_DocumentedLeniencePin()
    {
        // The reviewer's finding on this block, re-verified here rather than taken on faith, and pinned
        // so a future .NET runtime tightening this behaviour is visible: MailAddress does not throw on a
        // trailing-dot-only localpart, even though RFC 5322 dot-atom-text forbids it (a dot may only ever
        // sit between runs of atext). This is not asserting correctness — it is recording exactly why
        // AssertWellFormedAddress must never be the only check a test relies on (see its remarks).
        var parsed = new MailAddress("trailingdot.@zerowiki.org");
        Assert.Equal("trailingdot.", parsed.User);
    }

    [Theory]
    [InlineData(".old.name.")] // leading AND trailing dot (LoginServiceTests.cs:76)
    [InlineData("legacy.")] // trailing dot ONLY — see TrailingDotOnlyUsername_WasGenuinelyChoosableBeforeD11
    public void LegacyUsernameThatIsNotALegalDotAtom_GetsTheDeterministicFallback(string username)
    {
        // RFC 5322 dot-atom-text requires 1*atext at both ends, so a dot may only ever sit *between* runs
        // of atext — a leading dot, a trailing dot, or both are equally illegal. This localpart must
        // therefore not be used directly regardless of which end the dot sits at.
        var factory = CreateFactory();
        var id = Guid.NewGuid();
        var account = new AuthenticatedAccount(id, username, IsAdministrator: false);

        var author = factory.CreateAuthor(account);

        Assert.Equal(username, author.Name); // display name stays the raw username regardless
        Assert.DoesNotContain(username, author.Email);
        Assert.Contains($"+{id:N}", author.Email);
        Assert.EndsWith($"@{DefaultHostDomain}", author.Email, StringComparison.Ordinal);
        AssertWellFormedAddress(author.Email);
    }

    [Fact]
    public void FallbackLocalpart_IsDisambiguatedByAccountId_SoTwoLegacyUsernamesNeverCollapse()
    {
        // Deliberately pairs a leading-dot username with a trailing-dot-ONLY one: MailAddress alone would
        // not catch a regression that used the raw trailing-dot username directly (see AssertWellFormedAddress's
        // remarks — MailAddress accepts a trailing-dot localpart, which is illegal RFC 5322 dot-atom-text),
        // so this test cannot lean on it as the only check. AssertLocalPartIsTheFallbackForm below is the
        // structural check that actually catches that regression; MailAddress stays only as an addition.
        var factory = CreateFactory();
        var accountA = new AuthenticatedAccount(Guid.NewGuid(), ".old.name.", IsAdministrator: false);
        var accountB = new AuthenticatedAccount(Guid.NewGuid(), "legacy.", IsAdministrator: false);

        var authorA = factory.CreateAuthor(accountA);
        var authorB = factory.CreateAuthor(accountB);

        Assert.NotEqual(authorA.Email, authorB.Email);
        AssertLocalPartIsTheFallbackForm(authorA.Email, accountA.Username);
        AssertLocalPartIsTheFallbackForm(authorB.Email, accountB.Username);
        AssertWellFormedAddress(authorA.Email);
        AssertWellFormedAddress(authorB.Email);
    }

    [Fact]
    public void FallbackLocalpart_IsDeterministic_SameAccountAlwaysProducesTheSameAddress()
    {
        var factory = CreateFactory();
        var account = new AuthenticatedAccount(Guid.NewGuid(), ".old.name.", IsAdministrator: false);

        var first = factory.CreateAuthor(account);
        var second = factory.CreateAuthor(account);

        Assert.Equal(first.Email, second.Email);
    }

    [Fact]
    public void FallbackLocalpart_IsBuiltFromACharacterCredentialPolicyNeverAcceptsInAUsername()
    {
        // Re-derived from CredentialPolicy.UsernamePattern as it actually stands, not from D11's prose:
        // '+' does not satisfy the pattern in any position it could occupy, so nothing this pattern would
        // ever let a member choose can collide with the fallback space this factory draws from. (The
        // charset has excluded '+' in every version this pattern has had, including the one that
        // predates D11 — see the DEVLOG for that historical check, which a unit test cannot reach.)
        Assert.DoesNotMatch(CredentialPolicy.UsernameMatcher(), "+");
        Assert.DoesNotMatch(CredentialPolicy.UsernameMatcher(), "a+b");
        Assert.DoesNotMatch(CredentialPolicy.UsernameMatcher(), $"account+{Guid.NewGuid():N}");
    }

    [Theory]
    [InlineData("host.example.com")]
    [InlineData("wiki.internal")]
    public void HostDomain_IsWhateverConfigurationSays_NeverHardcoded(string domain)
    {
        var factory = CreateFactory(domain);
        var account = new AuthenticatedAccount(Guid.NewGuid(), "alice", IsAdministrator: false);

        var author = factory.CreateAuthor(account);

        Assert.EndsWith($"@{domain}", author.Email, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("_legacy_")]
    [InlineData(".old.name.")]
    public void RawUsernameCannotBreakTheAuthorNameLine(string username)
    {
        // Git's name field constrains only '<', '>' and newline; CredentialPolicy.UsernamePattern has
        // excluded all three in every version it has ever had (the charset is [A-Za-z0-9_.-] today, and
        // was [A-Za-z0-9._-] before D11), so no username reaching this factory can ever contain one.
        Assert.DoesNotContain('<', username);
        Assert.DoesNotContain('>', username);
        Assert.DoesNotContain('\n', username);

        var factory = CreateFactory();
        var author = factory.CreateAuthor(new AuthenticatedAccount(Guid.NewGuid(), username, IsAdministrator: false));

        Assert.DoesNotContain('<', author.Name);
        Assert.DoesNotContain('>', author.Name);
        Assert.DoesNotContain('\n', author.Name);
    }

    /// <summary>
    /// Asserts <paramref name="email"/> parses as an RFC-shaped address using .NET's own, independent
    /// parser (<see cref="MailAddress"/>) rather than re-deriving the expected string the way the code
    /// under test builds it — a fixture built by the code under test cannot falsify that code.
    /// </summary>
    /// <remarks>
    /// <b><see cref="MailAddress"/> is a convenience cross-check, not a conformance oracle — it is looser
    /// than RFC 5322 <c>dot-atom-text</c> in at least one documented way</b> (a reviewer finding on this
    /// block): it does not throw on a <em>trailing</em>-dot-only localpart (e.g.
    /// <c>trailingdot.@zerowiki.org</c>), which is illegal. It still catches a <em>leading</em> dot and
    /// several other malformed shapes (proven empirically above — see
    /// <see cref="LegacyUsernameThatIsNotALegalDotAtom_GetsTheDeterministicFallback"/>'s
    /// <c>".old.name."</c> case throwing under the fallback-disabled mutant during this block's mutation
    /// testing), so it stays useful as an <em>additional</em> check. It must never be the only one a test
    /// relies on for a population that includes a trailing-dot-only shape — see
    /// <see cref="AssertLocalPartIsTheFallbackForm"/>, which is the structural check that actually covers
    /// that shape.
    /// </remarks>
    private static void AssertWellFormedAddress(string email)
    {
        var parsed = new MailAddress(email); // throws FormatException on most, but not all, illegal shapes
        Assert.NotEmpty(parsed.User);
        Assert.NotEmpty(parsed.Host);
    }

    /// <summary>
    /// Structurally asserts <paramref name="email"/>'s localpart is the <c>+</c>-fallback form rather than
    /// <paramref name="rawUsername"/> used directly, and that no dot sits at either end of that localpart
    /// — the property <see cref="MailAddress"/> alone cannot be trusted to catch (its remarks above).
    /// </summary>
    private static void AssertLocalPartIsTheFallbackForm(string email, string rawUsername)
    {
        var atSignIndex = email.IndexOf('@', StringComparison.Ordinal);
        Assert.True(atSignIndex > 0, $"'{email}' has no localpart to check.");
        var localPart = email[..atSignIndex];

        Assert.NotEqual(rawUsername, localPart);
        Assert.Contains('+', localPart);
        Assert.NotEqual('.', localPart[0]);
        Assert.NotEqual('.', localPart[^1]);
    }
}
