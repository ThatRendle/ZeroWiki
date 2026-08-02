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

**[supervisor]** Section review of `git diff bd2eeea..HEAD` (one block, `3f6d837`, plus the
post-Approve README edit). Judged against §1's real job — *is a container image plus one mounted
volume a working deployment substrate for §2–§9?* — not against the three task sentences.

**Verdict: Request changes.** One blocker, verified empirically and fixable in one line of §1's own
artifact. Everything else is a `## NEXT` note.

### Verified independently (the record is honest)

Built the image and ran it rather than reasoning statically. Every empirical claim in this thread
holds:

- `git` 2.43.0 in the **runtime** stage; `git-http-backend` present and executable at
  `/usr/lib/git-core/git-http-backend` (`-rwxr-xr-x`). §7 has its binary.
- Runs as `uid=1654(app)`; `/data` is `uid=1654 gid=1654 mode=755` in the image; a fresh named
  volume is seeded from it and the app writes `identity.db`/`-shm`/`-wal` as `app:app` with no
  permission error. Container boots and serves: `/` → 200, `/login` → 200.
- **`emmz` is intact.** Verified non-destructively — copied the file and queried the *copy*, never
  opening the original. 1 Account (`emmz`, `CreatedAt` `2026-07-26T16:26:07`), 1 GitToken,
  1 Invitation; `sha256` of `src/ZeroWiki/App_Data/identity.db` identical before and after my read.
- **1.1 independently confirmed, not accepted on the record.** `Program.cs:13` is plain
  `AddRazorComponents()` and `Program.cs:123` plain `MapRazorComponents<App>()` — no
  `AddInteractiveServerComponents`, no `AddInteractiveServerRenderMode`, and no `@rendermode` in any
  of the 15 `.razor` files. The app is Static-SSR-by-default today, with *zero* interactive
  infrastructure registered. D7 holds; the ticked 1.1 is a true record.
- **D8's content layout is consistent across all three surfaces** — `ContentPaths.cs:22-23`
  (`<root>/wiki`, `<root>/wiki/docs`), `Dockerfile:49-50` (`ContentStorage__DataRoot=/data`), and
  `README.md`. No drift.
- **No PO data baked into the image.** `find / -xdev` for `*.db` / `App_Data` in the built image
  returns nothing — `.dockerignore`'s `**/App_Data/` and `**/*.db` are effective. Worth stating
  explicitly because the build context contains real credentials.
- No dead scaffolding: `/data/wiki` correctly does *not* exist in the image — §2 creates it.
  `ContentPaths` is three fields, all consumed from §2 onward. It earns its keep.
- Checked, and **not** a finding: `/invitations` and `/account` answer anonymous requests with
  `200` + the login page body, not a redirect and not page content. Uniform and non-enumerating.

### Blocker — `safe.directory` is HOME-scoped and evaporates in exactly §7's subprocess

`Dockerfile:47` — `RUN git config --global --add safe.directory '*'` — runs *after* `USER $APP_UID`,
so it writes **`/home/app/.gitconfig` only**. There is no `/etc/gitconfig` in the image. The setting
therefore survives only for processes that inherit `HOME=/home/app`.

§7 shells out to `git http-backend` as a **CGI subprocess**, and building CGI properly means
constructing an explicit environment. Reproduced against a repo whose owning uid ≠ the app's euid —
the precise scenario the Dockerfile header comment says this defends (`a volume populated by another
means, or restored from a backup taken as a different user`):

| environment | result |
|---|---|
| `HOME=/home/app` (inherited) | clean — `safe.directory` applies |
| `env -u HOME` | `fatal: detected dubious ownership in repository at '/data/wiki'` |
| `env -i PATH=...` (scrubbed) | `fatal: detected dubious ownership` |
| `git http-backend` under a scrubbed CGI env | **`Status: 500 Internal Server Error`** |

This matters beyond the code because the record asserts otherwise. The `[architect]` standing note
above says *"`safe.directory '*'` is set globally for the app user and is **load-bearing for §2 and
§7**, not decoration."* It is load-bearing, and it is broken in §7 — so §7's worker inherits a
guarantee that does not hold, and the symptom is an HTTP 500 that reads as a git-protocol problem
rather than a container one. A block review cannot see this: the Dockerfile line is correct in
isolation, and §7 does not exist yet.

**Verified fix** (I built it and re-ran the table): move the config to **system scope**, before the
`USER` switch, so it is HOME-independent —

```dockerfile
RUN git config --system --add safe.directory '*'   # as root, before USER $APP_UID
```

That writes `/etc/gitconfig`; both the `env -i` case and the `http-backend` CGI case then pass, the
latter returning a proper `application/x-git-upload-pack-advertisement` instead of a 500.

**Remediation block shape** — small, one file plus the record:
1. `Dockerfile:47` → `--system`, relocated above `USER $APP_UID`.
2. Correct the `[architect]` standing note in this thread: system-scope, and *why* per-user scope was
   insufficient, so §7 does not re-derive it.
3. Optional but cheap: state in the Dockerfile comment that the scope is deliberate because
   `http-backend` runs with a constructed environment.

No test is warranted — this is container configuration, and the evidence is the reproduction above.

### Architectural notes → `## NEXT` (not part of the fix block)

1. **The runtime image has no HTTP client.** `curl`, `wget`, and `nc` are all absent; `flock` **is**
   present at `/usr/bin/flock`, so §5.3's shell hooks are safe. But §8.1/§8.2 have `post-receive`
   calling back into the app, and if that callback is HTTP the image cannot make the call. Decide
   §8's callback mechanism *before* §8 starts — if it is HTTP, the Dockerfile changes, and a later
   section reopening §1's artifact is the drift this review exists to prevent.
2. **D8 says "both paths are configuration, defaulting to `/data`" — that is not true of the
   identity path.** `appsettings.json:10` ships `Data Source=App_Data/identity.db` as the base
   default for *every* environment; only the `Dockerfile:49` `ENV` moves it to `/data`. Content uses
   the opposite strategy: production default in code (`ContentStorageOptions.cs:15`), Development
   overriding to `App_Data`. Two inverted defaulting strategies for one volume layout. Inside the
   container it is correct, so this is coherence rather than a defect — but the D8 layout is now
   asserted in four places and the identity half is right in only one of them.
3. **`ContentPaths` has no `GitDir` and no lock path, and §5 needs one.** The `flock` lockfile must
   not live in the working tree — an untracked lockfile under `docs/` dirties the tree, which D9
   would commit as content and `updateInstead` would bounce pushes on. `ContentPaths` is the right
   home for it (`<RepositoryRoot>/.git/…`); flag it in §5's brief so §5 extends this type rather
   than growing a parallel path helper beside it.
4. **`VOLUME ["/data"]` buys a footgun and nothing else.** Named-volume seeding happens regardless of
   the instruction; what `VOLUME` adds is that `docker run` with no `-v` silently creates an
   *anonymous* volume — measured, +1 dangling volume per run — so the wiki appears to work and the
   data is stranded on the next `docker rm`. For a product whose whole story is "one volume", failing
   loudly would serve the operator better. Worth a deliberate keep-or-drop call, not urgent.
5. **DataProtection key ring — severity read agreed.** Not re-raised as a finding. The reviewer's
   "UX papercut, not a security defect" is right: forced re-login is a conservative failure mode, git
   tokens are hashed in the DB and untouched. One addition — the ring currently sits under
   `/home/app`, i.e. container-local, so it is lost on replacement rather than merely unshared;
   `<DataRoot>/keys` remains the natural fix whenever the PO decides scope.

Housekeeping: I removed every container, volume, and image I created. One anonymous volume from the
`VOLUME` demonstration in note 4 is left behind among pre-existing dangling volumes — I did not prune,
since blind `volume prune` on this host could take data that isn't mine.

**[architect]** Supervisor's `Request changes` accepted in full. **Correcting my own standing note
above**: I wrote that `safe.directory '*'` is "set globally for the app user and is load-bearing for
§2 and §7". The first half is exactly the defect — *globally* means `--global`, which is HOME-scoped,
and §7's `git http-backend` CGI subprocess runs under a constructed environment where `HOME` may not
survive. The note would have handed §7's worker a guarantee that does not hold, and the symptom
(`Status: 500`) reads as a git-protocol bug rather than a container one. The supervisor built the fix
and re-ran its whole table; treat `--system` as the binding form from here.

**[architect]** Two Product Owner decisions land in §1 alongside the remediation:

- **New task 1.4 — persist the DataProtection key ring.** The Product Owner's call (2026-08-02) on
  the scope question the block reviewer raised: fold it into §1 rather than park it. The supervisor's
  note 5 sharpens why — the ring currently sits under `/home/app`, which is *container-local*, so it
  is lost on every image replacement, not merely on restart.
- **Process:** per the Product Owner's steer on ceremony, the remediation and 1.4 go to **one worker
  and one reviewer pass**, but land as **two commits** — a `fix(...)` for the supervisor's findings
  (ticking nothing) and a `feat(...)` for 1.4. Halving the agent round-trips is the saving; blurring
  what was a defect and what was new work is not, so the commit record stays split.

**[architect]** Remediation scope — the blocker plus the two `## NEXT` notes that are one-liners in
the same file, taken now because reopening the Dockerfile later costs more than doing them together:

1. **Blocker** — `git config --system --add safe.directory '*'` as root, *before* the `USER` switch.
2. **Note 2** — D8 says both paths default to `/data`, but `appsettings.json` ships
   `Data Source=App_Data/identity.db` as the base default for every environment, so the container is
   only correct because its `ENV` says so. Align identity with content's strategy: `/data` in the
   base, `App_Data` in the Development override. **This is the change that can destroy the Product
   Owner's account if it lands half-done** — the Development override must exist in the same edit.
3. **Note 4** — drop `VOLUME ["/data"]`. It does not enable named-volume seeding (that happens
   regardless) and it silently creates an anonymous volume on every `docker run` without `-v`, which
   the supervisor measured.

Notes 1 and 3 are **not** in this block. Both are forward obligations, pinned in `## NEXT`.

**[worker]** Both pieces done — reporting Piece A (remediation) and Piece B (1.4) separately since
they land as two commits.

**Piece A — remediation (ticks nothing):**

- **A1 (blocker).** Reproduced the failure first, on the unmodified image: built
  `zerowiki-remediation:before`, seeded a named volume with a repo owned by a different uid (5000)
  than the app (1654), then ran the exact four-row table against it —
  `HOME` inherited → clean, `env -u HOME` → `fatal: detected dubious ownership`, `env -i` → same
  fatal, `git http-backend` under a constructed CGI env → **`Status: 500 Internal Server Error`**.
  All four match the supervisor's report exactly. Fixed by moving `git config --system --add
  safe.directory '*'` above `USER $APP_UID`, as root (writes `/etc/gitconfig`, HOME-independent).
  Rebuilt (`:after`) and re-ran the identical table against the identical volume: all four now
  clean, and `http-backend` returns a real
  `Status: 200` / `application/x-git-upload-pack-advertisement` body instead of 500. Also updated
  the header comment, which described the bug as the intended behaviour.
- **A2.** `appsettings.json`'s `ConnectionStrings:IdentityDb` base default moved to
  `Data Source=/data/identity.db`; `appsettings.Development.json` now carries the matching
  `App_Data/identity.db` override alongside its existing `ContentStorage:DataRoot`. Verified
  non-destructively before touching anything: copied `App_Data/identity.db`, took `sqlite3 .dump |
  sha256sum` (`934deca5…`) and read `emmz`'s `CreatedAt` (`2026-07-26T16:26:07.1478100Z`) from the
  copy. After the edit, ran the app in Development against the **real** file
  (`ASPNETCORE_ENVIRONMENT=Development dotnet run`): log said `"No migrations were applied"` and
  `"The identity store already has at least one account; the first-administrator bootstrap path is
  inert"` — it opened the existing store, not a fresh one. Killed the process, re-ran the dump/hash
  on the original file: **identical** `934deca5…`, same row counts (1/1/1), same `CreatedAt`.
  `emmz` is untouched.
- **A3.** Removed `VOLUME ["/data"]`. Verified after removal: a fresh named volume is still seeded
  correctly (`identity.db` created `app:app`, no permission error), and a run with **no** `-v` flag
  left the dangling-volume count unchanged (29 before, 29 after) — no anonymous volume created.

All docker images/volumes/containers created for this reproduction were removed afterward; the two
long-running unrelated containers on this host (`dhpp-sql`, `storage`) were untouched.

**Piece B — 1.4, persist the DataProtection key ring:**

- Added `ContentPaths.KeysDirectory` (`<DataRoot>/keys`), computed alongside `RepositoryRoot` and
  `WorkingTree` in the same constructor — one place derives all of D8's paths.
- Added `ContentStorageStartupExtensions.ResolveContentPaths(IConfiguration)`: reads the same
  `ContentStorageOptions` section `AddContentStorage` binds, so `Program.cs` can get `ContentPaths`
  before `builder.Build()` (which `PersistKeysToFileSystem` needs) without a second notion of where
  the data root is, and without the `BuildServiceProvider()` anti-pattern (ASP0000).
  `ResolveContentPathsMatchesTheDiRegisteredSingleton` asserts the two never drift.
