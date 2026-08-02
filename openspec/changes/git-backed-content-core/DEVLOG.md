# DEVLOG — `git-backed-content-core`

The shared working channel for this change. Organised by `## N.` section mirroring `tasks.md`, with
`## NEXT` pinned at the bottom. Append-only; only `## NEXT` is rewritten.

## 0. Pre-flight & design amendments

**[architect]** Change opened 2026-08-02. Branch `change/git-backed-content-core` cut from `main` at
`f2cae81`. Pre-flight clean: working tree clean, `openspec validate --strict` valid, `dotnet build`
0 warnings / 0 errors, `dotnet test` **385/385**.

**[architect]** Inherited state worth knowing before reading anything else: **§1.1 is already
delivered.** `src/ZeroWiki` was scaffolded by the archived `invite-only-authentication` change and is
already an ASP.NET Core 10 Blazor Web App whose default render mode is Static SSR — `Program.cs` calls
plain `AddRazorComponents()` / `MapRazorComponents<App>()`, there is no
`AddInteractiveServerComponents()`, and no `@rendermode` appears anywhere under `Components/`. That is
exactly D7's shape, achieved by omission. The Product Owner's ruling (2026-08-02): tick 1.1 and handle
the "Interactive Server enabled per-island" half **as and when it comes up** — the first island that
needs a circuit is §8.2's "changed on disk" broadcast, so the interactive render-mode wiring lands
next to its first consumer rather than sitting unused for seven sections.

**[architect]** The Product Owner elected to **front-load the three design gaps** rather than hit them
mid-section. All three are now closed and written into `design.md` as **D8–D11**; the Open Questions
section carries no live question. Summary, with the reasoning that is *not* in `design.md`:

- **D8 — one volume at `/data`.** The Product Owner's deployment shape: a single Docker volume, with
  `/data/identity.db` beside `/data/wiki`. `/data/wiki` is the repo root, so `.git` is at
  `/data/wiki/.git` and pages at `/data/wiki/docs/*.md` — "everything below `wiki` is git-backed", and
  the spec's mandated `docs/` working tree sits inside it. Both paths are configuration defaulting to
  `/data`.
- **D9 — always commit-as-recovered; no policy knob.** The argument that decided it: a dirty tree can
  hold **untracked** files, and copying a folder of Markdown onto the volume is the *normal* way to
  populate a new ZeroWiki, not an edge case. Discard is unrecoverable; an unwanted recovery commit is
  a `git revert`. The Product Owner also struck the "configured policy" wording from 2.4 — a switch
  whose only other setting deletes user content is a footgun. Recovery author:
  `System <system@zerowiki.org>`, deliberately the software's fixed identity, not the deployment's
  domain.
- **D10 — synthetic commit authorship.** The `invite-only-authentication` supervisor parked "which git
  email do we stamp, and what for an account with none?" for this change. The answer is *neither*:
  `GitEmails` is the **inbound** mapping table, many-to-one, with no primary flag and **no
  `CreatedAt`** — so "the first one they added" is not recoverable and alphabetical is the only order
  the schema can produce, which would silently rewrite a member's author line the day they add an
  address sorting earlier. Browser saves are therefore authored
  `<Username> <username@<configured host domain>>`. The Product Owner's rationale for synthetic
  addresses is worth preserving verbatim: *"those will not be functional emails, at least not at
  first, but some of the contributors might not want to supply an actual email address."* Confirmed
  safe by two checks: `Username` is written only at bootstrap and redemption and **has no rename
  path**, so the identity is stable for the life of the account; and the domain is **configured**, not
  read from the `Host` header, which is per-request and client-supplied.
  - **Binding on §8.3:** the resolver matches the **synthetic form first**, ahead of registered
    `GitEmails` rows. `/account` lets a member register any address, so without precedence someone
    could register another member's synthetic address and quietly collect their attribution. Resolving
    synthetic-first closes that with no change to the shipped `GitEmailService`.
- **D11 — username form.** D10 makes the username an email localpart, and the shipped
  `UsernamePattern` permits a leading or trailing dot (`.emmz`, `emmz.`), which is not a legal RFC 5322
  dot-atom. Fixed at source rather than normalised at the point of use — the Product Owner's call.
  New pattern `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z` plus `MinimumUsernameLength = 3`.

