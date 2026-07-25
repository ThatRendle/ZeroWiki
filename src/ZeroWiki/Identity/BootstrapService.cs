using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Identity;

/// <summary>
/// The one-time first-administrator bootstrap: it resolves the chicken-and-egg of an
/// invite-only system (an invitation needs an inviter) and becomes inert the instant any
/// account exists, leaving no permanent privileged path.
/// </summary>
public sealed class BootstrapService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Whether the bootstrap path is currently open. Evaluated against the store on every
    /// call and deliberately never cached: an account created a moment ago has to close this
    /// path immediately, without a restart. Any caching here would be a privileged backdoor
    /// held open for the life of the process.
    /// </summary>
    /// <remarks>
    /// The existence check must stay non-materialising. It compiles to
    /// <c>SELECT EXISTS(SELECT 1 FROM Accounts)</c>, so no column is read back — which is what
    /// stops an unreadable column on some row making a populated store look empty and
    /// re-opening the bootstrap.
    /// </remarks>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        !await db.Accounts.AnyAsync(cancellationToken);

    /// <summary>
    /// Creates the first administrator account, or reports that one already exists.
    /// </summary>
    /// <remarks>
    /// "Exactly one administrator" is a concurrency requirement, not just a logic one. Checking
    /// and inserting inside SQLite's default deferred transaction is not enough: the read takes
    /// no write lock, so two simultaneous callers can both observe an empty store and both
    /// inserts then succeed. Taking the write lock up front (<c>BEGIN IMMEDIATE</c>, which is
    /// what <c>deferred: false</c> issues) serialises the pair, so the second caller reads the
    /// first caller's committed account and refuses.
    /// </remarks>
    public async Task<BootstrapOutcome> CreateFirstAdministratorAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var trimmedUsername = username.Trim();

        // Completing the guard above rather than adding ceremony: it would be incoherent for a
        // blank username to be a caller error while "a:b" — the value with a real consequence,
        // since §8 presents the username as the Basic-auth userid — is persisted silently. The
        // check is on the trimmed value and sits before the hash, so it cannot become another
        // way to spend 64 MiB on a request that is going to be rejected.
        if (trimmedUsername.Length > CredentialPolicy.MaximumUsernameLength
            || !CredentialPolicy.UsernameMatcher().IsMatch(trimmedUsername))
        {
            throw new ArgumentException(CredentialPolicy.UsernameRuleDescription, nameof(username));
        }

        // A policy number rather than a structural invariant, and enforced here for one reason:
        // this call mints the only account created with no invitation, no authentication and no
        // audit trail, and nothing in this system can reset a password afterwards. A weak first
        // administrator password is permanent. That combination — most privileged record,
        // irreversible mistake — is the exception; it is not a licence to push every product
        // number down into every service.
        if (password.Length < CredentialPolicy.MinimumPasswordLength)
        {
            throw new ArgumentException(CredentialPolicy.MinimumPasswordLengthRuleDescription, nameof(password));
        }

        // Advisory pre-filter, not the decision. /bootstrap stays anonymously reachable for the
        // life of a deployment, so without this every POST to an already-bootstrapped instance
        // would derive — and discard — a 64 MiB Argon2id hash. Refusing early costs a single
        // EXISTS query; the authoritative check is still the one under the write lock below, and
        // moving that one up here instead of adding this one would reopen the two-writers race.
        if (await db.Accounts.AnyAsync(cancellationToken))
        {
            return BootstrapOutcome.AlreadyBootstrapped;
        }

        // Hashed before the write lock is taken, never inside it: Argon2id at these parameters
        // costs ~100 ms, and holding the store's single write lock for that long would serialise
        // every other writer behind a CPU burn.
        var passwordHash = passwordHasher.Hash(password);

        // Opened through EF so its own open-count bookkeeping stays straight; the raw connection
        // is still needed below because only the non-async overload accepts `deferred`.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();

        // No async overload takes `deferred`, and EF's own BeginTransactionAsync() would give us
        // a deferred transaction, which is precisely the one that does not hold.
        await using var writeLock = connection.BeginTransaction(deferred: false);

        // Enlisted, then committed and disposed through EF's own wrapper. Committing the raw
        // SqliteTransaction directly would leave the context still associated with it, and the
        // next query on this context would fail against a disposed transaction. The result is
        // only ever null when null is passed in.
        var transaction = await db.Database.UseTransactionAsync(writeLock, cancellationToken)
            ?? throw new InvalidOperationException("Could not enlist the bootstrap write transaction.");

        await using (transaction)
        {
            if (await db.Accounts.AnyAsync(cancellationToken))
            {
                return BootstrapOutcome.AlreadyBootstrapped;
            }

            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = trimmedUsername,
                PasswordHash = passwordHash,
                DisplayName = trimmedUsername,
                IsAdministrator = true,
                CreatedAt = timeProvider.GetUtcNow(),
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return BootstrapOutcome.Created;
        }
    }
}