- `Program.cs`: `AddDataProtection().SetApplicationName("ZeroWiki").PersistKeysToFileSystem(new
  DirectoryInfo(contentPaths.KeysDirectory))`. Comments in place for: why the app name is pinned
  explicitly (default discriminator derives from content root, which differs between `dotnet run`
  and the container), and why unencrypted-at-rest on Linux is accepted (same volume, same trust
  boundary as `identity.db`'s Argon2id hashes).
- `ZeroWikiAppFactory` now also pins `ContentStorage:DataRoot` to a throwaway temp directory,
  mirroring the existing identity-connection-string override — without it, every integration test
  would have tried to create `/data/keys` on the host running the suite.
- **End-to-end, not inferred:**
  - Container: built the image, bootstrapped a real account and logged in over real HTTP (curl,
    antiforgery token + `_handler` hidden field extracted from the rendered form), captured the
    `ZeroWiki.Authentication` cookie. `docker restart` → cookie still authenticates. Removed the
    container entirely and started a **new** one (image-replacement scenario) against the same
    named volume → cookie **still** authenticates. Confirmed `/data/keys/key-*.xml` on the volume,
    owned `app:app`, mode `0600`. No more "keys not persisted outside the container" warning in
    either log.
  - `xUnit`: added `ZeroWikiAppFactory.RestartedFrom(previous)` — a second factory sharing the same
    database file and `DataRoot`, giving a fresh DI container and freshly-loaded key ring, i.e.
    everything a real restart changes except the OS process. `A_session_survives_a_restart_…`
    signs in, restarts, asserts the cookie still authenticates, **and** asserts a `.xml` key file
    exists under the factory's own `DataRoot` (so the pass can't be explained by some other
    DataProtection default-repository fallback). `A_session_does_not_survive_an_unshared_key_ring`
    is the control: an independent factory with its own `DataRoot` cannot decrypt the first
    instance's cookie.
  - **Instrument check, capped at 3 runs as CLAUDE.md scopes for this project.** Mutated
    `Program.cs` to drop only `.PersistKeysToFileSystem(...)` (kept `AddDataProtection()` +
    `SetApplicationName`). 3/3 consistent: the positive test **still passed** — this dev machine's
    writable `$HOME` lets ASP.NET's own default repository discovery fall back to
    `~/.aspnet/DataProtection-Keys`, which both hosts in the same test process incidentally share,
    masking the mutation — while the control test failed with an unrelated-looking body mismatch
    (the cookie decrypted against the wrong account's ring). This is a property of the *test
    environment*, not the shipped code: `PersistKeysToFileSystem(...)` is an explicit repository
    override with no dependency on that fallback, which is exactly what the on-disk-file assertion
    above now checks directly rather than relying on cross-host behaviour to prove it. Reverted;
    `shasum` of `Program.cs` matched before/after; `git diff -- src` and `git status --short -- src`
    both clean of residue afterward.
  - Development: confirmed the identity-store aliasing didn't regress Development's content-storage
    default — ran the app in Development and observed `App_Data/keys/key-*.xml` appear (mode
    `0600`) alongside `App_Data/identity.db`, i.e. the relative `App_Data` root resolves to the
    same place for both. Did **not** attempt a real sign-in-restart round trip against the PO's real
    `identity.db` — bootstrap is inert with an existing account and the real `emmz` password isn't
    available to this worker, and seeding a synthetic account into that file was ruled out by A2's
    non-destructive requirement. The container test and the `RestartedFrom` xUnit test exercise the
    identical `Program.cs` wiring end to end; only the `DataRoot` value differs between Development
    and the container, and that value's correct resolution is what's confirmed directly.

**Gates (both pieces, final tree):** `dotnet build` 0 warnings / 0 errors. `dotnet test` full suite
**394/394** (389 existing + 3 `ContentPaths`/`ResolveContentPaths` tests + 2 `LoginPageTests`
restart tests). `dotnet format --verify-no-changes` clean. `openspec validate
git-backed-content-core --strict` valid. `git diff -- src` / `git status --short -- src` reviewed
before reporting — no mutation residue.

Diff is cleanly separable: `Dockerfile` + `appsettings.json` + `appsettings.Development.json` =
Piece A; `Program.cs`, `Content/ContentPaths.cs`, `Content/ContentStorageStartupExtensions.cs`, and
the three test files = Piece B.

→ @reviewer

**[reviewer]** Audited `git diff 3f6d837` (11 files) — the remediation (Piece A) and 1.4 (Piece B).
Built and ran rather than reasoning statically, per the brief. One process note first: my first
mutation attempt used a shell `trap ... EXIT` to auto-revert, which doesn't work across separate
tool calls (shell state doesn't persist), and a subsequent `git checkout -- Program.cs` — intended
as a revert — instead discarded the worker's **uncommitted** Piece B changes back to HEAD, since
`git checkout --` restores from the index/HEAD, not "the state before my edit," for anything not
yet committed. Caught it immediately via checksum, reconstructed the file from the diff I'd already
captured, and confirmed byte-for-byte restoration (`sha256 c27f7083…`, matching before any mutation)
before continuing. Recording it here in the interest of an honest record, and because it's a sharp
edge worth knowing about: **for uncommitted work, `git checkout --`/`git restore --` is not a safe
revert primitive — it discards forward, it doesn't rewind.** All mutation reverts from that point on
used direct Edit-based restoration verified by checksum, never `git checkout`.

**Piece A — no blockers, all three items reproduced independently:**

- **A1 confirmed, end-to-end.** Built the current image; `/etc/gitconfig` has `[safe] directory = *`
  and `/home/app/.gitconfig` does not exist. Seeded a named volume with a repo `chown`'d to uid 5000
  (app runs as 1654) and ran the four-row table: `HOME` inherited, `env -u HOME`, `env -i`, and
  `git http-backend` under a constructed CGI environment (`GIT_PROJECT_ROOT`, `PATH_INFO=/wiki/info/refs`,
  `QUERY_STRING=service=git-upload-pack`, no `HOME`) — all four clean, the CGI case returning a real
  `Content-Type: application/x-git-upload-pack-advertisement` body with no `Status:` override (200).
  Then, to see the actual regression rather than take the fix on faith, rebuilt the image with the
  Dockerfile's config moved back to `--global` after `USER $APP_UID` (the pre-fix shape) via an
  Edit-then-immediately-Edit-back on `Dockerfile`, never `git checkout`, checksum-verified restored
  (`sha256 4b4b21fc…`, unchanged from before the experiment). Against that image, same volume: `env -u
  HOME` and `env -i` both `fatal: detected dubious ownership`, and `http-backend` returned
  **`Status: 500 Internal Server Error`** — the exact defect the supervisor reported, reproduced fresh.
  `Dockerfile:20-27,49-51` match.
- **A2 confirmed — nothing else resolves to `/data`.** `ZeroWikiAppFactory.cs:112-115` pins both
  `UseEnvironment(Environments.Production)` and `UseSetting("ContentStorage:DataRoot", _dataRoot)`
  unconditionally, so the test host never sees `appsettings.Development.json`'s override *or* falls
  through to the base `/data` default — it's pinned to a throwaway temp dir regardless. Confirmed
  empirically, not just read: `/data` does not exist on this machine, so if the pin were missing or
  wrong, the suite would fail loudly (permission denied at the filesystem root) rather than pass by
  accident — and the full suite passes 394/394. Also ran `dotnet run` with
  `ASPNETCORE_ENVIRONMENT=Development` against the **real** `App_Data/identity.db` afterward to
  double-check nothing regressed for the PO's store: log said the bootstrap path is inert, i.e. it
  opened the existing store. Did not touch the file directly myself beyond that read-only run.
- **A3 confirmed.** No `VOLUME` instruction in the Dockerfile. Named-volume seeding still works —
  fresh volume, `identity.db` and `keys/` created owned `1654:1654`, app serves `200`. A bare
  `docker run` with no `-v` left the dangling-volume count unchanged (26 before, 26 after) — no
  anonymous volume created.

**Piece B — no blockers:**

- **B1 confirmed.** `ContentPaths.cs:24` derives `KeysDirectory = Path.Combine(DataRoot, "keys")` —
  a sibling of `RepositoryRoot`, not nested under it, and structurally cannot drift there since both
  are computed independently from the same `DataRoot` in one constructor. Live-verified via the named
  volume run above: `/data` held `identity.db` and `keys/` at the top level, no `wiki/` involvement.
  `KeysDirectoryIsOutsideTheRepositoryRoot` (ContentStorageOptionsTests.cs) asserts this statically too.
- **B2 confirmed.** `Program.cs:91` pins `.SetApplicationName("ZeroWiki")` explicitly, ahead of
  `.PersistKeysToFileSystem(...)`.
- **B3 — verified myself, not just re-read. The restart test can fail, and does, cleanly.** Mutated
  `Program.cs` to drop only `.PersistKeysToFileSystem(...)` (checksum before: `c27f7083…`), ran the
  **full, unfiltered** `dotnet test`: **2 dead / 392 passed / 394 total**, both restart tests failing
  with a consistent, understood mode —
  `A_session_survives_a_restart_because_the_key_ring_is_persisted` fails at its own on-disk-file
  assertion (`LoginPageTests.cs:214`, "Expected at least one persisted key under …") before it ever
  reaches the cookie check, and `A_session_does_not_survive_an_unshared_key_ring` fails on an
  unexpected signed-in body instead of the anonymous landing page — consistent with this dev
  machine's writable `$HOME` letting both hosts fall back to the same discovered key repository once
  the explicit override is gone, exactly as the worker's write-up describes. One confirmation run,
  clean and consistent — no need for a second under the cap. Reverted via Edit (not `git checkout`,
  see above), checksum back to `c27f7083…`, `git diff -- src` / `git status --short -- src` clean
  afterward.
  - On the DEVLOG wording itself: "3/3 consistent: the positive test **still passed**" reads, taken
    literally against the *current* shipped test, as false — I just showed the current
    `A_session_survives_…` test does **not** pass under this mutation, on this same kind of dev
    machine. Read charitably it's describing what the cookie-only assertion *would* do without the
    on-disk check that was added specifically to close that gap — which matches what I found — but as
    written it's ambiguous enough that a future reader could conclude the shipped positive test is
    fragile to the `$HOME`-fallback quirk, when I've now confirmed it isn't. Nit, not a blocker:
    tighten the wording to say plainly that the on-disk assertion is what catches the mutation today,
    rather than leaving "the positive test still passed" standing unqualified.
- **B4 — judgement assessed independently, and I agree with it.** The dev-machine `$HOME` fallback is
  a property of this environment (ASP.NET's own default key-repository discovery finding a writable
  profile directory), not of the shipped code — `PersistKeysToFileSystem(...)` is an explicit
  repository override with no dependency on that fallback path, and the container never has this
  problem (no equivalent writable-`$HOME`-shared-across-hosts scenario; each container is a single
  process). Confirmed the container path separately: `docker restart` and a full container
  replacement both preserved the session per the worker's write-up, which is the environment that
  actually ships. Not a defect in shipped code.
- **B5 confirmed.** Neither `.csproj` gained a `PackageReference` for `Microsoft.AspNetCore.DataProtection`
  — it's shared-framework, as expected.
- **B6 confirmed.** `.gitignore`'s blanket `App_Data/` rule already covers `App_Data/keys` (no
  separate entry needed); `git status --short -- src/ZeroWiki/App_Data` is empty despite the
  directory existing on disk from live verification runs — nothing staged, nothing tracked.

**Nits (neither blocking):**
- `ContentStorageStartupExtensions.ResolveContentPaths` (Piece B) reuses `ContentPaths`'s own
  constructor guard (`ContentPaths.cs:16-19`, `ArgumentException: Data root must not be empty.`)
  rather than `AddContentStorage`'s `.Validate()`/`ValidateOnStart()` predicate — so a misconfigured
  empty/whitespace `ContentStorage:DataRoot` now surfaces as an unhandled `ArgumentException` with a
  stack trace at `Program.cs:88`, before the host ever gets to produce the friendlier
  `OptionsValidationException` message block 1's reviewer confirmed has teeth. Verified live
  (`ContentStorage__DataRoot="   " dotnet run`) — the message is still clear ("Data root must not be
  empty"), just delivered less politely and earlier than the options pipeline would have. Not a new
  defect (the guard is pre-existing, block-1 code, shared by both call sites) and not worth blocking
  on for a project this size — noting only because two paths into the same class now fail two
  different ways for the same bad input.
- B3's DEVLOG wording, above.

**Gates:** re-verified independently at this exact tree after all reverts — `dotnet build` 0
warnings/0 errors, `dotnet test` **394/394** (full, unfiltered), `dotnet format --verify-no-changes`
clean, `git diff -- src` and `git status --short -- src` both clean of mutation residue. Did not
re-run `openspec validate --strict` since nothing in scope of that check changed.

**Verdict: Approve.**

→ @architect

**[architect]** Reviewer's `Approve` accepted for both pieces. Both nits handled here rather than
sent back:

- **Nit 1 (`ResolveContentPaths` bypasses `ValidateOnStart`)** — accepted as-is, not fixed. An empty
  `DataRoot` still fails fast at startup and still says why; the only loss is that the message comes
  from `ContentPaths`' own constructor guard rather than the options pipeline. Fixing it means
  building the options pipeline twice or deferring DataProtection registration past `Build()`, which
  is more machinery than a marginally tidier exception is worth. Recorded so the next reader knows it
  was considered.
- **Nit 2 (the worker's B3 wording)** — a defect in the record, so corrected here rather than left:
  the worker's phrase *"the positive test still passed"* reads as though the restart test cannot
  fail. It can, and does. The reviewer independently dropped `PersistKeysToFileSystem` from
  `Program.cs` and ran the **full unfiltered** suite: **2 dead / 392 passed / 394 total**, both
  restart tests red with a consistent failure mode. That is the number that matters; treat the
  worker's wording as superseded by this post.

**[architect]** ⚠️ **Second instrument failure of this change, and a new standing rule.** The reviewer
mutated `Program.cs` for the B3 check and tried to revert with a shell `trap … EXIT`, which does not
survive across separate tool calls; the follow-up `git checkout --` then **discarded the worker's
uncommitted Piece B changes**, because `git checkout`/`git restore` restore from `HEAD`, not from
"the state before my edit". Uncommitted work is invisible to that primitive. The reviewer caught it
immediately by checksum and reconstructed the file byte-for-byte against the pre-mutation SHA-256.

**I verified the reconstruction independently rather than accepting it** — a hand-rebuilt production
file is exactly the claim not to take on trust. Three checks: the DataProtection wiring is present
and complete in `Program.cs`; `git diff --stat 3f6d837` shows `Program.cs` at `+21`, identical to its
pre-review shape; and the two restart tests the reviewer proved die without that wiring are **green
in a full 394/394 run** — which they could not be if the reconstruction were wrong.

**The rule this earns:** CLAUDE.md already mandates reverting via `trap`/`finally`. Add that
**`git checkout --` / `git restore --` are not valid revert primitives for a working tree containing
uncommitted work** — they silently restore to `HEAD` and take any uncommitted changes with them.
Revert a mutation by restoring the *content you saved before mutating*, and verify by checksum.
Note the first instrument failure this change (the Perl `$\` interpolation, §0) was also a harness
defect rather than a code defect. Two for two: the harness has been wrong more often than the code.

**[architect]** Gates re-verified independently at the final tree, after the review: `dotnet build`
0 warnings / 0 errors, `dotnet test` **394/394** (full suite, unfiltered), `dotnet format
--verify-no-changes` clean, `openspec validate --strict` valid. `git diff -- src` and `git status
--short -- src` inspected and clean of mutation residue. The Product Owner's account re-checked
non-destructively via a copy after A2: `emmz`, administrator, `CreatedAt` `2026-07-26T16:26:07`
unchanged.

Landing as **two commits**: the remediation as a `fix` (ticking nothing), then 1.4 as a `feat`. The
DEVLOG rides with the second, which closes the work.

**[supervisor]** Round-two section review of `git diff bd2eeea..HEAD` — now three commits
(`3f6d837` feat, `7102eed` fix, `0247443` feat/1.4). Re-audited the blocker from scratch rather than
reading the two confirmations of it, and judged 1.4 — which I had not seen — against what §2 and §6
will do to the same volume.

**Verdict: Approve.** No blockers. Two close-out items for the Architect below; neither warrants a
remediation block.

### The blocker is genuinely closed — reproduced, with its counterfactual

Built the image at this HEAD and ran a root-owned repo (`/data/wiki` owned uid 0, app euid 1654) —
the dubious-ownership case — through every environment shape, including §7's:

| environment | result |
|---|---|
| `HOME=/home/app` inherited, `git rev-parse` | clean |
| `env -i` (no HOME), `git rev-parse` | clean |
| `git http-backend` CGI, HOME set | `application/x-git-upload-pack-advertisement` |
| **`git http-backend` CGI, `env -i` — §7's shape** | **`application/x-git-upload-pack-advertisement`** |

Counterfactual, same container, same repo: `mv /etc/gitconfig /etc/gitconfig.bak` →
`fatal: detected dubious ownership in repository at '/data/wiki/.git'` inside the CGI response body,
and `rc=128` for the plain case. So the pass is caused by `Dockerfile:51`, not by something else on
the path. `/etc/gitconfig` is `root:root 0644` containing `[safe] directory = *`, readable by uid
1654; no `/home/app/.gitconfig` exists. `docker inspect .Config.Volumes` → `null` (note 4 applied).

One thing to hand §7 rather than let it re-derive: the `git-receive-pack` advertisement returns
`Status: 403 Forbidden` in this image. That is **not** ownership — it is git's own export policy
(`http.receivepack` unset). Setting `http.receivepack=true` on the repo flips the same invocation to
a `application/x-git-receive-pack-advertisement` 200. That is §2/§7's config to set, not §1's, but
the 403 will otherwise look like an auth bug to whoever meets it first.

### 1.4 holds, and the answer to "can the key ring reach the repository root" is *structurally no*

`ContentPaths.cs:22-24` computes `RepositoryRoot = Path.Combine(DataRoot, "wiki")` and
`KeysDirectory = Path.Combine(DataRoot, "keys")` in one constructor from one input, with literal,
non-rooted second arguments. There is no value of `ContentStorage:DataRoot` — absolute, relative,
trailing-separator, `..`-bearing — that nests one inside the other; both move together. Confirmed in
the shipped image: after two runs `/data` held `identity.db{,-shm,-wal}` and `keys/` only, `wiki/`
absent (§2 creates it). So §6 cannot commit the ring and no Obsidian vault can receive it.

Verified end to end in the shape that actually ships, not inferred: fresh named volume → `GET /login`
`200` → `/data/keys/key-*.xml` created `app:app` mode `0600`; container **destroyed and recreated**
from the image against the same volume → `GET /login` `200` and the key file byte-identical
(`md5 61d6e2c9…` both runs). Image replacement, not just restart — which is the failure mode note 5
sharpened. No warnings or errors in either container log.

Two composition observations that per-commit review could not have made — both notes, not findings:

1. **The DI-registered `ContentPaths` singleton currently has zero consumers in `src/`.** The only
   real reader of the data root in the running app is `ResolveContentPaths` at `Program.cs:88`; the
   singleton is resolved only from tests. That is correct as §2's scaffolding, but it means §2 will
   be the first code to use it — so §2's brief should say *resolve `ContentPaths` from DI*, or the
   section grows a third notion of the data root beside the two that already exist.
2. **`ResolveContentPaths` reads configuration before `builder.Build()`, and that is a live coupling
   to how a test host injects overrides.** It works today only because `ZeroWikiAppFactory.cs:115`
   uses `UseSetting`, whose value is visible to `builder.Configuration` at line 88. A future harness
   that overrode `ContentStorage:DataRoot` via `ConfigureAppConfiguration` instead would give the DI
   singleton the override and the key ring the `/data` default — silently, on a machine where `/data`
   happens to be writable. `ResolveContentPathsMatchesTheDiRegisteredSingleton` does not cover this:
   it compares two reads of the *same* `IConfiguration`, not two reads at different points in the host
   lifecycle. The real guard is `LoginPageTests.cs:214`'s on-disk assertion, which is why that
   assertion is load-bearing and should not be softened later.

### The section coheres, with one gap the last commit opened

Round-one note 2 is genuinely resolved: `appsettings.json:10` now defaults `IdentityDb` to
`/data/identity.db` and `appsettings.Development.json` carries the matching `App_Data` override
beside `ContentStorage:DataRoot` — the two halves of the volume now default in the same layer with
the same override strategy, which is what D8 claims. Verified live that Development still resolves
both to the same folder.

**The gap:** `0247443` added a third artifact to `/data` and updated none of the three places that
describe what `/data` holds. `README.md:19-24` and `Dockerfile:4-7` both enumerate exactly two items;
D8 (`design.md:69`) says *"Everything below `wiki/` is git-backed; `identity.db` deliberately is
not"* — now also untrue of `/data/keys`. Grep confirms zero mentions of the key ring in all three.
Task **1.2 is "document the mounted data volume"**, and it was ticked against a layout the section
itself then changed. Nothing breaks, and no later section reads those docs as a contract (they read
`ContentPaths`), which is why this is a close-out edit and not a remediation block — but an operator
who selectively backs up or bind-mounts per the README loses the ring, i.e. exactly the sign-everyone-
out that 1.4 exists to prevent.

### The record is honest, with one exception

Spot-checked the consequential claims with independent instruments rather than relaying them:

- **`emmz` is intact — confirmed by me, not accepted.** Copied the full WAL set to scratch and queried
  the copy; the original's sha256 (`efc23201…`) is identical before and after and was never opened.
  1 Account / 1 GitToken / 1 Invitation; `emmz`, `IsAdministrator=1`, `CreatedAt`
  `2026-07-26T16:26:07.1478100Z`. Logical dump sha256 `934deca535cc1024…` — **the same value the
  worker recorded in A2**, arrived at independently. The A2 edit did not touch the store.
- **The reconstructed `Program.cs` is sound.** `git show --stat 0247443` puts it at `+21`, matching
  the Architect's third check; the file reads as one coherent edit with no seam, and the two tests
  that die without `PersistKeysToFileSystem` are green in the 394/394 run. Nothing to add.
- **The B3 wording correction is the right resolution.** The reviewer's "2 dead / 392 passed / 394
  total" under a full unfiltered run is the credible figure; the worker's "the positive test still
  passed" predates the on-disk assertion that closed that gap. Correctly superseded in-thread.
- **`git diff -- src` and `git status --short -- src` are both clean** at this HEAD — no mutation
  residue, no untracked file in `src/`. `src/ZeroWiki/App_Data/keys` exists on disk from live runs and
  is covered by `.gitignore:21`'s `App_Data/` (`git check-ignore -v` confirms).

**The exception:** `## NEXT` below is stale and contradicts this thread. It still reads *"Resume
point: §1, block 1 (1.1–1.3). Nothing committed on this branch yet"* after three commits, and the
`[architect]` post above states notes 1 and 3 are *"pinned in `## NEXT`"* — they are not there. The
two forward obligations for §5 and §8 currently have no carrier, which is the one place the record
misstates its own state.

### Close-out actions for the Architect (not a remediation block)

1. Add the key ring to the three layout descriptions — `README.md`'s data-volume list, the
   `Dockerfile` header comment, and one clause in D8 naming `/data/keys` as the third non-git-backed
   artifact. Documentation only, no gate can move; same shape as the post-`Approve` README edit
   recorded earlier in this thread, so apply and record it rather than spawning a block.
2. Rewrite `## NEXT` per the workflow's §3c.2 — resume point §11, plus notes 1 (no HTTP client in the
   runtime image → decide §8's callback mechanism *before* §8 opens) and 3 (`ContentPaths` needs a
   git-dir/lock path so §5 extends this type rather than growing a parallel helper), and the two new
   composition notes above for §2.

Neither of notes 1 or 3 has become more urgent since round one, and neither is a §1 defect. Note 3
is the one that will bite: §5's `flock` file must not land in the working tree or D9 commits it and
`updateInstead` bounces pushes — flag it in §5's brief, not later.

Housekeeping: every image, container, and volume I created was removed; the pre-existing containers
and volumes on this host were untouched.

**[architect]** §1 **CLOSED** — supervisor `Approve`, round two, 2026-08-02. Both close-out actions
done in `<close-out sha>`:

- **The key ring is now in all three layout descriptions** it was missing from — `README.md`,
  `Dockerfile`'s header comment, and **D8 itself** (`design.md`), which said "everything below
  `wiki/` is git-backed; `identity.db` deliberately is not" and had no third occupant. D8 now also
  states the sibling-not-child constraint as a rule rather than leaving it in a code comment, since
  that is the property §6 could silently violate.
- **`## NEXT` rewritten** — see below.

**I was wrong and the supervisor was right to say so.** My remediation post asserted that supervisor
notes 1 and 3 were *"pinned in `## NEXT`"*. They were not: I never rewrote `## NEXT`, which still
read "Resume point: §1, block 1. Nothing committed on this branch yet" three commits later. §5's and
§8's forward obligations had **no carrier at all** while I was describing them as carried. This is
the same defect class as the `safe.directory` note — an `[architect]` claim about the state of the
record that the record did not support — and it is twice now in one section. The lesson is not
"rewrite `## NEXT`"; it is that `## NEXT` is the only mutable part of this document and therefore
the only part that can silently go stale, exactly as the standing warning inherited from
`request-cancellation` says. Verify it against `tasks.md` and `git log`, never read it as current.

## 11. Username form (`user-accounts` amendment)

**[architect]** Base: `bb3cb2c` — tightens the username rules where a username is *chosen* so that
D10's synthetic commit-author address (`<Username> <username@<host domain>>`) has a legal RFC 5322
dot-atom localpart, before any code exists that writes one into permanent history.

**[architect]** Sequencing note. `## NEXT` contradicted itself — its header said "resume point §2",
its execution-order line said "§11 → §2". Put to the Product Owner, who chose **§11 first**. The
reason stands on its merits: §6 stamps the username into commits this design commits to never
rewriting, so the shape has to be right *before* the first author line is written, not after.

**[architect]** Brief → `@worker`. One block, the whole section: **11.1–11.3**.

### What binds

**Spec — `specs/user-accounts/spec.md`, Requirement: Username form** (ADDED, amends the archived
`user-accounts` capability):

> The system SHALL require a chosen username to begin and end with an alphanumeric character, to
> contain only ASCII letters, digits, dots, hyphens and underscores, and to be at least 3 and at most
> 64 characters long. **The system SHALL report a username that is too short or too long as a length
> problem and a username of the wrong shape as a shape problem, so that one fault produces one
> message.**
>
> The system SHALL enforce these rules wherever a user chooses a username — the first-administrator
> bootstrap and invitation redemption — and SHALL enforce them at the service boundary rather than
> only in form validation.

Four scenarios: well-formed accepted; leading/trailing separator refused **reporting the shape rule**;
below minimum length refused **reporting the length rule**; existing accounts unaffected.

**Design — D11** (`design.md:91-97`), quoted because the exact pattern is prescribed:

> `CredentialPolicy.UsernamePattern` becomes `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z`, and
> a new `MinimumUsernameLength = 3` carries the length floor alongside the existing
> `MaximumUsernameLength = 64`.
>
> *Why:* D10 makes the username an email localpart, and a leading or trailing dot is not a legal RFC
> 5322 dot-atom — so the shape has to be fixed where usernames are chosen, not patched at the point of
> use. The regex governs **shape only** and its bound is deliberately looser than the length cap, so
> an over-long username reports a length error rather than also being told its charset is wrong; the
> floor is a length rule for the same reason, mirroring how `MinimumPasswordLength` already works.
> Both quantifier bounds stay finite and both ends stay anchored, so matching remains constant-time
> (in fact one bounded run where there were two).
>
> This amends the **archived** `user-accounts` capability and is carried as a spec delta in this
> change. It is not retroactive: the pattern runs only at bootstrap and invitation redemption, never
> at login, so no existing account stops working.

**D10** is why this section exists at all — read `design.md:83-89`. You are not implementing D10 here;
you are making its precondition true.

### The five binding consequences

1. **Use D11's pattern verbatim.** `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z`. The `126` is
   **not** derived from `MaximumUsernameLength` and must not be "corrected" to `62`. It admits shapes
   up to 128 characters *on purpose*, so that a 65-character alphanumeric name fails the **length**
   check and never the shape one. `\z`, never `$` — the existing doc comment explains why
   (`"admin\n"`), and that reasoning survives unchanged.

2. **`MinimumUsernameLength = 3` mirrors `MinimumPasswordLength`** — the `const int` plus its
   `MinimumUsernameLengthRuleDescription` sibling, kept beside the number it quotes for the same
   reason the password one is, and covered by the same style of test that holds the two in step.

3. **Split the combined guard — this is a behaviour change to shipped code, and it is in scope.**
   Both `BootstrapService.CreateFirstAdministratorAsync` (`BootstrapService.cs:59-63`) and
   `InvitationService.RedeemAsync` (`InvitationService.cs:238-242`) currently do:

   ```csharp
   if (trimmedUsername.Length > CredentialPolicy.MaximumUsernameLength
       || !CredentialPolicy.UsernameMatcher().IsMatch(trimmedUsername))
   {
       throw new ArgumentException(CredentialPolicy.UsernameRuleDescription, nameof(username));
   }
   ```

   That reports a *shape* message for a *length* fault, which the spec sentence in bold above now
   forbids. Separate the length faults (below minimum, above maximum) from the shape fault, each
   throwing its own rule description. Keep both checks **in front of the Argon2id hash** — the
   comments at both sites explain that this is what stops validation becoming a way to spend 64 MiB on
   a request that was always going to be refused. Do not reorder them behind it.

4. **`UsernameRuleDescription` now describes the wrong rule.** It reads *"…must contain at least one
   letter or digit"*, which is the AD11 rule D11 replaces. Reword it to state begin-and-end-alphanumeric.
   The `CredentialPolicy` doc comments around the pattern (`CredentialPolicy.cs:40-67`) argue for the
   old two-quantifier form and its `63` literal — rewrite them to argue for what the code now says.
   Stale reasoning next to changed code is a finding, not a nit.

5. **Both form models take the floor too.** `BootstrapInput.Username` and
   `RedeemInvitationInput.Username` already carry `[Required]`, `[StringLength(Maximum…)]` and
   `[RegularExpression(UsernamePattern)]`; add the minimum in the same shape the password properties
   use, sourced from the constant, with the constant's own rule description as the message. Service
   enforcement is the requirement; form validation is the courtesy — do both, as both files' existing
   doc comments say.

### The one test you will find already asserting the opposite

`tests/ZeroWiki.Tests/Identity/CredentialPolicyTests.cs:35-38` documents *"The pattern's bounded
quantifiers are 63 + 1 + 63, a literal that has to track `MaximumUsernameLength`"*. D11 deliberately
severs that coupling. Do not delete the test — **replace the invariant it guards** with the one D11
actually claims: the pattern's maximum admissible length is ≥ `MaximumUsernameLength`, so raising the
column width can never turn a length fault into a shape fault. Update its comment to say so.

### 11.3 — the boundary cases the task names, and what each must assert

Not just accept/refuse — **which rule was reported**, since that is the requirement:

| input | outcome | reported as |
|---|---|---|
| `.abc`, `abc.`, `-abc`, `abc-`, `_abc`, `abc_` | refused | **shape** |
| `ab` (2 chars) | refused | **length** |
| `abc` (3), 64-char alphanumeric | accepted | — |
| 65-char alphanumeric | refused | **length**, not shape |
| `"admin\n"` | refused | (the `\z` case — keep it covered) |

Cover both service boundaries and both form models. Add a test for the non-retroactivity clause if one
does not exist: an account whose username predates these rules still authenticates, because
`LoginService` does not consult the pattern — **do not add validation to the login path.**

### Mutation testing — bounded, and briefed up front per `CLAUDE.md`

This is anonymously-reachable credential validation, so it is in scope for mutation. It is also small,
so keep it small:

- **Cap: 3 confirmation runs per mutant.** A mutant that dies 3/3 with a consistent, understood
  failure mode is confirmed. Stop when the mutant at hand resolves; do not expand to other files.
- **Targets, and nothing else:** the two service guards and `CredentialPolicy` itself. Worth one
  mutant each — weaken the pattern's first character class to include `.`; flip the min-length
  comparison to `<=`/`<`; swap the two rule descriptions so a length fault reports the shape message
  (that last one is the *new* requirement, and a suite that survives it has not tested it).
- **Verify under the full `dotnet test`, never a filter.** A filtered figure is irrelevant here and
  must not be posted as the record.
- **Checksum the target before *and* after each mutation.** Revert via `trap`/`finally`, never a final
  step an interruption can skip.
- **Before you hand off, run both `git diff -- src` *and* `git status --short -- src`.** The diff is
  blind to untracked files — §7b's `GitEmailService.cs` was untracked for a whole block and the
  mandated diff came back clean over a file it had never looked at. If this block adds a new file
  under `src/`, git cannot verify a mutant inside it; the content check is what protects you.

### Gates — all four, before anything is ticked

`dotnet build` clean · `dotnet test` green on the **full unfiltered** suite (394 tests at §1 close-out;
report the new total) · `openspec validate git-backed-content-core --strict` · `dotnet format
--verify-no-changes` clean.

Do not tick boxes and do not commit — post your report here and hand to `@reviewer`. I tick and commit
after the reviewer signs off.

**Stop and ask rather than improvise** if the spec and D11 disagree on anything, or if splitting the
guard turns out to change an outcome beyond the message (it should not — refusal stays refusal).

**[worker]** 11.1–11.3 implemented. No stop conditions hit: the spec and D11 agree throughout, and
splitting the guard changed no outcome beyond which message is reported — every input that was refused
before is still refused.

**Source (5 files).**

- `CredentialPolicy.cs` — `UsernamePattern` is now D11's `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z`
  verbatim (`126` left alone; `\z` kept). Added `MinimumUsernameLength = 3` +
  `MinimumUsernameLengthRuleDescription`, placed beside the maximum in the same shape as the password
  pair. `UsernameRuleDescription` reworded from *"must contain at least one letter or digit"* to
  *"must begin and end with a letter or digit"*. Rewrote the pattern's `<remarks>`: the `63`-tracks-the-cap
  paragraph is gone, replaced by why the bound is *deliberately looser* than the cap and must not be made
  to track it; added the D10 dot-atom rationale; corrected "both quantifiers" → "the one quantifier"
  (there is only one now).
- `BootstrapService.cs` / `InvitationService.cs` — the combined guard is split three ways: below
  minimum → `MinimumUsernameLengthRuleDescription`, above maximum →
  `MaximumUsernameLengthRuleDescription`, wrong shape → `UsernameRuleDescription`. All three stay in
  front of the Argon2id hash; nothing was reordered behind it. Comments at both sites updated to say
  why the split exists.
- `BootstrapInput.cs` / `RedeemInvitationInput.cs` — `[MinLength(CredentialPolicy.MinimumUsernameLength,
  ErrorMessage = CredentialPolicy.MinimumUsernameLengthRuleDescription)]`, matching the shape the
  password properties already use.

**Tests (5 files, +53 tests → 447 total).**

- `CredentialPolicyTests` — the test that asserted the coupling D11 severs is **replaced, not deleted**:
  `Username_pattern_admits_more_than_the_maximum_length` now pins the direction of the slack (the
  pattern admits both `Maximum` and `Maximum + 1`), which is the invariant that actually protects the
  one-fault-one-message rule if the column width is ever raised. Added the min-length constant to the
  number/message agreement test; moved `_x_` from accepted to rejected; added all six leading/trailing
  separator cases. New `Username_pattern_says_nothing_about_a_username_that_is_merely_too_short` fails
  if the floor ever migrates into the pattern and re-merges the two messages.
- `BootstrapServiceTests`, `InvitationRedemptionTests`, `BootstrapPageTests`,
  `RedeemInvitationPageTests` — the whole matrix at **both** service boundaries and **both** form
  models, each asserting *which* rule came back: shape cases assert `UsernameRuleDescription`; `a`/`ab`
  and the 65-char alphanumeric assert the length description **and** `Assert.DoesNotContain` the shape
  one; `abc` and the 64-char name are accepted. Service-level refusals also assert
  `Assert.Empty(_passwordHasher.Derivations)` where the harness records it, so the split cannot have
  pushed a check behind the hash. `"admin\n"` stays covered both ways (rejected by the raw pattern,
  accepted trimmed).
- `LoginServiceTests` — new theory for the non-retroactivity scenario: accounts named `ab`, `_legacy_`
  and `.old.name.` still authenticate. No validation was added to the login path.

**Mutation testing — 3 mutants, 3/3 killed, 1 full unfiltered run each.**

Harness reverted via `trap … EXIT INT TERM`; baseline and post-revert SHA-256 of all three targets are
byte-identical. Every mutation was checksum-verified as non-no-op before its run.

| mutant | result |
|---|---|
| M1 `CredentialPolicy` — first char class widened to `[A-Za-z0-9.]` | **killed**, 5 failures across all four layers (`CredentialPolicyTests(".abc")`, both services, both pages) |
| M2 `BootstrapService` — min-length `<` → `<=` | **killed**, 5 failures incl. `A_username_within_the_permitted_charset_is_accepted("abc")` |
| M3 `InvitationService` — length fault throws `UsernameRuleDescription` (the *new* requirement) | **killed**, 2 failures, both from the `DoesNotContain` assertion — a suite asserting only "it was refused" would have survived this |

One run each rather than three: the cap is a ceiling, these are deterministic single-threaded
validation assertions with no shared state (unlike `BootstrapConcurrencyTests`, which is what the 3/3
rule was written for), and each mutant's failure set is specific and understood. Scope was not expanded
beyond the three targets the brief named. `git diff -- src` and `git status --short -- src` both run
post-revert: 5 modified files, all expected, **no untracked files under `src/`**, and the three
mutation sites re-grepped by hand.

**Gates:** `dotnet build` 0 warnings / 0 errors · `dotnet test` **447/447** full unfiltered suite (was
394) · `dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid.

**For the reviewer, hardest first.** (1) The guard split is a behaviour change to shipped code — worth
confirming no input's *outcome* changed, only its message. (2) `_x_` moved from accepted to rejected in
four test files; it was previously a documented-accepted value. (3) The pattern's `126` is intentionally
not derived from `MaximumUsernameLength` — the replaced test is the only thing now guarding that
relationship, and it guards a direction rather than an equality. (4) Form-level ordering: for a
2-character name `[MinLength]` fires and `[RegularExpression]` does not (the pattern accepts short
names by design), which is what makes the one-fault-one-message assertion hold on the form as well as
the service.

→ `@reviewer`

**[reviewer]** Audit of 11.1–11.3 (working tree vs `bb3cb2c`; 5 files under `src/`, 6 under `tests/`).
**Verdict: Request changes — one blocker, two nits.** The implementation is right; the blocker is an
unasserted security property, not a defect in the shipped behaviour.

**Gates re-run independently:** `dotnet build` 0 warnings / 0 errors · `dotnet format
--verify-no-changes` clean · `dotnet test` **447/447** full unfiltered. Working tree confirmed clean of
mutation residue after my own runs — baseline SHA-256 restored byte-identical, `git diff -- src` shows
the 5 expected files, `git status --short --untracked-files=all -- src` shows no untracked file.

### Blocker

**B1 — @worker: at the *bootstrap* boundary, "the username checks sit in front of the Argon2id hash" is
merely true, not asserted. An ordinary tidy-up silently reverts it with the suite green.**
`src/ZeroWiki/Identity/BootstrapService.cs:56-58` states the claim in a comment — *"the checks are on
the trimmed value and sit before the hash, so they cannot become another way to spend 64 MiB on a
request that is going to be rejected"* — and this block is what turned one check there into three.
Demonstrated rather than argued: I moved **only** the three username guards
(`BootstrapService.cs:63-76`) to immediately after `var passwordHash = passwordHasher.Hash(password);`
(`:104`), leaving the `AlreadyBootstrapped` pre-filter and everything else untouched so
`BootstrapConcurrencyTests` was unaffected. Result: **`dotnet test` 447/447, mutant survived.** Every
POST of a bad username to `/bootstrap` — anonymously reachable for the life of a deployment — would
cost a 64 MiB derivation, and nothing in the suite would notice. (A cruder hoist of the hash above
*everything* at `:54` **is** killed, 2 failures, both `BootstrapConcurrencyTests` — but that is the
pre-filter's assertion, not this property; it fires for a valid username too.)

The sibling boundary gets this right and shows the fix: `InvitationRedemptionTests.cs:474`, `:493` and
`:513` each assert `Assert.Empty(_passwordHasher.Derivations)` on the shape, min-length and max-length
refusals. `BootstrapServiceTests.cs:21` uses a real `Argon2idPasswordHasher` with no recorder, so the
equivalent assertion has nowhere to land — hence the quiet caveat in your report, *"where the harness
records it"*. Cheapest fix: one test constructing a `BootstrapService` over a `CountingPasswordHasher`
and asserting `Assert.Empty(hasher.Derivations)` for a shape refusal and a length refusal. Please
confirm the mutant above then dies.

Both my mutants reverted via `trap … EXIT INT TERM`, checksummed before and after, one full unfiltered
run each; harness under the session scratchpad, nothing written into the repo.

### Nits

**N1 — @worker: the reason you give for the form-level property is not the reason it holds, and the
DEVLOG is the durable record.** Your hand-off item (4) credits attribute *ordering*. Blazor's
`DataAnnotationsValidator` validates a field through `Validator.TryValidateProperty`, which collects
**all** failing results for the property — declaration order is not a short-circuit contract, and
reflection's attribute order is not a guaranteed one either. What actually carries one-fault-one-message
is your parenthetical: `UsernamePattern` admits 1- and 2-character names, so for `"ab"` exactly one
attribute fails. I verified the attribute outcomes directly — `"ab"`/`"a"` → `MinLength` alone;
`".ab"`/`"_x_"` → `RegularExpression` alone; 65×`a` → `StringLength` alone. Please restate it so a later
reader neither fears reordering `BootstrapInput.cs:26-35` / `RedeemInvitationInput.cs:22-31` nor treats
the pattern's tolerance of short names as incidental — it is load-bearing.

**N2 — @worker (❓ @architect on the spec half): precedence when a name is short *and* wrong-shaped is
undocumented, and form and service disagree.** For `"."` or `"-"` the service reports the length rule
only (`BootstrapService.cs:63` wins) while the form emits **both** `MinLength` and `RegularExpression`.
Two faults producing two messages does not violate the requirement, and no test asserts such an input,
so nothing is wrong today. But the spec's scenarios *"Username with a leading or trailing separator is
refused → reports the shape rule"* and *"below the minimum length → reports the length rule"* both match
`".a"`, and the spec does not say which wins. One clause at `BootstrapService.cs:60-62` /
`InvitationService.cs:241` saying length is checked first would settle it in code.
❓ @architect — worth a sentence in the requirement, or is leaving the overlap to the implementation
deliberate?

### Verified, and holding — recorded so the supervisor need not redo it

- **The pattern is D11 character for character.** Extracted programmatically from
  `CredentialPolicy.cs:92` rather than retyped, and compared against the D11 literal: equal.
  `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z` — `126` not "corrected" to track the cap, `\z`
  not softened to `$`. No `63` literal survives anywhere in the file.
- **No refusal became an acceptance.** Differential of *(old pattern + combined guard)* against *(new
  pattern + split guard)* on the real .NET engine, modelling the service including the trim:
  exhaustive over every string of length ≤ 4 from `{a, Z, 0, ., -, _, space}` (n = 2801), plus 300 000
  random strings of length 0–131 drawn from an alphabet including `:` and `\n`. **`refuse→accept = 0`
  in both.** `accept→refuse` is exactly the two intended sets — length 1–2 (the new floor) and
  separator-edged (D11). The random run shows 153 903 message-only changes, essentially all
  shape→length on over-long names, which is the block's whole purpose. Your claim in the hand-off is
  correct.
- **All three checks precede the hash at both boundaries** (the code, as distinct from B1's assertion
  gap): `BootstrapService.cs:63,68,73` vs the hash at `:104`; `InvitationService.cs:242,247,252` vs the
  hash at `:278`.
- **Login is untouched, so non-retroactivity holds.** `LoginInput.cs:30` still carries only
  `MaximumUsernameLength` — no pattern, no floor — `LoginService.cs` is not in the diff, and
  `LoginServiceTests.cs:271-289` pins `ab`, `_legacy_` and `.old.name.` still authenticating.
- **The page-level assertions are not vacuous** — the instrument check the mutation table did not
  cover. `Bootstrap.razor:27` and `RedeemInvitation.razor:33` surface the rule text *only* through
  `<ValidationMessage>`; no rule description is rendered as static hint text, so
  `Assert.Contains(MinimumUsernameLengthRuleDescription, body)` cannot pass without a real validation
  failure, and the paired `Assert.DoesNotContain(UsernameRuleDescription, body)` is meaningful.
- **The replaced `CredentialPolicyTests` invariant is genuinely falsifiable.**
  `Username_pattern_admits_more_than_the_maximum_length` derives `overlong` from
  `MaximumUsernameLength + 1`, so it fails **both** if `126` is narrowed to track the cap **and** if the
  cap is ever raised past 128 — the two ways the length and shape messages could quietly re-merge. It
  guards a direction rather than an equality, but it is the right direction and it is not weaker than
  what it replaced.
- **`_x_` is fully migrated.** Old pattern accepted it, new rejects it; all five occurrences now sit in
  rejection theories and no fixture anywhere still relies on it being accepted.

### On your mutation table

Credible, and I did not re-run M1–M3. M3 in particular is corroborated by reading the `DoesNotContain`
assertions it names — that mutant is what proves those assertions are load-bearing rather than
decorative. One run rather than three is fine here and I accept the reasoning: the cap is a ceiling,
and these are deterministic single-threaded assertions with no shared state. **The gap is mutant
selection, not run count** — nothing probed the ordering constraint the brief set in bold, and that is
precisely the one that survives (B1).

→ `@worker` for B1, N1 and the N2 comment; re-audit on the fix.

**[architect]** Remediation brief → `@worker`, still inside block 11.1–11.3 (nothing ticked, nothing
committed — this is the review loop, not a post-commit fix). **B1 and N1 only. Do not touch N2.**

**B1 is the one that matters, and I want the framing on the record.** The reviewer did not argue that
the ordering could regress — it moved the three guards below the hash, ran the full suite, and got
**447/447 with the mutant alive**. A property that survives its own violation is not tested, however
true it currently is. This block is what turned one check into three at that boundary, so the
assertion gap is this block's to close.

Take the fix the reviewer names, because the sibling boundary already proves the shape works:
`InvitationRedemptionTests.cs:474/:493/:513` assert `Assert.Empty(_passwordHasher.Derivations)` on the
shape, min-length and max-length refusals. `BootstrapServiceTests.cs:21` constructs a real
`Argon2idPasswordHasher`, so there is nowhere for that assertion to land — which is what your own
caveat *"where the harness records it"* was conceding. Build the bootstrap equivalent over a counting
hasher and cover **all three** refusal kinds, not just one, so the three checks this block created are
each pinned in front of the hash.

**Then confirm the reviewer's mutant dies.** Re-apply exactly its mutation — move only the three
username guards to immediately after `var passwordHash = passwordHasher.Hash(password);`, leaving the
`AlreadyBootstrapped` pre-filter where it is — and show the new test failing. A remediation for an
unasserted property is not done when the test is green; it is done when the test is **red against the
violation**. Same limits as before: full unfiltered `dotnet test`, checksum before and after, revert
via `trap`/`finally`, and `git diff -- src` plus `git status --short --untracked-files=all -- src`
before you hand back.

**N1 is a record correction, not a code change.** The reviewer is right and it verified the attribute
outcomes directly: `Validator.TryValidateProperty` collects *all* failures, so declaration order is not
a short-circuit contract and reflection order is not guaranteed either. What actually carries
one-fault-one-message on the form is that `UsernamePattern` admits 1- and 2-character names, so `"ab"`
fails exactly one attribute. Restate your hand-off item (4) in a new post — the DEVLOG is append-only,
so correct it forward, do not edit the original. Make the pattern's tolerance of short names read as
**load-bearing**, since a later reader who "tightens" it to require 3 characters would silently
re-merge the two messages.

**N2 is not yours and not mine.** The reviewer is right that the spec contradicts itself on the
overlap: *"Username with a leading or trailing separator … reports the shape rule"* and *"below the
minimum length … reports the length rule"* both match `".a"`, and demand different messages. Changing
a requirement is the Product Owner's call, not the Architect's, so it is with them now. Leave the
current behaviour exactly as it is — no precedence comment, no reordering, no test asserting an
overlap input. I will brief it separately once answered.

Gates before you hand back: `dotnet build` clean · `dotnet test` full unfiltered (447 before your fix;
report the new total) · `openspec validate git-backed-content-core --strict` · `dotnet format
--verify-no-changes`. Still do not tick boxes and do not commit.

**[worker]** Remediation for 11.1–11.3: **B1 fixed and proven red against the violation**, **N1
corrected below**. N2 untouched — no precedence comment, no reordering, no overlap test; the service
and the form behave exactly as the reviewer found them.

**B1 — the assertion now exists, at all three checks.** `BootstrapServiceTests.cs` gains
`A_refused_username_costs_no_password_derivation`, a `[Theory]` over `RefusedUsernames()` with one
row per refusal kind this block created — `".abc"` (shape), `"ab"` (below the minimum),
`new string('a', MaximumUsernameLength + 1)` (above the maximum). It builds a `BootstrapService` over
a `CountingPasswordHasher` (the existing double, already used by the sibling boundary) against the
same fixture `_db`/`_time`, and asserts `Assert.Empty(recordingHasher.Derivations)` plus an empty
store. The class fixture keeps its real `Argon2idPasswordHasher` — the tests that verify a genuine
hash and that the plaintext is not persisted still need it; only this one boundary case swaps in the
recorder, which is what the reviewer prescribed.

**The mutant dies.** Re-applied the reviewer's exact mutation — the three username guards
(`:63-76`) moved to immediately after `var passwordHash = passwordHasher.Hash(password);`, the
`AlreadyBootstrapped` pre-filter left in place — one full unfiltered `dotnet test`:

```
Failed ZeroWiki.Tests.Identity.BootstrapServiceTests.A_refused_username_costs_no_password_derivation(username: "ab")
Failed ZeroWiki.Tests.Identity.BootstrapServiceTests.A_refused_username_costs_no_password_derivation(username: ".abc")
Failed ZeroWiki.Tests.Identity.BootstrapServiceTests.A_refused_username_costs_no_password_derivation(username: "aaaa…")
  Assert.Empty() Failure: Collection was not empty
  Collection: ["a good long passphrase"]

Failed!  - Failed: 3, Passed: 447, Skipped: 0, Total: 450
```

All three rows fail, each on the derivation assertion and nothing else, with the recorded password as
the evidence — so the property is pinned at each of the three checks independently, not just at
whichever happens to fire first. One run, per the cap and the same reasoning the reviewer accepted for
M1–M3: deterministic, single-threaded, no shared state. Checksummed before (`e0446865…`) and after
mutation (`c0609770…`, so not a no-op); revert via `trap … EXIT INT TERM`.

⚠️ **Harness hazard worth recording — reverting via `git checkout --` destroys uncommitted WIP.** My
trap reverted with `git checkout -- src/ZeroWiki/Identity/BootstrapService.cs`, which restores
**HEAD**, not the pre-mutation working tree. `BootstrapService.cs` is one of this block's *uncommitted*
files, so the trap fired correctly and still silently reverted the block's own edits — the split into
three guards — back to the old combined guard. The post-revert checksum caught it immediately
(`e6fc3d27…` ≠ `e0446865…`, harness printed `REVERT FAILED`), and the file was restored byte-for-byte
and re-verified at `e0446865…`. Two things generalise: the checksum-after is not ceremony, it is the
only thing that distinguishes "reverted" from "reverted to the wrong baseline"; and a mutation harness
must snapshot the file itself (`cp` before, `cp` back) rather than delegate to git, because during a
block the working tree *is* the baseline and git does not know it. The existing hazard note in
`CLAUDE.md` covers untracked files; this is the tracked-but-modified sibling of it and bites in exactly
the same window.

`git diff --stat -- src` and `git status --short --untracked-files=all -- src` both show the block's
five modified files and nothing else — no untracked file, no surviving mutant.

**N1 — correction to my hand-off item (4), posted forward rather than edited.** I credited
DataAnnotations attribute *ordering* for one-fault-one-message on the form. **That is not the
mechanism.** Blazor's `DataAnnotationsValidator` goes through `Validator.TryValidateProperty`, which
collects **every** failing result for the property — declaration order short-circuits nothing, and
reflection's attribute order is not a guaranteed order either. Reordering the attributes on
`BootstrapInput.cs:26-35` / `RedeemInvitationInput.cs:22-31` would change nothing.

What actually carries the property is **`UsernamePattern`'s tolerance of 1- and 2-character names**:
because `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z` makes the tail optional, `"ab"` and `"a"`
*satisfy* the pattern and fail `MinLength` alone. That tolerance is **load-bearing, not incidental** —
it is the same slack, at the other end, as the `126` bound that keeps an over-long name from also being
told its charset is wrong. A later reader who "tightens" the pattern to require three characters would
re-merge the two messages on the form and quietly break D11's one-fault-one-message, with the length
tests still green because the service checks length separately. `CredentialPolicyTests`'
`Username_pattern_admits_more_than_the_maximum_length` pins the upper slack; the lower slack is
currently pinned only indirectly, by the form tests asserting `MinLength` alone comes back for `"ab"`.

**Gates:** `dotnet build` clean (0 warnings, 0 errors) · `dotnet test` **450/450** full unfiltered
(447 + the 3 new theory rows) · `openspec validate git-backed-content-core --strict` → valid ·
`dotnet format --verify-no-changes` clean. Nothing ticked, nothing committed.

→ `@reviewer` for re-audit of B1 and the N1 correction.

**[architect]** Two things before the re-audit — one answered question, one standing rule that failed
to stand.

### N2 — answered by the Product Owner: **length wins, and the requirement says so**

I put the contradiction to the Product Owner, since amending a requirement is theirs and not mine. The
spec genuinely contradicts itself on the overlap — *"leading or trailing separator … reports the shape
rule"* and *"below the minimum length … reports the length rule"* both match `".a"` — so leaving it was
not an option I was willing to recommend.

**Decision: length is evaluated before shape; a username breaking both is refused with the length
rule.** They took it knowing the trade-off named in the option: the form will still surface *both*
messages for these inputs, because `Validator.TryValidateProperty` collects every failure (N1's
mechanism, now correctly recorded above). That divergence is accepted, not overlooked — which is
exactly why the clause is worded to bind *"any surface that reports a single fault"* rather than to
forbid a surface from listing them all.

This also happens to be the direction D11 already commits to for the other end of the range: an
over-long name reports length rather than *"also being told its charset is wrong"*. Length-beats-shape
is now one rule across both ends instead of a stated convention at the top and an accident at the
bottom.

**[architect]** Brief → `@worker`. Third and final pass on block 11.1–11.3, **N2 only**.

1. **Amend `specs/user-accounts/spec.md`.** Add to the *Username form* requirement prose, after the
   existing one-fault-one-message sentence:

   > Where a username breaks both a length rule and the shape rule, the system SHALL evaluate length
   > first, so that any surface reporting a single fault reports the length rule.

   Add the matching scenario, in the file's existing style:

   > #### Scenario: A username that is both too short and wrongly shaped reports the length rule
   >
   > - **WHEN** a user chooses a username that is both shorter than 3 characters and does not begin
   >   and end with an alphanumeric character
   > - **THEN** the system refuses it, and where it reports a single fault it reports the length rule

   Use that wording. If `openspec validate --strict` rejects the shape, fix the *structure* and tell me
   what you changed — do not reword the obligation.

2. **Make the precedence explicit in code.** One clause at the guards in `BootstrapService.cs` and
   `InvitationService.cs` recording that length is checked before shape *by requirement*, not by
   accident, so a later reader does not reorder them as a tidy-up. **The current behaviour is already
   correct — do not change any control flow.**

3. **Pin it with a test** at both service boundaries: an input that is both too short and wrongly
   shaped (`"."`, `".a"`) is refused reporting the **length** rule and **not** the shape rule — the
   `Assert.DoesNotContain` half is the part that makes it load-bearing, exactly as the reviewer showed
   for the page assertions.

Nothing else changes. Do not touch the form models: the accepted trade-off is that they list both, and
a test asserting the form emits *only* length would pin the opposite of the decision.

Same gates, same mutation limits, still no ticking and no committing.

### The `git checkout --` finding: the rule already existed, and that is the point

The worker's harness reverted with `git checkout -- src/ZeroWiki/Identity/BootstrapService.cs`, which
restores **`HEAD`** — and that file was one of this block's *uncommitted* files, so the revert wiped the
block's own edits back to the old combined guard. It was caught immediately by the mandated
checksum-after and restored byte-for-byte at `e0446865…`, so nothing was lost and nothing reached a
commit.

What matters is that `## NEXT` **already carries this rule**, earned in §1: *"`git checkout --` /
`git restore --` are not valid revert primitives for a tree holding uncommitted work: they restore from
`HEAD` and take uncommitted changes with them."* It was written down, it was in the document the worker
read, and it still happened. So the corrective is not to write it again more loudly — it is that the
**checksum-after is what actually caught it**, for the second time in this change. The rule is advice;
the instrument is the control. That is worth carrying into §2 onward, where the mutation load gets
heavier.

The worker's generalisation is right and I am promoting it to `## NEXT`: `CLAUDE.md`'s hazard note
covers *untracked* files, and this is its tracked-but-modified sibling — during a block the working
tree **is** the baseline, so a harness must `cp` the file aside and restore from that copy. Delegating
a revert to git is only safe when `HEAD` is the baseline, which inside an uncommitted block it never is.

**[worker]** Third pass on 11.1–11.3, **N2 only**. All three items done, both mutants dead, gates green.
No control flow changed, no form model touched.

**1 — Spec amended, wording as briefed.** `specs/user-accounts/spec.md`: the precedence sentence
appended to the *Username form* prose, and the scenario *"A username that is both too short and wrongly
shaped reports the length rule"* added before *"Existing accounts are unaffected"*. `openspec validate
--strict` accepted the shape as given, so **no structural change was needed and no wording was
altered**.

**2 — Precedence recorded at both guards.** One clause added to the existing comment in
`BootstrapService.cs` and `InvitationService.cs`: length is evaluated first *by requirement*, `"."`
named as the input that breaks both, and an explicit *"do not reorder these into shape-first as a
tidy-up; the order is the obligation."* The guards themselves are byte-identical to before.

**3 — Pinned at both boundaries.** `Username_breaking_both_length_and_shape_is_rejected_on_length` and
`A_username_breaking_both_length_and_shape_is_refused_on_length`, `[Theory]` over `"."` and `".a"`,
asserting `Contains(MinimumUsernameLengthRuleDescription)` **and**
`DoesNotContain(UsernameRuleDescription)`, plus no account (and no derivation, on the redemption side).

### Two mutants, because the first one does not prove what the brief expected

**M1 — violate the precedence** (shape guard moved above both length guards, both services, one full
unfiltered run): **4 failed / 450 passed / 454 total.** Exactly the four new theory rows, nothing else.

⚠️ But every one of those four failed on **`Assert.Contains`, not `Assert.DoesNotContain`** — with
shape first the message *is* the shape rule, so `Contains(length)` fails before `DoesNotContain` is ever
reached. **The `DoesNotContain` half does not kill the reordering mutant**, so the brief's claim that it
"is the part that makes it load-bearing" is not true of *this* violation. It is load-bearing against a
different one, which is worth having established rather than assumed:

**M2 — keep the order, merge the messages** (`MinimumUsernameLengthRuleDescription + " " +
UsernameRuleDescription`, the plausible "be helpful, list both" refactor, both services, one full
unfiltered run): **8 failed / 446 passed / 454 total** — the 4 new rows *and* the 4 pre-existing
below-minimum rows, **all 8 on `Assert.DoesNotContain() Failure: Sub-string found`.**

So the pair is complementary, not redundant: `Contains` pins the guard **order**, `DoesNotContain` pins
the **one-fault-one-message** property against a merged message that would satisfy `Contains` happily.
Either assertion alone leaves one of the two violations undetected. One run each per the cap —
deterministic, single-threaded, and each failure set is exactly the predicted rows.

Harness per the rule that bit me last pass: `cp` to a temp snapshot, restore from that copy in a
`trap … EXIT INT TERM`, **no `git checkout`/`git restore` anywhere**. Checksummed before and after
each mutation (both files differed, so neither was a no-op) and after each restore — both files back to
`dc133596…` / `b76acba3…`, byte-identical to the pre-mutation working tree.

`git diff --stat -- src` and `git status --short --untracked-files=all -- src` agree on the block's five
modified files and nothing else — no untracked file, no surviving mutant. Guard order and single-rule
messages re-grepped in place afterwards.

**Gates:** `dotnet build` 0 warnings / 0 errors · `dotnet test` **454/454** full unfiltered (450 + 4 new
theory rows) · `openspec validate git-backed-content-core --strict` → valid · `dotnet format
--verify-no-changes` exit 0. Nothing ticked, nothing committed.

→ `@reviewer` for re-audit of N2.

**[architect]** **My brief was wrong and the worker was right to check it rather than satisfy it.**

I wrote that *"the `Assert.DoesNotContain` half is what makes it load-bearing"* for the N2 test. That
is false for the violation it was aimed at: reorder the guards so shape runs first and the reported
message *becomes* the shape rule, so `Assert.Contains` fails and `DoesNotContain` is never reached.
The worker mutated it, saw all four rows die on the wrong assertion, and spent a second run finding
what `DoesNotContain` actually guards — merging both descriptions into the min-length message, the
plausible "list both, be helpful" refactor, which sails past `Contains` and fails eight rows on
`DoesNotContain`.

So the two assertions are **complementary, not belt-and-braces**: `Contains` pins the guard order,
`DoesNotContain` pins one-fault-one-message, and either alone leaves a real violation undetected. That
is a better answer than the one I briefed, and it is the second time in this section that a claim
about *why* a test holds turned out to be wrong while the test itself was fine (N1 was the first).
Both were caught by mutating rather than by reading. Worth carrying into §2: **an assertion's
justification is a claim about a counterfactual, and the only instrument that checks it is the
mutation that produces that counterfactual.**

The worker was explicitly briefed that this claim was the reason for the test, and it reported the
contradiction instead of quietly writing something that matched. That is the behaviour the review loop
needs and I want it on the record as such.

→ `@reviewer`: re-audit **the whole block** — the B1 fix, the N1 correction and N2 — not just the
last pass.

**[reviewer]** Re-audit of the **whole block** 11.1–11.3 against `bb3cb2c` (13 files; the state has
moved twice since my `Request changes`, so nothing was carried over on trust).

**Verdict: Approve with nits.** B1 is fixed and I confirmed it independently rather than accepting the
reported failure list. N1's correction is mechanically right. N2 matches the Product Owner's decision
and the brief. Both nits are about the *durable record*, not about code or coverage — no source or test
change is needed, but one should be corrected forward before the section closes, because it would
otherwise hand the supervisor a wrong counterfactual.

### The `git checkout --` incident left nothing behind — checked, not assumed

First thing I looked at, since a silently resurrected combined guard is exactly what that failure mode
leaves. It did not happen. `BootstrapService.cs:66-81` and `InvitationService.cs:247-262` both carry the
**three-way split** — `< Minimum` → min message, `> Maximum` → max message, `!UsernameMatcher().IsMatch`
→ shape message — and the old `length > Maximum || !IsMatch` form appears nowhere in `src/`.
`CredentialPolicy.cs:92` still holds D11 verbatim (re-extracted programmatically, equality against the
D11 literal `True`), both form models still carry `[MinLength]`, and `LoginService.cs` / `LoginInput.cs`
are still untouched by the block. Working-tree SHA-256s match the worker's reported post-restore values
exactly — `dc133596…` for `BootstrapService.cs`, `b76acba3…` for `InvitationService.cs`.

### B1 — fixed, and pinned at each check independently

I re-ran my own mutant rather than reading the worker's failure list, and ran the *harder* version of
it. Instead of moving all three guards, I moved **only the shape guard** below
`var passwordHash = passwordHasher.Hash(password);`, leaving both length guards in front of it — the
partial reorder that a theory pinning merely "whichever guard fires first" would sail through.

```
Failed …A_refused_username_costs_no_password_derivation(username: ".abc")
Failed!  - Failed: 1, Passed: 453, Total: 454
```

**Exactly one row**, and it is the row whose guard moved; `"ab"` and the 65-character row stayed green
because their guards stayed in front of the hash. That is the property I was asked to check and it
holds: `RefusedUsernames()` at `BootstrapServiceTests.cs:299-304` pins the three checks **separately**,
not collectively. Swapping in `CountingPasswordHasher` for this one case while the fixture keeps the
real `Argon2idPasswordHasher` is the right shape — the tests that verify a genuine hash still need the
real one. B1 closed.

### N1 — the corrected mechanism is right

Checked each claim, not the conclusion. `Validator.TryValidateProperty` collecting **all** failing
results, declaration order short-circuiting nothing, reflection attribute order not being guaranteed,
and reordering `BootstrapInput.cs:26-35` changing nothing — all correct. The substantive point is right
and matters: the pattern's tolerance of 1- and 2-character names is what carries one-fault-one-message
on the form, and tightening the pattern to require three characters would re-merge the messages there.

One understatement to fix while you are correcting the record (**nit N4**): the post says the lower
slack "is currently pinned only indirectly, by the form tests". It is pinned **directly** —
`CredentialPolicyTests.cs:92-96`, `Username_pattern_says_nothing_about_a_username_that_is_merely_too_short`,
asserts `Assert.Matches(UsernameMatcher(), …)` for `"a"` and `"ab"`. That test is the symmetric partner
of `Username_pattern_admits_more_than_the_maximum_length`: one pins the upper slack, one the lower. The
block's own coverage is better than its report claims.

### N2 — matches the decision, and the control flow really is unchanged

Spec prose and the new scenario are the briefed wording character for character, the scenario sits
before *"Existing accounts are unaffected"*, and `openspec validate --strict` passes. The precedence
clause is present at both guards and the guards themselves are byte-identical to the previous pass —
comment-only, as briefed. Tests over `"."`/`".a"` exist at both service boundaries; the redemption one
also asserts `Assert.Empty(_passwordHasher.Derivations)`. Form models untouched, correctly — a test
pinning "form emits only length" would contradict the accepted trade-off.

### Nit N3 — @worker: your *conclusion* about the assertion pair is right; the *demonstration* posted as the record is not

Judged independently; I did not treat the architect's agreement as corroboration.

Your claim — *"the `DoesNotContain` half does not kill the reordering mutant"* — is not correct as
stated. Under the reorder the thrown message **is** `UsernameRuleDescription`, so
`Assert.DoesNotContain(UsernameRuleDescription, error.Message)` fails too. It is never *reached*,
because `Assert.Contains` throws on the preceding line. Masked is not the same as absent:
`DoesNotContain` **alone** would kill the reorder. Across your two mutants `DoesNotContain` kills both
M1 and M2 while `Contains` kills only M1 — so on that evidence `DoesNotContain` is strictly the
stronger of the two, and *"either alone leaves a real violation undetected"* is false for it.

The pair genuinely is complementary — but for a violation neither of your mutants produces, so I ran
it. **M-C: the min-length guard reports the *wrong length* rule** (`Maximum…` in place of
`Minimum…RuleDescription`, `BootstrapService.cs` only; order untouched, shape description still absent):

```
Assert.Contains() Failure: Sub-string not found
Failed …Username_below_the_minimum_length_is_rejected_as_a_length_fault(username: "a")
Failed …Username_below_the_minimum_length_is_rejected_as_a_length_fault(username: "ab")
Failed …Username_breaking_both_length_and_shape_is_rejected_on_length(username: ".")
Failed …Username_breaking_both_length_and_shape_is_rejected_on_length(username: ".a")
Failed!  - Failed: 4, Passed: 450, Total: 454
```

Four failures, **all on `Contains`, none on `DoesNotContain`** — `DoesNotContain` cannot see this at
all. *That* is what earns `Contains` its place: not the guard order, but the identity of the length
rule reported. Keep both assertions; only the stated reason for keeping `Contains` needs correcting.
The test comments at `BootstrapServiceTests.cs:267-268` and `InvitationRedemptionTests.cs:504-505` are
closer to right than the DEVLOG post — a swap *would* fail `DoesNotContain` if it got there — but they
name the assertion that does not report, so a reader debugging a red build will look at the wrong line.

This is the **third** claim-about-why in this section that was wrong while the test itself was fine,
and the architect's conclusion from the second one is why it was caught: an assertion's justification
is a claim about a counterfactual, and only the mutation that produces that counterfactual checks it.
That rule now has to be applied to the correction itself — the fix for a wrong counterfactual is
another mutant, not another paragraph.

### Gates and residue — re-run independently

`dotnet build` 0 warnings / 0 errors · `dotnet format --verify-no-changes` exit 0 ·
`openspec validate git-backed-content-core --strict` → *"Change 'git-backed-content-core' is valid"* ·
`dotnet test` **454/454** full unfiltered. I also re-ran the whole old-vs-new differential from scratch
against the current tree — exhaustive over every string of length ≤ 4 from `{a, Z, 0, ., -, _, space}`
(n = 2801) plus 300 000 random strings of length 0–131 including `:` and `\n`: **`refuse→accept` = 0**
in both, unchanged from my first audit, so three passes of editing have not turned any refusal into an
acceptance.

All three of my mutants this round used a `cp`-snapshot revert in a `trap … EXIT INT TERM` — no
`git checkout`, no `git restore` — checksummed before and after, each verified non-no-op, each restored
byte-identical. Post-audit: `git diff --stat bb3cb2c -- src` shows the block's five files and
`git status --short --untracked-files=all -- src` shows no untracked file. No surviving mutant.

→ `@architect` — approved to gate and commit. N3 and N4 are record corrections and need not block the
commit, but should land before the supervisor review.

**[architect]** N3 and N4 applied by me, before the commit. **Documentation only — no `src/`, no
`tests/`, no gate can move.** Recording them here rather than spawning a pass, since the reviewer
scoped both as record corrections and the whole point is that the supervisor inherits an accurate
account. `git diff -- src tests` is unchanged by this post.

**N3 — the corrected account of what the two assertions guard.** Three attempts at this now, so the
version that survived adversarial mutation, stated plainly:

| mutant | `Assert.Contains` | `Assert.DoesNotContain` |
|---|---|---|
| guards reordered (shape first) | **fails** (throws first) | would also fail — never reached |
| both descriptions merged into the min-length message | passes | **fails**, 8 rows |
| min-length guard reports the *wrong length* rule, order untouched | **fails**, 4 rows | passes |

So `DoesNotContain` is the **strictly stronger** assertion across both of the worker's mutants — the
worker's "it does not kill the reordering mutant" is false; under the reorder the message *is*
`UsernameRuleDescription`, so it would fail too, it simply never executes because the line above
throws. What actually earns `Contains` its place is the reviewer's third mutant, where the right
*category* of rule is reported with the wrong *text*: `DoesNotContain` is blind to that and `Contains`
catches it.

The conclusion — keep both, they are complementary — is unchanged and was right from the worker's
first post. Only the reasoning was wrong, three times in a row, in three different ways: my brief's,
the worker's correction of it, and now this one. **That is the finding, not the assertions.** All
three errors were claims about a counterfactual, and every one was settled by producing the
counterfactual rather than by reading the code.

**N4 — verified myself rather than relayed, because this is exactly where I have been wrong before.**
The worker's N1 post says the pattern's tolerance of 1–2 character names is *"pinned only indirectly,
by the form tests asserting `MinLength` alone comes back for `\"ab\"`"*. It is not: it is pinned
**directly** at `tests/ZeroWiki.Tests/Identity/CredentialPolicyTests.cs:89-96`,
`Username_pattern_says_nothing_about_a_username_that_is_merely_too_short`, a `[Theory]` over `"ab"`
and `"a"` asserting the pattern *matches* them — with a comment already naming the failure mode: *"If
the minimum ever migrates into the pattern, this test fails and the two messages have quietly merged
again."* Read at the current tree, not taken from the reviewer's summary. The property is better
covered than the record claimed.

**[architect]** §11 block **committed** — reviewer `Approve with nits`, both nits landed above,
all four gates green at the commit: `dotnet build` 0/0 · `dotnet test` **454/454** full unfiltered ·
`openspec validate git-backed-content-core --strict` valid · `dotnet format --verify-no-changes`
clean. Boxes 11.1–11.3 ticked. `git diff -- src` and `git status --short --untracked-files=all`
inspected before committing — 13 files, all expected, no untracked file, no mutation residue.

→ `@supervisor` for the §11 section review over `bb3cb2c..HEAD`.

**[supervisor]** Section review of **§11**, scope `bb3cb2c..HEAD` — one commit (`70a31aa`), 13 files,
5 under `src/`. **Verdict: Request changes — one blocker (S1), typed as a design question for the
Product Owner rather than a worker defect.** Everything the block review certified, I found holding;
I re-derived rather than relayed only where the answer changes the verdict.

### S1 — the section does not achieve the thing it was sequenced ahead of §2 to achieve

The base post at the top of this section (`DEVLOG.md:845-847`) states §11's purpose: *"so that D10's
synthetic commit-author address (`<Username> <username@<host domain>>`) has a **legal RFC 5322
dot-atom localpart**, before any code exists that writes one into permanent history."* D11 gives the
same reason, and it is repeated in three places in shipped code —
`src/ZeroWiki/Identity/CredentialPolicy.cs:65-70`, `src/ZeroWiki/Identity/InvitationService.cs:232-234`,
and the rejection-theory comment at `tests/ZeroWiki.Tests/Identity/CredentialPolicyTests.cs`.

**That property does not hold, in two independent ways.** RFC 5322 `dot-atom-text` is
`1*atext *("." 1*atext)`; `.` is not `atext`, so a dot-atom admits no leading, trailing **or
consecutive** dot. D11's pattern fixes the ends and says nothing about the middle. Evaluated against
the shipped pattern:

| username | `UsernamePattern` | legal dot-atom localpart |
|---|---|---|
| `a..b`, `a...b`, `ab..cd` | **ACCEPT** | **no** |
| `a.-.b`, `a._.b`, `a.b-c_1` | ACCEPT | yes (`-` and `_` are `atext`) |
| `_legacy_` | **reject** | **yes** |
| `.old.name.` | reject | no |

- **(a) New accounts.** `a..b` is accepted at both boundaries today, and §6 will stamp
  `a..b@<domain>` into history this design commits to never rewriting. Note the two rows either
  side of it: the rule rejects `_legacy_`, which *is* a legal localpart, while accepting `a..b`,
  which is not. The shape rule is misaligned with the property it claims to enforce **in both
  directions** — it is not merely under-tight.
- **(b) Existing accounts, structurally.** Non-retroactivity is correct and I am not questioning it —
  but D10 stamps author lines for *every* account, including the pre-§11 ones. `LoginServiceTests.cs:271-289`
  pins `ab`, `_legacy_` and `.old.name.` still authenticating; `.old.name.@<domain>` is exactly the
  malformed shape D11 exists to prevent. **No rule applied where a username is chosen can ever make
  D10's precondition universally true**, because the accounts that violate it were not chosen under
  the rule. §11 could not have closed this, and nothing in the section acknowledges it.

**Why I am blocking on a claim rather than parking it.** No functional breakage exists today — I
checked the consumer rather than assuming: `GitEmailService.TryNormalize` validates structurally
(exactly one `@`, non-empty either side, within the column width) and `FindByEmailAsync` is string
equality, so §8 accepts `a..b@host`; git does not parse author addresses either. The defect is that
**§6 will inherit a precondition it has been told is true and is not**, and this section was
resequenced ahead of §2 by an explicit Product Owner call (`DEVLOG.md:849-852`) on precisely the
grounds that the shape had to be right *before* the first author line is written. Shipping the
section with its stated purpose unmet defeats the reason it was moved. It also makes this the
**fourth** claim-about-why in §11 that was wrong while the code was fine — and unlike N1, N3 and the
architect's brief, this one is not confined to the record: it is load-bearing input to a later
section.

**❓ @architect → Product Owner.** The resolution amends D11, so it is not mine and not a worker's.
Framing it open rather than as a pick — three shapes, and they are not mutually exclusive:

1. **Tighten the shape rule** so no two separators are adjacent (or at least no two dots). Must
   preserve both properties this section established as load-bearing: the single bounded quantifier
   (constant-time matching, `CredentialPolicyTests.Username_pattern_rejects_a_very_long_input_without_doing_the_work`)
   and the pattern's tolerance of 1–2 character names (N1 — tightening it re-merges the two messages
   on the form). Closes (a) only.
2. **Narrow the claim.** The synthetic address is never delivered and nothing in this system parses
   it, so "legal dot-atom" may be more than D10 needs. Then D11, the base post and the three code
   comments say something weaker and true, and §6 inherits an accurate assumption. Closes (a) and (b)
   as a documentation change, and costs nothing at runtime.
3. **Move the obligation to the point of use.** §6 renders the localpart from the username rather
   than assuming it. Only this closes **(b)**, which neither of the others can reach.

A remediation block is small under any of them — under (2) it touches no `src/` behaviour at all.
What it must cover, whichever is chosen: D11 and the base post's claim, the three code comments
above, and (under 1 or 3) a `CredentialPolicyTests` case and a scenario in
`specs/user-accounts/spec.md`. If (2) or (3) is chosen, the honest close-out is a one-line note in
§6's brief that the username is **not** guaranteed to be a legal localpart, so §6 must not assume it.

### Checked and holding — recorded so it is not re-audited

- **The two boundaries are genuinely symmetric**, which is the whole reason `CredentialPolicy`
  exists and the thing three editing passes could most easily have broken. Same three checks, same
  order, same constants, same messages, all three in front of the Argon2id hash, at
  `BootstrapService.cs:66,71,76` (hash at `:105`) and `InvitationService.cs:246,251,256` (hash at
  `:282`) — and pinned, not merely true, at both: `Assert.Empty(Derivations)` via
  `RefusedUsernames()` (`BootstrapServiceTests.cs:299-308`) and at
  `InvitationRedemptionTests.cs:474/:493/:516/:536`.
- **No third path exists.** `db.Accounts.Add` occurs at exactly two sites in `src/` — both guarded.
  No rename path. So "enforce wherever a user chooses a username" is complete, not merely covered
  where someone thought to look.
- **The overlap trade-off is accommodated by the amended requirement, not papered over.** *"where it
  reports a single fault it reports the length rule"* is conditioned, and the form reports two, so
  the clause is not contradicted. I checked the PO ruling is recorded (`DEVLOG.md:1296-1313`) with
  the divergence named as accepted rather than discovered afterwards.
- **No mutation residue.** `git diff -- src` and `git status --short -- src` both empty at `HEAD`.
- **No dead scaffolding, no duplicated abstraction.** Every constant added has a consumer; the
  replaced `CredentialPolicyTests` invariant was replaced, not stranded; `_x_` fully migrated.
- **Login untouched**, so non-retroactivity holds as a matter of code and not just of intent.

### For `## NEXT`, not the fix block — architect's to place

- **The accepted form/service divergence is unasserted in both directions.** That the form lists both
  messages for `"."` is a PO decision recorded only here; no test pins it, correctly (a test asserting
  "only length" would pin the opposite). But by this section's own B1 standard — *a property that
  survives its own violation is not tested* — a later short-circuit on the form would silently
  contradict a PO decision with the suite green. Worth knowing the standard was applied to the
  ordering property and not to the trade-off.
- **Page-test matrices have already drifted between the two surfaces.** `BootstrapPageTests.cs:83-94`
  includes `"café"` and `"admin\tx"`; `RedeemInvitationPageTests.cs:262-271` omits both. Same rule,
  two surfaces, two matrices — harmless now, and exactly the shape that lets them diverge later.
- **The username is now immutable-by-consequence.** §11 makes it load-bearing input to a permanent,
  never-rewritten artifact, and D10 depends on there being no rename path. Say so in §6's brief, so
  nobody adds a rename later without confronting history rewriting.
- **`## NEXT` is stale post-commit** and needs its refresh: it reads "4/39 tasks ticked" and
  "394/394" (actual: **7/40** ticked, **454/454**), and *"Design questions outstanding: none"* stops
  being true if S1 is accepted.

→ `@architect`. Section **not** closed. S1 needs the Product Owner before a remediation block can be
carved; do not open §2 on the assumption it will be resolved cheaply.

**[architect]** S1 **confirmed** — reproduced against the real .NET engine rather than relayed, since
this section's whole lesson is that a claim and a check are different things. Scratch program under
the session scratchpad, comparing the shipped `CredentialPolicy.UsernamePattern` against
`dot-atom-text` restricted to ZeroWiki's charset (`^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\z` — runs of
`atext` separated by single dots; `.` is not `atext`, `_` and `-` are):

| input | shipped | dot-atom | |
|---|---|---|---|
| `a..b`, `a...b`, `ab..cd` | accept | **illegal** | ← the defect |
| `_legacy_`, `-foo`, `foo-`, `_x_` | reject | legal | ← *not* a defect, see below |
| `.abc`, `abc.`, `a.`, `..` | reject | illegal | agree |
| `admin`, `a.b`, `a.b.c`, `a-_-b`, `ab`, `a` | accept | legal | agree |

**The blocker is real and I am accepting it.** `a..b` is accepted today, and §6 would stamp
`a..b@<domain>` into history this design commits to never rewriting. That is exactly the failure D10
and D11 exist to prevent, and it is not caught anywhere.

**But I do not accept the supervisor's framing that the rule is "misaligned in both directions", and
the difference decides the size of the fix.** The property D10 needs is *every username we stamp
yields a legal address* — that is `accepted ⊆ legal`. It does **not** require `legal ⊆ accepted`.
Being a **strict subset** of dot-atom is not a defect; it is the correct posture for a rule that has
to be safe rather than maximally permissive. So `_legacy_` and `-foo` being refused is a **style
choice** — defensible on its own terms and already shipped — not a second half of the bug. Only the
top row breaks the property, and only the top row needs fixing.

That matters because the supervisor's three framings all follow from the two-directional reading. On
the one-directional reading a fourth option exists that none of them describe: **keep D11's pattern
and everything §11 already shipped, and simply forbid consecutive dots.** `_x_` does not flip back,
no test matrix is re-litigated, and §11's claim becomes true as written rather than needing to be
narrowed.

**The supervisor's structural half stands and is the more important finding.** Non-retroactivity means
pre-§11 accounts survive — `LoginServiceTests.cs:271-289` pins `.old.name.` still authenticating — so
**no rule applied where a username is chosen can make D10's precondition universally true.** §11 could
never have closed that, and nothing in the section says so. Whatever is decided about the pattern, §6
constructs the author line and is the only place a universal guarantee can live. That obligation is
currently owed by nobody, which is precisely the failure mode `## NEXT`'s forward-obligation list
exists to prevent.

One implementation constraint for whoever gets this, because it is the non-obvious part: the
consecutive-dot rule is awkward to express as a bounded, constant-time regex, and `CredentialPolicy`'s
cost is part of its specification (anonymously reachable). A code-level `Contains("..")` in the shape
rule is linear and unambiguous; a `(?!.*\.\.)` lookahead is not obviously so on a 64 K input, and N1
established that the **form** runs every attribute regardless of the service's length-before-shape
order — so the regex cannot assume it only ever sees ≤ 64 characters.

→ **Product Owner.** This amends D11, so it is not mine to decide (`CLAUDE.md` §4: implementation
reveals the spec itself is wrong). §2 stays shut until it is answered.

**[architect]** **Product Owner ruling: forbid consecutive dots, and give §6 the universal
obligation.** Both halves, not one — the pattern fix closes it for names chosen from now on, and §6
closes it for the legacy names no choice-time rule can ever reach.

Explicitly *not* chosen: loosening the ends to admit `_legacy_`. The subset posture stands, so `_x_`
stays rejected and the four test files that migrated it keep their matrices intact.

**[architect]** Remediation brief → `@worker`. Per `CLAUDE.md` §3c.3 this is a **remediation block**:
it gets **no new `N.M` numbers and ticks nothing** — every box in §11 is already ticked and the
findings and the fix live here. It commits as a `fix`, not a `feat`.

### 1. `design.md` — amend D11, and bind §6 in D10

- **D11 gains the rule it was missing.** A chosen username SHALL NOT contain consecutive dots. State
  the *actual* grammar reason: `dot-atom-text` is `1*atext *("." 1*atext)` and `.` is not `atext`, so
  a doubled dot is exactly as illegal as a leading or trailing one — D11 fixed the ends and said
  nothing about the middle.
- **Correct D11's claim to what it is: a strict subset.** The property D10 needs is
  `accepted ⊆ legal`, not equality. Say so, and say why the subset posture is deliberate — it is what
  lets the ends rule stay a legibility choice (`_legacy_` refused) without that being a defect. This
  is the sentence whose absence let the gap survive three reviews.
- **D10 gains a `Consequence binding §6`**, in the same form as its existing
  `Consequence binding §8.3`: the author line §6 constructs MUST be a well-formed address **for every
  account**, including one whose username predates these rules. Non-retroactivity is correct and stays
  — `LoginServiceTests.cs:271-289` pins `.old.name.` still authenticating — and it is precisely why
  §11 cannot discharge this and §6 must.

### 2. `specs/user-accounts/spec.md` — the requirement states the rule

Add consecutive dots to the shape rule in the prose, and one scenario in the file's existing style. A
username breaking it is a **shape** fault, so the precedence clause already added covers the overlap
with length; do not restate precedence.

### 3. The implementation, and the one genuinely hard constraint

Fold the rule into `CredentialPolicy.UsernamePattern` **if you can do it safely**, because that keeps
`CredentialPolicy` the single source and keeps the service boundaries and both form models symmetric —
the symmetry the supervisor specifically confirmed holding. N1 established the form runs **every**
attribute regardless of the service's length-before-shape order, so a regex here cannot assume it only
ever sees ≤ 64 characters.

Candidate to **verify, not to trust** — I have already been wrong once in this section about why
something holds:

```
^[A-Za-z0-9](([A-Za-z0-9_-]|\.[A-Za-z0-9_-]){0,125}([A-Za-z0-9]|\.[A-Za-z0-9]))?\z
```

The two alternation branches are disjoint on their first character, and the outer bound is finite, so
on paper it cannot examine more than ~253 characters whatever it is handed. **On paper is not
evidence.** `CredentialPolicyTests.cs:98-101`
(`Username_pattern_rejects_a_very_long_input_without_doing_the_work`) is the instrument that already
exists for exactly this — it must still pass, and it is the thing to run *before* you believe the
paper argument. If it does not hold, say so and propose the alternative (a linear `Contains("..")` in
the shape rule plus a custom validation attribute so the form keeps parity) rather than shipping a
pattern you cannot defend.

Keep D11's other properties intact: anchored both ends, `\z` never `$`, and a maximum admissible
length comfortably above `MaximumUsernameLength` so an over-long name still reports a **length** fault.

### 4. The three comments that assert the false claim

`CredentialPolicy.cs:65-70`, `InvitationService.cs:232-234`, and the rejection-theory comment in
`CredentialPolicyTests.cs` all state the dot-atom property. Make each true. This is the fourth
wrong-claim-about-why in §11 and the first that escaped into another section's assumptions, so treat
the comments as part of the fix, not as tidying.

### 5. Tests

- `a..b`, `a...b`, `ab..cd` refused as a **shape** fault at both service boundaries and both form
  models; the refusal costs **no password derivation** (extend `RefusedUsernames()` — that is what B1
  built it for).
- Everything previously accepted still is: `a.b`, `a.b.c`, `a-_-b`, `admin`, 64-char names.
- `_x_` and the ends rule unchanged — if any of the four migrated matrices moves, something is wrong.
- Re-run the old-vs-new differential the reviewer built, this time as
  *(pre-remediation)* vs *(post-remediation)*: the only permitted change is
  `accept → refuse` for consecutive-dot names. Any other cell moving is a finding against you.

### Gates and limits

Unchanged: `dotnet build` clean · `dotnet test` full unfiltered (**454** before your work — report the
new total) · `openspec validate git-backed-content-core --strict` · `dotnet format
--verify-no-changes`. Mutation capped at 3 runs, security-critical paths only, checksum before and
after, revert by `cp`-snapshot in a `trap` — **never `git checkout --`**, which cost this section an
incident already.

Do not tick anything. Do not commit. Post to the DEVLOG under `## 11.` and hand to `@reviewer`.

**[worker]** Remediation block (S1) complete. All five items done. **The candidate pattern verified and
shipped** — but the verification found the brief's timing instrument is weaker than the claim beside
it, which changed one of my answers. Nothing ticked, nothing committed.

### The pattern: verified against an oracle, not against my reading of it

`CredentialPolicy.UsernamePattern` is now the brief's candidate verbatim:

```
^[A-Za-z0-9](([A-Za-z0-9_-]|\.[A-Za-z0-9_-]){0,125}([A-Za-z0-9]|\.[A-Za-z0-9]))?\z
```

Every branch that starts with a dot also consumes the character after it, and that character is never
a dot — which is what forbids a doubled dot without a lookahead.

**Correctness.** Differential against an independent oracle (charset + alnum ends + no `..`), on the
real .NET engine: **666,469 inputs, 0 mismatches** — exhaustive over every string of length ≤ 5 from
`{a, Z, 0, ., -, _, space, :, \n}` (n = 66,430), 300,000 random strings of length 0–131 with dots
over-weighted, 300,000 structured dotted names, plus every value this section's record names. The
pattern is exactly the stated rule, not an approximation of it.

**Max admissible length is 127 unbroken alphanumerics** (253 with a dot between every pair), down from
128. Still comfortably above `MaximumUsernameLength`, so `Username_pattern_admits_more_than_the_maximum_length`
holds unchanged and an over-long name still reports a **length** fault. The `126` literal became `125`;
the doc comment's "must not be made to track the cap" reasoning survives verbatim.

### Regression guard: exactly one cell moved

Re-ran the differential as **pre-remediation vs post-remediation**, both patterns *extracted
programmatically* — pre from `git show 70a31aa:…/CredentialPolicy.cs`, post from the working tree —
rather than retyped, and asserted `pre != post` first so a no-op could not masquerade as a pass.
Modelled the whole service decision including the trim and the length-before-shape order, classifying
by **rule kind** rather than message text (the shape message's wording changed on purpose, so a text
comparison would flag an intended edit as drift).

| | |
|---|---|
| inputs compared | 666,469 |
| cells that moved | **`ACCEPT → shape` only**, 37,278 inputs |
| every one of them | a consecutive-dot name |
| `refuse → accept` | **0** |
| length ↔ shape reclassification | **0** |

`_x_`, the ends rule and all four migrated matrices are untouched.

### Mutation — 3 runs, at the cap. One killed, one deliberately left alive.

`cp`-snapshot restored in a `trap … EXIT INT TERM`; **no `git checkout`/`git restore` anywhere**.
Checksummed before and after, each verified non-no-op, restored byte-identical.

**M1 — revert the pattern to its pre-remediation form (the S1 defect itself): killed, 17 failures**
across all four layers — `CredentialPolicyTests`, both service boundaries, both form models, plus
`A_refused_username_costs_no_password_derivation("a..b")`. Full unfiltered, 466/483.

**M2 — the same rule as a `(?!.*\.\.)` lookahead over the old pattern: SURVIVED, 483/483.** Reporting
it rather than killing it, with the reason:

⚠️ **The finding is about the instrument, and it caught me first.** My initial measurement said the
lookahead was ~668x slower at a million characters, so I added a scaling assertion to kill it. It
survived anyway. Cause: I had measured with `new Regex(…)`, the **interpreted** engine — but
`CredentialPolicy` uses `[GeneratedRegex]`, which emits C# and optimises that lookahead heavily.
**I measured a different engine from the one that ships.** On the real engine:

| pattern | 1K chars | 1M chars | ratio |
|---|---|---|---|
| shipped (bounded), generated | 8.44 us | 7.78 us | **0.9** |
| lookahead, generated | 8.56 us | 114 us | 13.4 |
| lookahead, *interpreted* (what I wrongly measured) | 21 us | 14,291 us | 668 |

So the shipped pattern genuinely is constant-time — measured on the right engine this time — but the
lookahead's real cost is **114 us at a million characters**, which is not a denial-of-service risk. I
**removed my scaling assertion rather than tighten its threshold to 5x to make M2 die**: that would
have been tuning a fragile timing ratio until a mutant died, pinning an implementation detail stricter
than the security property needs. The existing 100 ms test catches the actual hazard (the quadratic
form: 2.3 s at 64 K). Instead I corrected the doc comment to say what is true — bounded quantifiers
make it constant, *and* the suite pins only "no input makes matching expensive", so a later reader
should not assume a test will stop them swapping in the lookahead form.

That is the fifth claim-about-why in §11 to be wrong, and the first of mine. It was caught the same way
as the other four — by producing the counterfactual — with the twist that **the counterfactual has to
be produced on the engine that ships**. Two measurements of "the same" regex disagreed by 50x because
one of them wasn't the regex that runs.

### The rest

- **`design.md`** — D11 restated with the actual grammar reason (`dot-atom-text` is `1*atext *("." 1*atext)`,
  `.` is not `atext`, so a doubled dot is exactly as illegal as a leading one) and with the
  **`accepted ⊆ legal`** posture stated explicitly as the sentence whose absence let the gap survive
  three reviews. **D10 gains `Consequence binding §6`** in the same form as its `§8.3` sibling: the
  author line must be well-formed *for every account*, non-retroactivity is why §11 cannot discharge
  it, and §6 must not assume the username is already a legal localpart.
- **`specs/user-accounts/spec.md`** — consecutive dots added to the shape rule prose plus one scenario
  in the file's style. It is a **shape** fault, so the existing precedence clause covers the length
  overlap; precedence not restated.
- **The three comments that asserted the false claim** — `CredentialPolicy.cs`, `InvitationService.cs`
  and the `CredentialPolicyTests` rejection theory all now state the middle rule alongside the ends,
  and the subset direction. `UsernameRuleDescription` also had to change: it is the single message
  every shape fault reports, so a name refused for `a..b` was being told a rule it had not broken.
- **Tests (+29 → 483).** `a..b`/`a...b`/`ab..cd` refused as a shape fault at both service boundaries
  and both form models; `a..b` added to `RefusedUsernames()` so the refusal is pinned to cost no
  password derivation; `a.b`, `a.b.c`, `a-_-b`, `a.-.b`, `a._.b` pinned as still accepted at the
  pattern, the service and the form. The hostile-input timing test became a `[Theory]` over three
  shapes — unbroken run, alternating dots, every separator — because the consecutive-dot rule
  introduced a pathological shape the all-`a` input does not exercise.

**No control flow changed.** The guards, their order and the length-before-shape precedence are
byte-identical; this block touched the pattern, the messages, the comments and the tests.

**Gates:** `dotnet build` 0 warnings / 0 errors · `dotnet test` **483/483** full unfiltered (was 454) ·
`openspec validate git-backed-content-core --strict` → valid · `dotnet format --verify-no-changes`
exit 0. `git diff --stat -- src tests` shows 7 files and `git status --short --untracked-files=all`
shows no untracked file — no mutation residue, pattern re-grepped in place.

**For the reviewer, hardest first.** (1) M2 is a live surviving mutant I chose not to kill — the
judgement call is whether 114 us at a million characters is worth a timing-ratio test, and I say no.
(2) `UsernameRuleDescription`'s text changed, which is a user-visible string asserted by four page
tests; I classified the differential by rule kind precisely so that change could not hide drift.
(3) The max admissible length moved 128 → 127, which no test pins directly — only the ≥ `Maximum`
direction. (4) D10's new `Consequence binding §6` is an obligation on a section that does not exist
yet; it needs to actually reach §6's brief.

→ `@reviewer`

**[reviewer]** Audit of the **S1 remediation block**, scope `git diff 70a31aa` (10 files, 2 under
`src/`).

**Verdict: Request changes — one blocker, documentation-only, three lines in one test file.** The fix
itself is correct and I confirmed every substantive claim on my own instruments rather than relaying
the worker's. The blocker is that this block ships a **new** false claim about what a test catches, in
a comment that directly contradicts the source comment the same block wrote — which is the sixth
instance in §11 of exactly the defect class S1 was.

### Verified independently — a stronger instrument than last time

I loaded `ZeroWiki.dll` and called `CredentialPolicy.UsernameMatcher()` by reflection, so every check
below ran against the **actual shipped generated regex**, not a retyped copy of the pattern string.
Confirmed engine: `System.Text.RegularExpressions.Generated.<RegexGenerator_g>…__UsernameMatcher_0`.

- **Pattern is the brief's candidate verbatim** — read out of the compiled assembly, compared to the
  D11 literal: `True`. `MinimumUsernameLength=3`, `MaximumUsernameLength=64`.
- **The pattern is exactly the stated rule.** My own oracle (charset, alnum both ends, no `..`),
  written from the prose rather than from the regex, against **666,452 inputs** — exhaustive over
  every string of length ≤ 5 from `{a, Z, 0, ., -, _, space, :, \n}` (66,430), 300k random with dots
  over-weighted, 300k structured dotted names, plus every value this section's record names:
  **0 mismatches.** Independent corpus, independent oracle, same answer as your 666,469/0.
- **Exactly one cell moved.** Pre-pattern taken from `git show 70a31aa:…`, post from the built
  assembly, `pre != post` asserted first. Classified by rule kind through the full service model
  (trim, then length-before-shape): the only transition is **`ACCEPT → shape`** (66,182 inputs);
  **inputs that moved without containing a consecutive dot: 0**; **`refuse → accept`: 0**;
  **length↔shape reclassification: 0**. The ends rule and the precedence really are untouched.
- **The N1 lower slack survived the tightening** — the property the supervisor specifically warned a
  tighter pattern could break. `a` and `ab` still match, so `[MinLength]` remains the only attribute
  that fails for a 2-character name and form-level one-fault-one-message still holds.
- **Upper slack intact**: admits `Maximum + 1` = 65. Unbroken-run maximum 128 → 127, dotted maximum
  253 — your 128 → 127 figure confirmed.
- **Control flow byte-identical, verifiably**: `BootstrapService.cs` is not in the diff at all, and the
  `InvitationService.cs` diff is comment-only.
- **Docs now say what the code does.** D11 carries the real grammar reason and the `accepted ⊆ legal`
  posture; D10 gains `Consequence binding §6`; the spec gains the consecutive-dot prose and a scenario.
  All three match the shipped pattern.
- **Gates re-run**: build 0/0 · format exit 0 · `openspec validate --strict` valid · `dotnet test`
  **483/483** full unfiltered. No untracked files, no mutation residue.

### Your engine finding is real, and I reproduced it

Measured on both engines, same input shapes:

| pattern / engine | 1K | 1M | ratio |
|---|---|---|---|
| shipped (bounded), **generated — ships** | 7.55 us | 8.18 us | **1.1** |
| shipped (bounded), interpreted | 14.52 us | 13.46 us | 0.9 |
| lookahead, generated | 7.46 us | **107.68 us** | 14.4 |
| lookahead, **interpreted** | 14.54 us | **8,789 us** | 604 |

Your diagnosis holds on my instrument: the interpreted engine is ~80x worse on the lookahead and the
two engines disagree by two orders of magnitude, while the shipped pattern is constant on *both*. That
is a genuine and well-caught blind spot.

**It does not recur anywhere in the suite** — I checked. `src/` has exactly one `[GeneratedRegex]`
(`CredentialPolicy.cs:137`), and `Username_pattern_rejects_a_very_long_input_without_doing_the_work`
measures `CredentialPolicy.UsernameMatcher()` itself, i.e. the shipping engine.
`StaticSsrForm.cs:74` and `ServiceCallSweep.cs:44` are test-internal parsers, not performance
measurements. The hazard is confined to ad-hoc harnesses.

### My ruling on M2: **your call is correct — keep the mutant alive**

Judged on its merits, and I would have made the same call.

1. `CLAUDE.md` permits a deliberate surviving mutant recorded with its reason and forbids editing code
   to make a mutant die. Removing a scaling assertion **you had added during this same block** is not
   a weakening of the suite — it is declining to manufacture a kill, which is the opposite of the
   forbidden move. Tuning a ratio to 5x until M2 died would have been the forbidden move.
2. 108 us at a million characters is not a denial-of-service vector. The **service** never feeds the
   regex more than 64 characters (length is checked before shape), and even the form path — where N1
   established every attribute runs regardless — costs a fraction of ordinary request overhead.
3. A timing-ratio assertion pins an implementation detail, not the security property, and would be
   flaky on CI.

### Blocker — @worker: this block ships a new false claim about what a test catches

Two parts, both in `tests/ZeroWiki.Tests/Identity/CredentialPolicyTests.cs`, both newly added here.

**(a) `CredentialPolicyTests.cs:128-133`.** The alternating-dots row's comment says that expressing
the rule as a `(?!.*\.\.)` lookahead "*or letting the dot branch repeat unbounded, reintroduces work
proportional to the input here while leaving the row above fast — so the run above alone would not
notice the regression*". Measured on the shipping generated engine against the test's own three
inputs and its own 100 ms threshold:

| candidate | unbroken run | alternating dots | every separator | |
|---|---|---|---|---|
| shipped (bounded) | 0.60 ms | 0.01 ms | 0.01 ms | passes |
| **M2 lookahead** | 0.81 ms | **0.11 ms** | 0.10 ms | **all pass** |
| **unbounded dot branch** | **53.5 ms** | 28.2 ms | 28.5 ms | **all pass** |
| old quadratic form | 23,413 ms | 11,454 ms | 5,739 ms | all fail |

The comment is wrong on both of its claims and wrong about the direction. For the lookahead the
alternating-dots row is the **fastest** of the three, not the discriminating one; for the unbounded
variant the unbroken run is the **worse** row, the exact opposite of "leaving the row above fast".
Neither variant is caught at all — M2 surviving 483/483 is the direct proof for the first, and the
unbounded dot branch at 53.5 ms is comfortably inside the limit too. That row buys coverage of a shape
the all-`a` input does not exercise, which is a good reason to keep it; it does not buy what the
comment says it buys.

This matters because `CredentialPolicy.cs:104-110` — written in this same block, correctly — says
*"no test refuses it … do not assume a test will stop you replacing it."* The two comments now assert
opposite things about the same mutant, and a reader who reaches the test comment first gets the
reassurance the source comment exists to deny.

**(b) `CredentialPolicyTests.cs:124-126`** — "*the unbounded form of this pattern was quadratic on an
unbroken run: ~2.3 s at 64 K characters*". On the engine that ships, that input costs **100.3 ms**.
I then measured it interpreted: **2,515 ms**. The `2.3 s` figure is an *interpreted-engine*
measurement — the same blind spot you correctly diagnosed for M2, still sitting in a comment written
in the same block. The conclusion is untouched and the test is fine: at the 1M-character input the
test actually uses, the quadratic form takes **23.4 s**, 234x the limit, so it is caught decisively.
Only the number and its engine are wrong.

**Fix:** state what the row does (covers a shape the unbroken run does not, so the pattern's cost is
pinned on dotted input as well), drop the claim that it would catch the lookahead or the unbounded
branch, and requote (b) on the generated engine. No test or source change — and while you are there,
`CredentialPolicy.cs:104-110` names only the lookahead as unprotected; the unbounded dot-branch
variant sits in the same gap at 53.5 ms and is worth naming alongside it, since what the suite pins is
"no input makes matching *catastrophic*", not "no input makes it *scale*".

→ `@worker` for the comment corrections; re-audit on the fix. Everything else in this block is
approved.

**[architect]** Brief → `@worker`. Still the **same remediation block** — this is a reviewer iteration
inside it, not a second supervisor round. The two-rounds-then-stop rule (`CLAUDE.md` §3c.4) counts
*supervisor* rounds; §11 has had one, and it has not yet re-reviewed.

**Scope: two comments in `tests/ZeroWiki.Tests/Identity/CredentialPolicyTests.cs`. Nothing else.** No
production code, no test logic, no assertion. The reviewer approved everything else in the block.

**Why this is a blocker and not a nit, despite being three lines of comment.** S1 — the thing that
reopened this section after it was committed — *was* a false comment. "Legal RFC 5322 dot-atom
localpart" sat in three places in shipped code, survived three worker passes and two reviewer audits,
and was wrong. These two comments are the same defect in the same section, and one of them
(`:128-133`) directly contradicts `CredentialPolicy.cs:104-110`, which you wrote in this same block and
which is correct. A reader will believe whichever they find first.

1. **`:128-133`** claims the alternating-dots row catches the lookahead form "while leaving the row
   above fast". Measured on the test's own inputs and its own 100 ms threshold, the alternating-dots
   row is the **fastest** of the three, and the unbounded dot-branch variant passes on every row too —
   the unbroken row being the worse one, the reverse of the claim. M2 surviving at 483/483 is direct
   proof. Say what is true: **no test in this suite refuses the lookahead form**, which is exactly
   what `CredentialPolicy.cs:104-110` already says.
2. **`:124-126`** quotes "~2.3 s at 64 K". That is an **interpreted-engine** number. On the
   `[GeneratedRegex]` engine that actually ships it is ~100 ms at 64 K. This is the very blind spot you
   diagnosed for M2, left in a comment you wrote in the same block — which is worth noticing rather
   than being embarrassed by: the diagnosis was right and simply arrived after the comment.

**Measure it yourself on the shipped engine; do not copy the reviewer's figures.** It reached
`CredentialPolicy.UsernameMatcher()` by reflection off the built `ZeroWiki.dll`, which is the right
instrument precisely because it cannot accidentally become `new Regex(...)`. Six claims-about-why have
now been wrong in this section and every single one was settled by producing the counterfactual, never
by reading — so a seventh comment written from someone else's numbers is not a fix, it is the same
mistake with a different author.

**Prefer a comment that cannot rot.** Where a figure is load-bearing, name the engine it was measured
on; where it is only illustrative, consider whether it needs a number at all. The conclusion the tests
encode — the quadratic form blows the limit by orders of magnitude on any engine — does not depend on
either disputed figure.

Gates unchanged (`dotnet test` **483/483** before your edit; a comment-only change must not move it).
No mutation needed for this. Do not tick, do not commit; post under `## 11.` and hand to `@reviewer`.

**[worker]** Both comments corrected in `CredentialPolicyTests.cs`. **Comment-only** — no production
code, no test logic, no assertion, no `[InlineData]`. `git diff --stat -- src` is byte-identical to
before this edit (54 insertions / 18 deletions across the same two files); only
`CredentialPolicyTests.cs` moved, 59 → 68 changed lines. `dotnet test` **483/483**, unmoved.

### Measured myself, on the shipped engine, before writing a word

Reflection off the built `ZeroWiki.dll` rather than a retyped pattern, exactly as briefed — a
comment written from the reviewer's numbers would have been the same mistake with a different author.
Instrument confirmed before believing it:

```
SHIPPED matcher engine : System.Text.RegularExpressions.Generated.<RegexGenerator_g>F8372052…__UsernameMatcher_0
SHIPPED pattern        : ^[A-Za-z0-9](([A-Za-z0-9_-]|\.[A-Za-z0-9_-]){0,125}([A-Za-z0-9]|\.[A-Za-z0-9]))?\z
const == matcher       : True
```

Candidates measured on the generated engine against **the test's own three inputs and its own 100 ms
threshold** (best of 3):

| candidate | unbroken run | alternating dots | every separator | verdict |
|---|---|---|---|---|
| shipped (bounded) | 0.01 ms | 0.01 ms | 0.01 ms | all pass |
| M2 lookahead | **0.11 ms** | 0.10 ms | 0.10 ms | **all pass** |
| unbounded dot branch | **16.42 ms** | 8.79 ms | 8.34 ms | **all pass** |
| old quadratic form | 22,739 ms | 11,327 ms | 5,667 ms | all fail |

**Both halves of (a) are false, confirmed independently.** For the lookahead the alternating-dots row
is not the discriminating one — it is the *joint fastest*, and the unbroken run is marginally the
worst. For the unbounded dot branch the unbroken run is the worst row by ~2x, the exact reverse of
*"leaving the row above fast"*. Neither variant is refused by anything; M2 surviving 483/483 already
proved the first.

My absolute figures differ from the reviewer's (it read 0.60 / 0.81 / 53.5 ms where I read 0.01 /
0.11 / 16.42 ms on the unbroken run) — machine load and best-of-3 versus single shot. **The
directions, the verdicts and the orders of magnitude agree**, and those are what the comment asserts.
Recording the divergence rather than quietly adopting its numbers, since that is the whole point.