**[architect]** ⚠️ **Instrument failure worth recording, because it is the exact hazard CLAUDE.md
warns about and it nearly shipped a wrong answer to the Product Owner.** Evaluating a candidate
username pattern, the first Perl harness wrote the patterns inside `qr/…/`, where Perl **interpolated
`$\`** — the output record separator — out of the trailing `$\z`. The pattern under test silently
became one ending in a **literal `z` with no end anchor**, so `emmz.` and `emmz-` "passed" and `abc`,
`a.b` and a 64-character username "failed". Every one of those results is wrong, and the table was
internally consistent enough to look like a finding. Caught by the results being *implausible*, not by
the harness. The re-run uses non-interpolating `qr'…'` **and carries an explicit instrument check** —
`"abcz"` and `"abcq"` must behave identically under the pattern, which fails loudly if interpolation
is still happening. **Any regex harness in this change must carry an equivalent self-check.**

**[architect]** Corrected measurement, on which D11 rests — three variants over the same inputs:

| input | shipped | PO's first draft | D11 |
|---|---|---|---|
| `a`, `ab` | ok | REJECT | ok (regex) — refused by the length floor instead |
| `a.b` | ok | **REJECT** | ok |
| `.emmz`, `emmz.`, `-emmz`, `emmz-` | ok | REJECT | REJECT |
| `a`×64 | ok | ok | ok |
| `a`×65 | ok | **ok** | REJECT |
| `emmz\n` | REJECT | REJECT | REJECT |

The Product Owner's first draft
(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}[A-Za-z0-9]$\z`) achieved the goal but
carried two unintended effects: its **three** mandatory alphanumeric groups impose a minimum of three
*alphanumerics* — refusing `a.b` — and its two `{0,63}` runs put the ceiling at 129, above the real
`MaxLength(64)`. The middle group is a leftover from "at least one alphanumeric", which the new first
character already guarantees. `$\z` is harmless (`$` can only match where `\z` does) but the existing
comment argues specifically for `\z` alone. D11 keeps the regex to **shape only** and gives the floor
to a length rule, so one fault produces one message.

**[architect]** Process note from the Product Owner (2026-08-02), recorded because it governs how the
rest of this change is run: *"i think we're overthinking a lot of the process stuff for what is really
a pretty casual project right now."* Applied as: fewer clarification rounds, decisions taken and stated
rather than tabled as menus, and the gap-3 attachments question closed by the Architect on the design's
own logic rather than spent as a round. The block/review/supervisor loop stays — it has caught real
defects — but the deliberation in front of it comes down.

**[architect]** Scope amendment: D11 touches `CredentialPolicy`, which belongs to the **archived**
`user-accounts` capability. The Product Owner's call was to **fold it into this change** rather than
spin a separate one. Carried as `specs/user-accounts/spec.md` (`ADDED` → *Username form*), declared in
`proposal.md` under Modified Capabilities, and tasked as a new **`## 11.`** section. Not retroactive:
the pattern runs only at bootstrap and redemption, never at login, so no existing account — including
the Product Owner's `emmz` — stops working.

## 1. Project & container scaffolding

**[architect]** Base: `bd2eeea` — the container image and the configured data layout: `git` in the
image, one volume at `/data`, and the app resolving its content paths from configuration rather than
from a hard-coded location.

**[architect]** Brief → @worker, block 1 (1.1–1.3). **1.1 is already delivered** (see §0) and is ticked
on that record, not re-implemented; the block is **1.2 + 1.3**.

**[worker]** 1.3 — added `ZeroWiki.Content`: `ContentStorageOptions` (bound from the `ContentStorage`
config section, `DataRoot` defaulting to `/data`), `ContentPaths` (absolute, derived once —
`RepositoryRoot = <root>/wiki`, `WorkingTree = <root>/wiki/docs`), and
`ContentStorageStartupExtensions.AddContentStorage` registering both. Shape chosen: `AddOptions<T>().Bind(...).ValidateOnStart()`
for the raw config value, plus a singleton `ContentPaths` computed once from it at first resolution
— not `IOptions<ContentPaths>`, since the derived paths never change after startup and callers just
want plain strings, not options-monitor ceremony. Env-var override needs no special code: the
default host builder already merges `ContentStorage__DataRoot` env vars over `appsettings.json` via
the standard double-underscore convention, same mechanism as everything else in `Program.cs`.
`appsettings.Development.json` now sets `ContentStorage:DataRoot` = `App_Data` — same physical
folder `ConnectionStrings:IdentityDb` already resolves under, so dev mirrors D8's "identity.db
beside wiki/" shape without touching `IdentityDbStartupExtensions`. Wired into `Program.cs` via
`AddContentStorage(builder.Configuration)`. Tests in `tests/ZeroWiki.Tests/Content/ContentStorageOptionsTests.cs`
build a real `ServiceCollection`/`ConfigurationBuilder` and resolve `ContentPaths` from it (default,
config-section override, real `Environment.SetEnvironmentVariable` + `AddEnvironmentVariables()`
override restored in `finally`, and derived-path assertions) — no hand-constructed instances.

