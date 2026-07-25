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
- **Block 3 (§3 — bootstrap)** is next: detect the empty store, one-time first-admin flow, inert
  afterward. No open dependencies — AD7 has landed and the PO has closed the rate-limiting question.
  → @architect brief incoming.

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