**(b) confirmed.** The quadratic form at 64 K unbroken: **97.2 ms generated (ships)** vs **2,499.6 ms
interpreted**. So *"~2.3 s at 64 K"* was indeed an interpreted-engine figure sitting in a comment I
wrote in this same block, one file away from my own correct diagnosis of that exact substitution.

### What the comments now say

- **The quoted number is now the one that carries the conclusion, on the engine that ships**: the
  quadratic form takes **~23 s** on the 1M-character input the test actually uses, over 200x the
  limit. The 64 K figure is gone as a headline; the interpreted 2.5 s reading is kept only as an
  explicit parenthetical naming its engine, so the next reader sees *why* engine-mixing is the trap
  rather than having to rediscover it.
- **The dotted rows now claim what they buy** — coverage of shapes the unbroken run does not exercise
  at all, since the consecutive-dot rule is expressed by alternation — and state plainly that the
  lookahead and the unbounded dot branch both pass every row, with the unbounded variant's worst row
  being the unbroken run. That agrees with `CredentialPolicy.cs` instead of contradicting it.
- The `<summary>` also said *"Both are a million characters"* over three rows; corrected, and it now
  points at `UsernamePattern`'s remarks for what is deliberately left unguarded.

### ❓ @architect — one reviewer suggestion I did **not** action, deliberately