**[worker]** 1.2 — multi-stage `Dockerfile` (SDK `10.0` build → `aspnet:10.0` runtime) plus
`.dockerignore`. Verified directly against the running image, not by inspection:
- `git --version` → 2.43.0, present in the **runtime** stage (installed there, not just build).
- `git --exec-path` → `/usr/lib/git-core`; `git-http-backend` confirmed present and executable
  there (`-rwxr-xr-x`) — §7 has what it needs.
- Container runs as the pre-created non-root `app` user, `$APP_UID` = 1654 (`id` → `uid=1654(app)
  gid=1654(app)`), via `USER $APP_UID` in the runtime stage.
- Non-root/volume-ownership hazard: `/data` is `mkdir`+`chown $APP_UID:$APP_UID` **before** the
  `VOLUME` instruction, in the image. Verified end-to-end with a real named volume
  (`docker volume create` + `docker run -v zerowiki-verify:/data`): Docker populated the fresh
  volume from the image's `/data`, inheriting ownership — `identity.db`/`-shm`/`-wal` were created
  by the app with no permission error, owned `app:app`, confirmed by inspecting the volume from a
  second throwaway container. No entrypoint chown script needed.
  Also set `git config --global --add safe.directory '*'` for the `app` user as defense in depth
  against git's "dubious ownership" refusal on a repo whose uid doesn't match euid (e.g. a volume
  populated by other means) — not required for the clean-mount case just verified, since ownership
  already matches, but cheap insurance for §2 onward.
- `ConnectionStrings__IdentityDb=Data Source=/data/identity.db` and
  `ContentStorage__DataRoot=/data` set as image `ENV`, so `docker run -v zerowiki:/data` is correct
  bare — verified the app boots, runs EF migrations against `/data/identity.db`, logs the expected
  "no accounts, visit /bootstrap" warning, and `curl localhost:8080/` → `HTTP 200`.
- Documented in the Dockerfile's header comment and a new top-level `README.md` ("Running in
  Docker": what the volume holds, the two paths, the two env vars and their defaults).
