using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Exercises <see cref="GitEmailService"/> against the real EF Core migration on an in-memory
/// SQLite connection, so the uniqueness and ownership checks (AD24) run through the actual
/// <c>NOCASE</c> unique index rather than an in-memory stand-in.
/// </summary>
public sealed class GitEmailServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly GitEmailService _service;

    public GitEmailServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _service = new GitEmailService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_address_is_added_to_the_caller_and_listed_back()
    {
        var alice = await AddAccountAsync("alice");

        var outcome = await _service.AddAsync(alice.Id, "alice@example.com");

        Assert.Equal(GitEmailAddOutcome.Added, outcome);
        var listed = Assert.Single(await _service.ListAsync(alice.Id));
        Assert.Equal("alice@example.com", listed.Email);
    }

    [Fact]
    public async Task Adding_the_same_address_again_on_the_same_account_reports_it_is_already_there()
    {
        var alice = await AddAccountAsync("alice");
        await _service.AddAsync(alice.Id, "alice@example.com");

        var outcome = await _service.AddAsync(alice.Id, "alice@example.com");

        Assert.Equal(GitEmailAddOutcome.AlreadyOnThisAccount, outcome);
        Assert.Single(await _service.ListAsync(alice.Id));
    }

    [Fact]
    public async Task An_address_already_on_another_account_is_refused_without_naming_the_owner()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        await _service.AddAsync(alice.Id, "shared@example.com");

        var outcome = await _service.AddAsync(bob.Id, "shared@example.com");

        Assert.Equal(GitEmailAddOutcome.TakenByAnotherAccount, outcome);
        Assert.Empty(await _service.ListAsync(bob.Id));

        // AD24's bound (name that it is taken, never to whom) is structural at this layer, not
        // something a runtime assertion here can exercise: GitEmailAddOutcome is a bare enum with
        // no field anywhere in its shape that could carry an account identifier, so there is
        // nothing for `outcome` to leak and nothing for an assertion against it to catch — a check
        // here would pass no matter what AddAsync did, which is not a check. The bound is actually
        // exercised at the page, against the response bob receives:
        // AccountPageTests.An_email_already_on_another_account_is_refused_by_the_real_reason_and_names_no_owner
        // asserts alice's username is absent from the whole rendered body.
    }

    [Fact]
    public async Task Matching_is_case_insensitive_through_the_stored_collation()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        await _service.AddAsync(alice.Id, "Shared@Example.com");

        Assert.Equal(GitEmailAddOutcome.TakenByAnotherAccount, await _service.AddAsync(bob.Id, "shared@example.com"));
        Assert.Equal(
            GitEmailAddOutcome.AlreadyOnThisAccount,
            await _service.AddAsync(alice.Id, "SHARED@EXAMPLE.COM"));
    }

    [Fact]
    public async Task An_address_is_trimmed_before_it_is_stored_or_compared()
    {
        var alice = await AddAccountAsync("alice");

        Assert.Equal(GitEmailAddOutcome.Added, await _service.AddAsync(alice.Id, "  alice@example.com  "));

        var listed = Assert.Single(await _service.ListAsync(alice.Id));
        Assert.Equal("alice@example.com", listed.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@no-local-part.example.com")]
    [InlineData("no-domain@")]
    [InlineData("two@ats@example.com")]
    public async Task A_malformed_address_is_refused_without_reaching_the_store(string? malformed)
    {
        var alice = await AddAccountAsync("alice");

        Assert.Equal(GitEmailAddOutcome.Malformed, await _service.AddAsync(alice.Id, malformed));
        Assert.Empty(await _service.ListAsync(alice.Id));
    }

    [Fact]
    public async Task An_address_longer_than_the_column_is_refused()
    {
        var alice = await AddAccountAsync("alice");
        var tooLong = new string('a', GitEmailService.MaximumEmailLength - "@example.com".Length + 1) + "@example.com";

        Assert.Equal(GitEmailAddOutcome.Malformed, await _service.AddAsync(alice.Id, tooLong));
    }

    [Fact]
    public async Task An_address_owned_by_another_account_cannot_be_removed()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        await _service.AddAsync(alice.Id, "alice@example.com");
        var emailId = Assert.Single(await _service.ListAsync(alice.Id)).Id;

        Assert.False(await _service.RemoveAsync(bob.Id, emailId));
        Assert.Single(await _service.ListAsync(alice.Id));
    }

    [Fact]
    public async Task Removing_an_unknown_email_reports_no_match()
    {
        var alice = await AddAccountAsync("alice");

        Assert.False(await _service.RemoveAsync(alice.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Adding_under_an_already_cancelled_token_throws_and_leaves_no_email()
    {
        // AddAsync is a bare Add + SaveChangesAsync, not transactional (F1/D1) — but its first
        // cancellable await is FindByEmailAsync's uniqueness check, before the insert, so a
        // pre-cancelled call throws before SaveChangesAsync ever writes and leaves no row. This is
        // the pre-write window only; it says nothing about a cancellation observed after the write
        // commits.
        var alice = await AddAccountAsync("alice");

        var cancellationToken = new CancellationToken(canceled: true);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.AddAsync(alice.Id, "alice@example.com", cancellationToken));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);

        Assert.Empty(await _service.ListAsync(alice.Id));
    }

    [Fact]
    public async Task Removing_under_an_already_cancelled_token_throws()
    {
        // Deliberately the opposite of D1's guarantee that removal survives a disconnect: that
        // guarantee is a property of the caller, which passes CancellationToken.None (§3), not of
        // this method, which correctly honours whatever token it is given. This proves the
        // parameter is live — 4.5's sweep is what proves every caller passes None to it.
        var alice = await AddAccountAsync("alice");
        await _service.AddAsync(alice.Id, "alice@example.com");
        var emailId = Assert.Single(await _service.ListAsync(alice.Id)).Id;

        var cancellationToken = new CancellationToken(canceled: true);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RemoveAsync(alice.Id, emailId, cancellationToken));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);

        // The association stays exactly as added: the throw happened before anything was removed.
        Assert.Single(await _service.ListAsync(alice.Id));
    }

    [Fact]
    public async Task The_last_email_on_an_account_can_be_removed()
    {
        // The account model allows "zero associated git emails" explicitly; nothing here may
        // invent a must-keep-one rule the spec does not state.
        var alice = await AddAccountAsync("alice");
        await _service.AddAsync(alice.Id, "alice@example.com");
        var emailId = Assert.Single(await _service.ListAsync(alice.Id)).Id;

        Assert.True(await _service.RemoveAsync(alice.Id, emailId));
        Assert.Empty(await _service.ListAsync(alice.Id));
    }

    [Fact]
    public async Task Removing_an_email_frees_the_address_for_another_account()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        await _service.AddAsync(alice.Id, "shared@example.com");
        var emailId = Assert.Single(await _service.ListAsync(alice.Id)).Id;

        Assert.True(await _service.RemoveAsync(alice.Id, emailId));

        Assert.Equal(GitEmailAddOutcome.Added, await _service.AddAsync(bob.Id, "shared@example.com"));
    }

    [Fact]
    public async Task A_member_does_not_see_another_members_emails()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        await _service.AddAsync(alice.Id, "alice@example.com");

        Assert.Empty(await _service.ListAsync(bob.Id));
    }

    [Fact]
    public async Task A_known_email_resolves_to_its_owning_account()
    {
        var alice = await AddAccountAsync("alice");
        await _service.AddAsync(alice.Id, "alice@example.com");

        var resolved = await _service.FindByEmailAsync("alice@example.com");

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.AccountId);
        Assert.Equal("alice", resolved.Username);
    }

    [Fact]
    public async Task An_unknown_email_reports_no_match_rather_than_an_error()
    {
        var alice = await AddAccountAsync("alice");
        await _service.AddAsync(alice.Id, "alice@example.com");

        Assert.Null(await _service.FindByEmailAsync("nobody@example.com"));
    }

    [Fact]
    public async Task An_email_stored_with_different_case_still_resolves()
    {
        // Supervisor finding S1: the NOCASE collation is the authority, not a C#-side ToLower().
        var alice = await AddAccountAsync("alice");
        await _service.AddAsync(alice.Id, "Alice@x.com");

        var resolved = await _service.FindByEmailAsync("alice@x.com");

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.AccountId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_email_resolves_to_nothing(string? email)
    {
        Assert.Null(await _service.FindByEmailAsync(email));
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