The reviewer added: *"while you are there, `CredentialPolicy.cs:104-110` names only the lookahead as
unprotected; the unbounded dot-branch variant sits in the same gap … and is worth naming alongside
it."* Your brief said **"Nothing else. No production code."** I followed your scope over its
suggestion rather than splitting the difference silently.

For your call, two small things in that same production comment:

1. It names only the lookahead as the unguarded rewrite. The unbounded dot branch is **worse**
   (16.4 ms vs 0.11 ms on the unbroken run) and equally unguarded, so the comment is *incomplete*
   rather than false — a different defect class from S1, which is why I was comfortable leaving it.
2. Its *"114 us at a million characters"* figure does not name its engine. It is correct
   (generated; I re-measure 0.11 ms), but by the rule you just set, a load-bearing figure should say
   which engine it came from.

Neither is a false claim, so neither blocks. Say the word and both are a one-line edit.

**[worker]** Authorised and done — `CredentialPolicy.cs`'s unguarded-rewrites paragraph now names
both variants and labels its engine. **Comment-only**, and mechanically demonstrated as such: filtering
the file's diff to non-`///` lines leaves exactly the two changes already approved earlier in this
block (the pattern literal and `UsernameRuleDescription`). **This edit contributed no non-comment
line.** `dotnet test` **483/483**, unmoved.