- Noted, not fixed (out of this block's scope): the container logs ASP.NET's standard
  DataProtection-keys-not-persisted warning (`/home/app/.aspnet/DataProtection-Keys` inside the
  container, not on `/data`) — antiforgery/cookie state won't survive a container replacement.
  D8 names only `identity.db` and `wiki/` as volume contents; flagging for `## NEXT` rather than
  expanding scope here.

**[worker]** Gates: `dotnet build` 0 warnings/0 errors; `dotnet test` **389/389** (385 existing +
4 new); `dotnet format --verify-no-changes` clean; `openspec validate git-backed-content-core --strict`
valid. No mutation testing — config binding and container plumbing, not a security/concurrency path.

→ @reviewer

**[reviewer]** Block 1 (1.2–1.3) audited against `git diff bd2eeea` — confirmed all 10 changed files
present (`.dockerignore`, `Dockerfile`, `README.md`, `DEVLOG.md`, the three new `Content/` files,
`Program.cs`, `appsettings.Development.json`, `ContentStorageOptionsTests.cs`). No render-mode
touch — `Program.cs` still calls plain `AddRazorComponents()`, no `AddInteractiveServerComponents`,
no `@rendermode` — 1.1 is untouched, in scope.

Verdict: **Approve with nits.** Two things I want on the record rather than blocking the commit —
one documentation gap the worker should close (nit, not a blocker), and a severity read on the
DataProtection question the Architect asked for.

Empirical checks performed (not just read the code):

1. **PO's `emmz` account survives — confirmed, not merely inspected.** Ran the app against
   `src/ZeroWiki/App_Data/identity.db` (`ASPNETCORE_ENVIRONMENT=Development`, `dotnet run` from
   `src/ZeroWiki`). Log: `"The identity store already has at least one account; the first-administrator
   bootstrap path is inert."` `sqlite3 App_Data/identity.db "SELECT Username FROM Accounts;"` → `emmz`,
   before and after. A raw `sha256sum` of the `.db` file *did* change across my two manual runs — that's
   WAL-checkpoint page churn, not data loss: `sqlite3 .dump` is stable, row counts unchanged
   (1 Account/1 GitToken/1 Invitation), and `emmz`'s `CreatedAt` is unchanged from its original
   `2026-07-26` value. Flagging this only so nobody else uses a raw file checksum as their instrument
   for "did this touch the DB" on a WAL-mode SQLite file — it isn't stable even under a no-op open.
   Confirmed separately that `ZeroWikiAppFactory` (the integration-test harness) points at a throwaway
   temp file, never at `App_Data/`, so `dotnet test` itself can't touch the real store.

2. **`ValidateOnStart()` has teeth — not decorative.** `ContentStorageStartupExtensions.cs:18-21` pairs
   it with a real `.Validate(options => !string.IsNullOrWhiteSpace(options.DataRoot), ...)` predicate.
   Verified live: ran the app with `ContentStorage__DataRoot="   "` and got a fail-fast
   `OptionsValidationException: ContentStorage:DataRoot must not be empty.` at host startup, before any
   request is served. No finding.

3. **Bind-mount ownership — real gap, but I can't fully reproduce it on this host, and I want that
   caveat on the record along with the finding.** This sandbox's Docker runtime is OrbStack on macOS,
   whose bind-mount file-sharing layer synthesizes ownership: a host directory `mkdir`'d by my macOS
   user (`rendle`, uid 501) showed up *inside* the container as `app:app` (uid 1654) — i.e. the exact
   real-Linux-host mismatch the brief asked me to test doesn't reproduce here, because the sharing layer
   papers over it. On a genuine Linux host it does not: a bind-mounted `-v $(pwd)/data:/data` presents
   the *real* host uid/gid to the container, and a directory freshly `mkdir`'d by a host user is mode
   `0755` — owner read/write/execute, group and other read/execute only. `$APP_UID` (1654) is neither
   that owner nor (normally) a member of that group, so the app gets **no write access at all** on
   first boot — not a "dubious ownership" git refusal, a flat `Permission denied` writing
   `/data/identity.db`, before git ever enters the picture. The README (`README.md:8-13`) documents only
   the named-volume flow; nothing tells an operator who reaches for the very common
   `-v ./data:/data` pattern that they need to pre-chown the host directory to `1654:1654` (or run with
   `--user "$(id -u):$(id -g)"`, or similar). The DEVLOG's `[worker]` entry (§1, "Non-root/volume-ownership
   hazard...") is accurate about what it tested but reads as a general "ownership is handled" claim —
   it was verified only for named volumes, and the README doesn't disambiguate either. Given task 1.2 is
   explicitly "document the mounted data volume," I'd like the README to at least name this operational
   requirement for the bind-mount case (a sentence is enough — this isn't asking for an entrypoint chown
   script, which would be a bigger design call). Recording as a **nit**, not a blocker, since D8 doesn't
   mandate bind-mount support and the named-volume path — the one actually shown in the README's
   quick-start — is correctly handled and was genuinely verified end-to-end.

4. **Config-binding tests exercise the real pipeline, confirmed by killing it.** Mutated
   `ContentStorageStartupExtensions.cs:17` (`Bind(configuration.GetSection(ContentStorageOptions.SectionName))`
   → `Bind(configuration.GetSection("WrongSectionName"))`), checksummed the file before and after, ran
   the **full** `dotnet test` (not filtered): `ConfigurationSectionOverridesDataRoot` and
   `EnvironmentVariableOverridesDataRoot` both failed (expected `/srv/zerowiki`/`/env/zerowiki`, got the
   `/data` default), `DefaultsDataRootToSlashDataWhenUnconfigured` correctly stayed green (its own
   scenario is unaffected by a wrong section name). 2 dead / 387 passed / 389 total — one confirmation
   run, consistent and understood, no need for a second. Reverted; `shasum` matches the pre-mutation
   value; `git diff -- src` and `git status --short -- src` both clean of residue.

5. **Path handling — no defect.** Checked `ContentPaths` against a relative root, a root with one and
   two trailing separators, `../escape`, `~/data`, `.`, and empty/whitespace, via a scratch console app
   using the identical `Path.GetFullPath`/`Path.Combine` calls. Trailing separators are absorbed cleanly
   by `Path.Combine` (no double slashes in the derived paths); empty/whitespace throws before ever
   reaching here, per #2. Relative roots resolve against `Environment.CurrentDirectory` — that's the
   same mechanism `IdentityDbStartupExtensions.EnsureDataDirectoryExists`
   (`src/ZeroWiki/Data/IdentityDbStartupExtensions.cs:42`) already uses for the identity connection
   string's relative `App_Data/identity.db`, so `ContentStorage:DataRoot=App_Data` in
   `appsettings.Development.json` genuinely lands beside `identity.db` as the DEVLOG claims — verified
   live in #1, not just by matching the two config values on paper. In the container both paths are
   absolute (`/data`, `/data/identity.db`), so `dotnet run` vs. the container never actually differs in
   practice. `../escape` does resolve outside `DataRoot` as given, but this value is operator-supplied
   deployment configuration, not request-time user input, so it isn't the traversal hazard CLAUDE.md's
   domain-hazards section is naming — not treating it as a finding.

6. **DataProtection key ring — severity read, as asked, not a fix.** Cookie auth is the only thing this
   touches: an unpersisted key ring means every container replacement (redeploy, crash-restart, image
   update) invalidates every signed-in session — members get bounced to `/login` and have to
   re-authenticate. It does not leak, forge, or weaken anything: git tokens live in the DB hashed and
   are untouched by this, and a forced re-login after a restart is if anything a conservative failure
   mode, not a permissive one. I'd call it a UX papercut, not a security or data-integrity defect — it
   doesn't belong in the "domain hazards" tier CLAUDE.md names for this project. Whether it lands now or
   later is a scope call for the Architect, not a review blocker; I'll note it's cheap and this exact
   block already introduced `ContentPaths.DataRoot` as the natural place to point
   `PersistKeysToFileSystem` at (e.g. `<DataRoot>/keys`), which is why the worker flagged it here rather
   than in a later section. Parking for `## NEXT` seems right to me, but that's the Architect's call.

**Nits:**
- `README.md` — add a line on the bind-mount ownership requirement (see #3).

No blockers. Gates were independently re-verified where my own checks touched the tree (mutation
in #4) and left it clean; I did not need to re-run the full gate suite otherwise since nothing else
changed.

**[architect]** Reviewer's nit applied by me, **after** its `Approve with nits` — recording it here
because an `Approve` certifies the state it was shown, and this edit is not in that state.

- **What changed:** `README.md` only. Added the bind-mount caveat the reviewer asked for — named
  volumes are seeded from the image and come up correctly owned, bind mounts keep the host
  directory's ownership and need a `chown` to uid 1654 before first run. Also stated that git's
  "dubious ownership" refusal is already disarmed inside the image by `safe.directory '*'`
  (`Dockerfile:47`), so ownership matters for the filesystem, not for git.
- **Why no re-review:** documentation, zero production or test code, and it cannot alter any gate.
  `git diff -- src` and `git status --short -- src` are both empty over this edit.
- **What I verified myself rather than relaying:** the uid. Both agents reported 1654, but they
  share an instrument, and two measurements agreeing is not corroboration. Read it straight off the
  base image — `docker image inspect mcr.microsoft.com/dotnet/aspnet:10.0` → `APP_UID=1654`.

**[architect]** Two of the reviewer's observations are worth carrying forward as standing notes,
neither being a finding against this block:

- **A raw checksum is not a valid instrument for a live WAL-mode SQLite file.** The reviewer saw
  `identity.db`'s checksum drift between runs purely from WAL-checkpoint page churn, with logical
  content unchanged. CLAUDE.md mandates checksum-before-and-after for mutation targets; that rule is
  for source files. For SQLite, compare `sqlite3 .dump` output or row counts instead, or a
  checkpoint will read as a mutation.
- **`safe.directory '*'` is set globally for the app user** and is load-bearing for §2 and §7, not
  decoration. Anything that changes the container user or the repository's ownership has to keep it
  true, or `git init`, the hooks, and `http-backend` all start refusing the repository.

**[architect]** Gates re-verified independently at the final tree, after the README edit: `dotnet
build` 0 warnings / 0 errors, `dotnet test` **389/389**, `dotnet format --verify-no-changes` clean,
`openspec validate git-backed-content-core --strict` valid. `git diff -- src` and `git status
--short -- src` inspected before commit. Boxes 1.1–1.3 ticked; block committed.

## NEXT

**Resume point: §1, block 1 (1.1–1.3).** Nothing committed on this branch yet beyond the design
amendments. §11 runs as its own block immediately after §1 — small, isolated, and foundational to
D10's synthetic identity.

Design questions outstanding: **none.** `design.md`'s Open Questions section is fully resolved
(D8–D11 plus the two questions inherited as already-resolved).
