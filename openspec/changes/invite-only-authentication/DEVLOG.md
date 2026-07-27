# DEVLOG — invite-only-authentication

The shared working channel for this change. Attributed, append-only posts grouped by `## N.` section,
mirroring `tasks.md`. `## NEXT` (pinned at bottom) is the only part rewritten.

## Architecture decisions (Architect, pinned)

Greenfield repo — this change scaffolds the whole solution. Binding calls made with the Product Owner:

- **AD1 — Solution layout.** `ZeroWiki.slnx` at repo root (the .NET 10 `dotnet new sln` default XML
  format — Architect-accepted over the literal `.sln`, functionally equivalent across build/test/CI);
  app in `src/ZeroWiki/` (ASP.NET Core 10
  Blazor Web App, **Static SSR** render mode per design D-context / content-core D7); tests in
  `tests/ZeroWiki.Tests/` (xUnit). Target framework `net10.0`.
- **AD2 — Data access = EF Core + SQLite** (`Microsoft.EntityFrameworkCore.Sqlite`). Confirmed with
  Product Owner. Schema via EF migrations; `Database.Migrate()` on startup. Transactions cover
  invite-redemption and token-revocation (design D6).
- **AD3 — Argon2id via `Konscious.Security.Cryptography`** (pure-managed, no native dep). Confirmed
  with Product Owner. Passwords only.
- **AD4 — Token hashing.** Git access tokens and invitation tokens are **high-entropy random**
  values; they are hashed at rest with **SHA-256** (fast hash is correct for high-entropy secrets —
  Argon2 is reserved for low-entropy passwords per AD3). Plaintext shown once, never stored. Storing
  invitation tokens hashed too (not just git tokens) keeps the store free of usable secrets (design
  D6 intent), even though the spec only mandates it for git tokens.
- **AD5 — Store location.** SQLite file path is configurable (`ConnectionStrings:IdentityDb` /
  env), defaulting to a data directory under the mounted volume, **separate from the content git
  repo** (design D6). Dev default is a local path under the app.
- **AD6 — Admin/member distinction.** Persist a single `IsAdministrator` boolean on the account
  (design D2 + Open Question re: roles). No broader role model this change.
- **AD14 — Invitation lifetime is 7 days. Product Owner's decision (2026-07-26).** The spec bounds
  the lifetime ("expires after a bounded time") but never names the bound, so like AD10 the number
  is the Product Owner's, not the Architect's. Rationale as offered and accepted: a week survives a
  weekend and a missed message, while keeping a link that leaks into a chat backlog from staying
  live indefinitely. **The number lives beside AD10 in `CredentialPolicy`'s sibling — an invitation
  policy constant — not inline at a call site**, for the same reason AD10 does: the value is quoted
  in user-facing copy ("this link expires in 7 days") and in the expiry test, and those three must
  not drift. Expiry is computed **once at issue** (`CreatedAt + 7 days` persisted into `ExpiresAt`),
  never re-derived at redemption from a constant that may since have changed.
- **AD15 — An invitation is visible and revocable to its issuer, and to any administrator. Product
  Owner's decision (2026-07-26).** The spec says a "member" issues and that an unused invitation
  can be revoked, but is silent on by whom; this closes it. Members see and revoke their own;
  administrators see and revoke all. Two consequences that bind the implementation: **listing is
  scoped in the query, not filtered in the view** (`WHERE IssuerAccountId = @me` unless the caller
  is an administrator) — a view-level filter leaks through any future JSON/partial surface; and the
  revoke path **takes the caller's identity as a parameter and authorises inside the service**,
  exactly as `GitTokenService.RevokeAsync(accountId, tokenId)` already does, so a route that forgets
  to check cannot revoke someone else's invitation. **This also answers `design.md`'s open question**
  ("whether the admin/member distinction needs any persisted role beyond 'can issue invitations'"):
  it does not — `IsAdministrator` (AD6) carries the whole distinction §4 needs.
- **AD16 — the four authorization additions all stay, and two of them are load-bearing. Architect's
  ruling (2026-07-26),** closing @worker's Block 4a ❓. **Measured, twice, independently** — the
  first measurement was wrong and is retracted in §4's thread; these numbers are the reviewer's,
  reproduced by the worker under a `shasum` landing guard:
  - `AddAuthorization()` — **required.** Without it every request to an `[Authorize]` page is a 500.
  - `app.UseAuthorization()` — **required, and its position is the point.** `WebApplication`
    auto-inserts the middleware at the *front* of the pipeline, ahead of the explicit
    `UseAuthentication()`, where it evaluates `[Authorize]` against a not-yet-authenticated `User`
    and 302s **every signed-in member** to `/login`. The explicit call placed *after*
    `UseAuthentication()` is the only reason a logged-in member reaches the page.
  - `AuthorizeRouteView` and `AddCascadingAuthenticationState()` — **inert today, kept deliberately.**
    §6 and §7 render `AuthorizeView`, which needs the cascading state; `AuthorizeRouteView` keeps the
    renderer and the endpoint from being able to disagree about the same `[Authorize]`. Do not delete
    them as dead code in §6 — this is the ruling that says why they are there.

  **The lesson is worth more than the ruling.** Had the two required lines been dropped as
  "redundant", **both anonymous tests would have stayed green** while the site was broken for every
  authenticated user: the tests assert that anonymous is denied, and the breakage denies *everyone*.
  A suite can be green and still be describing a broken system — §5 found this in its own tests, and
  §4 found it again in the experiment built to prevent it (a mutation script whose `\n` could not
  match `Program.cs`'s CRLF silently mutated nothing, and a no-op mutation is indistinguishable from
  a surviving mutant unless you check the edit landed). **Any mutation run in this repo verifies the
  file actually changed before believing the result.**
- **AD17 — a failed redemption names its reason, except for a token that matches nothing. Product
  Owner's decision (2026-07-26).** "Expired" / "already used" / "revoked" are told to the invitee; a
  token that resolves to **no stored row** gets a single uniform "this invitation link is not valid".
  **Why this does not contradict §5's uniform login error, which still stands in full:** §5's
  requirement is *no username enumeration*, and a username is a low-entropy value an attacker can
  guess. An invitation token is high-entropy and SHA-256-hashed at rest (AD4), so anyone shown a
  reason has already **proven possession of a real token** — there is nothing left to enumerate, and
  the reason is a fact about a secret they demonstrably hold. **This is the boundary, and it binds:**
  the three named reasons are reachable **only after** the presented token has matched a stored hash.
  Deriving a reason from anything an unauthenticated caller can supply *without* a matching token
  would reintroduce exactly the oracle §5 closed. Rationale for naming them at all: a genuine invitee
  whose link expired must be able to tell that from a typo, or they retry instead of asking for a new
  link.
- **AD18 — redemption creates the account; it does not establish a session. Product Owner's decision
  (2026-07-26).** On success the invitee is redirected to `/login` to sign in with the credentials
  they just chose. **§5's login stays the only route in the system that mints a session**, which is
  the whole point: the uniform-failure behaviour, the dummy-hash timing equalisation and the
  three-way server-side logging (AD8) all live there and are tested there. A second session-minting
  path would either duplicate those properties or quietly not have them. It costs the invitee one
  extra step, and makes that step the hardened one.
- **AD19 — a test must assert that the condition it is named for actually occurred, not infer it from
  the outcome. Architect's ruling (2026-07-27),** generalising AD16 after Block 4b hit the same
  mistake in three different disguises. Every one produced **a green suite describing something other
  than the system**:
  1. **A mutation that never landed** (4a) — the script's `\n` could not match a CRLF file, so three
     mutants modified nothing and were reported as "tests survived". Fix: checksum before/after.
  2. **A race that stopped happening** (4b/B1) — eight redemption attempts released by
     `SetResult()` onto a saturated thread pool drained near-serially, so the losers refused at the
     *pre-lock* check and the race never occurred. `deferred: false → true` — reverting the entire
     write-lock discipline — passed the full `dotnet test` **0/4 caught**. Fix: make the rendezvous
     **positional, not temporal** — park every attempt at a known point in the code (here, the seam
     between the pre-lock read and `BEGIN IMMEDIATE`), on dedicated threads, so starvation can only
     make the test *time out and fail loudly*. Now caught **3/3, no variance**.
  3. **A seam that quietly moved** (4b/N9) — the revoke test hooked the clock read between the
     revocability decision and the write. Adding a harmless `_ = timeProvider.GetUtcNow();` at the top
     of the method — the shape an ordinary refactor produces — moved the seam, and the test's XOR was
     then satisfied on its *other* branch, so the real N2 regression **passed 3/3**. Fix: assert
     inside the hook that the interleaving happened (`Assert.False(redemption.Wait(…))`), so the test
     now *refuses to run* against a seam it cannot find, and names the line that moved it.

  **The common shape:** each test knew what the *outcome* should be and never checked that the
  *situation* it was written for had arisen. Note that the code was correct in all three cases — this
  is not about finding bugs, it is about whether the proof would still fail if the bug returned. **A
  filtered test run is not evidence**: B1 was caught under a filter and invisible under the full
  `dotnet test`, which is the gate the workflow actually runs. Verify mutants under the full suite.

## 1. Identity store

_[architect] → @worker — Block 1 (tasks 1.1–1.4 + scaffolding). Brief posted; see thread below._

[worker] Block 1.1–1.4 implemented. Summary:

- **Scaffold.** `dotnet new blazor --interactivity None` (Static SSR, no interactive render mode
  registered at all — no `AddInteractiveServerComponents`/`AddInteractiveWebAssemblyComponents` in
  `Program.cs`). Removed `Weather.razor` and its NavMenu link; left `Home.razor` untouched for Block 6.
  `dotnet new xunit` for `tests/ZeroWiki.Tests`, project reference added to `src/ZeroWiki`.
- **Solution file.** `dotnet new sln` on this SDK produces `ZeroWiki.slnx` (the new XML solution
  format), not a classic `.sln` — that's the .NET 10 tooling default, functionally equivalent
  (`dotnet build`/`test`/`sln add` all work against it transparently). Flagging in case the Architect
  wants a classic `.sln` instead; otherwise no action needed.
- **Schema (EF Core + SQLite, AD2).** `ZeroWiki.Data`: `Account`, `GitEmail`, `Invitation`, `GitToken`
  entities per the brief's shape, each with an `IEntityTypeConfiguration<T>` under `Data/Configurations/`.
  `Username` uses SQLite `COLLATE NOCASE` (`.UseCollation("NOCASE")`) so the unique index is
  case-insensitive per spec. `GitEmail.Email` and both `TokenHash` columns are unique but
  case-sensitive (no case-fold requirement in the brief/spec for those). Cascade delete
  Account→GitEmail/GitToken; `Invitation.IssuerAccountId` FK is `Restrict` (deleting an issuing account
  isn't a modeled scenario, and restricting avoids silently orphaning invitation history).
  `PasswordHash`/`TokenHash` are opaque `string` columns — no hashing logic here, that's Block 2.
- **Migration.** `dotnet ef migrations add InitialIdentitySchema` — one migration, all four tables +
  five unique/FK indexes. Verified by running it, not just generating it (see below).
- **Startup provisioning (1.1, AD5).** `Data/IdentityDbStartupExtensions.cs`:
  `AddIdentityDb(IConfiguration)` reads `ConnectionStrings:IdentityDb` (throws if missing — no silent
  default-to-nowhere), creates the containing directory via `SqliteConnectionStringBuilder.DataSource`
  before `AddDbContext` runs, and `MigrateIdentityDbAsync()` runs `Database.MigrateAsync()` once in
  `Program.cs` before `app.Run()`. Dev default in `appsettings.json`:
  `Data Source=App_Data/identity.db` (relative to content root). Docker/production overrides via the
  standard ASP.NET Core env-var convention `ConnectionStrings__IdentityDb=Data Source=/data/identity.db`
  — no bespoke env-fallback code needed, `IConfiguration` already layers env vars over appsettings.
  **Smoke-tested for real**: ran the app, confirmed `App_Data/identity.db` is created on first run with
  all 4 tables + `__EFMigrationsHistory`, and that `Accounts.Username` has `COLLATE NOCASE`; then cleaned
  up the artifact (gitignored, not in the tree).
- **NuGet vulnerability.** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.10 transitively pulls
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which trips `NU1903` (CVE-2025-6965, SQLite < 3.50.2 memory
  corruption). Pinned `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 as a direct `PackageReference` in
  `ZeroWiki.csproj` to resolve the transitive version up — warning is gone, build is 0 warnings.
- **Tests** (`tests/ZeroWiki.Tests/Data/IdentityDbContextTests.cs`, 6 tests): each test opens a fresh
  `Data Source=:memory:` `SqliteConnection`, runs the real `Database.Migrate()` (not `EnsureCreated`) so
  the generated migration is what's under test. Covers: full graph round-trip (Account + GitEmails +
  GitTokens + Invitation, reloaded via a second `IdentityDbContext` on the same connection), duplicate
  username rejected, duplicate username rejected case-insensitively (`alice` vs `ALICE` — proves the
  NOCASE collation), duplicate git email rejected, duplicate git token hash rejected, and cascade delete
  (removing an Account removes its GitEmails/GitTokens).
- **`.gitignore`** added at repo root — `bin/`/`obj/`, `*.db`/`*.db-shm`/`*.db-wal`, `App_Data/`.

**Gates:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — 6/6 passed. `dotnet format
--verify-no-changes` — clean (exit 0).

**For @reviewer to scrutinise:**
1. `.slnx` vs `.sln` — flagged above, not corrected unilaterally since the brief said `.sln` explicitly.
2. `GitEmail.Email` / `TokenHash` uniqueness is case-sensitive — only `Username` got NOCASE per the
   explicit "treat case-insensitively" instruction. If git emails should also case-fold, that's a
   one-line addition to `GitEmailConfiguration`.
3. `Invitation.IssuerAccountId` delete behavior chosen as `Restrict` (not in the brief) — my own call,
   reasoned above.
4. `SQLitePCLRaw.bundle_e_sqlite3` pinned to 2.1.12 (not the newer 3.50.x/3.53.x line) to stay a minor
   bump over what EF Core 10.0.10 already resolves; wanted the smallest change that clears the CVE.

→ @reviewer

[reviewer] Block 1.1–1.4 audited. Verdict: **Request changes** — one blocking schema finding; everything
else clean. Gates re-run independently and confirmed green: `dotnet build` 0 warn/0 err (NU1903 gone),
`dotnet test` 6/6, `dotnet format --verify-no-changes` exit 0. Schema, migration, snapshot, and startup
provisioning all line up; unique indexes (Username NOCASE, GitEmail.Email, both TokenHash columns), FKs,
cascade/restrict, and nullable state timestamps match the intended shape. AD2/AD4/AD5/AD6 and Static SSR
(no interactive render mode registered anywhere — only the harmless template `@using static …RenderMode`
in `_Imports.razor`) all compliant. No plaintext secret columns; `.gitignore` covers `*.db*` + `App_Data/`
so nothing secret-bearing can reach the content repo.

**Blocking:**
1. **`GitEmail.Email` uniqueness must be case-insensitive (NOCASE).**
   `GitEmailConfiguration.cs:12-16` — the unique index on `Email` is case-sensitive, so two accounts can
   register `Alice@x.com` and `alice@x.com` as distinct emails. The spec (`specs/user-accounts/spec.md`
   "Account lookup by git email") and `GitEmail.cs:15` both require an email to resolve to **exactly one
   account**; a push author of `ALICE@x.com` would then be ambiguous (or missed) in the Block 8 resolver.
   Bake the invariant into the schema now — add `.UseCollation("NOCASE")` on `Email` and regenerate the
   migration — mirroring how `Username` is already handled (`AccountConfiguration.cs:12-15`). Cheaper here
   than a second migration after Block 8 ships against real data. (Architect may overrule if email
   normalization is instead mandated at every write path, but DB-level NOCASE is the robust, consistent
   choice.)

**Non-blocking / answers to your 4 flagged items:**
- **(1) `.slnx` vs `.sln`** — Non-blocking. `.slnx` is the .NET 10 default and all gates drive it fine; it
  only deviates from AD1's literal `ZeroWiki.sln` wording. Architect's cosmetic call: accept `.slnx`
  (and reword AD1) or ask for a classic `.sln`. No correctness impact.
- **(2) `TokenHash` case-sensitivity** — Keep case-sensitive; correct for hashes. Just ensure Block 2
  emits a consistent case (e.g. lowercase hex) so the exact-match unique index behaves. GitEmail is the
  one that needs NOCASE (see blocker above).
- **(3) `Invitation.IssuerAccountId` = `Restrict`** — Good call, non-blocking. Account deletion isn't a
  modeled scenario this change, and Restrict preserves the invitation audit trail rather than
  cascade-wiping who-invited-whom. Prefer it over Cascade.
- **(4) `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 pin** — Accept, non-blocking. It clears NU1903 with the
  smallest bump over EF's transitive 2.1.11; build confirms 0 warnings. Worth a one-line comment in the
  csproj noting it's a transitive-CVE pin to drop once EF Core 10's transitive catches up.

**Architectural note (not this block):** the scaffolded default `Home.razor` still renders template content
to anonymous visitors — must be locked down under Block 6 (§6.1/§6.2, D5). Out of scope now, flagging so
it isn't forgotten.

→ @worker — add NOCASE to `GitEmail.Email` + regenerate the migration; then I'll re-audit. Everything
else is clean.

[worker] Blocking fix applied (re: 1.2, per reviewer/architect):

- `GitEmailConfiguration.cs` — `Email` now `.UseCollation("NOCASE")`, mirroring `Username`. `TokenHash`
  columns left case-sensitive, as directed.
- Regenerated the migration clean (deleted `InitialIdentitySchema` + snapshot, `dotnet ef migrations
  add InitialIdentitySchema` fresh) — still a single migration; confirmed the generated
  `CreateTable("GitEmails", ...)` now carries `collation: "NOCASE"` on the `Email` column.
- Added `Duplicate_git_email_is_rejected_case_insensitively` (`Alice@x.com` vs `alice@x.com`,
  cross-account) alongside the existing exact-duplicate and username-case-insensitive tests.
- Added the NU1903/CVE-2025-6965 pin-rationale comment above the `SQLitePCLRaw.bundle_e_sqlite3`
  `PackageReference` in `ZeroWiki.csproj`, per architect's note.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — 7/7 passed (was 6, +1 new
case-insensitive email test). `dotnet format --verify-no-changes` — clean (exit 0).

→ @reviewer

[reviewer] Re-audit of the blocker fix — **Approve**. Delta verified:
- `GitEmailConfiguration.cs:12-15` — `.UseCollation("NOCASE")` added to `Email`; `TokenHash` correctly
  left case-sensitive.
- Migration regenerated clean: exactly **one** migration (`20260724174030_InitialIdentitySchema`, old
  timestamp replaced — no stray second migration). `…InitialIdentitySchema.cs:36` carries
  `collation: "NOCASE"` on the `Email` column and `IdentityDbContextModelSnapshot.cs:67-68` shows
  `.UseCollation("NOCASE")` on `GitEmail.Email` feeding the unique index.
- New test `Duplicate_git_email_is_rejected_case_insensitively` (`IdentityDbContextTests.cs:130-144`)
  asserts real behaviour — `Alice@x.com` vs `alice@x.com` across two accounts throws `DbUpdateException`
  against the actual migration.
- `ZeroWiki.csproj:16-19` — transitive-CVE pin-rationale comment added.

Gates re-run independently: `dotnet build` **0 warn / 0 err**, `dotnet test` **7/7 passed**,
`dotnet format --verify-no-changes` **exit 0**. The other 3 flagged items (`.slnx`, `Restrict` FK,
2.1.12 pin) are closed per Architect. No remaining blockers or nits.

**✅ SIGN-OFF — Block 1 (§1.1–1.4 + scaffolding) APPROVED.** Clear to tick 1.1–1.4 and commit.

---

_[architect] → @worker — **§1 amendment unit: AD7, the `DateTimeOffset` storage representation.**
Reopening §1 after sign-off, deliberately. No new tasks — 1.1–1.4 stay ticked; this corrects **how** the
schema they defined stores timestamps. Landing it **before Block 3** rather than just before Block 4, so
§3's bootstrap (which writes `Account.CreatedAt`) is built against the final schema instead of being
churned by a regenerated migration afterwards._

**Why this is happening at all.** Block 2 discovered that `OrderByDescending(t => t.CreatedAt)` throws on
SQLite, and worked around it client-side. The reviewer then verified the whole picture empirically, and
two of the three findings are the reason for this unit:

1. `ORDER BY`, `Max`/`Min`, and `Where(x => x.ExpiresAt > now)` on a `DateTimeOffset` **all throw
   loudly** — EF Core has errored rather than silently client-evaluating a `Where` since 3.0. (This
   killed my original justification, which was a fear of silent client-side evaluation. It was wrong.)
2. **Equality *does* translate, and is offset-sensitive.** Stored form is TEXT
   `'2026-07-25 11:00:00+00:00'`. The same instant written with a different offset does not compare
   equal, and TEXT ordering across mixed offsets is chronologically wrong. Every write goes through
   `TimeProvider.GetUtcNow()` today, so we are correct **by habit, not by schema**.
3. **The obvious fix is the wrong one and fails silently.** EF's built-in
   `DateTimeOffsetToBinaryConverter` restores server-side `>` and `ORDER BY` — and compares *wrongly*,
   because it packs the offset into the value instead of normalising it. Measured, with `now = 12:00Z`, a
   `Where(ExpiresAt > now)` returned a row expiring at `16:00+05:00` (= `11:00Z`) as **unexpired**. That
   is a silent fail-open in the invitation-expiry filter, reachable via
   `HasConversion<DateTimeOffsetToBinaryConverter>()` — the first thing a reasonable implementer tries.
   **Do not use it.**

**The decision (binding).** Store every `DateTimeOffset` as a **fixed-width ISO-8601 UTC string**:

- Format **exactly** `yyyy-MM-ddTHH:mm:ss.fffffffZ` — 7 fixed fractional digits, literal `Z`. **Never**
  the `"o"` round-trip form, which carries a variable offset and breaks fixed-width ordering.
- `HasMaxLength(28)`. No `NOCASE` on these columns.
- Apply via **`ConfigureConventions`** — `configurationBuilder.Properties<DateTimeOffset>().HaveConversion<…>()`
  — **not** per-property `HasConversion`. This is binding: it covers `DateTimeOffset?` too, and it makes
  the invariant impossible to forget on a column added in §4 or later. Per-property configuration
  certainly would be forgotten.
- Round-trip must **normalise** a non-UTC input (a `+05:00` value stores as the correct instant) and keep
  NULL as NULL.

*Why ISO-8601 text over the conventional `long` UTC ticks* — ticks is unambiguous and I considered it. The
deciding argument is the operator: this project sells zero-config self-hosting, so the person debugging
"why was my invite rejected" is in a `sqlite3` shell. `2026-07-25T13:00:00.0000000Z` answers that;
`639205812000000000` does not. More importantly, SQLite's own `datetime()`/`julianday()`/`strftime()`
parse ISO-8601 with `T` and `Z`, so an operator's hand-written `WHERE ExpiresAt > datetime('now')` is
**correct by default** — whereas against ticks the same query needs arithmetic they will get silently
wrong. Designing out a silent-comparison bug class and then leaving the operator's own query inside it
would be incoherent. Ordering correctness is structural, not incidental: fixed width + always-`Z` +
SQLite's default BINARY collation ⇒ lexicographic order **is** chronological order. That is as strong as
ticks, not weaker. The one real cost is a format string to get wrong — and `ParseExact` throws loudly on a
malformed value, which is the right failure direction.

**Tasks**

1. Add the converter and wire it through `ConfigureConventions` on `IdentityDbContext`.
2. **Regenerate** the single `InitialIdentitySchema` migration (delete + `dotnet ef migrations add`, as
   you did for the NOCASE fix) — do **not** stack a second migration. Nothing has shipped, so one clean
   migration is the honest representation; a follow-up migration would be fiction. Confirm exactly one
   migration exists afterwards and that the snapshot agrees.
3. **Remove `GitTokenService.ListAsync`'s materialise-then-sort workaround and its `<remarks>`** — order
   server-side now. This is the one Block 2 file this unit touches, and the reason to do it here: left in
   place, the workaround calcifies into a pattern the rest of the change copies.
4. **Tests that lock the invariant in** — this is the part that makes it worth anything:
   - Server-side ordering works (`OrderByDescending(CreatedAt)` reaches SQL and returns the right order).
   - **A server-side `Where(i => i.ExpiresAt > now)` excludes an expired invitation** — the §4-critical
     one. It must prove the predicate reached SQL, not that a client-side filter got the right answer.
   - A non-UTC input (`+05:00`) round-trips to the correct instant and compares correctly against it —
     i.e. the exact case `DateTimeOffsetToBinaryConverter` got wrong.
   - Ordering is correct across a wide range (year 1 → 9999) and NULL stays NULL (`RevokedAt == null`
     still translates).
   - Existing tests stay green: the whole suite currently runs the real migration, so a bad converter
     shows up immediately.

**Out of scope:** no new entity, no new column, no §3/§4 behaviour, no `tasks.md` changes (1.1–1.4 remain
ticked — this is a correction within them, not new work).

**Gates:** the usual four. Then → @reviewer. This commits as its own commit against §1, with the
amend-a-signed-off-migration reason in the body.

[worker] AD7 implemented. Summary:

- **`Data/Converters/Iso8601UtcDateTimeOffsetConverter.cs`** — `ValueConverter<DateTimeOffset, string>`.
  Writes `value.UtcDateTime.ToString(Format, InvariantCulture)`, reads
  `DateTimeOffset.ParseExact(…, AssumeUniversal | AdjustToUniversal)`. `FormattedLength = 28` is public so
  the max length can't drift from the format.
- **Wired via `ConfigureConventions`** on `IdentityDbContext`:
  `Properties<DateTimeOffset>().HaveConversion<Iso8601UtcDateTimeOffsetConverter>()
  .HaveMaxLength(FormattedLength)`. Confirmed it reaches `DateTimeOffset?` too — the regenerated migration
  gives **all seven** timestamp columns across four tables `TEXT`/`maxLength: 28` (`Account.CreatedAt`,
  `GitToken.CreatedAt`/`RevokedAt`, `Invitation.CreatedAt`/`ExpiresAt`/`RedeemedAt`/`RevokedAt`), and
  there's a test for the nullable case specifically. No `NOCASE` on any of them.
- **Migration regenerated, not stacked.** Deleted `20260724174030_InitialIdentitySchema` + snapshot,
  re-added → exactly one migration, `20260725130232_InitialIdentitySchema`. Verified the NOCASE collations
  from the Block 1 fix survived (`Username`, `GitEmail.Email` both still `collation: "NOCASE"`) and that
  the snapshot agrees.
- **`GitTokenService.ListAsync`** — workaround gone. Now `Where` → `OrderByDescending(t => t.CreatedAt)` →
  `Select(… new GitTokenSummary …)` → `ToListAsync`, all server-side; the `<remarks>` explaining the
  client-side sort is deleted. N3's projection kept.
- **Tests: 6 new (60 total), `Data/DateTimeOffsetStorageTests.cs`.** Where a query has to reach SQL I
  assert on `ToQueryString()` — it only succeeds for a fully translated query, so a client-side filter
  can't pass these by luck:
  - stored form is exactly `2026-07-25T13:00:00.0000000Z`, 28 chars (raw `SELECT`, not through EF);
  - `18:00+05:00` stores as `13:00Z` and reloads as the same instant with a zero offset;
  - **the §4-critical one** — an invitation expiring `16:00+05:00` (= `11:00Z`) is excluded by
    `Where(i => i.ExpiresAt > now)` at `now = 12:00Z`, with `"ExpiresAt" > ` asserted present in the
    generated SQL. This is the exact row the built-in binary converter admitted;
  - ordering is chronological in SQL across `DateTimeOffset.MinValue` → year 9999, with a mixed-offset
    pair (`12:00Z` before `18:00+05:00` = `13:00Z`) in the middle, `ORDER BY` asserted in the SQL;
  - NULL stays NULL — raw `WHERE RevokedAt IS NULL` counts 1, `Where(t => t.RevokedAt == null)`
    translates to `IS NULL` and returns the unrevoked token;
  - nullable `RevokedAt` written as `16:00+05:00` stores as `11:00Z`.

**Negative control (worth knowing, with its caveat).** I temporarily swapped
`HaveConversion<DateTimeOffsetToBinaryConverter>()` in and ran the new file: **all 6 fail**, then restored
and re-verified the wiring. Caveat, stated plainly rather than overclaimed: because the migration was
regenerated for the TEXT representation, some of those failures are representation mismatch rather than
the comparison semantics specifically, so this proves the suite is *coupled to the AD7 representation* —
it is not an independent reproduction of the reviewer's measured fail-open. The reviewer's own measurement
stands as that.

**Judgement calls:**

- **Format literals are quoted** — `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`, not the brief's literal
  `yyyy-MM-ddTHH:mm:ssZ` spelling. Output is byte-identical (asserted against the raw column), but
  unquoted `T`/`Z` rely on "unrecognised characters are copied through", and an unquoted `Z` in a *parse*
  format is the kind of thing that has surprising interactions with `DateTimeStyles`. Quoting makes it
  impossible to read either as a specifier. Say the word if you want the unquoted spelling.
- **`ParseExact` deliberately left to throw** on a malformed column, per the brief's "fails loudly" —
  no `TryParseExact` fallback. Note this is the *opposite* of `Argon2idPasswordHasher.Verify`'s
  absorb-corruption stance, and I think correctly so: a corrupt password hash must not become a
  distinguishable error, whereas a corrupt timestamp is a data-integrity fault with nothing to hide.
  Flagging because the inconsistency is deliberate and might otherwise look accidental.
- **Converter lives in `Data/Converters/`**, mirroring `Data/Configurations/`.
- **No `Max`/`Min` test** — the brief's list didn't ask for one and nothing calls them yet; the reviewer
  verified they work. Happy to add if wanted.

**Nothing awkward turned up.** Specifically: `Properties<DateTimeOffset>()` covering the nullable form
needed no extra call; `HaveMaxLength` composes on the same builder; comparison and ordering translate
without a single hint or raw-SQL escape hatch; and no existing test needed changing — all 54 prior tests
passed unmodified against the regenerated migration, including `GitTokenServiceTests`' round-tripped
`RevokedAt` equality assertions.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **60/60 passed** (54 + 6 new).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0). No new entity, no new column, no §3/§4 behaviour, `tasks.md` untouched.

→ @reviewer

[worker] AD7 nits N1/N2 + the optional companion applied. Both reviewer findings were right, and my
original T4 was weaker than I thought — the pair I chose (`12:00Z` vs `18:00+05:00`) sorts correctly even
with the offset preserved, so it proved less than it looked like it did.

- **N1 — the mixed-offset pair is now adversarial.** `13:00Z` vs `09:00−05:00` (= `14:00Z`). A *negative*
  offset is the point: preserving it makes `"T09"` sort before `"T13"`, inverting a pair whose true order
  is the other way round.
- **N2 — added a pair differing only in fractional digits.** `13:00:00.0000000Z` vs `13:00:00.5000000Z`.
  `.` (0x2E) sorts before `Z` (0x5A), so any representation that drops trailing fractional digits inverts
  them. T4 now carries both pairs, so one test covers both mutation classes.
- **Companion taken** — `Live_invitation_written_with_a_negative_offset_survives_the_sql_predicate`: an
  invitation expiring `09:00−05:00` (= `14:00Z`) must survive `Where(i => i.ExpiresAt > now)` at
  `now = 12:00Z`. The fail-*closed* mirror of the existing fail-open test, same `ToQueryString()` proof.

**Confirmed empirically, not assumed** — built both mutants in turn, ran the file, restored, re-verified:

| Converter | Result for `DateTimeOffsetStorageTests` |
|---|---|
| real (ISO-8601 UTC, fixed width) | **7/7 pass** |
| M1 — offset-preserving (`…fffffffzzz`, still valid ISO-8601 TEXT) | **6 of 7 fail** |
| M3 — variable width (`.FFFFFFF`, UTC-normalised) | **4 of 7 fail** |

The failures are the semantic ones, with the diffs to show it:
- **Under M1**, ordering fails at **pos 1** — the negative-offset row (true position 3) sorts straight
  after `MinValue`; and the new companion returns **0 rows instead of 1**, reproducing the fail-closed
  drop the reviewer measured. The fail-open test also fails. (The NULL test passes, correctly — null
  handling is orthogonal to the representation.)
- **Under M3**, ordering fails at **pos 1** — the sub-second row sorts *before* the whole-second row, the
  `.`-before-`Z` inversion exactly. Both expiry tests **pass** under M3, since M3 does normalise to UTC —
  which is precisely why N2 was needed: T4 is the only test that catches a variable-width representation.

`ParseExact`-throws and the quoted format literals left as they are per the ruling. Noting the reviewer's
lowercase-`z` finding for the record, since it's the sharpest argument for the quoting and I hadn't found
it: unquoted, `Z` is read as the case-insensitive UTC designator, so `…0000000z` would parse fine on read
while sorting after every uppercase-`Z` value in SQL. No converter change, no migration regeneration.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **61/61 passed** (was 60; +1
companion). `openspec validate invite-only-authentication --strict` — valid.
`dotnet format --verify-no-changes` — clean (exit 0). Still exactly one migration.

[reviewer] AD7 audited. Verdict: **Approve** — no blocking findings. The representation is correct, the
regeneration is clean, the convention reaches everything, and — the part I was asked to close — **the test
suite does independently catch a plausible-but-wrong converter**, verified by mutation rather than
argument. Gates re-run independently: `dotnet build` **0 warn / 0 err**, `dotnet test` **60/60**,
`openspec validate invite-only-authentication --strict` **valid**, `dotnet format --verify-no-changes`
**exit 0**.

**Scope confirmed.** Tracked diff is `IdentityDbContext.cs` (+13), the migration pair (old deleted, new
added), the snapshot (+25/−12), and `GitTokenService.cs` (−13/+6). `git status` over `Account.cs`,
`GitToken.cs`, `GitEmail.cs`, `Invitation.cs` and `Data/Configurations/` is **empty** — no entity, no
column, no configuration change. `tasks.md` and `openspec/specs/` untouched. Exactly the amendment
described.

**Item 2 — migration regeneration is clean, and Block 1's collations survived.**
- `src/ZeroWiki/Data/Migrations/` holds **exactly three files**: `20260725130232_InitialIdentitySchema.cs`,
  its `.Designer.cs`, and the snapshot. The old `20260724174030_*` pair is deleted, not orphaned — one
  migration, no stack, no stray second.
- **The invariant I blocked Block 1 over is intact.** `…InitialIdentitySchema.cs:19` carries
  `collation: "NOCASE"` on `Accounts.Username`, `:36` on `GitEmails.Email`, and the snapshot agrees at
  `IdentityDbContextModelSnapshot.cs:47` and `:70` (`.UseCollation("NOCASE")`). That was the real
  regression risk in regenerating rather than stacking, and it didn't bite.
- Snapshot diff is exactly the seven property retypings (`DateTimeOffset`/`DateTimeOffset?` → `string`
  with `HasMaxLength(28)`, `IsRequired()` only on the non-nullable ones) and nothing else. No index, key,
  FK or delete-behaviour drift.

**Item 3 — the convention reaches every column, including nullables.**
- All **seven** timestamp columns are `TEXT`/`maxLength: 28` in the migration: `Accounts.CreatedAt` (`:23`),
  `GitTokens.CreatedAt`/`RevokedAt` (`:56-57`), `Invitations.CreatedAt`/`ExpiresAt`/`RedeemedAt`/`RevokedAt`
  (`:77-80`). Three of those are `DateTimeOffset?`, so `Properties<DateTimeOffset>()` is confirmed to cover
  the nullable form.
- **No `NOCASE` on any of them** — the only two collation lines in the migration are the two intended ones.
  Correct: BINARY is what makes lexicographic order chronological.
- **Nothing left per-property.** `grep -rn 'HasConversion' src/ tests/` finds no hand-written call anywhere
  outside the generated migration/snapshot; the only wiring is
  `IdentityDbContext.cs:27-32`. `HaveMaxLength(Iso8601UtcDateTimeOffsetConverter.FormattedLength)` chaining
  off the same builder is the right touch — the max length cannot drift from the format, and the
  `<summary>` at `:22-26` records *why* it's a convention. That's the binding detail honoured properly.
- `GitTokenService.ListAsync` (`:64-76`) — workaround and `<remarks>` gone, `OrderByDescending` now sits
  before the projection so both reach SQL. Good that this landed with AD7 rather than being left to rot.

**Item 1 — I reproduced my own fail-open case against this converter, and the gap is closed. Verified by
mutation, not inspection.**

The worker was right to be honest that swapping in `DateTimeOffsetToBinaryConverter` changes the
*representation* (TEXT → INTEGER), so those 6 failures don't prove the suite catches wrong *semantics*. So
I built three mutant converters that all store plausible ISO-8601 **TEXT** and ran every one of the 6
shipped assertions against each:

- **M1 — `"o"` round-trip format: valid ISO-8601 text, offset preserved.** This is the same *class* of bug
  as the built-in binary converter, in the representation AD7 actually uses — the most realistic way a
  future hand would get this wrong.
- **M2 — fixed-width, always-`Z`, canonical-*looking*, but every instant shifted +5h.**
- **M3 — ISO-8601 UTC but variable width** (`FFFFFFF` drops trailing zero fractions).

| shipped test | M1 offset kept | M2 shifted | M3 variable width |
|---|---|---|---|
| T1 stored form is fixed-width ISO-8601 UTC | **FAIL** | **FAIL** | **FAIL** |
| T2 non-UTC input normalised | **FAIL** | **FAIL** | **FAIL** |
| **T3 expired invitation excluded by an SQL predicate** | **FAIL (semantic)** | pass | pass |
| T4 ordering across the range | pass | *(setup error)* | pass |
| T5 NULL stays NULL | pass | pass | pass |
| T6 nullable also fixed-width | **FAIL** | **FAIL** | **FAIL** |

**The answer to the question: yes.** `Expired_invitation_is_excluded_by_a_predicate_evaluated_in_sql`
(`DateTimeOffsetStorageTests.cs:63-89`) fails under M1 **for the semantic reason, not a representation
mismatch** — it returns **2 rows instead of 1, admitting the expired `16:00+05:00` invitation against a
`12:00Z` now**. That is my measured fail-open, independently reproduced against a converter that stores
perfectly legitimate-looking ISO-8601 text in the AD7 column type. The `ToQueryString()` assertion is what
makes it airtight: the predicate demonstrably reached SQL, so it cannot pass on a lucky client-side
filter. T3 is the load-bearing test of this unit and it earns that position. (M2 illustrates the
complement: a *uniform* shift is order- and comparison-preserving, so no semantic test can catch it — it's
caught only by the exact-string assertions T1/T2/T6. Both kinds of assertion are needed and both are
present.)

**Two residual weaknesses in the ordering test — nits, not blockers, since the class of bug is already
caught by T3 and T1/T2/T6:**
- **N1 — T4's mixed-offset pair is not adversarial.** `12:00Z` vs `18:00+05:00` (= `13:00Z`) sorts
  correctly *even when the offset is preserved*, because `"T12"` < `"T18"` as text. Verified: **T4 passes
  under M1.** A **negative** offset inverts: `13:00Z` vs `09:00−05:00` (= `14:00Z`) — the text reads
  `"T09"` < `"T13"` while the instant is *later*.
- **N2 — no two values differ only in fractional digits**, so variable-width formatting slips past every
  semantic test (verified: T3 *and* T4 both pass under M3; only the exact-string assertions catch it).
  A sub-second pair in the same second closes it: `15:00:00.0000000Z` vs `15:00:00.5000000Z` — under
  variable width the `.` (0x2E) sorts *before* the `Z` (0x5A) and they invert.

I wrote and ran the strengthened version so the recommendation isn't theoretical. Ordering over
`[13:00Z, 09:00−05:00, 15:00:00.000Z, 15:00:00.500Z]` **passes on the real converter and fails under both
M1 and M3.** Folding those two pairs into `Timestamps_order_chronologically_in_sql_across_the_whole_range`
(`:91-113`) is a two-line change that makes the ordering test carry its own weight instead of relying on
T1. Optional companion: a **negative-offset expiry** case — an invitation expiring `09:00−05:00` (=
`14:00Z`) must still be live at `12:00Z`. That's the fail-**closed** mirror of T3 (a live invitation
silently dropped rather than an expired one admitted); verified it returns 0 rows instead of 1 under M1.
T3 covers fail-open; this would cover the other direction.

**The quoted format literals — confirmed strictly more correct, and for a concrete reason.**
`'T'`/`'Z'` vs the brief's unquoted spelling (`Iso8601UtcDateTimeOffsetConverter.cs:31`):
- **Formatting is byte-identical** — both produce `2026-07-25T13:00:00.0000000Z`, 28 chars. Confirmed.
- **Parsing differs on exactly one input**, and it matters: `2026-07-25T13:00:00.0000000z` (**lowercase
  z**) is **rejected** by the quoted format and **accepted** by the unquoted one — because an unquoted `Z`
  is read as the UTC designator, which is case-insensitive, rather than as a literal. Everything else I
  threw at both (offset instead of `Z`, space separator, missing fractional digits, no designator, trailing
  junk) is rejected by both.
- That one difference is a genuine silent-corruption path, not tidiness: a lowercase-`z` value would parse
  *fine* on read while sorting **after every** uppercase-`Z` value in SQL (`z` = 0x7A > `Z` = 0x5A) —
  wrong ordering, no error. The quoted spelling makes the column accept exactly one byte-sequence per
  instant, which is precisely the invariant ordering correctness rests on. **@architect's read is right;
  keep the quoted form** and treat the brief's spelling as superseded.

**❓→ [reviewer] the `ParseExact`-throws asymmetry. Your concern is real — I reproduced it — and there is
a clean answer, which is containment at §5, not a change here.**

Measured against the **real** `IdentityDbContext` and migration, corrupting only `alice`'s
`Accounts.CreatedAt` to `'not-a-timestamp'` via raw SQL:

| operation | result |
|---|---|
| `Accounts.SingleOrDefault(a => a.Username == "alice")` (corrupt row) | **throws** `FormatException: String 'not-a-timestamp' was not recognized as a valid DateTime.` |
| same for `"bob"` (healthy row) | ok |
| same for `"carol"` (no such user) | `null` |
| **projection** `Select(a => new { a.Id, a.Username, a.PasswordHash, a.IsAdministrator })` for alice | **ok — returns the password hash** |
| `Accounts.CountAsync()` / `AnyAsync(a => a.Username == "bob")` | ok |
| `Accounts.ToListAsync()` (materialises every row) | **throws** |
| **`Accounts.AnyAsync()`** (§3's empty-store check) | **ok — `true`** |

So:

1. **Your diagnosis is correct.** If §5 materialises the `Account` entity, login for that one user is a
   500 while "wrong password" and "no such user" both return the uniform response — the blocker-1
   differential-oracle shape, reached from the other side. Not hypothetical; that's the first row.
2. **The answer you were looking for is a projection, and it's free.** The converter only runs for columns
   actually materialised, so a §5 lookup that projects `(Id, Username, PasswordHash, IsAdministrator)` is
   **immune by construction** — verified, it returns the hash off the corrupt row without touching
   `CreatedAt`. And §5 has no business loading `CreatedAt`, `GitEmails` or `GitTokens` to check a password
   anyway, so this is the shape it should have regardless. **This is an AD8 addendum, not an AD7 change:**
   *§5's account lookup projects the columns it needs and never materialises the `Account` entity.*
3. **The asymmetry is defensible as-is and I'd keep it.** The worker's reasoning is principled, not
   accidental: `Verify` absorbs corruption because it is an authentication *decision* on attacker-supplied
   input where every failure must look identical; `ParseExact` throws because a corrupt timestamp is a
   data-integrity fault, and the alternative — absorbing or defaulting — fails **open** on `ExpiresAt` and
   admits expired invitations, which is the entire reason AD7 exists. Same principle, opposite direction,
   correctly applied. **Explicitly do not "fix" this with `TryParseExact` + a default**: that would trade a
   narrow, corruption-only 500 for a silent wrong instant on the security boundary. Loud is right.
4. **Good news worth stating: the invite-only invariant is not exposed.** §3's `Accounts.AnyAsync()`
   doesn't materialise, so a corrupt row **cannot** make the store look empty and re-open the first-admin
   bootstrap. That was the one place this hazard could have been a security problem rather than an
   availability one, and it isn't.
5. **One forward note for §7:** `ToListAsync()` over all accounts *does* throw, so a single corrupt row
   poisons any admin/member list view. Project there too, or accept that a corrupt row breaks that page
   loudly — which is arguably correct for an admin screen. §7's call, flagging so it isn't a surprise.
6. Optional belt for §5, pairing with R5: have the uniform-failure path catch at the boundary and return
   the same uniform failure it would for a wrong password, logging the distinction server-side. That
   covers any future code path that does materialise an entity, without weakening `ExpiresAt`.

**Other observations (non-blocking):**
- `Iso8601UtcDateTimeOffsetConverter.cs:10-23` — the `<remarks>` explaining *why* UTC normalisation is the
  point and naming `DateTimeOffsetToBinaryConverter` as the trap ("Do not swap this for it") is exactly
  the right thing to leave behind. The measured failure is what makes that comment credible; keep it.
- `FormattedLength` as a `public const` consumed by the context is better than the brief's literal `28`.
- No `Max`/`Min` test, correctly — I verified both translate under a converter during the AD7 premise work,
  and nothing calls them yet. Don't add speculative coverage.
- `DateTimeOffset.MinValue`/`MaxValue` in T4 are safe: `UtcDateTime` cannot overflow for any constructible
  `DateTimeOffset`, so the range test can't become flaky.

**✅ SIGN-OFF — AD7 (§1 amendment: `DateTimeOffset` → fixed-width ISO-8601 UTC string) APPROVED.** All four
gates green, one clean migration with Block 1's NOCASE collations intact, convention covers all seven
columns including nullables, no entity/configuration/spec/`tasks.md` change, and the suite independently
catches the fail-open it exists to prevent. Clear to commit. N1/N2 are two lines in the ordering test if
you want them in this commit; the `ParseExact` hazard needs no change here — record the projection
requirement as the AD8 addendum.

## 2. Password & token hashing

_[architect] → @worker — **Block 2 = tasks 2.1–2.3.** Pure service layer over the Block 1 schema. No UI,
no endpoints, no auth middleware this block — those are Blocks 3–7._

**Tasks in scope**

- `2.1` Argon2id password hashing (vetted library, tuned parameters); verify against stored hash.
- `2.2` High-entropy git token generation; store hashed, return plaintext once.
- `2.3` Token verification and revocation.

**Binding spec (verbatim, `specs/authentication/spec.md`)**

> ### Requirement: Per-user git access tokens
> The system SHALL allow an authenticated user to generate one or more git access tokens, store them
> hashed at rest, display each token value only once at creation, and allow the user to revoke a token.
> A revoked token SHALL no longer authenticate.
>
> #### Scenario: Token generated and shown once
> - **WHEN** an authenticated user generates a git access token
> - **THEN** the system stores the token hashed and displays its plaintext value exactly once
>
> #### Scenario: Revoked token stops working
> - **WHEN** a user revokes a git access token
> - **THEN** that token no longer authenticates against the system

> ### Requirement: Credential verification for the git remote
> The system SHALL verify a git-remote credential presented as a username plus a git access token,
> resolve it to the owning account, and reject the request when the token is missing, unknown, or
> revoked. The login password SHALL NOT be accepted as a git-remote credential.

And `specs/user-accounts/spec.md` — "The system SHALL NOT store passwords in plaintext or with a
reversible transformation."

**Binding design decisions**

- **D4 / AD3 — Argon2id via `Konscious.Security.Cryptography`.** Passwords only. **Not** ASP.NET Core
  Identity's `PasswordHasher<T>` (that's PBKDF2, not memory-hard) and not full Identity.
- **AD4 — token hashing is SHA-256, not Argon2.** Git tokens *and* invitation tokens are high-entropy
  random values, so a fast hash is the correct choice; Argon2 is reserved for low-entropy passwords.
  Emit hashes as **lowercase hex** consistently — the unique index on `TokenHash` is an exact match and
  case-sensitive (closed reviewer item (2) from Block 1).
- **Argon2id parameters (the "tune at implementation" detail design D4 left open — Architect's call):**
  `m = 65536` KiB (64 MiB), `t = 3`, `p = 1`, 16-byte random salt, 32-byte output. Comfortably above the
  OWASP floor (19 MiB / t=2 / p=1) and affordable at this scale (~10 users, low login rate).
  Put them in **one named, documented constants block** — no magic numbers at call sites.
- **The stored hash must be self-describing.** `Account.PasswordHash` is a single opaque `string` column
  (Block 1), and Konscious is a raw KDF that returns bytes — it does **not** produce an encoded hash
  string for you. So encode salt + parameters + hash into that one column in **PHC string format**:
  `$argon2id$v=19$m=65536,t=3,p=1$<b64 salt>$<b64 hash>`. Parse it back on verify and use the
  *embedded* parameters, not the current constants, so parameters can be raised later without
  invalidating existing hashes. Reject malformed/unknown-algorithm hash strings as a failed
  verification, never as a crash.
- **Constant-time comparison** on password verify — `CryptographicOperations.FixedTimeEquals`, never
  `==`/`SequenceEqual` on the hash bytes.
- **Randomness** from `RandomNumberGenerator` (never `Random`). Git token = **32 bytes**, encoded
  **base64url without padding** (~43 chars) so it is safe as a Basic-auth password and in a URL.
- **Inject `TimeProvider`** (framework primitive) rather than calling `DateTimeOffset.UtcNow` directly.
  `RevokedAt`/`CreatedAt` come from it. Block 4's expiry logic depends on this being testable — get it
  right here.
- **"Shown once" is a code invariant, not a UI one:** issuing a token returns the plaintext to the
  caller exactly once and persists only the hash. Never log the plaintext, never add a field or column
  that could retain it.
- **Login password rejected for git is structural, not a special case:** git verification only ever
  hashes the presented secret and looks it up in `GitTokens`, so a login password can never match.
  Don't add a bespoke "is this the password?" check — assert the property in a test instead.

**Shape (deviate only with a reason posted here)**

- `ZeroWiki.Security` — crypto primitives, no EF dependency: `IPasswordHasher` / `Argon2idPasswordHasher`
  (`Hash(string password)` → PHC string, `Verify(string password, string storedHash)` → bool), and the
  token primitive generating a plaintext + its SHA-256 lowercase-hex hash. Make the token primitive
  **reusable for invitation tokens** (AD4) — Block 4 will call the same thing; don't hard-code "git".
- `ZeroWiki.Identity` (or similar) — `GitTokenService` over `IdentityDbContext`: issue (returns
  plaintext once), verify a presented token → owning `Account` or none (rejecting unknown **and**
  revoked), list an account's tokens, revoke by id. Revocation is **idempotent** — revoking an
  already-revoked token is a no-op, not an exception.
- Register both in DI in `Program.cs`. DI registration only — no request pipeline changes.

**Out of scope for this block** (don't build it, don't stub it): the username-match wrapper and
git-email lookup (§8.1/8.2), invitation issue/redeem/revoke flows (§4 — but the token primitive they
reuse is in scope), any Razor page, cookie auth, rehash-on-verify parameter upgrade.

**Tests:** unit tests for 2.1–2.3 in this block (round-trip hash/verify, wrong password fails, tampered
/ malformed PHC string fails, distinct salts for equal passwords, embedded-parameter verify, token
plaintext never persisted, verify resolves the owning account, revoked token stops verifying, revoke is
idempotent, login password does not verify as a git token). Use the Block 1 in-memory SQLite +
real-`Migrate()` pattern already established in `IdentityDbContextTests`. The §9 scenario tests come
later — this is the unit layer.

**Done-gates (all four, before anything is ticked):** `dotnet build` clean (0 warnings — the bar Block 1
set), `dotnet test` green including the existing 7, `openspec validate invite-only-authentication
--strict`, `dotnet format --verify-no-changes` clean. Then hand off → @reviewer.

[worker] Block 2.1–2.3 implemented. Summary:

- **`ZeroWiki.Security` (no EF dependency).** `IPasswordHasher` / `Argon2idPasswordHasher`
  (Konscious.Security.Cryptography.Argon2 1.3.1, AD3/D4) and `ISecretTokenGenerator` /
  `SecretTokenGenerator` + `SecretToken(Plaintext, Hash)` record. The token primitive is
  deliberately generic — nothing in it mentions "git", so §4 invitation tokens call the same thing (AD4).
- **Argon2id parameters (2.1).** One `private const` block at the top of the hasher: `m=65536` KiB,
  `t=3`, `p=1`, 16-byte salt, 32-byte digest, `Version = 19`. No magic numbers at call sites.
- **PHC encoding.** `Hash` emits `$argon2id$v=19$m=65536,t=3,p=1$<b64salt>$<b64hash>` (PHC base64 =
  standard alphabet, padding stripped). `Verify` parses it and derives with the **embedded** parameters,
  not the constants, so the constants can be raised later without invalidating stored hashes — there's a
  test that hashes with `m=8192,t=1` via Konscious directly and asserts it still verifies. Comparison is
  `CryptographicOperations.FixedTimeEquals`. Malformed/unknown-algorithm/unknown-version/absurd-cost/
  non-canonical-base64 strings all return `false`, never throw (12-case `[Theory]`).
- **Token generation (2.2).** 32 bytes from `RandomNumberGenerator.GetBytes`, `Base64Url.EncodeToString`
  (unpadded, 43 chars), hashed `Convert.ToHexStringLower(SHA256.HashData(...))` — lowercase hex, so the
  case-sensitive unique index on `TokenHash` behaves (closes reviewer item (2) from Block 1).
  Password bytes and raw secret bytes are zeroed with `CryptographicOperations.ZeroMemory` after use.
- **`ZeroWiki.Identity.GitTokenService` (2.2/2.3)** over `IdentityDbContext`, `ISecretTokenGenerator`,
  and `TimeProvider`: `IssueAsync` → `IssuedGitToken(Id, Token, CreatedAt)` (the plaintext's only
  existence — the store gets the hash alone); `VerifyAsync` hashes the presented value and projects
  `Select(t => t.Account)` over `TokenHash == hash && RevokedAt == null`; `ListAsync`; `RevokeAsync`
  (idempotent — re-revoking keeps the original `RevokedAt` and returns `true`; `false` only means
  "no such token for this account"). All `CreatedAt`/`RevokedAt` come from `TimeProvider`.
- **DI (`Program.cs`).** `TimeProvider.System` + `IPasswordHasher` + `ISecretTokenGenerator` as
  singletons, `GitTokenService` scoped (it holds the scoped `IdentityDbContext`). Registration only — no
  pipeline changes.
- **Tests: 38 new cases (45 total).** `Security/Argon2idPasswordHasherTests` (9 facts + a 12-case
  theory), `Security/SecretTokenGeneratorTests` (6 facts), `Identity/GitTokenServiceTests` (8 facts + a
  3-case theory) on the Block 1
  in-memory SQLite + real-`Migrate()` pattern with `FakeTimeProvider`
  (`Microsoft.Extensions.TimeProvider.Testing` 10.8.0 added to the test project). Notable assertions:
  the issued plaintext appears **nowhere** in the persisted row (raw `SELECT Id || '|' || TokenHash ||
  '|' || CreatedAt || '|' || COALESCE(RevokedAt,'')` scanned for it, not just a column comparison); the
  login password *and* its stored PHC hash both fail `VerifyAsync`; a token can't be revoked by another
  account (and still verifies afterwards).

**Two real bugs the tests caught (not brief items) — both fixed:**

1. **SQLite cannot `ORDER BY` a `DateTimeOffset`.** `ListAsync`'s `OrderByDescending(t => t.CreatedAt)`
   threw `NotSupportedException` from the EF Sqlite provider. Fixed by materialising then ordering
   client-side (an account has a handful of tokens; documented in a `<remarks>`). **See the ❓ below —
   this will bite §4.**
2. **Konscious rejects a zero-length password** (`ArgumentException: Argon2 needs a password set`), so
   `Verify(password: "", storedHash)` *threw* instead of returning `false`. Fixed: `Hash` now
   `ArgumentException.ThrowIfNullOrEmpty(password)` (an empty password must never reach the store) and
   `Verify` treats null/empty password as a failed verification. Covered by
   `An_absent_password_can_neither_be_hashed_nor_verified`.

**Judgement calls not in the brief:**

- **`RevokeAsync(accountId, tokenId)` is scoped to the owning account**, not `RevokeAsync(tokenId)`.
  Safe-by-default: Block 7's UI can't be tricked into revoking someone else's token, and the check is
  free. Asserted by `A_token_cannot_be_revoked_by_another_account`.
- **No `IGitTokenService` interface.** `IPasswordHasher`/`ISecretTokenGenerator` are interfaces because
  they're pure primitives worth substituting; `GitTokenService` is a concrete class registered as
  itself — there's no second implementation and no consumer needing a seam (its tests run against real
  SQLite). Say the word if you want the interface for §8.
- **Parsed-parameter bounds.** `TryParse` rejects `m` outside 8 KiB–1 GiB, `t` outside 1–64, `p` outside
  1–16, so a corrupt or hostile stored hash can't make verification allocate unboundedly.
- **Canonical-base64 check.** Decoded PHC segments are re-encoded and compared to the input, which
  rejects padding, embedded whitespace, and non-zero trailing bits in one line rather than a char loop.
- **`ListAsync` returns revoked tokens too**, newest first — the owner should see the full history; the
  §7 UI decides what to show.

❓ **@architect — `DateTimeOffset` is not SQL-comparable on SQLite, and §4 depends on comparing it.**
Bug 1 above is the tip: the EF Sqlite provider refuses `DateTimeOffset` in `ORDER BY` *and* in
`>`/`<`/`>=`/`<=` translations. `RevokedAt == null` is fine (null check), and I worked around ordering
client-side, but **Block 4's "unexpired invitation" filter (`ExpiresAt > now`) cannot be expressed in
SQL against the Block 1 schema as it stands.** Options: (a) accept client-side evaluation for invitation
queries too — fine at this scale, invitations are a handful of rows; (b) add an EF value converter
mapping `DateTimeOffset` → sortable/comparable UTC text or ticks, which is a Block 1 schema change plus
a new migration. I did **not** touch the schema (out of this block's scope). Your call before §4 starts.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **45/45 passed** (7 existing + 38 new).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0).

**For @reviewer to scrutinise:**
1. The PHC parser (`Argon2idPasswordHasher.TryParse` / `TryDecodeB64`) — hand-rolled string parsing on a
   crypto path is exactly where I'd want a second pair of eyes. Specifically: is the six-segment split
   + canonical-re-encode check airtight, and are the parameter bounds sensible?
2. `Verify` derives with `stored.Hash.Length` (the length embedded in the stored hash) rather than the
   `HashLength` constant, so a hash written with a different digest length still verifies. Intentional —
   confirm you agree it's not a weakness (a truncated-digest hash would verify against its own short
   digest; nothing we emit is short, and clamping would instead lock those hashes out).
3. Empty-password policy (bug 2): `Hash` throws, `Verify` returns `false`. Is throwing right, or should
   `Hash` also just be a policy-free primitive and let §3/§5 validate input?
4. `RevokeAsync` scoping to `accountId` (judgement call above) — deviation from the brief's "revoke by id".
5. `GitTokenService` as a concrete class with no interface (judgement call above).
6. `Microsoft.Extensions.TimeProvider.Testing` as a new test dependency vs. a five-line hand-rolled
   `TimeProvider` subclass. I picked the framework-blessed `FakeTimeProvider` because §4's expiry tests
   will want `Advance`.
7. The `null!` in three test call sites (`Hash(null!)`, `Verify(null!, …)`, `ComputeHash(null!)`) — used
   deliberately to assert the null-guards, not to dodge a real null.

→ @reviewer

[architect] Answering @worker's items 3–7; leaving **1 and 2 (the PHC parser and the
`stored.Hash.Length` derive) to @reviewer** — hand-rolled parsing on a crypto path is exactly what the
audit is for, and I don't want my read substituting for it.

- **(3) Empty-password policy — keep as implemented.** `Hash` throwing and `Verify` returning `false` is
  the right asymmetry, and not a layering violation: an empty password reaching the store is a
  *corrupt-state* bug, so failing loudly at the primitive is defence in depth behind §3/§5's input
  validation, not a substitute for it. Verification, by contrast, must never throw on attacker-supplied
  input — every bad credential is one uniform `false` (D5). Both halves are correct for their direction.
- **(4) `RevokeAsync(accountId, tokenId)` — accepted**, and better than the brief. An ownership check
  that costs nothing and makes cross-account revocation unrepresentable beats one enforced only by
  §7's UI. Deviation approved.
- **(5) No `IGitTokenService` — accepted.** An interface with one implementation and no seam-needing
  consumer is ceremony; its tests run against real SQLite, which is the stronger test anyway. §8 can
  introduce one if a real need appears — don't pre-build it.
- **(6) `FakeTimeProvider` — accepted.** Framework-blessed over hand-rolled, and §4's expiry tests will
  want `Advance`. Test-only dependency, correct trade.
- **(7) `null!` in the three guard tests — fine.** Asserting a null-guard requires passing null; that's
  the test's point.

❓→ **[architect] answering @worker's `DateTimeOffset` question — decision deferred one step, pending
verification.** Your `ORDER BY` finding is real and well-known (the Sqlite provider throws
`NotSupportedException` telling you to convert the type or order client-side). But the load-bearing part
of your question is the *comparison* claim — that `>`/`<` also fail to translate — and I'm not deciding a
schema change on an unverified premise. @reviewer: **empirically verify both halves** (ordering *and*
`ExpiresAt > now` in a `Where`) against the real migration, and report what actually happens. Distinguish
"throws" from "translates but sorts/compares wrongly because the stored text carries an offset" — those
have different consequences and only one of them is silent.

My leaning, for you to attack rather than ratify: **(b), a value converter**, with `DateTimeOffset` kept
in the domain model. Reasoning — every timestamp here comes from `TimeProvider.GetUtcNow()`, so the
offset carries **zero** information and we're paying a real query-capability cost to store it. Option (a)
is fine on performance grounds at this scale (I'm not worried about row counts), but it makes
*invitation expiry* — a security boundary — a client-side filter, and a silently-wrong comparison there
fails **open**, admitting expired invitations. I'd rather the invariant hold in SQL. This is greenfield
with nothing deployed, so the honest form is regenerating the single `InitialIdentitySchema` migration,
not stacking a second one on top. Open question I want the reviewer's view on: **UTC ticks (`long`) vs a
fixed-width sortable UTC string** — ticks is the conventional converter and unambiguous; ISO-8601 text
keeps the store human-inspectable in a `sqlite3` shell, which is worth something for a self-hosted
zero-config wiki whose operator will poke at it.

Note this is an **Architect call, not a Product Owner one** — design D6 fixes SQLite as the store but
says nothing about column representation, so nothing in the spec or design is in question. It does mean
amending a signed-off block's migration; that's legitimate here precisely because nothing has shipped,
and it will be logged as such.

@reviewer — audit Block 2 **as it stands** (client-side ordering included) and treat the above as a
seventh item, not a blocker on the rest.

[reviewer] Block 2.1–2.3 audited. Verdict: **Request changes** — one blocking crypto-contract finding in
the PHC parser; everything else is clean and the shape is right. Gates re-run independently and confirmed
green: `dotnet build` **0 warn / 0 err**, `dotnet test` **45/45**, `openspec validate
invite-only-authentication --strict` **valid**, `dotnet format --verify-no-changes` **exit 0**.

Scope is exactly the brief: `Security/` primitives with no EF dependency, `Identity/GitTokenService` over
`IdentityDbContext`, four DI lines in `Program.cs`, two `PackageReference`s, three test files. No Razor
page, no cookie auth, no middleware, no §4/§8 stubs, no schema touch. Design compliance verified:
Argon2id via Konscious at m=65536/t=3/p=1 with a 16-byte salt and 32-byte tag in one named constants block
(`Argon2idPasswordHasher.cs:22-31`); no `PasswordHasher<T>` and no ASP.NET Core Identity anywhere;
`FixedTimeEquals` on the digest (`:71`); `RandomNumberGenerator` everywhere and no `Random`;
32-byte tokens as unpadded base64url (`SecretTokenGenerator.cs:25-29`) hashed SHA-256 lowercase hex
(`:41`) so the case-sensitive unique index behaves — Block 1 item (2) is now closed; `TimeProvider`
injected and never `DateTimeOffset.UtcNow` (`GitTokenService.cs:29,103`); revoke idempotent and
ownership-scoped (`:88-108`); plaintext exists only in the `IssuedGitToken`/`SecretToken` return values,
is never a column and never logged. The "login password can't be a git credential" property is genuinely
structural, not a special case (`GitTokenService.cs:47-61`), and the token primitive is generic enough for
§4 invitations per AD4 (`ISecretTokenGenerator.cs`) — nothing in it says "git".

**Blocking:**

1. **`TryParse` accepts parameter sets `Verify` cannot actually process, so a hostile/corrupt stored hash
   can make `Verify` throw instead of returning `false`.** `Argon2idPasswordHasher.cs:127-132` validates
   `m`, `t` and `p` *independently* but never their **relation**. RFC 9106 requires `m >= 8 * p`, and
   Konscious enforces its own `m >= 4 * p`; with `MinParsedMemorySizeKib = 8` and
   `MaxParsedDegreeOfParallelism = 16` (`:37-40`) the accepted-but-invalid window is wide open. Measured
   against this exact code:

   ```
   $argon2id$v=19$m=8,t=1,p=3$<16-byte salt>$<32-byte tag>
     -> AggregateException (InvalidOperationException: "Memory should be enough to provide
        at least 4 blocks per DegreeOfParallelism")
   ```

   Also confirmed throwing at `m=8,p=4`, `m=16,p=5`, `m=32,p=9`; `m=8,p=2` and `m=64,p=16` return `false`
   normally. The escaping exception is an `AggregateException` (Konscious runs lanes on tasks), so nothing
   in `Verify` contains it — it propagates out of `Derive` at `:63-71`.

   This breaks three things you've each committed to in writing: `IPasswordHasher.Verify`'s own contract
   ("A malformed or unrecognised `storedHash` is a failed verification, **not an error**",
   `IPasswordHasher.cs:16-20`); your DEVLOG claim above that malformed strings "all return `false`, never
   throw"; and D5's uniform-failure requirement — a corrupt `Accounts.PasswordHash` row would turn §5
   login for *that one user* into a 500 while every other bad credential returns the uniform failure,
   which is a (narrow, but free) differential oracle.

   Severity is honestly low-exploitability: `storedHash` is only ever written by `Hash()`, which always
   emits `m=65536,p=1`, so reaching this needs DB write access or a later import path. I'm blocking anyway
   because it is a stated invariant, the fix is one comparison, and this is the block whose entire job is
   that the crypto path has no sharp edges. Asked for:
   - enforce the relation in `TryParse` — `memorySizeKib >= 8 * degreeOfParallelism` (RFC 9106's bound,
     strictly stronger than Konscious's, so the library can never be the one to complain);
   - **and** wrap the `Derive` call in `Verify` in a `catch` returning `false`, so any future
     library-side validation change stays a failed verification rather than a 500 — the belt as well as
     the braces, given the exception type is `AggregateException` and not something you'd predict;
   - **and** add `$argon2id$v=19$m=8,t=1,p=3$…` to the `Malformed_stored_hash_fails_verification_without_throwing`
     theory (`Argon2idPasswordHasherTests.cs:131-156`) so it can't regress.

**Answers to your items 1 and 2 (the two the Architect left to me).**

**(1) The PHC parser — sound apart from the blocker above.** I probed it directly rather than reading it
only. Findings:
- **Segment split is airtight for the format we emit.** `segments.Length != 6` rejects truncation *and* an
  extra `$` segment (verified: a 7-segment string → `false`); `segments[0].Length != 0` pins the leading
  separator; the algorithm id is an ordinal exact match so `argon2i`, `argon2d` and case variants are all
  rejected. Note it also rejects PHC's optional `keyid`/`data` segments — fail-closed, and correct for a
  parser that only ever needs to read its own output.
- **`TryParseTagged` is solid** (`:143-155`): requires at least one digit, ordinal prefix, an explicit
  `=`, and `NumberStyles.None`, so sign characters, whitespace, thousands separators and hex are all
  rejected, and overflow simply returns `false`. Verified `t=-1` → `false` and `t=99999999999999` →
  `false`. Cost order is pinned to `m,t,p`; verified `p=1,t=3,m=65536` → `false`.
- **`TryDecodeB64`'s canonical re-encode is the right technique and it works** (`:160-194`). Verified it
  rejects padded input, embedded whitespace (which `Convert.TryFromBase64String` silently tolerates —
  this is the case a hand-written char loop usually misses, and the re-encode catches it), non-zero
  trailing bits, and base64url `-`/`_`. `length % 4 == 1` and empty are rejected before decoding; the
  `padded.Length / 4 * 3` buffer sizing is exact. Keep the comparison `StringComparison.Ordinal` —
  it must never become a culture-sensitive one.
- **Parameter bounds are sensible in magnitude but wrong in structure** — independent ranges, no
  relation. That's blocker 1. See also nits N1/N2 for the salt/tag floors and the generous ceiling.
- Everything else I threw at it returned a clean `false`: `"$$$$$"`, `""`, no-`$` strings, `m=065536`
  (accepted, non-canonical but harmless), 1-byte and 7-byte salts, 1-, 3- and 4-byte digests.

**(2) `Verify` deriving at `stored.Hash.Length` — agreed, not a weakness, and for a stronger reason than
you gave.**
- Clamping to `HashLength` would be actively wrong, not merely restrictive: it would derive a 32-byte tag
  and `FixedTimeEquals` it against a shorter stored array, which returns `false` for *every* password —
  i.e. it would lock out precisely the hashes the embedded-parameter design exists to keep verifiable.
  Deriving at the embedded length is the only self-consistent choice. `:69` is right.
- **Accidental truncation fails closed**, which I don't think you knew: Argon2's tag length is an input to
  the final `H'`, so `GetBytes(1)` is *not* `GetBytes(32)[..1]`. Measured — chopping a real 32-byte digest
  down to 1 byte produces a stored hash that verifies **nothing, not even its own password**. A
  half-written column is therefore a failed login, not a weak one.
- The only case that buys an attacker anything is a **deliberately crafted** short-tag hash. Measured:
  with a legitimately derived 1-byte tag, 13/4096 arbitrary passwords verified (≈1/256, as theory says);
  with 2 bytes, 0/4096. But crafting one requires write access to `Accounts.PasswordHash` — at which
  point the attacker can simply store the hash of a password they already know. It grants strictly
  nothing over the capability it presupposes. **Confirmed: not a weakness.** I'd still add the floor in
  N1, purely to remove the degenerate case from the reachable set for one comparison's worth of code.

**Non-blocking nits:**
- **N1 — add Argon2's own length floors while you're in `TryParse`.** `Argon2idPasswordHasher.cs:134-137`
  accepts any decodable salt and tag, including 1 byte of each; RFC 9106 requires salt ≥ 8 and tag ≥ 4.
  Verified these *don't* throw (Konscious just derives), so this is weak-hash acceptance rather than a
  crash — but `MinParsedSaltLength = 8` / `MinParsedHashLength = 16` costs two comparisons, is well below
  anything we emit, and closes item (2)'s degenerate case for good.
- **N2 — the ceilings are very generous.** `:38-40` permits `m` up to 1 GiB *and* `t` up to 64 in
  combination. Measured cost on this machine: 93 ms at 64 MiB/t=1, 344 ms at 256 MiB/t=1 — so the
  accepted maximum is roughly a gigabyte of allocation and a minute-plus of CPU for a single `Verify`.
  Unreachable today, but the bound's job is to make a corrupt row cheap to reject; 256 MiB / t=16 is still
  four times anything we'd ever write.
- **N3 — `ListAsync` hands `GitToken` entities, `TokenHash` included, up to the §7 UI.**
  `GitTokenService.cs:71-81`. Not a plaintext leak and not wrong, but a small projection DTO (id, created,
  revoked) would keep at-rest hashes out of the render path entirely. §7's call, flagging now.
- **N4 — the nullable annotations and the behaviour disagree.** `IPasswordHasher.Verify(string password,
  string storedHash)` (`IPasswordHasher.cs:20`) is annotated non-null, but the implementation treats null
  as `false` (`Argon2idPasswordHasher.cs:58`) and the tests assert that via `null!`. Widen `storedHash` to
  `string?` — a null column is exactly the corrupt-state case `Verify` should absorb, so let the signature
  say so rather than making callers use `null!` to reach documented behaviour.
- **N5 — PHC interop is implied but untested.** The class doc (`:9-14`) rightly sells the string as
  *self-describing*; emitting a standard format also implies another Argon2 implementation could read it,
  and nothing here checks Konscious against the reference vectors. No consumer needs it, so don't build
  one — just say "self-description, not interop" in the doc so a future reader doesn't assume more.
- **N6 — one small test-coverage shape point.** Every case in the 12-case malformed theory fails at
  *parse*. The blocker's case is the first that parses and then fails downstream; adding it (and, if you
  take N1, a legal-but-unusual salt/tag length case) gives the theory coverage of both sides of `TryParse`,
  not just the front door. The rest of the suite is genuinely good — the raw-`SELECT` scan for the
  plaintext (`GitTokenServiceTests.cs:189-203`) and `A_token_cannot_be_revoked_by_another_account` both
  assert behaviour rather than execution, and `Verify_uses_the_parameters_embedded_in_the_stored_hash`
  tests the thing the design decision actually hinges on.

---

**❓→ [reviewer] answering the Architect's `DateTimeOffset` question — premise verified empirically, and
the conclusion changes slightly.**

Probed against the **real `InitialIdentitySchema` migration** on in-memory SQLite (throwaway project
outside the repo; nothing added to the tree — `git status` shows only the block's own files).

**What actually happens:**

| Operation | Result |
|---|---|
| `OrderBy/OrderByDescending(x => x.CreatedAt)` | **throws** `NotSupportedException` — "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses" |
| `Max/Min(x => x.ExpiresAt)` | **throws** `NotSupportedException` — same, for aggregates |
| `Where(i => i.ExpiresAt > now)` | **throws** `InvalidOperationException` — "The LINQ expression … could not be translated" |
| `Where(t => t.RevokedAt == null)` | translates, correct |
| `Where(t => t.CreatedAt == value)` | translates, correct **for identical offsets** |
| stored form | TEXT, `'2026-07-25 11:00:00+00:00'` |

**So: @worker's claim is confirmed in full — both halves throw, loudly.** And the specific fear in the
Architect's post does **not** materialise: EF does not silently client-evaluate a `Where` predicate (that
has been an error since EF Core 3.0), so option (a) cannot fail open *by accident*. Client-side evaluation
is only ever reached by someone deliberately writing `.ToListAsync()` first, as `ListAsync` does. On that
narrow point the leaning's justification is weaker than stated.

**But I still land on (b), a value converter — for two hazards that *are* silent, neither of which is in
your post:**

1. **Equality translates and is offset-sensitive.** `==` and `!=` *do* reach SQL, as TEXT comparison
   against `'…+00:00'`. The same instant written with a different offset does not compare equal, and TEXT
   ordering across mixed offsets is chronologically wrong. Today every write goes through
   `TimeProvider.GetUtcNow()`, so we're safe *by habit*; nothing in the schema enforces it. One §4/§7/§8
   write path that passes a local-offset `DateTimeOffset` breaks lookups with no error anywhere.
2. **The obvious converter is the wrong one, and it fails exactly as you feared.** EF's built-in
   `DateTimeOffsetToBinaryConverter` restores server-side `>` and `ORDER BY` — and compares **wrongly**,
   because it packs the offset into the value instead of normalising it. Measured, with `now = 12:00Z`:

   ```
   Where(ExpiresAt > now) returned:  future-utc-13:00Z ✓
                                     future-as-18:00+05:00(=13:00Z) ✓
                                     expired-as-16:00+05:00(=11:00Z) ✗ ← an EXPIRED row, admitted
                                     far-future-9999 ✓
   ```

   That is the silent fail-open, in the invitation-expiry filter, from the one-liner a reasonable
   implementer reaches for first (`HasConversion<DateTimeOffsetToBinaryConverter>()`). Whatever else is
   decided, **this must be written down** so §4 doesn't walk into it.

**Both custom candidates work.** Verified each restores server-side `ORDER BY`, `>`/`<`, `Max`, and null
checks, orders correctly from year 1 to 9999, and normalises a `+05:00` input to the correct instant:

- **UTC ticks (`long`)** — `v => v.UtcTicks` / `v => new DateTimeOffset(v, TimeSpan.Zero)`. Stores
  `639205812000000000` (INTEGER). Correct on every probe.
- **Fixed-width ISO-8601 UTC string** — `yyyy-MM-ddTHH:mm:ss.fffffffZ`, 28 chars. Stores
  `2026-07-25T13:00:00.0000000Z` (TEXT). Correct on every probe.

**My pick: the ISO-8601 UTC string**, narrowly, and I'll argue it rather than just agree:

- **Ordering correctness is structural, not incidental.** Fixed width + always-`Z` + SQLite's default
  BINARY collation ⇒ lexicographic order *is* chronological order, for every representable value. That's
  as strong a guarantee as ticks gives, not weaker.
- **The operator can read it, and that matters most precisely here.** D-context sells zero-config
  self-hosting; the person debugging "why was my invite rejected" is the operator in a `sqlite3` shell.
  `2026-07-25T13:00:00.0000000Z` answers that question; `639205812000000000` does not.
- **Ad-hoc SQL stays correct by default.** SQLite's own `datetime()`/`julianday()`/`strftime()` parse
  ISO-8601 with `T` and `Z`, so an operator's hand-written `WHERE ExpiresAt > datetime('now')` does the
  right thing. With ticks, the same query needs an arithmetic conversion they will get wrong, silently —
  which is the same class of bug we're trying to design out.
- Cost is 28 bytes versus 8 per value, on a table with tens of rows. Irrelevant.
- The one genuine advantage of ticks is fewer moving parts — no format string to get wrong. I'd rather
  spend a `ParseExact` (which **throws** on a malformed value on read, i.e. fails loudly) than lose
  inspectability at the exact boundary we care about.

**Implementation notes, if the Architect takes this:**
- Apply it via `ConfigureConventions` — `configurationBuilder.Properties<DateTimeOffset>()
  .HaveConversion<…>()` — **not** per-property `HasConversion`. Verified: the convention covers
  `DateTimeOffset?` as well, NULL stays NULL, and server-side `RevokedAt < cutoff` and `RevokedAt == null`
  both translate correctly. That's nine columns across four entities today and it can't be forgotten on a
  column added in §4 or later, which per-property configuration certainly would be.
- Format must be exactly `yyyy-MM-ddTHH:mm:ss.fffffffZ` — 7 fixed fractional digits, literal `Z`, never
  the `"o"` round-trip form with a variable offset. `HasMaxLength(28)`. No `NOCASE` on these columns.
- Regenerating the single `InitialIdentitySchema` is the honest form, agreed — nothing has shipped, and a
  second migration would be fiction. Amending a signed-off block is legitimate here; I'd want the reason
  in the commit body as well as here.
- It also lets `GitTokenService.ListAsync` (`:71-81`) drop the materialise-then-sort and its `<remarks>`,
  so the workaround doesn't calcify into a pattern the rest of the change copies.
- **Lock it in with a test, or it's worth nothing:** for §4 specifically, a test asserting an expired
  invitation is excluded by a *server-side* predicate (`Where(i => i.ExpiresAt > now)` reaching SQL), not
  by a client-side filter. That test is what stops a future refactor quietly reintroducing the hazard.

**Architectural notes (not blockers on this block):**

- **A1 — §5 will have a timing oracle for username enumeration unless it's designed out now.** `Verify`
  costs ~93 ms of CPU (measured, at m=64 MiB); a login for a username that doesn't exist has no hash to
  verify and returns in ~0 ms. D5's "uniform login failures" is therefore *not* achievable by returning
  the same message — §5 must also equalise the work, conventionally by verifying against a fixed dummy
  PHC hash when the account lookup misses. Flagging under this block because it's a property of this
  primitive, and it's exactly the requirement that quietly never gets implemented.
- **A2 — §5's login endpoint is an unauthenticated 64 MiB / 93 ms amplifier.** Ten concurrent attempts is
  ~640 MiB. This is *not* an argument to lower the parameters — they're right per the brief and I'd keep
  them — it's an argument that §5 needs rate limiting or a concurrency gate on the login path, decided
  deliberately rather than discovered in production on a small self-hosted box.
- **A3 — no rehash-on-verify, correctly out of scope, but the design's whole point is that it becomes
  possible.** Whoever eventually raises the constants must add it in the same change, or existing accounts
  stay at the old cost forever. Worth a line in `design.md` rather than a comment in code.
- **A4 — AD4's password/token split pays off in §8 and it's worth naming.** The git Smart HTTP path calls
  `VerifyAsync` on *every* request; SHA-256 plus an indexed lookup is the right cost profile there, where
  Argon2 would be ruinous. Nothing to change — just the reason not to "unify the hashing" later.

→ @worker — fix blocker 1 (the `m >= 8 * p` relation, the `catch` in `Verify`, and the theory case); N1 is
strongly recommended in the same edit since it's the same three lines. N2–N6 are yours to take or leave.
Then I'll re-audit. Nothing else in the block needs to change.

[architect] Ruling on the audit. **Blocker 1 stands — fix it.** The severity argument ("only `Hash()`
writes the column") is exactly the argument that ages badly: §4 redemption, §3 bootstrap and any future
import all write that column, and a stated contract that holds only by current call-site accident isn't a
contract. Take all three parts (`m >= 8*p`, the `catch` around `Derive`, the theory case).

**N1–N6 — take N1 through N5; N6 comes free with the blocker.**

- **N1 accepted** (salt ≥ 8, tag ≥ 16). Same three lines, and it deletes the degenerate short-tag case
  from the reachable set rather than leaving it merely unreachable.
- **N2 accepted** — tighten to **256 MiB / t ≤ 16**. A bound whose job is to make a corrupt row *cheap to
  reject* shouldn't itself authorise a gigabyte and a minute of CPU. Still 4× anything we emit.
- **N3 accepted, and do it now rather than deferring to §7.** Return a projection (id, created, revoked) —
  not `GitToken` entities. My call over the reviewer's "§7's call": doing it now means at-rest hashes
  never enter the render path *by construction*, and it spares §7 having to reshape this service later.
- **N4 accepted** — widen `storedHash` to `string?`. A signature that forces `null!` to reach documented
  behaviour is the signature being wrong, not the tests.
- **N5 accepted** — doc wording only: say **self-description, not interop**. Do *not* build vector tests.
- **N6** — satisfied by the blocker + N1 cases.

**AD7 — `DateTimeOffset` is stored as a fixed-width ISO-8601 UTC string via a value converter.**
Accepted as the reviewer argued it, including the representation. Two things to be straight about: my
stated fear was **wrong** — EF Core has errored rather than silently client-evaluating a `Where` since
3.0, so option (a) could not have failed open by accident. The decision stands on the reviewer's two
hazards instead, which are better ones: **equality does translate and is offset-sensitive** (safe today
by habit, not by schema), and **the built-in `DateTimeOffsetToBinaryConverter` is the one-liner a
reasonable implementer reaches for and it compares *wrongly*** — measured admitting an expired row. That
second one is decisive: the trap is on the default path, so the schema has to close it before §4 gets
there.

ISO-8601 text over ticks, on the reviewer's third argument specifically: an operator's hand-written
`WHERE ExpiresAt > datetime('now')` is *correct by default* against ISO-8601 and silently wrong against
ticks. Designing out a bug class and then leaving the operator's own query in it would be incoherent.
Binding details: exactly `yyyy-MM-ddTHH:mm:ss.fffffffZ`, `HasMaxLength(28)`, no `NOCASE`, applied via
`ConfigureConventions` → `Properties<DateTimeOffset>()` (**never** per-property — it must be impossible
to forget on a column added in §4), regenerate the single `InitialIdentitySchema`, drop `ListAsync`'s
materialise-then-sort, and land the server-side-predicate test.

**Sequencing:** AD7 is **§1 work (identity-store schema), not §2** — a block never spans sections, so it
does not ride in the Block 2 commit. Block 2 finishes and commits on its own; AD7 lands next as a
separate briefed unit against §1, with its own review and its own commit carrying the amend-a-signed-off-
migration reason in the body. It must be done before Block 4 starts.

**AD8 — §5 must equalise login work, not just the message.** Accepting reviewer note A1 as binding for
§5.2: verify against a fixed dummy PHC hash when the username lookup misses, so a miss costs the same
~93 ms as a hit. This is **not** scope creep — `specs/authentication/spec.md` requires rejecting
"without revealing whether the username exists", and a 93 ms-vs-0 ms timing differential reveals exactly
that. A uniform error string over a non-uniform response time does not satisfy the requirement.

**AD9 — raising the Argon2 constants must include rehash-on-verify, in the same change.** Recording
reviewer note A3 here (the DEVLOG archives with the change, so it persists as the rationale without
amending a Product-Owner-approved artifact mid-flight). No code this change — the PHC design is what
makes it possible later; whoever raises the cost owes the upgrade path or existing accounts stay at the
old parameters forever.

**Reviewer note A2 (login is an unauthenticated 64 MiB / 93 ms amplifier) → Product Owner.** Agreed on
the substance, and agreed it is *not* a reason to lower the parameters. But no spec or task in this
change mentions rate limiting, so adding it is scope beyond the approved proposal — that's the Product
Owner's call, not mine. Raising it with them before Block 5; §3 and §4 don't depend on the answer, so it
blocks nothing now. **A4** needs no action — it names why AD4's password/token split is right and must
not be "unified" later.

→ @worker — blocker 1 + N1–N5 as ruled above. **Block 2 only** — do *not* touch the schema or the
migration; AD7 is a separate unit and I'll brief it after this commits.

[worker] Round 1 fixes applied — blocker 1 (all three parts) + N1–N5. Delta:

**Blocker 1 — `TryParse` accepted parameter sets `Verify` couldn't process.** Conceded without
reservation; the reviewer is right that the contract was only holding by call-site accident, and my DEVLOG
claim that malformed strings "never throw" was simply false for this class of input.
- **(a) Relation enforced.** New `MinParsedMemorySizeKibPerLane = 8` and the check
  `memorySizeKib < MinParsedMemorySizeKibPerLane * degreeOfParallelism` in `TryParse`. RFC 9106's bound,
  so Konscious's weaker `m >= 4*p` can never be the thing that complains.
- **(b) `Derive` wrapped in `Verify`.** `catch (Exception ex) when (ex is not OutOfMemoryException)` →
  `false`. I went broad rather than listing `AggregateException`/`ArgumentException`/
  `InvalidOperationException`, precisely because the reviewer's point was that the escaping type isn't
  predictable — a type list would only re-create the bug for whatever a future Konscious throws instead.
  `OutOfMemoryException` is the one exclusion: it says nothing about the stored hash, and masking it as a
  wrong password would hide process-level trouble. Commented as such.
- **(c) Regression cases added** to `Malformed_stored_hash_fails_verification_without_throwing`:
  `m=8,t=1,p=3` and `m=32,t=1,p=9` (both with a 16-byte salt and 16-byte tag, so they reach the relation
  check rather than being rejected earlier — verified by arithmetic against each preceding guard).
- **Plus a bound-tightness test the ruling didn't ask for:**
  `Verify_accepts_a_multi_lane_hash_that_sits_exactly_on_the_rfc_memory_bound` derives at `m=64,t=1,p=8`
  via Konscious and asserts it still verifies. An over-tight relation check would silently lock out legal
  hashes, which is the same failure mode in the other direction — the theory cases alone can't catch it.

**N1** — `MinParsedSaltLength = 8`, `MinParsedHashLength = 16`, checked alongside the b64 decode. Theory
cases added for a 4-byte salt and an 8-byte tag (both decodable, both now rejected).
**N2** — ceilings tightened to `MaxParsedMemorySizeKib = 256 * 1024` and `MaxParsedIterations = 16`;
constants doc-comment reworded to say the ceilings sit just above what this class emits, because rejecting
a corrupt row has to stay cheap. Theory case added for `t=32`.
**N3** — `ListAsync` now returns `IReadOnlyList<GitTokenSummary>` (`Id`, `CreatedAt`, `RevokedAt`) via a
server-side `Select`, so `TokenHash` never leaves the data layer. New `Identity/GitTokenSummary.cs`. Extra
test `Listed_tokens_do_not_carry_the_at_rest_hash` asserts the summary's `ToString()` contains neither the
hash nor the plaintext — a record prints every property it has, so it fails the moment someone adds one.
**N4** — `IPasswordHasher.Verify(string password, string? storedHash)`; XML doc now says an *absent*
stored hash is a failed verification. The `null!` at that call site is gone; the two remaining `null!`s
(`Hash(null!)`, `Verify(null!, stored)`) stay, since `password` is genuinely non-null-annotated and the
tests are asserting those guards.
**N5** — class `<remarks>` on `Argon2idPasswordHasher`: PHC is used for **self-description, not interop**,
and explicitly notes nothing is checked against the reference vectors. No vector tests added.
**N6** — covered by the blocker + N1 cases: the theory now exercises both sides of `TryParse` (front-door
parse failures *and* sets that parse but are refused downstream).

`stored.Hash.Length` left exactly as it was, per the ruling — and noted, the tag length feeding `H'` is a
better reason than mine. Schema, entities, configurations and the migration untouched; `ListAsync` keeps
its client-side ordering and `<remarks>` pending AD7 as a separate §1 unit.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **52/52 passed** (was 45; +7
cases: 5 new theory cases, 2 new facts). `openspec validate invite-only-authentication --strict` — valid.
`dotnet format --verify-no-changes` — clean (exit 0).

No disagreements with the ruling. One thing worth a second look rather than a nit:
`catch (Exception ex) when (ex is not OutOfMemoryException)` is deliberately broad on a crypto path — if
you'd rather see an explicit type list despite the unpredictability argument, say so and I'll narrow it.

→ @reviewer

[reviewer] Re-audit of the round-1 delta — **Approve**. Blocker 1 is genuinely closed (verified by sweep,
not by inspection), N1–N5 are all present as ruled, and both of the Architect's own calls were honoured.
Gates re-run independently: `dotnet build` **0 warn / 0 err**, `dotnet test` **52/52**, `openspec validate
invite-only-authentication --strict` **valid**, `dotnet format --verify-no-changes` **exit 0**.

**Scope confirmed clean.** Tracked diff is `Program.cs` (+6, the round-0 DI lines, unchanged),
`ZeroWiki.csproj` (+1), `ZeroWiki.Tests.csproj` (+1). `git status` over `src/ZeroWiki/Data` and
`src/ZeroWiki/Migrations` is **empty** — no entity, configuration, snapshot or migration touched, so AD7
is untouched and `ListAsync` legitimately keeps its client-side ordering and `<remarks>` pending your §1
unit.

**Delta verified:**
- **Blocker 1(a)** — `Argon2idPasswordHasher.cs:56` `MinParsedMemorySizeKibPerLane = 8` with the relation
  check at `:159` (`memorySizeKib < MinParsedMemorySizeKibPerLane * degreeOfParallelism`), folded into the
  existing bounds guard rather than bolted on after it. Constant carries the "RFC 9106, stricter than
  Konscious's four, so the library is never the one to complain" rationale at `:50-55` — exactly the right
  thing to write down.
- **Blocker 1(b)** — `:79-98`. Scoping is right; see item 1 below.
- **Blocker 1(c)** — `Argon2idPasswordHasherTests.cs:170-171`, `m=8,t=1,p=3` and `m=32,t=1,p=9`. I checked
  the arithmetic claim: both carry a 16-byte salt (`c2FsdHNhbHRzYWx0c2FsdA`) and a 16-byte tag
  (`aGFzaGhhc2hoYXNoaGFzaA`), so neither is rejected by the new length floors first, and both were
  independently confirmed to throw under the *old* code. Genuine regression cases — with one caveat in
  nit R3.
- **N1** — `:47-48` floors, applied at `:164-165` alongside the decode with correct null-flow
  (`[NotNullWhen(true)]` short-circuit, 0 warnings). Theory cases at `:173-174`: 4-byte salt (`c2FsdA`),
  8-byte tag (`aGFzaGhhc2g`) — both decodable, both now rejected.
- **N2** — `:44-45` now `256 * 1024` / `16`, doc-comment reworded at `:38-42` to explain the ceilings sit
  just above what the class emits. Theory case `t=32` at `:167`. **Verified at the boundary**: m=262144
  accepted / m=262145 rejected, t=16 accepted / t=17 rejected, p=16 accepted (with m=128) / p=17 rejected.
- **N3, done now per your call** — `GitTokenService.cs:71-82` returns `IReadOnlyList<GitTokenSummary>`;
  new `Identity/GitTokenSummary.cs`. I checked this reaches SQL and isn't just a type-level fig leaf —
  generated query is `SELECT "g"."Id", "g"."CreatedAt", "g"."RevokedAt" FROM "GitTokens" AS "g" WHERE
  "g"."AccountId" = @accountId`. **`TokenHash` is not in the SELECT list at all**, so the hash never
  leaves the database, let alone the data layer. `Listed_tokens_do_not_carry_the_at_rest_hash`
  (`GitTokenServiceTests.cs:172-185`) is a good test — asserting through `ToString()` means it fails if
  someone *adds* a hash property later, which a property-by-property assertion wouldn't.
- **N4** — `IPasswordHasher.cs:21` `Verify(string password, string? storedHash)`, doc at `:15-20` now
  states an absent stored hash is a failed verification and, better than I asked for, says *why*
  ("a corrupt stored value must not be distinguishable from a wrong password"). The `null!` at that call
  site is gone; the two `password` guard `null!`s correctly remain.
- **N5** — `Argon2idPasswordHasher.cs:15-19`, self-description not interop, and explicitly says nothing is
  checked against the reference vectors. No vector tests. Correct.
- **`stored.Hash.Length`** — `:88`, untouched.

**Item 1 — the breadth of the `catch`. Keep it broad. Two small refinements, neither blocking.**

- **The scoping is right, and that was the more important half of the question.** The `try` at `:80-89`
  wraps *only* the `Derive` call; `computed` is declared outside it at `:79`, `TryParse` runs before the
  `try`, and `FixedTimeEquals` runs after it at `:100`. So the filter cannot mask a parser bug, cannot mask
  a comparison bug, and cannot turn a mismatch into an exception-shaped path. Wrapping the whole method
  body — the obvious lazy version — would have swallowed `TryParse` bugs silently; this doesn't.
- **Broad-and-return-false is correct here, and I'd resist narrowing it.** On a path fed attacker-supplied
  input, "too narrow" fails as a 500 plus the differential oracle blocker 1 was about; "too broad" fails
  as a false negative in *our own* code, which the round-trip tests catch immediately because
  `Correct_password_verifies_against_its_hash` runs through the same `Derive`. Those costs are not
  symmetric, and a type list would re-create blocker 1 for whatever the next Konscious version throws.
  The worker's reasoning is sound; @architect's read is right.
- **Nothing cancellation-shaped can reach it today** — I checked rather than assumed. `IPasswordHasher`
  takes no `CancellationToken`, `Derive` threads none, and the whole path is synchronous, so no
  `OperationCanceledException`/`TaskCanceledException` can arise. `StackOverflowException` isn't catchable
  and `ThreadAbortException` isn't thrown on .NET Core, so neither is a concern. **But** the day someone
  threads a token in (§5 under load, say), this filter would silently convert a cancellation into "wrong
  password" — cancellation is control flow, not a verification result. See nit R1.
- **The `OutOfMemoryException` exclusion is right in intent but leaky in fact.** Konscious surfaces lane
  failures wrapped in `AggregateException` — that's how the blocker's exception arrived. An OOM raised
  inside a lane therefore arrives as `AggregateException(OutOfMemoryException)`, and
  `ex is not OutOfMemoryException` is *true* for the wrapper, so it gets swallowed — precisely the outcome
  the exclusion exists to prevent. See nit R2.

**Item 2 — the `m >= 8 * p` bound is exactly right, in both directions. Verified empirically.**

- **Nothing legal is rejected.** RFC 9106 §3.1 fixes the bound at `8*p <= m`, so the check admits every
  conformant parameter set by construction. Confirmed with **real Konscious derives sitting exactly on the
  bound** for p = 1, 2, 3, 4, 8, 15, 16 (m = 8, 16, 24, 32, 64, 120, 128) — **all seven verify**. The
  worker's new `Verify_accepts_a_multi_lane_hash_that_sits_exactly_on_the_rfc_memory_bound`
  (`Argon2idPasswordHasherTests.cs:109-128`) covers p=8; I extended it to the p=1 floor and the p=16
  corner. Adding that test unprompted was the right instinct — an over-tight relation check is the same
  bug in the opposite direction, and the rejection theory structurally cannot see it.
- **Nothing the KDF chokes on is accepted.** Konscious needs `m >= 4*p`; `8*p` implies `4*p`, so the
  accepted set is a strict subset of what the library can process. Confirmed by brute force: **1088
  parameter sets swept** (p 1–17 × t ∈ {1,2,16,17} × m ∈ {7, 8, 8p−1, 8p, 4p, 65536, 262144, 262145} ×
  {8-byte salt/16-byte tag, 16-byte salt/32-byte tag}) — **zero throws**, in-bounds or out. Every parsed
  bound also checked individually at its boundary (all 13 cases as expected, including a 64-byte tag,
  which correctly still verifies — the floors don't accidentally cap the top).
- **One honest characterisation for the record:** the band `4*p <= m < 8*p` is one we now reject that
  Konscious *would* have processed — verified, it accepts `8p−1` for every p tested. That's deliberate and
  costs nothing real: those sets are not RFC-conformant, no conforming producer emits them, and this class
  only ever emits p=1. The bound is the tightest one that admits every conformant hash while sitting
  strictly inside the library's tolerance. That's the right place to put it.

**Nits — all non-blocking, none needed before commit:**
- **R1** — add `OperationCanceledException` to the filter now: `when (ex is not OutOfMemoryException and
  not OperationCanceledException)` (`Argon2idPasswordHasher.cs:90`). Unreachable today; free; stops a
  future token-threading refactor from silently reporting a cancelled verification as a wrong password.
- **R2** — the OOM exclusion can't catch a wrapped OOM (above). Either flatten it, or — my preference,
  since after N2 the ceiling is 256 MiB and a parameter-attributable OOM is implausible — leave the filter
  alone and say **best-effort** in the comment at `:92-96`. A comment that overstates a guarantee is worse
  than a guard that admits its limit.
- **R3 — the belt now hides the brace, and the theory can't see it.** Measured: delete the relation check
  and the two new cases *still pass*, because the KDF throws `AggregateException` and the `catch` returns
  `false`, so `Assert.False` still holds. Those cases now prove the **contract** (uniform `false`, no
  throw) rather than the **mechanism** (that `TryParse` rejects before the KDF is reached). That's the
  right thing to assert and I wouldn't change the test — but worth one line in the comment at `:168-169`
  so a future reader doesn't over-trust it. If the mechanism ever needs locking down, `internal` +
  `InternalsVisibleTo` on `TryParse` is the honest way; don't assert on timing.
- **R4** — for the record on N2's rationale: the accepted ceiling still buys a corrupt row **5.0 s** of
  CPU (measured at m=256 MiB, t=16) versus ~93 ms for a legitimate verify. That's 16× cheaper than the old
  1 GiB/t=64 ceiling, so N2 did its job, and I'd leave it — tightening further starts colliding with
  plausible future parameter raises. If "cheap to reject" is ever meant literally, the bound has to be on
  the *product* `m * t`, not on m and t independently. Offering the observation, not asking for a change.
- **R5 — a corrupt stored hash is now permanently silent, including to the operator.** Both the
  `TryParse` rejection and the `catch` return a bare `false`, so a corrupt `Accounts.PasswordHash` is
  indistinguishable from a wrong password *forever* — right for the client, unhelpful for whoever debugs
  "alice can't log in". Not this class's job (it's a dependency-free primitive, correctly). **§5's job:**
  log the distinction between "no such username", "stored hash unusable" and "wrong password" server-side
  while returning one uniform response — and never log the password or the hash. Pairs naturally with AD8,
  which is already touching that code path.
- **R6** — the theory has no case just *above* the new ceilings (`m=262145`, `p=17`). I verified both are
  rejected, so this is cosmetic symmetry only.

**✅ SIGN-OFF — Block 2 (§2.1–2.3, password & token hashing) APPROVED.** All four gates green, scope clean,
no schema touched, blocker 1 closed and verified by sweep, N1–N5 landed as ruled, both items answered.
Clear to tick 2.1–2.3 and commit. R1–R6 are non-blocking; R1/R2 are two-line edits if you want them in
this commit, R5 belongs to §5 and is already implied by AD8.

[worker] Post-sign-off polish per architect — R1, R2, R3, and R6 taken. No behaviour change beyond R1.

- **R1** — filter is now
  `catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)`.
  Nothing cancellation-shaped can reach it today, which is exactly why it's cheap to close before someone
  threads a `CancellationToken` through and a cancelled login starts reporting "wrong password".
- **R2** — the comment now states the memory exclusion is **best-effort**, and why: Konscious runs lanes
  on tasks, so a lane's `OutOfMemoryException` arrives wrapped in an `AggregateException`, for which
  `ex is not OutOfMemoryException` is true and the swallow happens anyway. Left unflattened as ruled —
  after N2's 256 MiB ceiling a parameter-attributable OOM isn't plausible; the comment also records the
  "broad on purpose, a type list would reopen the hole" reasoning so the next reader doesn't narrow it.
- **R3** — the two relation cases now carry the caveat that they pin the **contract** (malformed input →
  `false`, never an exception) and *not* the mechanism: they pass whether `TryParse`'s relation check
  rejects them or `Verify`'s `catch` absorbs the KDF's complaint, so they must not be read as evidence
  the relation check exists. Good catch — the belt does hide the brace, and I'd have left that trap in.
- **R6** — took it for boundary symmetry: theory cases added one KiB above the memory ceiling
  (`m=262145`) and one lane above the parallelism ceiling (`p=17`).
- **R4** and **R5** untouched per the ruling.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **54/54 passed** (was 52; +2
R6 theory cases). `openspec validate invite-only-authentication --strict` — valid.
`dotnet format --verify-no-changes` — clean (exit 0). No schema, entity, configuration, or migration
change; still `Security/`, `Identity/`, `Program.cs` DI, two `PackageReference`s, and the three test files.

## 3. Bootstrap

_[architect] → @worker — **Block 3 = tasks 3.1–3.3.** The first UI in this change: a Static SSR page with
a form POST. Built against the AD7 schema, which has landed (`71fb453`)._

**Tasks in scope**

- `3.1` Detect the empty-store condition (no accounts) on startup.
- `3.2` Present a one-time bootstrap flow to create the first administrator account.
- `3.3` Make the bootstrap path inert once any account exists.

**Binding spec (verbatim, `specs/user-accounts/spec.md`)**

> ### Requirement: First-administrator bootstrap
> The system SHALL provide a way to create the first administrator account when no accounts exist, so
> that the initial member can invite others. Once any account exists, the bootstrap path SHALL NOT create
> additional accounts.
>
> #### Scenario: Bootstrap on empty deployment
> - **WHEN** the system starts with no accounts and the bootstrap step is completed
> - **THEN** exactly one administrator account exists
>
> #### Scenario: Bootstrap disabled once populated
> - **WHEN** at least one account already exists
> - **THEN** the bootstrap path does not create a new account

Design **D2**: the one-time bootstrap resolves the chicken-and-egg (invites require an inviter) "without
leaving a permanent privileged backdoor". **AD6**: administrator = `IsAdministrator = true`.

**Binding decisions**

- **B1 — "exactly one" is a concurrency requirement, and it is the hard part of this block.** Two
  simultaneous POSTs to the bootstrap form must not both create an administrator. A read-then-write
  (`AnyAsync` → `Add` → `SaveChanges`) is **not** sufficient: SQLite's default deferred transaction takes
  no write lock on the read, so both requests can observe an empty store and both inserts then succeed —
  two admins, spec violated. **Do the check and the insert inside a single transaction that takes the
  write lock before the check** (`BEGIN IMMEDIATE` semantics — on Microsoft.Data.Sqlite,
  `BeginTransaction(deferred: false)`; verify how to reach that through EF Core rather than assuming
  `BeginTransactionAsync()` gives it to you). **Prove it with a test that runs concurrent bootstrap
  attempts and asserts exactly one account exists** — not a test that runs the happy path twice in
  sequence. If you conclude this genuinely cannot be done without a schema change (e.g. a single-row gate
  table), **stop and tell me** — that would be another §1 unit and my call, not yours to absorb.
- **B2 — the gate is a live check, never the startup result.** 3.1 says "on startup", and that is for
  first-run signalling/logging only. The **inertness gate in 3.3 must re-evaluate per request**: an
  account created at 10:00 must make the bootstrap path inert immediately, with no restart. Caching the
  startup answer in a singleton and gating on it would leave a **permanent privileged backdoor open for
  the life of the process** — exactly what D2 forbids. This is the most dangerous mistake available in
  this block.
- **B3 — inert means inert on both verbs.** The GET must not render the form when any account exists
  (redirect away), **and** the POST must independently refuse. The POST check is the one that matters,
  since the GET check is only a UX nicety and a hand-crafted POST skips it. Don't let the redirect stand
  in for the guard.
- **B4 — keep `AnyAsync()` non-materialising** (§3 note from the AD7 audit, reviewer-verified). A corrupt
  timestamp column must not be able to make the store look empty and re-open the bootstrap. Do not
  refactor the emptiness check into anything that materialises `Account` entities.
- **B5 — the bootstrap page must be reachable anonymously.** No account exists, so nobody can log in.
  §6 adds the global anonymous denial; note in your handoff that bootstrap needs exemption there, so §6
  doesn't lock the first run out.
- **B6 — Static SSR, form POST, antiforgery.** No interactive render mode (AD1) — `Program.cs` registers
  none and it stays that way. `app.UseAntiforgery()` is already wired (`Program.cs:30`); use the framework
  form handling so the antiforgery token is validated. No JS.
- **B7 — input validation stays minimal and deliberate.** Require a non-empty username, a non-empty
  password, and a confirm-password match; trim the username; apply sane max lengths. **Do not invent a
  password complexity policy** — none is specified and inventing one is a product decision. If you think
  one is needed, say so in your handoff and I'll put it to the Product Owner.
- **B8 — the password goes through `IPasswordHasher.Hash`** (Block 2). Never store or log the plaintext,
  and don't reimplement hashing. Note `Hash` throws on an empty password by design — B7's validation is
  what keeps that unreachable from the UI.
- **B9 — after success, redirect to a confirmation state** ("administrator account created, you can now
  sign in"). Do **not** redirect to `/login` — §5 hasn't built it and a 404 on success is a bad first-run
  experience. §5 will retarget this. Do **not** auto-login; sessions are §5's job.

**Out of scope:** login, sessions, cookie auth (§5); the anonymous home page and global access control
(§6); invitations (§4); the git-token UI (§7). Don't stub them.

**Tests:** both spec scenarios, plus the concurrency proof from B1 and a per-request-gate proof for B2
(bootstrap inert *without a restart* once an account appears). Reuse the in-memory SQLite +
real-`Migrate()` pattern. §9's scenario tests come later; this is the unit/integration layer.

**Human-in-the-loop — this block cannot fully self-verify.** Checking the first-run bootstrap UX in a real
browser is explicitly a Product-Owner verification step in this repo's workflow, so **3.2 will not be
ticked on automated gates alone.** Implement and self-test as far as you can, then include in your handoff
a **precise, copy-pasteable verification script for the Product Owner**: the exact commands to start from
an empty store, the URL to visit, what to enter, what they should see on success, and how to confirm the
path is then inert (including what a second visit does). Assume they will paste it verbatim.

**Gates:** the usual four. Then → @reviewer.

[worker] Block 3.1–3.3 implemented. Summary:

- **`Identity/BootstrapService.cs`** — `IsAvailableAsync` (the live gate) and
  `CreateFirstAdministratorAsync` → `BootstrapOutcome.Created` / `AlreadyBootstrapped`
  (`Identity/BootstrapOutcome.cs`). Password via `IPasswordHasher.Hash` (B8), `CreatedAt` from
  `TimeProvider`, `IsAdministrator = true` (AD6), username trimmed (B7).
- **B1 — write lock taken before the check.** Verified the API rather than assuming it:
  `SqliteConnection` on Microsoft.Data.Sqlite 10.0.10 exposes `BeginTransaction(bool deferred)` and
  `BeginTransaction(IsolationLevel, bool deferred)` — **no async overload takes `deferred`**, so EF's
  `BeginTransactionAsync()` cannot give us `BEGIN IMMEDIATE`. Implementation opens the context's own
  connection, `BeginTransaction(deferred: false)`, enlists via `Database.UseTransactionAsync`, then does
  `AnyAsync` → `Add` → `SaveChangesAsync` → commit inside it.
- **Hashing happens *before* the lock is taken.** Argon2id is ~100 ms; holding the store's single write
  lock across that would serialise every other writer behind a CPU burn. Costs a wasted hash on the
  refusal path, which is fine (and incidentally uniform in time).
- **B2 — gate is a live per-call check.** `IsAvailableAsync` is `!await db.Accounts.AnyAsync(ct)` with no
  field, no memo, no static. Startup detection (3.1) lives in `Identity/BootstrapStartupExtensions.cs`
  and **logs the answer and discards it** — the doc comment says so explicitly, so nobody later "optimises"
  it into a cached gate.
- **B4** — the check stays `AnyAsync` (`SELECT EXISTS(SELECT 1 …)`, no column materialised).
- **B3 — inert on both verbs.** GET redirects to `/` via `OnInitializedAsync`; the POST is guarded
  independently inside the service under the write lock. Verified against a real hand-crafted POST (below).
- **B6 — Static SSR, framework form handling.** `Components/Pages/Bootstrap.razor` (`EditForm` +
  `FormName="bootstrap"` + `[SupplyParameterFromForm]` + `DataAnnotationsValidator`), no interactive render
  mode, no JS; antiforgery validated by the existing `app.UseAntiforgery()`. Confirmed the rendered form
  carries `__RequestVerificationToken` and `_handler`.
- **B9** — success redirects to `/bootstrap/complete` (`Components/Pages/BootstrapComplete.razor`), which
  says the account exists and that signing in comes later. No auto-login. Refusal redirects to `/`.
- **B7** — `Components/Pages/BootstrapInput.cs`: required username (≤64, matching the column),
  required password (≤256), `[Compare]` confirmation. **No complexity policy invented** — see the question
  at the end.

**Proof for B1 — measured against three implementations, not argued.** Ran
`Concurrent_bootstrap_attempts_create_exactly_one_administrator` (8 attempts, each with its **own
`DbContext` and connection**, released together off a `TaskCompletionSource`, on a **file-backed** temp
database — the shared in-memory pattern is a single connection and cannot show this race at all):

| Implementation | Result |
|---|---|
| no transaction (`AnyAsync` → `Add` → `SaveChanges`) | **8 of 8 `Created` — eight administrators.** Reproduced on 3 of 3 runs |
| `BeginTransaction(deferred: true)` | **`SQLite Error 5: 'database is locked'`** after the 30 s busy timeout, on 3 of 3 runs |
| `BeginTransaction(deferred: false)` (shipped) | **exactly 1 `Created`, 7 `AlreadyBootstrapped`**, one row, `IsAdministrator = 1` |

Two notes on that table. The naive version is exactly as bad as B1 predicted — silently, eight times over.
The *deferred* version does **not** produce two admins on this platform: each deferred transaction holds a
shared read lock from its `AnyAsync`, so neither can upgrade to a write lock and they deadlock until the
busy timeout. So B1's conclusion is confirmed while its stated mechanism differs slightly — deferred fails
loudly (a 30 s hang then a 500) rather than over-creating. Either way it is unusable, and
`BEGIN IMMEDIATE` is the fix. Flagging because the brief's wording implies deferred over-creates.

**Proof for B2** — `Gate_closes_the_moment_an_account_appears_without_a_restart` asserts one *service
instance* answers `true`, then `false` after an account appears, then `true` again after it is deleted.
Mutation-checked: adding `_cached ??=` to `IsAvailableAsync` fails **exactly that test** and nothing else,
so it is the test that carries B2.

**Live smoke test — and it caught a bug no unit test could.** Ran the real app on an empty store and drove
the form with `curl`:
- startup logged `The identity store has no accounts. Visit /bootstrap …` (3.1 ✅);
- POST → **302 → `/bootstrap/complete`**, one row: `admin` (trimmed from `"  admin  "`),
  `IsAdministrator = 1`, `CreatedAt = 2026-07-25T15:23:07.0226560Z` (AD7 form),
  `PasswordHash` starting `$argon2id$v=19$m=65536,t=3,p=1$` (Block 2 parameters);
- **GET `/bootstrap` with no restart → 302 → `/`** (B2 + B3 GET);
- **hand-crafted POST reusing an earlier antiforgery token, skipping the GET entirely → 302 → `/`, and the
  store still holds exactly one account** (B3 POST, the guard that counts).

The bug: my first attempt satisfied analyzer BL0008 by renaming the markup-facing accessor to `Model`,
which silently broke binding — Blazor renders field names from the `@bind-Value` **expression**
(`Model.Username`) while the binder's prefix comes from the **attributed property's name**. The POST
arrived with every field empty and re-rendered the form with "Enter a username." Fixed with
`[SupplyParameterFromForm(Name = "Input")]` on a `PostedInput` backing property plus an `Input` accessor,
so rendered names and binder prefix agree; commented in place. **Unit tests could never have caught this**
— they call the service directly. Worth remembering for §4/§5's forms.

**Judgement calls:**

- **`DisplayName` defaults to the trimmed username.** The form asks only for username/password/confirm per
  B7, but `Account.DisplayName` is required. Adding a fourth field is a product decision I didn't want to
  make; there is no page in this change for editing it afterwards (§7.2 is git emails only). Say if the
  bootstrap form should collect it.
- **Refusal redirects to `/`, same as the GET.** No error page: telling an attacker "bootstrap already
  used" is information they can have, but a bare redirect is simpler and §5 will retarget both.
- **The concurrency test uses a stub `IPasswordHasher`.** Deliberate: real Argon2id would add ~100 ms of
  CPU before the lock on each of 8 tasks (and ~512 MiB), *widening* the window and making the race
  looser. The stub makes all 8 hit `BEGIN IMMEDIATE` at effectively the same instant, which is the
  sharper test. Argon2id is covered by `Created_administrator_can_be_verified_with_the_submitted_password`
  in the other file, and end-to-end by the live smoke test above.
- **`?? throw` on `UseTransactionAsync`.** It returns `IDbContextTransaction?` and is only null when null
  is passed, so this is an assertion rather than a reachable branch. The alternative was `!`.
- **Committing through EF's wrapper, not the raw `SqliteTransaction`.** Found by test: committing the raw
  transaction leaves the context still associated with it, and the *next* query on that context throws
  "The transaction object is not associated with the same connection object as this command."

**Two things for §6, so the first run doesn't get locked out:**
1. **B5 — `/bootstrap` and `/bootstrap/complete` must be exempt** from §6's global anonymous denial. Nobody
   can log in before an account exists, so gating them would make first run impossible.
2. **Non-blocking observation:** because the template sets `BlazorDisableThrowNavigationException=true`
   (`ZeroWiki.csproj:7`), `NavigateTo` does not abort rendering, so the inert GET still renders the form
   into the body of its own 302. Browsers never show it and it contains nothing secret (labels plus the
   visitor's own antiforgery token), so I have not changed it — flipping that property is a template-wide
   behaviour change beyond this block. Flagging in case §6 would rather have redirects abort rendering.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **73/73 passed** (61 + 12 new).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0). Smoke-test artifacts (`src/ZeroWiki/App_Data/`) removed; it is gitignored regardless.

❓ **@architect — password policy.** B7 says don't invent one and I haven't. My recommendation: **still
don't**, but not because it doesn't matter — because the useful control here is a *minimum length*, and
picking the number is a product call. Concretely, nothing today stops the first administrator's password
being `a`: Argon2id at 64 MiB makes offline cracking expensive, but a one-character password is guessable
online in a handful of requests, and the PO has ruled login rate limiting out of scope for this change, so
there is no compensating control. That combination is worth one sentence from the Product Owner — a single
`[MinLength(n)]` on `BootstrapInput.Password` (and later on invitation redemption) is a one-line change if
they want it. I'd propose 12 characters if they have no preference. Not blocking; the block is complete
either way.

**3.2 needs human confirmation — Product Owner verification recipe.** Everything above is automated or
`curl`-driven; the brief requires a real browser for the first-run UX. Copy-pasteable:

```bash
# 1. Start from a genuinely empty store (the file is gitignored; deleting it loses nothing else)
cd /Users/rendle/github/emmz/ZeroWiki
rm -rf src/ZeroWiki/App_Data

# 2. Run the app
dotnet run --project src/ZeroWiki
```

Then, in a browser:

1. **Watch the console as it starts.** It must log, as a warning:
   `The identity store has no accounts. Visit /bootstrap to create the first administrator account.`
2. Open **<https://localhost:7070/bootstrap>** (or <http://localhost:5171/bootstrap> if you'd rather skip
   the dev certificate). You should see the heading **"Create the first administrator"**, the explanation
   that the wiki is invite-only with no accounts yet, and three fields: Username, Password, Confirm
   password.
3. **Check the validation first:** press **Create administrator** with everything blank. The page should
   come back with *Enter a username.*, *Enter a password.*, *Confirm the password.* — and no account is
   created. Then type mismatched passwords and submit: *The passwords do not match.*
4. Now enter a username (e.g. `admin`) and the same password in both fields, and press **Create
   administrator**. The URL should become **`/bootstrap/complete`** and the page should read
   **"Administrator account created"**, noting that revisiting `/bootstrap` will not create another
   account and that signing in does not exist yet.
5. **Confirm it is now inert — without restarting the app.** In the same browser, go back to
   **`/bootstrap`**. You should be redirected to the home page (`/`) and **must not** see the form again.
   Press the browser's back button and try to re-submit as well; still no second account.
6. **Confirm exactly one account exists:**
   ```bash
   sqlite3 src/ZeroWiki/App_Data/identity.db \
     "SELECT COUNT(*) AS accounts, group_concat(Username) AS usernames, group_concat(IsAdministrator) FROM Accounts;"
   ```
   Expected: `1|admin|1` — one account, your username, administrator. The `PasswordHash` column should
   start `$argon2id$v=19$m=65536,t=3,p=1$` and contain nothing resembling your password:
   ```bash
   sqlite3 src/ZeroWiki/App_Data/identity.db "SELECT substr(PasswordHash,1,34) FROM Accounts;"
   ```
7. **Optional — confirm restart doesn't reopen it.** Stop the app (Ctrl+C) and `dotnet run` again. The
   startup log should now say `…already has at least one account; the first-administrator bootstrap path
   is inert.` and `/bootstrap` should still redirect to `/`.

If step 5 ever shows the form again, or step 6 shows more than one account, that is a failure — stop and
say so rather than working around it.

→ @reviewer

[reviewer] Block 3.1–3.3 audited. Verdict: **Request changes** — **one** blocking finding, and it is not
about any of B1/B2/B3, all three of which I reproduced by mutation and confirmed correct. Gates re-run
independently: `dotnet build` **0 warn / 0 err**, `dotnet test` **73/73**, `openspec validate
invite-only-authentication --strict` **valid**, `dotnet format --verify-no-changes` **exit 0**.

**Scope confirmed.** Tracked diff vs `71fb453` is `Program.cs` (+2: the `BootstrapService` registration
and the startup log call) plus the six new source/test files and this DEVLOG. `tasks.md` and
`openspec/specs/` are untouched. No login, no session, no cookie auth, no invitation, no git-token UI,
no anonymous-denial middleware — not even a stub. Nothing reaches outside §3.

---

**BLOCKING**

**BL1 — the refusal path burns a full 64 MiB Argon2id hash on an anonymous, permanently-reachable
endpoint. `BootstrapService.cs:58` vs `:68`/`:79`.**

Hashing outside the write lock is right, and I'd have blocked the reverse. But "outside the lock" got
conflated with "before the check": the *only* emptiness check is the one at `:79`, inside the lock, so a
POST to `/bootstrap` on a fully-bootstrapped deployment derives a 64 MiB / t=3 Argon2id hash and then
throws it away.

Measured against the real app on a populated store (5 sequential refused POSTs vs 5 control `GET /`):

| request | wall time |
|---|---|
| refused `POST /bootstrap` | **0.25, 0.25, 0.25, 0.26, 0.24 s** |
| control `GET /` | 0.00 s ×5 |

That is ~4 requests/second to saturate a core and 64 MiB of allocation per concurrent request, from an
unauthenticated route — and **B5 requires §6 to keep `/bootstrap` exempt from the global anonymous
denial**, so this endpoint stays anonymous for the life of every deployment, long after bootstrap is
over. This is not the A2/rate-limiting bucket the PO closed: nothing is being *added*. Login has to hash
to do its job; the bootstrap refusal path has to hash to do nothing.

Fix is a cheap advisory pre-filter, with the authoritative check unmoved:

```csharp
// Cheap pre-filter. The authoritative check is the one under the write lock below; this only
// avoids deriving a 64 MiB hash for a request already known to be refused.
if (await db.Accounts.AnyAsync(cancellationToken)) { return BootstrapOutcome.AlreadyBootstrapped; }

var passwordHash = passwordHasher.Hash(password);
// … BeginTransaction(deferred: false) → AnyAsync → Add → SaveChanges → Commit, unchanged
```

I verified this does not weaken B1: mutant **B** below moves the check before the lock *instead of*
keeping it under the lock, and the concurrency test catches that immediately (8 admins). Adding the
check while keeping the one at `:79` leaves that test green — all 8 attempts pass the pre-filter on an
empty store and still serialise on `BEGIN IMMEDIATE`.

On the "incidentally uniform in time" justification in your handoff: it buys nothing here. Whether
bootstrap is open is already public — the GET answers `302 →/` vs `200`, and the POST answers
`302 →/` vs `302 →/bootstrap/complete`. There is no oracle to protect, so the uniformity is paying
250 ms for nothing.

@architect — if you read this as the same bucket as the closed A2 ruling, overrule me and I'll re-audit
as a nit. I don't think it is, and I'd rather not ship a permanent anonymous amplifier that §4 and §5
can then copy.

---

**The three priority hazards — all verified by mutation, not by reading**

I rebuilt the tree in a scratch copy and ran seven mutants through the full suite and, where a unit test
could not see the difference, through real HTTP against the running app.

### B1 — "exactly one administrator" ✅ correct, and the ordering is load-bearing *and* tested

Your API claim is right, verified by reflection on `Microsoft.Data.Sqlite` 10.0.10 rather than taken on
trust. `SqliteConnection` exposes `BeginTransaction(bool deferred)` and
`BeginTransaction(IsolationLevel, bool deferred)`; the only async forms are
`BeginTransactionAsync(CancellationToken)` and `BeginTransactionAsync(IsolationLevel, CancellationToken)`
— **no `Begin*Async` overload takes a `bool` at all**, and `DatabaseFacade` exposes only
`BeginTransactionAsync(CancellationToken)`. So `BEGIN IMMEDIATE` genuinely is unreachable through the
async surface, and dropping to the raw `SqliteConnection` is not a shortcut, it is the only route.

I also probed the lock semantics directly, two connections, file-backed, 1 s busy timeout: with
`deferred: true`, a read inside A's transaction leaves a second writer free to proceed; with
`deferred: false`, the second writer is refused with `SQLite Error 5: 'database is locked'`. The write
lock is taken up front, as claimed.

**The shipped code takes the lock before the check** — `BeginTransaction(deferred: false)` at `:68`,
`AnyAsync` at `:79` — and the test suite proves that ordering is not incidental:

| mutant | `Concurrent_bootstrap_attempts_create_exactly_one_administrator` |
|---|---|
| shipped (`deferred: false`, check under the lock) | **pass** |
| **B — check moved *before* `BeginTransaction`** (lock still taken) | **FAIL — expected 1, actual 8** |
| C — no transaction at all | **FAIL — expected 1, actual 8** |
| D — `deferred: true` | **FAIL — `SqliteException: SQLite Error 5: 'database is locked'` after 30 s** |

Your table reproduces exactly, and mutant **B** is the one that matters most: it is the plausible
"tidy-up" a future hand makes (hoist the check out of the transaction), it keeps the transaction, and the
test still catches it. That is the strongest result in this block.

Your correction to B1's stated mechanism is right and my probe explains why both are true: with **no**
transaction each insert is its own autocommit unit, reads and writes never overlap, and you get eight
admins; with a **deferred** transaction spanning read+write, every attempt holds SHARED and none can
upgrade to EXCLUSIVE, so it deadlocks to `SQLITE_BUSY` instead. Different failure, same verdict.

**Refusal under contention is a refusal, not an exception** — confirmed: 1 `Created`, 7
`AlreadyBootstrapped`, no exception escapes. And the rollback path is safe to reuse the context after:
`Second_bootstrap_attempt_after_a_successful_one_is_refused` and `Bootstrap_creates_no_account_once_one
_already_exists` both query the same context *after* a refusal has disposed (and thus rolled back) the
transaction, which is exactly the disposed-transaction trap your judgement call describes. Double
disposal (EF wrapper then the outer `await using var writeLock`) is idempotent and covered by those
tests.

### B2 — the gate is live, and nothing anywhere reintroduces staleness ✅

Your mutation claim reproduces **exactly**: `_cached ??=` in `IsAvailableAsync` fails
`Gate_closes_the_moment_an_account_appears_without_a_restart` and **nothing else** — 1 failed, 72 passed.
That test carries B2 on its own.

I then swept the whole path for the staleness vectors the brief named, because the unit test only covers
the service:

- `BootstrapService` is registered **`AddScoped`** (`Program.cs:15`), not singleton — a singleton would
  in any case fail at startup on the scoped `IdentityDbContext` capture.
- `BootstrapService.cs:31-32` — no field, no memo, no static, no `Lazy`, no captured `bool`.
- `BootstrapStartupExtensions.cs:16-33` — static, no state; the answer is logged and dropped, and the
  `<remarks>` at `:9-14` says so. Nothing reads it.
- `Bootstrap.razor:61-67` — the component is instantiated per request under Static SSR; it holds form
  state only, never an availability snapshot.
- No output-caching or response-caching middleware is registered, so no cached 200 of the form page.
  A browser could still heuristically cache its own earlier GET — harmless precisely because the POST
  guard is authoritative, which is the point of B3.

Live confirmation, no restart: create the admin, then `GET /bootstrap` → **302 → `/`** on the same
running process.

### B3 — the POST guard holds with the GET check deleted ✅

The decisive test, since in the shipped code `OnInitializedAsync` runs on the POST request too, so your
hand-crafted-POST evidence cannot by itself separate the two guards. I deleted `OnInitializedAsync`
entirely (mutant **E**) and drove the real app:

| | shipped | mutant E (no GET check) |
|---|---|---|
| `GET /bootstrap`, store populated | 302 → `/` | 200, form rendered |
| `POST /bootstrap`, store populated | 302 → `/`, **1 account** | 302 → `/`, **1 account, still `admin`** |

The POST refuses on the service guard alone. B3 satisfied: the redirect is a courtesy, exactly as the
comment at `Bootstrap.razor:57-60` claims.

---

**The other binding decisions**

- **B4 ✅** — `BootstrapService.cs:32` and `:79` are both `AnyAsync`, no materialisation, and the
  `<remarks>` at `:25-30` records why. This is the §3 note from the AD7 audit honoured properly: a
  corrupt timestamp row cannot make the store look empty and re-open bootstrap.
- **B6 ✅, and antiforgery is genuinely *validated*, not merely rendered.** `grep` for
  `InteractiveServer|InteractiveWebAssembly|InteractiveAuto|AddInteractive|@rendermode|RenderMode`
  across `src/` and `tests/` returns **nothing at all** — not even the template `@using static` line.
  `Program.cs:9` is a bare `AddRazorComponents()` and `:35` a bare `MapRazorComponents<App>()`. Your
  handoff confirmed the token is *present in the markup*, which is not the same claim, so I tested the
  negative: **`POST /bootstrap` with all three fields and `_handler`, but no `__RequestVerificationToken`
  → `400`, 0 accounts.** No JS anywhere.
- **B7 ✅** — `BootstrapInput.cs:13-23`. `StringLength(64)` matches `AccountConfiguration`'s
  `HasMaxLength(64)` on `Username`; `DisplayName`'s column is 128 so the defaulted value always fits.
  Your comment at `:12` is accurate — `RequiredAttribute` does `Trim()` before the emptiness test, so
  `"   "` fails, which the `[InlineData("   ")]` case also pins at the service layer. And the password
  is correctly **not** trimmed anywhere; only the username is.
- **B8 ✅** — measured on the live row: `PasswordHash` starts `$argon2id$v=19$m=65536,t=3,p=1$` (Block 2
  parameters), `SELECT … WHERE (Id||Username||DisplayName||PasswordHash||CreatedAt) LIKE '%passphrase%'`
  returns **0**, and the plaintext appears **0** times in the whole server log. `IsAdministrator = 1`
  (AD6), `CreatedAt = 2026-07-25T15:34:03.6519750Z` — AD7's fixed-width form.
- **B9 ✅** — 302 → `/bootstrap/complete`, no auth cookie in the response, no session code anywhere.
- **B5** — handoff note present and correct; recorded for §6 below.

---

**The form-binding item — (a) the fix, (b) the guard**

**(a) The fix is correct, and the shape is justified — I checked the "simpler" alternative rather than
assuming.** BL0008 is real on this SDK and fires exactly as you describe: rebuilding the page in the
official ASP.NET Core Identity shape (`[SupplyParameterFromForm] private BootstrapInput Input { get; set;
} = new();`) produces

> `warning BL0008: Property 'ZeroWiki.Components.Pages.Bootstrap.Input' has [SupplyParameterFromForm] and
> a property initializer. This can be overwritten with null during form posts.`

— 1 warning, against a 0-warning gate. So the two-member shape is not over-engineering, it is the price
of the analyzer. (For the record that shape *works*, because there the attributed property is itself
named `Input`; the bug only appears once the two names diverge.)

I reproduced the actual bug to confirm the diagnosis is the right one (mutant **F**, `Name` removed):
rendered field names stay `Input.Username` / `Input.Password` / `Input.ConfirmPassword` in **both**
variants, and the POST comes back **200 with "Enter a username. / Enter a password. / Confirm the
password."** and **0 accounts**. Render names from the expression, binder prefix from the attributed
property — exactly your account of it.

**No residual mismatch for this form.** The one soft spot is that `Name = "Input"` and the accessor
`Input` are coupled by a string the compiler does not check. Two shapes that would bite differently:
a page with two `[SupplyParameterFromForm]` models needs a distinct `Name` per accessor, and any rename
of `Input` silently desynchronises. Cheap fix, nit N2 below: **`Name = nameof(Input)`**.

**(b) Yes — a test-level guard is worth adding, and here is the measurement that settles it.** Under
mutant F the form is completely non-functional over HTTP and **the unit suite passes 73/73**. A silently
empty post returning 200 is invisible to every test in this repo, and §4, §5 and §7 each add more Static
SSR forms to the same trap.

The guard is one `WebApplicationFactory<Program>` test: GET the page, scrape `__RequestVerificationToken`
and `_handler` out of the markup, POST the form, assert `302 → /bootstrap/complete` and one account. That
single test kills mutant F, kills the missing-antiforgery case, kills a `FormName`/`_handler` regression,
and kills an accidental interactive render mode — and the scraping harness is reusable by §4/§5/§7
verbatim. It needs `Microsoft.AspNetCore.Mvc.Testing` and `Program` made visible to the test project.

**@architect — this is your call, not the worker's.** It is arguably §9's scenario layer, but §9 lands
*after* three more forms have been written against the trap, so a small harness unit now (the way AD7 was
slotted in ahead of §3) buys more than the same work later. I am not blocking on it.

---

**Nits (non-blocking)**

- **N1 — `[SupplyParameterFromForm(Name = "Input")]` → `Name = nameof(Input)`** (`Bootstrap.razor:51`).
  Same value, but a rename of the accessor now updates it, so the binder prefix cannot silently drift
  from the rendered field names again. One token; the explanatory comment above it stays worth keeping.
- **N2 — `/bootstrap/complete` states something untrue before bootstrap** (`BootstrapComplete.razor:8`).
  Measured: it returns **200 to anyone at any time**, including on an empty store, asserting "The first
  administrator account now exists". Harmless, but it is a page §6 must exempt (B5) that renders
  unconditionally. Consider redirecting to `/bootstrap` when `IsAvailableAsync()` is still true, so the
  exempted pair is self-consistent.
- **N3 — open the connection through EF** (`BootstrapService.cs:60-64`). `connection.OpenAsync()` on the
  raw `DbConnection` bypasses `RelationalConnection`'s open-count bookkeeping;
  `db.Database.OpenConnectionAsync(cancellationToken)` is the supported form and keeps EF's accounting
  straight. No observable defect today — the context owns and disposes the connection at end of scope —
  but the cast at `:60` plus the manual open is the kind of thing that stops being harmless the moment a
  connection is shared.
- **N4 — password minimum length (your ❓, as a nit per @architect).** I agree with your recommendation
  and would push for it: with rate limiting ruled out, the *first administrator* — the highest-privilege
  account in the system, and the only one created before any policy exists — can currently have a
  one-character password. **12 is a reasonable floor**; 8 is the NIST SP 800-63B minimum and I would not
  go below it. A single `[MinLength(12)]` on `BootstrapInput.Password`, mirrored on §4's redemption form.
  Product Owner's number, not mine.
- **N5 — the concurrency test's stub hasher is sound, confirmed.** Real Argon2id would put ~100 ms and
  64 MiB *ahead* of the lock on each of 8 tasks, spreading their arrival at `BEGIN IMMEDIATE` and making
  the race looser, not tighter — plus ~512 MiB peak in a test process. The stub is the sharper test and
  the real hasher is covered by `Created_administrator_can_be_verified_with_the_submitted_password` and
  by the live run. Keep it. (It is also what keeps mutants B/C failing in under a second rather than
  minutes.)
- **N6 — your other judgement calls, all accepted.** `DisplayName` defaulting to the trimmed username is
  right for this block — a fourth field is a product decision and there is no edit page until §7 anyway;
  say so to the PO and move on. Refusal redirecting to `/` is fine and §5 retargets both. `?? throw` on
  `UseTransactionAsync` is the correct choice over `!` — it is an assertion with a message rather than a
  silent suppression, and this repo's bar is no null-forgiving to dodge a real null. Committing through
  EF's wrapper is not merely a preference, it is required, and the test that found it is the reason to
  trust it.

---

**Answer to the `BlazorDisableThrowNavigationException` observation**

Agreed it is harmless as *disclosure* — the 3,525-byte body carries labels and the visitor's own
antiforgery token, nothing else, and no client ever renders it — **but it is not inert**: it is precisely
why `SubmitAsync` still executes after `OnInitializedAsync` has already redirected on a populated store,
which is what makes BL1's wasted Argon2 hash reachable at all. Fix BL1 in the service (defence where it
belongs) rather than by flipping a template-wide property, and the observation goes back to being purely
cosmetic for §6.

---

**Architectural notes (not blocking this block)**

- **Username charset is unconstrained, and the bootstrap account is the one that can't be fixed later.**
  `BootstrapInput.Username` validates length only, so `:`, whitespace and control characters are all
  accepted. §8 makes the username the Basic-auth username on the Smart HTTP remote (where `:` is
  structurally illegal in `user:pass`), and §7 will put it in URLs and UI. B7 said minimal validation and
  I am not asking for more here — but every *other* account is created via §4's redemption, which can
  adopt a charset rule later, whereas the first administrator is created **before any rule exists** and
  there is no rename page anywhere in this change. Worth deciding the rule at §4 and applying it here in
  the same breath.
- **B5 is now load-bearing in two directions.** §6 must exempt `/bootstrap` *and* `/bootstrap/complete`,
  and BL1 is what that exemption costs if left unfixed. Recording it so §6 inherits both halves.

---

**3.2 — needs human confirmation, not complete.** The verification recipe is precise, copy-pasteable and
covers the right things (empty store → startup warning → validation → success → inert without restart →
one row → restart). I have exercised every step of it over HTTP except the browser rendering itself, and
all of it behaved as written. It still must not be ticked until the Product Owner confirms in a real
browser.

→ @worker — **BL1 only** (`BootstrapService.cs:58`): pre-filter the emptiness check before the hash,
leaving the authoritative check under the write lock exactly where it is; confirm the concurrency test
stays green and re-post. N1–N6 are yours to take or leave with a line of reasoning. Everything else in
this block is clean and the B1/B2/B3 work is the strongest evidence I've audited in this change.

[architect] Ruling on the Block 3 audit.

**BL1 upheld as blocking.** And it is explicitly **not** the A2 bucket the Product Owner closed: nothing
is being added, no rate limiter, no gate, no throttle. The fix is *not doing 64 MiB of expensive work on a
request we have already decided to refuse* — a cheap `AnyAsync` pre-filter before the hash, with the
authoritative under-lock check staying exactly where it is. Reading the PO's scope ruling as "therefore
leave a 0.25 s / 64 MiB anonymous amplifier in place" would be a misuse of it. B5 makes `/bootstrap`
permanently anonymous, so this never ages out on its own. Keep both checks; the reviewer already
confirmed that leaves the concurrency proof green, and mutant B (check hoisted above the lock) is what
catches a fix that goes too far.

**Integration test harness — approved, build it now. My call, per the reviewer's referral.** The
measurement decides it: under mutant F the form is **entirely non-functional over HTTP while 73/73 unit
tests pass**. A suite that stays green while the only user-facing path in the block is dead is not
telling us what we think it is. One `WebApplicationFactory<Program>` test (GET → scrape the antiforgery
token → POST → assert 302 and exactly one account) kills that mutant plus the missing-token case, a
`FormName` regression, and an accidental render mode. Scope-wise this belongs to §3, not §9: it is the
evidence that 3.2/3.3 actually work, and §9's scenario tests are a later layer over the same harness.
Build it **minimal and reusable** — §4, §5 and §7 each add a Static SSR form and will hit the identical
trap; discovering it three more times by hand is the alternative I'm rejecting.

**AD10 — apply the Product Owner's 12-character password minimum in this round** (see `## NEXT`). Minimum
length only: no composition rules, no strength meter, no extra UI — those were explicitly not chosen. §4's
redemption form must carry the same minimum so the two paths cannot diverge.

**AD11 — username charset, decided now rather than at §4.** The reviewer is right that the bootstrap admin
is the only account created before any policy exists and there is no rename page anywhere in this change,
so "decide it later" means "the first admin is permanently outside whatever we decide". Binding rule:
**1–64 characters, ASCII letters, digits, `.`, `-`, `_`, and at least one alphanumeric.** Apply it to
bootstrap now and to §4 redemption when it lands.

I'm treating this as an Architect call rather than a Product Owner one because the load-bearing part is a
**technical constraint, not a preference**: §8 uses the username as the Basic-auth userid, where a colon
is structurally illegal (RFC 7617 forbids it in the userid), and whitespace/control characters in a
credential are a correctness hazard rather than a style choice. The rule is deliberately the *minimum*
that satisfies that constraint — I have not invented a vanity-name policy on top. If the Product Owner
later wants something stricter, tightening a charset is backward-compatible in a way that loosening a
username people already hold is not.

**Nits: take N1, N2, N3.** `nameof(Input)` (an uncompiled string naming a property is exactly the failure
that just cost a live smoke test to find); `/bootstrap/complete` must not return 200 claiming an
administrator exists when the store is empty; and `db.Database.OpenConnectionAsync` over the raw
connection call. N4–N6 are already answered or accepted.

→ @worker — BL1, the integration harness, AD10, AD11, and N1–N3.

[worker] Round 1 fixes applied — BL1, the integration harness, AD10, AD11, N1–N3. No disagreements.

**BL1 — fixed, and the fix is measured.** Added a cheap `AnyAsync` pre-filter *before* the hash
(`BootstrapService.cs:55-63`); the authoritative check under the write lock is untouched. The comment
says explicitly that moving the under-lock check up here *instead of* adding this one would reopen the
race, so the next reader can't "tidy" it into mutant B. Measured against the real app on a populated
store, same method as the reviewer's:

| request | before | after |
|---|---|---|
| refused `POST /bootstrap` | 0.24–0.26 s | **0.0017–0.0064 s** |
| control `GET /` | 0.0008–0.0014 s | 0.0008–0.0014 s |

~100× down, and now within noise of a request that does nothing. Conceded on the "incidentally uniform
in time" line in my last handoff — the reviewer is right that there is no oracle to protect here (the GET
already answers 200 vs 302), so it was paying 250 ms for nothing.

**I re-ran your mutant B to be sure the fix didn't go too far** — deleted the *under-lock* check, keeping
only the new pre-filter: `Concurrent_bootstrap_attempts_create_exactly_one_administrator` fails
**expected 1, actual 8**. So the pre-filter is provably advisory and the under-lock check is provably
load-bearing. With both in place the concurrency test is green.

**Integration harness — built, minimal, and it kills mutant F.** Three files under
`tests/ZeroWiki.Tests/Web/`:
- `ZeroWikiAppFactory` — `WebApplicationFactory<Program>` over a throwaway SQLite file
  (`UseSetting("ConnectionStrings:IdentityDb", …)`, temp file deleted on dispose), a
  redirect-surfacing `CreateHttpClient()`, and `GetAccountsAsync()`. `Program.cs` gained
  `public partial class Program;` so the factory can name it.
- `StaticSsrForm` — fetch a page, carry back its hidden fields (`_handler`, antiforgery token), post.
  **It reads the field names out of the rendered markup rather than restating them**, which is the
  whole point: a form whose rendered names have drifted from its binder prefix fails here.
- `BootstrapPageTests` — 6 facts + 7 theory cases over real HTTP.

Verified it earns its place by re-applying **mutant F** (`Name` removed): previously 73/73 green with the
form completely dead; now **6 integration tests fail**. It also covers the missing-antiforgery case
(400, 0 accounts), which I'd only checked by hand before.

**AD10 — 12-character minimum** on `BootstrapInput.Password`. Length only; no composition rules, no
meter, no extra UI. **AD11 — username charset** as ruled: `^[A-Za-z0-9._-]*[A-Za-z0-9][A-Za-z0-9._-]*$`
with the existing `StringLength(64)` supplying the length bound. Both constants live in
`Identity/CredentialPolicy.cs` so §4's redemption form references the same values rather than restating
them — that's the "natural" factoring AD10 allowed, not a new abstraction layer: one static class, four
consts and a message, no behaviour. The XML doc records that AD11's reason is RFC 7617's illegal colon in
the Basic-auth userid, not taste. Covered by theory cases both ways — `has space`, `colon:name`, `___`
(no alphanumeric) and `café` rejected; `admin`, `a.b-c_1`, `_x_` accepted.

Two notes on AD11 you may want to sanity-check: I set `MatchTimeoutInMilliseconds = 250` on the
`[RegularExpression]`, because length and pattern validation run independently so the pattern can still
be handed a very long string; and I put the rule **only** on the form, not in `BootstrapService`, because
Blazor runs DataAnnotations validation *server-side* — `OnValidSubmit` never fires for an invalid model,
so a hand-crafted POST is validated too. The service keeps its blank-username/blank-password guards.
Say the word if you'd rather see the charset re-asserted at the service boundary as well.

**N1** `Name = nameof(Input)`. **N2** `/bootstrap/complete` now redirects to `/bootstrap` while the store
is empty, so the exempted pair is self-consistent; new test asserts both directions. **N3**
`db.Database.OpenConnectionAsync(cancellationToken)`; the raw `SqliteConnection` is still fetched
afterwards because only the non-async overload takes `deferred`, and the comment says so.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **84/84 passed** (73 + 11 new: 6 facts
+ 7 theory cases, less none removed). `openspec validate invite-only-authentication --strict` — valid.
`dotnet format --verify-no-changes` — clean (exit 0). `BlazorDisableThrowNavigationException` untouched.
Smoke-test `App_Data/` removed.

---

**3.2 — Product Owner verification recipe (REISSUED; supersedes the one in my previous post).** Updated
for AD10 (12-character minimum) and AD11 (username charset). Stands alone; paste verbatim.

```bash
# 1. Start from a genuinely empty store (the file is gitignored; deleting it loses nothing else)
cd /Users/rendle/github/emmz/ZeroWiki
rm -rf src/ZeroWiki/App_Data

# 2. Run the app
dotnet run --project src/ZeroWiki
```

Then, in a browser:

1. **Watch the console as it starts.** It must log, as a warning:
   `The identity store has no accounts. Visit /bootstrap to create the first administrator account.`
2. Open **<https://localhost:7070/bootstrap>** (or <http://localhost:5171/bootstrap> if you'd rather skip
   the dev certificate). You should see the heading **"Create the first administrator"**, the explanation
   that the wiki is invite-only with no accounts yet, and three fields: Username, Password, Confirm
   password.
3. **Empty submit.** Press **Create administrator** with all three fields blank. The page comes back with
   *Enter a username.*, *Enter a password.* and *Confirm the password.* No account is created.
4. **Short password.** Username `admin`, and `secret123` in both password fields. You should get
   **"A password must be at least 12 characters."** and no account. *(This is the new 12-character
   minimum — AD10.)*
5. **Bad username.** Username `admin user` (with a space), and a password of at least 12 characters in
   both fields. You should get **"A username can use letters, digits, dots, hyphens and underscores, and
   must contain at least one letter or digit."** and no account. `admin:user` and `café` are rejected the
   same way; `admin`, `a.b-c_1` and `_x_` are all accepted. *(This is the new charset rule — AD11.)*
6. **Mismatched passwords.** Username `admin`, then two *different* passwords of at least 12 characters.
   You should get **"The passwords do not match."** and no account.
7. **Now create it for real.** Username `admin`, and the same password of **at least 12 characters** in
   both fields — e.g. `correct horse battery staple`. Press **Create administrator**. The URL should
   become **`/bootstrap/complete`** and the page should read **"Administrator account created"**, noting
   that revisiting `/bootstrap` will not create another account and that signing in does not exist yet.
8. **Confirm it is now inert — without restarting the app.** In the same browser, go back to
   **`/bootstrap`**. You should be redirected to the home page (`/`) and **must not** see the form again.
   Press the browser's back button and try to re-submit as well; still no second account.
9. **Confirm exactly one account exists:**
   ```bash
   sqlite3 src/ZeroWiki/App_Data/identity.db \
     "SELECT COUNT(*), group_concat(Username), group_concat(IsAdministrator) FROM Accounts;"
   ```
   Expected: `1|admin|1` — one account, your username, administrator. And confirm the password was
   hashed, not stored:
   ```bash
   sqlite3 src/ZeroWiki/App_Data/identity.db "SELECT substr(PasswordHash,1,34) FROM Accounts;"
   ```
   Expected: it starts `$argon2id$v=19$m=65536,t=3,p=1$` and contains nothing resembling what you typed.
10. **Optional — confirm a restart doesn't reopen it.** Stop the app (Ctrl+C) and `dotnet run
    --project src/ZeroWiki` again. The startup log should now say `…already has at least one account;
    the first-administrator bootstrap path is inert.`, `/bootstrap` should still redirect to `/`, and
    `/bootstrap/complete` should still show the confirmation.

If step 8 ever shows the form again, or step 9 shows more than one account, that is a failure — stop and
say so rather than working around it.

→ @reviewer

[reviewer] Round-1 delta re-audited. Verdict: **Request changes** — **one** blocker, and it is in the new
AD11 work, not in anything from the last round. **BL1 is genuinely fixed, B1/B2/B3 all survive the change,
and the harness earns its place** — all verified by mutation. Gates re-run independently: `dotnet build`
**0 warn / 0 err**, `dotnet test` **84/84**, `openspec validate invite-only-authentication --strict`
**valid**, `dotnet format --verify-no-changes` **exit 0**.

**Scope still clean.** Tracked diff adds only `CredentialPolicy.cs`, `tests/…/Web/`, the
`Microsoft.AspNetCore.Mvc.Testing` reference and `public partial class Program;`. `grep` for
`signin|login|CookieAuth|AddAuthentication|UseAuthorization|Invitation(Service|Page)|GitTokenPage` across
`src/` returns **nothing**; `tasks.md` and `openspec/specs/` untouched; no render mode anywhere.

---

**BLOCKING — BL2: the AD11 username regex is quadratic, and it is reachable anonymously. It reinstates
BL1's amplifier through the validation path, and adds a 500.** `CredentialPolicy.cs:33`, consumed at
`BootstrapInput.cs:17-20`.

`^[A-Za-z0-9._-]*[A-Za-z0-9][A-Za-z0-9._-]*$` puts two overlapping `*` quantifiers either side of a single
required character. On input that is valid-charset-then-invalid, every split point has to be tried.
Measured, clean 4× per doubling:

| input length | match time |
|---|---|
| 1,000 | 3.0 ms |
| 4,000 | 9.2 ms |
| 16,000 | 142 ms |
| 64,000 | **2,288 ms** |

`MatchTimeoutInMilliseconds = 250` does not fix this, it *bounds* it — at exactly the figure we just spent
a blocker removing. And `RegularExpressionAttribute` does not catch `RegexMatchTimeoutException`, so it
propagates. Over real HTTP, 200,000-character username:

| store state | result |
|---|---|
| empty | `status=500`, **0.260 s** |
| **already bootstrapped** | `status=500`, **0.253 s** |

That second row is the point. DataAnnotations validation runs **before** `OnValidSubmit` calls the
service, so BL1's pre-filter — which does work, measured below — is simply never reached. `/bootstrap`
is permanently anonymous under B5, `FormOptions.ValueLengthLimit` allows 4 MB per field, and the result
is 250 ms of CPU plus an unhandled exception per request, from an unauthenticated endpoint. Same
amplifier, same route, one round later, through a different door.

**The fix is to bound the pattern's work, not to time-box it.** I measured three candidates through the
attribute, and one is clearly right:

| pattern | 50 K chars | 1 M chars | decisions changed vs shipped |
|---|---|---|---|
| shipped | 253 ms **TIMEOUT-THROW** | 250 ms **TIMEOUT-THROW** | — |
| `^(?=.{1,64}\z)…` | 0.3 ms reject | 0.0 ms reject | rejects at 65 → doubles up with `StringLength`'s message |
| **`^[A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z`** | **0.0 ms reject** | **0.0 ms reject** | **none within the length `StringLength(64)` admits** |
| `^(?=.*[A-Za-z0-9])[A-Za-z0-9._-]+\z` | 0.1 ms | 1.1 ms | none (linear, not constant) |

I ran all 31 inputs — every AD11 case, both charset directions, control characters, homoglyphs, 64/65/200
chars — through each. The bounded-quantifier form differs from the shipped pattern on exactly one input
(200 chars), which `StringLength(64)` already rejects. So it is behaviour-preserving where it matters and
constant-time everywhere. Keep the 250 ms timeout as a belt once it can no longer be handed unbounded
work. One wart to expect: `[RegularExpression]` needs a compile-time constant, and `{0,63}` cannot be
composed from `MaximumUsernameLength` in a `const string`, so the `63` has to be a literal with a comment
tying it to the constant.

**Use `\z`, not `$`, while you are in there — and this is not cosmetic.** `$` matches before a trailing
newline, so `Regex.IsMatch("admin\n", UsernamePattern)` is **true**. Today that is inert, and I want to be
precise about why: `RegularExpressionAttribute.IsValid` additionally requires `m.Length ==
stringValue.Length`, so the *attribute* rejects it (confirmed both by direct measurement —
`Regex.IsMatch=True, match.Length=5/6, attribute.IsValid=False` — and end-to-end over HTTP, where
`admin\n` came back with the charset message and created nothing). But `UsernamePattern` is a **public
shared constant whose entire purpose is reuse by §4** — and by the service-boundary guard discussed below,
which would naturally be written as `Regex.IsMatch(...)` and **would** admit `admin\n`. A credential
validator that is safe only because one particular consumer happens to add a length check is the exact
shape this thread has now rejected twice. `\z` makes it correct under either call style; verified.

---

**BL1 — fixed, and I confirmed both halves of your claim independently**

- The pre-filter sits at `BootstrapService.cs:60-63`, **before** the hash at `:68`; the authoritative
  check is untouched at `:88`. Measured on the real app, populated store: refused POST **0.0016 s /
  0.0018 s** against 0.24–0.26 s last round. ~150×, and within noise of the control GET.
- **Your mutant-B re-run reproduces exactly.** I deleted the under-lock check while keeping the
  pre-filter: `Concurrent_bootstrap_attempts_create_exactly_one_administrator` fails **expected 1, actual
  8**. The pre-filter is provably advisory and the under-lock check provably load-bearing, which is the
  thing that had to stay true. The comment at `:55-59` naming that trap explicitly is the right artefact
  to leave behind.

**B1 / B2 / B3 — still intact after the change.** Re-ran the whole battery against the new service:

| mutant | result | reading |
|---|---|---|
| cached gate (`_cached ??=`) | **1 failure**, `Gate_closes_the_moment_an_account_appears_without_a_restart` | B2 intact, still carried by exactly that test |
| pre-filter only, under-lock check deleted | **1 failure**, concurrency, expected 1 actual 8 | B1 intact |
| GET redirect deleted | **1 failure**, and no second account created | B3 intact — the POST guard still refuses alone |
| `[SupplyParameterFromForm]` with no `Name` | **6 failures** | see below |

`Bootstrap.razor` now also has two independent refusals in front of a POST (pre-filter and under-lock
check), so B3 is strictly stronger than last round, not weaker.

---

**The integration harness — approved, and it does what it was built for**

Mutant F (the `Name` removed) previously left **73/73 green with the form entirely dead**. It now fails
**6 integration tests**. That is the whole justification, discharged.

**Reusability: yes, and it is not over-built.** Three files, ~140 lines, no fixtures, no base classes, no
DI gymnastics. `StaticSsrForm` is the load-bearing piece and it generalises cleanly — taking the field
names *out of the rendered markup* (`StaticSsrForm.cs:30-37`) is what makes it a drift detector rather
than a restatement, and §4/§5/§7 can use it verbatim. `CreateClient` carries cookies by default, which
§5's session tests will need. `ZeroWikiAppFactory.GetAccountsAsync()` is the only bootstrap-flavoured
part; when §4 wants invitations it will want a general `WithDbAsync(...)` instead — a two-line
generalisation then, not a design flaw now.

---

**Your two sanity-checks, answered**

**(a) The timeout is the wrong answer; length must gate the pattern.** Fully covered by BL2 above — the
timeout converts an unbounded hang into a bounded 250 ms burn plus a 500, which is a *worse* failure than
the one it prevents and the exact resource profile BL1 was upheld to remove. Your instinct that the
pattern can be handed a very long string is right; the conclusion drawn from it was the wrong one.

**(b) The charset is correct — 25 of 26 cases, and the 26th is the `$` finding above.** Verified against
the real attribute, not by eye:

- **Accepted, correctly:** `admin`, `a.b-c_1`, `_x_`, `A1`, `x`, `1`.
- **Rejected, correctly:** `has space`, `___`/`...`/`-.-` (no alphanumeric), `café`, `adımin` (dotless-i
  homoglyph), embedded NUL, tab, newline, leading newline, trailing/leading space, `admin/../x`,
  `admin@host`, `admin%20x`, zero-width space, empty.
- **The colon requirement holds in every position** — `colon:name`, `:`, `admin:`, `:admin` all rejected.
  RFC 7617 satisfied, which is the one rule with a hard technical requirement behind it.
- The single hole is `admin\n` at the raw-`Regex` level, inert via the attribute, live for any other
  consumer — see BL2's `\z` note.

---

**My answer on the service boundary: I agree with you, and I'd go further — the argument is now
concrete rather than hypothetical.**

The worker's reasoning is correct as far as it goes: Blazor does run DataAnnotations server-side, and I
confirmed it over HTTP (`has space`, `admin:user`, `café`, a 9-character password and a mismatch are all
refused with their messages and zero accounts, with no hand-crafted POST able to skip it). So this is not
a live hole in the web path.

But that is a statement about **today's only caller**, and the contract belongs to the service:

1. **`BootstrapService` already half-asserts it.** `ArgumentException.ThrowIfNullOrWhiteSpace(username)`
   at `:50` says blank is a caller error. It is incoherent for `""` to throw while `"a:b"` — the value
   with an actual RFC 7617 consequence in §8 — is silently persisted. Adding the rest is *completing* an
   existing guard, not adding ceremony.
2. **The duplication objection does not apply here, because `CredentialPolicy` already exists.** Both
   sites reference the same constants; they cannot drift. That class was created for precisely this.
3. **The store is what needs protecting, not the form.** There is no rename page anywhere in this change,
   §4 creates accounts through a different path, and §9 calls services directly. "Valid because of how it
   is reached" is what we rejected in Block 2 and again in BL1.
4. **BL2 makes this non-theoretical**: the natural way to write that guard is
   `Regex.IsMatch(username, CredentialPolicy.UsernamePattern)`, which today would admit `admin\n`. Fix the
   pattern first, then add the guard.

**Throw, don't return an outcome.** `BootstrapOutcome` answers a *domain* question — "was the store
empty?" — and folding a caller-error signal into it would force every caller to distinguish "someone else
got there first" from "you passed garbage", change the page's refusal redirect semantics, and duplicate
what the form already reports far more helpfully. `ArgumentException` matches the guards already at
`:50-51` and the existing `Blank_username_is_rejected_before_anything_is_written` test shape.

Two implementation details that matter: **validate the trimmed value** (so `Username_is_trimmed` stays
green and `"  alice  "` remains acceptable), and **put the guard before the hash at `:68`**, or it
reintroduces BL1 for that path.

---

**Nits (non-blocking)**

- **N1 — the charset theory is vacuous against a broken form.**
  `BootstrapPageTests.cs:81-94` asserts only `200` + no accounts, both of which a *completely dead* form
  satisfies. Proof: it is **not** among mutant F's 6 failures — it passes while the form posts nothing.
  Assert the AD11 message text, exactly as `A_password_below_the_minimum_length_is_rejected_and_creates
  _nothing` (`:74-77`) already does. Then the four cases test the charset instead of testing 200.
- **N2 — AD10's message is a magic string; AD11's isn't.** `BootstrapInput.cs:26` hard-codes `"at least
  12"` beside `CredentialPolicy.MinimumPasswordLength`, and `:16` hard-codes `"at most 64"` beside
  `MaximumUsernameLength` — while AD11 correctly keeps its wording in `UsernameRuleDescription`
  (`CredentialPolicy.cs:41`). Raise the minimum and the message lies. Move both next to their numbers, for
  the same reason the charset message already lives there.
- **N3 — the form now rejects what the service trims.** `Username = "admin "` fails AD11 (space), yet
  `BootstrapService.cs:53` still trims. Trailing whitespace is what a paste produces, and the user gets a
  charset message that never mentions spaces. The service's `Trim()` is now unreachable from the web path.
  Either trim before validating, or accept it and know the trim is now only a service-boundary courtesy —
  worth one deliberate line either way, especially since §4's form will inherit the same shape.
- **N4 — the harness doesn't pin the environment**, so it tests whatever `WebApplicationFactory`
  defaults to. Fine today; §6 will care, because the `Development` branch skips `UseExceptionHandler`
  and HSTS. A note for when §6 lands, not a change now.

---

**Product Owner recipe — one step is wrong and would strand them at the first click. Everything else I
drove verbatim and it is accurate.**

**Step 2 is incorrect.** It offers `https://localhost:7070/bootstrap` as the primary URL. But
`dotnet run --project src/ZeroWiki` — the command in step 2's own code block — selects the **first**
launch profile, which is `http`, and binds **only** `http://localhost:5171`:

```
Now listening on: http://localhost:5171
https://localhost:7070/bootstrap -> CONNECTION REFUSED (curl exit 7)
http://localhost:5171/bootstrap  -> 200
```

The Product Owner will paste the primary URL, get a browser connection error, and have no way to know the
recipe is at fault rather than the app. Fix by either making `http://localhost:5171/bootstrap` the stated
URL (it is also the one the console prints, so it self-corrects), or changing step 2's command to
`dotnet run --project src/ZeroWiki --launch-profile https`. Do not leave both URLs with the wrong one
first.

**Everything else verified verbatim against a real run:**

| step | claim | result |
|---|---|---|
| 1 | startup warning text | exact match |
| 3 | blank → three messages | `Enter a username.` / `Enter a password.` / `Confirm the password.`, 0 accounts |
| 4 | `secret123` → 12-char message | exact match, and **only** that message |
| 5 | `admin user` → charset message | exact match; `admin:user` and `café` identical, 0 accounts |
| 6 | mismatch → `The passwords do not match.` | exact match |
| 7 | `correct horse battery staple` → `/bootstrap/complete` | 302 → `/bootstrap/complete` |
| 8 | inert without restart | `GET /bootstrap` → 302 → `/` |
| 9 | `1\|admin\|1`, hash prefix | exact match; password appears nowhere in the row |
| 10 | restart → inert log line | exact match: *"The identity store already has at least one account; the first-administrator bootstrap path is inert."*, `/bootstrap` → 302 → `/`, `/bootstrap/complete` → 200 |

(One caution for whoever re-runs this: a stale app still holding port 5171 makes a *new* `dotnet run`
fail to bind while the browser keeps talking to the old one, which looks exactly like a bootstrap bug. It
cost me a false alarm. Worth a line in step 2 — "if the console does not say `Now listening on`, an older
instance is still running".)

**3.2 still needs the Product Owner's browser confirmation** — unchanged, and it must not be ticked until
they have run the corrected recipe.

→ @worker — **BL2 only**: bound the pattern's work (the measured bounded-quantifier form) and switch `$`
to `\z`; plus the step-2 URL in the recipe. N1–N4 are yours to take or leave with a line of reasoning, and
the service-boundary guard is @architect's ruling to make — my view is above. Everything else in this
delta is clean, and the BL1 fix plus the harness are exactly what was asked for.

[architect] Ruling on the re-audit. **BL2 upheld — and it is my mistake, not the worker's.** AD11 is my
decision; I specified a charset and a shape and left the pattern to implementation, and the natural
reading of it is quadratic. The result is that the rule I introduced to close a §8 correctness hazard
reinstated the very amplifier BL1 had just removed, one round later and through an earlier door —
DataAnnotations runs *before* `OnValidSubmit`, so BL1's pre-filter never even executes. Worth stating
plainly because it generalises: **a validation rule is attacker-reachable code**, and on a permanently
anonymous route its cost is part of its specification. Any future credential rule in this change gets
costed the same way.

- **Take the measured bounded form** `^[A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z` (0.0 ms at
  1 MB, and no decision changes within the length `StringLength(64)` admits). **Keep the timeout as a
  belt, not as the fix** — the reviewer is right that a timeout converts an unbounded hang into a bounded
  burn *plus* a 500, which is worse than what it prevents. Length gates the pattern; the timeout is what's
  left over.
- **`\z`, not `$`** — non-negotiable, and the reason is the one that matters: `$` matches before a
  trailing newline, so `admin\n` passes a bare `Regex.IsMatch`. The attribute happens to reject it today
  only because it also requires a full-length match. `UsernamePattern` is a public constant whose entire
  purpose is reuse by §4 and by the service guard below — a validator that is safe only because one
  particular caller adds a length check is the exact shape this thread has now rejected twice.

**Service-boundary guard — confirmed, implement it.** The reviewer attacked my lean and agreed with it for
a better reason than I had: `BootstrapService` *already* half-asserts this (`ThrowIfNullOrWhiteSpace`), so
it is incoherent for `""` to throw while `"a:b"` — the value with an actual RFC 7617 consequence in §8 —
is silently persisted. **Throw `ArgumentException`, not a new `BootstrapOutcome`**: the outcome type
answers a domain question ("did someone beat you to it"), and folding caller-error into it would force
every caller to distinguish garbage input from a lost race. Validate the **trimmed** value, and place the
guard **before the hash** — otherwise it reintroduces BL1 for that path, which is the whole lesson of this
round.

**Nits — take all four.** N1 (assert the charset messages' text, as the password test does; a theory that
passes against a dead form proves nothing — it wasn't among mutant F's six failures). N2 (AD10's messages
hard-code "12"/"64" while AD11's correctly reads from `CredentialPolicy` — make them consistent). N3
(**resolve the trim disagreement rather than papering over it**: the form now rejects `"admin "` while the
service trims it. Bind: **trim first, then validate**, in both places, so the two paths cannot disagree
about what a username *is*). N4 (pin the environment in the harness — cheap now, and §6's access-control
tests will depend on it).

**The recipe URL is a genuine save.** `dotnet run` binds only `http://localhost:5171`, so the recipe's
primary `https://localhost:7070` is connection-refused — it would have stranded the Product Owner at the
first click, on the one step where they cannot tell a bad instruction from a broken feature. Fix step 2,
and add the stale-instance caution the reviewer hit.

→ @worker — BL2, the service-boundary guard, N1–N4, and the recipe fix.

[worker] Round 2 fixes applied — BL2, the service guard, N1–N4, recipe. No disagreements; two findings
of my own came out of the work, both below.

**BL2 — bounded pattern + `\z`.** `UsernamePattern` is now
`@"^[A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"` (verbatim literal, because `\z` is not a
valid C# escape). Timeout kept at 250 ms and its doc comment now says it is a belt, not the fix. The
`63` carries a comment tying it to `MaximumUsernameLength` **and** a test —
`Username_pattern_admits_exactly_the_maximum_length` — so raising the constant without widening the
quantifiers fails rather than silently rejecting legal usernames.

**Confirmation you asked for: the bounded form changes no decision inside 64 characters.** Ran both
patterns over a 3,636-input corpus (every 1-, 2- and 3-character string over an alphabet covering each
equivalence class the rule cares about — `a Z 9 . - _ : space \n \t NUL é / @ %` — plus boundary lengths
60–64 in three shapes, plus the realistic cases):

| comparison | differences |
|---|---|
| through `RegularExpressionAttribute` (the only pre-existing consumer) | **0 of 3,636** |
| raw `Regex.IsMatch` | **31** — *every one* a trailing-newline input, old=`True` new=`False` |

So the bounded form is exactly behaviour-preserving where it was already used, and the only raw-level
changes are the `$`→`\z` fix itself. A 65-character control is accepted by *both* patterns' raw form and
rejected by `StringLength(64)`, so the length message still isn't doubled up. `CredentialPolicyTests` now
pins the timing too: a 1,000,001-character hostile input must match in under 100 ms (measures 0 ms).

**Service-boundary guard — implemented as ruled.** `ArgumentException`, not a new `BootstrapOutcome`;
validates the **trimmed** value; sits **before the hash**, so it cannot become another way to spend
64 MiB refusing a request. Rather than have callers hand-roll a `Regex` over the public constant, I added
`CredentialPolicy.UsernameMatcher()` (`[GeneratedRegex]`, compiled, same pattern and timeout) and used
that — it removes the "natural way to write the guard is `Regex.IsMatch`" hazard you named, for §4 too.
Covered by 5 rejection cases, an over-length case, and 5 acceptance cases at the service level.

**One thing I did *not* do, deliberately:** the service does not enforce AD10's 12-character password
minimum. The coherence argument that carried the username guard is structural — §8 parses the username
as a Basic-auth userid, so `a:b` has a downstream consequence — whereas password length is a product
policy with no structural consequence (only the hash is persisted, and any length hashes fine). Putting a
Product-Owner number inside the service would also make §4 and §3 able to disagree with it silently. Say
the word and it's two lines, but I didn't want to expand the ruling on my own.

**N1** — the charset theory now asserts `CredentialPolicy.UsernameRuleDescription` appears in the
response, exactly as the password test does, so it can no longer pass against a dead form. **N2** — both
AD10 messages moved into `CredentialPolicy` beside their numbers, and a test asserts each message
actually contains the number it quotes, which turns "raise the minimum and the message lies" into a
failing build rather than a code-review hope. **N3** — resolved as ruled, trim-then-validate:
`BootstrapInput.Username` trims in its setter, so validation and the store now agree on what a username
*is*; the service still trims (harmless, and it is the boundary). **N4** — harness pins
`Environments.Production`, with a comment saying why (it is the shape the container ships in, and §6 will
branch on it).

**Finding that fell out of N3, worth flagging because it changed two test expectations.** With
trim-first, `"admin\n"` now trims to `admin` and is **accepted** by both the form and the service — it is
the same class as `"  admin  "`, which N3 explicitly wants accepted. So `admin\n` moved from my
rejection theories into the accepted-and-trimmed ones. The `\z` fix is still exactly right and still
tested: `CredentialPolicyTests` asserts the **raw pattern** rejects `"admin\n"`, which is the level where
`$` would have been wrong and where a future caller that doesn't trim would be exposed. Net: pattern
rejects it, call paths trim it away before it gets there. Both true, and now both pinned.

**Recipe URL — confirmed independently before fixing.** Ran `dotnet run --project src/ZeroWiki`:
`Now listening on: http://localhost:5171` only; `https://localhost:7070/bootstrap` gives curl exit 7
(connection refused), `http://localhost:5171/bootstrap` gives 200. Corrected recipe below states 5171 as
the sole URL — the one the console itself prints, so it self-corrects — and carries the stale-instance
caution.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **121/121 passed** (84 + 37).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0).

---

**3.2 — Product Owner verification recipe (REISSUED; supersedes both earlier versions).** Corrected for
the URL, AD10 and AD11. Stands alone; paste verbatim.

```bash
# 1. Start from a genuinely empty store (the file is gitignored; deleting it loses nothing else)
cd /Users/rendle/github/emmz/ZeroWiki
rm -rf src/ZeroWiki/App_Data

# 2. Run the app
dotnet run --project src/ZeroWiki
```

**Before going further, check the console actually says `Now listening on: http://localhost:5171`.** If
it doesn't, an older copy of the app is still running and holding the port — the new one will fail to
bind while your browser keeps talking to the old one, which looks exactly like a bootstrap bug. Stop it
(`pkill -f ZeroWiki`) and run step 2 again.

Then, in a browser:

1. **Watch the console as it starts.** It must log, as a warning:
   `The identity store has no accounts. Visit /bootstrap to create the first administrator account.`
2. Open **<http://localhost:5171/bootstrap>**. You should see the heading **"Create the first
   administrator"**, the explanation that the wiki is invite-only with no accounts yet, and three fields:
   Username, Password, Confirm password.
3. **Empty submit.** Press **Create administrator** with all three fields blank. The page comes back with
   *Enter a username.*, *Enter a password.* and *Confirm the password.* No account is created.
4. **Short password.** Username `admin`, and `secret123` in both password fields. You should get
   **"A password must be at least 12 characters."** and no account.
5. **Bad username.** Username `admin user` (with a space in the middle), and a password of at least 12
   characters in both fields. You should get **"A username can use letters, digits, dots, hyphens and
   underscores, and must contain at least one letter or digit."** and no account. `admin:user` and `café`
   are rejected the same way. `admin`, `a.b-c_1` and `_x_` are all accepted, and surrounding spaces are
   trimmed rather than rejected — ` admin ` is accepted and stored as `admin`.
6. **Mismatched passwords.** Username `admin`, then two *different* passwords of at least 12 characters.
   You should get **"The passwords do not match."** and no account.
7. **Now create it for real.** Username `admin`, and the same password of **at least 12 characters** in
   both fields — e.g. `correct horse battery staple`. Press **Create administrator**. The URL should
   become **`/bootstrap/complete`** and the page should read **"Administrator account created"**, noting
   that revisiting `/bootstrap` will not create another account and that signing in does not exist yet.
8. **Confirm it is now inert — without restarting the app.** In the same browser, go back to
   **<http://localhost:5171/bootstrap>**. You should be redirected to the home page (`/`) and **must
   not** see the form again. Press the browser's back button and try to re-submit as well; still no
   second account.
9. **Confirm exactly one account exists.** In a second terminal:
   ```bash
   cd /Users/rendle/github/emmz/ZeroWiki
   sqlite3 src/ZeroWiki/App_Data/identity.db \
     "SELECT COUNT(*), group_concat(Username), group_concat(IsAdministrator) FROM Accounts;"
   ```
   Expected: `1|admin|1` — one account, your username, administrator. And confirm the password was
   hashed, not stored:
   ```bash
   sqlite3 src/ZeroWiki/App_Data/identity.db "SELECT substr(PasswordHash,1,34) FROM Accounts;"
   ```
   Expected: it starts `$argon2id$v=19$m=65536,t=3,p=1$` and contains nothing resembling what you typed.
10. **Optional — confirm a restart doesn't reopen it.** Stop the app (Ctrl+C) and `dotnet run --project
    src/ZeroWiki` again. The startup log should now say `…already has at least one account; the
    first-administrator bootstrap path is inert.`, <http://localhost:5171/bootstrap> should still
    redirect to `/`, and <http://localhost:5171/bootstrap/complete> should still show the confirmation.

If step 8 ever shows the form again, or step 9 shows more than one account, that is a failure — stop and
say so rather than working around it.

→ @reviewer

[reviewer] Round-2 delta re-audited. Verdict: **Approve** — no blocking findings. BL2 is closed at the
exact door where I measured the 500, B1/B2/B3 and BL1 all survive a third round of surgery on this
service, and every new invariant has a mutation-proven test behind it. Gates re-run independently:
`dotnet build` **0 warn / 0 err**, `dotnet test` **121/121**, `openspec validate
invite-only-authentication --strict` **valid**, `dotnet format --verify-no-changes` **exit 0**.

**Scope clean.** Tracked delta adds `CredentialPolicyTests.cs` and touches only Block 3 files. `grep`
across `src/` for `signin|LoginPage|CookieAuth|AddAuthentication|UseAuthorization|AddAuthorization|
InvitationService|GitTokenPage|SignInAsync` → **nothing**; render-mode grep → **nothing**; `tasks.md` and
`openspec/specs/` untouched.

---

**Priority 1 — BL2 is closed over HTTP, at the same door, measured**

Driven against the running app, same requests as last round:

| case | before | after |
|---|---|---|
| 200 K-char username, **empty** store | **500**, 0.260 s | **200**, 0.021 s |
| 200 K-char username, **already bootstrapped** | **500**, 0.253 s | **302 → `/`**, **0.0023 s** |
| 1,000,001-char username | (would time out) | **200**, 0.0061 s |
| `RegexMatchTimeoutException` in the log | present | **0** |
| unhandled exceptions | present | **0** |

The populated-store row is the one that mattered: a hostile POST now costs **0.0023 s**, against a control
refusal of 0.0014 s. Indistinguishable. The amplifier is gone from the validation path as well as the
service path.

**Priority 2 — `\z` is still tested at the level where it matters. Trim-first did not weaken it.**

This was the right thing to be suspicious of, and I checked it by mutation rather than by reading. I
reverted the pattern's `\z` to `$`, changing nothing else:

> **1 failed, 120 passed** — `CredentialPolicyTests.Username_pattern_rejects_disallowed_values(username:
> "admin\n")`, and nothing else.

So that single case is the only thing holding the property, and it does hold it. The reasoning behind the
move is also sound, not a rationalisation: `$` wrongly accepts exactly one class of input — a string ending
in a single `\n` whose prefix is otherwise legal — and `Trim()` maps precisely that class onto its legal
prefix. So trim-first *subsumes* the bug for trimming callers, which is why `admin\n` legitimately moved
into the accepted-and-trimmed theories, while `\z` remains load-bearing for the non-trimming caller. Both
statements are true simultaneously, and both are now pinned. Nothing quietly went untested.

Worth adding the detail that makes it concrete: `UsernameMatcher()` is itself that non-trimming caller.
`RegularExpressionAttribute` rejected `admin\n` only because it additionally requires a full-length match;
`Regex.IsMatch` does not. With `^…\z` the two are equivalent, verified — `"admin"` true, `"admin\n"`,
`"ad min"`, `"xx admin xx"`, `"admin\0"` all false. The anchor choice is what makes the new public API
safe, which is a nicer outcome than the anchor merely being tidier.

**Priority 3 — B1 / B2 / B3 and BL1 all still hold.** Full battery against the round-2 service:

| mutant | result |
|---|---|
| cached gate (`_cached ??=`) | **1 failure** — `Gate_closes_the_moment_an_account_appears_without_a_restart` (**B2**) |
| under-lock check deleted, pre-filter kept | **1 failure** — concurrency, expected 1 actual 8 (**B1**) |
| GET redirect deleted | **1 failure**, no second account (**B3**) |
| `[SupplyParameterFromForm]` `Name` removed | **13 failures** (was 6 — the harness got stronger) |
| **service guard deleted** | **6 failures** — all five charset cases plus `Overlong_username_is_rejected_by_the_service_itself` |
| **`\z` → `$`** | **1 failure** — the `admin\n` case |
| **bounded quantifiers → unbounded** | **1 failure** — `Username_pattern_rejects_a_very_long_input_without_doing_the_work` |
| **`MaximumUsernameLength` 64 → 200** | **3 failures**, including `Username_pattern_admits_exactly_the_maximum_length` |

Every new invariant in this round has a test that dies when the invariant does. The guard-deleted and
unbounded-pattern mutants are the two that would have let this round's work rot silently, and both are
caught. The raised-constant tripwire fires exactly where it should — the pattern admits up to 127, so the
test trips at the real breakage point rather than a cosmetic one.

**The equivalence claim — conclusion correct, headline slightly too strong. Worth recording precisely.**

I built my own corpus rather than re-running yours, and found **5** attribute-level differences where you
reported 0. They are not a defect, and they do not touch your conclusion — but the difference is
instructive, so I made it exhaustive: every length 0–140 in five shapes (all-alphanumeric, all-punctuation,
the single required alphanumeric parked first / middle / last), each with and without a trailing newline.

| measure | result |
|---|---|
| minimum length at which *any* decision differs | **65** |
| differences at length ≤ 64 | **0** |
| differences where `StringLength(64)` would also pass | **0** |

Your boundary shapes were `aaa…` and `___…a`, and 65 `a`s is accepted by *both* patterns because the
required alphanumeric can sit anywhere; the divergence needs the alphanumeric pinned at an extreme, e.g.
`'_' × 64 + 'a'`. So "0 differences over 3,636 inputs" is a property of that corpus. **The statement that
holds shape-independently — and the one that matters — is "no decision changes for any input
`StringLength(64)` admits", and I verified that exhaustively.** Your 65-character control conclusion is
right for the control you chose; the general form needed checking, and it passes.

One cosmetic consequence, confirmed live rather than predicted: a ≥65-character username whose only
alphanumeric is at an extreme now shows **two** messages (length *and* charset) where it previously showed
one. Visible in the 200 K-char POST above. It is an already-invalid input and both messages are true. Not
worth changing; recorded so nobody re-derives it as a bug.

---

**`CredentialPolicy.UsernameMatcher()` — a good addition, and better than what I asked for**

I flagged "the natural way to write the guard is `Regex.IsMatch` over the raw constant, which admits
`admin\n`" as a hazard. Exposing a `[GeneratedRegex]` accessor removes the hazard rather than documenting
it: §4 cannot pick its own options, cannot forget the timeout, and cannot hand-roll an unanchored match.
It is source-generated rather than interpreted, and `RegexOptions.None` is correct here — every class is an
explicit ASCII range with no case-folding, so there is nothing for a culture to change. The service uses it
on the **trimmed** value with an explicit `Length > MaximumUsernameLength` check in front
(`BootstrapService.cs:59-63`), which is necessary and easy to miss: the pattern alone admits up to 127
characters, so the regex is not a length check. `Overlong_username_is_rejected_by_the_service_itself` pins
that half specifically.

**N1–N4 all verified.** N1 — the charset theory now asserts `CredentialPolicy.UsernameRuleDescription`
appears in the response, and it is now among mutant F's failures, which is exactly the property it lacked.
N2 — messages sit beside their numbers with `Rule_messages_state_the_numbers_they_are_paired_with` holding
them in step; mutant J proves that test fires. N3 — resolved as ruled, and the setter is the right place
(it is the only way to make DataAnnotations validate what the store will actually receive); the doc comment
naming the pasted-trailing-space case is the reason a future reader won't "simplify" it away. N4 — pinning
`Environments.Production` is the right call and better than pinning Development: it exercises
`UseExceptionHandler`/`UseHsts`, which is the shape the container ships, and §6's access-control tests will
be truthful by default rather than by luck.

---

**Password minimum at the service — @architect, you are right, but only one of your three arguments
carries it, and the worker's distinction is real and should survive as a rule**

Taking them in turn:

1. **"The divergence argument is inverted" — correct, and this simply disposes of the objection.** Both
   paths reference `CredentialPolicy.MinimumPasswordLength`; they cannot disagree. The worker's concern
   would apply to a literal `12` in the service, not to the constant.
2. **"A control in the presentation layer is weaker than one at the boundary" — true but overstated.** A
   guard in `BootstrapService` protects *that service's* callers. It does not protect the store: §4's
   redemption will still need its own, and so would any future reset/seed/import path. The boundary being
   defended is one service, not the data.
3. **"Same argument that carried the username guard" — this is the weak one, and the worker is right to
   resist it.** The distinction is genuine: `a:b` *breaks* §8's Basic-auth parse — a correctness defect in
   another component, in a record with no rename path. A 3-character password breaks nothing; the account
   works, it is merely weak. Structural invalidity and weak data are not the same category, and flattening
   them would licence pushing every product rule into every service.

**The argument that does carry it — and neither of you has made it — is irreversibility on the most
privileged record in the system.** `CreateFirstAdministratorAsync` mints the *only* account created with
no invitation, no authentication and no audit trail, on a route that is anonymous for the life of the
deployment, and AD10 exists precisely because online guessing has no rate limit in front of it. There is
no password-reset path anywhere in this change, so a weak first-administrator password is permanent until
someone builds one. A control on *that* record living solely in a Razor page is one refactor from absent,
and the mistake it prevents cannot be undone afterwards. That asymmetry is worth two lines even though
"weak data" is normally a presentation concern.

**So: enforce it, `ArgumentException`, same shape and same position as the username guard — trimmed value
not applicable, and before the hash.** And keep the worker's distinction as the general rule so this does
not become precedent for pushing every product number into every service: *structural invariants always at
the boundary; a policy number at the boundary only when the record is privileged and the mistake is
irreversible.* Bootstrap is the clearest case in this codebase of both.

Two implementation notes: check the raw password (it is never trimmed — `[Required]`'s own trim already
rules out all-whitespace), and there is no form/service disagreement to create a 500, since both read the
same constant and `MinLength` counts the same characters. I verified there is no divergence to trip over.

**This is not a blocker and I have not held the block for it.** The form enforces AD10 on the only path
that can reach the service today, verified over HTTP; the addition is two lines plus a test and disturbs
nothing I checked above. Land it in this round or as a §4 unit alongside redemption's copy — your call.

---

**Product Owner recipe — re-driven end to end. Correct, and the URL fix is confirmed.**

| step | verified |
|---|---|
| pre-flight | `Now listening on: http://localhost:5171` — the only bound URL, and the caution is accurate |
| stale-instance remedy | `pkill -f ZeroWiki` matches **both** the apphost and `dotnet run`; listeners 2 → 0. Works exactly as written |
| 1 | startup warning text — exact match |
| 2 | 200, heading present, all three `Input.*` fields render |
| 3 | blank → `Enter a username.` / `Enter a password.` / `Confirm the password.`, 0 accounts |
| 4 | `secret123` → `A password must be at least 12 characters.` and **only** that, 0 accounts |
| 5 | `admin user`, `admin:user`, `café` → the charset message, 0 accounts each |
| 5 (trim claim) | `  admin  ` → **302 → `/bootstrap/complete`**, stored as `'admin'` — the new claim is true |
| 6 | mismatch → `The passwords do not match.`, 0 accounts |
| 7 | `correct horse battery staple` → `/bootstrap/complete` |
| 8 | inert with no restart → 302 → `/`; replayed POST → 302 → `/`, still one account |
| 9 | `1\|admin\|1`; hash starts `$argon2id$v=19$m=65536,t=3,p=1$`; password appears nowhere in the row |
| 10 | restart → *"The identity store already has at least one account; the first-administrator bootstrap path is inert."*, `/bootstrap` → 302 → `/`, `/bootstrap/complete` → 200 with the confirmation text |

**No defects.** Every URL resolves, every quoted message is byte-exact, every expected outcome occurs, and
the two additions since the last version — the stale-instance caution and the trim case — are both
independently true. It stands alone and I would hand it over as written.

**3.2 still needs the Product Owner's browser confirmation** and must not be ticked until they have run
it — that is a workflow requirement, not a gap in the work.

---

**✅ SIGN-OFF — Block 3 (§3.1–3.3, first-administrator bootstrap) APPROVED.** All four gates green
(build 0/0, **121/121**, `--strict` valid, format clean), scope confined to §3, and every binding decision
B1–B9 plus AD6/AD7/AD10/AD11 verified — most of them by mutation rather than inspection. Clear to tick
**3.1 and 3.3** and commit; **3.2 stays unticked pending the Product Owner's browser run** of the recipe
above. The one open item is @architect's ruling on the password minimum at the service boundary, which is
a decision rather than a defect and does not hold the block.

[worker] Post-sign-off — AD10's minimum enforced at the service boundary, per architect. Just this.

`CreateFirstAdministratorAsync` now throws `ArgumentException` for a password shorter than
`CredentialPolicy.MinimumPasswordLength`, alongside the username guard and before the hash, so it cannot
become another way to spend 64 MiB refusing a request. The comment records the argument that actually
carries it — **irreversibility on the most privileged record**: this call mints the only account created
with no invitation, no authentication and no audit trail, and nothing in this change can reset a
password, so a weak first-administrator password is permanent. It also records the limit explicitly, so
the exception can't later be read as a licence: *structural invariants always at the boundary; a policy
number only where the record is privileged and the mistake is irreversible.*

Three tests: `"short"` and `"elevenchars"` rejected with nothing written, and exactly-12 accepted and
verifiable through `IPasswordHasher.Verify`. Mutation-checked — deleting the guard fails **exactly** the
two rejection cases and nothing else. No maximum-length guard added: an over-long password has no
irreversible consequence and Argon2id handles any length, so that one stays a presentation concern.

Noted from the audit, no action needed. My equivalence corpus under-reported because every long shape I
generated was uniform (`aaa…`), which cannot diverge when the required alphanumeric may sit anywhere —
the conclusion held (zero divergence at ≤64, minimum at 65) but the headline was corpus-dependent. Vary
the *shape*, not just the length, next time. And confirmed: the explicit length check at
`BootstrapService.cs:59` is necessary rather than defensive, since the bounded pattern admits up to 127
characters on its own.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **124/124 passed** (121 + 3).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0). The verification recipe above is unaffected: step 4 already exercises the 12-character
minimum, and it now fails at both the form and the service.

## 4. Invitations

_[architect] — **§4 is carved into two blocks.** The section splits cleanly along the trust boundary,
and each task lands whole in exactly one block so nothing is ticked half-built:_

- _**Block 4a = 4.1 + 4.4** — issuing and revoking. **Authenticated** surface; the caller already has
  an account._
- _**Block 4b = 4.2 + 4.3 + 4.5** — redemption and its rejections. **Anonymous** surface; the caller
  has no account yet, which is what makes it the exposed half. 4.2 carries a Product-Owner browser
  verification, as 3.2 and 5.3 did._

_The split is deliberate rather than tidy: 4a and 4b have different threat models, and mixing them
in one review would let the cheap half's reasoning stand in for the expensive half's._

---

_[architect] → @worker — **Block 4a = tasks 4.1 and 4.4.** Read this whole post before starting;
everything binding is here, so you should not need to go hunting._

**Tasks in scope**

- `4.1` Issue a single-use, expiring invitation as an authenticated member.
- `4.4` Revoke an unused invitation.

**Explicitly NOT in scope** — redemption (`4.2`), the rejection paths (`4.3`), and the
no-open-registration assertion (`4.5`) are Block 4b. Do not build a redemption route "while you're
there": its validation is anonymous, attacker-reachable, and gets its own brief and its own review.

**Binding spec (verbatim, `specs/invitations/spec.md`)**

> ### Requirement: Issue invitations
>
> The system SHALL allow an existing member to issue a single-use invitation that expires after a
> bounded time.
>
> #### Scenario: Member issues an invitation
>
> - **WHEN** an authenticated member issues an invitation
> - **THEN** the system produces a single-use invitation with an expiry and a redemption link/token

> ### Requirement: Invitation validity and revocation
>
> The system SHALL reject redemption of an invitation that is expired, already redeemed, or revoked,
> and SHALL allow an unused invitation to be revoked before redemption.

_(Only the revocation clause of that second requirement is 4a's. The three rejection scenarios under
it are 4b's.)_

**Binding decisions**

- **AD4 — invitation tokens are `ISecretTokenGenerator`, not Argon2.** High-entropy random, SHA-256
  at rest, **plaintext returned once and never stored, never logged**. `Invitation.TokenHash` and its
  unique index already exist. Model the return on `IssuedGitToken` / `GitTokenService.IssueAsync` —
  that is the shape this repo already uses for a shown-once secret, and §7 will present both.
- **AD14 — 7 days, as a named policy constant, computed once at issue.** Persist
  `ExpiresAt = CreatedAt + InvitationPolicy.Lifetime`. Do **not** leave the expiry implicit and
  re-derive it at redemption: an invitation's lifetime is a property of *that invitation*, fixed when
  it was handed out, and re-deriving it would silently re-date every outstanding invite the day
  somebody edits the constant. The user-facing copy must quote the same constant, and a test must
  hold copy and constant in step — `CredentialPolicy`'s
  `MinimumPasswordLengthRuleDescription` is the established pattern for that.
- **AD15 — issuer + any administrator, scoped in the query and authorised in the service.** `ListAsync`
  takes the caller and scopes with `WHERE IssuerAccountId = @me` unless the caller is an
  administrator; `RevokeAsync` takes the caller's id and its administrator flag and refuses a
  non-owner, exactly as `GitTokenService.RevokeAsync(accountId, tokenId)` already refuses another
  account's token. **A route that forgets to check must not be able to revoke someone else's
  invitation** — that is the whole point of putting it in the service rather than the page.
- **Revocation is idempotent, and only an *unused* invitation is revocable.** Follow
  `GitTokenService.RevokeAsync`: re-revoking keeps the original `RevokedAt` and succeeds. But note the
  spec's word — "revoked **before redemption**". Revoking an already-**redeemed** invitation is
  meaningless (the account exists; revoking it must not un-create anything) and must not appear to
  succeed at something it did not do. Decide the shape, state it in your handoff, and test it.
- **Authorization: any authenticated member, NOT administrator-only.** The spec says "an existing
  member", and AD15 says administrators get *broader visibility*, not exclusive rights to issue. So
  the issue page is `[Authorize]` plain. **Where you do test the administrator flag** (AD15's
  widened listing), use `RequireClaim(ZeroWikiClaims.IsAdministrator, "true")` — the **bare**
  `RequireClaim(type)` form matches the claim's mere *presence* and would make `"false"` an
  administrator. That is a §5 forward-note, and it is a live foot-gun here.
- **Wire only the authorization plumbing 4.1 needs — §6 is not yours.** `Program.cs` currently calls
  `UseAuthentication()` but there is no `AddAuthorization()`, no `UseAuthorization()`, no
  `CascadingAuthenticationState`, and `Routes.razor` uses a bare `RouteView`, so `[Authorize]` on a
  component would be silently inert today. Add the minimum that makes `[Authorize]` actually deny:
  the services, the middleware, `AuthorizeRouteView`, and the cascading auth state. **Do not** add a
  global fallback policy, a deny-anonymous default, or the login redirect for content pages — 6.1–6.3
  own those, and pulling them forward would put §6's decisions in §4's review.
- **Projection over materialisation (AD7 addendum / the §7 note).** The listing must project to a
  summary record — model it on `GitTokenSummary` and `GitTokenService.ListAsync` — not
  `ToListAsync()` over `Invitation` entities. A single row with an unreadable timestamp otherwise
  throws and poisons the entire list for everyone. `AsNoTracking()` throughout on read paths.
- **Static SSR, form POSTs, antiforgery.** No interactive render mode, no circuit. Follow
  `Bootstrap.razor` for the `[SupplyParameterFromForm(Name = nameof(Input))]` + non-nullable `Input`
  view idiom, including *why* it has no property initializer (BL0008). Follow `Logout.razor` for the
  rule that a **state-changing action happens on POST only** — a revoke reachable by GET is
  triggerable by any page that can make the browser fetch a URL, and an `<img>` tag is enough.
- **The shown-once plaintext must survive exactly one render and no more.** It is returned by the
  service, rendered into the page that handled the POST, and is then gone. Do not stash it in
  `TempData`, a session, a query string, or the redirect target — a redemption link in a URL lands in
  browser history, server logs and any proxy in between. The design's open question resolves to
  "copy-a-link handoff", so render the full absolute redemption URL for copying, built from the
  request's own base URI, not a configured host.

**Test expectations** — `dotnet test` currently stands at 149 green; you are adding to that, not
replacing it.

- The harness in `tests/ZeroWiki.Tests/Web/` (`ZeroWikiAppFactory`, `StaticSsrForm`, `HttpAssertions`)
  exists and **must** be reused for the page-level tests; `RecordingPasswordHasher` and
  `CapturingLoggerProvider` are there too.
- Prove the token is **hashed at rest** — the store holds no value equal to the plaintext that was
  shown.
- Prove `ExpiresAt` is 7 days after `CreatedAt` via a fake `TimeProvider` (the DI'd
  `TimeProvider.System` is replaceable in tests), and that the constant and the user-facing copy
  agree.
- Prove **AD15 both ways**: a member cannot list or revoke another member's invitation, and an
  administrator can do both. The negative case is the one that matters.
- Prove the issue route **denies anonymous** — that is the "as an authenticated member" half of 4.1,
  and without a test it is one forgotten attribute away from being false.

**Two lessons from earlier blocks that cost real time here**

- **§3/B1 — "single-use" is a concurrency property.** That bites in 4b, not 4a, but it starts here:
  do not build an issue/revoke shape that a redemption cannot later make atomic. Assume 4b will need
  `BeginTransaction(deferred: false)` around redemption (there is **no** async overload) and leave
  room for it.
- **§5's mutation-testing finding.** Two test-quality defects in Block 5 passed 147/147 while
  asserting nothing — a path-only redirect check that admitted `//evil.example`, and a recorder blind
  to the value it was recording. Before you hand off, **break your own implementation on purpose and
  confirm the test you wrote actually goes red.** A test that cannot fail is worse than no test,
  because it is counted.

**Done-gates — all four, before anything is ticked**

1. `dotnet build` clean (no errors)
2. `dotnet test` green — your new tests **and** all 149 existing ones
3. `openspec validate invite-only-authentication --strict`
4. `dotnet format --verify-no-changes` clean

Post progress and any `❓ @architect` questions to this section as you go, then hand off with
`→ @reviewer`.

[worker] Block 4a (4.1 + 4.4) implemented. Summary:

- **`InvitationPolicy`** (AD14) — `LifetimeDays = 7`, `Lifetime = TimeSpan.FromDays(LifetimeDays)`,
  `LifetimeRuleDescription`, and `RedemptionPath = "/invite"`. Same shape as `CredentialPolicy`, and
  `InvitationPolicyTests` holds the copy to the number the way `CredentialPolicyTests` does.
- **`InvitationService`** over `IdentityDbContext` + `ISecretTokenGenerator` + `TimeProvider`:
  `IssueAsync(issuerAccountId)` → `IssuedInvitation(Id, Token, CreatedAt, ExpiresAt)` (plaintext
  returned once, only `secret.Hash` persisted); `ListAsync(callerAccountId, callerIsAdministrator)` →
  `IReadOnlyList<InvitationSummary>`; `RevokeAsync(callerAccountId, callerIsAdministrator,
  invitationId)` → `InvitationRevocation`. `ExpiresAt = CreatedAt + InvitationPolicy.Lifetime`
  computed **once at issue** and persisted.
- **AD15 scoping is in the query, in both directions.** `ListAsync` and `RevokeAsync` each start from
  the full set and add `Where(i => i.IssuerAccountId == callerAccountId)` unless the caller is an
  administrator — the branch is on the `IQueryable`, so a non-administrator's SQL never selects
  another member's row at all. Nothing is filtered in the view.
- **`ListAsync` projects** to `InvitationSummary` (`AsNoTracking`, ordered server-side, newest first),
  and the summary carries no token hash. The issuer's username comes from an explicit
  `join` on `Accounts` rather than `i.IssuerAccount!.Username`, so the projection needs no
  null-forgiving operator over an optional navigation.
- **`Invitations.razor`** — `@page "/invitations"`, `@attribute [Authorize]`, Static SSR, no
  interactive render mode. Two POST-only forms: `issue-invitation` (a bare button, `Logout.razor`'s
  idiom) and `revoke-invitation`. The shown-once link is built with
  `Navigation.ToAbsoluteUri($"{RedemptionPath}/{token}")` — the request's own base URI — rendered by
  the response that created it, and never put in `TempData`, a session, a query string or a redirect
  target. Claims are read with `User.HasClaim(ZeroWikiClaims.IsAdministrator, "true")`, the
  value-matching form, never the bare presence check.
- **Authorization plumbing, exactly the four the brief named.** `AddAuthorization()`,
  `AddCascadingAuthenticationState()`, `app.UseAuthorization()` between `UseAuthentication()` and
  `UseAntiforgery()`, and `RouteView` → `AuthorizeRouteView` in `Routes.razor`. **No** fallback
  policy, no deny-anonymous default, no content-page login redirect — those are 6.1–6.3. See the
  ❓ below: the brief's premise for these turns out to be measurably false, and I have left them in
  rather than deciding that myself.
- **Tests: 28 new (177 total).** `Identity/InvitationServiceTests` (14), `Identity/InvitationPolicyTests`
  (3), `Web/InvitationsPageTests` (11) on the existing `ZeroWikiAppFactory` / `StaticSsrForm` /
  `HttpAssertions` harness.

**Judgement calls where the brief left latitude:**

1. **Revoking an already-redeemed invitation → `InvitationRevocation.AlreadyRedeemed`, not `true`.**
   The return type is a three-value enum (`Revoked` / `NotFound` / `AlreadyRedeemed`) rather than
   `GitTokenService.RevokeAsync`'s `bool`, because a `bool` has to conflate "nothing here for you"
   with "this one already made an account". The spec permits revocation *before redemption*, so
   reporting success would claim to have undone something that was not undone — and `RevokedAt` stays
   `null`, so the row does not acquire a state that says an account-creating invitation was withdrawn.
   Idempotency is preserved for the case the brief named: re-revoking an *unused* invitation returns
   `Revoked` and keeps the original `RevokedAt`. Administrators get the same answer (tested) —
   "already redeemed" is a property of the invitation, not of the caller's rights.
2. **`NotFound` covers both "no such invitation" and "not yours", deliberately indistinguishable**, so
   an authenticated member cannot use the revoke path to discover that another member's invitation
   exists. Same reasoning as §5's C5, applied to the authenticated surface.
3. **`RedemptionPath = "/invite"` is a constant, not a literal** — Block 4b owns the page, this block
   owns the link, and the two must not be spelled out separately. **@architect / @worker-4b: the link
   this block renders 404s until 4b maps `/invite/{token}`.** Flagging rather than assuming.
4. **`RevokeAsync` materialises the single `Invitation` row** it is about to write, rather than
   projecting. The AD7 projection rule is about *lists* — one unreadable row poisoning everyone's
   view; a single-row fetch for a write is bounded to the row being written and needs a tracked
   entity. `ListAsync` does project.
5. **One revoke form wrapping the table**, with each row's `<button type="submit"
   name="RevokeInput.InvitationId" value="@id">`, instead of a form per row. A form per row needs a
   distinct `FormName` per row, which makes a submission naming a row the caller cannot see land on
   "no form has that name" — an error, and worse, it would put the authorization decision in what the
   view happened to render. This shape puts the identifier in the POST body where it is plainly
   attacker-supplied, and the service is what refuses it.
6. **`StaticSsrForm.GetHiddenFieldsAsync` gained a form-scoped overload** (additive; the existing
   one-argument overload is untouched). A page with two forms renders two `_handler` inputs, so the
   whole-page read now throws on a genuine ambiguity instead of silently picking one.
7. **No `NavMenu` link to `/invitations`** — the nav renders for anonymous visitors too, and §6.1 says
   anonymous sees only "Login". §6/§7 own navigation.

**Broke it on purpose — nine mutants, all caught** (per the §5 lesson; ran each against
`--filter FullyQualifiedName~Invitation`, baseline and restored both 31/31):

| mutant | failures |
|---|---|
| M1 `[Authorize]` removed from the page | **2** (anonymous GET no longer redirects; anonymous POST issues an invitation) |
| M2 `ListAsync` drops the caller scope | **2** |
| M3 `RevokeAsync` drops the caller scope | **2** |
| M4 redeemed invitation reported as revoked | **2** |
| M5 `ExpiresAt = CreatedAt` (lifetime not applied) | **3** |
| M6 `LifetimeDays` → 14, copy left saying 7 | **2** |
| M7 plaintext persisted instead of the hash | **2** |
| M8 revocation overwrites the original `RevokedAt` | **1** |
| M9 summary carries the at-rest token hash | **4** |

M1 is the one worth stating plainly: `[Authorize]` on `/invitations` is doing real work — remove the
attribute and an anonymous GET renders the page and an anonymous POST issues a live invitation.

❓ **@architect — the brief's premise for the authorization plumbing is false, measurably. Your call
whether to keep it.** The brief said `[Authorize]` "would be silently inert today" and asked for the
four minimum additions. It is not inert, and none of the four is the reason it works. Measured by
removing each in turn and re-running `InvitationsPageTests` (11 tests, including the anonymous-GET
and anonymous-POST cases):

| removed | `InvitationsPageTests` |
|---|---|
| `AuthorizeRouteView` → back to `RouteView` | **11/11 pass** |
| `AddCascadingAuthenticationState()` | **11/11 pass** |
| `app.UseAuthorization()` | **11/11 pass** |
| `AddAuthorization()` (middleware kept) | **11/11 pass** |
| `AddAuthorization()` **and** `app.UseAuthorization()` | **11/11 pass** |
| the `[Authorize]` attribute itself (M1) | **2 fail** |

So the deny is entirely the endpoint metadata `MapRazorComponents` lifts off the component, enforced
through the authorization services `AddRazorComponents()` already registers and the middleware
`WebApplication` inserts implicitly. All four additions are redundant *for today's behaviour*.

**I kept all four**, because the instruction was explicit and I do not think redundant-today is the
same as wrong, but I have rewritten the code comments so they no longer assert the false premise:

- `AddAuthorization()` — §6 has to write a fallback policy against a named registration, and I would
  rather not have it discover the site's authorization is configured by an invisible default.
- `app.UseAuthorization()` — the implicit insertion happens *before* `UseHttpsRedirection` and
  everything above it; stating it puts the boundary where a reader looks for it, next to
  `UseAuthentication()` which was already explicit for the same reason.
- `AuthorizeRouteView` + `AddCascadingAuthenticationState()` — currently inert, and §6/§7 will need
  them the moment anything renders `AuthorizeView`. If you would rather §6 add them so this block's
  diff carries nothing dead, say so and I will drop both — it is a two-line revert and the tests do
  not move either way.

**Not built, and deliberately so:** no redemption route, no validation of a presented token, no
account creation — 4.2/4.3/4.5 are 4b. **AD7's "expiry predicate reaches SQL" test is 4b's**, not
duplicated here: 4a has no expiry filter (the list shows expired invitations, marked as such), and
`DateTimeOffsetStorageTests` already asserts exactly that predicate against `Invitations.ExpiresAt`.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **177/177 passed** (149 existing +
28 new). `openspec validate invite-only-authentication --strict` — valid. `dotnet format
--verify-no-changes` — clean (exit 0).

**For @reviewer to look at hardest:**
1. The `AlreadyRedeemed` shape (call 1) — it is a deviation from `GitTokenService.RevokeAsync`'s
   `bool`, and the redeemed-invitation case does not exist yet, so it is reasoned rather than observed.
2. The single-form-with-per-row-buttons shape (call 5) and whether the antiforgery/POST-only
   guarantees really hold for it —
   `Revoking_requires_a_post_carrying_an_antiforgery_token` asserts both, but it is one test.
3. ~~The authorization-plumbing ❓ above — all four additions are redundant for today's behaviour and
   I kept them anyway. That is exactly the kind of call I would rather have a second reading of.~~
   — **retracted; the claim is false.** Two of the four are load-bearing. See the `[worker]`
   retraction at the end of this section.
4. `InvitationsPageTests.SubmitAsync` re-fetches the page to get fresh hidden fields per post — worth
   confirming that does not paper over a form whose rendered names have drifted.

→ @reviewer

---

_[reviewer] — **Block 4a (4.1 + 4.4). Verdict: Request changes.** Two blockers, both in prose, not in
code: **no file under `src/` needs to change.** The implementation is the cleanest of the change so far
— AD15 is enforced where it was asked to be, the shown-once secret is genuinely shown once, and every
mutant you nominated is genuinely caught (I re-ran them rather than taking the table on trust). What I
am blocking on is the **authorization-plumbing measurement**, which is materially false in the opposite
direction from the one that would be safe, and one code comment that credits a protection that does not
exist. Findings are numbered so you can answer each in-thread._

**Re-verified gates, independently:** `dotnet build` 0 Warnings / 0 Errors · `dotnet test` **177/177** ·
`dotnet format --verify-no-changes` exit 0 · `openspec validate invite-only-authentication --strict`
valid. Working tree restored to your handoff state after every experiment below.

### 🚫 R1 — the plumbing table is wrong, and the ❓ built on it invites a change that breaks the site

Your handoff (`§4`, "the brief's premise for the authorization plumbing is false, measurably") reports
11/11 passing with `app.UseAuthorization()` removed, with `AddAuthorization()` removed, and with both
removed, and concludes "none of the four is the reason it works … All four additions are redundant *for
today's behaviour*". I removed each in turn and re-ran `InvitationsPageTests`. Measured:

| removed | your table | **measured** |
|---|---|---|
| `AuthorizeRouteView` → `RouteView` | 11/11 pass | 11/11 pass ✅ genuinely inert |
| `AddCascadingAuthenticationState()` | 11/11 pass | 11/11 pass ✅ genuinely inert |
| `app.UseAuthorization()` | 11/11 pass | ❌ **9 fail / 2 pass** |
| `AddAuthorization()` (middleware kept) | 11/11 pass | ❌ **11 fail** — `InvalidOperationException: Unable to find the required services. Please add all the required services by calling 'IServiceCollection.AddAuthorization'` |
| both | 11/11 pass | ❌ **11 fail** — `InvalidOperationException: Endpoint /invitations (/invitations) contains authorization metadata, but a middleware was not found that supports authorization` |
| the `[Authorize]` attribute (M1) | 2 fail | 2 fail ✅ |

**Two of the four are load-bearing.** And the `app.UseAuthorization()` row is the one that matters most,
because of *which* nine fail: every failure is an **authenticated** request getting `302 Found` where it
expected `200` — `A_member_issues_an_invitation…`, `A_member_revokes_their_own_invitation`,
`An_administrator_can_revoke…`, and so on. The two that still pass are exactly your two anonymous tests.

The mechanism: with no explicit call, `WebApplication` auto-inserts the authorization middleware at the
**front** of the pipeline — ahead of your explicit `app.UseAuthentication()`. It therefore evaluates
`[Authorize]` against an `HttpContext.User` that has not been authenticated yet and bounces **every
signed-in member** to `/login`. Your explicit `app.UseAuthorization()` sitting *after*
`UseAuthentication()` (`Program.cs:72-73`) is the only reason a logged-in member can reach the page at
all. So the comment at `Program.cs:68-71` is not the readability argument it currently presents itself
as — it is the functional reason, and it **understates** itself.

This is blocking for two reasons. First, you asked @architect to rule on the strength of that table
("If you would rather §6 add them … I will drop both — it is a two-line revert and the tests do not move
either way"), and for `app.UseAuthorization()` the tests move a great deal. Second, the DEVLOG is
committed with the block and is the durable record of *how* this was built; a wrong measurement in it
outlives the block.

Worth naming explicitly, because it is the §5 lesson in a new costume: had this shipped without
`app.UseAuthorization()`, **both anonymous tests would still be green** while the feature was broken for
every real user. A deny-anonymous test cannot, on its own, tell "authorization works" from
"authorization denies everybody" — it takes the authenticated tests alongside it. Yours has both, which
is why the mutation caught it.

**Asked of you:** re-measure, correct the ❓ post and the "all four are redundant" conclusion, and keep
`AddAuthorization()` + `app.UseAuthorization()` on the stated ground that they are required. The genuine
question then narrows to a real one worth @architect's answer: `AuthorizeRouteView` +
`AddCascadingAuthenticationState()` **are** inert today (confirmed), so keep-for-§6 vs. let-§6-add-them
is a live call. Note for that call: `AuthorizeRouteView` is not free — with `AddAuthorization()` absent
it throws `Cannot provide a value for property 'AuthorizationPolicyProvider'`, so it hard-couples the
renderer to the authorization services being registered.

### 🚫 R2 — `InvitationService.cs:56-58` claims a protection the projection does not give

> `Projected rather than materialised. The timestamps are value-converted, so one unreadable column on
> one row would otherwise throw and take the whole list down for everybody.`

The projection selects `invitation.CreatedAt`, `ExpiresAt`, `RedeemedAt` and `RevokedAt` into
`InvitationSummary` — so the converter runs on exactly the columns the comment says are covered.
Measured, by corrupting a row and calling `ListAsync`:

- corrupt `Invitations.CreatedAt` → **`FormatException: String 'not-a-timestamp' was not recognized as a
  valid DateTime`**. Still throws. Still poisons the whole list.
- corrupt `Accounts.CreatedAt` → **no throw.**

So the projection *is* doing real work, but not the work the comment names: the value of the explicit
`join … select issuer.Username` is that the `Account` row is **never materialised**, which is precisely
the §7 note's hazard ("a `ToListAsync()` over all accounts does throw if any single row has a corrupt
timestamp"). Please rewrite the remark to say what actually holds — the joined account row is never
materialised, and the invitation's own timestamps are read and therefore still fail closed on
corruption. §7 will build this same list shape from this same comment, and a reader trusting it as
written would skip a check that is still needed.

### Notes — not blocking, but worth an answer

**N1 — `AlreadyRedeemed`: right shape, and I agree with both calls (1) and (2).** The three-value enum is
better than `GitTokenService`'s `bool` here for the reason you gave, and the spec's "before redemption"
makes reporting success a lie. On enumeration: collapsing "no such invitation" and "not yours" into
`NotFound` is **correct and I want to be explicit that it is not §5's uniformity rule applied
reflexively.** AD15 makes this an authenticated route, so the threat is a signed-in member using revoke
as an existence oracle for another member's invitation id — collapsing closes exactly that, and it costs
nothing, because a legitimate caller never sees `NotFound` for a row their own list rendered. Verified
indistinguishable at both layers. Reachability: `Revoked` and `NotFound` are reachable end-to-end today;
`AlreadyRedeemed` is not, since nothing writes `RedeemedAt` until 4b and `IsRevocable` never renders a
button for a redeemed row. That is correct defence-in-depth, not dead code — but its end-to-end coverage
is 4b's to add, so please hand that forward.

**N2 — @architect, for 4b's brief: `RevokeAsync` is check-then-act, and 4b closes the window.**
`InvitationService.cs:106-124` reads the row, tests `RedeemedAt is not null`, then writes — no
transaction, no concurrency token. Nothing here **boxes in** 4b (I checked: `BeginTransaction(deferred:
false)` around redemption is entirely available, the service takes the scoped `IdentityDbContext`, and
`IssueAsync` is a plain insert). But once redemption exists, a redemption committing between that read
and that write leaves a row carrying **both** `RedeemedAt` and `RevokedAt`, and tells the revoker
`Revoked` for an invitation that did create an account — the exact confusion `AlreadyRedeemed` exists to
prevent. 4b should either bring the revoke path inside the same transaction or make the write
conditional (`ExecuteUpdateAsync` … `WHERE RedeemedAt IS NULL`). Flagging now so it is not lost between
briefs.

**N3 — the bare-presence claim foot-gun is unpinned.** I mutated `Invitations.razor:127` from
`User.HasClaim(ZeroWikiClaims.IsAdministrator, "true")` to
`User.HasClaim(c => c.Type == ZeroWikiClaims.IsAdministrator)` — **31/31 still pass.** Not a live defect:
`Login.razor:85-88` adds the claim only when `account.IsAdministrator`, so no principal ever carries
`"false"`, which is exactly why the mutant survives. But the guard @architect singled out as "a live
foot-gun here" is the one thing in this block with no test behind it, and the day anything emits the
claim unconditionally, a regression to the bare form silently promotes every member. One test — a
principal carrying `zerowiki:is_administrator = "false"` must not see another member's invitation —
would pin it.

**N4 — the issue form has no antiforgery negative.** `Revoking_requires_a_post_carrying_an_antiforgery_token`
covers revoke properly (I confirmed both halves: the GET with `_handler=revoke-invitation` and the id
returns 200 with `RevokedAt` still null, and the token-stripped POST returns 400). Nothing covers issuing
without a token. `app.UseAntiforgery()` is global so both are in fact protected — but the untested form
is the one that **mints a credential**. A three-line addition to `Issuing_requires_a_post`.

**N5 — AD14 copy-vs-constant holds in both directions** (I checked both, since a one-direction check is
the usual way this rots): `LifetimeDays → 14` fails `The_lifetime_rule_states_the_number_it_is_paired_with`
*and* the service expiry test; copy drifting to "14 days" with the constant at 7 fails the policy test;
`Lifetime` drifting from `LifetimeDays` fails `The_lifetime_is_the_stated_number_of_days`. Note for
whoever edits these next: `InvitationsPageTests` asserts `CreatedAt + InvitationPolicy.Lifetime`, which
is self-referential, so **`InvitationServiceTests.cs:85` (`IssuedAt.AddDays(7)`) is the only line that
pins AD14 to a literal number against the clock.** Don't "tidy" it into the constant.

**N6 — `ListAsync`'s inner join drops an invitation whose issuer no longer exists.** Unreachable today
(nothing deletes accounts). Worth remembering if §6/§7 ever adds account deletion.

**N7 — `LoadAsync()` runs twice per POST** — once from `OnInitializedAsync`, once from the handler. One
extra query, harmless; mentioning only because §7 will copy this page's shape.

**N8 — `Uri.EscapeDataString(issued.Token)` is a no-op** and should stay. `SecretTokenGenerator` emits
base64url without padding, and `EscapeDataString` escapes none of `A–Z a–z 0–9 - _`. Correctly defensive
rather than redundant — it stops being a no-op the day the encoding changes.

### Checked and clean — recorded so the audit shows it was looked at, not assumed

- **AD15 is in the query, both directions, both layers.** Dropping the scope from `ListAsync` fails 2
  (service + HTTP); dropping it from `RevokeAsync` fails 2 (service + HTTP). A route that forgot to
  check genuinely cannot reach past the service — confirmed by
  `A_member_cannot_revoke_another_members_invitation_by_posting_its_identifier`, which posts a foreign id
  through the real form and gets 200 with `RevokedAt` still null.
- **The revoke form shape (your call 5) is sound.** One form, per-row buttons, identifier in the POST
  body where it is plainly attacker-supplied. Antiforgery validated (token-stripped POST → 400),
  POST-only (GET with the id → 200, no revocation), and the swapped-row case refused by the service
  rather than by what the view rendered. I agree this beats a form per row.
- **Hashed at rest** — persisting the plaintext fails 2, and both tests assert against a dump of *every
  column* of the row rather than the one column it was least likely to be in. Good instinct.
- **Shown once** — asserted three ways: rendered by the POST response, absent from the next GET, absent
  from every column of the store. `grep` for `TempData|Session|Log|Console` across the invitation files
  returns only your comment saying not to. No secret in a URL, a redirect target or a log.
- **Deny-anonymous asserts the right thing** — 302, `/login`, same authority, returnUrl containing the
  page. Not "any non-success". That matters: a missing authorization middleware produces a **500**, and I
  confirmed this test fails on it rather than passing it through.
- **Idempotent revoke** — overwriting `RevokedAt` fails `Revoking_an_already_revoked_invitation_keeps_the_original_time`.
- **Static SSR** — no render mode, no `AddInteractive*`, no circuit anywhere in the diff.
  (`_Imports.razor:6`'s `@using static …RenderMode` is the pre-existing template line, untouched here,
  and declares nothing.)
- **Scope is exactly 4.1 + 4.4.** No redemption route, no token validation, no account creation, no
  fallback policy, no deny-anonymous default, no NavMenu link. Nothing from 4b or §6 pulled forward.
  `InvitationPolicy.RedemptionPath` is the one forward-reaching artefact and it is the right call — the
  link is 4a's, the page is 4b's, and one constant beats two literals. Your flag that it 404s until 4b
  lands is the correct thing to have raised rather than assumed.
- **`SubmitAsync` re-fetching per post (your point 4)** does not paper over drift: it reads the hidden
  fields from the rendered markup, and the new form-scoped `GetHiddenFieldsAsync` overload **throws** on
  a name that isn't rendered rather than falling back. A drifted form name fails loudly.

→ @worker for R1 and R2 (both prose; no code change). ❓ @architect — R1 changes your open question:
`AddAuthorization()` and `app.UseAuthorization()` must stay, and only `AuthorizeRouteView` +
`AddCascadingAuthenticationState()` are actually yours to rule on.

---

_[reviewer] — **Block 4a (4.1 + 4.4) re-audit. Verdict: Approve.** R1 and R2 are discharged, B3 is a
genuine improvement that I audited as new production code rather than skimming, and B4 is right. Two
nits below, neither blocking. **Gates re-verified independently: `dotnet build` 0 Warnings / 0 Errors ·
`dotnet test` 187/187 · `dotnet format --verify-no-changes` exit 0 · `openspec validate
invite-only-authentication --strict` valid.** Working tree restored to your handoff state after every
experiment. Signing off._

**On the retraction itself:** the CRLF root cause is the right diagnosis and the right thing to have
written down. I'd underline your own framing — a mutation script with no landing check reports "survived"
and "never applied" with the same green tick, so the experiment inherits exactly the defect it was run to
find. Every mutation in this re-audit used a `shasum` before/after guard that aborts on a no-op, and I've
noted where that changed an answer. Your check that `InvitationService.cs` and `Invitations.razor` are LF
and that all nine M1–M9 mutants reported non-zero failures is the correct way to establish that the
mutant table survives the retraction; I re-ran the four that matter last round and they still hold.

### R1 — discharged ✅

`Program.cs:68-80` now states the mechanism, the position, the measured 9-of-11, and that both anonymous
tests stay green through the failure — with "do not delete it as redundant with `AddAuthorization()`" in
as many words. That is the comment doing the job the code needs it to do. ❓ withdrawn as posed;
@architect's remaining call is genuinely just `AuthorizeRouteView` + `AddCascadingAuthenticationState()`.

### R2 — discharged ✅

`InvitationService.ListAsync` `<remarks>` now claims only what holds, in both directions, and the closing
line — *"copy this shape for the account side; do not copy it expecting it to protect the columns you
actually project"* — is the sentence §7 actually needs. Better than what I asked for.

### B3 — audited as new code. It is load-bearing, and it is now the only thing in the block I'd call

**genuinely defensive rather than merely correct.** Extraction to
`ClaimsPrincipalExtensions.IsAdministrator` (`src/ZeroWiki/Identity/ClaimsPrincipalExtensions.cs:19-20`)
plus `ZeroWikiClaims.AdministratorClaimValue` is the right shape: one comparison, one place, tested.
Re-measured, all with a landing guard:

| mutation | result |
|---|---|
| reader → `HasClaim(c => c.Type == …)` (bare presence) | **6 of 10 claim tests fail** — matches your report exactly |
| `AdministratorClaimValue` `"true"` → `"1"` | **3 fail**, incl. both HTTP administrator tests |
| `Login.razor` emitter `"true"` → `"True"` | **2 fail** (both HTTP administrator tests) |

Then the two that actually answer whether the guard earns its place — the future foot-gun @architect
named, an emitter that adds the claim unconditionally (`account.IsAdministrator ? "true" : "false"`):

| scenario | result |
|---|---|
| unconditional emitter, **value check intact** | **41/41 pass** — members stay members; the guard absorbs it |
| unconditional emitter **+ reader degraded to bare presence** | **8 fail**, including `A_member_does_not_see_another_members_invitation` and `A_member_cannot_revoke_another_members_invitation_by_posting_its_identifier` |

That second row is the finding: with the emitter drifted, degrading the reader **promotes every member to
administrator and breaks AD15 in the granting direction** — and it is now caught at both the unit and the
HTTP level. Before B3 the identical mutation survived 31/31. This is the gap closed, demonstrated rather
than asserted.

**@architect's Q — is failing closed on `"True"` right, and is ordinal the correct comparison? Yes to
both, and I'd add the reason the worker didn't state.** `ClaimsPrincipal.HasClaim(type, value)` compares
the *type* with `OrdinalIgnoreCase` and the *value* with `Ordinal`, so ordinal is not a choice the code
made — it is the framework's contract, and pinning `"True"` documents it rather than imposing it. On
direction: the two failure modes are not symmetric. Fail-closed costs an administrator their AD15-widened
view — visible, immediately reported, and it violates nothing. Fail-open makes a member an administrator
— silent, and it breaks a binding decision. Choose the loud harmless failure over the quiet harmful one;
that is the correct call. Worth recording that this is a *robustness* argument, not a live security
boundary: the claim lives inside a data-protected auth cookie, so it is never attacker-supplied. The
value check earns its keep against a future **emitter**, which is exactly the scenario measured above.

**@architect's Q — is the extension on the path `ListAsync` and `RevokeAsync` authorise through?**
Precisely: it is the **single producer** of the flag both consume. `Invitations.razor:127`
(`User.IsAdministrator()`) feeds `RevokeAsync` at `:151` and `ListAsync` at `:158`, and a repo-wide grep
confirms no other production call site computes it. But state it exactly, because the distinction
matters for 4b and §7: the **service still takes `bool callerIsAdministrator` as a trusted parameter**
and does not derive it. That is AD15 as written ("`RevokeAsync` takes the caller's id and its
administrator flag"), so it is correct-per-decision, not a gap — but it means the extension is a
convention every future caller must follow, not a boundary the service enforces. A later route that
passes a literal `true`, or recomputes the check inline, walks straight past it. Worth one line in 4b's
brief: **anything calling `InvitationService` gets its flag from `User.IsAdministrator()` and nowhere
else.**

**@architect's Q — is the `Login.razor` literal-vs-constant coupling sufficient?** Yes, and I checked
rather than reasoned. Leaving `Login.razor` alone was the right scope call (§5 file, outside this block),
and the coupling is not held by hope: mutating the emitter to `"True"` fails
`An_administrator_sees_another_members_invitation` and `An_administrator_can_revoke_another_members_invitation`,
because both sign in as a real administrator through the real login form over HTTP. Drift in the emitted
value — case, constant, or conditional — goes red. Coverage exists in the other direction too: an emitter
that promoted everyone would fail `A_member_does_not_see_another_members_invitation`. Unifying the
literal in §6/§7 is a tidy-up, not a correctness need. No action before §4 lands.

### B4 — discharged ✅

The comment on `InvitationServiceTests.cs` names the line as the only assertion pinning AD14 to a real
clock and says not to DRY it into the constant it exists to check. That is the note that stops a future
tidy-up quietly deleting the test's reason to exist.

### Nits — not blocking, fix at your convenience or leave for §6

**Nit 1 — the `AddAuthorization()` comment quotes the wrong exception.** `Program.cs:44-46` says removing
it produces *"endpoint contains authorization metadata, but a middleware was not found that supports
authorization"*. That is the **both-removed** error. Measured for this removal alone (middleware kept,
landing guard confirmed): `InvalidOperationException: Unable to find the required services. Please add
all the required services by calling 'IServiceCollection.AddAuthorization' in the application startup
code.` The comment's substantive claim — required, not decorative; every request to an `[Authorize]` page
fails — is true and well made; only the quoted string belongs to a different experiment. Swap it, since
the whole point of these two comments is that someone can trust them.

**Nit 2 — the retraction is threaded inside the post it retracts.** It sits at DEVLOG line 2910, which
splits your original handoff in two: the original's tail ("Not built, and deliberately so…", the gates,
and "For @reviewer to look at hardest") now appears *below* the retraction, and item 3 of that list still
reads "all four additions are redundant for today's behaviour" — the retracted claim, restated after its
own retraction. The DEVLOG is append-only and is the durable record of how this was built; a reader
arriving cold hits the correction before the thing it corrects and the false claim after it. Move the
retraction to the end of the section as a new post, or strike item 3 in place.

### Re-verified unchanged from the first audit

Nothing in the B1–B4 edits touched behaviour, and I re-checked the properties that could have been
disturbed: Static SSR clean (no render mode, no `AddInteractive*`, no circuit anywhere in the diff); no
secret in a log, `TempData`, session, query string or redirect target (the only grep hit is the comment
saying not to); scope still exactly 4.1 + 4.4 with no redemption route, no `RedeemAsync`, no fallback
policy and no deny-anonymous default; AD15 still enforced in the query in both directions at both layers.
N2 (`RevokeAsync` check-then-act) correctly left for 4b per @architect — carrying it forward stands.

**→ @architect — Block 4a approved.** Open items travelling with it: N2 and the `AlreadyRedeemed`
end-to-end case into 4b's brief; the `User.IsAdministrator()`-is-the-only-producer convention into 4b/§7;
`AuthorizeRouteView` + `AddCascadingAuthenticationState()` still yours to rule on; Nit 1 and Nit 2 at
your discretion before the commit.

---

[worker] **Retraction and fixes for the reviewer's B1–B4. The plumbing table in my Block 4a handoff
post above is wrong — do not read it. The corrected table is below, and it matches the reviewer's
exactly.**

_(Post relocated here per @architect's Nit 2. It was first written **inside** the handoff post it
retracts, which split that post so its tail — including the very item this retracts — read as
current *after* the correction. The handoff post above now stands whole and unedited except that its
item 3 is struck in place with a pointer here. Nothing was deleted.)_

**Root cause, because it is the more useful finding.** `Program.cs` has **CRLF** line endings (87
CRs; it is a template-scaffolded file). My mutation script's perl patterns were written as
`s/app\.UseAuthorization\(\);\n//` — a bare `\n` after `);`, which cannot match `);\r\n`. So three of
the five plumbing mutations **never modified the file**, and I reported "11/11 pass" about code that
was never mutated. The script had no guard that the edit landed; a silent no-op was indistinguishable
from a surviving mutant. That is the §5 lesson — a test that cannot fail is worse than no test,
because it is counted — turned on the experiment I was running *to apply* that lesson. Twice as
embarrassing and worth writing down.

Two things this did **not** touch, checked rather than assumed: `InvitationService.cs` and
`Invitations.razor` are **LF**, and all nine M1–M9 mutants reported non-zero failures, which is proof
they applied. The mutant table stands; only the plumbing table was corrupt. Re-run with a
`shasum`-before/after guard that aborts unless the file actually changed:

| removed | `InvitationsPageTests` | |
|---|---|---|
| `app.UseAuthorization()` | **9 fail / 2 pass** | ← I reported 11/11 pass |
| `AddAuthorization()` | **11 fail** (500, missing services) | ← I reported 11/11 pass |
| `AddCascadingAuthenticationState()` | 11/11 pass | genuinely inert |
| `AuthorizeRouteView` → `RouteView` | 11/11 pass | genuinely inert |

**The failure mode is the point, and the reviewer is right that it matters more than the wrong row.**
The nine failures are all *authenticated* requests getting 302 instead of 200: `WebApplication`
auto-inserts the middleware at the **front** of the pipeline, ahead of the explicit
`UseAuthentication()`, so `[Authorize]` is evaluated against a not-yet-authenticated `User` and every
signed-in member is bounced to `/login`. Had that shipped, **both anonymous tests would have stayed
green** while the feature was broken for every real user — the tests you would expect to catch it are
exactly the ones that would not. My explicit `app.UseAuthorization()`, positioned after
`UseAuthentication()`, is the only reason an authenticated member can reach the page. **Withdrawing
the ❓ as posed:** two of the four are load-bearing, not redundant.

**B1 — `Program.cs` comments rewritten.** The ordering note now states the functional reason
explicitly: the auto-insertion-at-front behaviour, the 302-for-authenticated-users failure it
prevents, the measured 9-of-11, and that both anonymous tests stay green through it — with "do not
delete it as redundant with `AddAuthorization()`" said in as many words. `AddAuthorization()` is
labelled required (its absence is a 500, not a redirect) rather than "explicit for readability".

**B2 — `InvitationService.ListAsync` `<remarks>` rewritten to claim only what is true.** The old
comment credited the projection with protecting the invitation timestamps; it does not, because it
selects them. What the join actually buys is that the issuer's `Account` row is **never
materialised** — which is the §7 hazard, designed out. The new remark says both halves, and ends with
"copy this shape for the account side; do not copy it expecting it to protect the columns you
actually project", since §7 will copy from this comment.

**B3 — the administrator check is now tested, and the check moved to make that possible.** It was
inline in `Invitations.razor`, where a mutation to the bare-presence form could not be reached by any
test — hence 31/31 surviving. Extracted to `ClaimsPrincipalExtensions.IsAdministrator(this
ClaimsPrincipal)` in one tested place, with `ZeroWikiClaims.AdministratorClaimValue` naming the
`"true"` so the comparison value is not a literal at each reader. `Invitations.razor` now calls
`User.IsAdministrator()`. New `ClaimsPrincipalExtensionsTests` (10 cases): `"true"` grants;
`"false"`, `""`, `" true "`, `"0"`, `"yes"` and `"True"` all grant nothing; an authenticated
principal with no such claim, an anonymous principal, and a right-value/wrong-type claim all grant
nothing. **Mutated the implementation to `HasClaim(c => c.Type == …)` — 6 of the 10 fail.**
(`"True"` is asserted as *not* granting: `HasClaim` compares values ordinally, so a differently-cased
emitter loses rights rather than gains them. Pinned so a change to that direction has to be
deliberate.)

**B4 — `InvitationServiceTests.cs`** `Assert.Equal(IssuedAt.AddDays(7), …)` now carries a comment
marking it deliberately literal, naming it as the only assertion pinning AD14 to a real clock, and
saying not to DRY it into the constant it exists to check.

**Not fixed, correctly:** N2 (`RevokeAsync` check-then-act) is Block 4b's per @architect.

**One note for the record, not a fix:** `Login.razor` still emits the claim as the literal `"true"`
rather than `ZeroWikiClaims.AdministratorClaimValue`. I did not touch it — it is a §5 file and
outside this block — but emitter and reader are now held together only by
`An_administrator_sees_another_members_invitation`, which signs in as a real administrator over HTTP
and would fail if they drifted. §6 or §7 could unify the literal in one token.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **187/187 passed** (was 177;
+10 claim tests). `openspec validate invite-only-authentication --strict` — valid. `dotnet format
--verify-no-changes` — clean (exit 0). No behavioural change to any pre-existing file; the only
`src/` behaviour added is the extracted claim check, which is the same comparison it replaced.

---

[worker] **Nits N1 and N2 applied** (@architect's ruling; no `→ @reviewer`, sign-off already covers
this block).

**N1 — the misattributed exception string in `Program.cs` is fixed.** The reviewer is right, and the
mistake has the same shape as the comments B1/B2 just corrected: I quoted the *both-removed* error
against the `AddAuthorization()`-alone case, from a different row of the same experiment. The comment
now quotes the measured one — *"Unable to find the required services. Please add all the required
services by calling 'IServiceCollection.AddAuthorization'"* — and keeps the endpoint-metadata string
in parentheses, explicitly attributed to the both-removed case so the two cannot be confused again.

**N2 — the retraction is relocated and item 3 is struck in place**, as described at the top of the
post above. The wrong claim stays readable; it no longer reads as current.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **187/187 passed**.
`openspec validate invite-only-authentication --strict` — valid. `dotnet format
--verify-no-changes` — clean (exit 0).

**@architect's ruling recorded:** `AuthorizeRouteView` and `AddCascadingAuthenticationState()` stay,
inert though they are — §6/§7 render `AuthorizeView`, which needs the cascading state, and
`AuthorizeRouteView` keeps renderer and endpoint from being able to disagree.

---

_[architect] → @worker — **Block 4b = tasks 4.2, 4.3 and 4.5.** Read this whole post before starting.
Block 4a is committed and signed off; you are extending it, not revisiting it._

**This is the anonymous half of §4, and that is the entire point of the split.** The caller has no
account, no session and no audit trail. Every line you add is reachable by anyone who can make an
HTTP request. 4a's code is reusable; **4a's threat model is not**, and the review will check that you
did not carry it across.

**Tasks in scope**

- `4.2` Redeem an invitation: validate (unredeemed, unexpired, unrevoked), create the account with a
  chosen username and password, mark the invitation redeemed.
- `4.3` Reject expired, already-redeemed, or revoked invitations.
- `4.5` Ensure there is no open/self-service registration path.

**Binding spec (verbatim, `specs/invitations/spec.md`)**

> ### Requirement: Invite-only account creation
>
> The system SHALL create new accounts only by redeeming a valid invitation. The system SHALL NOT
> offer open/self-service registration.
>
> #### Scenario: No open registration
>
> - **WHEN** an anonymous visitor attempts to create an account without an invitation
> - **THEN** the system does not create an account and provides no open registration path
>
> #### Scenario: Account created by redeeming an invitation
>
> - **WHEN** a valid, unredeemed, unexpired invitation is redeemed with a chosen username and password
> - **THEN** the system creates the account and marks the invitation as redeemed

> ### Requirement: Invitation validity and revocation
>
> #### Scenario: Expired invitation is rejected
>
> - **WHEN** an invitation is redeemed after its expiry
> - **THEN** the system rejects it and creates no account
>
> #### Scenario: Already-redeemed invitation cannot be reused
>
> - **WHEN** an invitation that has already created an account is redeemed again
> - **THEN** the system rejects it and creates no second account
>
> #### Scenario: Revoked invitation cannot be redeemed
>
> - **WHEN** an invitation is revoked and then a redemption is attempted
> - **THEN** the system rejects it and creates no account

**Binding decisions — the five that will decide the review**

1. **"Single-use" is a CONCURRENCY requirement, exactly as "exactly one administrator" was (§3/B1).**
   Two simultaneous redemptions of the same invitation must create **one** account. A read-then-write
   cannot achieve this on SQLite: the read takes no write lock, so both callers observe an unredeemed
   invitation and both inserts succeed. Take the write lock **before** the check —
   `connection.BeginTransaction(deferred: false)`, which issues `BEGIN IMMEDIATE`. **There is no async
   overload**; `BeginTransactionAsync()` gives you the deferred transaction that does *not* hold.
   `BootstrapService.CreateFirstAdministratorAsync` is the worked example — follow its whole shape,
   including enlisting via `db.Database.UseTransactionAsync(...)` so EF's bookkeeping stays straight.
   **Prove it with a genuinely concurrent test** (`BootstrapConcurrencyTests` is the pattern), not the
   happy path run twice.
2. **The Argon2id hash is computed BEFORE the write lock is taken, and never inside it.** ~93 ms at
   64 MiB; holding SQLite's single write lock for that long serialises every other writer behind a CPU
   burn. But equally — **the cheap validity checks come before the hash** (BL1/BL2's lesson). This
   route is anonymous, so an attacker who can make the server derive a 64 MiB hash before it notices
   the token is garbage has a free amplifier. Order: cheap token lookup and validity → hash → write
   lock → re-check under lock → insert. Note that this puts a *pre-lock* check and an
   *under-lock* re-check in the same method; that is deliberate and is what bootstrap does.
3. **AD7 — the expiry predicate MUST reach SQL. This is the single most important test in §4.** Assert
   on `ToQueryString()` that `ExpiresAt > now` appears in the WHERE clause and is not a client-side
   filter, exactly as `DateTimeOffsetStorageTests` does. Expiry is a security boundary, and the
   built-in `DateTimeOffsetToBinaryConverter` was **measured silently admitting an expired row** —
   that is why AD7's fixed-width ISO-8601 converter exists. A test that only checks "an expired
   invitation is rejected" against a correctly-configured context would pass while the boundary was
   one converter change from failing open.
4. **AD17 — name the reason, but only after the token has matched a stored row.** Expired / already
   used / revoked are told to the invitee; a token matching **nothing** gets one uniform "this
   invitation link is not valid". The named reasons must be **unreachable** without a hash match — if
   any input an anonymous caller controls can produce a distinguishing response without possessing a
   real token, you have rebuilt the oracle §5 closed. Test both halves: the reasons are distinct for
   a held token, and an unknown token is indistinguishable from a malformed one.
5. **AD18 — redemption does NOT establish a session.** Create the account, mark the invitation
   redeemed, redirect to `/login`. Do not call `SignInAsync` anywhere in this block. §5's login stays
   the only route that mints a session.

**Also binding, and cheaper to get right the first time**

- **AD10 + AD11 — the same 12-character password minimum and the same username charset as bootstrap.**
  Use `CredentialPolicy` and `CredentialPolicy.UsernameMatcher()`. **Do not hand-roll a `Regex`** over
  `UsernamePattern` and do not reintroduce an unbounded quantifier (that was BL2). AD10 exists
  *precisely* so the two password-choosing paths cannot diverge — this is the second one, and it is
  the reason the constant is shared rather than duplicated.
- **N2 (reviewer, carried from 4a — you are closing it).** `RevokeAsync` is currently check-then-act
  with no transaction. Harmless while nothing else writes these rows; the moment redemption exists, a
  redemption committing between revoke's read and its write yields a row with **both `RedeemedAt` and
  `RevokedAt`**, reporting `Revoked` for an invitation that already created an account. Close it with
  the same write-lock discipline, and test the interleaving.
- **`AlreadyRedeemed` needs its end-to-end case.** 4a could only prove the enum value in isolation.
  Now that redemption exists, exercise revoke-after-redeem for real.
- **`InvitationPolicy.RedemptionPath`** (`/invite`) already exists so 4a's link and 4b's page cannot
  spell the path differently. Use it; do not re-literal it.
- **The token arrives in the URL — treat that as a known cost, not a thing to fix here.** A copy-a-link
  handoff is `design.md`'s resolved answer, so the token is in the query string and therefore in
  browser history and any proxy log. Do not make it worse: **never log the token**, and do not put it
  in the redirect target after a successful redemption. Redemption consumes it, which is what bounds
  the exposure.
- **The `IsAdministrator()` convention (reviewer).** `InvitationService` takes
  `bool callerIsAdministrator` as a **trusted parameter** and does not derive it — AD15 as written, so
  correct, but it means `ClaimsPrincipalExtensions.IsAdministrator()` is a convention callers must
  follow rather than a boundary the service enforces. Nothing to change; do not add a *third* caller
  that passes a literal.
- **AD16's mutation rule** — verify the file actually changed (checksum before/after) before believing
  any mutation result. A no-op mutation is indistinguishable from a surviving mutant, and that is how
  4a's plumbing table came to be wrong.

**4.5 — how to prove a negative.** "No open registration" is not provable by a test that pokes at
guessed URLs; the next route someone adds would not be covered. Build the **structural** test:
enumerate the application's endpoints (`EndpointDataSource` off the booted `ZeroWikiAppFactory`) and
assert that the set of endpoints reachable **anonymously** that can create an `Account` is exactly
`{/bootstrap (inert once populated), /invite (requires a matching token)}`. That test stays true as
routes are added, which a URL-guessing test does not. If you find a materially better shape, say so in
the DEVLOG before building it.

**Test expectations** — `dotnet test` is at **187** green; you add to that.

- The concurrent-redemption test (binding decision 1) and the `ToQueryString()` expiry test (3) are
  the two the reviewer will look at hardest. Neither can be replaced by a happy-path test.
- Reuse `tests/ZeroWiki.Tests/Web/` (`ZeroWikiAppFactory`, `StaticSsrForm`, `HttpAssertions`),
  `RecordingPasswordHasher`, `CapturingLoggerProvider`. `StaticSsrForm` gained a form-scoped overload
  in 4a for multi-form pages.
- Prove **no account is created** on every rejection path, not merely that the response was an error.
  The spec's wording is "rejects it and creates no account" — assert the store.
- **Break your own implementation on purpose and confirm each new test goes red.** §5 shipped two
  tests that asserted nothing while passing; 4a shipped a claim check no test could reach. Both were
  found by mutation, not by reading.

**Product-Owner verification — 4.2 does NOT get ticked on gates alone.** Like §3.2 and §5.3, redeeming
an invitation in a real browser is a human-in-the-loop step. Implement and self-test to the gates,
then **draft a precise, copy-pasteable recipe** for the Product Owner: exact commands, what to click,
what they should see at each step, ending with a successful login as the new account. Write it against
**AD10's 12-character minimum** and **AD18's redirect-to-login** — the §3 recipe went stale by
predating AD10, and this one must not repeat that. Post the recipe to the DEVLOG and hand off; the
Architect gates the tick on the Product Owner's confirmation.

**Done-gates — all four, before anything is ticked**

1. `dotnet build` clean (no errors)
2. `dotnet test` green — your new tests **and** all 187 existing ones
3. `openspec validate invite-only-authentication --strict`
4. `dotnet format --verify-no-changes` clean

Post progress and any `❓ @architect` questions to this section as you go, then hand off with
`→ @reviewer`.

---

[worker] **Block 4b — plan, and the two calls the brief leaves to me.** Posting before I build so
neither is a surprise at review. Brief read in full, plus 4a's thread, AD4/7/10/11/14/15/16/17/18 and
the `NEXT` standing rules.

**Shape:** `InvitationService` gains `RedeemAsync` (anonymous, token-authorised), `ValidateAsync`
(the advisory "why can't I redeem this" the GET renders), and a shared `Redeemable(...)` queryable so
the AD7 predicate exists in exactly one place. `RevokeAsync` gets the same write-lock discipline
(closing N2). New `/invite/{Token}` Static SSR page; success redirects to `/login` (AD18) — no
`SignInAsync` anywhere in the block. Order inside `RedeemAsync` is BL1's: token hash → cheap validity
→ **Argon2id** → `BEGIN IMMEDIATE` → SQL re-check under the lock → insert.

**Call 1 — redemption has a sixth outcome, `UsernameTaken`, and it is *inside* AD17's boundary.**
AD17 names three reasons and one uniform answer for a token matching no row. It does not cover "your
token is fine, but that username is taken" — which is not an invitation state at all. Not naming it
would tell a genuine invitee "this invitation link is not valid" about a link that is perfectly valid,
and they would burn the invite chasing the wrong problem. I am treating this as a gap AD17 does not
address rather than a decision to reinterpret, and I am holding the boundary AD17 *does* bind: the
uniqueness check happens **under the write lock, after the presented token has already matched a
stored hash**, so `UsernameTaken` is unreachable without possession of a real token, exactly like the
three named reasons. A taken username does **not** consume the invitation — the invitee retries with
another name. ❓ @architect — say so if you would rather this collapse into the uniform message.

**Call 2 — both credential guards go at the service boundary, mirroring `BootstrapService`.** The
brief says "the same 12-character password minimum and the same username charset **as bootstrap**",
and bootstrap enforces both in the service *and* in the form. `NEXT`'s standing rule pulls the other
way ("a policy *number* only where the record is privileged and the mistake irreversible… do not cite
it as precedent"). I read the standing rule as a bar on *generalising* AD10 into services, not as a
bar on the one other path AD10 names by name so "the two cannot diverge" — and no password reset
exists in this change, so a weak password here is as permanent as bootstrap's. Fail-closed, one `if`,
in front of the hash so it cannot become an amplifier. Flagging it because it is the closer of the two
readings; @reviewer, this is the line to argue with if either of you reads the standing rule the other
way.

**4.5** — building the structural test as briefed, with one strengthening: rather than reading
`[Authorize]` metadata off each endpoint (which §6's fallback policy would make meaningless — the
policy lives in middleware options, not endpoint metadata), I enumerate `EndpointDataSource` and then
**probe each route anonymously over the real pipeline**, classifying by what the site actually does.
That keeps the test true through §6 instead of silently going vacuous the moment §6 lands. Same
assertion either way: the anonymously reachable set is exactly the named list, and only two members of
it can create an account.

---

[worker] **Block 4b (4.2 + 4.3 + 4.5) implemented.** Summary:

- **`InvitationRedemption`** — six-member outcome enum. `Redeemed`, the uniform `NotValid`, the three
  AD17 reasons, and `UsernameTaken` (call 1 in my plan post above).
- **`InvitationService.RedeemAsync(presentedToken, username, password)`** in the order the brief
  fixed: structural credential guards → `string.IsNullOrEmpty(token)` → SHA-256 token hash → cheap
  indexed lookup and classification → **Argon2id** → `BEGIN IMMEDIATE` → SQL re-check under the lock
  → uniqueness check under the lock → one `SaveChanges` inserting the account and stamping
  `RedeemedAt` → commit. Early returns roll back by disposal.
- **`InvitationService.Redeemable(IQueryable<Invitation>, DateTimeOffset)`** — the AD7 predicate, in
  one public place so the check and the test that reads its SQL cannot diverge. It is what the
  redemption path runs; there is no second copy.
- **`ValidateAsync`** — the advisory "why can't I redeem this", shared with `RedeemAsync`'s pre-lock
  check so the page's courtesy answer and the authoritative one come from the same code.
- **`RevokeAsync` now holds the write lock across its read and its write — N2 closed.**
- **`/invite/{Token}`** (`RedeemInvitation.razor`) — Static SSR, one POST-only form, `Input.*` field
  names, no render mode, no circuit. Success → `Navigation.NavigateTo("/login")`. **No `SignInAsync`
  anywhere in the block** (grep-verified), no logging of anything, no `TempData`/session/query-string
  carriage of the token, and the token is not in the redirect target.
- **`RedeemInvitationInput`** — AD10/AD11 from `CredentialPolicy`, same attributes as
  `BootstrapInput`, same BL0008 no-initializer idiom.
- **Tests: 55 new (242 total).** `InvitationRedemptionTests` (30), `InvitationRedemptionConcurrencyTests`
  (3), `RedeemInvitationPageTests` (18), `NoOpenRegistrationTests` (4). `InvitationServiceTests` gained
  one line for the new constructor parameter; nothing else of 4a's was touched.

**A real defect the tests found, worth stating plainly because it is not obvious.** My first version
ran the advisory `ValidateAsync` in `OnInitializedAsync` unconditionally. That hides the form when the
invitation is bad — and **a form that is not in the render tree has no submit handler for the
framework to dispatch a POST to**, so an invitation that expired while the invitee was typing got a
bare **400**, not a decision. The authoritative under-lock check was unreachable from the page on
exactly the paths 4.3 is about. The advisory check is now confined to the GET
(`HttpMethods.IsGet(HttpContext.Request.Method)`), so the POST always reaches `RedeemAsync` and
`RedeemAsync` is always what refuses. **M13 below is that mutation** — the tests now hold it down.

**Judgement calls where the brief left latitude:**

1. **`UsernameTaken` is a sixth outcome, and it does not consume the invitation.** Reasoned in my plan
   post; AD17's boundary is held (unreachable without a hash match — it is checked *under the lock*,
   after the token has matched). The page keeps the form up for this one outcome only, so a name clash
   cannot burn a good link. If @architect prefers the uniform message, it is a small change.
2. **Both credential guards at the service boundary**, mirroring bootstrap. Also reasoned above; this
   is the call I would most expect an argument about, and the standing rule is the thing to argue
   from.
3. **The reason-classification precedence is used → revoked → expired**, matching `Invitations.razor`'s
   `Describe`, so the issuer's list and the invitee's page cannot tell different stories about one
   row. Pinned by a test and by M17.
4. **Redemption disagreeing with SQL fails closed to the uniform message.** If the under-lock SQL gate
   refuses a row that the C# re-read then classifies as fine — which would mean the AD7 converter
   failure mode — the answer is `NotValid`, not a redemption and not a guess at a reason.
5. **No logging.** `LoginService` logs its three-way distinction because AD8 requires it; nothing in
   §4's spec asks for a redemption audit trail, and adding one would put a fifth dependency and a new
   "what is safe to log next to a token" question into an anonymous path. Flagging rather than
   deciding it is out of scope forever — §7 may want it.
6. **`Redeemable` is `public static`.** The alternative was asserting only on captured SQL; I did both,
   but the brief asked for `ToQueryString()`, and `ToQueryString()` cannot be pointed at a query built
   privately inside a service without exposing it. Exposing the predicate is the honest way to make the
   test read production code rather than a lookalike.

**AD7 is pinned twice, deliberately.** `The_redeemability_predicate_is_evaluated_in_sql` asserts
`ToQueryString()` contains `"ExpiresAt" > `, `"RedeemedAt" IS NULL` and `"RevokedAt" IS NULL` in the
WHERE clause. `The_expiry_comparison_reaches_sqlite_on_the_redemption_path_itself` reads the statements
SQLite **actually executed** during a real `RedeemAsync` (via `LogTo`) and asserts the same. The first
can be satisfied by a helper nobody calls; the second cannot.

**Broke it on purpose — 17 mutants, all caught.** Every run used a `shasum` before/after guard that
aborts on a no-op (AD16). Baseline and restored both 86/86 on
`FullyQualifiedName~Invitation|FullyQualifiedName~NoOpenRegistration`.

| mutant | failures |
|---|---|
| M1 `Redeemable` drops `ExpiresAt > asOf` | **3** |
| M2 `Redeemable` drops `RedeemedAt IS NULL` | **3** |
| M3 `Redeemable` drops `RevokedAt IS NULL` | **2** |
| M4 the write lock becomes `deferred: true` | **2** |
| M5 the password is hashed **inside** the write lock | **1** |
| M6 the password is hashed **before** the token is checked | **1** |
| M7 `RevokeAsync` loses its write lock (the N2 regression) | **1** |
| M8 an unmatched token is told a reason (AD17 breach) | **7** |
| M9 redemption mints an administrator | **2** |
| M10 the username uniqueness check is dropped | **3** |
| M11 AD10's password guard removed | **1** |
| M12 AD11's username guard removed | **4** |
| M13 the advisory check also runs on the POST (the defect above) | **4** |
| M14 the consumed token is carried into the redirect | **1** |
| M15 a username clash consumes the invitation | **2** |
| M16 `app.MapGet("/register", …)` added to `Program.cs` | **1** |
| M17 reason precedence puts expiry before "used" | **3** |

M16 is the one that says whether 4.5's structural test is worth anything: adding a real open
registration route to `Program.cs` fails
`The_routes_an_anonymous_visitor_can_reach_are_exactly_the_ones_named` without anybody having
guessed the URL. M5 is worth a word too — it is asserted by opening a second connection and issuing
`BEGIN IMMEDIATE` *while a hash is in flight*, so it measures the lock rather than the clock.

**A process note, because it cost half an hour and would cost the next person the same.** My first
mutation harness restored with `git checkout -- <file>`. Block 4b is **uncommitted**, so that
restored `InvitationService.cs` to 4a and silently deleted the block's work in it; every mutant after
the first then reported a no-op. The landing guard caught it immediately (that is what it is for), and
the file was rewritten and re-verified green before anything else ran. **A mutation harness in this
repo must restore from a copy, never from git, while the work is uncommitted.** Recorded next to
AD16's rule since it is the same family of mistake.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **242/242 passed** (187 existing +
55 new), re-run four times, all green. `openspec validate invite-only-authentication --strict` —
valid. `dotnet format --verify-no-changes` — clean (exit 0). *Wall-clock note so it is not read as a
regression: the full suite takes ~2 minutes on this machine, but the 187 pre-existing tests take 1 m
36 s of that on their own — it is real Argon2id at 64 MiB, not anything this block added.*

**For @reviewer to look at hardest:**
1. **`UsernameTaken` (call 1)** — whether a sixth outcome is inside AD17 or a reinterpretation of it.
   I argued the former in the plan post; it is the call most worth a second reading.
2. **The service-boundary credential guards (call 2)** — the standing rule can be read against this.
3. **`RedeemAsync`'s two-checks-one-method shape** — a pre-lock advisory classification and an
   under-lock SQL gate, with the `?? NotValid` fail-closed fallback between them. Bootstrap has the
   same shape, but mine has a third read in the raced branch and that is the bit I would audit.
4. **`WriteLock` disposing both halves.** EF's `UseTransaction` enlistment does **not** own the
   `SqliteTransaction`, so disposing only the enlistment would leave `BEGIN IMMEDIATE` holding the
   store's write lock. `BootstrapService` gets this right by nesting two `await using`s; I moved it
   into one type. Worth confirming the ordering is equivalent.
5. **`NoOpenRegistrationTests.IsAssetPlumbing`** matches two ASP.NET Core marker types **by name**
   because both are `internal`. I argued it fails safe (a rename un-filters and floods the assertion),
   but it is the loosest thing in the block.

→ @reviewer

---

[worker] **Product-Owner verification recipe for 4.2** (@architect — 4.2 is not tickable on gates).

**This was dry-run end to end before being written down**, per the §3 lesson: the app was started on
the real `http` profile and every step below driven over HTTP against a throwaway store. Each
"you should see" is a transcribed result, not an expectation. Your own store at
`src/ZeroWiki/App_Data/identity.db` was not touched.

**Setup**

```bash
cd /Users/rendle/github/emmz/ZeroWiki
pkill -f ZeroWiki          # a stale instance still holding 5171 looks exactly like a feature bug
dotnet run --project src/ZeroWiki
```

Leave it running. It binds **`http://localhost:5171`** only — `https://localhost:7070` is
connection-refused on this profile.

**1 — sign in as yourself.** Open `http://localhost:5171/login` and sign in with your existing
administrator account (the one you created during the §3 check).
→ You land on the home page.

**2 — issue an invitation.** Go to `http://localhost:5171/invitations` and click **“Create an
invitation”**.
→ A box appears headed **“Invitation created”**, with a link of the form
`http://localhost:5171/invite/<43 random characters>`. **Copy it now** — it is shown once and
reloading the page will not show it again. The table below gains a row reading **“Waiting to be
used”** with a **Revoke** button.

**3 — become the invitee.** Open a **private / incognito window** (this matters: the invitee must be
anonymous, and your normal window is signed in). Paste the link.
→ Page titled **“Accept your invitation”**, with **Username**, **Password** and **Confirm password**
boxes and a **“Create my account”** button.

**4 — check the password minimum (AD10).** Type username `newcomer`, password `short` in both
password boxes, click **Create my account**.
→ The page comes back with **“A password must be at least 12 characters.”** and no account is
created. This is the same 12 as the bootstrap form, from the same constant.

**5 — redeem it properly.** Username `newcomer`, password `another good passphrase` in both boxes
(24 characters), click **Create my account**.
→ You are taken to the **sign-in page** at `http://localhost:5171/login`. **You are deliberately not
signed in** — AD18: redemption creates the account, login is the only thing in ZeroWiki that starts a
session.

**6 — sign in as the new account.** Still in the private window, sign in as `newcomer` /
`another good passphrase`.
→ You land on the home page. Visit `http://localhost:5171/logout` to confirm: it reads **“You are
signed in as newcomer.”** *(This is the step that proves the whole loop — the password you chose in a
browser was hashed with Argon2id and verifies on login.)*

**7 — the link is spent (4.3).** In the private window, paste the **same** invitation link again.
→ **“This invitation has already been used to create an account. If that was you, sign in instead.”**
and **no form**.

**8 — a bad link says nothing (AD17).** Visit `http://localhost:5171/invite/nonsense`.
→ **“This invitation link is not valid. Ask whoever invited you for a new one.”** Note it does *not*
say expired, used or revoked — a token matching nothing gets one uniform answer, so it cannot be used
to probe for real invitations.

**9 — back in your own window**, reload `http://localhost:5171/invitations`.
→ The row now reads **“Used”** and its **Revoke** button is gone.

**Optional extras if you want to see 4.3's other two paths**

- **Revoked:** issue a second invitation, copy the link, click **Revoke** on its row, then open the
  link in the private window → **“This invitation was withdrawn. Ask whoever invited you for a new
  one.”**
- **Name clash:** issue a third invitation and try to redeem it as `newcomer` → **“That username is
  already taken. Your invitation is still good — choose another name and try again.”** The form stays
  up and the row still reads “Waiting to be used”, so the link is not wasted.

**Afterwards.** The `newcomer` account persists in your gitignored store. To remove it (with the app
stopped):

```bash
pkill -f ZeroWiki
sqlite3 src/ZeroWiki/App_Data/identity.db "DELETE FROM Accounts WHERE Username = 'newcomer';"
```

Leave the used invitation row alone — it is harmless, and it is the record that the invitation was
consumed. **Do not delete `App_Data/`**: it holds your administrator account and nothing recreates it.

---

[worker] **@architect's four rulings applied.** Recipe above is unaffected — no behaviour the Product
Owner sees has changed.

**Ruling 1 — `UsernameTaken` stays, and the residual is now named in the code.** The `<remarks>` on
`InvitationRedemption.UsernameTaken` states it as an oracle rather than around it: **a holder of a
live, unredeemed invitation can probe whether a username exists**, which is username enumeration —
the thing §5 spends a dummy hash and a three-way private log closing — reached from a direction §5
does not cover. It records who can reach it (someone the system is actively granting membership to,
not an anonymous stranger — the distinction §5's oracle did not require), why every alternative is
worse (a uniform message leaves a genuine invitee unable to get in *and* unable to learn why), that
the invitation deliberately survives the clash, and that **neither reason survives being carried to
another surface, so this is not precedent**. No "no security impact" softening anywhere in it.

**Ruling 2 — the guard comment now rests on AD10's own wording**, quoting *"applies to every path
where a user chooses a password — §3 bootstrap and §4 invitation redemption — so the two cannot
diverge"*, so a reader sees the decision scoping itself to both paths rather than §3's exception being
widened. AD11 is stated separately as a structural invariant (RFC 7617 makes a colon illegal in the
Basic-auth userid the git remote will present), which belongs at the boundary unconditionally.
@architect is right that this is the stronger argument; I had cited the brief, which is the weaker one.

**Rulings 3 and 4** — precedence and the fail-closed disagreement branch — unchanged.

**Ruling 5 — logging added, exactly two statements.**

- **Success:** `"Invitation {InvitationId} redeemed: it created account {AccountId}."` The comment
  above it says why it is load-bearing rather than chatter — `Invitation` has `RedeemedAt` but **no
  `RedeemedByAccountId`**, so once a row is consumed the store can say it was used and *not by whom*,
  and this line is the only place that answer exists. It also records that adding the column would be
  a schema change outside this change's specs, so nobody "tidies" the line away thinking the store
  covers it.
- **Refusal:** one `Rejected(outcome)` helper logging
  `"Invitation redemption refused: {Outcome}."`, funnelling all four refusal returns through one
  place. The `ArgumentException` paths are caller errors, not refusals, and stay unlogged.
- **Never** the token, its hash, or the password — `LoginService`'s posture, and asserted rather than
  asserted-about.

Three new tests (245 total): the success line carries both ids;
every refusal names its reason in order; and `No_token_hash_or_password_ever_reaches_the_log` sweeps
the whole log after a refusal, a clash and a success.

**Mutants re-run in full after the edit — 20 now, all caught.** Three of the earlier patterns
(M6/M10/M15) stopped matching once the refusal returns were funnelled through `Rejected(...)`; the
landing guard reported them as no-ops rather than as survivors, which is the second time this block
that AD16's rule was the thing that noticed. Patterns fixed and re-run. Baseline and restored both
89/89 on the block filter. Three new:

| mutant | failures |
|---|---|
| M18 the success line drops the account id | **1** |
| M19 refusals are no longer logged | **1** |
| M20 the refusal log carries the presented token | **2** |

**Standing rule, for pinning as requested: a mutation harness restores from a *copy*, never from
`git checkout`, while the block is uncommitted.** Mine used `git checkout -- <file>` and so restored
`InvitationService.cs` to 4a, deleting the block's uncommitted work in it; every mutant after the
first then reported a no-op. AD16's landing guard is what turned a silent wipe into an immediate
`!!! NO-OP` on the very next line of output. The rule generalises past mutation: **any script in this
repo that "restores" a file must restore what was there, not what git last saw.**

**And the §4.3 defect, restated in @architect's terms because the framing is better than mine:** the
advisory `ValidateAsync` running on every render hides the form when the invitation is bad, and **a
form absent from the render tree has no submit handler for the framework to dispatch a POST to** — so
an invitation expiring mid-typing returned a bare **400** and the authoritative under-lock check
became unreachable **on exactly the 4.3 paths this block exists to implement**. It reads as a UI
nicety and is a hole in the requirement. M13 is the mutation that pins it, and it fails 4 tests.

**Gates re-run:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **245/245 passed** (187
existing + 58 new). `openspec validate invite-only-authentication --strict` — valid. `dotnet format
--verify-no-changes` — clean (exit 0). No `→ @reviewer` per @architect; the review is being spawned.

---

[reviewer] **Block 4b (4.2, 4.3, 4.5 + 4a's N2) audited. Verdict: Request changes** — **two** blocking
findings, and they are both in the same place: *the two properties the brief named as the ones the
review would look at hardest are the two the test suite does not actually hold down.* The
implementation is right. The proofs are not.

**Gates re-verified independently, all four green:** `dotnet build --no-incremental` 0 Warnings /
0 Errors; `dotnet test` **245/245**; `openspec validate invite-only-authentication --strict` valid;
`dotnet format --verify-no-changes` exit 0. The worker's numbers reproduce exactly.

**Method note, since it is what found both blockers.** I re-ran seven mutants myself under my own
landing guard, restoring **from a copy in the scratchpad, never from git** (the block is
uncommitted). Five reproduced the worker's result. Two survived — one of the worker's own (M4) and
one I constructed. Working tree restored and checksum-verified against the handoff state afterwards:
`InvitationService.cs` `be7fcb7a…`, `Program.cs` `0a878430…`, `RedeemInvitation.razor` `835135ca…`,
probe file deleted, `git status` byte-identical to the pre-audit snapshot.

---

### 🚫 B1 — the concurrency proof does not survive the gate the workflow actually runs

`M4` (the write lock becomes `deferred: true`) is **caught 6 runs out of 6** when
`InvitationRedemptionConcurrencyTests` is run on its own, and caught under the worker's 89-test block
filter (3 runs: 1, 2, 2 failures — already variable). It **passes `dotnet test` 245/245, 4 runs out of
4.** That is the gate in `CLAUDE.md` step 5, and it is the one a future block will run.

The mechanism is in `InvitationRedemptionConcurrencyTests.cs:60-76`. `release.SetResult()` with
`RunContinuationsAsynchronously` queues eight continuations onto the thread pool; under full-suite
parallel load the pool is already saturated by the other test classes, so the eight are drained
near-serially. The winner commits before the losers reach their `SELECT`, every loser refuses at the
**pre-lock** `RejectionAsync`, and the deferred-transaction race never materialises. Wall-clock
corroborates: with M4 applied the class alone takes **1 m** (SQLite busy-timeout waits — the race
happening), while inside the full suite all 245 finish in **14 s** (the race not happening).

So the test does not prove single-use under concurrency; it proves it *when the machine happens to be
idle*. The brief's wording was "prove it with a genuinely concurrent test, not the happy path run
twice" — as landed this is closer to the happy path run eight times whenever the suite is busy.

**Suggested shape (your call, @worker).** Stop hoping for parallelism and force the rendezvous. The
block already has the hook: `CountingPasswordHasher.OnHash` fires *between* the pre-lock check and
`BEGIN IMMEDIATE`, which is exactly the point at which all attempts must be parked for the race to
exist. Hold every attempt there on a `Barrier`/`CountdownEvent` until all of them have completed the
pre-lock read, then release. With `deferred: false` the second blocks on `BEGIN IMMEDIATE`, re-reads
the winner's committed row and refuses; with `deferred: true` both `SELECT` clean and both insert.
Run the attempts on dedicated threads (`TaskCreationOptions.LongRunning` or `new Thread`) so pool
starvation cannot serialise them. **Re-verify M4 under the full `dotnet test`, not under a filter** —
the filter is what made this invisible.

Same caveat applies to `A_revocation_racing_a_redemption_never_leaves_a_row_that_is_both`: the
assertion is good (it checks the exclusive-or of the two outcomes *against the row*, not just the
row), but its ability to *catch* a regression rests on the same scheduling luck. Please re-check
**M7** under the full suite once B1's construction lands.

### 🚫 B2 — AD17's boundary is unasserted for `UsernameTaken`, the one outcome kept *because* it sits inside that boundary

@architect's ruling 1 let the sixth outcome stay on the explicit ground that it is "under the lock,
post-hash-match, so inside AD17's boundary". Nothing in the suite pins that.

I moved the uniqueness check to before the token lookup — five inserted lines, landing-guard verified
(`be7fcb7a…` → `144e1176…`). An anonymous caller then presents `"not-a-real-token"` with username
`"alice"` and is told **`UsernameTaken`**: username enumeration with no invitation at all, from an
anonymous stranger, which is precisely the oracle the `<remarks>` argues is acceptable *only* because
the prober must hold a live invitation. **The worker's entire block suite stays green — 89/89.** A
single-assertion probe fails it in 456 ms.

`InvitationService.cs:279-282` is correct today. Nothing holds it there. M8 pins the boundary for the
three named reasons (`RejectionAsync`'s `null` arm — 8 failures), but the sixth outcome has no
equivalent, and the reason the gap is invisible is structural: **every existing garbage-token test
uses a username that does not exist** — `"bob"` in `InvitationRedemptionTests.cs:111-122`,
`"intruder"` in `NoOpenRegistrationTests.cs:118-131`. The one input that would expose it is never
supplied.

**Fix:** one service-level assertion — an unmatched token plus an **existing** username returns
`NotValid` — and, ideally, its page-level twin asserting the body is byte-identical to the
unmatched-token/unknown-username body (`An_unknown_token_is_indistinguishable_from_a_malformed_one`
already has the right shape to extend). Then re-run your MX equivalent and confirm it goes red.

---

### Notes — not blocking

- **N1 — the authoritative under-lock expiry check compares against a pre-hash clock.**
  `InvitationService.cs:246` captures `now`; ~93 ms of Argon2id (`:257`) and an unbounded wait on
  `BEGIN IMMEDIATE` (`:259`, default SQLite busy timeout 30 s) then pass before `Redeemable(…, now)`
  runs at `:263`. The decision that "binds" therefore uses a timestamp up to ~30 s stale and can admit
  an invitation that expired while the caller was queued. Trivial against a 7-day lifetime and of no
  adversarial value (you would have to be racing your own just-expired token), but the block's own
  framing is that the under-lock check is the authoritative one, and re-reading `GetUtcNow()` after
  the lock costs nothing. Worth deciding rather than inheriting.
- **N2 — `OpenConnectionAsync` has no matching close.** `BeginWriteLockedTransactionAsync`
  (`InvitationService.cs:391`) increments EF's open-count; `WriteLock.DisposeAsync` (`:421-425`)
  disposes both transaction halves — correctly, and your item 4 is right that disposing only the
  enlistment would leave `BEGIN IMMEDIATE` holding the lock — but never calls `CloseConnectionAsync`,
  so the connection stays open for the life of the scoped context. `BootstrapService.cs:93` has the
  identical omission and was approved in §3, so this is consistency rather than a regression; 4b just
  adds two more call sites and `RevokeAsync` now hits it on every call. `WriteLock` is the one place
  that would fix all of them.
- **N3 — `RevokeAsync` now takes the store's global write lock on every call, including `NotFound`.**
  `:131` runs before the scoping query, so any authenticated member can take `BEGIN IMMEDIATE` by
  revoking an id that does not exist. Correct per N2's closure and low risk on an authenticated
  surface; recorded because §6/§7 put more writers behind the same single lock.
- **N4 — a corrupt AD7 timestamp is a 500 on an anonymous route.** The under-lock read materialises
  the `Invitation` entity (`:263`) and `RejectionAsync` projects `ExpiresAt` (`:365`); both throw on
  an unreadable value, so the AD7 corruption mode surfaces as a 500 rather than the uniform refusal.
  It is *inside* AD17's boundary (only a token holder reaches it), so not an enumeration oracle — but
  it is the same shape as §5's R4 and belongs on §7's note list.
- **N5 — the log sweep asserts the rendered message only.** `CapturingLoggerProvider.Messages` is
  `formatter(state, …)`, so a secret passed as a *surplus* argument beyond the template's
  placeholders would be dropped from the rendered string and escape
  `No_token_hash_or_password_ever_reaches_the_log` while still reaching a structured sink. M20 landed
  inside the template, so it was caught. Asserting over `Entries`' state would close it. Theoretical;
  recorded only.
- **N6 — `RedeemAsync` throws `ArgumentException` on an anonymous route** (`:233`, `:238`).
  Unreachable today — I checked `RedeemInvitationInput`'s DataAnnotations attribute by attribute
  against both guards and they mirror exactly, and `OnValidSubmit` gates the call — so no anonymous
  500. It is §3's approved shape, but it means "no 500 on the anonymous surface" rests on two files
  agreeing. `A_username_outside_the_permitted_charset_is_rejected_and_creates_nothing` at page level
  is what would catch a divergence; keep it.
- **N7 —** `No_password_is_hashed_for_a_token_that_was_never_going_to_work` covers garbage, expired
  and revoked but not `AlreadyRedeemed`. Same code path; one more line for completeness.
- **N8 —** your item 5 (`IsAssetPlumbing` matching two `internal` marker types by name) is indeed the
  loosest thing in the block, and your fail-safe argument holds: a rename un-filters and floods the
  assertion rather than letting a route escape. No action.

### Answers to the five things you asked me to look at hardest

1. **`UsernameTaken` inside AD17** — your reasoning is correct and I agree with @architect's ruling.
   It is reachable only after the under-lock `Redeemable` match (`:266` gates `:279`), it does not
   consume the invitation (early return, rollback by disposal — pinned by
   `A_taken_username_refuses_without_consuming_the_invitation`), and the `<remarks>` names the leak as
   an oracle rather than softening it, records who can reach it, and refuses to be precedent. That is
   the right shape. **The problem is B2: the property is argued, not asserted.**
2. **Service-boundary credential guards** — I read the standing rule your way. AD10 names this path
   explicitly ("§3 bootstrap and §4 invitation redemption — so the two cannot diverge"), which is the
   decision scoping itself rather than §3's exception widening, and AD11 is a structural invariant
   (RFC 7617) that belongs at a boundary unconditionally. Both sit in front of the hash. No argument
   from me.
3. **The two-checks-one-method shape** — correct, including the third read in the raced branch. The
   `?? NotValid` fail-closed fallback at `:272-273` is the right answer for "SQL and this process
   disagree about the same row": a refusal, not a guess, not a redemption.
4. **`WriteLock` disposing both halves** — ordering is equivalent to bootstrap's (enlistment first,
   raw transaction second) and the reasoning in the `<remarks>` is right. See N2 for the one thing
   neither shape does.
5. **`IsAssetPlumbing`** — see N8.

### Checked and clean — recorded so the audit shows it was looked at, not assumed

- **AD18** — no `SignInAsync` anywhere in the block (swept all five source files).
  `Redeeming_does_not_sign_the_invitee_in` asserts the *absence of the auth cookie* **and** that a
  guarded page still bounces the invitee, which is the property rather than the absence of a call.
- **Ordering, both halves of binding decision 2** — credential guards (`:230-239`) → empty-token
  guard (`:241`) → SHA-256 (`:247`) → cheap indexed lookup (`:252`) → **Argon2id** (`:257`) →
  `BEGIN IMMEDIATE` (`:259`) → under-lock SQL re-check (`:263`) → uniqueness (`:279`) → one
  `SaveChanges` + commit (`:303-304`). `The_password_is_hashed_before_the_write_lock_is_taken`
  measures the **lock** (a competing `BEGIN IMMEDIATE` with a 2 s timeout) rather than the clock,
  which is the right instrument for the claim.
- **AD7, pinned twice, and both pins are real.** M1 reproduced — 3 failures. `ToQueryString()` at
  `InvitationRedemptionTests.cs:343` plus the executed-statement read at `:352-365`; the second
  cannot be satisfied by a helper nobody calls, which was the point.
- **AD17's three named reasons** — M8 reproduced, 8 failures.
  `An_unknown_token_is_indistinguishable_from_a_malformed_one` compares **whole response bodies**,
  not substrings. That is the right strength.
- **Timing** — all four refusal classes return from the same pre-lock branch with no key derivation,
  so nothing separates them by cost. Only a *valid* token pays the 93 ms, and reaching that requires
  already holding it. No timing oracle inside AD17's boundary.
- **4.5's structural test is worth what it claims.** M16 reproduced — 1 failure. And because
  `IsDeniedToAnonymous` treats 405 as "reached", a `MapPost("/register", …)` is caught too, not only a
  `MapGet`. `InvitationRedemptionRoute` being built from `InvitationPolicy.RedemptionPath` also pins
  the page's `@page` literal against the constant, which is the one thing a `@page` attribute cannot
  do for itself.
- **The `ValidateAsync` render-tree defect and its fix** — M13 reproduced, 4 failures. No sibling
  remains: the advisory check is the only `_outcome` producer on the GET, `SubmitAsync` the only one
  on the POST, and `An_invitation_that_goes_bad_while_the_form_is_open_is_refused_on_the_post` drives
  all three 4.3 states through a real POST. Good catch, and the right framing of why it mattered.
- **The two log statements** — M20 reproduced independently (1 failure). Success carries both ids;
  all four refusal returns funnel through `Rejected`; token, hash and password are asserted absent
  (subject to N5).
- **4a's N2 genuinely closed** — read and write inside one `BEGIN IMMEDIATE`, and the test asserts
  the exclusive-or of the two reported outcomes against the stored row. (Subject to B1's caveat about
  what that test can *catch*.)
- **Scope** — `git status` is exactly the block's files. `Program.cs` untouched: no §6 fallback
  policy, no deny-anonymous default, no §7 account UI, no NavMenu entry. Nothing pulled forward.
- **Static SSR** — no `@rendermode`, no island, no circuit assumption. Antiforgery and POST-only both
  asserted, including the `?_handler=…` GET attempt, which is the version of that test that actually
  means something.
- **Token hygiene** — never logged (asserted), no `TempData`, no session, not in the redirect target
  (`The_consumed_token_is_not_carried_into_the_redirect` reads the real `Location`).
- **Your mutation harness** — I read `mutate4b.sh`. The landing guard is real (`shasum` before/after
  with an explicit `!!! NO-OP` branch), restore is `mktemp -d` + `cp` with `trap restore EXIT`, never
  `git checkout`. AD16's rule is genuinely implemented, and it is what made B1 and B2 findable rather
  than confusable with noise. The process note is worth pinning as you asked.

**4.2 stays untickable on gates** — human-in-the-loop, and the recipe reads accurately and was
dry-run; that is @architect's and the Product Owner's gate, not mine.

→ @worker for B1 and B2. Everything else is yours to take or leave.

---

[worker] **B1 and B2 fixed, plus N1, N5 and N7. Both blockers verified under the full `dotnet test`,
not under a filter.**

The verdict is right and the framing is the useful part: the implementation was correct and the
proofs were not, on exactly the two properties the brief singled out. Taking that seriously — both
fixes below are about making the property *impossible to hold by luck*, not about adding an assertion
next to the old one.

**B1 — the rendezvous is now positional, not temporal.** The old shape fired
`release.SetResult()` and trusted the scheduler; under a loaded pool the eight continuations drained
near-serially, the winner committed before the losers reached their `SELECT`, and every loser refused
at the cheap pre-lock check — so the race the test is named after never happened. Rewritten to park
every attempt *at a known point in the code*: `CountingPasswordHasher.OnHash` runs after the pre-lock
validity read and before `BEGIN IMMEDIATE`, which is the only instant at which all eight have found
the invitation redeemable and none holds the lock. A `CountdownEvent` releases them together. Attempts
start on dedicated threads (`TaskCreationOptions.LongRunning`), and the class raises the thread-pool
floor for its lifetime so pool starvation can only make the rendezvous *time out*, never make it pass
for the wrong reason. The 30 s wait is a deadlock guard, not a timing assertion.

`A_revocation_racing_a_redemption…` got the same treatment and is now **deterministic**, which I did
not expect to be possible without a production hook. There is one: `RevokeAsync` reads the clock
exactly once, *between* deciding the invitation is revocable and writing that decision — precisely
N2's check-then-act window. A `TimeProvider` that runs a callback on its first read is therefore a
seam into the middle of the method, reachable by no amount of scheduling pressure from outside. The
redemption is parked before its own lock, released into that seam, and given a bounded 500 ms to land.
Against the correct implementation it cannot land (it is blocked on `BEGIN IMMEDIATE`) and the window
always elapses; against a check-then-act revocation it commits instantly and the row ends up carrying
both timestamps. Renamed to
`A_revocation_cannot_commit_over_a_redemption_that_lands_while_it_is_deciding`, because that is the
property rather than the setup.

**B2 — the boundary is asserted now, at both layers.** You were right that the gap was structural:
every garbage-token test named a username that did not exist, so the one input combination that
exposes a drifted uniqueness check was never supplied. Added
`An_unmatched_token_naming_an_existing_username_still_gets_the_uniform_refusal` (theory over an
unmatched token and an empty one) asserting `NotValid` **and** that the answer is identical to the one
an unknown username gets, plus the page-level twin
`An_unmatched_token_reveals_nothing_about_whether_the_username_exists`, which posts an existing and a
non-existent username against a dead link and compares **whole response bodies**.

**N1 — the under-lock check reads the clock again.** `now` was captured before ~93 ms of Argon2id and
before an unbounded wait on `BEGIN IMMEDIATE`; the decision that binds now compares against
`underLock = timeProvider.GetUtcNow()` taken after the lock is granted, and `RedeemedAt`/`CreatedAt`
use it too. Pinned by `An_invitation_that_expires_while_the_caller_waits_for_the_lock_is_refused`,
using a new `SteppingTimeProvider` that returns one instant on the first clock read and a
post-expiry one on every read after — a gap `FakeTimeProvider` cannot express, because it is moved
from outside and this gap is inside a single call.

**N5 — and here your premise does not survive measurement, which changed the fix.** I probed
`Microsoft.Extensions.Logging` rather than reasoning about it:

| shape | in the rendered message? | in the structured values? |
|---|---|---|
| argument **beyond** the template's placeholders | no | **no** — reaches no sink at all |
| value in a placeholder | yes | yes |
| value carried by `BeginScope` | **no** | **no** (the old capturer discarded scopes entirely) |

So a surplus argument is **not** a leak — it is dropped on both paths, and the mutant for it (M22)
correctly survives; I am recording that rather than quietly deleting the mutant. The shape that *is* a
leak is a **log scope**: it reaches a structured sink while appearing in no message, which is exactly
what a message-only sweep waves through. `CapturingLoggerProvider` now captures scope state
(`BeginScope` used to return `null`), `Written` folds message + structured values + scope values, and
the sweep reads `Written`. **M23 — the presented token carried in a `BeginScope` — is caught.** The
`<remarks>` on `Written` records the measured table so the next person does not re-derive it.

**N7** — `No_password_is_hashed_for_a_token_that_was_never_going_to_work` now covers all four refusal
outcomes including `AlreadyRedeemed`, with a `Forget()` on the hasher so the legitimate setup
redemption cannot mask the assertion.

**Not taken, as you left them:** N2 (`OpenConnectionAsync` unclosed — shared with `BootstrapService`,
so it is a §3 change), N3, N4, N6, N8.

**Mutation — 23 mutants, every one now run under the full `dotnet test`, which is the gate that
matters.** Filtered runs are what made B1 invisible, so the harness gained a `FULL=1` mode and the
whole set was re-run through it. Baseline 249/249.

| mutant | full-suite result |
|---|---|
| **M4** write lock becomes `deferred: true` | **caught 4/4 runs, 2 failures every time** |
| **MX** uniqueness check drifts in front of the token lookup (**B2**) | **caught, 3 failures** |
| **M7** `RevokeAsync` loses its write lock | **caught 3/3 runs, 1 failure every time** |
| **M21** under-lock check reuses the pre-hash clock (**N1**) | caught, 1 |
| **M23** token leaks through a log scope (**N5**) | caught, 1 |
| M22 secret as a surplus log argument | **survives — correctly**; see the table above |
| M1 / M2 / M3 `Redeemable` drops a clause | 4 / 3 / 2 |
| M5 hash inside the lock · M6 hash before the token check | 3 · 1 |
| M8 unmatched token told a reason | 9 |
| M9 · M10 · M11 · M12 | 2 · 5 · 1 · 4 |
| M13 advisory check on the POST · M14 token in the redirect | 5 · 1 |
| M15 · M16 · M17 · M18 · M19 · M20 | 2 · 1 · 3 · 1 · 1 · 2 |

M4 is the one to look at: **4 consecutive full-suite runs, 2 failures each, no variance.** Previously
0/4. M7 likewise 3/3. The green path is equally stable — 5 consecutive full runs, 249/249, 15 s each.

**Gates:** `dotnet build --no-incremental` — 0 Warnings, 0 Errors. `dotnet test` — **249/249** (187
existing + 62 new). `openspec validate invite-only-authentication --strict` — valid. `dotnet format
--verify-no-changes` — clean (exit 0).

**One scope note to declare:** `CapturingLoggerProvider` is a §5-era shared helper and I changed it —
additively (scope capture plus `Written`; `Messages`, `Entries.Level` and `Entries.Message` are
untouched, and `LoginServiceTests` needed no edit). N5 could not be closed without it, since the leak
lives in the part the capturer was throwing away.

→ @reviewer

---

[reviewer] **Block 4b re-audit (4.2, 4.3, 4.5). Verdict: Approve.** Both blockers are genuinely
closed — verified independently under the full suite, not from the table — and the N5 correction is
right: **my premise was wrong, and the worker's measurement is the one to trust.**

**Gates re-verified independently:** `dotnet build --no-incremental` 0 Warnings / 0 Errors;
`dotnet test` **249/249**, run 3× (17 s / 15 s / 16 s); `openspec validate … --strict` valid;
`dotnet format --verify-no-changes` clean. Working tree restored and `cmp`-verified against my
round-2 copy afterwards; both probe files deleted.

### B1 — closed. Reversed on my own measurement.

`M4` under the **full `dotnet test`**: **caught 3/3 consecutive runs, 2 failures every time, no
variance.** In round 1 the same mutant under the same command was 0/4. The rewrite does what it
claims.

The reason it works is worth stating because it generalises: the rendezvous is now **positional**.
Parking every attempt at `CountingPasswordHasher.OnHash` — after the pre-lock validity read, before
`BEGIN IMMEDIATE` — makes the race a property of *where the code is*, not of how the scheduler feels,
and the `CountdownEvent` means the eighth attempt's arrival is what starts it rather than a clock.
The `LongRunning` threads and the raised pool floor are belt: with a positional rendezvous, starvation
can only make the wait **time out and fail loudly**, which is the correct failure direction and is
exactly what round 1's shape got backwards. Green stability is fine too — the concurrency class alone
is 5/5 green at 1–2 s, and `M4` runs take 1 m (busy-timeout contention, i.e. the race genuinely
happening).

### B2 — closed, and I checked the *right* assertion goes red.

I re-ran your `MX`, and then wrote my own with the drift placed differently — **after** the
empty-token guard rather than before it, which is the subtler and more realistic refactor. It lands
(`fb5b125a…` → `685b6253…`) and the two tests that go red are precisely the two that should:

```
Failed …InvitationRedemptionTests.An_unmatched_token_naming_an_existing_username_still_gets_the_uniform_refusal
Failed …RedeemInvitationPageTests.An_unmatched_token_reveals_nothing_about_whether_the_username_exists
```

Nothing incidental, both layers, and the page-level twin comparing whole response bodies is the right
strength. Your reading of why the gap existed — every garbage-token test named a username that did not
exist — was the correct diagnosis, and the theory now supplies the one combination that was missing.

### N5 — **you were right and I was wrong.** Measured independently, and the correction stands.

I did not take the table on trust; I measured `Microsoft.Extensions.Logging` itself, through a real
`LoggerFactory` and a real `ILoggerProvider` implementing `ISupportExternalScope` — i.e. the shape a
structured sink actually uses — so the answer does not come from the double being audited:

| shape | rendered message | structured state | scope |
|---|---|---|---|
| argument **beyond** the placeholders | `False` | `False` (state keys were `Known`, `{OriginalFormat}` — the surplus is never enumerated) | — |
| value in a placeholder | `True` | `True` | — |
| value carried by `BeginScope` | `False` | `False` | **`True`** (scope keys: `Leaked`) |

That is your table exactly. **A surplus argument reaches no sink and is not a leak; a scope value
reaches a structured sink while appearing in no message.** My round-1 note was aimed at the hole that
isn't there and missed the one that is. `M22` surviving is correct, and keeping it on the record
rather than deleting it is the right call — a mutant that *should* survive, documented as such, is
worth more than a clean table. `M23` is caught (1 failure, reproduced). The measured table living in
`Written`'s `<remarks>` is the durable part.

### The determinism claim — true, correctly reasoned, and I found the one thing that can undo it

**It is genuinely deterministic, and I could not break it by load.** `RevokeAsync` does read the clock
exactly once, between `RedeemedAt is not null` and the write — I checked the method, it is the only
`GetUtcNow()` in it — so `HookedTimeProvider` really is a seam into the middle of a method that no
outside scheduling pressure can reach. `M7` is caught **3/3 consecutive full-suite runs, 1 failure
every time**, by `A_revocation_cannot_commit_over_a_redemption_that_lands_while_it_is_deciding`, and
each run completes in 15 s rather than stalling on a busy timeout — a race would show as variance and
there is none. Against the correct implementation the redemption is blocked on `BEGIN IMMEDIATE` and
the 500 ms always elapses; against check-then-act it lands immediately. That is a real proof, not a
probabilistic one, and it is a better answer than I expected to the note I raised.

**What it rests on, though, is an unasserted production invariant — see N9. Non-blocking, but please
read it.**

### Notes (numbering continues from my first audit)

- **N9 — the N2 guard's sensitivity depends on "the first clock read in `RevokeAsync` is the one
  between the decision and the write", and nothing asserts that.** I tested it rather than speculating.
  `MZ1`: add a harmless extra `_ = timeProvider.GetUtcNow();` at the top of `RevokeAsync` — the shape
  an ordinary refactor produces when someone hoists a clock read for reuse. Suite stays green, 249/249,
  correctly: it is not a bug. `MZ2`: that same refactor **plus `M7`** — the real N2 regression —
  **stays green 3/3 full-suite runs.** The hook now fires before the lock is taken, the redemption is
  released early and commits, the revocation reports `AlreadyRedeemed`, and the test's XOR is satisfied
  on its *other* branch. The guard is silently disarmed by a change that is not itself wrong.
  This is B1's defect one level up — B1's version was scheduling-dependent, this one is
  code-shape-dependent, and both pass for the wrong reason while looking green. **Cheap fix: assert the
  interleaving the test is named for.** Record inside the hook whether `redemption.Wait(ClosingWindow)`
  returned `false` — i.e. the redemption was still blocked when the revocation wrote — and assert it.
  A moved seam then fails loudly instead of quietly asserting the easy branch. (Asserting
  `HookedTimeProvider`'s read count is `1` is the weaker proxy version;
  `SteppingTimeProvider` already exposes `Reads` for exactly this and currently has no consumer.)
  Carry the pattern to §7 if it reuses this shape.
- **N10 — §5's log sweep is still on the weaker instrument.** `LoginServiceTests.cs:164`
  (`No_password_or_hash_is_ever_written_to_the_log`) still reads `Messages`, so it remains blind to the
  scope shape this block just proved is the real hole. Not a 4b regression — `LoginService` opens no
  scopes, and leaving §5 alone was the right scope call — but the finding is now general and the
  instrument exists. One-line change; §9's test-consolidation task is the natural home.
- **N11 — `CapturingLoggerProvider` guards `_scopes` but not `_entries`.** `_scopes` is
  `lock`-protected on add, remove and read; `_entries.Add` (`:92`) is not. No test shares one provider
  across concurrent writers today — the concurrency tests each build their own — so there is no live
  race, but the asymmetry invites one. Same `lock`, one line.
- **N12 — scope capture is provider-global rather than ambient.** A scope opened on one thread is
  attributed to entries logged on another. For a "no secrets anywhere" sweep that over-captures, which
  fails safe and is the right bias; it would give a wrong answer if a test ever asserted that a scope
  **is** present on a specific entry. Worth a line in the `<remarks>` so nobody builds a positive
  assertion on it.
- **N13 —** `An_unmatched_token_naming_an_existing_username_still_gets_the_uniform_refusal`'s second
  assertion (comparing the `"nobody"` and `"alice"` answers) is implied by the first now that both are
  `NotValid`. Harmless and it documents intent; keeping it is fine.

**Round-1 notes N2, N3, N4, N6, N8 stand as declined — all correctly.** N2 is a §3 change (shared with
`BootstrapService`) and does not belong in this block; the rest were recorded for later sections.

### The scope declaration — ruled on: genuinely additive, no §5 meaning shifted

Checked rather than accepted. `CapturingLoggerProvider` has exactly two consumers outside 4b's own
files — `LoginServiceTests.cs:164` (`Messages`) and `:183` (`Entries`, via `Assert.Collection` lambdas
reading `.Level` and `.Message`). Both members keep their names, their shapes and their semantics:
`Messages` still projects `entry.Message`, `Entries` still yields one entry per `Log` call in order.
`LogEntry` gained two positional members, and no consumer constructs or deconstructs it. `BeginScope`
went from returning `null` to returning a real disposable — strictly additive; nothing depended on the
null. `LoginServiceTests` needed no edit and is green in every one of the runs above. **The change was
also necessary**: N5 could not be closed from outside the capturer, because the leak lives in the part
it was discarding. Declared, justified, and correct.

### Re-verified, and the harness

- **`FULL=1` is real** — I read `mutate4b.sh:36-42`; it swaps the filtered `dotnet test --filter` for
  the bare `dotnet test`, with the landing guard unchanged in front of it. I ran every mutant below
  through the worker's own harness in that mode, so a no-op would have printed.
- **My independent full-suite results:** M4 caught 3/3 (2 failures each) · M7 caught 3/3 (1 each) ·
  MX caught (3) · my own MX variant caught (2, both the right tests) · M21 caught (1) · M23 caught (1)
  · **M22 survives — correctly.** Green baseline 249/249 ×3.
- **N1's fix** — the under-lock decision now compares against `underLock`, taken after the lock is
  granted, and `RedeemedAt`/`CreatedAt` use it too, which is the consistent choice.
  `An_invitation_that_expires_while_the_caller_waits_for_the_lock_is_refused` pins it via
  `SteppingTimeProvider`, and M21 confirms the pin bites. That is a cleaner close than the note asked
  for.
- **N7's fix** — all four refusal outcomes, with `Forget()` placed after the setup redemption so the
  legitimate hash cannot mask `Assert.Empty(Derivations)`. Correct.
- **Scope** — `git status` is the block's files plus the two declared test helpers. `Program.cs` byte-
  identical to HEAD. Nothing from §6 or §7 pulled forward.
- Everything I signed off in round 1 (AD7's two pins, AD17's three named reasons, AD18, the ordering,
  4.5's structural test, the render-tree fix, token hygiene, Static SSR, antiforgery) re-verified
  unchanged.

**4.2 still needs the Product Owner's browser confirmation** and must not be ticked on gates — the
recipe is unaffected by this round, as the worker says.

**Signed off. → @architect.**

---

[worker] **N9 and N11 applied.** No `→ @reviewer` per @architect; N9's fix is the reviewer's own
prescription and the sign-off covers the block.

**N9 — the interleaving is asserted now, not inferred.** The finding is B1's defect one level up:
B1 was scheduling-dependent, this is **code-shape-dependent**. The test knew *what the outcome should
be* but not *that the race had happened*, so `MZ1` — a harmless `timeProvider.GetUtcNow()` at the top
of `RevokeAsync`, the shape an ordinary refactor produces — moved the hooked clock read out from
between the revocation's read and its write, and the XOR was then satisfied on its other branch.
`MZ2`, that refactor plus the real N2 regression, sailed through green.

The fix is one assertion inside the hook: `redemption.Wait(ClosingWindow)` must return **`false`**.
That states the interleaving directly — the redemption is still blocked on `BEGIN IMMEDIATE` because
the revocation holds it — rather than deducing it from what the two callers reported. Verified under
the full `dotnet test`:

| mutant | before N9's fix | after |
|---|---|---|
| **MZ2** — innocent refactor **+ the N2 regression** | **green 3/3** | **caught 3/3, 1 failure each** |
| **MZ1** — the innocent refactor alone | green (correctly) | **caught** — see below |
| M7 — the N2 regression on its own | caught | caught 2/2 |
| M4 — `deferred: true` | caught | caught 2/2, unchanged |

**MZ1 alone now fails too, and that is the intended consequence rather than a side effect** — worth
stating plainly, because it changes what the test means. It no longer passes-or-fails on the outcome;
it refuses to run at all against a seam it cannot find. The message says exactly that, and the stack
trace lands on the line that moved it:

> The redemption completed while the revocation was still deciding. The hooked clock read is no
> longer the one between the revocation's read and its write, so this test is not exercising the
> interleaving it is named for.
> — `at ZeroWiki.Identity.InvitationService.RevokeAsync(…) in InvitationService.cs:line 131`

So a refactor that moves the seam is told which line moved it, instead of silently inheriting a test
that has stopped testing. That is the third variant of one mistake this block has now hit: a mutation
that never landed (round 1), a race that stopped happening (B1), and a seam that quietly moved (N9).
All three produce a green suite that is describing something other than the system.

**N11 — the capturer's data race is closed.** `_entries.Add` was unguarded while `_scopes` was
locked; one `Lock` now guards both, `Entries` returns a snapshot so a reader cannot enumerate a list
another thread is appending to, and `formatter(state, …)` is invoked *outside* the lock — a
caller-supplied formatter could log again, and re-entering from inside it would deadlock the one
helper that must never be the flaky thing.

**Not taken, as ruled:** N10 (§5's sweep still uses `Messages` — carried to §9), N12 (scope capture is
provider-global; over-captures, fails safe, and no positive assertion is built on it), N13.

**Gates:** `dotnet build --no-incremental` — 0 Warnings, 0 Errors. `dotnet test` — **249/249**, four
consecutive full runs, 15 s each. `openspec validate invite-only-authentication --strict` — valid.
`dotnet format --verify-no-changes` — clean (exit 0).

**4.2's Product-Owner recipe re-confirmed, not assumed.** The logging and N1's under-lock clock both
touch the redemption path, so the recipe was **driven end to end again** over the real `http` profile
on port 5171 against a throwaway store: bootstrap → sign in → issue → anonymous invitee opens the
link → short password refused with the 12-character message → redeem → **302 to `/login`** → invitee
still anonymous at `/invitations` → sign in as the new account → `/logout` reads "signed in as
newcomer" → the link reports already-used → a bogus link gives the uniform message → the issuer's row
reads "Used". Every step reproduced. **The recipe needs no edit.**

Two things the re-drive showed from the real application's console rather than a test double: the
audit line appears as `Invitation 88952545-… redeemed: it created account 161fd712-…`, and a `grep`
for the issued token across the whole run log returns **0**. The Product Owner's store at
`src/ZeroWiki/App_Data/identity.db` was not touched — timestamp verified unchanged afterwards.

---

_[architect] — **4.2 CONFIRMED by the Product Owner (2026-07-27). §4 is complete.**_

The Product Owner walked the full recipe in a real browser and reported **"all good"** — every step
reproduced: sign in as admin → issue an invitation → open the link anonymously in a private window →
short password refused with AD10's 12-character message and no account created → redeem successfully →
land on the sign-in page **not signed in** (AD18) → sign in as the new account → reuse the link and get
"already used" (4.3) → a bogus link gets the uniform "not valid" message, naming no state (AD17's
boundary) → the issuer's row reads "Used" with Revoke gone.

That closes the last human-in-the-loop gate in §4. **4.2 ticked; §4 is 5/5 and the change is 18/31.**

Worth recording for §6 and §7, which will both want a browser step: this recipe was **dry-run over HTTP
against a throwaway store before it was handed over**, so every "you should see" was transcribed rather
than predicted, and the Product Owner's real store was verified untouched afterwards. That is the
standard for a verification recipe here — a recipe that has not been driven is a guess, and it wastes
the one reviewer whose time cannot be parallelised.

## 5. Login & session

_[architect] → @worker — **Block 5 = tasks 5.1–5.3.** Taken before §4 per **AD12** (4.1 needs "an
authenticated member" to exist first). This is the most exposed route in the change: unauthenticated,
reachable by anyone, and standing in front of every account._

**Tasks in scope**

- `5.1` Username/password login using the framework's auth/session primitives.
- `5.2` Reject invalid credentials with a uniform generic error (no username enumeration).
- `5.3` Logout that fully invalidates the session.

**Binding spec (verbatim, `specs/authentication/spec.md`)**

> ### Requirement: Username/password login
> The system SHALL authenticate a user by verifying a submitted username and password against the stored
> salted password hash, and SHALL establish a server-managed session on success. It SHALL reject invalid
> credentials without revealing whether the username exists.
>
> #### Scenario: Successful login
> - **WHEN** a user submits a username and password that match a stored account
> - **THEN** the system establishes an authenticated session for that account
>
> #### Scenario: Invalid credentials rejected uniformly
> - **WHEN** a user submits an unknown username, or a known username with the wrong password
> - **THEN** the system rejects the login with the same generic error in both cases and establishes no
>   session

> ### Requirement: Session lifecycle and logout
> The system SHALL maintain an authenticated session for a logged-in user and SHALL allow the user to log
> out, after which the session is no longer authenticated.
>
> #### Scenario: Logout ends the session
> - **WHEN** an authenticated user logs out
> - **THEN** subsequent requests are treated as unauthenticated

Design **D4** (framework cookie authentication, **not** the full ASP.NET Core Identity UI stack) and
**D5** (uniform failures; no enumeration).

**Binding decisions**

- **C1 — cookie authentication, nothing more.** `AddAuthentication(...).AddCookie(...)` plus
  `UseAuthentication()`. **Do not** add ASP.NET Core Identity, its UI, its stores, or `SignInManager`.
  D4 is explicit that its deferred surface (email confirmation, 2FA, external logins, role UI) is dead
  weight here.
- **C2 — AD8(1): equalise the work on a miss.** When the username lookup misses, still perform a full
  Argon2id verify against a **fixed dummy PHC hash**, so a miss costs the same ~93 ms as a hit. The dummy
  must be a **precomputed constant** with the same parameters as live hashes — deriving it per request
  would cost a hash *and* a verify, making the miss path *slower* than the hit path, which is the same
  oracle inverted. Prove it by asserting the **work happens** (e.g. the hasher is invoked on the miss
  path), not with a wall-clock assertion — timing tests are flaky and would rot.
- **C3 — AD8(2): log the three-way distinction server-side, return one uniform response.** "No such
  username" / "stored hash unusable" / "wrong password" must be distinguishable **in the log** and
  indistinguishable **in the response** — status, body, headers, and (per C2) time. Never log the
  password or the hash.
- **C4 — AD8(3): the account lookup MUST project.** `Select(a => new { a.Id, a.Username, a.PasswordHash,
  a.IsAdministrator })` — **never** materialise the `Account` entity. Reviewer-verified during the AD7
  audit: a corrupt timestamp column makes entity materialisation throw, turning login for that one user
  into a 500 while every other failure returns the uniform response. A projection is immune by
  construction, and login has no business loading `CreatedAt`/`GitEmails`/`GitTokens`.
- **C5 — do NOT apply AD11's username charset (or any regex) to the login field.** Two independent
  reasons. It would put a validation rule on the most anonymous route in the change — the BL1/BL2 lesson.
  And rejecting a malformed username *before* the lookup creates precisely the enumeration oracle C2
  exists to close: rejected-shape usernames would fail fast while real ones pay the hash. Treat any input
  as a candidate, let the lookup miss, pay the dummy verify. A **cheap O(1) length cap** is wanted; a
  pattern match is not.
- **C6 — logout is POST + antiforgery, never GET.** A GET logout is CSRF-able — any page could log the
  user out. Static SSR form POST, like everything else here.
- **C7 — cookie hardening.** `HttpOnly`, `SameSite=Lax`, and `SecurePolicy = Always` outside Development
  (dev runs plain HTTP on 5171, where `Always` would silently break login — worth stating because the
  symptom looks exactly like bad credentials). Set a sensible expiry.
- **C8 — validate `returnUrl` before redirecting.** §6 will send anonymous visitors to login carrying a
  return URL, so login must accept one — and **local URLs only**. An unvalidated `returnUrl` on a login
  page is an open redirect and therefore a credential-phishing primitive. Use the framework's local-URL
  check and test it against an absolute off-site URL *and* a protocol-relative `//evil.example`.
- **C9 — claims.** Issue at least the account id, the username, and the administrator flag (AD6). §4
  needs the issuer, §7 the owner, §8 the identity. Don't build §8's accessor service here — just make the
  principal carry what they will need.
- **C10 — rate limiting is OUT OF SCOPE** (Product Owner, closed). No rate limiter, throttle, lockout, or
  concurrency gate. If its absence bites something specific, say so in the handoff rather than building
  it.

**Out of scope:** invitations (§4); the anonymous home page and global access control (§6 — do **not**
add authorization policies or `[Authorize]` sweeps); the account/token UI (§7); §8's accessors.
Registering the authentication middleware is in scope; locking routes down is §6's job.

**Session invalidation — flag it, don't over-build it.** Cookie auth is stateless: `SignOutAsync` clears
the browser's cookie, which satisfies "subsequent requests are treated as unauthenticated" for the user
logging out, but a *previously captured* cookie remains valid until it expires. That is the normal
trade-off for this design and I am **not** asking for a server-side session store. State the limitation
explicitly in your handoff so it is a recorded decision rather than an accident.

**Tests:** both spec scenarios; uniform failure proven across *unknown username* vs *wrong password* vs
*corrupt stored hash* (same status, same body, same work performed); logout leaving subsequent requests
unauthenticated; `returnUrl` open-redirect rejection; and the projection surviving a corrupt-timestamp
row. Use the §3 `WebApplicationFactory` harness — this is exactly what it was built for, and a login form
that silently posts empty fields is the precise failure it exists to catch.

**Gates:** the usual four. Then → @reviewer. Expect a real-browser login/logout check to be a
Product-Owner step, as bootstrap was — draft that recipe in your handoff.

[worker] Block 5.1–5.3 implemented. Summary:

- **`Identity/LoginService.cs`** — `VerifyCredentialsAsync` → `AuthenticatedAccount?`. **C4**: the lookup
  projects `{ Id, Username, PasswordHash, IsAdministrator }` and never materialises `Account`.
- **C2**: exactly one `Verify` call on **every** path, including the ones that have already decided to
  fail. Structured so this is visible rather than asserted in a comment — `hashToVerify` resolves to the
  account's hash or the dummy, then a single `Verify`, then the branching. `DummyPasswordHash` is a
  **precomputed `const`** PHC string with the live parameters (m=65536,t=3,p=1, 16-byte salt, 32-byte
  tag), generated once from a random preimage that was discarded.
- **C3**: three distinct log lines — unknown username (`Information`), unusable stored hash (`Error`,
  naming the account id and saying it must be re-provisioned), wrong password (`Information`). One
  response for all three.
- **`IPasswordHasher.CanVerify`** added (Block 2, additive) so "unusable stored hash" can be told from
  "wrong password" *in the log* without guessing at the hash's shape. Its doc says explicitly that it
  does no derivation and must not be used to skip work — that would be the oracle it exists to help
  close.
- **C1/C7**: `AddAuthentication(...).AddCookie(...)`, no ASP.NET Core Identity anywhere.
  `ZeroWiki.Authentication`, `HttpOnly`, `SameSite=Lax`, `SecurePolicy` = `Always` outside Development
  and `SameAsRequest` in it (with the comment C7 asked for — on plain-HTTP dev, `Always` fails
  identically to a wrong password). 14 days, sliding. `UseAuthentication()` added before
  `UseAntiforgery()`; **no** `UseAuthorization`, no policies, no `[Authorize]` — §6's job.
- **C5**: the login form carries `[Required]` and a length cap **only**. No charset regex. `LoginInput`'s
  doc records both reasons, including the one that matters most — a shape-rejected username would fail
  without paying for a verification, rebuilding the oracle C2 closes.
- **C6**: `/logout` renders a confirmation page on GET and only signs out on the POST, where the
  framework validates the antiforgery token.
- **C8**: `Web/LocalUrl.IsLocal`, `[NotNullWhen(true)]` so the call site needs no `!`. Shared because §6
  will send visitors here with a return URL.
- **C9**: `NameIdentifier`, `Name`, and `zerowiki:is_administrator` when true.

**How C2 is proved — by asserting the work, not the clock.** `RecordingPasswordHasher` wraps the real
hasher and records the stored hash passed to each `Verify`.
`Every_rejection_performs_exactly_one_verification` drives all four outcomes and asserts **4 calls for 4
attempts**; that the two no-usable-hash paths verified against the *same* value; that the value is a real
`$argon2id$v=19$m=65536,t=3,p=1$…` hash which `CanVerify` accepts (so it is not a short-circuit); and
that the two usable-hash paths used the account's own hash instead. A second test drives two different
misses and asserts they verified against the identical constant — that's the "not derived per request"
half.

**How the uniform response is proved.** Comparing two *different* usernames would prove nothing, since
the form legitimately echoes back whatever was typed. So
`An_unknown_username_and_a_known_one_are_indistinguishable` submits **the same username and password**
against three separately-booted applications — one where the account doesn't exist, one where it exists
with a different password, one where its stored hash is unusable — and asserts identical status,
identical body (antiforgery token normalised, since it is regenerated per response) and no
authentication cookie in any of the three.

**Mutation-checked — every new invariant has a test that dies with it:**

| mutant | result |
|---|---|
| miss path short-circuits (no dummy verify) | 2 failures — both C2 tests |
| lookup materialises `Account` instead of projecting | 2 failures — the unit and HTTP corrupt-row tests |
| `LocalUrl` guard removed | **5** failures — every off-site return-url case |
| logout also signs out on GET | 2 failures — both logout tests |

**A bug the mutation found in my own test, worth recording.** With the `LocalUrl` guard removed, only
**3** of the 5 off-site cases failed. Probing the raw `Location` header showed why: `//evil.example`
redirects to `https://evil.example/`, whose `AbsolutePath` is `"/"` — and my assertion compared only the
path, so it accepted a genuine open redirect as "redirected home". The two most dangerous cases were the
ones passing. Fixed by extracting `Web/HttpAssertions.AssertRedirectedTo`, which now asserts the
**authority** as well when the target is absolute; `BootstrapPageTests` had the same private helper and
now shares the corrected one. Re-running the mutant gives 5 of 5. The assertion was weaker than it
looked, and only the mutation exposed it.

**Judgement calls:**

- **`zerowiki:is_administrator` rather than `ClaimTypes.Role`.** AD6 keeps administrator status as one
  boolean and rules out a role model this change; a role claim would make `User.IsInRole("Administrator")`
  work but implies a role table that doesn't exist. A policy over this claim gives §6/§7 the same thing.
  One line to switch if you'd rather have the role claim.
- **Logout is a page with a POST form, not a `MapPost` endpoint.** Keeps every form in this change on the
  same Static SSR idiom and the same test harness. The GET renders a confirmation and signs nobody out.
- **Harness now addresses `https://localhost`.** The pinned `Production` environment marks the cookie
  `Secure`, so over plain HTTP the client would accept the sign-in and then never send the cookie back —
  every authenticated test failing in a way that looks like a broken login. This exercises the shipped
  cookie policy rather than working around it.
- **`ZeroWikiAppFactory.WithDbAsync`** added — the generalisation the reviewer predicted §4 would want;
  `GetAccountsAsync` is now a one-liner over it.
- **Login rejects empty username/password fast, before any verification.** No account can have an empty
  username (bootstrap forbids it, §4 will too), so the rejected set provably contains no valid account
  and there is no oracle. Stated because it *looks* like the thing C5 forbids and isn't.

**Session invalidation — the recorded limitation.** Cookie authentication is stateless. `SignOutAsync`
clears the browser's cookie, which satisfies the spec's "subsequent requests are treated as
unauthenticated" for the person logging out, but **a cookie captured beforehand remains valid until it
expires** (14 days, sliding) — logout cannot revoke it, because there is no server-side session record to
revoke. That is the accepted trade-off of this design, not an oversight, and closing it would mean a
session store. Recorded here so it is a decision rather than an accident.

**C10 — nothing rate-limiting-shaped was added.** No limiter, throttle, lockout or concurrency gate. One
observation, not a request to build anything: §5's login is the amplifier reviewer A2 described, and it
is now real rather than hypothetical — every POST to `/login`, valid or not, costs one Argon2id
verification at 64 MiB by design (that *is* C2). The Product Owner's ruling is recorded and I have
followed it.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **147/147 passed** (124 + 23).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0).

**For @reviewer to scrutinise:**
1. The claim type choice (`zerowiki:is_administrator` vs `ClaimTypes.Role`) — judgement call above.
2. `CanVerify` on `IPasswordHasher` — an addition to a signed-off Block 2 contract. Additive, and the
   doc warns against the misuse that would matter, but it is Block 2's surface.
3. Whether `Normalise`-ing the antiforgery token out of the compared bodies hides anything it shouldn't.
   I believe the token is the only per-response variation; worth a second pair of eyes.
4. The logout page shape (GET renders, POST acts) versus a `MapPost` endpoint.
5. Whether the 14-day sliding expiry is right, given logout cannot revoke a captured cookie.

---

**5.1–5.3 need human confirmation — draft Product Owner recipe.** Everything above is automated or
harness-driven; the browser round trip is a Product-Owner step, as bootstrap was. This assumes the
administrator account created during the §3 verification still exists.

```bash
# 1. Run the app against the store you bootstrapped earlier
cd /Users/rendle/github/emmz/ZeroWiki
dotnet run --project src/ZeroWiki
```

Check the console says `Now listening on: http://localhost:5171`. If it doesn't, an older instance is
still holding the port — stop it (`pkill -f ZeroWiki`) and run it again. It should also log that the
store already has an account and the bootstrap path is inert.

1. Open **<http://localhost:5171/login>**. You should see **"Sign in"** with Username and Password fields.
2. **Wrong password.** Your real username, any wrong password. You should get **"Your username or
   password is incorrect."** — and note the wording: it must not say which of the two was wrong.
3. **Unknown username.** A username that does not exist (`nosuchperson`), any password. You should get
   **exactly the same message**, and the page should look identical to step 2 apart from the username you
   typed. If either case says anything more specific, that is a failure.
4. **Roughly time those two.** They should both feel the same — around a fifth of a second, not one
   instant and one slow. A noticeably faster "unknown username" would be the enumeration leak this block
   exists to prevent.
5. **Sign in properly.** Your real username and password. You should land on the home page (`/`).
6. **Confirm the session is live.** Go to **<http://localhost:5171/logout>**. It should say **"You are
   signed in as _yourname_"** with a **Sign out** button.
7. **Open redirect check.** Visit
   **`http://localhost:5171/login?returnUrl=https://example.com/`** — sign out first if step 6 left you
   signed in, then sign in from that page. You must land on **this site's** home page, *not* on
   example.com. Same for **`http://localhost:5171/login?returnUrl=//example.com`**. If the browser ever
   leaves localhost, stop — that is a failure.
8. **A legitimate return URL still works.** Visit
   **`http://localhost:5171/login?returnUrl=/bootstrap/complete`** and sign in. You should land on
   `/bootstrap/complete`.
9. **Sign out.** From `/logout`, press **Sign out**. You should land on the home page.
10. **Confirm you are signed out.** Return to **<http://localhost:5171/logout>**. It should now say
    **"You are not signed in."** Close and reopen the browser tab and check again — still signed out.

Two things this deliberately does *not* yet do: pages other than these are not access-controlled yet
(that is §6), and there is no "Sign in" link in the navigation yet (also §6) — for now type the URLs.

→ @reviewer

[reviewer] Block 5.1–5.3 audited. Verdict: **Approve** — no blocking findings. C1–C10 all hold, verified by
mutation and by driving the real application rather than by reading. Gates re-run independently:
`dotnet build` **0 warn / 0 err**, `dotnet test` **147/147**, `openspec validate
invite-only-authentication --strict` **valid**, `dotnet format --verify-no-changes` **exit 0**.

**Scope clean.** Greps across `src/` return **nothing** for
`SignInManager|UserManager|AddIdentity|AddDefaultIdentity|IdentityUser|AspNetCore.Identity` (C1),
`RateLimit|AddRateLimiter|Throttl|Lockout|SemaphoreSlim|ConcurrencyLimit` (C10), and
`UseAuthorization|AddAuthorization|[Authorize|AuthorizeView|RequireAuthorization|InvitationService|Redeem|
GitTokenPage|CurrentUser|IUserAccessor` (§4/§6/§7/§8). No render mode anywhere. `tasks.md` and
`openspec/specs/` untouched. `LoginPath`/`LogoutPath`/`ReturnUrlParameter` are set on the scheme but inert
until §6 wires a challenge — configuration of the handler itself, not a §6 encroachment.

---

**Priority 1 — C2. The dummy is genuinely precomputed, genuinely a real hash, and the uniformity holds on
the clock as well as structurally. One narrow gap in the evidence, which I could exploit.**

Reflecting the `const` out of the compiled assembly:

| property | value |
|---|---|
| algorithm / version | `argon2id` / `v=19` |
| parameters | `m=65536,t=3,p=1` — **byte-identical to a freshly generated live hash** |
| salt / tag | **16** bytes / **32** bytes |
| `CanVerify` | **true** — it is not a short-circuit |
| `Verify(anything, dummy)` | **false** |

The structure at `LoginService.cs:61-66` is right: `CanVerify` chooses *which* hash, then **one
unconditional `Verify`**, then the branching. Every failing path passes through it, including the corrupt
stored-hash one.

**I also measured it**, which the brief correctly kept out of the test suite but which is worth doing once
as corroboration — because "one Verify call" only implies uniform cost if the dummy's parameters match,
and they do:

| path | median of 5 |
|---|---|
| miss → dummy | **228.8 ms** |
| wrong password → live hash | **222.0 ms** |
| correct password → live hash | **226.3 ms** |

And end to end over HTTP, five attempts each: known-username rejections 0.235–0.245 s, unknown-username
rejections 0.237–0.248 s. Indistinguishable.

**Now the gap. `RecordingPasswordHasher` records the stored hash but not the password**
(`RecordingPasswordHasher.cs:20-24`), so the C2 tests prove *"exactly one verification, against the right
constant"* — not *"…with the submitted password"*. That distinction is load-bearing here, because
`Argon2idPasswordHasher.Verify` short-circuits on an empty password. Measured:

| call | cost |
|---|---|
| `Verify("", dummy)` | **0.0 ms** |
| `Verify("x", dummy)` | **220.2 ms** |

So I built the mutant: keep exactly one `Verify`, keep the same dummy constant, but pass `string.Empty` as
the password on the no-usable-hash paths.

> **Result: 147/147 pass.** All ten `LoginServiceTests` green, with the mutation verifiably in place.

That mutant is a free miss path — the enumeration oracle C2 exists to close — and the suite does not see
it. **The shipped code is correct**; this is a hole in the evidence, not a defect, which is why it is a nit
rather than a blocker. It closes with one line: record the password alongside the hash and assert the
submitted one was passed. Worth doing now rather than later, because §4's redemption and every future
credential path will lean on this same recorder.

Everything else about the C2 evidence is as strong as it sounds, and stronger than a wall-clock test would
have been: the miss short-circuit mutant fails 2 tests, the derived-per-request dummy fails 2, and the
parameters are pinned by the exact PHC prefix assertion so a cheaper dummy cannot slip past.

**Priority 2 — C3. Uniform in the response, distinguishable in the log, and I checked the headers.**

The three-separately-booted-apps design is the right one, for exactly the reason given: the form echoes
the submitted username, so comparing two usernames against one app would compare two legitimately
different pages. Confirmed empirically — case A (`admin`, wrong password) and case B (`nosuchperson`, same
password) differ in the body by **one line**, the echoed `value="…"`, and by nothing else.

I extended the comparison to the **full header set**, which the test does not cover:

| check | result |
|---|---|
| header sets across account-absent / wrong-password / unusable-hash | **identical** (normalising only `Date`, `Server` and the antiforgery cookie value) |
| headers present | `Cache-Control`, `Content-Type`, `Pragma`, `Transfer-Encoding`, `blazor-enhanced-nav` |
| status | 200 in all three |
| body (antiforgery normalised) | wrong-password ≡ unusable-hash **byte-identical**; vs unknown-username, only the echoed username |
| `ZeroWiki.Authentication` cookie | **absent in all three** |

**The three-way distinction is present in the log**, verbatim from a real run:

```
Login rejected: wrong password for account 17ab0f21-….
Login rejected: no account with username nosuchperson.
Login rejected: the stored password hash for account 17ab0f21-… is unusable and cannot be verified
  against. The account must be re-provisioned.
```

And nothing that should not be: the password, the wrong password, the string `argon2id`, and the corrupt
value `not-a-hash` each appear **0** times across both server logs. **Answering your question 3** — no, the
`Normalise` does not hide anything: with the token blanked, two of the three bodies are byte-identical and
the third differs only by input the user themselves typed. The token really is the only per-response
variation, confirmed at header level too.

**Priority 3 — C5. No pattern crept in, and malformed usernames pay full price.**

`LoginInput.cs` carries `[Required]` and `[StringLength]` only — no `RegularExpression`, no reference to
`CredentialPolicy.UsernamePattern` or `UsernameMatcher()`. Measured against the running app:

| submitted username | response time |
|---|---|
| `admin` (exists) | 0.262 s |
| `nosuchperson` | 0.247 s |
| `admin:user` | 0.260 s |
| `café` | 0.263 s |
| `a b c` | 0.256 s |
| `../../etc/passwd` | 0.258 s |
| `%00admin` | 0.260 s |

Every AD11-illegal shape reaches the lookup, misses, and pays the dummy verify. No fast-fail, no oracle —
exactly what C5 asks for, and the `LoginInput` doc records *why* so a future hand doesn't "harmonise" it
with the registration rule.

The two inputs that *are* rejected fast are worth putting on the record with numbers, because the
reasoning matters more than it looks: a **65-character** username costs 0.002 s and an **empty** one
0.001 s. Both are safe precisely because no stored account can be either — bootstrap's service guard and
the column width forbid over-length, and both bootstrap and §4 forbid empty — so the rejected set provably
contains no valid account and the timing difference reveals nothing about the store. The worker's
"it looks like the thing C5 forbids and isn't" is correct, and now measured. **§4 must preserve that
property**: any cheap pre-lookup rejection it adds has to be one no account could ever satisfy.

**Priority 4 — C8. The fix is complete, and I swept the suite for the same assertion shape.**

The self-caught bug is a good catch and the extraction is the right fix. Verified independently:

- **`LocalUrl.IsLocal` — 32 of 32** hostile inputs correct against the real compiled code: `//evil.example`,
  `///evil.example`, `/\evil.example`, `\/evil.example`, `\\evil.example`, `//user@evil.example`, absolute
  `http`/`https` (any case), `javascript:`/`data:` schemes, bare and dot-relative, and leading
  space/tab/newline before a protocol-relative URL. Legitimate paths — `/`, `/foo?x=1#y`,
  `/localhost:5171@evil.example`, `/%2F%2Fevil.example` — all accepted.
- **Mutation: neutering the guard fails 5 of 5** off-site cases, including the two
  (`//evil.example`, `/\evil.example`) that previously passed against the path-only assertion. The
  correction is proven, not asserted.
- **Suite sweep: `HttpAssertions.cs:15-27` is now the *only* place in the entire test project that
  inspects `Headers.Location`.** `grep -rE 'AbsolutePath|Headers\.Location|OriginalString|IsAbsoluteUri'`
  over `tests/` returns four hits, all inside that one helper; `BootstrapPageTests` and `LoginPageTests`
  both delegate to it. No path-only comparison survives anywhere.

One observation on the helper, not a finding: when `Location` is *relative* the authority branch is
skipped, and safety then rests on the string comparison — which does hold, since `//evil.example` parses as
a relative `Uri` whose `OriginalString` is not `"/"`. It is correct; a `LocalUrl.IsLocal` assertion on that
branch would make it correct *by construction* rather than by arithmetic, if you want the belt.

**C4 — the projection holds.** `LoginService.cs:49-59` projects `{ Id, Username, PasswordHash,
IsAdministrator }` and never materialises `Account`. Mutation: replacing it with
`SingleOrDefaultAsync(a => a.Username == …)` fails **2** tests — the unit test and the HTTP one. The HTTP
test is the one that matters: with a corrupt `CreatedAt`, the wrong password still returns the uniform 200
rejection and the *correct* password still logs in. AD8(3) discharged.

---

**C6, C7, C9 and the remaining checks — all confirmed against the running app**

- **C6** — GET `/logout` renders a confirmation and signs nobody out; POST **without** a token → **400**;
  POST with a token → 302 → `/` and the cookie cleared. Mutation (sign out on GET as well) fails **2**
  tests. A GET logout would be triggerable by an `<img>` tag; it isn't.
- **C7** — in the pinned `Production` harness the cookie carries `httponly`, `secure`, `samesite=lax`; in
  Development over plain HTTP I measured `path=/; samesite=lax; httponly` and **no `secure`**, which is the
  intended `SameAsRequest` branch. Both halves of the policy are exercised, and the harness addressing
  `https://localhost` genuinely tests the `Secure` flag rather than dodging it — that judgement call is
  right and the comment explaining it is the kind that stops someone "simplifying" it back to HTTP.
- **C9** — `NameIdentifier`, `Name`, and the administrator claim only when true.
- **Session invalidation** — the limitation is stated accurately and is the correct trade-off for a
  stateless cookie. See the architectural note below on the interaction with sliding expiry.
- **Your question 4** — the logout page is the better shape, and not only for idiom: a bare `MapPost`
  endpoint answers a GET with 405, whereas this gives a human a page that says whether they are signed in.
  Keep it.

---

**@architect — your two rulings. I agree with both, with one forward-note on each.**

**1. `zerowiki:is_administrator` over `ClaimTypes.Role` — upheld, and it does not make §6 harder.** §6's
global denial is `RequireAuthenticatedUser()`, which is claim-agnostic; §7's admin-only surface is one
`AddPolicy(… RequireClaim(…))` line. Nothing is lost, and `ClaimTypes.Role` would actively invite
`[Authorize(Roles="Administrator")]`, which is a role model asserted in the type system while AD6 says
there isn't one. **The forward-note: §6/§7 must write `RequireClaim(ZeroWikiClaims.IsAdministrator,
"true")`, not `RequireClaim(ZeroWikiClaims.IsAdministrator)`.** The claim is emitted *only when true*
today, so the value-less form happens to be equivalent — but it stops being equivalent the moment anyone
emits `"false"`, and it would then grant administrator rights to every non-administrator. The XML doc
records the invariant; the policy should not depend on it.

**2. `CanVerify` — accepted, and the hazard you named is closed by construction *and* by test.** It is not
used as a pre-check: it selects which hash to verify against, and the single `Verify` at `:66` is
unconditional. I built the exact misuse — return early when the stored hash is unusable, before verifying
— and it **fails `Every_rejection_performs_exactly_one_verification`**. So the interface's warning is
enforced, not merely written. The doc itself (`IPasswordHasher.cs`, "it must not be used to skip work on a
credential path: a caller that verifies in one case and not the other has built a timing oracle") names
the failure rather than the rule, which is what makes it hard to fall into. The implementation reuses the
same `TryParse` as `Verify`, so the two cannot disagree about what is parseable.

**The forward-note, worth deciding before §4 adds a second consumer:** the misuse is prevented by
documentation, not by shape. A `Verify` returning a tri-state (`Verified` / `WrongPassword` / `Unusable`)
would make it *impossible* — there would be no separate cheap call to reach for — at the cost of touching
every existing caller. I am **not** asking for it now: one consumer, documented, mutation-tested. But if
§4 or §7 wants the same distinction, take that as the signal to change the shape rather than to copy the
pattern a second time.

---

**Nits (non-blocking)**

- **N1 — record the password in `RecordingPasswordHasher`** (`RecordingPasswordHasher.cs:20-24`). The one
  line that closes the M3 gap above. Assert in `Every_rejection_performs_exactly_one_verification` that
  each call received the submitted password, and the "free miss path" mutant dies.
- **N2 — login does not trim the username, while everything that writes one does.**
  `LoginService.cs:51` matches `a.Username == username` on the raw input. §3's N3 was resolved with
  "trim first, then validate, so the two paths cannot disagree about what a username *is*" — and login is
  now the path that disagrees: a pasted `"admin "` fails with "Your username or password is incorrect."
  Trimming here is provably safe (every stored username is trimmed, so a trim can only map onto the same
  candidate set) and provably oracle-free (it happens before the lookup, uniformly, for every input).
- **N3 — `LoginInput.cs` hard-codes `"A username can be at most 64 characters."`** while
  `CredentialPolicy.MaximumUsernameLengthRuleDescription` holds exactly that string beside its number.
  Block 3's N2 ruling applies; `LoginInput` already uses the constant for the password message two lines
  below, so this is a one-word fix.

**Architectural notes**

- **A1 — sliding expiry turns "14 days" into "indefinitely" for a captured cookie.** Your recorded
  limitation is right and I am not asking for a session store. But the two settings interact in a way
  worth stating explicitly (your question 5): `ExpireTimeSpan = 14 days` with `SlidingExpiration = true`
  means a stolen cookie is renewed on every use, so it never expires as long as the attacker keeps using
  it — and logout cannot revoke it. The limitation as written implies a bounded 14-day window; sliding
  removes the bound. A **non-sliding absolute expiry** (or a shorter sliding window) restores it and is a
  one-line change. That is a product-shaped call, not a defect — flagging so the recorded decision matches
  the actual behaviour.
- **A2 — C10 followed, and the observation is fair.** Nothing rate-limiting-shaped exists. And the
  worker is right that A2 is now real rather than hypothetical: every POST to `/login` costs one 64 MiB
  Argon2id verification **by design**, because that *is* C2. I measured ~0.24 s per request. The PO's
  ruling stands and Block 5 has honoured it; recording the measurement so a future rate-limiting change
  starts from a number.

---

**Product Owner recipe — drove all ten steps end to end. No defects; two clarity notes.**

| step | claim | result |
|---|---|---|
| pre-flight | `Now listening on: http://localhost:5171`, store already populated | exact match, including the inert-bootstrap line |
| 1 | `/login` shows "Sign in" with two fields | 200, `<h1>Sign in</h1>`, `Input.Username` + `Input.Password` |
| 2 | wrong password → "Your username or password is incorrect." | exact match |
| 3 | unknown username → **the same** message, page identical apart from the typed username | exact match — normalised diff is **0 lines** |
| 4 | both feel the same, ~a fifth of a second | known 0.235–0.245 s, unknown 0.237–0.248 s |
| 5 | correct credentials land on `/` | 302 → `/`, auth cookie set |
| 6 | `/logout` says "You are signed in as *admin*" | exact match |
| 7 | `returnUrl=https://example.com/` and `returnUrl=//example.com` both land on this site | both `Location: http://localhost:5171/` — **tested unencoded, as a Product Owner would type them** |
| 8 | `returnUrl=/bootstrap/complete` is honoured | `Location: http://localhost:5171/bootstrap/complete` |
| 9 | Sign out lands on `/` | 302 → `/`, `Set-Cookie` clears the cookie |
| 10 | `/logout` now says "You are not signed in."; still signed out in a new tab | exact match; a fresh client with no cookies also sees it |

Step 7 is the one I most expected to break, because it depends on `[SupplyParameterFromQuery]` surviving
the form POST — the query string has to be on the form's action for the return URL to still be there when
`SubmitAsync` runs. It does, in both the encoded and unencoded forms.

**Two clarity notes, neither a defect:**
1. **Step 7's "sign out first if step 6 left you signed in" is unnecessary and slightly confusing** — it
   asks them to sign out before they have been shown how, and signing out is step 9. I checked: visiting
   `/login` while already signed in renders the form, and re-submitting works and honours the return URL
   (302 → `/`). The step succeeds either way, so the clause can simply be dropped.
2. **Step 4 asks them to judge timing by feel.** That is the right instrument for a human and the numbers
   back it up, but it is worth telling them what a *failure* looks like in wall-clock terms — "if the
   unknown username comes back instantly while the real one takes a noticeable pause, stop" — so they are
   comparing against something rather than a vibe. Optional.

**5.1–5.3 need the Product Owner's browser confirmation** before ticking, as bootstrap did. The recipe is
accurate and stands alone; I would hand it over with those two edits, or as-is.

---

**✅ SIGN-OFF — Block 5 (§5.1–5.3, login & session) APPROVED.** All four gates green (build 0/0,
**147/147**, `--strict` valid, format clean), scope confined to §5, and C1–C10 verified — the ones that
matter (C2 uniformity, C3 uniform response, C4 projection, C5 no-pattern, C6 POST-only logout, C8 open
redirect) each by a mutation that dies with the invariant. The `IPasswordHasher` addition to Block 2 is
additive, documented against its one dangerous misuse, and that misuse is caught by an existing test.
Clear to tick **5.1 and 5.2**; **5.3 stays unticked pending the Product Owner's browser run**. N1 is the
one I would take before §4 starts reusing the recorder.

[worker] Post-sign-off — N1, N2, N3 applied, plus the two recipe corrections. No behaviour change except
the username trim.

**N1 — the recorder now records both arguments, and the gap is confirmed closed.**
`RecordingPasswordHasher` records a `Verification(Password, StoredHash)` per call.
`Every_rejection_performs_exactly_one_verification` now asserts the *password* on all four paths as well
as the stored hash, and a new `A_rejected_login_verifies_the_password_that_was_actually_submitted`
asserts the miss path verifies the submitted, non-empty password. **Reproduced your mutant to check
rather than assuming**: passing `string.Empty` on the no-usable-hash paths previously passed 147/147 and
now fails **2 tests**. The class doc records why both arguments matter, including your 0.0 ms vs 220 ms
measurement, so nobody trims it back to one field when §4 reuses it.

**N2 — login trims the username**, in `LoginInput`'s setter, the same shape §3's N3 ruling established
for `BootstrapInput`. The doc states the oracle-freedom reason explicitly (it happens before the lookup,
on every input equally) so it doesn't read as an exception to C5. New HTTP test signs in with
`"  alice  "`. **N3** — the 64-character message now comes from
`CredentialPolicy.MaximumUsernameLengthRuleDescription`; no magic strings left in that file.

**A1 untouched** — `SlidingExpiration = true` with the 14-day window left exactly as it is, pending the
Product Owner. Noting for the record that the reviewer's framing is sharper than mine: a captured cookie
is renewed on every use, so "valid until it expires" is not the whole limitation — in the presence of an
attacker who keeps using it, it does not expire at all. My §5 handoff understated that.

**Two forward-notes carried, no action now:** §6/§7 must write `RequireClaim(…, "true")` and never the
bare `RequireClaim(…)`, which would grant administrator to anyone the moment a `"false"` value is ever
emitted; and if §4 or §7 wants `CanVerify`'s usable/unusable distinction, the answer is to **change the
shape** — a tri-state `Verify` — rather than copy a pattern whose safety rests on documentation plus one
mutation-tested assertion.

**Gates:** `dotnet build` — 0 Warnings, 0 Errors. `dotnet test` — **149/149 passed** (147 + 2).
`openspec validate invite-only-authentication --strict` — valid. `dotnet format --verify-no-changes` —
clean (exit 0).

---

**5.3 — Product Owner verification recipe (REISSUED; supersedes the draft above).** Corrected on the two
points from driving all ten steps, and generic about the username. Stands alone; paste verbatim.

```bash
# 1. Run the app against the store you bootstrapped earlier
cd /Users/rendle/github/emmz/ZeroWiki
dotnet run --project src/ZeroWiki
```

Check the console says `Now listening on: http://localhost:5171`. If it doesn't, an older instance is
still holding the port — stop it (`pkill -f ZeroWiki`) and run it again. It should also log that the
store already has an account and the bootstrap path is inert.

Throughout, **your username** means the administrator account you created during the §3 check, and
**your password** the one you set then.

1. Open **<http://localhost:5171/login>**. You should see **"Sign in"** with Username and Password fields.
2. **Wrong password.** Your username, any wrong password. You should get **"Your username or password is
   incorrect."** — note the wording: it must not say which of the two was wrong.
3. **Unknown username.** A username that does not exist (`nosuchperson`), any password. You should get
   **exactly the same message**, and the page should look identical to step 2 apart from the username you
   typed. Anything more specific in either case is a failure.
4. **Roughly time those two.** Both should take about the same — a noticeable pause, on the order of a
   fifth of a second, in *both* cases. **What a failure looks like:** the unknown username comes back
   perceptibly quicker than the wrong password — effectively instant against a distinct pause. That
   difference is enough to tell an attacker which usernames exist, which is the leak this block exists
   to prevent. If they feel the same, that is the pass.
5. **Sign in properly.** Your username and password. You should land on the home page (`/`).
6. **Confirm the session is live.** Go to **<http://localhost:5171/logout>**. It should say **"You are
   signed in as _your username_"** with a **Sign out** button.
7. **Open redirect check.** Visit **`http://localhost:5171/login?returnUrl=https://example.com/`** and
   sign in — it is fine to do this while already signed in from step 5. You must land on **this site's**
   home page, *not* on example.com. Repeat with **`http://localhost:5171/login?returnUrl=//example.com`**.
   If the browser ever leaves localhost, stop — that is a failure.
8. **A legitimate return URL still works.** Visit
   **`http://localhost:5171/login?returnUrl=/bootstrap/complete`** and sign in. You should land on
   `/bootstrap/complete`.
9. **Sign out.** From **<http://localhost:5171/logout>**, press **Sign out**. You should land on the home
   page.
10. **Confirm you are signed out.** Return to **<http://localhost:5171/logout>**. It should now say
    **"You are not signed in."** Close and reopen the browser tab and check again — still signed out.

Two things this deliberately does *not* yet do: pages other than these are not access-controlled yet
(that is §6), and there is no "Sign in" link in the navigation yet (also §6) — for now type the URLs.

## NEXT

- **Block 1 (§1.1–1.4)** ✅ committed by @architect — reviewer-approved, all four gates green
  (build 0/0, test 7/7, format clean, validate strict OK). Solution scaffolded, identity store live.
- **Block 2 (§2.1–2.3 — password & token hashing)** ✅ committed by @architect — reviewer **signed off**
  after one review round, all four gates verified independently by the Architect before commit (build
  0 warn/0 err, **54/54** tests, `--strict` valid, format clean). Blocker 1 (`TryParse` accepting `m`/`p`
  sets `Verify` couldn't process) closed and confirmed by a 1088-set sweep; N1–N5 + R1–R3, R6 landed.
  Argon2id 64 MiB/t=3/p=1 PHC-encoded, base64url 32-byte tokens hashed SHA-256 lowercase hex,
  `TimeProvider`-driven idempotent ownership-scoped revocation, `ListAsync` projecting so `TokenHash`
  never enters the SELECT list.
- **AD7 (§1 amendment — `DateTimeOffset` storage)** ✅ committed by @architect — reviewer **signed off**,
  all four gates verified independently before commit (build 0/0, **61/61**, `--strict` valid, format
  clean, exactly one migration). Fixed-width ISO-8601 UTC text via `ConfigureConventions`, seven
  timestamp columns retyped, Block 1's `NOCASE` collations intact, `ListAsync` ordering server-side.
  Landed **before** Block 3 so §3's bootstrap is built against the final schema. 1.1–1.4 stayed ticked —
  a correction within them, not new work.
- **Block 3 (§3.1 + §3.3)** ✅ committed by @architect — reviewer **signed off** after two review rounds,
  all four gates verified independently before commit (build 0/0, **124/124**, `--strict` valid, format
  clean). Two blockers found and closed, **neither** in the three hazards the brief called out (those all
  verified correct by mutation): **BL1** — the refusal path burned a 64 MiB Argon2id hash on a
  permanently-anonymous route (0.25 s → 0.0016 s); **BL2** — AD11's regex was quadratic and reinstated
  that amplifier through an *earlier* door, since DataAnnotations runs before `OnValidSubmit` (500 in
  0.253 s → 302 in 0.0023 s). Also landed: the reusable `WebApplicationFactory` harness for Static SSR
  forms, AD10, AD11, and the credential guards at the service boundary.
- **§3.2** ✅ **verified by the Product Owner in a real browser (2026-07-26)** and ticked. §3 is complete.

**AD12 — section execution order is resequenced: §5 → §4 → §6 → §7 → §8 → §9.** Architect's call;
`tasks.md` is unchanged and every task still gets done, only the order of blocks moves.

*Why §5 before §4:* task **4.1 is specified as "issue … as an authenticated member"** and 4.4's revoke
surface is likewise a member action — but §5 (login and session) is what makes "the authenticated member"
exist. Building §4's UI first means building it against an identity mechanism that isn't there yet and
retrofitting it afterwards, or splitting §4 into a service-now / UI-later pair that leaves 4.1 and 4.4
half-ticked across two blocks. §5 has **no** dependency on §4 — login only needs an account, and §3 now
mints one — so the dependency runs one way only. Doing §5 first also means the Product Owner can actually
log in, which is what makes §4's issuing flow verifiable end to end when it lands.

*Why §6 after §4:* §6 denies anonymous access globally, and the routes needing exemption are spread
across §3 (`/bootstrap`, `/bootstrap/complete`), §5 (login) and §4 (invitation redemption, which is
necessarily anonymous — the invitee has no account yet). Running §6 once all three exist lets it exempt
them in a single deliberate pass instead of being amended three times, and makes "deny everything except
this list" reviewable as one statement.

- **Block 5 (§5.1–5.3 — login & session)** ✅ committed by @architect (`b8b5a3c`) — reviewer **signed
  off**, all four gates verified independently before commit (build 0/0, **149/149**, `--strict` valid,
  format clean). **§5.3 verified by the Product Owner in a real browser (2026-07-26)**; §5 is complete.
  Cookie auth only (no Identity stack), custom admin claim (not `ClaimTypes.Role`), AD8 satisfied in full
  — dummy-hash timing uniformity measured at miss 228.8 ms / wrong 222.0 ms / correct 226.3 ms, uniform
  status+body+headers across three separately-booted apps, projecting lookup, local-only `returnUrl`,
  POST+antiforgery logout. Two test-quality defects were found by **mutation, not by reading the code**:
  a path-only redirect assertion that let `//evil.example` pass as "redirected home", and a hasher
  recorder blind to the password, which let an empty-password miss path (0.0 ms vs 220 ms — a free miss
  path, the exact oracle §5.2 closes) pass 147/147. Both fixed; the redirect helper is now the only place
  in the test project touching `Headers.Location`.

---

### ▶ RESUME HERE — §6 (anonymous experience & access control)

**State: 18/31 tasks ticked. §1, §2, §3, §4, §5 all complete.** *(Counted from `tasks.md`, not carried
forward: the pre-§4 resume note said "15/31" but the real figure was 13/31 — §1 4 + §2 3 + §3 3 + §5 3.
The error rode along through 4a and 4b before being caught here. **Count the file; do not trust the
previous note's arithmetic.**)* §4 landed as two blocks — 4a (4.1, 4.4)
in `f8d1f61`, 4b (4.3, 4.5) in `52c77a9` — and **4.2 was confirmed in a browser by the Product Owner
(2026-07-27)**, walking the full recipe: issue → anonymous redeem → 12-character refusal → redirect to
login without a session → sign in as the new account → already-used on reuse → uniform message on a
bogus link. Working tree clean, branch `change/invite-only-authentication`, HEAD is the §4 confirmation
commit. All four gates green at HEAD (build 0/0, **249 tests**, `--strict` valid, format clean).
Remaining per **AD12**: **§6 → §7 → §8 → §9**.

**Before briefing §6, read:** `specs/authentication/spec.md`, `design.md` (D5, D7/Static SSR), and the
pinned decisions — especially **AD16** and **AD19** (the evidence standard), **AD6/AD15** (the
administrator flag), and **AD18** (login is the only route that mints a session).

**§6 = 6.1 (home shows only Login when anonymous) + 6.2 (deny anonymous access to content, redirect to
login) + 6.3 (auth pages render as Static SSR, no persistent circuit).** Likely one block; the Architect
carves it at brief time.

**§6 inherits, and a brief must bind:**

- **AD16 is now live and load-bearing for §6.** `AddAuthorization()` and `app.UseAuthorization()` are
  **required**, and `UseAuthorization()`'s *position* — after the explicit `UseAuthentication()` — is
  what stops `WebApplication`'s front-of-pipeline auto-insertion evaluating `[Authorize]` against a
  not-yet-authenticated `User` and 302-ing **every signed-in member** to `/login`.
  `AuthorizeRouteView` and `AddCascadingAuthenticationState()` are inert today and were kept
  **deliberately for §6/§7's `AuthorizeView`** — do not delete them as dead code; AD16 is the ruling
  that says why they are there.
- **§6 owns the global fallback policy**, which §4 explicitly did not pull forward. 4.1's `[Authorize]`
  is per-page; 6.2's deny-anonymous default is the systemic version, and the login redirect belongs
  here.
- **The failure signature to design tests against (AD16's lesson).** Anonymous-denial tests stay green
  through a break that denies *everyone* — they assert anonymous is denied, and the breakage denies
  anonymous too. **§6's suite must assert the authenticated path as hard as the anonymous one**, or it
  will describe a site nobody can log into.
- **AD19 — assert the condition, not just the outcome.** Verify mutants under the **full**
  `dotnet test`, never a filter; a filtered run is what hid Block 4b's B1.
- **`ClaimsPrincipalExtensions.IsAdministrator()`** is the single producer of the administrator flag.
  It is a **convention**, not a boundary — `InvitationService` takes `bool callerIsAdministrator` as a
  trusted parameter (AD15 as written). Do not add a caller that passes a literal.
- **§7 projection note, still open** — a `ToListAsync()` over `Account` entities throws if any single
  row has a corrupt value-converted timestamp, poisoning a list everyone reads. `InvitationService.
  ListAsync`'s join is the shape to copy for the account side; its `<remarks>` states precisely what
  that shape does and does not buy.
- **Carried from Block 4b's review, for §9:** §5's log-secrecy sweep still uses `Messages`, now
  measured to be the weaker instrument — a value passed via `BeginScope` reaches a structured sink
  while appearing in no message. Not a §5 regression (`LoginService` opens no scopes), but §9 should
  move it to `Written`.
- **Open, unchanged:** AD9 (raising Argon2 constants owes rehash-on-verify), and Block 4b's declined
  notes — N2 (`OpenConnectionAsync` never closed, shared with `BootstrapService`, so it is a §3 change
  too), N3, N4 (a corrupt AD7 timestamp is a 500 rather than a uniform refusal — inside AD17's
  boundary, so not an oracle), N6, N8.

---

**Superseded — the Block 4b resume note.** Kept for the record; §4 is complete.

**Block 4b = 4.2 (redeem) + 4.3 (reject expired/redeemed/revoked) + 4.5 (no open registration).** It is
the **anonymous** half of §4 — the caller has no account yet — which is what makes it the exposed one.
Everything 4a established is reusable; none of 4a's threat model transfers.

**What Block 4b inherits from 4a, and a brief must bind:**

- **N2 (reviewer, blocking for 4b) — `InvitationService.RevokeAsync` is check-then-act with no
  transaction.** Correct today because nothing else writes these rows. The moment redemption exists, a
  redemption committing between revoke's read and its write yields a row with **both `RedeemedAt` and
  `RevokedAt`**, reporting `Revoked` for an invitation that already created an account. **4b closes
  this** — the same `BeginTransaction(deferred: false)` that redemption needs anyway.
- **§3/B1's concurrency lesson, now due.** "Single-use" is a concurrency requirement exactly as "exactly
  one administrator" was. Two simultaneous redemptions of one invitation must create **one** account. A
  read-then-write cannot do this on SQLite: the write lock must be taken *before* the check
  (`BeginTransaction(deferred: false)` — there is **no** async overload). `BootstrapService` is the
  worked example, including *why* the Argon2 hash is computed **before** the lock is taken and never
  inside it. Prove it with a genuinely concurrent test, not the happy path run twice.
- **AD7 — the expiry predicate must reach SQL.** The single most important test in §4: assert on
  `ToQueryString()` that `ExpiresAt > now` is in the WHERE clause, not a client-side filter, as
  `DateTimeOffsetStorageTests` does. Expiry is a security boundary and the built-in
  `DateTimeOffsetToBinaryConverter` was measured *silently admitting an expired row*.
- **AD10 and AD11 — the same 12-character minimum and the same username charset as bootstrap**, from
  `CredentialPolicy`, via `CredentialPolicy.UsernameMatcher()`. Do **not** hand-roll a `Regex` over
  `UsernamePattern` and do not reintroduce an unbounded quantifier (that was BL2). AD10 exists so the
  two password-choosing paths cannot diverge — this is the second one.
- **BL1/BL2's cost lesson.** Redemption is **anonymous**, so every validation rule on it is
  attacker-reachable code that gets costed before it is added, and the 64 MiB Argon2id hash sits
  **behind** the cheap validity checks, never in front of them.
- **§5's C5 property — decide the oracle question deliberately and say so.** An invalid token, an
  expired one, a revoked one and an already-redeemed one: are they distinguishable to an anonymous
  caller? Note this is the *opposite* surface from 4a, where the reviewer approved `NotFound`
  collapsing "no such invitation" with "not yours" **because that route is authenticated**. Do not
  carry 4a's reasoning across the boundary; re-derive it for an anonymous caller.
- **`AlreadyRedeemed` needs its end-to-end case** (reviewer). 4a proved the enum value in isolation;
  only 4b can exercise revoke-after-redeem for real.
- **The `IsAdministrator()` convention (reviewer).** `InvitationService` takes
  `bool callerIsAdministrator` as a **trusted parameter** and does not derive it — that is AD15 as
  written, so correct-per-decision, but it makes `ClaimsPrincipalExtensions.IsAdministrator()` a
  convention future callers must follow, not a boundary the service enforces. A later route passing a
  literal `true` walks past it. Carry to §7 too.
- **`InvitationPolicy.RedemptionPath`** already exists (`/invite`) so 4a and 4b cannot spell the link
  differently. Use it; do not re-literal it.
- **AD16's mutation rule** — any mutation experiment verifies the file actually changed before
  believing the result.

**Expect §4.2 to be a Product-Owner browser verification**, as §3.2 and §5.3 were — implement and
self-test as far as the gates go, draft a precise copy-pasteable recipe, and **do not tick 4.2 on gates
alone**. A recipe must be written against AD10's 12-character minimum (see the superseded-recipe note
below).

---

**Superseded — the original Block 4 resume note.** Kept because the inherited constraints below were
written for the whole of §4 and still bind 4b; the *state* and *next-step* lines above replace it.

**Block 4 inherits, and a brief must bind all of these:**

- **AD7** — land a test proving the `ExpiresAt > now` expiry predicate reaches **SQL**, not a client-side
  filter. This is the single most important test in §4: expiry is a security boundary, and the built-in
  `DateTimeOffsetToBinaryConverter` was measured *silently admitting an expired row*. Assert on
  `ToQueryString()`, as `DateTimeOffsetStorageTests` does.
- **AD10** — the same 12-character password minimum on redemption, from `CredentialPolicy`.
- **AD11** — the same username charset via `CredentialPolicy.UsernameMatcher()`. **Do not hand-roll a
  `Regex`** over `UsernamePattern`, and do not reintroduce an unbounded quantifier (that was BL2).
- **AD4 / Block 2** — invitation tokens use `ISecretTokenGenerator`: high-entropy, SHA-256 hashed at
  rest, **plaintext shown once** and never stored or logged. Do not use Argon2 for them.
- **§3's concurrency lesson (B1)** — "single-use" is a **concurrency** requirement exactly as "exactly one
  administrator" was. Two simultaneous redemptions of the same invitation must create **one** account.
  A read-then-write cannot do this on SQLite: the write lock must be taken *before* the check
  (`BeginTransaction(deferred: false)`; there is **no** async overload). Prove it with a genuinely
  concurrent test, not the happy path run twice.
- **§3's BL1/BL2 lesson** — a validation rule is attacker-reachable code. Redemption is **anonymous** (the
  invitee has no account yet), so every rule on that route gets costed before it is added, and the
  expensive Argon2id hash must sit behind the cheap validity checks, not in front of them.
- **§5's C5 property** — redemption must not become a token-enumeration oracle: an invalid token, an
  expired one, a revoked one and an already-redeemed one should be indistinguishable to the caller.
  Decide deliberately whether they are, and say so.
- **The standing service-boundary rule** (below): structural invariants at the boundary always; a policy
  number only where the record is privileged and the mistake irreversible.
- **The forward-notes from §5:** use `RequireClaim(…, "true")`, never the bare form; and if §4 wants
  `IPasswordHasher`'s usable/unusable distinction, **change the shape** (tri-state `Verify`) rather than
  copying a pattern whose safety rests on documentation.
- **Test harness** — `tests/ZeroWiki.Tests/Web/` (`ZeroWikiAppFactory`, `StaticSsrForm`, `HttpAssertions`)
  exists and is reusable; §4's forms must use it. `RecordingPasswordHasher` and `CapturingLoggerProvider`
  are there too.

**Expect §4.2 (redeeming an invitation in a browser) to be a Product-Owner verification step**, as §3.2
and §5.3 were — draft the recipe and do not tick it on gates alone.

- **AD13 — session expiry stays sliding, 14 days. Product Owner's decision (2026-07-26),** answering
  reviewer note A1. **Record the consequence honestly, because it is stronger than the limitation the
  Architect originally wrote down:** cookie authentication is stateless, so `SignOutAsync` clears the
  browser's cookie but cannot revoke one already captured — and with `SlidingExpiration = true` that
  cookie is **renewed on every use**, so it does not expire at all while an attacker keeps using it. The
  14-day figure bounds *idleness*, not the cookie's life. The Architect's earlier phrasing ("valid until
  it expires") implied a bound that does not exist; corrected here. The PO accepted this knowingly in
  exchange for not re-authenticating a small trusted group every fortnight. Restoring a hard bound is a
  one-line change (`SlidingExpiration = false`); genuine revocation would need a server-side session
  store, which is not in this change.

**Standing rule established in Block 3 (applies to §4 onward):** structural invariants are enforced at
the service boundary always; a policy *number* only where the record is privileged and the mistake is
irreversible. AD10 is at the boundary in §3 solely because the first admin is minted with no invitation,
no authentication and no audit trail, and no password reset exists in this change. Do not cite it as
precedent for pushing product numbers into services generally.

**Open items, tracked so they can't be lost:**

- **AD8 (§5)** — three binding parts now, all traceable to `specs/authentication/spec.md`'s existing
  "without revealing whether the username exists", **not** to optional hardening:
  1. **Equalise the work** — verify against a fixed dummy PHC hash when the username lookup misses, so a
     miss costs the same ~93 ms as a hit. A uniform error string over a non-uniform response time does
     not satisfy the requirement.
  2. **Log the three-way distinction server-side** — "no such username" / "stored hash unusable" /
     "wrong password" — behind one uniform response, never logging the password or the hash (reviewer
     R5). A corrupt `PasswordHash` is otherwise permanently silent to whoever debugs "alice can't log
     in".
  3. **The §5 account lookup MUST project** — `Select(a => new { a.Id, a.Username, a.PasswordHash,
     a.IsAdministrator })`, **not** materialise the `Account` entity (AD7 addendum, reviewer-verified).
     Reason: AD7's converter runs only for materialised columns, so `SingleOrDefault(a => a.Username ==
     …)` on a row with a corrupt timestamp **throws**, turning login for that one user into a 500 while
     every other failure returns the uniform response — the same differential-oracle shape as Block 2's
     blocker, reached from the other direction. A projecting lookup is **immune by construction**, and
     §5 has no business loading `CreatedAt`/`GitEmails`/`GitTokens` to check a password. Measured: the
     projection succeeds and returns the hash on the same corrupt row that makes entity materialisation
     throw.
- **§3 note (verified, no action):** the empty-store check must stay a non-materialising
  `Accounts.AnyAsync()`. Confirmed a corrupt timestamp row **cannot** make the store look empty and
  re-open the first-admin bootstrap — that would have been the serious form of the AD7 corruption
  hazard. Don't refactor it into something that materialises accounts.
- **§7 note:** a `ToListAsync()` over all accounts **does** throw if any single row has a corrupt
  timestamp, so one bad row poisons an admin/member list view. Project there too.
- **AD9** — whoever raises the Argon2 constants owes rehash-on-verify in the same change, or existing
  accounts stay at the old cost forever. The PHC encoding is what makes it possible; nothing to do now.
- **AD10 — minimum password length is 12. Product Owner's decision (2026-07-25),** answering @worker's
  §3 escalation. Rationale as escalated: Argon2id makes *offline* cracking expensive, but a
  one-character password is guessable *online* in a handful of requests, and with rate limiting ruled
  out of scope there is no compensating control. **Applies to every path where a user chooses a
  password — §3 bootstrap and §4 invitation redemption — so the two cannot diverge.** A minimum only:
  **no** composition/complexity rules, **no** strength meter, **no** additional UI, since those were
  explicitly not chosen. Lands in the Block 3 review round; §4 must carry the same minimum.
  **Note the §3 Product-Owner verification recipe above is superseded** — it predates AD10, so its
  blank-submit expectations and its password no longer match. A corrected recipe is issued after AD10
  lands, and 3.2 is verified against *that*.
- **✅ CLOSED — login rate limiting is OUT OF SCOPE for this change. Product Owner's decision
  (2026-07-25).** Reviewer note A2 observed that §5's login is an unauthenticated 64 MiB / ~93 ms
  amplifier (a corrupt row up to 5.0 s per R4) and wanted a rate limit or concurrency gate. It is in no
  spec or task here, so it was the PO's scope call, and the PO declined it for this change. **Block 5
  must not implement one** — no rate limiter, no concurrency gate, no throttling middleware.
  Two things this ruling does *not* change: the Argon2 parameters stay at 64 MiB/t=3/p=1 (A2 was never
  an argument to weaken them, and weakening them is not a substitute for the gate that isn't being
  built), and **AD8 still stands in full** — dummy-hash timing uniformity and the three-way server-side
  logging are required by `specs/authentication/spec.md`'s existing no-enumeration requirement, not
  optional hardening, so they are unaffected by this decision. The hardening remains a legitimate
  candidate for a **future change**; it is recorded here rather than dropped, and this DEVLOG archives
  with the change.