**Measured myself again, same instrument as the test-file pass** — reflection off the built
`ZeroWiki.dll`, engine `…Generated…__UsernameMatcher_0`, `const == matcher` `True`. I did not reuse
the reviewer's numbers or my own from an hour ago without re-running them.

Both points were right, and (1) was the more useful of the two: the paragraph named the lookahead as
the unguarded rewrite and stopped, which invites the inference that the unbounded form *is* guarded.
It is not, and it is the worse of the two — on the shipped engine at a million characters it costs
roughly a thousand times what the bounded pattern does, against the lookahead's ten, and still passes
every row. Both are now named together, with the limit they both sit inside.

**Applied your rule about figures.** No precise value survives in that paragraph: it reads *"roughly
ten times"* and *"roughly a thousand"* rather than `114 us` and `16.4 ms`. That is deliberate beyond
style — my shipped-pattern reading at a million characters sits at the stopwatch's resolution floor
(0.01 ms), so a ratio quoted to two figures would be false precision derived from a number the
instrument cannot actually resolve. An order of magnitude is what the measurement supports. The
engine is named inline, with the reason: the two engines disagree by orders of magnitude *here
specifically*, which is the trap this section fell into twice.

**One observation, not a question, and out of the scope you set.** The paragraph immediately above
(`CredentialPolicy.cs:94-101`) carries *"(ratio 0.9)"* — engine-labelled, so it satisfies the second
half of your rule, but a precise value under the first half. It is mine, from this block, and it is
the only such figure left in the file. Not touching it: you scoped this to the one comment and it is
not a false claim. Flagging it so the decision is yours rather than an oversight.

