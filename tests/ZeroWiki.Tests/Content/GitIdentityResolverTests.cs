using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// D19 §5 / D10's <em>Consequence binding §8.3</em>: the inbound resolver matches both synthetic
/// shapes <see cref="AccountGitAuthorFactory"/> can have produced, ahead of any registered
/// <see cref="GitEmail"/> row, ahead of falling back to the raw pushed identity (<see langword="null"/>).
/// </summary>
public sealed class GitIdentityResolverTests : IDisposable
{
    private const string DefaultHostDomain = ContentAuthorshipOptions.DefaultHostDomain;

    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly GitEmailService _gitEmails;

    public GitIdentityResolverTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _gitEmails = new GitEmailService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private GitIdentityResolver CreateResolver(string hostDomain = DefaultHostDomain) =>
        new(Options.Create(new ContentAuthorshipOptions { HostDomain = hostDomain }), _gitEmails, _db);

    private AccountGitAuthorFactory CreateAuthorFactory(string hostDomain = DefaultHostDomain) =>
        new(Options.Create(new ContentAuthorshipOptions { HostDomain = hostDomain }));

    [Fact]
    public async Task Synthetic_username_shape_resolves_to_its_owning_account()
    {
        var alice = await AddAccountAsync("alice");

        var resolved = await CreateResolver().ResolveAsync($"alice@{DefaultHostDomain}");

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.AccountId);
        Assert.Equal("alice", resolved.Username);
    }

    [Fact]
    public async Task Synthetic_id_shape_resolves_to_its_owning_account()
    {
        // ".old.name." is not a legal dot-atom (LegacyUsernameThatIsNotALegalDotAtom_GetsTheDeterministicFallback),
        // so AccountGitAuthorFactory would author it under the account+<id> fallback shape.
        var legacy = await AddAccountAsync(".old.name.");

        var resolved = await CreateResolver().ResolveAsync($"account+{legacy.Id:N}@{DefaultHostDomain}");

        Assert.NotNull(resolved);
        Assert.Equal(legacy.Id, resolved.AccountId);
        Assert.Equal(".old.name.", resolved.Username);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ab")] // legal dot-atom but too short for D11 -- a legacy account can still hold it
    [InlineData(".old.name.")] // not a legal dot-atom -- exercises the id-shape branch below
    [InlineData("legacy.")] // trailing-dot-only -- not a legal dot-atom either
    public async Task RoundTrips_through_the_real_outbound_factory_for_every_username_shape(string username)
    {
        // The load-bearing property from D19 §5: whatever AccountGitAuthorFactory actually stamps
        // outbound, this resolver must recognise inbound, driven through the real factory rather than
        // a hand-reconstructed expectation of what it produces -- so the two can never silently agree
        // on paper while disagreeing in code.
        var account = await AddAccountAsync(username);
        var factory = CreateAuthorFactory();
        var author = factory.CreateAuthor(new AuthenticatedAccount(account.Id, account.Username, IsAdministrator: false));

        var resolved = await CreateResolver().ResolveAsync(author.Email);

        Assert.NotNull(resolved);
        Assert.Equal(account.Id, resolved.AccountId);
    }

    [Fact]
    public async Task A_squatted_GitEmails_row_loses_to_the_synthetic_username_shape()
    {
        // The security property this ordering exists for: bob registers alice's bare synthetic
        // address as one of his own git emails via /account. A push carrying that exact address must
        // still attribute to alice, never to bob.
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        Assert.Equal(GitEmailAddOutcome.Added, await _gitEmails.AddAsync(bob.Id, $"alice@{DefaultHostDomain}"));

        var resolved = await CreateResolver().ResolveAsync($"alice@{DefaultHostDomain}");

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.AccountId);
        Assert.NotEqual(bob.Id, resolved.AccountId);
    }

    [Fact]
    public async Task A_squatted_GitEmails_row_loses_to_the_synthetic_id_shape()
    {
        var legacy = await AddAccountAsync(".old.name."); // not a legal dot-atom -> id-shape address
        var attacker = await AddAccountAsync("attacker");
        var syntheticAddress = $"account+{legacy.Id:N}@{DefaultHostDomain}";
        Assert.Equal(GitEmailAddOutcome.Added, await _gitEmails.AddAsync(attacker.Id, syntheticAddress));

        var resolved = await CreateResolver().ResolveAsync(syntheticAddress);

        Assert.NotNull(resolved);
        Assert.Equal(legacy.Id, resolved.AccountId);
        Assert.NotEqual(attacker.Id, resolved.AccountId);
    }

    [Fact]
    public async Task An_ordinary_registered_git_email_still_resolves_when_neither_synthetic_shape_matches()
    {
        var alice = await AddAccountAsync("alice");
        Assert.Equal(GitEmailAddOutcome.Added, await _gitEmails.AddAsync(alice.Id, "alice@example.com"));

        var resolved = await CreateResolver().ResolveAsync("alice@example.com");

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.AccountId);
    }

    [Fact]
    public async Task Synthetic_id_shape_on_a_foreign_domain_is_not_treated_as_synthetic()
    {
        var alice = await AddAccountAsync("alice");

        var resolved = await CreateResolver()
            .ResolveAsync($"account+{alice.Id:N}@elsewhere.example");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Synthetic_username_shape_on_a_foreign_domain_is_not_treated_as_synthetic()
    {
        await AddAccountAsync("alice");

        var resolved = await CreateResolver().ResolveAsync("alice@elsewhere.example");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task An_illegal_dot_atom_localpart_never_matches_the_bare_username_shape_even_if_a_username_equals_it()
    {
        // ".old.name." is a real, storable Username (legacy accounts predate D11) but is not itself a
        // legal dot-atom, so D19 §5 forbids matching it via the bare-username shape even though a live
        // account's Username literally equals this localpart.
        await AddAccountAsync(".old.name.");

        var resolved = await CreateResolver().ResolveAsync($".old.name.@{DefaultHostDomain}");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task An_id_shape_address_naming_no_live_account_falls_through_to_GitEmails_then_raw()
    {
        var deletedAccountId = Guid.NewGuid();
        var address = $"account+{deletedAccountId:N}@{DefaultHostDomain}";

        Assert.Null(await CreateResolver().ResolveAsync(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@no-local-part.example.com")]
    [InlineData("no-domain@")]
    public async Task A_malformed_or_missing_address_resolves_to_null_rather_than_throwing(string? malformed)
    {
        Assert.Null(await CreateResolver().ResolveAsync(malformed));
    }

    [Fact]
    public async Task An_unknown_email_resolves_to_null()
    {
        await AddAccountAsync("alice");

        Assert.Null(await CreateResolver().ResolveAsync("nobody@example.com"));
    }

    [Theory]
    [InlineData("host.example.com")]
    [InlineData("wiki.internal")]
    public async Task HostDomain_IsWhateverConfigurationSays_NeverHardcoded(string domain)
    {
        var alice = await AddAccountAsync("alice");

        var resolved = await CreateResolver(domain).ResolveAsync($"alice@{domain}");

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.AccountId);
    }

    private async Task<Account> AddAccountAsync(string username)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "$argon2id$stub",
            DisplayName = username,
            CreatedAt = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.Zero),
        };

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        return account;
    }
}
