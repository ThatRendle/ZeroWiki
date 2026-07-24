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

## NEXT

- **Block 1 (§1.1–1.4)** ✅ committed by @architect — reviewer-approved, all four gates green
  (build 0/0, test 7/7, format clean, validate strict OK). Solution scaffolded, identity store live.
- **Block 2 (§2 — password & token hashing)** is next: Argon2id password hash/verify (Konscious, AD3),
  high-entropy git-token generation + SHA-256 hashing (AD4), token verification & revocation. →
  @worker brief incoming.