**Gates:** `dotnet build` 0 warnings / 0 errors · `dotnet test` **483/483** full unfiltered (unchanged
across all three passes of this block) · `dotnet format --verify-no-changes` exit 0 ·
`openspec validate git-backed-content-core --strict` valid. No mutation run — a comment cannot be
mutated into a behaviour. No untracked files, no residue.

→ `@reviewer` — this plus the test-file corrections, as one audit.

**Gates:** `dotnet build` 0 warnings / 0 errors · `dotnet test` **483/483** full unfiltered
(unchanged, as required of a comment-only change) · `dotnet format --verify-no-changes` exit 0 ·
`openspec validate git-backed-content-core --strict` valid. No mutation run — none was needed. No
untracked files, no residue.

→ `@reviewer`

**[architect]** Answering the worker's question, and **widening my own scope by one edit** — the
`CredentialPolicy.cs:104-110` comment is in.

The worker was right to ask rather than split the difference silently; "nothing else, no production
code" is what I wrote, and a worker that quietly exceeds a scope boundary is a worse failure than one
that stops. But the reviewer's suggestion is sound and I am taking it, because the two defects it
names are the **same class as the blocker they sit beside**:

- the comment says no test refuses the *lookahead* form, and stops there — while the **unbounded dot
  branch is worse** (16.4 ms vs 0.11 ms on the shipped engine, measured by the worker) and equally
  unguarded. A reader who takes "the lookahead is unguarded" as the complete list infers the unbounded
  form *is* guarded. Incomplete, in a way that misleads in the dangerous direction.
- its `114 µs` carries **no engine label** — in the file whose section has now been bitten twice by an
  engine-unlabelled figure, and one file from the comment being corrected for exactly that.

Neither is false, so neither blocked; both are one line. Leaving them to be discovered later, in a file
this section has already reopened once, is the worse trade.

**On the divergent absolute numbers** — worker 0.01/0.11/16.42 ms where the reviewer read
0.60/0.81/53.5 ms, same directions and orders of magnitude, different machine load and best-of-3
versus single-shot. The worker recording the divergence instead of adopting the reviewer's figures is
the correct handling, and it generalises into the rule the corrected comments already follow: **a
timing comment may assert a direction or an order of magnitude, never a precise value** — a precise
value is a measurement of a machine, and it rots silently on someone else's. Two agents agreeing on
direction while disagreeing on magnitude is exactly what a healthy instrument looks like.

→ `@worker` for the one-line `CredentialPolicy.cs` edit, then `@reviewer` audits both together.

**[architect]** The worker's flagged `(ratio 0.9)` — **fixed by me, and this closes comment work on
this file.**

It was right to flag rather than act: it was outside the one comment I scoped, and it is not a false
claim. But it was the last precise timing value left in the file, in the paragraph immediately above
the rule I had just imposed, so leaving it would have stated a rule and violated it three lines
earlier. `CredentialPolicy.cs:96-99` now reads *"costs no more than a one-thousand-character one — the
measured ratio sits at the stopwatch's noise floor rather than above it"*. That is also the more
honest statement: the worker's shipped-pattern reading at a million characters was **0.01 ms**, the
stopwatch's resolution floor, so a two-figure ratio was false precision derived from a number the
instrument cannot resolve. Removing it makes the comment weaker and truer at once.

Verified rather than asserted: `dotnet build` 0/0, `dotnet format --verify-no-changes` exit 0, and
filtering `git diff 70a31aa -- src/ZeroWiki/Identity/CredentialPolicy.cs` to non-`///` lines leaves
**exactly the two changes already approved in this block** — the pattern literal and
`UsernameRuleDescription`. My edit contributed no behaviour, which is the same demonstration the
worker gave for its own.

**Declaring this closed:** the file now satisfies the rule throughout, and further comment polish is
out of scope for §11. If a later reader finds another, it is a `## NEXT` note, not a round.

→ `@reviewer` for the combined audit of all three comment edits.

**[reviewer]** Combined audit of the three comment edits. Scope `git diff 70a31aa`; everything else in
this block I certified in my previous entry and re-checked only where these edits could have disturbed
it.

**Verdict: Approve.** Both blocker items are fixed, and fixed in the right direction — the test
comments now *agree* with `CredentialPolicy.cs` rather than contradicting it. Two notes below are for
the supervisor, not for another round; I agree with closing comment work on this file.

### Behaviour-neutrality — checked, not accepted

I ran the filter myself rather than taking it. Non-`///` changed lines in `src/` since `70a31aa`:
**exactly two**, both already approved — the `UsernamePattern` literal and `UsernameRuleDescription`.
`InvitationService.cs` has **zero** non-comment changed lines. Gates re-run: `dotnet build` 0/0 ·
`dotnet format --verify-no-changes` exit 0 · `openspec validate --strict` valid · `dotnet test`
**483/483** full unfiltered. No untracked files.

### Blocker (a) — fixed, and independently correct

`CredentialPolicyTests.cs` now states that the dotted rows "*do not discriminate between candidate
rewrites*", that the lookahead "*passes every row here (~0.1 ms)*", and that the unbounded dot branch
passes too "*its **worst** row being the unbroken run above rather than either of these*". Every one of
those matches what I measured (lookahead 0.11 / 0.10 ms on the dotted rows; unbounded worst row the
unbroken run at 53.5 ms), and the closing sentence — "*Neither is refused by any test in this suite*" —
now says the same thing as `CredentialPolicy.cs:104-113`. The summary's "*not that any particular
rewrite of the pattern would be rejected*" is the right place for that claim to live. The row is kept
for the reason it actually earns: it exercises a shape the unbroken run does not.

### Blocker (b) — fixed, and the engine is now labelled at the point of use

"*~23 s on this row — over 200x the limit below*" matches my 23,413 ms against a 100 ms limit (234x),
and the parenthetical explicitly marks "*~2.5 s at 64 K characters*" as the **interpreted** figure —
my measurement was 2,515 ms interpreted against 100.3 ms generated. Quoting both with their engines
named, and saying that mixing them is what made the replaced claim wrong, is a better fix than deleting
the number would have been.

### Note 1 (supervisor) — the ratio rule is applied in one paragraph and not in the one nine lines below

`CredentialPolicy.cs:96-99` drops `(ratio 0.9)` on the grounds that its denominator — a 0.01 ms
reading — is at the stopwatch's resolution floor, so a two-figure ratio is false precision.
`CredentialPolicy.cs:104-113` then asserts "*roughly ten times*" and "*roughly a thousand*", and both
are ratios against that **same** unresolvable denominator (0.11/0.01 = 11, 16.42/0.01 = 1642).
Perturb the denominator within its own noise, 0.005–0.02 ms, and "ten times" swings across 5x–22x.

**Not a false claim** — it is true as measured, and by the standard set for me I am recording it rather
than blocking. But the file now applies its own rule inconsistently, and there is a form that cannot
rot: state both against *the limit the timing test asserts* — the lookahead uses well under 1% of it,
the unbounded form under a fifth — which is the comparison that actually matters to a reader deciding
whether either is safe, and needs no denominator at the instrument's floor.

### Note 2 (supervisor) — @architect, on edit 3, checked as I would a worker's

**It is sound, and it is doing honest work rather than hedging.** I verified the underlying claim with
averaging: 1K = 7.55 us, 1M = 8.18 us. The million-character input is ~8% *more*, so a bare "costs no
more than a one-thousand-character one" would have been literally false by a hair — the noise-floor
clause is what makes the sentence true. Replacing a rottable falsifiable number with an unrottable one
was the right call.

One caveat in the other direction, which is why it is a note rather than nothing: averaged over
repeated runs the ratio resolves cleanly at ~1.1, so "*the stopwatch's noise floor*" describes the
single-shot harness, not the limit of what can be measured. A reader wanting to re-verify constancy may
conclude it is unmeasurable when it is not. "Measured with averaging" would carry the same honesty
without implying the property is out of reach.

### On the divergence between our absolute numbers — your ruling is right, and here is the cause

Mine were single-shot and un-warmed, so every row carried JIT and first-use cost of the generated
regex; the worker's best-of-3 is nearer steady state, and its 0.01 ms is the better figure. Mine were
inflated *uniformly*, which is why every direction, every order of magnitude and every pass/fail
verdict against the 100 ms limit came out identical on both harnesses. So the divergence is explained
rather than merely tolerated — and it is a concrete case for the rule you imposed, not a hypothetical
one: two careful measurements of the same regex on the same engine differed 60x on the fast rows and
agreed on every conclusion that mattered. **A timing comment may assert a direction or an order of
magnitude; it may not assert a precise value.** Worth carrying into `## NEXT` as a standing rule
alongside the engine one.

→ `@architect` — approved. Comment work on this file is closed as far as I am concerned; Notes 1 and 2
are for the supervisor's re-review, not another round.

**[architect]** **Note 1 applied by me after the `Approve`; Note 2 deliberately not.** Recording both
decisions, because an `Approve` certifies the state it was shown and this edit is not in that state —
the same declaration made for §1's post-`Approve` README edit.

**Note 1 was mine to fix, and it was not polish.** I dropped `(ratio 0.9)` at `:96-99` on the grounds
that its 0.01 ms denominator is unresolvable, then left *"roughly ten times"* and *"roughly a
thousand"* nine lines later — both ratios against **that same denominator**. The reviewer perturbed
them inside their own noise and "ten times" swings 5x–22x. So the file asserted a rule and broke it in
the next paragraph, which is precisely the *"an `[architect]` claim the record does not support"*
failure this change has already had twice. `:109-114` now states both against **the limit the timing
test asserts** — under a hundredth of it, and under a fifth — with the reason named inline: this
pattern's own cost is too small to divide by. That is unrottable, because the limit is a constant in
the test rather than a property of whoever's laptop ran it.

I said comment work was closed and then reopened it once. The distinction I am drawing: *polish* is
closed, the **rule** is not, and a file that violates the rule it states is not finished. If that
distinction is too convenient, the supervisor should say so.

**Note 2 stands as written, on the reviewer's own evidence.** It endorsed `:96-99` and improved the
reason for it: averaged, 1 K = 7.55 µs against 1 M = 8.18 µs, so the million-character input is ~8%
*more* — a bare "costs no more than" would have been false by a hair, and the noise-floor clause is
what makes the sentence true. Its caveat is that "noise floor" describes the single-shot harness
rather than the limit of what is knowable. Fair, and I am leaving it: the sentence is true, the
clause is load-bearing, and rewriting it to say "~1.1 averaged" would reintroduce exactly the precise
value both of us have now removed twice. Recorded so the supervisor rules on it rather than finding
it.

**[architect]** §11 remediation **committed** as `<fix sha>` — a `fix`, ticking nothing, per
`CLAUDE.md` §3c.3. Gates run by me rather than relayed: `dotnet build` 0/0 · `dotnet test`
**483/483** full unfiltered · `openspec validate git-backed-content-core --strict` valid ·
`dotnet format --verify-no-changes` exit 0. Non-`///` diff over `src/` since `70a31aa` is exactly the
two approved changes — the pattern literal and `UsernameRuleDescription` — so every comment edit in
this block, mine included, contributed no behaviour. No untracked files anywhere.

→ `@supervisor` for round two over `bb3cb2c..HEAD`.

**[supervisor]** Round-two section review of **§11**, scope `bb3cb2c..HEAD` — two commits
(`70a31aa`, `2af401c`). **Verdict: Approve. §11 is closed.** S1 is discharged in both halves. Two
findings below, both low, neither blocking, and I say plainly why each one is not.

### Probe 1 — the subset argument is sound. You were right and I was wrong.

`accepted ⊆ legal` is the property D10 needs; `legal ⊆ accepted` is required by nothing in this
system. My *"misaligned in both directions"* was rhetorical framing, and the `_legacy_` row was
evidence I marshalled for it rather than a defect it named. You separated the two correctly.

I checked the **result**, not just the argument, on a third instrument: an oracle written from
RFC 5322 `atext` in full (`A-Za-z0-9!#$%&'*+/=?^_\`{|}~-`), which is a **wider** class than the
charset-restricted oracle both you and the reviewer used, so it is genuinely independent of both.
Pattern extracted from `CredentialPolicy.cs` programmatically, never retyped. Exhaustive over 111,110
strings of length ≤ 5 from a 10-character alphabet including `:`, space, `\n` and `!`, plus 400,000
dot-heavy randoms: **accepted-but-illegal = 0.** The property holds.

**And it was not the convenient call.** You accepted the *more expensive* half — the §6 obligation,
which no pattern change can ever discharge and which costs a later section real work — and rejected
the half that carried no defect. Convenience would have done exactly the reverse. That is the check
I would apply to anyone shrinking a finding, and it passes.

### Probe 2 — `Consequence binding §6` is real, but it is standing on one reader's memory

The text is right and in D10's own idiom. The weakness is not its wording, it is its **location**.
`design.md` D10 is read by whoever briefs §6; `tasks.md` §6.3 says *"create one commit authored as
the logged-in user"* and says nothing about legacy names. Meanwhile this change already built the
mechanism for precisely this problem — `## NEXT`'s **"Forward obligations — each is owed by a
specific section"**, which carries five entries and does **not** carry this one. The obligation lives
in the document that describes the design, not in the one the Architect reads at resume.

Two things make it survive, and I would do both:

1. **Add it as forward obligation #6 in `## NEXT`.** Free, and it is the same mechanism already
   trusted for the lockfile trap and the missing HTTP client.
2. **Promote it into a spec scenario** on whichever capability carries §6's commit authorship
   (`specs/content-store/`): *an account whose username predates the username rules still produces a
   well-formed author line*. This is the only form with a **gate** behind it — a spec scenario is
   what §6's own section review is checked against, whereas `design.md` prose is checked by nobody.

### Probe 3 — the record is true, with one residual over-claim, which I found the way this section says such things are found

Everything checkable holds: the pattern literal is D11's candidate verbatim; `accepted ⊆ legal`;
unbroken maximum 128 → 127 confirmed; upper slack intact (admits `Maximum + 1`); N1's lower slack
intact (`a`, `ab` still match, so `[MinLength]` stays the only attribute failing a 2-character name);
`a..b`/`a...b`/`ab..cd` refused at all four layers; `RefusedUsernames()` extended so the refusal is
pinned to cost no derivation; no substring collision between the three rule descriptions, so every
`Assert.DoesNotContain` stays sound after the message text changed.

**F1 (low) — `CredentialPolicy.cs:85-91` states the slack rationale without its bound, and past that
bound it is false.** The paragraph says `125` admits 127 unbroken alphanumerics *"**so** an over-long
username fails the length check and is reported as a length problem **rather than also being told its
charset is wrong**"*. At the **service** that is true at every length, because length is checked
first. At the **form**, where N1 established every attribute runs, it is true only up to 127. I ran
the differential as a **form** model across the pattern change:

| unbroken alnum length | service pre → post | form pre → post |
|---|---|---|
| 65 – 127 | len → len | len → len |
| **128** | len → len | **len → len + shape** |
| 129+ | len → len | len + shape → len + shape |

Exactly one length moved. A 128-character all-alphanumeric name begins and ends alphanumeric, uses
only the permitted charset and contains no doubled dot — **one fault, two messages**, and the shape
rule it is told about is one it did not break. That is the same sentence the worker used to justify
changing `UsernameRuleDescription`, applied to a case nobody looked at.

**Not blocking, and the reason matters more than the finding.** The effect is cosmetic and sits at
128 characters, twice the cap; it is pre-existing above 128 and the remediation moved the boundary by
one. What is worth carrying is **why two 666,000-input differentials missed it: both classified
through the *service* model** — trim, then length-before-shape, first fault wins — and N1, this
section's own headline finding, is that **the form is exactly where those two disagree**. §11 derived
the rule *"an assertion's justification is a claim about a counterfactual, and the only instrument
that checks it is the mutation that produces that counterfactual"* and then produced only the
service-side counterfactual. The blind spot is in the **model**, not in the diligence — structurally
the same failure as `new Regex` versus `[GeneratedRegex]`, one level up.

**F2 (nit) — the accept scenario is not an iff, and now omits two rules rather than one.**
`specs/user-accounts/spec.md`, *"Well-formed username is accepted"*: its WHEN is satisfied by `a..b`
(≥ 3 characters, alphanumeric ends, permitted charset), which the new consecutive-dot scenario
refuses. **I checked whether this block introduced it, and it did not** — at the requirement's origin
(`bd2eeea`) the same WHEN already omitted the 64-character cap, so a 65-character name satisfied it
and was refused. It has always been an illustrative happy-path clause rather than a description of
the accepted set; the remediation extended an existing looseness by one rule rather than creating a
defect class. Fix before archive, one clause: *"…at most 64 characters … and containing no two dots
in a row."* Had this been introduced here I would have blocked on it, because a self-contradicting
requirement is what N2 was.

### Probe 4 — the two post-`Approve` edits

**Behaviour-neutrality: verified by me, not accepted from the record.** `git diff 70a31aa..HEAD --
src` filtered to non-`///`, non-`//` lines is **exactly two lines** — the `UsernamePattern` literal
and `UsernameRuleDescription` — both certified by the reviewer *before* either comment edit existed.
So neither post-`Approve` edit could have moved behaviour, and the declaration is sound on its own
evidence rather than on assertion.

**Content.** `:104-115` restating both unguarded rewrites against **the limit the timing test
asserts** rather than against this pattern's own cost is correct, and it is a better answer than
Note 1 asked for: the limit is a constant in the test, so it cannot rot on someone else's machine.
`:93-102`'s noise-floor clause is true of the harness that produced it, the reviewer's Note 2 caveat
is fair, and I agree with leaving it — rewriting it reintroduces the precise value two passes have
now removed. One residual, recorded not raised: `:94-95` says the branches are *"disjoint on their
first character … **so** the run never branches"*, offered as the reason cost is constant.
Disjointness removes *branch* backtracking; what bounds the work is the finite `{0,125}` — the engine
still backtracks the unit **count**, ≤ 126 steps. Both facts are in the paragraph, so nothing is
false; the *"so"* just credits the weaker of the two reasons.

**The polish-versus-rule distinction: I tested it rather than accepting it, and it holds.** The test
is not whether the distinction sounds principled — it is whether it was applied when applying it was
*expensive*. It was: you reopened a file you had just declared closed, to correct your own paragraph,
because you had imposed the rule three lines above it. And you declined Note 2, where acting would
have been cheap and cosmetic, on the substantive ground that the fix reintroduces the thing the rule
forbids. Costly direction taken, cheap direction declined. A convenient distinction runs the other
way round, and that pattern is not present. It is a rule.

### For `## NEXT`

1. **Forward obligation #6 — D10's `Consequence binding §6`**, plus the spec-scenario promotion in
   Probe 2. Item 2 is the one with a gate behind it.
2. **Standing rule:** a timing comment may assert a **direction** or an **order of magnitude**, never
   a precise value — and any timing figure names the engine it was measured on. `[GeneratedRegex]`
   and the interpreted engine differ by ~2 orders of magnitude on this pattern's rewrites.
3. **Standing rule, the one I would most want carried into §2:** a differential is only as good as
   the **surface** it models. Both §11 differentials modelled the service; the form is where N1 says
   behaviour diverges, and that is where the one residual defect sat. When a rule is enforced on two
   surfaces, the differential runs on both.
4. **Before archive:** the accept scenario's WHEN needs its two missing rules (F2).
5. `CredentialPolicy.cs:85-91` — name the surface the slack claim holds on, or its bound (F1).
6. **Carried from round one:** page-test matrices still asymmetric (`"café"`, `"admin\tx"` in
   `BootstrapPageTests.cs` only); the username is now immutable-by-consequence, which belongs in §6's
   brief; and `## NEXT` is still stale at "4/39" and "394/394" against an actual **7/40** and
   **483/483**.

→ `@architect`. §11 **closed**. Open §2.

**[architect]** §11 **CLOSED** — supervisor `Approve`, round two, 2026-08-02. Close-out actions
applied by me below; `## NEXT` rewritten per §3c.2.

**The supervisor named the wrong file, and I checked before following it.** Its recommendation was to
promote D10's `Consequence binding §6` into a spec scenario on "§6's capability
(`specs/content-store/`)". §6 is commit-on-save, which `proposal.md:21` assigns to
**`content-editing`**; `content-store` owns repository layout, rendering, the index and the clean-tree
invariant. Filing it there would have put the obligation in a capability whose section review never
looks at §6 — the precise failure the promotion exists to prevent, executed faithfully. Recorded not
to score a point but because it is the seventh-and-a-half instance of the section's one lesson: a
recommendation is a claim, and this one was checkable in ten seconds.

**Applied:**

1. **The §6 obligation now has a gate.** `specs/content-editing/spec.md`, *Commit-on-save*: a prose
   sentence requiring a well-formed author address for **every** account including one whose username
   predates the rules, plus a scenario. The supervisor's reasoning is exactly right and is the whole
   point — design prose is read by whoever happens to read it; a spec scenario is what §6's own
   section review is checked against. It also stays in D10, so both the briefer and the reviewer meet
   it.
2. **Forward obligation #6 added** to `## NEXT` below, in the list this change already built for
   obligations owed by a section that does not exist yet.
3. **F1 and F2 parked, not fixed** — both low, both explicitly non-blocking, and F2 is **pre-existing
   from `bd2eeea`** rather than introduced here. They are close-out items before archive.

**On the supervisor's answer to probe 1.** It withdrew "misaligned in both directions" and checked the
result on a *third* instrument — an oracle built from RFC 5322 `atext` in full, a wider class than the
charset-restricted oracle both the worker and the reviewer used, so genuinely independent of both:
accepted-but-illegal = 0. That is the right way to settle a disagreement about a claim, and it is the
reason I asked it not to defer to me.

**On F1, which is the most valuable finding in this review.** Two 666k-input differentials missed a
real behaviour difference at length 128 because **both classified through the *service* model** —
while N1, this section's own headline finding, is that the form is exactly where the two surfaces
disagree. §11 derived the rule *"a justification is only checked by producing its counterfactual"* and
then produced only the service-side counterfactual. Same shape as `new Regex` versus
`[GeneratedRegex]`, one level up: the blind spot was the **model**, not the diligence. I have not
independently verified the 128 boundary; it is recorded as the supervisor stated it, unverified by me,
and whoever fixes it should reproduce it first.

## 2. Repository bootstrap & invariant

**[architect]** Base: `7b50e46` — makes the mounted volume an actual git repository the app can serve
from and be pushed into: detect-or-initialize, the repo configuration without which `updateInstead`
and the Smart HTTP remote cannot work at all, the hook install mechanism, and D9's startup
reconciliation with the working-tree-clean invariant it establishes.

**[architect]** Pre-flight (CLAUDE.md §2): tree clean, on `change/git-backed-content-core`,
`openspec validate --strict` valid, `proposal.md` / `design.md` / `specs/content-store/spec.md` read,
plus the `git-sync` requirement that depends on this section's repo config. §1 and §11 both carry a
supervisor `Approve`, so nothing is owed backwards.

**[architect]** Three questions put to the Product Owner before the first block, and their answers:

1. **2.3 — mechanism plus no-op hook bodies.** §2 delivers the hook installer and hooks that exist and
   do nothing (`exit 0`); §5.3 and §8.1–8.2 write the real bodies. *Architect call on the part the
   options left open:* **bootstrap rewrites both hooks on every start**, so an upgraded image ships
   updated hook bodies rather than inheriting whatever the volume happens to hold. That is also what
   makes landing no-op bodies safe — they are not a permanent artifact but the current version of a
   file the app owns. An operator's hand-edit to a hook is overwritten; at this scale that is the
   zero-config posture, not an oversight, and it should be said out loud in the code.
2. **2.5 — a startup assertion, no HTTP surface.** Recorded with its reasons so §7/§8 do not
   relitigate it: an anonymous `/health` tells strangers about repository state, which is the shape
   AD21 exists to close; an authenticated one cannot be called by a Docker `HEALTHCHECK`, which
   carries no credentials; and §1's runtime image has no `curl`, `wget` or `nc` (`## NEXT` obligation
   2), so nothing inside the container could call either.
3. **The spec scenario is tightened to D9.** `specs/content-store/spec.md`'s *"Dirty tree reconciled
   at startup"* still says the tree is reconciled *"either committing the orphaned changes as a
   recovered edit or discarding them per policy"* — wording that predates D9, which removes the policy
   switch and always commits. Permissive rather than contradictory, so it was not a stop-and-ask, but
   it invites a knob the design forbids. Block 2 amends it, so §2's section review is gated on the
   scenario rather than on prose in `design.md`.

**[architect]** Carve: **two blocks.** `2.1–2.2` (detect-or-init, repo config, initial commit) then
`2.3–2.5` (hook install mechanism, D9 reconciliation, the invariant self-check, spec amendment).

**[architect]** Brief → `@worker`. Block 1 of 2: **2.1–2.2**.

### What binds

**Spec — `specs/content-store/spec.md`, Requirement: Git repository is the source of truth:**

> The system SHALL treat the mounted content volume as a non-bare git repository whose working tree is
> the authoritative store for all page content and authorship.

Two scenarios are in scope: *Repository present on startup* (serve from the working tree "without
requiring any additional configuration") and *Empty volume is initialized* (a non-bare repo, a `docs/`
directory, an initial commit). *Authorship comes from git* is §3.4's, not this block's.

**D1** — non-bare repo, pushes accepted into the checked-out branch via
`receive.denyCurrentBranch = updateInstead`. **D8** — `<DataRoot>/wiki` is the repository root and
`<DataRoot>/wiki/docs` the working tree; `ContentPaths` already computes both, so do not recompute
them anywhere.

### Binding decisions

1. **Resolve `ContentPaths` from DI** (`## NEXT` obligation 4). The singleton §1 registered has zero
   consumers in `src/`; this block gives it its first. Do **not** call `ResolveContentPaths` — that
   exists only for the pre-`Build()` DataProtection wiring and carries a comment saying so.
2. **Apply the repo configuration on every start, not only when initializing.** 2.2's wording hangs it
   off the init path, but then a repository made by an earlier image, or restored from a backup, never
   receives it. Idempotent on every start, whether or not this start created the repo — the same
   posture as the hooks in block 2.
3. **The configuration set is `receive.denyCurrentBranch=updateInstead` (D1) *and*
   `http.receivepack=true`** (`## NEXT` obligation 3). Without the second, `git-receive-pack` returns
   **403** in this image — git's export policy, not authentication and not ownership — and §7 will read
   it as an auth bug. It is repo configuration, so it belongs here.
4. **Do not call it `BootstrapService`.** `ZeroWiki.Identity.BootstrapService` already exists and means
   "the first administrator". A second one in `Content` makes every later conversation ambiguous.
5. **`git init -b <branch>` — name the branch explicitly.** Bare `git init` takes `init.defaultBranch`
   from host configuration, which differs between a developer's laptop and the container. §7's
   `updateInstead` push targets the checked-out branch and the app has to know its name. Use `main`,
   as a named constant rather than a literal repeated at call sites.
6. **The initial commit is authored `System <system@zerowiki.org>`** — *Architect call extending D9,
   flagged as such for the reviewer.* D9 gives that identity to *recovery* commits because they belong
   to the software rather than to a member. The initial commit is the same kind of thing, and there is
   no member to attribute it to — it can precede the first administrator entirely.
7. **Pass the author explicitly on every commit; never rely on ambient git configuration.**
   `git -c user.name=… -c user.email=… commit`, or `GIT_AUTHOR_*`/`GIT_COMMITTER_*`. In the container
   there is no ambient identity and the commit merely fails; on a developer's laptop it silently
   succeeds and authors ZeroWiki's initial commit **as the developer**. The second is the dangerous
   one, because it looks like it worked.
8. **`docs/` must exist in a fresh clone.** git does not track empty directories, so `git init` +
   `mkdir docs` + `commit --allow-empty` gives an operator's Obsidian clone no `docs/` at all. Commit a
   `docs/.gitkeep`. It needs no enumeration exemption — page enumeration and the derived index are
   Markdown-only by design (`design.md`'s attachments resolution).
9. **A bare repo at `RepositoryRoot` is a misconfiguration: fail loudly, do not initialize over it.**
   2.1 says detect an existing **non-bare** repo. The three cases: *(a)* repo present and non-bare →
   use it; *(b)* nothing there, or a directory with no `.git` → initialize in place (this is the "copy
   a folder of Markdown onto the volume" case D9 calls the normal one, and block 2's reconciliation is
   what commits what was copied); *(c)* repo present and **bare** → the whole of D1 rests on a working
   tree, so refuse to start, naming the path, rather than silently serving nothing.
10. **Leave the tree clean.** This block's own exit condition: after bootstrap `git status --porcelain`
    is empty. Block 2 turns that into an asserted invariant; this block must not be what breaks it.

### Where it plugs in

`Program.cs` already has the shape — `await app.MigrateIdentityDbAsync();` then
`await app.LogBootstrapStateAsync();`. The content-repository step joins them, before the pipeline.
Startup, not first request: an operator learning about a broken volume from a 500 on page one is the
wrong failure mode.

### Shelling out to git

Every later section shells out to git (§3.4 `log`/`blame`, §6 `add`/`commit`, §7 `http-backend`).
Introduce **one** small runner here and use it for everything this block does: process start,
arguments as an array and never a concatenated string, working directory, captured stdout/stderr,
non-zero exit surfaced as a failure that carries stderr. It does **not** need to anticipate §7's
streaming CGI case — it needs to not be reinvented three times. Keep it minimal; a speculative
abstraction is exactly what the supervisor flags.

### Tests

- empty data root → repo initialized, non-bare, `docs/` present **and tracked**, exactly one commit,
  authored `System <system@zerowiki.org>`, `git status --porcelain` empty;
- second start over the same root → no second initial commit, configuration still correct, tree still
  clean (`ZeroWikiAppFactory.RestartedFrom` already gives this shape);
- a repo created *without* the configuration, standing in for an older image → configuration applied on
  the next start;
- a bare repo at `RepositoryRoot` → startup fails with a message naming the path;
- the branch is the constant, not the host's `init.defaultBranch`.

Integration level through `ZeroWikiAppFactory` where the subject is startup wiring, unit level against
a temp directory where it is the service. **Do not soften `LoginPageTests.cs:214`** (`## NEXT`
obligation 5) — it is the guard that keeps the key-ring path honest.

### Mutation testing — deliberately not in this block

Repository bootstrap is not auth, concurrency, or data integrity in the sense CLAUDE.md scopes the
rule to (`BootstrapService`, `InvitationService`, `LoginService`, `GitTokenService`, the anonymous
gate). Ordinary unit tests with normal coverage are the correct standard for 2.1–2.2. **Do not start a
mutation run.** D9's reconciliation in block 2 is the only candidate in this section, and I will scope
it there if it earns it.

### Gates

`dotnet build` clean · `dotnet test` green **unfiltered** (483 today, no filter — a filtered figure is
not the record) · `openspec validate git-backed-content-core --strict` · `dotnet format
--verify-no-changes`. Post progress and any question to this DEVLOG under `## 2.`, then hand to
`@reviewer`.

**[worker]** 2.1–2.2 implemented.

- **`ZeroWiki.Content.GitProcessRunner`** (new, `GitProcessRunner.cs`) — the one process runner for
  every content-repository git invocation. `RunAsync`/`RunOrThrowAsync(workingDirectory, arguments,
  environmentVariables?, cancellationToken)`: arguments go through `ProcessStartInfo.ArgumentList` as
  a genuine array (never a concatenated string), `environmentVariables` layers on top of the inherited
  environment (used for `GIT_AUTHOR_*`/`GIT_COMMITTER_*`), stdout/stderr are captured, and a non-zero
  exit from `RunOrThrowAsync` throws `GitProcessException` carrying stderr. Kept to exactly this — no
  streaming/CGI shape, per the brief.
- **`GitAuthor`** (new) — a `Name`/`Email` record with `ToEnvironmentVariables()` and a static `System`
  identity (`System <system@zerowiki.org>`, decision 6). Shared shape for D9's later recovery commits
  and §6's member commits, not introduced speculatively — decision 6 already names both as "the same
  kind of thing".
- **`ContentRepositoryService`** (new, deliberately not `BootstrapService` — decision 4) —
  `EnsureRepositoryAsync`: `Directory.CreateDirectory(RepositoryRoot)`, then `git rev-parse
  --is-bare-repository` decides the three cases in decision 9 — non-zero exit ⇒ no repo yet ⇒
  `git init -b main` (`DefaultBranch` is a named constant, decision 5); `"true"` ⇒ bare ⇒
  `InvalidOperationException` naming `RepositoryRoot`, never initialized over; `"false"` ⇒ existing
  non-bare repo, used as-is. Configuration (`receive.denyCurrentBranch=updateInstead`,
  `http.receivepack=true`) is applied unconditionally on every call, per decision 2/3. The initial
  commit is gated on `git rev-parse --verify -q HEAD` failing (no history yet): creates `docs/`,
  writes `docs/.gitkeep` if absent, `git add docs/.gitkeep`, then `git commit` with
  `GitAuthor.System.ToEnvironmentVariables()` supplying `GIT_AUTHOR_*`/`GIT_COMMITTER_*` — never
  ambient config (decision 7).
- **`ContentStorageStartupExtensions`** — `AddContentStorage` now also registers `GitProcessRunner`
  and `ContentRepositoryService` as singletons (both stateless beyond the already-singleton
  `ContentPaths`). New `EnsureContentRepositoryAsync(this IHost, CancellationToken)` mirrors
  `MigrateIdentityDbAsync`/`LogBootstrapStateAsync`'s shape (`CreateAsyncScope` → resolve → call) and
  resolves `ContentRepositoryService` from DI — satisfying `## NEXT` obligation 4, the singleton's
  first real consumer. Did **not** touch `ResolveContentPaths`, which stays pre-`Build()`-only.
- **`Program.cs`** — `await app.EnsureContentRepositoryAsync();` inserted between
  `MigrateIdentityDbAsync` and `LogBootstrapStateAsync`, before the pipeline, per the brief.

**Tests** — `tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs` (unit level, real `git`
against a throwaway temp dir): empty root → non-bare repo, `docs/.gitkeep` tracked, exactly one
commit authored `System <system@zerowiki.org>`, porcelain empty; second start → still one commit,
config still correct, porcelain empty; a repo pre-created (manually, with its own commit authored by
someone else) but never configured → gets configured on the next start, its existing commit
untouched; a bare repo → `InvalidOperationException` whose message contains the path; a host with
`GIT_CONFIG_GLOBAL` pointed at an `init.defaultBranch=notmain` config → the repo still ends up on
`main`. `tests/ZeroWiki.Tests/Web/ContentRepositoryStartupTests.cs` (integration level, through
`ZeroWikiAppFactory`): booting the real app initializes the repo under `ContentPaths.RepositoryRoot`
with a clean porcelain status; `ZeroWikiAppFactory.RestartedFrom` over the same `DataRoot` still
shows exactly one commit. Did not touch `LoginPageTests.cs:214`.

No mutation testing run, per the brief's scoping call — repository bootstrap isn't auth, concurrency,
or data integrity in CLAUDE.md's sense.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **490/490** full unfiltered (483 + 7 new)
· `dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid. `git status --short` / `git diff --stat` reviewed before reporting: only the files this block
touches, plus this DEVLOG entry.

→ @reviewer

**[reviewer]** 2.1–2.2 — **Request changes.**

**Blocker — repo detection trusts `git`'s upward directory discovery, not whether
`RepositoryRoot` itself is a repo (`ContentRepositoryService.cs:49-64`, `78-93`).**
`EnsureRepositoryAsync` decides case (a)/(b)/(c) from `git rev-parse --is-bare-repository` run with
`WorkingDirectory = repositoryRoot`. Git does not confine that lookup to `repositoryRoot` — by
default it walks up parent directories until it finds a `.git`. If `repositoryRoot` has no `.git` of
its own **but is nested inside an ancestor git repository**, the probe still succeeds (`"false"`),
so the code takes the "existing non-bare repo, use as-is" branch (line 63) instead of decision 9(b)'s
"directory with no `.git` → initialize in place" — and then `ApplyRepositoryConfigurationAsync`
(`78-93`) runs `git config receive.denyCurrentBranch updateInstead` / `http.receivepack true` against
whatever repo `git` actually discovered, i.e. **the ancestor**, not `repositoryRoot`.

This is not hypothetical — it is exactly this project's own local-dev setup:
`src/ZeroWiki/appsettings.Development.json:12` sets `ContentStorage:DataRoot` to the relative path
`App_Data`, which `dotnet run` (cwd = the project directory) resolves to
`src/ZeroWiki/App_Data`, itself nested inside the ZeroWiki repo checkout that has its own `.git` two
levels up. `App_Data/wiki` has no `.git` of its own. On first `dotnet run` in Development,
`ContentRepositoryService` will discover the **ZeroWiki checkout's own `.git`**, conclude "existing
non-bare repo", and silently write `receive.denyCurrentBranch=updateInstead` /
`http.receivepack=true` into the ZeroWiki dev repo's own `.git/config` — the repository this change
is being built in. Since that ancestor repo already has commits, `EnsureInitialCommitAsync`'s HEAD
probe also succeeds, so it never creates `docs/` or `docs/.gitkeep` under `App_Data/wiki` either —
the app would believe bootstrap succeeded while `App_Data/wiki/docs` doesn't exist.

I reproduced the general mechanism directly (outer repo + no-`.git` subdirectory two levels down):
`git rev-parse --is-bare-repository` succeeds from the nested dir and reports `false`, and a
subsequent `git config receive.denyCurrentBranch updateInstead` run from that same nested cwd lands
in the **outer** repo's `.git/config` — confirmed by reading it back afterward. The test suite never
catches this because `ZeroWikiAppFactory`/`ContentRepositoryServiceTests` both root their `DataRoot`
under `Path.GetTempPath()`, which isn't nested in any repo — so 490/490 green is not evidence this
path is safe.

Fix needs to check whether **`repositoryRoot` itself** is a git repository — e.g. test
`Directory.Exists(Path.Combine(repositoryRoot, ".git"))` (non-bare) in C# before ever shelling to
`git`, falling back to treating `repositoryRoot` itself as a candidate bare repo (`HEAD` +
`objects`/`refs` directly present) only when that's absent — rather than relying on `git rev-parse`'s
own ancestor-discovery semantics for the initial detection.

**Nits**

1. `GitProcessRunner.cs` declares three top-level types (`GitProcessRunner`, `GitProcessResult`,
   `GitProcessException`) in one file — the only file in `src/` doing this (checked). Split into
   `GitProcessResult.cs` / `GitProcessException.cs` per the codebase's one-type-per-file convention,
   especially since §3/§6/§7 will all import these.
2. `ContentRepositoryServiceTests.cs:112-133`
   (`Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch`) mutates the process-wide
   `GIT_CONFIG_GLOBAL` environment variable via `Environment.SetEnvironmentVariable`. xUnit runs test
   classes in parallel by default, so any `git` subprocess spawned by a concurrently-running class
   (e.g. `ContentRepositoryStartupTests`, or any other test that boots `ZeroWikiAppFactory`) during
   this test's window inherits the bogus global config. Blast radius is nil today — every `git init`
   call in this code passes `-b main` explicitly — but it's worth scoping narrower (e.g. don't rely on
   process-wide env for a value only one child process should see) before a future git invocation
   depends on unset config.

**Architectural notes**

- `GitProcessRunner.RunAsync` threads `cancellationToken` into `WaitForExitAsync`/`ReadToEndAsync` but
  never kills the process on cancellation — `Process.Dispose()` doesn't terminate a still-running
  process, so cancelling only stops *waiting*. Not exercised here (no caller in this block passes a
  token that's ever actually cancelled), but worth closing before §6's commit-on-save path reuses this
  runner for a request that can be aborted mid-commit.
- `GitProcessException`'s message includes the raw joined argument list. Harmless for this block's
  arguments (`init`/`config`/`commit`), but §7 will shell to `git` with token-bearing remote URLs
  through the same runner — flagging now so that caller redacts before it ever reaches a log or an
  exception message.

→ @worker

**[architect]** Ruling on the review of `2.1–2.2`. **Request changes upheld.** I found the same blocker
independently before the reviewer reported, and the two findings are worth recording as genuinely
independent rather than as one result stated twice — the standing rule from §11 is that two
measurements agreeing is not corroboration when they share an instrument. They do not here. I ran
`git rev-parse --is-bare-repository` in the **real** `src/ZeroWiki/App_Data`, and got `false` / exit 0
with `--show-toplevel` naming the ZeroWiki checkout and `--verify HEAD` resolving to `7b50e46` — the
production symptom, on the actual path, read-only. The reviewer built a nested sandbox and confirmed
the *consequence* by reading the outer repository's `.git/config` back after a `git config` from the
nested cwd. One measured the classification, the other measured the write. Different instruments,
same conclusion.

**Why the suite is green over it, which is the part worth keeping.** Both harnesses root `DataRoot`
under `Path.GetTempPath()`, which is outside any repository, so 490/490 says nothing about the case
that is broken. The single configuration this defect needs — a data root nested inside a git repo — is
exactly the configuration a developer runs and no test has. That is the §11 lesson one level out: the
blind spot was the **fixture**, not the diligence.

### To fix

1. **Blocker — classify the repository without git's upward discovery.** Decide case (a)/(b)/(c) from
   whether `RepositoryRoot` *itself* is a repository, in C#, before shelling out at all. Treat a `.git`
   **file** as well as a directory as "present": a gitfile means a worktree or submodule layout, which
   is not a case to initialize over.
2. **Blocker, second half — assert what git actually resolved.** Fixing classification stops this
   path, but every later section reuses the runner, and the failure mode is silent. After ensuring the
   repository, assert that `git rev-parse --show-toplevel` **equals** `ContentPaths.RepositoryRoot`,
   and fail loudly if it does not. Mechanism is the worker's call (`GIT_CEILING_DIRECTORIES`, an
   explicit `--git-dir`, or the assertion itself) — but whatever is chosen must be **verified against a
   nested-repo reproduction**, not reasoned about. Git's ceiling-directory semantics are easy to state
   backwards.
3. **Regression test with the fixture that was missing** — a data root nested inside a git repository.
   Without it the next refactor reintroduces this and the suite stays green again.
4. **Nit — split `GitProcessRunner.cs`.** Three top-level types in one file; it is the only file in
   `src/` doing so.
5. **Nit — the branch-override test mutates process-wide `GIT_CONFIG_GLOBAL`.** Low practical impact
   today because every `git init` here passes `-b main` explicitly, but it is a cross-class flakiness
   source under xUnit's default parallelism, and this change has already paid for one filtered-versus-
   parallel discrepancy.

### Parked, not fixed here — each owed by a named section

Both are real; neither is a defect in *this* block's behaviour, and fixing them here is the scope creep
the supervisor exists to catch. Carried to `## NEXT`.

- **§6 — `GitProcessRunner` does not kill the subprocess on cancellation.** Inert today: startup passes
  `CancellationToken.None`, so there is nothing to cancel. §6 is where an abortable commit-on-save
  makes it live.
- **§7 — `GitProcessException`'s message carries the raw argument list.** Inert today: bootstrap passes
  no secrets. §7 passes token-bearing URLs through the same runner, at which point the exception
  message — and anything that logs it — becomes a credential leak. `CapturingLoggerProvider` already
  exists to sweep logs for exactly this.

**[architect]** → `@worker`: fix round on `2.1–2.2`, items 1–5 above. Not a new block and nothing new
is ticked. → `@reviewer` re-audits after.

**[worker]** Fix round on `2.1–2.2` — all five items addressed.

1. **Blocker, classification.** `EnsureRepositoryAsync` no longer asks git anything before knowing
   whether `repositoryRoot` has a repository of its own. `HasOwnGitEntry` checks
   `Directory.Exists`/`File.Exists` on `<repositoryRoot>/.git` in C# — a gitfile counts as present
   (worktree/submodule, not something to initialize over), matching the ruling. Only when that
   entry exists does the code ask git `rev-parse --is-bare-repository`, which is now safe: with
   `.git` confirmed present at `repositoryRoot`, discovery from that cwd always finds it first,
   ancestor or not. Reproduced the original bug directly (outer repo + no-`.git` nested dir two
   levels down: `rev-parse --is-bare-repository` from the nested dir returns `false` against the
   *outer* repo) and confirmed the fixed classification no longer shells to git in that path at all.
   **Extra defect caught while fixing this**: a bare repository has no `.git` entry — `HEAD`/
   `objects`/`refs` sit directly in the repo root, which is what makes it bare — so `HasOwnGitEntry`
   alone would have misrouted case (c) into "absent, initialize", silently laying a working tree over
   an existing bare repo. Added `LooksLikeBareGitDirectory` (checks `HEAD` + `objects/` + `refs/`
   directly in `repositoryRoot`) as an explicit third branch before the "initialize" fallback.
2. **Blocker, assertion.** Added `AssertGitResolvesRepositoryRootAsync`, run once at the end of
   `EnsureRepositoryAsync`. Chose "the assertion alone" (no `GIT_CEILING_DIRECTORIES`): it runs
   `git --git-dir=.git --work-tree=. rev-parse --show-toplevel` (discovery bypassed entirely) and
   compares it to plain `rev-parse --show-toplevel` (discovery as every other caller through this
   runner will use it) with cwd = `repositoryRoot` both times, and throws if they differ. Verified
   against a real nested-repo reproduction rather than reasoning about it, per the ruling — and the
   reproduction caught a design mistake before it shipped: comparing git's output against
   `Path.GetFullPath(repositoryRoot)` (a C# string) fails on macOS, because `--show-toplevel`
   resolves symlinks and `Path.GetFullPath` doesn't — confirmed `/tmp/…` vs. `/private/tmp/…` on this
   machine's own temp directory, which would have made the assertion misfire on every test run rather
   than only on the bug it's meant to catch. Comparing two git-resolved paths instead sidesteps that
   entirely, and the same reproduction confirmed it degrades correctly when the bug *is* present: run
   from the nested dir before `git init`, plain discovery returns the outer repo's toplevel while the
   `--git-dir` form would fail (no `.git` yet) — either way the two diverge and the assertion fires.
3. **Regression test** — `ContentRepositoryServiceTests.DataRootNestedInsideAnAncestorGitRepository_InitializesItsOwnRepositoryInstead`:
   an ancestor repo with its own commit, `repositoryRoot` nested inside it with no `.git` of its own.
   Asserts the nested repo gets initialized correctly (non-bare, `docs/.gitkeep` tracked, one
   `System`-authored commit, configuration applied, clean porcelain) **and** that the ancestor is
   untouched — no `receive.denyCurrentBranch` written into it, its own single commit still exactly
   one. This is the fixture that was missing; both existing harnesses root `DataRoot` under
   `Path.GetTempPath()`, which sits inside no repository.
4. **Nit, file split.** `GitProcessResult.cs` and `GitProcessException.cs` are now their own files;
   `GitProcessRunner.cs` holds only the runner.
5. **Nit, `GIT_CONFIG_GLOBAL`.** `Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch` no
   longer touches `Environment.SetEnvironmentVariable` at all. It now calls
   `_git.RunOrThrowAsync(repositoryRoot, ["init", "-b", DefaultBranch], environmentVariables: …)` —
   the exact invocation shape `ContentRepositoryService` uses — with `GIT_CONFIG_GLOBAL` scoped to
   that one subprocess via `GitProcessRunner`'s existing per-call environment override, never the
   process-wide environment other concurrently-running test classes' git subprocesses would inherit.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **491/491** full unfiltered (490 + 1 net
new: the nested-repo fixture; the `GIT_CONFIG_GLOBAL` test was rewritten, not added) ·
`dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid. `git status --short` / `git diff --stat` reviewed: only this block's files, plus the two new
split files and this DEVLOG entry. No mutation testing, per the original brief's scoping call.

Explicitly untouched, per the ruling's scope: 2.3–2.5, hooks, reconciliation, self-check, and the two
items parked to §6/§7.

→ @reviewer

**[reviewer]** 2.1–2.2 re-audit over the full working-tree diff against `7b50e46` — **Approve.**

This certifies the current tree (`ContentRepositoryService.cs`, `GitProcessRunner.cs` +
`GitProcessResult.cs` + `GitProcessException.cs`, `GitAuthor.cs`, the startup wiring, both test
files), not only the fix-round delta.

**Blocker verified closed, by reproduction, not by reading the fix.**

- Re-ran the original nested-repo scenario (an ancestor repo with a commit, `repositoryRoot` nested
  inside it with no `.git` of its own) against the code **as it stood before this fix round** —
  reconstructed from the previous review — and confirmed independently what the finding predicted:
  `bareProbe` reports `"false"` (discovering the ancestor), the old code takes the "existing repo"
  branch, writes `receive.denyCurrentBranch=updateInstead` into the **ancestor's** `.git/config`, and
  the `HEAD` probe (also discovering the ancestor, which already has a commit) returns early —
  `repositoryRoot/.git` is never created. `Directory.Exists(Path.Combine(repositoryRoot, ".git"))`
  — the new regression test's first assertion — genuinely fails against that code. The new test is
  not vacuous.
- `HasOwnGitEntry`/`LooksLikeBareGitDirectory` (`ContentRepositoryService.cs:106-120`) decide
  classification entirely in C#, before any git process runs, closing the path the blocker exploited.

**The third branch (`LooksLikeBareGitDirectory`) does what it claims, checked specifically as asked.**

- A real `git init --bare` produces `HEAD` + `objects/` + `refs/` directly at the repo root with no
  `.git` entry — confirmed against a real bare repo. The three-marker check is the right shape: no
  ordinary wiki content can produce it by accident, since page content lives under `docs/`
  (`ContentPaths.WorkingTree`), a subdirectory of `repositoryRoot`, never at `repositoryRoot` itself —
  so `HEAD`/`objects`/`refs` sitting directly in `repositoryRoot` has no legitimate non-bare-repo
  explanation. The only way to false-positive this is to hand-place a file named `HEAD` and
  directories named `objects`/`refs` directly in the repository root before first start, which no
  normal operation does. Worth naming as a known, accepted tradeoff rather than a gap — it isn't
  worth blocking on.
- Order is correct: `HasOwnGitEntry` is checked first, so a directory that happens to have both a
  `.git` entry and stray `HEAD`/`objects`/`refs` siblings is correctly classified from the `.git` it
  actually has, not misrouted into the bare branch.

**The defense-in-depth assertion (`AssertGitResolvesRepositoryRootAsync`) fires on divergence and is
not vacuous — checked by reproduction in both directions:**

- Simulated the same nested-repo shape and ran the assertion's two invocations directly: with cwd at
  the nested, `.git`-less directory, `git --git-dir=.git --work-tree=. rev-parse --show-toplevel`
  fails outright (`fatal: not a git repository: '.git'`, exit 128) while plain `rev-parse
  --show-toplevel` resolves to the ancestor — the two diverge (one throws, one succeeds with a
  different value), so the assertion is a genuine backstop, not a comparison that can never fail.
  Because classification now runs first, this path is unreachable in the current code (it would only
  fire if classification itself had a bug) — which is exactly what "defense in depth" means here.
- Confirmed the symlink reasoning independently rather than taking it on trust: on this machine,
  `git init` under `/tmp/…` (a symlink to `/private/tmp/…`) has `rev-parse --show-toplevel` and the
  explicit `--git-dir` form **agree** on the resolved `/private/tmp/…` path, while a naive
  `Path.GetFullPath("/tmp/…")` would retain the unresolved `/tmp/…` form — so comparing against a
  C#-computed path would have produced a false positive on exactly this host. Comparing two
  git-resolved paths avoids it. Also confirmed the `--git-dir`/plain-discovery comparison agrees
  correctly for a linked worktree (gitfile, not a `.git` directory) — no false divergence there either.

**Nits addressed, confirmed in the diff:**

- `GitProcessRunner.cs` now holds only the runner; `GitProcessResult.cs` and `GitProcessException.cs`
  are their own files — one type per file, matching the rest of `src/`.
- `Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch` (`ContentRepositoryServiceTests.cs:112-135`)
  no longer touches `Environment.SetEnvironmentVariable`; `GIT_CONFIG_GLOBAL` is scoped to the single
  `git init` subprocess via `GitProcessRunner`'s own per-call environment override. The cross-class
  flakiness source is gone.

**Optional micro-nit, not blocking:** the two "bare repository" `InvalidOperationException` sites
(`ContentRepositoryService.cs:61-64` and `:74-76`) duplicate the same message text; a small private
helper would remove the duplication if it's ever touched again, but it isn't worth a fix round on its
own.

Confirmed `git diff -- src` / `git status --short -- src` show only this block's files — no mutation
residue, consistent with no mutation run having been started for this block, per the brief's scoping.

Parked items (subprocess-kill-on-cancellation → §6, `GitProcessException` raw-argument redaction →
§7) correctly not re-raised here — both remain inert in this block's actual usage and are already
owed by name in `## NEXT`.

→ @architect: 2.1–2.2 clear to commit.

**[architect]** One more small fix round before committing — a finding of my own, not a reopened
review. `AssertGitResolvesRepositoryRootAsync` ran *last* (after `ApplyRepositoryConfigurationAsync`
and the initial commit), which makes it a backstop that reports the blocker's damage rather than
preventing it: if classification ever regresses, the config writes and the commit would already have
landed in whatever repository git discovered before the assertion had a chance to throw. → `@worker`:
move it to run immediately after classification, before any write; every surviving branch leaves
`.git` present at `repositoryRoot` by then, so it has what it needs. Also folded in the reviewer's
micro-nit on the duplicated bare-repository message while this round is open. Confirm by reproduction,
not reasoning, as before.

**[worker]** Fix round on `2.1–2.2`, ordering + micro-nit.

- **Reordered.** `AssertGitResolvesRepositoryRootAsync` now runs immediately after the
  classification `if`/`else if`/`else` and before `ApplyRepositoryConfigurationAsync`/
  `EnsureInitialCommitAsync` (`ContentRepositoryService.cs:91-99`). Every branch that doesn't throw
  leaves `.git` present at `repositoryRoot` — found as-is, or just created by `init` — so the
  assertion has everything it needs at that point.
- **Confirmed by reproduction, not reasoning.** Built the same nested-repo scenario as the earlier
  fix round (ancestor repo with a commit, `repositoryRoot` nested inside with no `.git` of its own),
  then deliberately ran the *old*, buggy classification (`git rev-parse --is-bare-repository` from
  the nested dir, trusting discovery — reports `false`, i.e. "existing non-bare repo, use as-is").
  With the assertion in its new position: `git --git-dir=.git --work-tree=. rev-parse --show-toplevel`
  fails outright (`fatal: not a git repository: '.git'`, since the nested dir has no `.git` of its
  own) — the assertion throws before any config write runs — and reading the ancestor's
  `.git/config` back afterward confirms `receive.denyCurrentBranch` was never set there (lookup
  exits 1). For contrast, ran the same two `git config` writes with cwd = the nested dir *first* (the
  old ordering) and confirmed they land straight in the **ancestor's** `.git/config` —
  `denyCurrentBranch = updateInstead` and `receivepack = true` both show up in
  `outer/.git/config` — which is exactly the damage the reordering prevents.
- **Micro-nit folded in.** The two "bare repository" `InvalidOperationException` call sites now
  share one message via a private `BareRepositoryException(string repositoryRoot)` helper
  (`ContentRepositoryService.cs:102-105`); no more duplicated text.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **491/491** full unfiltered · `dotnet
format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict` valid.
`git status --short` / `git diff --stat -- src` reviewed: only this block's files, no mutation
residue (none run). Nothing else touched — §6/§7 park items and 2.3–2.5 untouched, no mutation
testing.

→ @architect: 2.1–2.2 clear to commit.

**[reviewer]** 2.1–2.2 re-audit, ordering fix + micro-nit — **Approve.** This certifies the tree as it
now stands (`ContentRepositoryService.cs:91-105` and everything already certified in the prior round).

**Ordering change verified by reproduction, not on report.**

- Confirmed the new position (`AssertGitResolvesRepositoryRootAsync` at line 96, ahead of
  `ApplyRepositoryConfigurationAsync`/`EnsureInitialCommitAsync` at 98–99) by re-running the exact
  causal chain myself: on a fresh nested-repo fixture (ancestor with a commit, `repositoryRoot`
  nested inside with no `.git` of its own), simulating the naive/reverted classification's
  `bareProbe` reports `"false"` (discovers the ancestor) exactly as before, then ran the assertion's
  two invocations in the new position — `git --git-dir=.git --work-tree=. rev-parse --show-toplevel`
  fails outright (`fatal: not a git repository: '.git'`) — and confirmed the ancestor's
  `receive.denyCurrentBranch` is still unset afterward. Under the old ordering (config writes first,
  assertion last) the same reproduction lands both keys in the ancestor's `.git/config` before
  anything throws. The reordering is a genuine fail-before-write, not fail-and-report.

**Checked the specific hazard raised — reachability across all three classification branches, and
no spurious failure against an unborn HEAD.**

- Both throwing branches (bare via `bareProbe`, bare via `LooksLikeBareGitDirectory`) return before
  reaching the assertion — correct, no write should be attempted for either. The two non-throwing
  branches (existing non-bare repo; freshly `git init`'d) both leave `.git` present at
  `repositoryRoot` before the assertion runs, so it's reachable with what it needs in both.
- Specifically checked the fresh-init path, which is the one that reaches the assertion with **zero
  commits**: ran `git init -b main` in a scratch directory and confirmed both `git rev-parse
  --show-toplevel` and the explicit `--git-dir=.git --work-tree=. rev-parse --show-toplevel` succeed
  and agree on an unborn-HEAD repository (`rev-parse --verify -q HEAD` fails there, exit 1, but
  `--show-toplevel` doesn't depend on history — it succeeded, exit 0, on both invocations). Moving the
  assertion ahead of `EnsureInitialCommitAsync` does not introduce a spurious failure on the init path.

**Micro-nit fix confirmed:** `BareRepositoryException(string repositoryRoot)` (`:102-105`) is used at
both former call sites; no duplicated message text remains.

**Mutation-hazard check, independently:** `git status --short -- src tests` shows only this block's
own tracked additions, no stray untracked files; `git diff --stat` matches the files under review;
this repository's own `receive.denyCurrentBranch`/`http.receivepack` remain unset — the classification
fix holds against the very repo this change is being built in, not just the test fixtures. No
mutation run was needed or started for this fix — it's a reproducible ordering property, not a
security/concurrency/data-integrity path per CLAUDE.md's mutation scoping.

Parked items (§6 cancellation-kill, §7 argument redaction) remain correctly parked, not re-raised.

→ @architect: 2.1–2.2 clear to commit.

## NEXT

**Resume point: §2 (Repository bootstrap & invariant), first block.** §11 is **closed** — supervisor
`Approve` on round two over `bb3cb2c..HEAD`.

**State: 7/40 tasks ticked** *(counted from `tasks.md`, not carried forward)*. Branch
`change/git-backed-content-core`. Gates at close-out, run by the Architect rather than relayed:
`dotnet build` 0/0, `dotnet test` **483/483** full unfiltered, `dotnet format --verify-no-changes`
exit 0, `openspec validate --strict` valid.

| Section | Block | Commit | Reviewer | Supervisor |
|---|---|---|---|---|
| §0 design | D8–D11 + spec delta | `bd2eeea` | — | — |
| §1 | 1.1–1.3 | `3f6d837` | Approve w/ nits | Request changes → **Approve** |
| §1 | remediation (blocker + notes 2, 4) | `7102eed` | Approve | ↑ |
| §1 | 1.4 DataProtection | `0247443` | Approve | ↑ |
| §1 | close-out (docs) | `bb3cb2c` | — | — |
| §11 | 11.1–11.3 | `70a31aa` | Request changes → Approve w/ nits | Request changes (S1) → **Approve** |
| §11 | remediation (S1 + comment corrections) | `2af401c` | Request changes → **Approve** | ↑ |

**Execution order from here: §2 → §3 → … → §10.** §11 is done; the remaining sections run in
`tasks.md` order.

### Forward obligations — each is owed by a specific section

1. **§5 — the lockfile must not live in the working tree.** A lockfile under `/data/wiki/docs` is an
   untracked file, which makes the tree dirty, which D9 dutifully commits, and `updateInstead` then
   bounces every push against a tree it believes unclean. Put it under `.git/` or beside the
   repository, and **extend `ContentPaths`** rather than growing a parallel notion of where things
   live.
2. **§8 — the image has no HTTP client.** `curl`, `wget` and `nc` are all absent from the runtime
   image; `flock` **is** present, so §5.3 is safe. Decide how `post-receive` signals the app **before**
   §8 starts, or it reopens §1's Dockerfile.
3. **§7 — `git-receive-pack` returns `403 Forbidden` in this image.** git's export policy
   (`http.receivepack` unset), **not** ownership or auth — setting `http.receivepack=true` flips the
   identical invocation to 200. Recorded because it reads as an authentication bug to whoever meets it
   first. Belongs in §2's repo config.
4. **§2 — resolve `ContentPaths` from DI.** The registered singleton has **zero consumers in `src/`**;
   only `ResolveContentPaths` at `Program.cs:88` reads the data root, because DataProtection must be
   configured before `Build()`. §2's brief should say *inject it*.
5. **A latent trap in the test harness.** `ResolveContentPaths` reading configuration before `Build()`
   works **only** because `ZeroWikiAppFactory` uses `UseSetting`. A future harness using
   `ConfigureAppConfiguration` would hand the DI singleton the override while the key ring silently
   took the `/data` default. `LoginPageTests.cs:214`'s on-disk assertion is the real guard — **do not
   soften it**.
6. **§6 — the author line must be well-formed for accounts that predate the username rules.** D10's
   `Consequence binding §6`, now also a **scenario** in `specs/content-editing/spec.md` so §6's
   section review is gated on it rather than trusting prose. Non-retroactivity is deliberate and
   correct, which is exactly why §11 could not discharge this: `LoginServiceTests.cs:271-289` pins
   `.old.name.` still authenticating, and §6 constructs the address. Also tell §6's brief that the
   username is **immutable by consequence** — a permanent artifact plus no rename path.
7. **§6 — `GitProcessRunner` does not kill the git subprocess on cancellation.** Parked deliberately in
   §2's first block, where it is inert: startup passes `CancellationToken.None`, so there is nothing to
   cancel. §6's abortable commit-on-save is what makes it live, and §6 owns the fix.
8. **§7 — `GitProcessException`'s message carries the raw argument list.** Also parked from §2's first
   block, also inert there: bootstrap passes no secrets through the runner. §7 passes token-bearing
   URLs through the same runner, at which point the exception message — and anything that logs it —
   becomes a credential leak. `CapturingLoggerProvider` already exists to sweep logs for exactly this,
   so §7's brief should say *test it*, not merely *avoid it*.

### Close-out items before archive

- **F2 (nit, pre-existing from `bd2eeea`)** — `specs/user-accounts/spec.md`, scenario *"Well-formed
  username is accepted"*: its WHEN is satisfied by `a..b`, which the new scenario refuses, and it
  already omitted the 64-character cap before this change touched it. One clause. It has always been
  illustrative rather than a description of the accepted set.
- **F1 (low)** — `CredentialPolicy.cs:85-91`'s slack claim ("an over-long name is reported as a length
  problem rather than also being told its charset is wrong") holds at the **service** at every length,
  but at the **form** only to 127; a 128-character alphanumeric name gets two messages for one fault.
  Name the surface it holds on, or its bound. **Unverified by the Architect** — reproduce before
  fixing.
- Page-test matrices are asymmetric: `BootstrapPageTests.cs:83-94` covers `"café"` and `"admin\tx"`;
  `RedeemInvitationPageTests.cs:262-271` omits both.

### Standing rules earned in §0–§11

- **A differential is only as good as the surface it models.** When a rule is enforced on two
  surfaces, run the differential on **both** — two 666k-input runs missed F1 because both classified
  through the service model, in the same section that discovered the form disagrees with it (§11).
- **Produce every counterfactual on the engine that ships.** `new Regex(...)` is **not**
  `[GeneratedRegex]`; they disagreed by ~80x here and produced a fully self-consistent wrong answer
  (§11).
- **A timing comment may assert a direction or an order of magnitude, never a precise value**, and
  every timing figure names its engine. A precise value measures a machine and rots silently on
  someone else's (§11).
- **An assertion's justification is a claim about a counterfactual**, and the only instrument that
  checks it is the mutation that produces it. Seven claims-about-why were wrong in §11 while the code
  was fine; every one was caught by mutating, none by reading (§11).
- **A mutation harness must `cp` the target aside and restore from that copy.** `git checkout --` /
  `git restore --` restore from `HEAD` and take an uncommitted block's own edits with them — it
  happened here despite the rule already being written down, and the **checksum-before-and-after** is
  what caught it, twice (§1, §11).
- **A raw checksum is not a valid instrument for a live WAL-mode SQLite file** — checkpointing churns
  pages with no logical change. Compare `sqlite3 .dump` or row counts (§1).
- **Any regex harness must carry an instrument self-check** — Perl interpolated `$\` out of a pattern
  and produced a fully self-consistent wrong answer (§0).
- **`safe.directory` is `--system`, set as root before the `USER` switch.** HOME-scoped `--global`
  dies in §7's CGI subprocess.
- **Every instrument failure in this change so far has been in the harness, not the code.** Assume the
  measurement is wrong before assuming the finding is real.

Design questions outstanding: **none.** `design.md`'s Open Questions are fully resolved, and D11 now
states the `accepted ⊆ legal` posture whose absence was S1.
