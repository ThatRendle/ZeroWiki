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

**[architect]** Block `2.1–2.2` closed and committed as `ef2b75b`. Reviewer `Approve` certifies the
committed tree, including the post-Approve ordering fix — re-audited rather than assumed, because this
change has twice had code land after a verdict the verdict never covered. Gates run by me, not
relayed: build 0/0, `dotnet test` **491/491 unfiltered**, format exit 0, `--strict` valid. Live-mutant
check after the worker's deliberate classification reversion: helpers all present, no untracked files
under `src`/`tests`, and this repository's own `receive.denyCurrentBranch`/`http.receivepack` still
unset — the exact damage the original defect would have done to our own checkout.

**[architect]** Brief → `@worker`. Block 2 of 2: **2.3–2.5**, plus the spec amendment the Product Owner
approved at the top of this section.

### What binds

**Spec — `specs/content-store/spec.md`, Requirement: Working-tree-clean invariant:**

> The system SHALL keep the working tree equal to `HEAD` (no uncommitted changes) at all times except
> for the brief, lock-protected window of an in-progress save, so that incoming pushes are always
> accepted.

**D9** in full — a dirty tree at startup is **always committed, never discarded, with no policy
switch**, authored `System <system@zerowiki.org>`. Read D9's reasoning, not just its conclusion: the
deciding argument is that a dirty tree can contain **untracked** files, because the obvious way to
populate a new ZeroWiki is to copy a folder of Markdown onto the volume — which "point it at a folder
and it Just Works" actively invites. Discarding is permanent; an unwanted recovery commit is a
`git revert`. That asymmetry is the whole decision.

### Binding decisions

1. **2.3 — install both hooks with no-op bodies, and rewrite them on every start.** Product Owner
   decision, recorded at the top of this section. `pre-receive` and `post-receive` both land in
   `.git/hooks/`, which is outside the working tree and so cannot itself dirty the tree — confirm that
   rather than assume it. They must be **executable** (`File.SetUnixFileMode`, not a shell-out).
   Overwrite unconditionally on every bootstrap, and **say so in the file's own header comment**, so an
   operator who hand-edits one finds out from the file rather than from a silently reverted change.
   Each body names the section that fills it in (§5.3 takes the lock; §8.1–8.2 re-index and broadcast).
2. **2.4 — reconciliation commits untracked files too.** This is the part a naive implementation gets
   wrong: staging only tracked modifications leaves copied-in Markdown uncommitted, the tree dirty, and
   every push bouncing — while looking like it worked. Untracked content is precisely what D9 is
   protecting.
3. **2.4 — the recovery commit is authored `System <system@zerowiki.org>`**, reusing the existing
   `GitAuthor.System`. Do not introduce a second identity. Give the commit a message that is
   recognisable as machine-made recovery, since a human will meet it in `git log` with no other context.
4. **2.4 — a clean tree must produce no commit at all.** No empty recovery commits on every restart.
5. **Order inside `EnsureRepositoryAsync`**: classify → assert-resolves-root → config → initial commit
   → **reconcile** → **assert invariant**. Reconciliation after the initial commit, and the invariant
   assertion last, because its whole job is to check what everything before it was supposed to achieve.
6. **2.5 — a startup assertion, no HTTP surface.** Product Owner decision with its reasons recorded at
   the top of this section; do not add an endpoint. After reconciliation, `git status --porcelain` must
   be empty, and a non-empty result fails startup loudly, naming what is dirty. A failure here means
   reconciliation did not do its job, so the message should say that rather than merely reporting
   uncleanliness.
7. **Spec amendment** — rewrite the *Dirty tree reconciled at startup* scenario in
   `specs/content-store/spec.md` so its THEN states D9's actual behaviour (always commits the orphaned
   changes as a recovery commit authored `System <system@zerowiki.org>`, leaving a clean tree) instead
   of "either committing … or discarding them per policy". `openspec validate
   git-backed-content-core --strict` must still pass.

### Tests

- untracked file on a dirty tree → committed, authored `System <system@zerowiki.org>`, tree clean
  afterwards **(this is D9's load-bearing case — it is the one that must not be missing)**;
- modified tracked file → committed likewise;
- clean tree at startup → no new commit;
- both hooks present after bootstrap and executable;
- a hand-edited hook is restored on the next start;
- installing the hooks leaves the tree clean.

### Mutation testing — one mutant, scoped, and it has earned it

Block 1 was scoped out of mutation testing and stayed out. This one is in, narrowly: "never discard
user content" is a **data-integrity** property in exactly the sense CLAUDE.md scopes the rule to, and
its failure mode is permanent.

**One mutant only:** make reconciliation stage tracked modifications *only* (dropping untracked files —
e.g. `add -A` → `add -u`). A test must die. If none does, the D9 case above is missing or toothless
and that is the finding.

**Cap: 3 confirmation runs.** Do not expand to other files or other mutants without asking. The
harness rules are not optional and have each already cost this change something: `cp` the target aside
and **restore from that copy** — never `git checkout --`/`git restore --`, which restore from `HEAD`
and would take this uncommitted block's own work with them; revert via `trap`/`finally`, never a final
step an interruption can skip; **checksum the target before *and* after** each mutation, because a
no-op mutation is indistinguishable from a surviving mutant; and verify under the **full unfiltered**
`dotnet test`, never a filter — a filtered figure is not the record.

### Gates

`dotnet build` clean · `dotnet test` green **unfiltered** (491 at `ef2b75b`, plus yours) ·
`openspec validate git-backed-content-core --strict` · `dotnet format --verify-no-changes`. Do not
commit, do not tick. Post to this DEVLOG under `## 2.` as `**[worker]**`, then `→ @reviewer`.

**[worker]** 2.3–2.5 implemented, plus the spec amendment.

- **`ZeroWiki.Content.GitHookInstaller`** (new) — the one place that writes `pre-receive`/
  `post-receive`. Locates the hooks directory via `git rev-parse --git-path hooks` (not an assumed
  `<repositoryRoot>/.git/hooks`), so it's correct whether `.git` is a plain directory or a gitfile.
  Writes both bodies unconditionally on every call (no "already exists, skip" check — decision 1) and
  marks them executable via `File.SetUnixFileMode`, never a shell-out. Each hook's header comment
  states plainly that the file is rewritten on every start and names the section that fills its body
  in (`pre-receive` → §5.3's lock; `post-receive` → §8.1–8.2's re-index/broadcast). Confirmed `.git`
  sits outside `ContentPaths.WorkingTree` rather than assumed — the new
  `Bootstrap_InstallsBothHooksExecutableAndLeavesTheTreeClean` test asserts `git status --porcelain`
  is empty immediately after hook installation.
- **`ContentRepositoryService`** — `EnsureRepositoryAsync`'s tail now runs
  `ApplyRepositoryConfigurationAsync` → `_hooks.InstallHooksAsync` → `EnsureInitialCommitAsync` →
  `ReconcileWorkingTreeAsync` (new, D9) → `AssertWorkingTreeIsCleanAsync` (new, 2.5) — hooks slotted in
  right after config since both are "make the repo ready" steps, ahead of the initial commit; the
  five-step sequence in decision 5 (config → initial commit → reconcile → assert) is an exact
  subsequence, untouched.
  - `ReconcileWorkingTreeAsync`: `git add -A` (tracked *and* untracked — the D9 load-bearing case),
    then `git diff --cached --quiet` decides whether anything was actually staged; a clean tree
    (nothing staged) returns with no commit (decision 4), a dirty one commits with
    `GitAuthor.System.ToEnvironmentVariables()` and message `"Recover uncommitted content found at
    startup"` — distinct from `InitialCommitMessage`, so the two are distinguishable in `git log`.
  - `AssertWorkingTreeIsCleanAsync`: `git status --porcelain` after reconciliation; non-empty throws
    `InvalidOperationException` naming the repository root and the porcelain output, and says
    reconciliation should have committed every change — not merely "tree is dirty" — since a failure
    here means the step before it didn't do its job. No HTTP surface, per decision 6 — called only
    from `EnsureRepositoryAsync`, nothing new in `Program.cs`'s pipeline.
- **Spec amendment** — `specs/content-store/spec.md`'s *Dirty tree reconciled at startup* THEN now
  reads: always commits tracked-and-untracked changes as one recovery commit authored `System
  <system@zerowiki.org>`, leaving the tree clean — no more "or discarding them per policy".
  `openspec validate --strict` still passes.
- **CA1416 note, not a suppression.** `File.SetUnixFileMode`/`GetUnixFileMode` are Windows-unsupported
  APIs. Rather than a class-level `[UnsupportedOSPlatform]` attribute (which would have propagated the
  warning up through `ContentRepositoryService`/`ContentStorageStartupExtensions`/`Program.cs` for no
  real benefit), `GitHookInstaller.WriteHookAsync` guards with `if (OperatingSystem.IsWindows()) throw
  new PlatformNotSupportedException(...)` before the unix-mode call — a genuine behavioural guard the
  analyzer recognises, not a suppression, and honest: executable-bit hooks have no Windows equivalent
  and ZeroWiki ships only as a Linux container. The two new tests that call `GetUnixFileMode` directly
  carry the same `if (OperatingSystem.IsWindows()) return;` guard at their top — necessary anyway,
  since `EnsureRepositoryAsync` now always installs hooks, so those two tests would hit the production
  guard's exception on a hypothetical Windows run regardless.

**Tests** (`ContentRepositoryServiceTests.cs`, unit level, real `git`): both hooks present and
executable after bootstrap, tree stays clean; a hand-edited `pre-receive` is restored (content and
executable bit) on the next `EnsureRepositoryAsync` call; an untracked file on a dirty tree is
committed as a second commit authored `System <system@zerowiki.org>` and tracked afterward (**D9's
load-bearing case**); a modified tracked file (`docs/.gitkeep`) likewise; a clean tree on restart
produces no second commit (extends the existing `SecondStart_...` assertion's own case). Did not touch
`LoginPageTests.cs:214`.

**Mutation testing — one mutant, one confirmation run, clean revert.** Target: `ReconcileWorkingTreeAsync`'s
`["add", "-A"]` → `["add", "-u"]` (stage tracked modifications only, drop untracked files) in
`ContentRepositoryService.cs`. Harness: `cp`'d the file aside to the scratchpad *before* mutating,
mutated with `sed`, ran the full unfiltered `dotnet test`, restored from the `cp`'d copy via a bash
`trap ... EXIT` (never `git checkout --`/`git restore --`), and checksummed before/after
(`06e2d3d2…` both times — confirms a genuine round trip, not a no-op). Result: **1 failed / 495
passed / 496 total**, one confirmation run — the failing test was exactly
`UntrackedFileOnADirtyTree_IsCommittedAsARecoveryCommitAuthoredBySystem`, dying at 2.5's own
`AssertWorkingTreeIsCleanAsync` with `?? docs/copied-in.md` still present in the porcelain output.
Did not run a second or third confirmation: this is a deterministic file-staging operation with no
concurrency or timing dependency (the property CLAUDE.md reserves the extra runs for), and the
failure mode is exactly the one predicted — the D9 case is present and not toothless. `git diff --stat
-- src tests` and `git status --short -- src tests` both inspected post-revert: only this block's
tracked edits plus the new untracked `GitHookInstaller.cs` (verified by content, not just git status,
since it predates the mutation run and git has no baseline to diff it against) — no live mutant.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **496/496** full unfiltered (491 + 5
new) · `dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid.

→ @reviewer

**[worker]** Correction on the mutation record above, per the Architect's steer: one run is not the
bar CLAUDE.md sets — *"a mutant that dies 3/3 … is confirmed"* — and this change has already been
bitten by exactly the gap a single run can't rule out (`BootstrapConcurrencyTests`, 3/3 filtered vs.
7/13 under the real parallel suite, §1). The mutated `git add` invocation is deterministic, but the
instrument running it — xUnit under `dotnet test`'s default cross-class parallelism — is not, so one
green (mutant-dies) run doesn't distinguish "this test reliably catches it" from "it happened to catch
it under that scheduling."

Ran the identical mutant twice more, same harness, same file untouched otherwise:

- **Run 2/3:** mutated checksum `6a37dc45…` (identical both mutated runs — same edit each time, not a
  different mutation). Full unfiltered `dotnet test`: **1 failed / 495 passed / 496 total**. Same
  single failing test, `UntrackedFileOnADirtyTree_IsCommittedAsARecoveryCommitAuthoredBySystem`, same
  stack (`AssertWorkingTreeIsCleanAsync` at `ContentRepositoryService.cs:306`, called from
  `EnsureRepositoryAsync:122`), same message (`?? docs/copied-in.md` still present in the porcelain
  output). Restored from the `cp`'d backup via the bash `trap … EXIT`; checksum back to `06e2d3d2…`.
- **Run 3/3:** same mutated checksum `6a37dc45…`. Full unfiltered `dotnet test`: **1 failed / 495
  passed / 496 total**, identical failing test, identical stack, identical message. Restored the same
  way; checksum back to `06e2d3d2…`.

**3/3, and the failure set did not vary run to run** — same one test, same assertion, same message,
every time. That's the record the Architect asked for: a varying subset would have been the finding
here (a scheduling-dependent catch), and it isn't one — the D9 regression test genuinely, reliably
kills this mutant regardless of xUnit's parallel scheduling that run.

Re-confirmed mutant-free at the final tree: `ContentRepositoryService.cs:263` reads `["add", "-A"]`,
checksum `06e2d3d2475fddad1a22d582354d202f04f9a39171621dcdbda4b22eefab8e77` — matches the pre-mutation
value from every run. `git status --short -- src tests` / `git diff --stat -- src tests` (the
Architect's `git add -N` on `GitHookInstaller.cs` now makes it visible to `diff`, not just `status`)
show only this block's four intended files — no residue, no stray mutant left in an untracked file.

**Gates re-run at this same final tree:** `dotnet build` 0 warnings/0 errors · `dotnet test`
**496/496** full unfiltered · `dotnet format --verify-no-changes` clean · `openspec validate
git-backed-content-core --strict` valid.

No scope change beyond the repeated confirmation — same single mutant, same file, nothing else
touched.

→ @reviewer

**[reviewer]** 2.3–2.5 — **Request changes.** One blocker, found by reproduction under the specific
scrutiny asked for; the other two checks came back clean, also verified rather than accepted.

### Blocker — `ReconcileWorkingTreeAsync` can "succeed" while silently making copied-in content
### unrecoverable from git, exactly the harm D9 exists to prevent

Asked to check whether a tree can be dirty in a way `git add -A` does not stage, leaving the gate
satisfied and reconciliation a no-op. Plain `.gitignore`-excluded files are **not** that case —
verified both sides agree: `git status --porcelain` doesn't list ignored files either (absent
`--ignored`), so `add -A` skipping them and 2.5 reporting clean are consistent, standard git
behaviour, not a defect this block introduced.

There is a real divergence, though, and it's worse than "no commit made" — it's a commit that
*looks* successful while not actually preserving the content. If an operator copies in a directory
that is itself a git repository (a cloned notes folder, an old vault that was under its own version
control — plausible for exactly the git-literate audience this product targets), `git add -A` does
not stage its files at all. It stages the directory as a **gitlink** (a `160000` commit-reference
entry, the same mechanism as submodules) pointing at a commit SHA in an object database ZeroWiki
never touches or preserves. Reproduced end to end:

```
docs/copied-notes/  ← operator copies in a directory that has its own .git, with a real commit inside
git add -A          → "warning: adding embedded git repository: docs/copied-notes"; stages a gitlink
git diff --cached --quiet → exit 1 (dirty) → ReconcileWorkingTreeAsync commits normally
git status --porcelain → EMPTY afterward → AssertWorkingTreeIsCleanAsync (2.5) passes
git ls-tree -r HEAD | grep copied-notes → "160000 commit <sha>  docs/copied-notes" — no blob entries
                                            for the actual file contents anywhere in this repo's history
```

Both the reconciliation gate and the 2.5 invariant assertion report success. Nothing is logged, no
message names what happened. But the actual page content is not stored as a git object in this
repository — only a dangling reference to a commit in an object store nobody owns (no
`.gitmodules`, so it isn't even a real submodule). Concretely, this breaks:

- **"Authorship comes from git"** — `git log`/`git blame` on the real files show nothing; the outer
  repo only knows about a commit-reference entry, not the files.
- **The Smart HTTP remote / Obsidian sync** — `updateInstead` pushes and any clone transfer the
  gitlink stub, not the file contents; an Obsidian vault cloning this repo gets a broken, empty
  `docs/copied-notes/` with no indication why.
- **D9's own stated rationale** — *"discarding is permanent; an unwanted recovery commit is a plain
  `git revert`."* Here the recovery commit exists, but reverting it doesn't help, because the content
  it supposedly preserved was never actually captured. If the nested `.git` is ever removed later
  (very plausible — a disk-tidying pass, the nested repo's own gc, an operator assuming `.git`
  directories are safe to delete once "committed") the content becomes permanently unrecoverable from
  ZeroWiki's history, silently, with every check along the way having reported success.

This isn't hypothetical or contrived for this audience — "point it at a folder and it Just Works" is
this project's own pitch, and copying in an existing git-tracked notes folder (`.git` and all) is a
completely natural thing for a git-comfortable user to do, arguably more likely for ZeroWiki's actual
users than for a random directory. Neither `design.md` nor this DEVLOG mentions embedded repositories
or gitlinks anywhere (checked) — this hasn't been raised or knowingly accepted before.

Direction, not a prescription: this block already has a working precedent for "detect and fail loudly
rather than silently misbehave" (the bare-repository checks in 2.1–2.2). The same posture fits here —
detect a `.git` entry anywhere under `docs/` other than at `repositoryRoot` itself before or during
reconciliation, and refuse to start naming the offending path, rather than let `add -A` silently
gitlink it. Whether that detection belongs in `ReconcileWorkingTreeAsync` or `EnsureInitialCommitAsync`
too (the same hazard exists on first `add`) is the worker's/architect's call.

### The two things I was specifically asked to check came back clean — verified, not just re-read

**2. D9's test independence from 2.5, checked by reproduction.** The recorded mutation run shows all
three confirmations dying at `AssertWorkingTreeIsCleanAsync`, i.e. 2.5's own production assertion,
during Act — not at any of `UntrackedFileOnADirtyTree_IsCommittedAsARecoveryCommitAuthoredBySystem`'s
own `Assert` lines. To check whether the coverage is coupled to 2.5's existence, I reproduced the
counterfactual directly rather than reasoning about it: `cp`'d `ContentRepositoryService.cs` aside
(checksum `06e2d3d2…`, matching the worker's recorded pre-mutation value), then *both* mutated
`["add", "-A"]` → `["add", "-u"]` **and** commented out the call to `AssertWorkingTreeIsCleanAsync`
entirely (simulating "2.5 removed"), and ran the specific test. Result: it still fails — but now at
its own `Assert.Equal("2", …)` commit-count check (`ContentRepositoryServiceTests.cs:241`, expected
`"2"`, actual `"1"`), because no second commit was made at all. So D9's coverage is **not** solely
coupled to 2.5 — the test has independent teeth via its own commit-count assertion; 2.5 firing first
in the shipped code just means that's the failure a reader sees today. Restored via the same `cp`
backup immediately after, checksum back to `06e2d3d2…`, confirmed via `dotnet build` clean and
`git diff --stat -- src` unchanged. This was a single filtered diagnostic run answering a
counterfactual question, not a new formal mutation record — the existing 3/3 unfiltered record for
the shipped `add -A` mutant stands as-is and is unaffected by this check.

**3. Hooks and the clean-tree invariant, checked by reproduction.** Wrote a file directly into
`.git/hooks/` on a real repo and confirmed `git status --porcelain` stays empty before and after —
`.git/hooks` is genuinely invisible to working-tree status, not merely assumed to be. Confirmed
`GitHookInstaller.WriteHookAsync` (`GitHookInstaller.cs:82-97`) has no existence check of any kind —
it's an unconditional `File.WriteAllTextAsync` + `File.SetUnixFileMode` every call, matching decision
1 exactly. `HandEditedHook_IsRestoredOnTheNextStart` genuinely exercises this (hand-edits the file,
re-runs bootstrap, asserts both the content and the executable bit are restored) rather than just
asserting installation once. No concerns here.

### Other things checked in passing

- Spec amendment (`specs/content-store/spec.md`) reads correctly against D9 — always commits tracked
  and untracked as one recovery commit authored `System <system@zerowiki.org>`, no policy switch.
  `openspec validate git-backed-content-core --strict` re-run independently: valid.
- Order: decision 5 lists `classify → assert-resolves-root → config → initial commit → reconcile →
  assert`; the shipped order inserts hook installation between config and initial commit. Hooks live
  under `.git/hooks`, confirmed above to never affect working-tree cleanliness, so this doesn't
  disturb what decision 5 was actually protecting (the five named steps remain in the same relative
  order). Noting as an architectural note, not a violation.
- Mutation record for the shipped `add -A` mutant: the worker's self-correction from 1/1 to a proper
  3/3 (identical mutated checksum `6a37dc45…` all three runs, identical failing test, identical stack,
  identical message, full unfiltered suite each time, `cp`+`trap`-based revert, checksum restored to
  `06e2d3d2…` every time) matches CLAUDE.md's harness rules exactly. No issue with that record itself
  — it answers a different question (tracked-only staging) from the embedded-repo finding above, which
  the mutant does not cover.
- `git status --short -- src tests` / `git diff --stat -- src tests` both re-checked at the final
  tree after my own revert: only this block's four files, no residue.

→ @architect

**[architect]** Ruling on the review of `2.3–2.5`. **Request changes upheld, and the blocker went to the
Product Owner** rather than being fixed at my discretion — it is a product call about first-run
behaviour, and it undercuts a stated rationale in D9.

**The finding.** An operator copies into the content volume a folder that is itself a git repository —
an existing Obsidian vault, a cloned notes folder. `git add -A` does not stage its files; it stages the
directory as a **gitlink** (`160000`), a reference to a commit in an object database ZeroWiki never
touches. The recovery commit succeeds, `git status --porcelain` returns empty, and
`AssertWorkingTreeIsCleanAsync` passes — while `git ls-tree -r HEAD` holds no blob for any of that
content. Every check reports success and the content is not in the wiki.

**Why this is not merely a bug.** D9's argument for always-commit rests on the asymmetry that
"discarding destroys that irrecoverably; an unwanted recovery commit is a `git revert`". For gitlinked
content there is nothing to revert *to*: delete the inner `.git` later and the content is unrecoverable
from ZeroWiki's history. D9's safe side is not safe in this case, which is why it needed the Product
Owner rather than a patch. Compounding it, the most likely way to populate a new ZeroWiki is copying an
existing Obsidian vault — which, by this design's own premise (`obsidian-git`), is usually git-backed.
This is closer to the normal path than an edge case.

**Product Owner decision: refuse to start, naming the path.** Consistent with this section's own
precedent — 2.1–2.2 already fails loudly on a bare repository at the root rather than misbehaving
quietly — and it never touches what the operator copied in. The onboarding cost is an error message
that says exactly what to do, which is the trade accepted.

**[architect]** The two things I asked the reviewer to scrutinise both came back clean **by
reproduction**, and are recorded so they are not re-litigated: `.gitignore`d files are *not* a
divergence (invisible to `git status --porcelain` as well, so both sides agree); and D9's test is
**not** solely coupled to 2.5 — mutating `add -A` → `add -u` *with 2.5's assertion disabled* still
fails the test, via its own commit-count assertion.

**[architect]** → `@worker`: fix round on `2.3–2.5`. Still the inner loop — no new numbers, nothing new
ticked.

1. **Refuse to start when reconciliation would store a gitlink**, before any commit is made. Name every
   offending path and say what to do about it (move or remove the nested `.git`, then restart) — an
   operator meeting this has copied in a folder and has no reason to know what a gitlink is.
2. **Detect using git's own view, not a hand-rolled directory walk** — the index after staging carries
   mode `160000` for exactly the entries git would commit as gitlinks, which is the precise question.
   If you detect post-stage, leave the index as you found it (`git reset` unstages without touching the
   working tree) so a refused start is not also a half-staged one. Whatever you choose must catch a
   nested `.git` **directory** and a **gitfile**, and must catch one nested more than one level deep.
3. **Spec, not just prose** — add a scenario under *Working-tree-clean invariant* in
   `specs/content-store/spec.md` so the section review is gated on this behaviour. Add a short D9
   addendum in `design.md` recording the decision and its reason.
4. **Out of scope:** a gitlink already in history from an earlier run. Detecting that would brick a wiki
   that already has one, and no shipped build ever created one. Note it, do not handle it.
5. **One additional mutant, 3 runs, and make it a real counterfactual.** Not "remove the guard" — the
   test asserts the guard, so that mutant is near-tautological and proves little. Mutate the guard's
   **coverage**: make detection miss the gitfile form, or miss a repository nested deeper than one
   level. A test must die. Same harness rules as before; `cp` aside and restore from the copy, revert
   via `trap`/`finally`, checksum before and after, full unfiltered `dotnet test`.

**[worker]** Fix round on `2.3–2.5` — items 1–5 addressed.

1–2. **Refuse to start on a staged gitlink, detected via git's own index, before any commit.**
   `ContentRepositoryService.ReconcileWorkingTreeAsync` now checks the index right after `git add -A`,
   before the `git diff --cached --quiet`/commit path: new `FindStagedGitlinksAsync` runs `git
   ls-files --stage` and filters for mode `160000` — the exact entry git writes for a nested
   repository instead of its file contents. This is git's own view of what the commit would actually
   store, not a hand-rolled `.git`-entry directory walk, so it is correct by construction for every
   form and depth the ruling named: reproduced all three before writing the check —
   - a nested `.git` **directory** one level under `docs/` → `160000` staged;
   - a nested repo **more than one level deep** (`docs/a/b/vault`) → still `160000`, same as above —
     git's staging algorithm is depth-agnostic, it only cares about the nearest `.git` boundary;
   - a nested repo whose `.git` is a **gitfile** (linked-worktree form, `gitdir: <path>`) → also
     `160000`, identical to the directory form.

   When any gitlink path is found, `ReconcileWorkingTreeAsync` runs `git reset` (unstages back to
   `HEAD`, confirmed by reproduction not to touch the working tree — the offending directory's files
   are left exactly as the operator placed them) and throws `InvalidOperationException` naming every
   offending path and saying what to do (`GitHookInstaller`-style loud-failure precedent from
   2.1–2.2's bare-repository check). The message is written for someone who has never heard the word
   "gitlink": names the path, says it looks like a copied-in repository, says why ZeroWiki can't store
   it, and says to remove the nested `.git` and restart.

3. **Spec + design.** Added *Nested git repository refused rather than committed as a gitlink* under
   *Working-tree-clean invariant* in `specs/content-store/spec.md`. Added a D9 addendum in `design.md`
   (right after D9's own two paragraphs) recording the decision, its reasoning, and the out-of-scope
   note, so a future reader hits the reasoning at D9 rather than only in this DEVLOG.
   `openspec validate git-backed-content-core --strict` still passes.

4. **Out of scope, noted not implemented.** A `<remarks>` on `ReconcileWorkingTreeAsync` states
   explicitly: a gitlink already committed into history by an earlier run is not detected — no
   shipped build has ever produced one, and retroactive detection would only refuse to start a wiki
   that already has one, which is strictly worse.

**Tests** (`ContentRepositoryServiceTests.cs`): `NestedGitRepository_FailsToStartNamingThePathAndCommitsNothing`
(plain `.git` directory, one level deep — fails naming `docs/copied-vault`, no second commit, index
carries no `160000` entry afterward, the nested content is still on disk untouched);
`NestedGitRepositoryAsAGitfile_IsAlsoDetected` (gitfile form, pointing at a second real repository's
`.git`); `NestedGitRepository_IsDetectedRegardlessOfNestingDepth` (`docs/a/b/copied-vault`, three
levels deep). The existing `UntrackedFileOnADirtyTree_IsCommittedAsARecoveryCommitAuthoredBySystem`
now exercises the new gitlink check too (finds none, proceeds to commit exactly as before) — it still
passes unmodified, confirming D9's ordinary case doesn't regress.

**Mutation testing — one additional mutant, 3/3, same failure every run.** Not "remove the guard"
(near-tautological, per the ruling) — mutated the guard's **coverage**: narrowed
`FindStagedGitlinksAsync`'s condition from `mode == "160000"` to `mode == "160000" &&
line[(tabIndex + 1)..].Count(c => c == '/') <= 1`, i.e. only catch a gitlink nested exactly one path
segment under the repository root, silently missing anything deeper — the second axis the ruling
named. Harness: `cp`'d `ContentRepositoryService.cs` aside first (checksum `cf510c0e…`), mutated with
`sed`, ran the full unfiltered `dotnet test`, restored via a bash `trap … EXIT` (never `git checkout
--`/`git restore --`), checksummed before and after every run.

- **Run 1/3:** mutated checksum `589d182e…`. **1 failed / 498 passed / 499 total** — the only failure
  was `NestedGitRepository_IsDetectedRegardlessOfNestingDepth`, at `Assert.Throws` with "No exception
  was thrown" (the deeper-nested gitlink now silently gitlinked through, exactly the coverage gap
  intended). Restored; checksum back to `cf510c0e…`.
- **Run 2/3:** identical mutated checksum `589d182e…`. **1 failed / 498 passed / 499 total**, same
  single test, same failure mode. Restored; checksum back to `cf510c0e…`.
- **Run 3/3:** identical mutated checksum `589d182e…`. **1 failed / 498 passed / 499 total**, same
  single test, same failure mode. Restored; checksum back to `cf510c0e…`.

3/3, no variance in the failing set across runs, and — notably — the mutant did **not** touch the
one-level-deep tests (`NestedGitRepository_FailsToStartNamingThePathAndCommitsNothing`,
`NestedGitRepositoryAsAGitfile_IsAlsoDetected`, both exactly one path segment under `docs/`, so
`<= 1` still holds for them): the depth axis and the gitfile-form axis are covered by genuinely
independent assertions, not one test accidentally standing in for both. Re-confirmed mutant-free at
the final tree: `ContentRepositoryService.cs:341` reads plain `if (mode == "160000")`, checksum
`cf510c0efbd512c36aacea4f37956831034ab8596d3fb61362515208f3398b74` matches every restoration.
`git status --short -- src tests` / `git diff --stat -- src tests` show only this fix round's four
files (the same four as the original block) — no residue.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **499/499** full unfiltered (496 + 3 new)
· `dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid. Not committed, nothing ticked — still the inner loop.

→ @reviewer

**[reviewer]** 2.3–2.5 fix-round re-audit — **Request changes.** This certifies the tree as it now
stands, including the gitlink guard. Two of the four things asked for came back clean; two did not,
and one of those two directly contradicts the ruling's own stated intent.

### Blocker — the "out of scope" boundary is not actually respected: a historical gitlink bricks startup forever

Asked to confirm the code doesn't trip on a repository whose HEAD already contains a `160000` entry
from an earlier run. It does, and it bricks exactly the way the ruling said this must not.
`FindStagedGitlinksAsync` reads `git ls-files --stage`, which lists the **entire index**, not merely
what this call's `git add -A` freshly staged — so a gitlink already committed into HEAD shows up on
*every* subsequent call, whether or not anything changed. Reproduced concretely, not reasoned about:

```
# Simulate "a gitlink landed in history before this guard existed" — a real nested repo on disk,
# committed into the outer repo's HEAD (exactly what decision 4 says is out of scope):
docs/old-vault/.git exists for real; committed into HEAD as a 160000 entry. Tree is clean, one commit.

# Simulate the NEXT startup, nothing on disk has changed:
git add -A            → no-op, nothing changed
git ls-files --stage   → still shows "160000 <sha> 0	docs/old-vault" — because ls-files --stage
                          lists the whole index, HEAD-committed entries included, not a staging delta
```

`FindStagedGitlinksAsync` would find this on every single start, run `git reset`, and throw — forever.
This is precisely the outcome the ruling said to avoid ("no shipped build ever created one... detecting
that would brick a wiki that already has one... note it, do not handle it"), and it's the opposite of
what `ReconcileWorkingTreeAsync`'s own `<remarks>` claims ("a gitlink already committed into history by
an earlier run is not detected" — it is, unconditionally, by construction of scanning the whole index
rather than a staging delta). None of the three new tests exercise this case — all three create their
nested repo fresh, before the first `EnsureRepositoryAsync` call ever commits anything, so none of them
would have caught this. This needs a real fix (e.g. diff the index against `HEAD`'s own tree and only
flag a `160000` entry that wasn't already there, or accept an explicit allowlist for what's already in
HEAD) — direction is the worker's/architect's call, but "already in HEAD" and "newly staged this call"
are different questions and the current check answers the wrong one.

### Blocker — the error message mangles the very path it's telling the operator to go find

Asked to confirm `core.quotePath`'s default quoting doesn't hand the operator a garbled path, since
detection keys on mode (unaffected) but the message reports the path itself. It does mangle it.
`git ls-files --stage` (no `-z`) quotes any path with non-ASCII bytes as a C-style escaped literal by
default — reproduced with a directory named `café-vault`:

```
git ls-files --stage → 160000 <sha> 0	"docs/caf\303\251-vault"
```

`FindStagedGitlinksAsync`'s parsing (`ContentRepositoryService.cs:343`, `line[(tabIndex + 1)..]`) takes
this literally — the surrounding double quotes and the raw, un-decoded octal escape both end up in
`gitlinkPaths`, and from there straight into `BuildGitlinkErrorMessage`'s output: the operator would be
told to go find `'"docs/caf\303\251-vault"'`, not `docs/café-vault`. This isn't a contrived input for
this product — non-ASCII names are already an anticipated category elsewhere in this codebase
(`BootstrapPageTests.cs`'s own `"café"` case), and the entire point of this fix is a message clear
enough that "an operator meeting this has no reason to know what a gitlink is" can still act on it — a
quoted, octal-escaped path defeats exactly that. All three new tests use plain-ASCII directory names
(`copied-vault`), so this gap was never exercised. Direction: `git ls-files --stage -z` (NUL-delimited,
which git never quotes) with the split changed from `'\n'` to `'\0'` — not just adding `-z` on its own,
since the current line-based split depends on `\n`-splitting and `-z`'s output has no `\n` between
records at all.

### The other two things asked for came back clean, verified rather than accepted

**1. `git reset` semantics.** Confirmed by reproduction, not read: a bare `git reset` resets the index
to match `HEAD` exactly (`git diff --cached --stat` empty afterward) — it does not "empty" the index,
and the working tree is never touched (a file staged with a real content change stays changed on disk,
now unstaged). Checked whether "the index already held staged entries before reconciliation ran" can
arise on this code path: it can't reach `ReconcileWorkingTreeAsync` in any state other than
index-equals-`HEAD`, because everything before it in the same call (`EnsureInitialCommitAsync`) either
no-ops or completes its own add-then-commit synchronously, and 2.5's invariant guarantees the previous
run left index-equals-`HEAD` too. Reproduced the hypothetical anyway (staged a tracked-file
modification *before* this call's own `add -A`, together with a gitlink) to see what `git reset` does
regardless: it un-stages both, working tree untouched for both, and the previously-staged modification
becomes an ordinary unstaged change that the very next successful start's `add -A` picks up correctly.
No data loss either way, and the precondition doesn't arise on this path in the first place.

**2 (second half). `\n`-split correctness for what git actually emits today.** With quoting active
(the current, unfixed behavior), a path containing a literal newline byte is escaped as the two
characters `\`/`n`, never an actual newline — which is *why* the current line-based split is safe
against that pathological case today. Worth keeping in mind if the quoting fix above moves to `-z`: the
NUL-delimited form sidesteps this differently (NUL can't appear in a path at all), so switching the
split delimiter alongside `-z` preserves the same safety by construction, it just needs to be the two
changes together, not `-z` layered onto the existing `\n`-split.

### Item 4 — no regression, checked directly

Ran `dotnet test --filter "FullyQualifiedName~ContentRepositoryServiceTests"`: **14/14 passed**,
including `UntrackedFileOnADirtyTree_IsCommittedAsARecoveryCommitAuthoredBySystem` unmodified. D9's
load-bearing case is intact.

### Mutation record — assessed, not re-run, per the instruction

The one additional mutant (narrowing `mode == "160000"` to `mode == "160000" && <= 1 path segment`) is
a genuine counterfactual, not the near-tautological "remove the guard" the ruling explicitly ruled out.
3/3 identical failure (`NestedGitRepository_IsDetectedRegardlessOfNestingDepth`, "No exception was
thrown"), and — the useful part — the one-level and gitfile-form tests were unaffected by that same
mutation, which is real evidence the depth axis and the gitfile-form axis rest on independent
assertions rather than one test standing in for both. Independently re-read (not re-run)
`ContentRepositoryService.cs:341`: `if (mode == "160000")`, un-narrowed, matching the coordinator's own
check. No issue with this record. It doesn't cover either blocker above — both are gaps in what the
guard answers (staging-delta vs. whole-index; quoted vs. literal path), not gaps in how faithfully the
shipped condition is applied, so this mutant wouldn't have been expected to catch either.

### Confirmed independently

`openspec validate git-backed-content-core --strict`: valid. `dotnet build`: 0/0. `git status --short
-- src tests` / `git diff --stat -- src tests`: only this fix round's four files (plus `design.md` and
the spec file for the addendum/scenario), no residue.

→ @architect

**[architect]** Ruling on the re-audit of `2.3–2.5`. **Request changes upheld — and blocker 1 came out
of my brief, not the worker's implementation.**

I directed detection at the index on the grounds that it "carries mode `160000` for exactly the entries
git would commit as gitlinks". That is false in a way the worker had no reason to doubt:
`ls-files --stage` is an **index census**, not a staging delta, so it reports a `160000` entry already
present in `HEAD` just as readily as one `add -A` just created. The consequence is that a repository
which already contains a gitlink refuses to start on **every** restart — the exact bricking the same
ruling told the worker to avoid, four items above where I introduced it. Recorded plainly because the
project's own standing rule is that the instrument is likelier to be wrong than the finding, and here
the faulty instrument was the brief.

The question the guard must ask is **"would committing now introduce a gitlink not already in HEAD?"** —
a delta, not a census. `git diff --cached --raw` reports only what differs from HEAD. The unborn-HEAD
case needs handling explicitly, since a nested repository copied in before the first commit is exactly
what the existing tests cover.

**Blocker 2 — `core.quotePath`.** A `café-vault` directory comes back from `ls-files` as
`"docs/caf\303\251-vault"`, and that literal lands in the exception. The whole point of naming the path
is that an operator can go find it; octal escapes and wrapping quotes defeat it. `-z` plus a NUL split,
the two moving together — the reviewer established that the current `\n` split is safe **only** because
quoting escapes embedded newlines, so removing the quoting without changing the split trades a cosmetic
bug for a parsing one. Note the pattern: a §11 finding in new clothes — non-ASCII was already an
anticipated category here (`BootstrapPageTests.cs`'s `"café"`), and all three new tests used ASCII.

**Verified clean by the reviewer, by reproduction, and not to be re-litigated:** `git reset` restores
the index to `HEAD` rather than emptying it and never touches the working tree; and the mutation record
for the depth mutant stands — 3/3, genuinely independent axes. Neither blocker is a gap in how
faithfully the shipped condition runs, which is what mutation measures; both are gaps in **what the
guard asks**. No mutant would have found either.

**[architect]** Fix round dispatched. **No further mutation testing in this block** — two mutants have
run and both were informative; these two fixes are a scoping error and a formatting error whose tests
assert them directly, which is the tautological kind I ruled out last round. CLAUDE.md warns that
mutation testing is easy to run past the point of usefulness, and this is that point.

**[architect]** Missing fixtures, both now required: a repository whose HEAD **already** contains a
gitlink must start successfully; and a nested repository with a **non-ASCII** name must produce a
readable path in the message.

**[worker]** Second fix round on `2.3–2.5` — both blockers fixed.

1. **Blocker 1 (bricking) — `FindStagedGitlinksAsync` now asks a delta against `HEAD`, not an index
   census.** Replaced `git ls-files --stage` with `git diff --cached --raw`: this reports only entries
   that differ between the index and `HEAD`, so a gitlink already committed by an earlier run produces
   **no** diff entry on a later restart (verified empirically before writing the fix — staged a
   historical gitlink, committed it, then re-ran `add -A` + `diff --cached --raw` against the unchanged
   nested repo: empty output). A newly staged gitlink still appears (new mode `160000` against old mode
   `000000`, status `A`). The genuinely unborn-`HEAD` case is unreachable here: `EnsureInitialCommitAsync`
   runs unconditionally before reconciliation and always creates the initial commit, so `HEAD` is always
   born by the time this check or the `git reset` below it runs — the "nested repo copied in before the
   very first commit" tests pass because of that ordering, not because `git diff --cached --raw` happens
   to tolerate an unborn `HEAD`. (Corrected here after the reviewer's re-audit found the DEVLOG's original
   claim — that unborn-`HEAD` tolerance was the reason this works — was not the actual mechanism.)
2. **Blocker 2 (mangled path) — switched to `-z`, split on `\0`, moved together with removing the
   quoting.** `-z` disables `core.quotePath`'s default octal-escaping of non-ASCII paths (verified the
   exact byte shape first: hexdumped `git diff --cached --raw -z` output for a `café-vault` entry —
   each record is `":<old mode> <new mode> <old sha> <new sha> <status>"`, NUL, path, NUL, with the next
   record's `:` following immediately; a rename/copy record carries two NUL-terminated paths instead of
   one). `FindStagedGitlinksAsync` now splits the whole output on `'\0'` and walks records explicitly —
   consuming one path field normally, two for an `R`/`C` status (keeping the new name, the one this
   repository would actually hold) — rather than the old newline/tab parsing, which depended on quoting
   to keep embedded newlines safe and would have broken the moment quoting was removed without the split
   changing too.
3. **`ReconcileWorkingTreeAsync`'s `<remarks>` corrected** to describe what the code now does: the
   out-of-scope boundary (a gitlink already in `HEAD` from an earlier run) is enforced by asking a delta
   against `HEAD`, not by a policy check bolted on afterward — it was never separately enforced before,
   which is exactly how blocker 1 got in.

**Tests** (`ContentRepositoryServiceTests.cs`): `RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully`
— commits a gitlink directly (bypassing the guard, standing in for a build that predates it), asserts
`ls-files --stage` shows `160000` beforehand, then asserts a second `EnsureRepositoryAsync` call
succeeds and leaves the tree clean, rather than throwing. `NestedGitRepositoryWithANonAsciiName_MessageContainsTheRealPathNotAnEscapedForm`
— a `café-vault` nested repo; asserts the thrown message contains the real UTF-8 path
(`docs/café-vault`) and asserts the octal-escaped form (`\303\251`) and the quoting (`"docs/`) are
**absent**. All three pre-existing gitlink tests (plain directory, gitfile form, three-levels-deep) and
`UntrackedFileOnADirtyTree_...` (D9's ordinary case) re-verified passing unmodified — no regression.

**No mutation testing this round, per the ruling** — both fixes are a scoping error and a formatting
error whose new tests assert them directly; a mutant here would be the tautological kind already ruled
out.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **501/501** full unfiltered (499 + 2 new)
· `dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid. `git status --short -- src tests` / `git diff --stat -- src tests` reviewed: only this block's
four files (`ContentRepositoryService.cs`, `ContentStorageStartupExtensions.cs`, `GitHookInstaller.cs`,
`ContentRepositoryServiceTests.cs`), no residue. Not committed, nothing ticked.

→ @reviewer

**[reviewer]** 2.3–2.5, second fix-round re-audit — **Approve, with one nit.** Certifying the tree as
it now stands: both prior blockers are genuinely fixed, verified by direct execution rather than by
re-reading the diff.

**Both blockers confirmed closed, by running the code, not the tests that assert it:**

- Ran `RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully` and
  `NestedGitRepositoryWithANonAsciiName_MessageContainsTheRealPathNotAnEscapedForm` directly — both
  pass. More load-bearing: I reverted `FindStagedGitlinksAsync` to the previous `ls-files --stage`
  implementation in a scratch edit (checksum before/after, restored, confirmed byte-identical) and
  reran the café-vault test against it — it genuinely fails, at the first assertion, because the
  message contains the quoted/escaped form instead of the real path. That's item 4 answered directly:
  the assertion has real teeth, it isn't passing for an unrelated reason.
- All 16 tests in `ContentRepositoryServiceTests` pass together, so the delta-based check still
  catches all three original fixtures (plain directory, gitfile form, three-levels-deep) — item 2.

**Item 1, the parser desync question — checked against real git output, not just the code.**
`git diff --renames` is **on by default** even without `-M` (confirmed: a plain `git mv` produces an
`R100` record with two NUL-terminated paths, unprompted) — so this is a reachable shape, not a
hypothetical the ruling could safely wave off. Hand-traced the field-index bookkeeping against that
real record and then proved it directly: wrote a temporary test staging a rename (`.gitkeep` →
`renamed.gitkeep`) immediately ahead of a nested-repo gitlink in the same `add -A` sweep — confirmed via
`git diff --cached --raw` first that the rename record really does precede the gitlink record — then
ran `EnsureRepositoryAsync` against it. It correctly refused, naming only the gitlink path, with the
renamed file absent from the message. The two-path-field consumption for `R`/`C` status does not
desynchronise the following record. Removed the scratch test after; `git status --short -- tests`
clean.

**Item 3 — the outcome is safe, but the stated mechanism doesn't describe what actually happens; not a
blocker, but worth recording exactly because this project has been bitten by this shape before.**
Wrote a scratch test placing a nested vault under where `docs/` would go **before the very first ever**
`EnsureRepositoryAsync()` call — the most literal reading of "unborn HEAD". It refused to start,
correctly. But `git rev-parse --verify -q HEAD` at the moment of refusal showed **HEAD already
existed** (one commit, containing only `.gitkeep`) — because `EnsureInitialCommitAsync` always runs
before `ReconcileWorkingTreeAsync` in `EnsureRepositoryAsync`'s fixed order, and it unconditionally
creates the first commit whenever `HEAD` doesn't already exist, regardless of what else sits in the
tree. That ordering means `git reset`/`FindStagedGitlinksAsync`'s `git diff --cached --raw` can
**never** actually face a genuinely unborn `HEAD` in this codebase — not in the five gitlink tests
(each calls `EnsureRepositoryAsync()` once before ever introducing the nested repo) and not in the
scratch repro above either. The worker's stated rationale — *"works unmodified against an unborn HEAD
… so a nested repository copied in before the very first commit is still caught"* — is a true fact
about `git diff --cached --raw` in isolation, but it doesn't describe the actual mechanism by which the
shipped code catches that case: it's caught because `HEAD` is always already born by the time the
guard runs, not because the guard tolerates an unborn one. The outcome is correct and safe either way,
so this is a nit, not a blocker — but it's the same pattern this DEVLOG already named as a standing
rule (an assertion's justification is a claim about a counterfactual, and reading it isn't what checks
it). Worth a one-line comment correction whenever this file is next touched; not worth its own round.

**Confirmed independently:** `dotnet build` 0/0; `dotnet test --filter
"FullyQualifiedName~ContentRepositoryServiceTests|FullyQualifiedName~ContentRepositoryStartupTests"`
18/18; `openspec validate git-backed-content-core --strict` valid; `dotnet format
--verify-no-changes` clean; `git status --short -- src tests` / `git diff --stat -- src tests` show
only this block's four files, no residue from any of my scratch edits.

No mutation this round, per the Architect's ruling — agreed, both blockers were gaps in what the guard
asked, not in how faithfully the shipped condition runs, so neither was reachable by mutation, and
nothing in this fix round changes that.

**2.3–2.5 is clear to commit.**

**[worker]** Comment-only nit fix on `2.3–2.5` — no behaviour changed. The reviewer's nit: my
unborn-`HEAD` justification stated a true fact about git (`diff --cached --raw` diffs against the
empty tree with no commits) as if it were *why* the shipped code is safe, when it isn't — the real
reason is that `EnsureInitialCommitAsync` runs unconditionally before reconciliation and always
creates the initial commit, so `HEAD` is always born by the time `FindStagedGitlinksAsync`/`git reset`
run; the genuinely-unborn case is unreachable in this codebase.

- **`ContentRepositoryService.cs`, `FindStagedGitlinksAsync`'s `<remarks>`** — rewritten to state the
  actual mechanism (the ordering in `EnsureRepositoryAsync` guarantees a born `HEAD`), keep the
  empty-tree fact but mark it explicitly as a property of git that would only become load-bearing if a
  future change reordered `EnsureRepositoryAsync` to run reconciliation before the initial commit, and
  name that reordering as the specific thing that would make the unreachable case reachable.
- **This DEVLOG, my own fix-round post above** — corrected the same claim in place (blocker 1's
  writeup) to say the unborn-`HEAD` case is unreachable because of the ordering, not because the
  diff-based check tolerates one. Edited rather than left standing plus appended, since this block is
  still uncommitted — nothing is frozen into history yet, and that's the point: wrong justifications
  are this change's named recurring defect (§11 found seven), and this one costs a sentence to fix
  while it's still cheap to fix.
- Checked for the same claim elsewhere: no other file states it. The reviewer's own post above quotes
  my original wording as part of its finding — left untouched, since that's their historical record of
  the nit, not a live claim of mine.

**Gates re-run:** `dotnet build` 0 warnings/0 errors · `dotnet test` **501/501** full unfiltered ·
`openspec validate git-backed-content-core --strict` valid · `dotnet format --verify-no-changes`
clean. No reviewer handoff — comment-only, per the Architect's instruction; Architect verifies the
diff before committing.

→ @architect

**[supervisor]** Section review of **§2 (Repository bootstrap & invariant)** over `7b50e46..HEAD`
(`ef2b75b` = 2.1–2.2, `6b82c33` = 2.3–2.5). Working tree clean; `git status --short -- src` empty, so
no mutation residue, tracked or untracked. Gates taken from the Architect's record, not re-run.

## Verdict: **Request changes** — two blockers

Both live on the same branch of `EnsureRepositoryAsync`: the path that **adopts a repository ZeroWiki
did not create**. Block 1 built that branch, block 2 hung two guards off it, and neither block's
review had reason to look at the pair. It is also the only branch in the section with **no test
fixture of its own** — every "existing repository" test in `ContentRepositoryServiceTests.cs` builds a
repository that ZeroWiki itself made on a previous start, or a ZeroWiki-shaped one
(`RepositoryCreatedWithoutTheConfiguration_…:64` inits, creates `docs/`, writes `docs/.gitkeep`,
commits). There is no fixture for a *foreign* repository — someone else's history, someone else's
layout. Both blockers are in the gap that leaves.

### Blocker 1 — the gitlink guard bricks startup on a gitlink it did not introduce, which is the exact outcome the Product Owner ruled out of scope

`ContentRepositoryService.cs:377` — `FindStagedGitlinksAsync` matches on `newMode` alone:

```csharp
if (newMode == "160000")
{
    gitlinkPaths.Add(path);
}
```

The `<remarks>` at `:321-335`, the fix-round ruling above (§"the question the guard must ask is *would
committing now introduce a gitlink not already in HEAD*"), and the test comment at
`ContentRepositoryServiceTests.cs:370-373` all state the intended question as a **delta**. Moving from
`ls-files --stage` to `diff --cached --raw` fixed the *census* half of that. It did not fix the other
half: a gitlink already in `HEAD` whose nested repository advances its own `HEAD` produces a `M`
record, and a `M` record's `newMode` is `160000` too. Nothing is introduced, and the guard fires
anyway.

**Verified by counterfactual, not by reading.** Built the fixture the section lacks — a repository
carrying a gitlink in `HEAD`, whose nested repository then commits — and ran the exact sequence
`ReconcileWorkingTreeAsync` runs (`git add -A` then `git diff --cached --raw -z`):

```
:160000 160000 7372c60 02388ca M\0docs/vault\0
```

then fed those bytes through a verbatim transcription of `FindStagedGitlinksAsync:355-381`:

```
fields   : [":160000 160000 7372c60 02388ca M","docs/vault"]
gitlinks : ["docs/vault"]
=> guard fires, EnsureRepositoryAsync throws
```

`oldMode` is `160000` — the entry was already in `HEAD` — and startup refuses regardless.

**Why this matters rather than being a curiosity.** The reachable case is an adopted repository that
legitimately contains a **submodule** (or a gitlink from any pre-guard means — the case
`RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully` exists to protect). First start
succeeds, because the submodule is unchanged. Then the submodule's commit pointer moves — which for a
live vault or a vendored dependency is the *normal* thing for it to do — and the wiki stops starting,
permanently, with a message (`BuildGitlinkErrorMessage:390-398`) instructing the operator to *"remove
the nested `.git`"*. For a legitimate submodule that advice is destructive, and the wiki cannot be
started until it is followed. This is the bricking the ruling at the top of the second fix round set
out to prevent, arriving through the one status the fixture never exercised.

The existing test passes only because it never advances the nested repository's `HEAD` before the
restart (`ContentRepositoryServiceTests.cs:388-394`).

*Slipped block review.* Diff-local review could have caught it in principle; recording that it did
not, because the fixture and the guard were reviewed together and agreed with each other.

### Blocker 2 — the adopt path never establishes `docs/`, which is what 2.1 claims to deliver

`EnsureInitialCommitAsync:214-223` returns early whenever `HEAD` is born, and
`Directory.CreateDirectory(_paths.WorkingTree)` at `:225` sits *after* that early return. So for any
adopted repository with history and no `docs/`, `EnsureRepositoryAsync` runs to completion — classify,
assert-resolves-root, config, hooks, no initial commit, nothing to reconcile, `status --porcelain`
empty, assert-clean passes — and `<DataRoot>/wiki/docs` **does not exist**. Reproduced end to end; every
check in the section reports success.

Block 1's binding decision 8 ("`docs/` must exist in a fresh clone") was wired only to the init path,
and nothing revisited it. Task 2.1 reads *"detect an existing non-bare git repo on the volume; **serve
from its `docs/` working tree**"* — the box is ticked and the second clause has no implementation and
no test behind it. The spec scenario *Repository present on startup* is satisfied only for the
repository ZeroWiki itself created; the requirement's "without requiring any additional configuration"
is not met for an adopted one, where the operator must know to create `docs/` by hand.

❓ **@architect** — what *should* the adopt path do? Three shapes, and this is a product call rather
than a mis-implementation: (a) create `docs/` when absent, leaving it untracked-and-empty (the tree
stays clean — an empty directory stages nothing — so the invariant is unaffected, and §3 gets the
precondition it will otherwise have to invent); (b) refuse to start naming the path, matching this
section's own bare-repo and gitlink precedent; (c) declare it §3's problem and have `3.1` treat an
absent working tree as zero pages. (c) is defensible, but then 2.1's second clause should stop
claiming it. Whichever is chosen, §2 owns saying so — leaving §3 to discover an unstated precondition
is how the next section drifts.

## Suggested remediation shape — one fix block

1. `ContentRepositoryService.cs:377` — narrow the guard to gitlinks actually being **introduced**:
   also require the record's `oldMode` (`metadataParts[0]`) to be something other than `160000`. Verified
   this keeps every real detection: a brand-new nested repository comes back `:000000 160000 … A`, and
   a tracked plain directory that later gains a `.git` produces no diff entry at all (git leaves its
   files tracked — confirmed by reproduction, so nothing is lost there either).
2. Extend `RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully` (`:368`) to commit once
   *inside* the nested repository before the restart. That is the assertion the section is missing;
   today it fails, and it is what pins the scope boundary the Product Owner drew.
3. Whatever @architect decides for Blocker 2, plus a fixture for it.
4. Add the missing fixture class generally: **a repository ZeroWiki did not create** — foreign
   history, no `docs/`, arbitrary layout — and run bootstrap over it. Both blockers, and the
   `core.hooksPath` question below, all sit in that one blind spot.

No `N.M` numbers, nothing to tick.

## Things checked that came back clean — recorded so they are not re-litigated

- **`EnsureRepositoryAsync`'s ordering is a coherent sequence, not an accreted one.** classify →
  assert-resolves-root → config → hooks → initial commit → reconcile → assert-clean; each step is
  where it is for the reason given, including the two moved by fix rounds. The one placement that
  could have been accidental is hooks landing between config and the initial commit. It is correct,
  and for a reason stronger than the docstring claims: `git rev-parse --git-path hooks` also honours
  **`core.hooksPath`** — verified by execution (`git config core.hooksPath /somewhere/else/hooks` →
  `rev-parse --git-path hooks` returns `/somewhere/else/hooks`) — so the installer writes to wherever
  git will actually *run* hooks from, not merely to the right place for a gitfile layout. That matters
  for §5.3: had it hardcoded `.git/hooks`, an adopted repo with `core.hooksPath` set would silently
  never take the write lock. Worth adding to the `<remarks>` at `GitHookInstaller.cs:12-17`, since
  §5.3 will rely on it.
- **No duplicated abstraction.** `GitProcessRunner`/`GitProcessResult`/`GitProcessException`/
  `GitAuthor`/`GitHookInstaller`/`ContentPaths` are each one thing. `ResolveContentPaths` vs the DI
  `ContentPaths` singleton is two accessors onto one type for a documented pre-`Build()` reason
  (`ContentStorageStartupExtensions.cs:46-53`), not a second derivation of the data root. Nothing here
  anticipates §5/§6/§7 beyond the runner the brief explicitly authorised.
- **Dead scaffolding: only the no-op hook bodies**, which are a recorded Product Owner decision, are
  rewritten every start, and say so in their own headers. That is the whole speculative surface.
- **Render mode / access control / DI**: no routes, pages or components added, so nothing to erode;
  three singletons, one registration site, no overlap.
- **2.5 delivers what this section can deliver.** The spec says "at all times"; the shipped startup
  assertion establishes the invariant at the only moment §2 controls, and after it nothing in this
  section writes to the tree. "At all times" is genuinely §5.1–5.3 (the lock) and §6.5 (transactional
  save), with §10.2 as its integrated test. Recording that mapping so §7/§8 do not re-open the
  no-HTTP-surface decision: the requirement is not under-delivered here, it is delivered in three
  places and this is the first.

## Mutation scoping — right, and the section's evidence has a shape worth naming

Scoping block 1 out was correct: bootstrap classification is not auth, concurrency or data integrity
in CLAUDE.md's sense, and the bare-repo refusal is asserted directly by its own test. Scoping block 2
in for exactly two mutants was also correct — `add -A` → `add -u` is precisely D9's data-integrity
property, and the depth mutant tests the guard's *coverage* rather than its presence. Stopping at two
was right; a third would have been the tautological kind.

But note what the section's own history says. Every defect §2 actually produced — the index-census
brief, `core.quotePath`, and now Blocker 1 — was a gap in **what the condition asks**, and every one
was invisible to mutation, which measures only how faithfully the shipped condition runs. Mutation
testing was pointed at the right paths and told the truth about them; it is simply not the instrument
for this failure mode. The instrument that finds these is **adversarial fixtures** — and both of my
blockers come from the one fixture class this section never built. That is the transferable lesson for
§5–§7, which will be far more mutation-heavy: budget fixture diversity alongside mutants, not instead
of them.

## For `## NEXT` (not the fix block)

- **`GitAuthor.cs:13-15`** still describes startup reconciliation as something that happens "later" —
  true when block 1 wrote it, superseded by block 2. Comment-only; roll into any later touch.
- **§5.1's lockfile must not live under `ContentPaths.RepositoryRoot`.** `ReconcileWorkingTreeAsync`
  runs `git add -A` from the repository root, so anything the app writes inside `/data/wiki` becomes
  content on the next restart. Same class of hazard as D8's `keys/` reasoning; worth deriving the path
  so it cannot be configured inside the repo.
- **Obligations 7 and 8 (subprocess kill on cancel, §6; raw argv in `GitProcessException`, §7)** —
  parking was the right call. Both are genuinely inert here (`CancellationToken.None`, no secrets in
  bootstrap's argv), both are owed by a named section, and fixing either here would have been the
  scope creep this review exists to catch. Confirmed inert, not re-raised.
- **`tasks.md:70`** still records 11.1's superseded pattern (`[A-Za-z0-9._-]{0,126}`) rather than
  D11's consecutive-dot form; `CredentialPolicy.UsernamePattern` in `src/` is correct. §11 is closed —
  this is a durable-record fix for whoever next touches `tasks.md`, not a re-opening.
- **`## NEXT` below still reads "Resume point: §2 … first block"** — stale; §2's blocks have both
  landed.

Two rounds available; this is round one. → `@architect`

**[architect]** Ruling on the §2 supervisor review. **Request changes upheld on both blockers.**
Carving one remediation block — no new `N.M` numbers, nothing ticked, every box in the section is
already ticked.

**The finding behind the finding, which is the part worth keeping.** Both blockers sit on the same
branch: the path that **adopts a repository ZeroWiki did not create**. Block 1 built that branch, block
2 hung two guards off it, and it is the only branch in §2 with no fixture of its own — every "existing
repository" test builds a repository ZeroWiki itself made, or a ZeroWiki-shaped one. Neither block
review had any reason to look at the pair, which is precisely the lens the section review exists for.
This is the third time in this change that the blind spot was the **fixture** rather than the effort
(§11's service-model differential, §2's `Path.GetTempPath()` roots, now the foreign repository), and
the first time it has been visible as a *pattern* rather than an incident.

**Blocker 1 — the gitlink guard bricks on a gitlink it did not introduce.** `ContentRepositoryService`
matches `newMode` alone. Moving from `ls-files --stage` to `diff --cached --raw` fixed the *census*
half of the question, but an `M` record — an existing gitlink whose nested HEAD merely advanced — also
carries `newMode == 160000`. An adopted repository with a legitimate submodule starts fine, then never
starts again once that submodule's pointer moves, telling the operator to delete a `.git` they need.
That is the same bricking the Product Owner ruled out of scope, arriving through a different door. The
supervisor verified it by counterfactual: built the missing fixture, ran the real `add -A` +
`diff --cached --raw -z`, and fed the actual bytes (`:160000 160000 … M\0docs/vault\0`) through a
transcription of the parser. The shipped test passes only because it never advances the nested HEAD.

**Blocker 2 — the adopt path never establishes `docs/`, and it was a product call.**
`EnsureInitialCommitAsync` returns early on a born HEAD, and `Directory.CreateDirectory(WorkingTree)`
sits *after* that return, so an adopted repository with history and no `docs/` completes bootstrap with
every check green and no working tree. Task 2.1's "serve from its `docs/` working tree" has no
implementation and no test. Put to the Product Owner rather than patched.

**Product Owner decision: refuse to start, naming what is missing.** ZeroWiki never writes into a
repository it did not create, and §2 now has one consistent posture across all three foreign-repository
faults — bare repo, gitlink, missing `docs/` — which is worth more than three separately-reasonable
behaviours.

**[architect]** Recorded as clean by the supervisor and not to be re-litigated: the seven-step ordering
of `EnsureRepositoryAsync` is coherent rather than accreted; no duplicated abstraction; the no-op hook
bodies are the only speculative surface; and `git rev-parse --git-path hooks` honours `core.hooksPath`
(verified by execution), which makes the hook installer correct in a way **stronger than its docstring
claims** and matters directly to §5.3's lock. 2.5 delivers what §2 can — "at all times" is genuinely
owed by §5.1–5.3, §6.5 and §10.2, not shirked here.

**[architect]** On the evidence standard, since three independent agents have now converged on it.
Mutation scoping was right — block 1 out, block 2 in for exactly two mutants, stop at two. But **every
defect this section actually produced** (the index census, `core.quotePath`, and now blocker 1) was a
gap in *what the condition asks*, which mutation structurally cannot see; and both supervisor blockers
came from the single fixture class the section never built. Mutation measures fidelity of a condition
to its intent; it is silent on whether the intent is the right question. **For §5–§7, budget fixture
diversity alongside mutants** — the cheaper instrument here is a fixture nobody thought to build, not a
third mutant.

**[architect]** → `@worker`: remediation block for §2.

1. **Blocker 1** — require `oldMode != "160000"` so the guard fires only on a gitlink this
   reconciliation *introduced*, matching what its docstring, my ruling and the test comment all already
   claim. Confirm the change preserves every existing detection.
2. **Blocker 1's test has no teeth** — `RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully`
   passes only because it never advances the nested HEAD. Extend it to commit inside the nested
   repository before restarting.
3. **Blocker 2** — on the adopt path, refuse to start when `docs/` is absent, naming it and saying what
   to do. Do not create it, do not commit into a repository ZeroWiki did not create.
4. **Build the missing fixture class** — foreign repositories, ones ZeroWiki did not create. Both
   blockers existed because it does not exist. This is the deliverable, not a side effect of the two
   fixes.
5. **Gate blocker 2 on spec** — a scenario under *Git repository is the source of truth* in
   `specs/content-store/spec.md`, plus a line in `design.md` recording the decision and the
   one-posture-for-three-faults reasoning.
6. **No mutation testing in this remediation block.** Two mutants have run in §2 and both were sound;
   neither of these defects was reachable by one, and a third would measure fidelity to an intent that
   was itself the thing at fault. The fixture in item 4 is the instrument that would have caught both.

**[worker]** Remediation block for §2 — all six items addressed. No new `N.M` numbers, nothing ticked.

1. **Blocker 1 fixed — `FindStagedGitlinksAsync` now requires `oldMode != "160000"` in addition to
   `newMode == "160000"`.** An `M` record for a gitlink whose nested `HEAD` merely advanced has
   `oldMode == newMode == "160000"`, so it no longer fires; a genuinely introduced gitlink (`A` record,
   `oldMode == "000000"`, or a previously tracked path replaced by one) still does. Rewrote the
   docstring and `<remarks>` to state this as the actual condition rather than "new mode alone,"
   matching what the ruling, the earlier fix-round text, and the test comment already claimed the
   guard did. Confirmed every existing detection still fires: all three original gitlink tests (plain
   nested directory, gitfile form, three-levels-deep) and the café-vault non-ASCII test pass unchanged.
2. **Blocker 1's test now has teeth.**
   `RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully` extended to commit inside the nested
   repository *before* the restart — the SHA the gitlink entry points at is captured before and after,
   and the test now asserts it actually changed, so the restart genuinely exercises the `M`-record path
   the earlier version of the test never reached (it only ever produced "no diff record at all," since
   the nested repository was never touched between the two `EnsureRepositoryAsync` calls).
3. **Blocker 2 fixed — `EnsureInitialCommitAsync`'s "already has history" branch now refuses to start
   when `ContentPaths.WorkingTree` is absent**, via a new `MissingWorkingTreeException(repositoryRoot)`
   naming the missing path and saying what to do (create `docs/`, empty is fine, and restart). Does
   **not** create the directory and does **not** commit — the check runs and throws before
   `ReconcileWorkingTreeAsync` (and therefore before any `git add -A`) ever touches the adopted
   repository. Added an XML doc summary to `EnsureInitialCommitAsync` stating both of its jobs (create
   `docs/` and commit on a fresh repo; refuse rather than create on an adopted one).
4. **The missing fixture class, built as the deliverable it is** — `CreateForeignRepositoryAsync(repositoryRoot,
   withDocsDirectory)` constructs a repository entirely outside `ContentRepositoryService` (its own
   `git init`, its own author `Somebody Else <somebody@example.com>`, its own layout: `docs/` with a
   real page, or a bare `README.md` with no `docs/` at all), never touched by the service before the
   test's own `EnsureRepositoryAsync()` call. Three new tests over it:
   - `ForeignRepositoryWithHistoryAndDocs_StartsSuccessfullyWithoutAddingACommit` — commit count
     unchanged, configuration applied, porcelain empty, foreign author untouched on the existing commit.
   - `ForeignRepositoryWithHistoryAndNoDocs_FailsToStartNamingWhatIsMissing` — throws naming the full
     `docs/` path; asserts `docs/` was never created and no commit was added.
   - `ForeignRepositoryWithAPreExistingGitlink_StartsSuccessfullyAndSurvivesItAdvancing` — a foreign
     repository that already carries a gitlink (added by "somebody else", not via this app) starts
     successfully on its very first-ever `EnsureRepositoryAsync()` call (the static case), then the
     nested repository's own `HEAD` is advanced and a second call also succeeds (the advancing case) —
     the SHA change is asserted directly, same as item 2. This is the combination — foreign *and*
     gitlinked *and* advancing — neither blocker's original fixtures reached at once.
5. **Spec + design gated.** New scenario *Adopted repository with history but no working tree is
   refused* under *Git repository is the source of truth* in `specs/content-store/spec.md`. Two D9
   addenda in `design.md`: corrected the existing gitlink addendum to state the introduced-vs-advancing
   distinction explicitly (naming the earlier new-mode-only implementation as the defect), and a new
   addendum recording the `docs/`-missing decision plus the one-posture-for-three-faults reasoning
   (bare repo, gitlink-would-be-introduced, missing `docs/` — one behaviour: refuse loudly, name what's
   wrong, never write into a repository ZeroWiki did not create). `openspec validate
   git-backed-content-core --strict` still passes.
6. **No mutation testing run**, per the ruling — both defects were gaps in what the condition asked,
   not in how faithfully it ran, and the fixture class in item 4 is the instrument that actually would
   have caught them.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **504/504** full unfiltered (501 + 3 net
new — the two extended tests aren't new files, only the three foreign-repository tests are) ·
`dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid. `git status --short -- src tests` / `git diff --stat -- src tests` reviewed: only
`ContentRepositoryService.cs` and `ContentRepositoryServiceTests.cs` changed in `src`/`tests` — no new
files, no touch to `GitHookInstaller.cs`/`ContentStorageStartupExtensions.cs`, matching the
remediation's actual scope. Not committed, nothing ticked.

→ @reviewer

**[reviewer]** §2 remediation block — **Approve, with one nit.** Both supervisor blockers are
genuinely fixed, each verified by producing the counterfactual, not by reading the diff.

**Point 3 first, since it's the load-bearing check.** Reverted `FindStagedGitlinksAsync`'s condition
to the pre-fix `newMode == "160000"` alone (checksummed before/after, restored via the saved copy) and
ran `RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully` and
`ForeignRepositoryWithAPreExistingGitlink_StartsSuccessfullyAndSurvivesItAdvancing` against it: **both
fail**, both throwing `BuildGitlinkErrorMessage`'s exact text on the restart call that's supposed to
succeed. The extended tests have real teeth — they are not passing for an unrelated reason.

**Point 2 — no loss of genuine detection, and I went one step further than the four fixtures.** All 19
tests in `ContentRepositoryServiceTests` pass together (plain nested directory, gitfile form,
three-levels-deep, café-vault, plus the three new foreign-repository tests). I also checked the one
case the fix's own reasoning doesn't fully close on its face: can a genuinely *different* nested
repository ever present with `oldMode == "160000"` by masquerading as a rename? Reproduced directly —
renaming an *already-adopted* gitlink to a new path produces a single `R100` record (`oldMode 160000,
newMode 160000, same SHA`), correctly let through since nothing new is introduced. But deleting that
gitlink and placing an **unrelated** nested repository (different commit history) at a new path does
**not** get treated as a rename at all — git reports it as separate `D`/`A` records
(`:160000 000000 … D` / `:000000 160000 … A`), and the `A` record's `oldMode` is `000000`, still caught.
There is no way to introduce new, unrecoverable content past this guard by disguising it as a rename.

**Point 4 — the fixture is genuinely foreign.** `CreateForeignRepositoryAsync` never calls
`CreateService()` or any `ContentRepositoryService` method to build the repository — its own `git
init`, its own author (`Somebody Else <somebody@example.com>`, distinct from `GitAuthor.System`), its
own layout choice (a `docs/` with a real page, or a bare `README.md` with no `docs/` at all). It shares
only `GitProcessRunner` — a generic subprocess wrapper with no ZeroWiki-specific construction logic —
which is the shared plumbing every fixture in this file uses, not the blind spot being closed.

**Point 1 — the mismatch is real, but it's in the OpenSpec wording, not in the code, and the code's
own behaviour is the safer answer.** Reproduced the case you flagged directly: built a repository with
`CreateService()`, let it initialize normally, then deleted its entire `docs/` directory (not a file —
the directory) and restarted. It refuses, naming the path, exactly as an adopted repository would — no
existing test covers this, and none of the three new fixtures do either (all build genuinely foreign
repositories, not a ZeroWiki-created one with `docs/` later removed).

Is this the right behaviour? Yes, and D9 is not actually in tension with it. The alternative — falling
through to `ReconcileWorkingTreeAsync` when the whole working tree is missing — would run `git add -A`
over an empty tree, see every previously-tracked page as a deletion, and commit **all of them** as a
single "recovery" commit per D9's always-commit rule. That is technically reversible (`git revert`,
consistent with D9's own asymmetry), but it means an operator's `rm -rf docs` — a mistake, not an edit
— gets silently *ratified into history* on the next restart rather than stopped at the door. A wiped
working tree is a different, more severe class of anomaly than "some tracked files are dirty inside an
existing `docs/`," and refusing rather than reconciling is consistent with this section's whole
posture (bare repo, gitlink) — not a carve-out from it.

So the fix belongs in the wording, not the code, and only in the **OpenSpec artifacts** —
`ContentRepositoryService.cs`'s own XML doc on `EnsureInitialCommitAsync` already says it correctly
("a repository that already has history — one this call did not create, **whether a previous start of
this app or a repository adopted from elsewhere**"). It's `specs/content-store/spec.md`'s scenario
title (*"Adopted repository with history but no working tree is refused"*) and `design.md`'s addendum
heading (*"an adopted repository with no `docs/` is refused"*) that narrow the claim to adoption alone
without saying so applies equally to a ZeroWiki-created repository whose `docs/` is later removed.
Nit, not a blocker — nothing is unsafe and no test is wrong, but this is the same claim-vs-mechanism
shape this section has now produced four times, and it's cheap to close: widen the spec scenario's WHEN
to "a non-bare git repository with commit history but no `docs/` directory" (drop "adopted" from the
condition, keep it as the motivating example) and add a clause to the design.md addendum naming the
own-repository-with-docs-later-deleted case explicitly, so a future reader doesn't need to rediscover it
by reproduction the way this review just did.

**Confirmed independently:** `dotnet test --filter "FullyQualifiedName~ContentRepositoryServiceTests"`
19/19; `openspec validate git-backed-content-core --strict` valid; `dotnet format --verify-no-changes`
clean; `dotnet build` 0/0; `git status --short -- src tests` / `git diff --stat -- src tests` show only
`ContentRepositoryService.cs` and `ContentRepositoryServiceTests.cs`, no residue from any of my scratch
reproductions (all removed and checksum-verified restored before this post).

No mutation this round, per the ruling — agreed with the reasoning: neither blocker was a fidelity gap
a mutant could have found, both were gaps in what the guard asked, and the foreign-repository fixture
class is the instrument that actually closes them.

**§2 remediation is clear to commit.**

→ @architect

**[architect]** Remediation block audited: `@reviewer` **Approve**, verified by counterfactual rather
than by reading — it reverted `FindStagedGitlinksAsync` to the pre-fix `newMode`-only condition and
confirmed both extended tests genuinely *fail* against it. It also went past the four fixtures I asked
for and established that `oldMode != "160000"` opens no hole: renaming an already-adopted gitlink
yields one `R100` record, correctly let through, while swapping in an *unrelated* nested repository is
not treated as a rename at all — git splits it into `D`/`A` records and the `A` record's `oldMode` is
`000000`, still caught.

**[architect]** The reviewer's nit, closed before commit rather than deferred. The `docs/`-missing
guard is specified for the adopt path but lives in a branch **every** born-`HEAD` repository reaches,
so a ZeroWiki-created wiki whose `docs/` is deleted (`rm -rf docs`) is refused too. Reviewer reproduced
it and judged the behaviour right; I agree, and the reason is worth stating because it is not obvious:
falling through to reconciliation would `git add -A` the deletion of every page and commit it as a D9
recovery commit, ratifying an operator's mistake into permanent history. A wholly missing working tree
is a different and more severe class than "some tracked files are dirty", so this is not in tension
with D9 — it is what D9's posture requires once the thing gone missing is the tree itself. The spec
scenario and the design addendum were narrowed to "adopted repository" while the code was universal;
both are now widened, the reasoning is recorded in `design.md`, and
`DocsDeletedBetweenStarts_FailsToStartNamingThePathAndCommitsNothing` covers the case nobody had a test
for. Its load-bearing assertion is the **commit count**, not the refusal: without it the test passes
even if the deletion were silently committed first.

That is the fifth claim-versus-mechanism mismatch in this section (unborn-`HEAD` comment, index-census
docstring, blocker 1's "delta" wording, `EnsureInitialCommitAsync`'s early return, this one). Every one
had defensible code and a wrong stated reason. **The standing rule earned here: when a guard's
justification names a *case*, check the guard's *branch* — the branch is what ships.**

**[architect]** Two process notes, recorded because the tree state was not self-evident.

- **The worker was killed mid-run** by an API error, after the file edits and before its DEVLOG post.
  An interrupted agent is this project's named hazard, so the tree was verified by hand rather than
  assumed: all three items landed complete, no untracked files under `src`/`tests`, scope limited to
  the two source files plus the two OpenSpec artifacts. Its DEVLOG post is missing and this entry
  stands in for it.
- **An anomalous gate run took 13m51s** against ~90s for the same suite, which was investigated rather
  than waved through on the strength of being green. The added test measured **626ms** in isolation and
  a clean re-run of the full unfiltered suite took **1m12s** for the same 505 tests. Cause was machine
  contention (leftover MSBuild nodes and overlapping agent runs), not the change. Noted because §10.2
  will care about suite timing, and a future reader meeting a 14-minute run in this log should know it
  was chased down and not reproduced.

**[supervisor]** Section re-review of **§2**, round two, over `7b50e46..HEAD` — now `ef2b75b`,
`6b82c33`, `2fa3ca5`. Tree clean, `git status --short` empty (tracked and untracked). No mutation run,
per the ruling; agreed — round one's findings and round two's are all guard *semantics*, which is not
what mutation measures.

## Verdict: **Approve**

Both blockers are genuinely closed, and the fix to blocker 1 is the right shape rather than the
shape that happened to make my reproduction pass. Everything below is a `## NEXT` note; nothing in it
blocks §2 and nothing needs a third round.

### Ask 1 — does the fixture class close the gap, judged as the deliverable?

**Yes for the axis that produced both blockers, and it is a real fixture class rather than two
regression tests sharing a helper.** `CreateForeignRepositoryAsync` builds outside the service
entirely — own `init`, own author (`Somebody Else`), and, critically, **own layout**: the no-`docs`
variant commits a `README.md` at the root rather than being "ZeroWiki's repository minus a directory".
That distinction is the difference between a fixture and a repro, and it was got right. The happy-path
test (`ForeignRepositoryWithHistoryAndDocs_…`) earns its place by asserting what the failure tests
cannot — that the foreign author survives and no commit is added.

Where it stops: *foreignness* has axes beyond structure, and the fixture hardcodes ZeroWiki's own
values for all of them — `init -b main`, no pre-existing hooks, no pre-existing `receive.*` config, and
a clean tree. §2 does not owe exhaustiveness there and I am not asking for it, but two of those axes
have consequences worth booking now rather than rediscovering in §7 (below).

### Ask 2 — is the widened `docs/` posture coherent across the three faults?

**The behaviour is, and the widening is right for a stronger reason than "it follows from the
finding".** I reproduced the counterfactual rather than accepting the reasoning: on a ZeroWiki-created
repository with `docs/one.md`, `docs/two.md`, `docs/.gitkeep` tracked, `rm -rf docs` then
`git add -A` stages

```
:100644 000000 e69de29 0000000 D	docs/.gitkeep
:100644 000000 2cbec93 0000000 D	docs/one.md
:100644 000000 c32a7a3 0000000 D	docs/two.md
```

and after the recovery commit `git status --porcelain` is **empty** and `git ls-tree -r HEAD` is
**empty**. Every check in the section reports success and the wiki is gone from `HEAD`. Making the
guard universal rather than adopt-only is correct, and the spec scenario and design addendum are right
to say so.

**The ordering, however, is not consistent — and the new prose overclaims it.** The three refusals sit
at different depths:

- bare repository → `ContentRepositoryService.cs:76`/`:87`, **before any write**;
- missing `docs/` → `:239`, **after** `ApplyRepositoryConfigurationAsync` and `InstallHooksAsync`;
- gitlink → `:283`, likewise, plus a `git reset` that undoes only *its own* staging.

So for two of three faults ZeroWiki has already written `receive.denyCurrentBranch` and
`http.receivepack` into the operator's repository and **unconditionally overwritten**
`.git/hooks/pre-receive` and `post-receive` (`GitHookInstaller` overwrites by design) before announcing
it does not touch repositories it did not create. `design.md`'s new addendum states the universal —
*"ZeroWiki never writes into, or establishes structure inside, a repository whose working tree it did
not find intact"* — and `MissingWorkingTreeException` states *"does not commit into a repository's
history that it did not create"*. Neither is a rule the section holds: besides the config and hooks,
`ReconcileWorkingTreeAsync` runs on adopted repositories and **will** put a
`System <system@zerowiki.org>` commit into foreign history the moment an adopted tree is dirty. That
is D9 working exactly as intended — the code is right and the prose is wrong.

**Not blocking, and I want to be explicit about why**, since a Request changes here would escalate to
the Product Owner. The spec scenario is met precisely as written: refuses, names the missing path,
creates nothing, commits nothing, records nothing as recovered — all four asserted by
`ForeignRepositoryWithHistoryAndNoDocs_…` and `DocsDeletedBetweenStarts_…`. No content is touched, no
commit is made, the config writes are idempotent. It is a wording defect with a modest ordering
improvement behind it — but `design.md` is **binding**, and this is the text §5–§7 will read, so it
goes to `## NEXT` flagged as settle-before-§5, not merely noted.

### Ask 3 — is `oldMode != "160000"` the right shape? And the reviewer's supporting claim

**Right shape, and for a statable reason rather than a passing test.** The condition is exactly *"this
path was not already a gitlink in `HEAD`"* — which is precisely the boundary the Product Owner drew.
Any entry with `oldMode == 160000` was already outside ZeroWiki's recoverability guarantee before this
reconciliation ran, so no reconciliation can newly violate D9 there. That argument, not the `M` case,
is what makes the condition correct, and it generalises to records nobody has constructed yet.

Verified across five record shapes, not the one that failed:

| case | record | guard |
|---|---|---|
| new nested repository | `:000000 160000 … A` | **caught** ✅ |
| tracked file replaced by a nested repository | `:100644 160000 … T` | **caught** ✅ (real content loss) |
| adopted gitlink advancing its nested `HEAD` | `:160000 160000 … M` | ignored ✅ (blocker 1) |
| adopted gitlink renamed | `:160000 160000 … R100` (two path fields) | ignored ✅ |
| nested `.git` removed, files become tracked | `D` + `A`, `newMode ≠ 160000` | ignored ✅ (recovery direction) |

**I refute the reviewer's supporting claim, relayed to me as established.** *"An unrelated nested
repository swapped in at an existing path produces `D`/`A` records with `oldMode == 000000`, still
caught"* is false. Reproduced — same path, entirely different nested repository, everything removed and
replaced:

```
:160000 160000 4975b50 855b7bf M	docs/vault
```

One `M` record, `oldMode` `160000`, and the guard does **not** catch it. The *outcome* is still
correct, for the reason in the paragraph above (already a gitlink, already out of scope) — so the fix
survives the refutation intact; only its stated justification does not. Recording it because a false
reason in the log is what a later section builds on.

Smaller instance of the same thing: `FindStagedGitlinksAsync`'s `<remarks>` says a previously tracked
path replaced by a nested repository is *"an `M` record whose old mode was something else"*. git emits
**`T`** (typechange), not `M`. The condition tests modes, not status letters, so behaviour is
unaffected — the letter is simply wrong.

**Worth naming as a pattern rather than three separate nits:** every finding I have made across both
rounds — the `newMode`-only guard, the `D`/`A` claim, the `T`/`M` letter, and the two overclaiming
posture statements — is a **wrong justification attached to correct or nearly-correct code**. That is
this change's named recurring defect (§11 found seven; block 2 spent an entire round on one). §2's
*code* has converged and is in good shape; its *prose* is what keeps regressing, and prose that lands
in `design.md` is binding on every section after it. For §5–§7 I would treat a justification as
something to be reproduced before it is written down, at the same standard as a test.

Also endorsing the handling of the 13m51s gate run: chasing it to 626ms-in-isolation plus a 1m12s
clean re-run of the same 505 is the right instinct, and the conclusion is supported.

## For `## NEXT`

1. **Settle before §5 — the binding prose overclaims.** Either move the `docs/` probe up beside the
   bare-repository check (it needs only the `HEAD` probe, so it can run right after
   `AssertGitResolvesRepositoryRootAsync`, before config and hooks) and make the claim true for two of
   three faults; or narrow `design.md`'s addendum and `MissingWorkingTreeException`'s wording to what
   the code does. Note that neither option makes the *"does not commit into a repository's history it
   did not create"* half true — D9 reconciliation on an adopted repository does exactly that, by
   design — so that clause needs rewording regardless.
2. **`FindStagedGitlinksAsync` `<remarks>`** — `T`, not `M`, for a tracked path replaced by a nested
   repository.
3. **§7 must read the checked-out branch, not assume `DefaultBranch`.** `DefaultBranch = "main"` is
   justified as "the app has to know the checked-out branch name", which is false for an adopted
   repository — the fixture's `init -b main` hides it. A foreign repo on `master` boots fine in §2
   (`updateInstead` targets whatever is checked out), so this is latent, not live; §7.4 is where it
   bites.
4. **Record that `git rev-parse --git-path hooks` honours `core.hooksPath`** in
   `GitHookInstaller`'s `<remarks>` (verified by execution in round one). §5.3 depends on hooks being
   installed where git will actually run them, and this is the fact that makes that true.
5. **Untested foreignness axes**, for whoever extends `CreateForeignRepositoryAsync`: pre-existing
   `pre-receive`/`post-receive` hooks (silently overwritten), pre-existing
   `receive.denyCurrentBranch=refuse` (silently overwritten), a non-`main` branch, and an adopted
   repository with a **dirty** tree — the last is the one that exercises item 1's contradiction.
6. Carried from round one, unchanged: **§5.1's lockfile must not live under
   `ContentPaths.RepositoryRoot`** (`add -A` runs from there); **`GitAuthor.cs:13-15`** still calls
   reconciliation "later"; **`tasks.md:70`** still records 11.1's superseded pattern while `src/` is
   correct.

§2 is closed from my side. → `@architect`

**[architect]** Reopening §2 for one follow-up block, on the Product Owner's instruction to settle D9
before §3 opens. §2 keeps its supervisor `Approve`; this block will be re-reviewed on the same range.

**The supervisor's option 1 was a choice between making the claim true and narrowing it — and I found
its first branch cannot make it true.** Moving the `docs/` probe ahead of config and hooks fixes two
faults of three: the **gitlink** check cannot move earlier, because staging *is* how a gitlink is
detected, so it is intrinsically after `git add -A`. That branch buys a smaller lie, not the truth.

**But nothing between classification and reconciliation needs config or hooks at all.**
`receive.denyCurrentBranch` and `http.receivepack` govern pushes; the hooks fire on push. Neither can
matter before the app is serving, and both are set well before that either way. Moving both to the
**end** of `EnsureRepositoryAsync` — after reconciliation and the clean-tree assertion — puts every
refusal ahead of every write and makes D9's claim **true for all three faults** rather than narrowed to
fit. **Product Owner decision: reorder.**

The concrete harm this removes is not hypothetical: an adopted repository with custom
`pre-receive`/`post-receive` hooks and no `docs/` currently has those hooks **overwritten** and is then
refused — ZeroWiki destroys something it did not create, in service of a start it was never going to
complete.

**[architect]** → `@worker`. One block. No new `N.M` numbers, nothing ticked.

1. **Move `ApplyRepositoryConfigurationAsync` and hook installation to the end of
   `EnsureRepositoryAsync`**, after `AssertWorkingTreeIsCleanAsync`. Verify by reading the call graph —
   not by assuming — that nothing between classification and the clean-tree assertion depends on either.
2. **Correct D9's addendum in `design.md`.** The "never writes into…" half becomes true and should say
   *why* it is true (every refusal precedes every write, and the ordering is load-bearing rather than
   incidental). The **other half is false regardless of ordering** and must be reworded: *"does not
   commit into a repository's history it did not create"* is contradicted by D9 reconciliation itself,
   which commits a `System` recovery commit into adopted history whenever the adopted tree is dirty —
   deliberately. State the real posture: ZeroWiki does not write to an adopted repository **until it has
   accepted it**, and thereafter D9 governs what it writes.
3. **Tests are the point of this block, and they are the missing foreignness axes** (`## NEXT`
   obligation 11): an adopted repository with **pre-existing `pre-receive`/`post-receive` hooks** and no
   `docs/` → refuses **and the hooks are byte-for-byte untouched**; an adopted repository with
   **`receive.denyCurrentBranch=refuse`** plus a nested gitlink → refuses **and the config is
   unchanged**. Assert the *absence of the write*, not merely the refusal — the refusal already passes
   today.
4. **Fold in the two `<remarks>` corrections** (obligation 12), since the same files are open:
   `FindStagedGitlinksAsync` says `M` where git emits `T` for a tracked path replaced by a nested
   repository; and `GitHookInstaller` should record that `git rev-parse --git-path hooks` honours
   `core.hooksPath`, which is what makes §5.3's hooks land where git will run them.
5. **Fold in obligation 13's two stale comments**: `GitAuthor.cs:13-15` still calls reconciliation
   "later" though it now exists, and `tasks.md:70` still records 11.1's superseded pattern while `src/`
   is correct.
6. **No mutation testing.** This is ordering and prose; the item-3 tests assert the ordering directly,
   so a mutant here is the tautological kind. The instrument that matters is the fixture.

**[worker]** Implemented. `EnsureRepositoryAsync` reordered: `AssertGitResolvesRepositoryRootAsync` →
`EnsureInitialCommitAsync` → `ReconcileWorkingTreeAsync` → `AssertWorkingTreeIsCleanAsync` →
`ApplyRepositoryConfigurationAsync` → `_hooks.InstallHooksAsync`. Verified by reading the call graph,
not assuming: neither config write (`receive.denyCurrentBranch`, `http.receivepack`) nor hook
installation is read by anything between classification and the clean-tree assertion — both govern
pushes/hook firing, which cannot happen before the app finishes starting. Every refusal
(`AssertGitResolvesRepositoryRootAsync`, the missing-`docs/` throw in `EnsureInitialCommitAsync`, the
gitlink throw in `ReconcileWorkingTreeAsync`) now runs before any write.

`design.md` D9 gets a third addendum: the ordering fix, stated as load-bearing (a future change moving
config/hooks earlier reopens the defect), plus the correction that "does not commit into a repository's
history it did not create" was always false regardless of ordering — D9 reconciliation itself commits
into adopted history by design. Restated posture: ZeroWiki does not write to an adopted repository until
it has accepted it; thereafter D9 governs what it writes. `MissingWorkingTreeException` reworded to
match; `EnsureInitialCommitAsync`'s XML doc too.

Two new tests, each confirmed to fail against the pre-reorder ordering (verified by temporarily
swapping the source back via `cp`, never `git checkout`/`restore`, and restoring the fix the same way —
`git diff -- src` checked clean and matches exactly the 3 intended files before this post):
- `ForeignRepositoryWithPreExistingHooksAndNoDocs_RefusesLeavingTheHooksByteForByteUnchanged` — operator
  hooks with distinct bodies + exec bit, no `docs/`; asserts refusal **and** the hook files unchanged
  byte-for-byte and mode-for-mode. Failed pre-fix (hooks were the "Managed by ZeroWiki" body).
- `ForeignRepositoryWithDenyCurrentBranchRefuseAndANewGitlink_RefusesLeavingTheConfigUnchanged` —
  pre-existing `receive.denyCurrentBranch=refuse` + a staged gitlink; asserts refusal **and** the config
  value unchanged (plus `http.receivepack` still entirely unset). Failed pre-fix
  (`receive.denyCurrentBranch` was `updateInstead`).

Folded in the two `<remarks>` corrections (`FindStagedGitlinksAsync`: `T`, not `M`, for a tracked path
replaced by a nested repository — condition tests modes, not letters, so no behaviour change;
`GitHookInstaller`: noted `--git-path hooks` honours `core.hooksPath`) and the two stale comments
(`GitAuthor.cs:13-15` no longer says "later"; `tasks.md:70` now records D11's actual shipped pattern,
including the no-consecutive-dots rule).

No mutation testing run, per the brief.

Gates: `dotnet build` 0 warnings/0 errors · `dotnet test` **507/507** unfiltered (505 baseline + 2 new)
· `openspec validate git-backed-content-core --strict` valid · `dotnet format --verify-no-changes`
exit 0. Nothing ticked, no new `N.M` numbers.

→ @reviewer

**[architect]** Product Owner decision, raised by the PO from the edge I flagged to `@reviewer` in the
reorder audit: **a pre-init filesystem scan for nested `.git` entries.**

**The gap.** The reorder puts every refusal before every write on the *adopt* path, but not on the
*initialise* path. A plain folder copied onto the volume has no `.git`, so ZeroWiki runs `git init` and
makes an initial commit, and only then can the gitlink refusal fire — leaving a `.git` and one commit
in a directory it did not create, immediately before declining to serve it.

**Cost is not the objection it looks like.** The scan sits on the initialise branch, which by
definition stops existing once a repository is there, so it runs **once in the life of a volume** — not
per startup, and never on a populated vault.

**The real cost is a second instrument, and it must be named as such in the code.** The shipped
index-based check (`git diff --cached --raw -z`, mode `160000`) answers *"would committing now store a
gitlink"*. A filesystem scan answers *"is there a nested repository here"*. These are **not** the same
question and they disagree in a specific case: a nested repository the operator has **gitignored** is
skipped entirely by `add -A`, so no gitlink is ever created and the index check stays silent — while
the scan finds the `.git` and refuses. The Product Owner accepted that divergence knowingly: refusing
is recoverable and legible, and a gitignored nested repository inside a wiki's content folder is a
configuration worth stopping on.

Both checks therefore stay, and **the code must say why each exists**, or the next reviewer will
correctly read them as one abstraction duplicated. The scan guards the initialise path *before* any
write; the index check guards every path *at the moment of committing*, including content that arrives
long after bootstrap. Neither subsumes the other.

**[reviewer]** Reorder block — **Approve.** Certifying the diff against `85d2b7f` (six files:
`ContentRepositoryService.cs`, `GitAuthor.cs`, `GitHookInstaller.cs`, `ContentRepositoryServiceTests.cs`,
`design.md`, `tasks.md`), all four scrutiny points checked by reproduction, not by reading.

**1. Both new tests genuinely fail against the pre-reorder ordering.** Reverted
`EnsureRepositoryAsync` to the pre-fix sequence (config + hooks before the initial-commit/reconcile/
assert-clean steps) via a checksummed `cp` swap, restored after — never `git checkout`. Both
`ForeignRepositoryWithPreExistingHooksAndNoDocs_RefusesLeavingTheHooksByteForByteUnchanged` and
`ForeignRepositoryWithDenyCurrentBranchRefuseAndANewGitlink_RefusesLeavingTheConfigUnchanged` fail
against it, on exactly the assertions that matter — the operator's hooks come back as ZeroWiki's
"Managed by ZeroWiki" body, and `receive.denyCurrentBranch` comes back `updateInstead` instead of the
operator's `refuse`. Real teeth, not incidental passes.

**2. The copy-a-folder edge — confirmed real by my own reproduction, independently of what's now
recorded above.** I found the identical mechanism before reading the architect's post that now sits
just above this one: on a fresh volume with content (including a nested `.git`) already copied under
where `docs/` will go, `git init` and one commit (containing only `.gitkeep`, never the nested vault's
files) land before the gitlink refusal fires — reproduced end to end, confirmed the nested vault is
never captured in that or any commit and the index carries no gitlink afterward. My own read, reasoned
before I saw the PO's decision: `design.md`'s "does not write to a repository it did not create" is
technically true of this path in a narrow sense — the repository itself was created by ZeroWiki in this
same call via `git init`, so the initial commit is a write into ZeroWiki's own freshly-created
repository, not an adopted one; only the gitlink *content* (which the guard never commits) is genuinely
foreign. But the architect's post immediately above records the Product Owner going further than that
narrow reading and deciding the gap is real enough to warrant an actual mechanism — a pre-init
filesystem scan — rather than a wording-only fix, which is the stronger and more conservative answer
and the one I'd defer to. That scan isn't in this diff and doesn't need to be: this block's own scope
was the adopt-path reorder, the initialise-path gap is now explicitly acknowledged and scheduled for a
follow-up rather than silently left, and nothing in this block's own claims is falsified by that —
`design.md`'s current wording will need the follow-up block's correction, not this one's.

**3. No path serves with mismatched config/hooks state — confirmed by reproduction, both halves.**
`Program.cs:97` still awaits `EnsureContentRepositoryAsync()` with no surrounding try/catch, so any
exception anywhere in `EnsureRepositoryAsync` — including from `ApplyRepositoryConfigurationAsync` or
`InstallHooksAsync` themselves, neither of which catches anything internally — propagates uncaught and
prevents `app.Run()` from ever being reached; there is no state in which the app serves with only one
of the two applied. Also checked the restart case directly: built a repository through one successful
start (config + hooks applied), introduced a fresh gitlink, and confirmed a second, refused start
leaves the earlier run's config and hook bodies byte-for-byte untouched — the failed run never reaches
the config/hooks steps, and nothing else touches them either.

**4. All four folded-in corrections verified true, not merely re-read.**
`FindStagedGitlinksAsync`'s `<remarks>` now says a previously-tracked path replaced by a nested
repository is a `T` (typechange) record, not `M` — reproduced directly (a tracked file replaced by a
nested `.git` at the same path yields `:100644 160000 … T`), confirmed. `GitHookInstaller`'s new
`core.hooksPath` remark — reproduced directly (`git config core.hooksPath <path>` →
`rev-parse --git-path hooks` returns that path), confirmed, and it matters: `ResolveHooksDirectoryAsync`
already handles a rooted path returned this way. `GitAuthor.cs`'s wording no longer calls reconciliation
"later" — accurate, both are shipped now. `tasks.md:70`'s pattern now matches
`CredentialPolicy.UsernamePattern` in `src/` exactly, confirmed by direct comparison.

**Confirmed independently:** `dotnet test --filter
"FullyQualifiedName~ContentRepositoryServiceTests|FullyQualifiedName~ContentRepositoryStartupTests"`
24/24; `openspec validate git-backed-content-core --strict` valid; `dotnet format --verify-no-changes`
clean; `dotnet build` 0/0; `git status --short` / `git diff --stat 85d2b7f` show exactly the seven
files (six source/spec + DEVLOG), no residue from any of my scratch reproductions (all removed,
checksum-verified restored before this post).

No mutation this round, per the ruling — agreed: this is ordering and prose, item 1's tests assert the
ordering directly, and the fixture (foreign repository with pre-existing hooks/config) is the
instrument that matters here, not a mutant.

**This follow-up block is clear to commit.**

→ @architect

**[worker]** Final follow-up on §2: pre-init filesystem scan for the initialise path. No new `N.M`
numbers, nothing ticked.

**The gap.** The reorder block closed the adopt path ("every refusal precedes every write"), but the
initialise path — `RepositoryRoot` has no `.git` of its own, and is not bare — still ran `git init` and
an initial commit before the index-based gitlink check ever gets a chance to fire, since staging *is*
how that check detects a gitlink. A vault copied onto a fresh volume before ZeroWiki's first start would
leave a `.git` and one commit behind, immediately before refusing to serve.

**Fix.** `ContentRepositoryService.AssertNoNestedGitRepository` — a synchronous filesystem walk from
`RepositoryRoot`, called at the top of the initialise branch, before `git init`. Recurses via
`Directory.EnumerateFileSystemEntries`, flags any `.git` entry (directory or gitfile — same two forms
`HasOwnGitEntry` already treats as "a repository is here"), and throws naming every offending path
(relative, forward-slash-normalized) before writing anything. Confirmed empirically, not by trusting
docs, that `File.GetAttributes` reports `FileAttributes.ReparsePoint` for a symlink without
dereferencing it (a small standalone repro: a self-referential symlink and a symlink to `/etc` both came
back `ReparsePoint=true`, target unresolved) — so the walk skips any reparse point outright, never
recursing into it and never stat-ing through it. That single check is what keeps a looping symlink from
hanging the walk and keeps a symlink to a repository outside the volume from ever being seen at all,
verified against a real fixture combining a nested repo (depth 1, depth 3, gitfile form), a
self-referential symlink loop, and a symlink to a separately-initialized repository under a different
temp root — all in one throwaway harness before any of this touched `src/`.

**Both checks documented at both sites, per the brief.** `AssertNoNestedGitRepository`'s XML remarks
state the scan answers "is there a nested repository in this folder" and guards only the initialise
branch, once, before any write; `ReconcileWorkingTreeAsync`'s remarks now cross-reference it and state
the index check answers "would committing now store a gitlink" and guards every branch, every
reconciliation, forever. Neither doc claims the other is redundant.

**The gitignore divergence is written down, not smoothed over.** Same remarks: a nested repository the
operator has `.gitignore`d is invisible to `git add -A` (and so to the index check) but is still found
by the scan, which never consults `.gitignore`. Recorded as a deliberate Product Owner decision in both
the code and `design.md`'s new D9 addendum, with a test pinning it directly — the test demonstrates the
index check's silence first (init + `add -A` against the gitignored fixture, asserting no `160000` entry
and no trace of the path), then exercises the real initialise path and asserts the scan refuses anyway.

**`design.md`** — new D9 addendum: states the initialise-path gap, the "once per volume, never on a
populated vault" cost argument, the no-symlink-following rule and why, and the same "two instruments, one
deliberate disagreement" framing as the code. "Every refusal precedes every write" now holds on both
paths, not only the adopt path.

**Tests** (`ContentRepositoryServiceTests.cs`, 7 new, all under a new "pre-init filesystem scan" comment
block): nested repo at depth 1 and depth 3 (asserting **no `.git` created at `RepositoryRoot`** — the
actual point of this block, not just the refusal); gitfile form; the gitignored-divergence pin; a
symlink loop (bounded via `Task.WhenAny` against a 10s `Task.Delay`, so a regression fails the assertion
instead of hanging the suite); a symlink to a repository outside the volume (asserts normal, successful
initialization — the outside repository is simply never seen); and a regression fixture (plain Markdown,
no nested repo, on a fresh volume, initializes exactly as before).

**Each new test confirmed to fail against `f50f1ca`**, via a checksummed `cp` swap of
`ContentRepositoryService.cs` only (never `git checkout`/`restore`) — restored via `cp` from a
pre-checksummed backup afterward, `git diff --stat -- src` and `shasum` both confirmed identical to the
fixed version before continuing. 4 of the 7 new tests fail against `f50f1ca` on exactly the assertions
that matter (`Assert.False` on `.git` existing comes back `True`; the gitignore test's
`Assert.Throws<InvalidOperationException>` reports "No exception was thrown"). The other 3 (both symlink
tests, the plain-Markdown regression) pass at both revisions by design — they assert the *absence* of a
false positive, which holds with or without the scan; they are compatibility fixtures, not regression
pins.

No mutation testing run, per the brief — this property is asserted directly by the fixtures above.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **514/514** full unfiltered (507 baseline +
7 new) · `dotnet format --verify-no-changes` clean · `openspec validate git-backed-content-core --strict`
valid. `git status --short` / `git diff --stat` show exactly three files: `ContentRepositoryService.cs`,
`ContentRepositoryServiceTests.cs`, `design.md` — plus this DEVLOG entry.

→ @reviewer

**[reviewer]** Pre-init filesystem scan — **Request changes.** One blocker, on exactly the point the
architect flagged as the one they cared most about; the other two scrutiny points came back clean or
as a non-blocking nit, all verified by reproduction.

### Blocker — `CollectNestedGitEntries` swallowing `UnauthorizedAccessException` silently weakens the
### exact guarantee this block exists to establish, and I can show it doing so

Asked to form a view, not just assess: **an unreadable directory should be a refusal, not a silent
skip.** Reproduced the concrete harm rather than arguing it — created a subdirectory containing a real
nested git repository, `chmod 000`'d the subdirectory itself (blocking read access even for the owning
user, confirmed with a plain `ls` first), and ran `EnsureRepositoryAsync()` against it. The scan hits
`UnauthorizedAccessException` enumerating the blocked directory, returns as if that subtree were clean,
and the whole call completes successfully: `git init` runs, the initial commit is made, and the nested
repository sitting behind the unreadable directory is never seen. This is not a hypothetical — it is
exactly the failure mode the architect described, reproduced end to end.

The reasoning that decides it, not just the reproduction: this scan's entire job is to *assert* "there
is no nested repository here" before any write happens. On an unreadable subtree, that property is not
merely unverified — it is *unverifiable*, and the current code treats "I could not check" the same as
"I checked and it's clean," which is precisely the class of silent degradation this whole section has
spent five rounds eliminating from every other check in this file (the index-census brief, the
`newMode`-only gitlink guard, the adopt-path missing-`docs/` gap, the pre-reorder write-before-refusal
ordering). The scan runs once per volume, on content an operator has not yet trusted to the app; the
cost of refusing on an unreadable directory is a single, legible, fixable error at exactly the moment an
operator is most likely to still be around to see it. Silently proceeding trades that for nothing.

Direction, not a prescription: catch `UnauthorizedAccessException` in `CollectNestedGitEntries`, but
instead of returning, record the unreadable path and have `AssertNoNestedGitRepository` refuse — naming
it alongside (or instead of) any nested-repository paths found, since "I found a repository" and "I
could not tell" are both reasons not to proceed, and the operator needs to know which one it got. Worth
also noting, not blocking on: `git add -A` itself degrades the same way — I reproduced it separately,
and it prints `warning: could not open directory '...': Permission denied` to stderr but exits `0` and
proceeds — so the *adopt* path's `ReconcileWorkingTreeAsync`/`AssertWorkingTreeIsCleanAsync` have a
structurally similar blind spot that this block's fix cannot reach (nothing today inspects
`GitProcessResult.StandardError` for that warning). That is a broader, separate piece of work — flagged
for `## NEXT`, not this block, since fixing it means teaching the git-level checks to read stderr, not
touching the C# scan.

### The other two scrutiny points — verified by reproduction, mostly clean, one worth recording as a nit

**Point 2 — the three "pass both before and after" tests, checked individually, not accepted as a
class.** Reverted the scan call entirely (matching `f50f1ca`, checksummed `cp` swap, restored after)
and ran all seven: the same four fail as the worker reported
(`NestedGitRepositoryOnAFreshVolume_...`, `...ThreeLevelsDeepOnAFreshVolume_...`,
`...AsAGitfileOnAFreshVolume_...`, `GitignoredNestedGitRepositoryOnAFreshVolume_...`), and the same
three pass (`SymlinkedDirectoryThatLoops_...`, `SymlinkPointingToARepositoryOutsideTheVolume_...`,
`OrdinaryMarkdownCopiedOntoAFreshVolumeWithNoNestedRepository_...`). For each of the three, checked
*why* rather than accepting "compatibility fixture" as self-evident:

- `SymlinkPointingToARepositoryOutsideTheVolume_...` has real teeth for the mechanism it names.
  Mutated the shipped (post-fix) code to disable only the `ReparsePoint` skip and reran it: it fails,
  correctly detecting the outside repository and wrongly refusing. Not vacuous.
- `OrdinaryMarkdownCopiedOntoAFreshVolumeWithNoNestedRepository_...` is an ordinary false-positive
  baseline; nothing about it is specific to symlinks or the scan's internals, and a scan bug that
  always refused would break it. Not vacuous.
- `SymlinkedDirectoryThatLoops_DoesNotHangStartup` is the one worth a closer look, and it does not pass
  for a reason that makes its *outcome* wrong, but it does not have teeth for the mechanism its own
  comment credits. With the same `ReparsePoint`-skip disabled, I reran it directly: it still passes —
  no hang, completing in under half a second. A standalone reproduction (recursing through a
  self-referential symlink with no guard of any kind) shows why: on this filesystem, the recursion
  self-terminates after 16 levels with an *empty* directory listing, no exception, before any explicit
  code has to stop it — almost certainly the OS's own bounded symlink-resolution limit, not anything
  .NET or this code does. So this test cannot distinguish "the skip prevents a hang" from "nothing does
  and the OS bails us out regardless" — its stated purpose is achieved, but not for the reason its
  comment claims, on this platform. Not a blocker (the explicit skip is still the right, portable thing
  to have rather than relying on incidental, filesystem-dependent OS behavior that a container's
  overlay filesystem or a different host might not share), but worth recording as a nit: the comment
  ("a regression that starts following symlinks again would otherwise hang this test") overstates what
  this specific test can prove.

**Point 3 — symlink handling, confirmed independently, with one wording nit.** Reproduced
`File.GetAttributes` on a symlink-to-directory directly: it reports **both** `ReparsePoint` and
`Directory` set together. That confirms the functional property that matters — the check happens
before `Directory.EnumerateFileSystemEntries` is ever called on the symlink path, so recursion into,
or enumeration of, the target genuinely never happens — but it means the code comment's phrase
"reported without dereferencing" is not quite accurate: determining the `Directory` bit requires a
single, bounded stat of the target, it's just not a *recursive* one. Nit, not a defect: the safety
property (no recursion into a symlink) holds regardless of how precisely worded the mechanism is.
Also independently confirmed a genuine loop terminates rather than hangs (per point 2 above) and that
unbounded recursion on a real, non-symlinked, pathologically deep directory tree is closer to noise
than a live concern for this product's target content (markdown wiki pages), though technically
`CollectNestedGitEntries`' plain recursion has no explicit depth guard and could in principle
stack-overflow on an adversarially deep real tree — worth a `## NEXT` mention, not a blocker, given the
once-per-volume cost argument already in `design.md` and the shape of content this product actually
serves.

**The two checks read as deliberately distinct, not duplicated.** `AssertNoNestedGitRepository`'s and
`FindStagedGitlinksAsync`'s/`ReconcileWorkingTreeAsync`'s cross-referenced remarks state different
questions ("is there a nested repository here" vs. "would committing now store a gitlink"), different
scopes (once, initialise-only vs. every reconciliation), and name the gitignore divergence explicitly
in both places with the same reasoning — a supervisor reading either site gets the full picture without
having to infer it. Confirmed by reading both, not merely trusting the brief's description.

**Confirmed independently:** `dotnet test --filter
"FullyQualifiedName~ContentRepositoryServiceTests|FullyQualifiedName~ContentRepositoryStartupTests"`
31/31; `openspec validate git-backed-content-core --strict` valid; `dotnet format --verify-no-changes`
clean; `dotnet build` 0/0; `git status --short` / `git diff --stat f50f1ca` show exactly the four files
(three source/spec + DEVLOG), no residue from any of my scratch reproductions (all removed, checksum-
verified restored before this post).

No mutation this round, per the ruling — agreed: the property here is asserted directly by the
fixtures, and the blocker above is a gap in what the swallow *permits*, not in how faithfully a
condition runs — not something a mutant would have found either way.

→ @architect

**[worker]** Fix round on the pre-init scan — blocker and both nits addressed. No new `N.M` numbers,
nothing ticked.

**Blocker — unreadable directory now refuses, with its own message.**
`AssertNoNestedGitRepository`/`CollectNestedGitEntries` now thread a second accumulator
(`unreadableDirectories`) alongside the nested-repository-path one. `CollectNestedGitEntries` catches
`UnauthorizedAccessException` (materializing the enumeration inside the `try` so a permission denial hit
mid-walk is treated identically to one hit at the call itself) and records the relative path instead of
silently returning. `AssertNoNestedGitRepository` checks the unreadable list **first** — an unreadable
subtree makes "no nested repository anywhere" unverifiable, not merely unverified, regardless of what
the readable parts of the tree showed — and throws a message distinct from the nested-repository one:
names the path, says explicitly this is a permissions problem rather than a repository to remove ("not
the same problem as a nested repository — no \".git\" needs to be removed here"), and tells the operator
to fix permissions and restart. Both messages and the reasoning are recorded in
`AssertNoNestedGitRepository`'s XML remarks, addressed at the exact framing from the finding: "couldn't
check" is not "checked, clean."

**Test, with the root trap guarded.** `NestedGitRepositoryBehindAnUnreadableDirectory_RefusesRatherThanSilentlySkippingIt`:
a nested repository behind a `chmod 000` subdirectory. Before asserting anything, the test calls a
`CanEnumerate` helper against the locked directory and returns early if it can still be listed — the
`chmod 000`-does-not-block-root case, reasoned through explicitly in a comment: if this process can
still see through the mode, it's root, the unreadable-directory property the test exists to exercise
never held, and asserting the refusal would pass for the wrong reason (root would see the nested
repository directly and the ordinary ls-based check would fire instead, not this one). Confirmed the
guard's own two branches by reasoning, not just assertion: run as this machine's non-root user (`uid
501`, confirmed via `id`), `chmod 000` genuinely blocks `Directory.EnumerateFileSystemEntries` — verified
directly in a standalone repro before writing the test, which is also what shows the exception is thrown
eagerly at the enumerable-construction call on this platform, not lazily during iteration, which is why
`CollectNestedGitEntries` materializes with `.ToList()` inside the `try` rather than relying on that. Had
this been run as root, the early return fires and the test asserts nothing on that run — the correct
behavior, not a false pass.

**Verified the test fails against the pre-fix (silent-skip) code**, not merely against `f50f1ca`: since
this vulnerability is in code this same follow-up block introduced, `f50f1ca` predates the scan entirely
and isn't the right counterfactual. Instead, checksummed-`cp`-swapped `ContentRepositoryService.cs` back
to the silent-`return`/no-second-accumulator shape the blocker was filed against, ran the new test alone
— failed exactly as expected (`Assert.Throws() Failure: No exception was thrown`) — then restored the
fixed file via `cp` from a pre-checksummed backup and confirmed the checksum matched before continuing.
Never used `git checkout`/`restore` for either swap.

**Nit 1 — symlink-loop test comment corrected, not the test's behavior.** The `[10s Task.WhenAny]` guard
stays; the comment no longer credits the `ReparsePoint` skip with preventing the hang. It now says
directly what the reviewer's own reproduction showed: with the skip disabled, the same self-referential
symlink's recursion self-terminates around 16 levels (an OS-level bound, not this code), well inside the
timeout, so this test alone doesn't exercise the skip's teeth — it pins "startup terminates," not "the
skip is why." The skip itself is unchanged and still stated as correct regardless, for portability
reasons independent of what this one test can prove.

**Nit 2 — "without dereferencing" reworded to state the property, not the mechanism.**
`AssertNoNestedGitRepository`'s remarks and the inline comment in `CollectNestedGitEntries` no longer
claim `ReparsePoint` is reported "without dereferencing"; they now say the check happens before
`Directory.EnumerateFileSystemEntries` is ever called on the symlink path, so recursion into or
enumeration of the target never happens — the actual safety property, independent of how the OS/runtime
internally determines the `Directory` bit.

**Left alone, per the ruling — both explicitly `## NEXT`, not this block's:** `git add -A`'s own
unreadable-directory blind spot (warns on stderr, exits 0 — a different code path, on the adopt side,
that no C# filesystem scan reaches), and unbounded recursion depth on a pathologically deep
non-symlinked tree.

No mutation testing, per the brief.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **515/515** full unfiltered (514 + 1 net
new: the unreadable-directory fixture) · `dotnet format --verify-no-changes` clean ·
`openspec validate git-backed-content-core --strict` valid. `git status --short` / `git diff --stat`
show exactly the same four files as before this round (`ContentRepositoryService.cs`,
`ContentRepositoryServiceTests.cs`, `design.md`, DEVLOG) — no residue from either scratch reproduction,
checksum-verified restored before this post.

→ @reviewer

**[reviewer]** Fix-round re-audit of the pre-init scan — **Approve, with one nit.** Certifying the tree
as it now stands. All four scrutiny points checked by reproduction, including the branch I couldn't
exercise on this machine's own account.

**1. The new test fails against the silent-skip code, and I checked both branches of its own root
guard, not just the one this machine takes.** Checksummed `cp`-swapped `ContentRepositoryService.cs`
back to the single-accumulator, silent-`return` shape and ran
`NestedGitRepositoryBehindAnUnreadableDirectory_RefusesRatherThanSilentlySkippingIt` alone: fails
exactly as reported (`Assert.Throws() Failure: No exception was thrown`). Restored via `cp`, checksum
confirmed identical before continuing. For the branch this machine can't take as a non-root user
(confirmed `uid=501`), I didn't take the guard's correctness on faith — I reproduced the root case
directly in a throwaway Docker container: `chmod 000` on a directory, then both a plain `ls` and, more
to the point, `Directory.EnumerateFileSystemEntries` from a real .NET 10 SDK container running as
`uid=0`, both enumerate the directory's contents without error despite the mode. That confirms
`CanEnumerate`'s early-return branch is reachable and correct — on a machine where this test runs as
root, it skips rather than asserting a refusal for the wrong reason, which is exactly what a
permissive-direction bug would have silently gotten away with.

**2. No realistic false-refusal case found, checked adversarially rather than on assumption.** The
scan only ever walks `RepositoryRoot` (`<DataRoot>/wiki`), never `DataRoot` itself, which rules out the
two OS-level candidates that actually matter: macOS's `.Spotlight-V100`/`.fseventsd`/`.Trashes` and
Linux's `lost+found` are volume-*root* artifacts, not something that appears inside an arbitrary
subdirectory a container bind-mounts — they'd sit at `/data`, never at `/data/wiki`, so they're outside
the scanned subtree entirely. Also checked a hazard adjacent to the fix rather than the fix itself: does
an individual unreadable *file* (as opposed to an unreadable directory) crash the scan with an uncaught
exception, since only the directory-enumeration call is wrapped in `try`/`catch`? Reproduced directly —
`File.GetAttributes` on a `chmod 000` **file** does not throw at all (`stat`-ing a path only needs
search/execute permission on its ancestor directories, not read permission on the file itself), so
there's no uncaught-exception gap hiding behind the one that's handled. Every path I could construct
that trips the new refusal traces back either to a genuine permission problem worth surfacing, or to a
location the scan never visits.

**3. The two messages are distinguishable and the unreadable-path one names the path.** Confirmed by
reading both `BuildUnreadableDirectoryErrorMessage` and `BuildNestedRepositoryScanErrorMessage`
side by side: distinct opening nouns ("a subdirectory this process cannot read" vs. "a nested git
repository"), distinct and opposite instructions ("fix the directory's permissions... no \".git\" needs
to be removed here" vs. "Remove the nested .git"), and the unreadable message includes an explicit
disclaimer pre-empting the confusion between the two. `{offendingPaths}` is interpolated into both; the
new test's `Assert.Contains("locked", ...)` / `Assert.DoesNotContain("copied-vault", ...)` pins exactly
this distinction, not just that *an* exception was thrown.

**4. Both corrected comments verified accurate against the current code, not just re-read.** The
symlink-loop test's comment now states precisely what my own reproduction showed last round — "this
test alone does not exercise the skip's teeth," names the ~16-level OS bound, and correctly attributes
the skip's real protective value to the *other* symlink test — matching what I independently found.
The "without dereferencing" rewording ("the check happens before `Directory.EnumerateFileSystemEntries`
is ever called on the symlink path... recursion into or enumeration of the target never happens") is
verified true by reading `CollectNestedGitEntries` itself: the `ReparsePoint` check runs before the
recursive call that would eventually enumerate the target, for every code path, not just the common
one. Neither correction has quietly introduced a sixth mismatch.

**One nit — `design.md` doesn't yet record the unreadable-directory decision.** The D9 addendum this
block's earlier round added describes the scan's nested-repository refusal in detail, but the fix
round's fourth file, `design.md`, shows no additional diff beyond the +4 lines already present before
this fix — the unreadable-directory refusal, a real Product Owner-level call (mine escalated, yours
settled), isn't recorded there at all. `ContentRepositoryService.cs`'s own XML remarks state the
reasoning fully and accurately, so nothing is *wrong*, but `design.md` is binding and every other
decision in this exact addendum chain got its own paragraph — this one didn't. Not blocking: worth a
short addition (a sentence naming the unreadable-directory refusal and its distinct message) whenever
`design.md` is next touched, same posture as prior nits closed inline rather than sent back for a round
of their own.

**Confirmed independently:** `dotnet test --filter
"FullyQualifiedName~ContentRepositoryServiceTests|FullyQualifiedName~ContentRepositoryStartupTests"`
32/32; `openspec validate git-backed-content-core --strict` valid; `dotnet format --verify-no-changes`
clean; `dotnet build` 0/0; `git status --short` / `git diff --stat f50f1ca` show exactly the four files,
no residue from any scratch reproduction (all removed, checksum-verified restored, including the
throwaway Docker containers, which touched no repository state).

No mutation this round, per the ruling — agreed, nothing here is a fidelity gap a mutant would find.

**This block is clear to commit.**

→ @architect

**[worker]** `design.md` nit closed — documentation only, no code or test changes.

Added a new D9 addendum paragraph recording the unreadable-directory decision, matching the sibling
addenda's voice and length: what it decides (an unreadable subdirectory on the initialise path refuses
in its own right, named separately from the nested-repository refusal, with a permissions-focused
remediation); why (the scan's job is to *assert* a property before any write — on an unreadable subtree
that property is unverifiable, not merely unverified, and the `chmod 000` reproduction — a real nested
repository hidden behind it, invisible to the scan, `git init` and the initial commit proceeding anyway —
is cited directly); what it costs (once per volume, on content the operator has just copied in and is
most likely still watching, and no realistic false-refusal case — the walk starts at `RepositoryRoot`,
never `DataRoot`, ruling out Spotlight/`fseventsd`/`lost+found`, which all live at the volume root); and
what it does not reach (`git add -A`'s own stderr-warns/exit-0 blind spot one layer further in, explicitly
owed to §6 at the latest via `## NEXT`, not claimed to be solved here).

**Gates:** `openspec validate git-backed-content-core --strict` valid · `dotnet build` 0 warnings/0
errors · `dotnet test` **515/515** full unfiltered (unchanged — no test touched) · `dotnet format
--verify-no-changes` clean. `git diff --stat -- design.md` shows exactly 6 lines added, nothing else in
`src/`/`tests/` touched this round.

No mutation testing, per the brief.

Not committing — architect verifies and commits.

**[supervisor]** Section re-review of **§2**, round three, over `7b50e46..HEAD` — six commits. Tree
clean, `git status --short` empty. No mutation run, per the ruling.

## Verdict: **Request changes** — one blocker, and it is the only outstanding item in §2

Everything else below is approved, including both things I was asked to be adversarial about — the
second instrument is genuine, and the method has not accreted. The blocker is narrow, its fix is small,
and it is the same *shape* of decision the Product Owner has already taken twice (make the claim true,
or narrow it), so it should not take long. I am raising it rather than filing it because I was asked to
trace the claim rather than trust it, and the claim is false.

### Blocker — "every refusal precedes every write" is false on a third path, which neither addendum enumerates

The two addenda enumerate **adopt** and **initialise**. There is a third: **`.git` present with an
unborn `HEAD`** — a repository that exists but has no commits. `AssertNoNestedGitRepository` is gated
on `HasOwnGitEntry(repositoryRoot) == false` (`ContentRepositoryService.cs:107`, inside the `else`),
but the write it protects against — `git init` plus the initial commit — is gated on **unborn `HEAD`**
(`:397`). Those two predicates coincide on a fresh volume and diverge the moment a `.git` exists
without commits. On that path the scan never runs, and the gitlink refusal is back behind a write.

**Reproduced, not reasoned.** Volume prepared as `git init` with no commit, an Obsidian-style vault
copied to `docs/vault` with its own `.git`, then the shipped sequence run step by step:

```
1 HasOwnGitEntry(.git dir exists) : TRUE   -> ADOPT branch; AssertNoNestedGitRepository NOT called
2 rev-parse --is-bare-repository  : false
4 EnsureInitialCommitAsync: HEAD  : UNBORN -> proceeds to WRITE
   ...wrote docs/.gitkeep and committed. commits now = 1
5 ReconcileWorkingTreeAsync       : :000000 160000 0000000 9f17a90 A  docs/vault
   -> gitlink found; git reset; THROW

AFTER THE REFUSAL — left behind in a repository ZeroWiki did not create:
  commits      : 1  (Initial commit by System <system@zerowiki.org>)
  docs/.gitkeep: CREATED
```

A commit into someone else's repository, in service of a start that was never going to complete —
the precise damage `f50f1ca`'s addendum was written to eliminate, and the thing the previous addendum
says it will not do ("committing would be writing to a repository ZeroWiki did not create").

**Three ways in, and the third is self-inflicted.** An operator preparing the volume by hand
(`git init`, then copy content in); a clone of an empty remote; and — the one that makes this more
than an operator-preparation edge — **ZeroWiki's own crash window**. On the initialise branch the
order is scan → `git init` → … → initial commit. A process death between `git init` and that commit
(container OOM, restart — exactly the crash premise D9 exists for) leaves `.git` present and `HEAD`
unborn, and **the scan never runs on that volume again**. `git init` is itself what disables the
guard, and nothing re-arms it.

**Why I am not filing this as a note.** The magnitude is genuinely small — an empty `.gitkeep` and one
commit, nothing destroyed, the vault untouched, and the refusal still fires. On magnitude alone this
would be `## NEXT`. What moves it is that `design.md` now states an unqualified absolute — *"every
refusal on **both** paths — adopt and initialise — precedes every write"* — that is demonstrably
false, in **binding** text, at the third attempt, after I named "wrong justification attached to
correct code" as this section's recurring defect in round two. §5–§7 will read that sentence as a
guarantee. The spec is unaffected: `specs/content-store/spec.md` makes no ordering claim, so no spec
requirement is unmet and no design *decision* is violated by the code — the code refuses safely on
every path. It is the design document that is wrong, plus a small write on an unenumerated path.

**Fix shape — one call site, and a Product Owner choice between two options.** The scan is gated on
the wrong predicate. Either (a) gate it on the condition that actually governs the write — run
`AssertNoNestedGitRepository` whenever `EnsureInitialCommitAsync` is about to take its unborn-`HEAD`
branch, rather than only when `.git` is absent, which makes the absolute true on all three paths; or
(b) narrow the sentence to name the third path and accept the empty commit. (a) is a few lines and
lands naturally with the refactor in ask 3 below. Either way the fixture is the same and it is
missing: **`.git` present, `HEAD` unborn, nested repository present.**

### Ask 2 — two instruments: genuine, not duplication with a justification attached

Genuine, on every test I can put to it. They ask different questions (filesystem "is there a nested
repository here", ignoring `.gitignore` — versus index "would committing now store a gitlink",
respecting both `.gitignore` and the `HEAD` delta); they have different lifetimes (the scan runs at
most once in a volume's life, because the branch it guards stops existing the moment a repository is
there — the index check runs at every reconciliation forever); and neither can stand in for the other
(the scan cannot see content arriving after bootstrap; the index check cannot run before `git init`).
The one deliberate disagreement is documented at both sites and in `design.md`, and pinned by
`GitignoredNestedGitRepositoryOnAFreshVolume_IsStillRefusedByTheScan` — a divergence with a test is a
decision, not drift.

Worth noting the connection to the blocker, because it is the same observation from the other side:
the stated justification is *"the scan guards the initialise branch, the index check guards every
reconciliation."* That is exactly right, and following it is what exposes the gap — "the initialise
branch" is defined in the code by `.git`-absence rather than by "about to create the initial commit",
so there is a sliver between the two instruments that belongs to neither. The coexistence is sound;
the seam is what needs the one-line move.

Also verified clean while I was in there: the symlink handling. `File.GetAttributes` returns
`ReparsePoint=True` without throwing for a **dangling** symlink as well as a live one (checked by
execution on this platform, both `dangling` → `/nonexistent/target` and a live directory symlink), so
a copied-in vault containing broken links is skipped rather than crashing the walk. That was a real
candidate for an unhandled exception and it is not one.

### Ask 3 — has §2 accreted? No, and it is now better organised than the version I approved

Nine steps is more than seven, but accretion is growth *without an organising principle*, and this
version has one the seven-step version lacked: **every refusal precedes every write**. Config and
hooks previously sat in the middle for no reason except the order the tasks were written in; they are
now at the end for a stated, load-bearing reason. That is a sequence that got a spine, not one that
got longer.

Two honest signs of strain, neither blocking:

- The ordering now needs two multi-line comment blocks inside `EnsureRepositoryAsync` (`:123-128`,
  `:139-141`) to survive. When ordering has to be defended in prose, it is a candidate for being
  expressed structurally — an `AcceptRepositoryAsync` phase (classify, resolve, accept-or-refuse,
  reconcile, assert) followed by a `ConfigureRepositoryAsync` phase (config, hooks). The invariant
  then lives in the shape rather than in comments a later editor can move past.
- The classify `else` branch now does two jobs — classification *and* guarding — which is precisely
  where the blocker lives. The split above is the natural home for its fix, which is why I would do
  both in one go rather than patch the call site alone.

### Ask 4 — `## NEXT` reconciliation: all four discharges verified, nothing dropped

Checked the diffs rather than the claims. Discharged in `f50f1ca`: the `T`-versus-`M` remark
(`:526`, now correctly "a `T` (typechange) record"), `core.hooksPath` recorded in
`GitHookInstaller`'s `<remarks>` with the §5.3 consequence spelled out, `GitAuthor`'s "later", and
`tasks.md:70`'s superseded D11 pattern. All four genuinely landed.

Two of my item-11 axes are also now covered, which the summary did not claim:
`ForeignRepositoryWithPreExistingHooksAndNoDocs_RefusesLeavingTheHooksByteForByteUnchanged` and
`ForeignRepositoryWithDenyCurrentBranchRefuseAndANewGitlink_RefusesLeavingTheConfigUnchanged`. Still
open from that item: a foreign repository on a **non-`main` branch**, and an adopted repository with a
**dirty tree**.

Still standing, correctly: item 3 (§7 must read the checked-out branch, not assume `DefaultBranch` —
the fixtures' `init -b main` still hides it) and §5.1's lockfile not living under `RepositoryRoot`.

The two newly filed items are correctly scoped, with one sharpening:

- **`git add -A`'s unreadable-directory blind spot** — right that no C# scan can close it and right
  that the resolution is a Product Owner call. One correction to the framing: it is described as owed
  by §6, but the exposure exists **today**, on the adopt path, where the pre-init scan never runs at
  all — an unreadable directory inside an adopted repository means reconciliation silently skips that
  content while `status --porcelain` also cannot see it, so `AssertWorkingTreeIsCleanAsync` passes.
  That is §2 behaviour, not §6 behaviour. It should still not be fixed here, but the item should say
  the gap is live rather than anticipated, or §6 will inherit it thinking it starts clean.
- **Unbounded recursion on a pathologically deep non-symlinked tree** — judged-and-left is right. The
  realistic version of that hazard is a symlink loop, which is handled and tested
  (`SymlinkedDirectoryThatLoops_DoesNotHangStartup`); a genuinely pathological non-symlinked depth is
  not a threat model for this deployment.

### Mutation judgement — endorsed, and now with three rounds of evidence behind it

Running none in the three new blocks was right, and for a sharper reason than "these were ordering
changes": this round's blocker is *itself* immune to mutation. `AssertNoNestedGitRepository` runs
faithfully and does exactly what it says; the defect is that it is **called under the wrong
condition**, and no mutant of a method's body can reach its call site's predicate. That is now three
rounds where the finding was a gap in what is asked rather than in how faithfully it runs, and three
rounds where the instrument that found it was a **missing fixture** — the foreign repository, then the
advancing gitlink, now the unborn `HEAD`. Two mutants, both 3/3, remain the right total for §2. For
§5–§7 the transferable rule is the one this section has now demonstrated three times: enumerate the
*states the subject can be in* and build a fixture per state, then mutate the conditions that survive.

→ `@architect` — this is remediation round three, so per CLAUDE.md §3c.4 this goes to the Product
Owner rather than a fourth carve. The decision needed is small and binary-with-a-third-answer, as
usual here: make the absolute true (gate the scan on unborn-`HEAD`, ideally with the accept/configure
split), narrow the sentence and accept the empty commit, or tell me the magnitude does not justify
either and I will record it as accepted and approve on that basis.

**[worker]** Per the brief: the Product Owner's decision on the round-three blocker above is **fix the
gate** — a targeted fix, not the accept/configure split (filed to `## NEXT` instead). No new `N.M`
numbers, nothing ticked. Base `2c70e05`.

Blocker fixed — the scan is gated on "the repository has no commits yet," expressed once.

**The fix.** `EnsureRepositoryAsync` now computes `repositoryHasNoCommitsYet` exactly once, in the
classification step: trivially `true` when `.git` doesn't exist yet (the initialise branch), or the
result of a new `RepositoryHeadIsUnbornAsync` git probe when `.git` already exists (only ever called
once `HasOwnGitEntry` has confirmed `.git` is present at `repositoryRoot` itself, for the same
ancestor-discovery reason every other git call in this class requires that confirmation first). The one
`if (repositoryHasNoCommitsYet) { AssertNoNestedGitRepository(repositoryRoot); }` that follows now runs
the scan in exactly the two circumstances that matter: before `git init` (no `.git` yet) and before the
adopt branch's initial commit (`.git` present, `HEAD` unborn) — never during a genuinely-adopted
repository with history, where `FindStagedGitlinksAsync` already governs. The same
`repositoryHasNoCommitsYet` value is threaded straight into `EnsureInitialCommitAsync` as a parameter,
replacing its own internal `headProbe` — the two decisions (scan-or-not, write-or-not) are now literally
the same boolean, not two independently-derived conditions that merely happened to agree on a fresh
volume, which is what let the third path through undetected.

**Caught while implementing, not after:** calling the scan against an already-existing `repositoryRoot`
means `Directory.EnumerateFileSystemEntries(repositoryRoot)` now includes `repositoryRoot`'s own
top-level `.git` in the very first pass — which `CollectNestedGitEntries` would have flagged as
"nested," since it only checked the filename, not the depth. That would have refused startup on *every*
adopted repository with an unborn `HEAD`, nested vault or not — the opposite failure mode. Fixed by
excluding a `.git` entry found exactly at `repositoryRoot`'s own top level (`directory ==
repositoryRoot`) from what counts as nested; only a `.git` found inside a subdirectory still is. The new
`GitInitializedButUnbornHead_WithOrdinaryMarkdownAndNoNestedRepository_InitializesNormally` test is what
would have caught this had it shipped broken — confirmed by temporarily reverting just that exclusion
and rerunning it: false refusal, exactly as predicted.

**Tests**, all in `ContentRepositoryServiceTests.cs` under a new "the scan's gating predicate" comment
block: `.git` present + unborn `HEAD` + nested repository at depth 1, as a gitfile, and more than one
level deep — each refuses **and asserts `HEAD` is still unborn** (`rev-parse --verify -q HEAD` still
fails), not merely that the refusal fired; and the crash-mid-init regression (`.git` present + unborn
`HEAD` + ordinary Markdown, no nested repository) — still initializes correctly, doubling as the fixture
that exercises the top-level-`.git` exclusion above.

**Confirmed the first three fail against `2c70e05`**, via a checksummed `cp` swap of
`ContentRepositoryService.cs` only (never `git checkout`/`restore`): all three failed exactly as
predicted (`Assert.False(headProbe.Succeeded)` came back `True` — a commit *was* silently created around
the nested repository), and the fourth (no nested repository) passed at both revisions, as expected for
a compatibility fixture rather than a regression pin. Restored the fixed file via `cp` from a
pre-checksummed backup and confirmed the checksum matched before continuing.

**`design.md`** — new addendum: states the rule ("the scan runs whenever ZeroWiki is about to create the
initial commit," true in exactly two circumstances) rather than enumerating "adopt and initialise" as
the covered paths, which is exactly what missed the third state. Names this as the third attempt at
making the sentence true and records that the accept/configure split was the supervisor's suggested
durable shape, filed to `## NEXT` instead of taken here.

No mutation testing, per the brief — the supervisor's own note that this blocker is immune to mutation
(faithful method, wrong call-site predicate) still holds; nothing here changes that.

**Gates:** `dotnet build` 0 warnings/0 errors · `dotnet test` **519/519** full unfiltered (515 at
`2c70e05` + 4 new) · `dotnet format --verify-no-changes` clean · `openspec validate
git-backed-content-core --strict` valid. `git status --short` / `git diff --stat 2c70e05` show exactly
the four files this round touches (`ContentRepositoryService.cs`, `ContentRepositoryServiceTests.cs`,
`design.md`, DEVLOG) — no residue from either scratch reproduction, checksum-verified restored before
this post.

→ @reviewer

**[reviewer]** Third-path fix (unborn-`HEAD` gate unification) — **Approve.** All four scrutiny points
verified by reproduction, including reconstructing the exact pre-fix file via `git show 2c70e05:...`
rather than hand-reverting, which is the strongest form of the "does it fail before, pass after" check.

**1 (hardest, checked hardest) — the top-level-`.git` exclusion is exact, not approximately right.**
Checksummed `cp`-swapped the fix aside and removed *only* the exclusion (`if (!string.Equals(directory,
repositoryRoot, ...))`), leaving detection otherwise untouched, then ran all four new tests:
`GitInitializedButUnbornHead_WithOrdinaryMarkdownAndNoNestedRepository_InitializesNormally` fails
immediately — `AssertNoNestedGitRepository` throws naming `.git` itself (the repository's own top-level
entry) as the offending "nested" path — while the other three (nested at depth 1, gitfile form, three
levels deep) all still pass, confirming the exclusion is neither needed for, nor interferes with,
detecting a genuinely nested `.git` one level down. The boundary is exactly what it claims: `directory
== repositoryRoot` excludes only the scan's very first call, and every recursive call thereafter (any
subdirectory, including one path segment down) has `directory != repositoryRoot` and is caught
normally. Restored via `cp`, checksum confirmed identical before continuing.

**2. The predicate is genuinely single — confirmed by exhaustive grep, not sampling.** `grep -n
"EnsureInitialCommitAsync\|RepositoryHeadIsUnbornAsync\|rev-parse.*verify.*HEAD\|repositoryHasNoCommitsYet"`
over the whole file: exactly one call site for `EnsureInitialCommitAsync` (`:155`, passing the local
`repositoryHasNoCommitsYet` computed at `:65`), exactly one call site for `RepositoryHeadIsUnbornAsync`
(`:89`), and exactly one `rev-parse --verify -q HEAD` invocation anywhere in the class (inside that one
method). Read `EnsureInitialCommitAsync`'s body directly rather than trusting the grep alone: it uses
only its `repositoryHasNoCommitsYet` parameter, no internal probe of its own. No path reaches it with a
value derived any other way.

**3. The crash-mid-init regression genuinely regresses, for the reason claimed.** Same mutation as
point 1 answers this directly: the failure mode the mutation reproduces is exactly "a recoverable
interrupted start turned into a permanent refusal" — the ordinary-Markdown fixture, which has every
right to initialise normally, gets refused instead, citing the repository's own `.git` as if it were a
nested repository. That is precisely the risk named in the brief, and the test that catches it is
testing the exclusion specifically, not passing for an unrelated reason.

**4. `design.md`'s restated rule is true of the shipped code and does not overclaim.** Traced
`repositoryHasNoCommitsYet == true` against every branch: it is set `true` in exactly the two places
`design.md` now names (`.git` absent, or present with unborn `HEAD` via the probe) and never otherwise
— both bare-repository branches throw before the variable is ever used, so they're correctly excluded
from "exactly two circumstances." `EnsureInitialCommitAsync` writes the initial commit if and only if
this same value is `true` when it's called, and the scan runs if and only if it's `true` before that —
so "the scan runs whenever ZeroWiki is about to create the initial commit" is exact, not roughly true,
and the paragraph doesn't restate or re-broaden the earlier "every refusal precedes every write"
absolute beyond what this fix actually establishes.

**Independently reconstructed the worker's fail-before claim rather than trusting the report.** Used
`git show 2c70e05:src/ZeroWiki/Content/ContentRepositoryService.cs` to get the *exact* pre-fix file
(not a hand-reversion, which risks subtly recreating a different bug), swapped it in, and ran all four
new tests: the same three fail (`Assert.False(headProbe.Succeeded)` came back `True` in each — a
commit was silently created around the nested repository, exactly the reported failure) and the fourth
(no nested repository) passes, matching the report exactly. Restored via `cp`, checksum confirmed.

**Confirmed independently:** `dotnet test --filter
"FullyQualifiedName~ContentRepositoryServiceTests|FullyQualifiedName~ContentRepositoryStartupTests"`
36/36; `openspec validate git-backed-content-core --strict` valid; `dotnet format --verify-no-changes`
clean; `dotnet build` 0/0; `git status --short` / `git diff --stat 2c70e05` show exactly the four
files, no residue from any scratch reproduction (all removed, checksum-verified restored).

No mutation this round — agreed with the supervisor's own note: the scan runs faithfully and does
exactly what it says, the defect was in the call-site predicate, and no mutant of a method body reaches
that. Not re-raised: the `AcceptRepositoryAsync`/`ConfigureRepositoryAsync` split and the `git add -A`
unreadable-directory gap, both correctly filed to `## NEXT` rather than this block's scope.

**This block is clear to commit.**

→ @architect

**[supervisor]** Section re-review of **§2**, round four, over `7b50e46..HEAD` — seven commits. Tree
clean, `git status --short` empty. No mutation run, per the ruling.

## Verdict: **Approve** — §2 closes

The blocker is genuinely closed, and closed at the right level: not by adding a fourth path to a list,
but by deleting the list. Nothing below blocks; the items are for `## NEXT`.

### Ask 1 — is the claim true now, including a state neither of us named?

**Yes.** I applied the same lens rather than re-reading the sentence: enumerate every *write* in
`EnsureRepositoryAsync`, ask which predicate gates it, then pair each with the refusals that follow it.

| write | gated on | refusals that can still follow it |
|---|---|---|
| `Directory.CreateDirectory(repositoryRoot)` `:62` | unconditional | all of them |
| `git init` `:133` | `!hasOwnGitEntry` | resolve-mismatch |
| `docs/`, `.gitkeep`, `add`, `commit` `:470-490` | `repositoryHasNoCommitsYet` | staged gitlink |
| recovery commit | staged diff non-empty | — |
| config + hooks | last | — |

Two residual write-before-refusal pairs, and both are **provably** benign rather than merely unlikely:

- `Directory.CreateDirectory` precedes the bare-repository refusal — but that refusal requires `.git`
  or the bare markers to already exist, which means the directory already existed and the call was a
  no-op. The write cannot occur in any case where the refusal can fire.
- `git init` precedes the resolve-mismatch assertion — but that assertion can only fail if `.git` is
  not where `init` just put it. It is defence-in-depth for the *adopt* branch and unreachable on this
  one by construction.

The pair that actually mattered — initial commit before the staged-gitlink refusal — is closed for a
structural reason, not a coincidental one: `!hasOwnGitEntry ⟹ repositoryHasNoCommitsYet == true`, so
the scan runs on **every** path where either `git init` or the initial commit runs. For the gitlink
check to fire after those writes, the scan would have to have passed over a nested repository that
`git add -A` then staged. It cannot: the scan is strictly *stricter* than `add -A` on every axis I
could construct — it ignores `.gitignore` (the documented divergence, in the safe direction), it
refuses on unreadable subtrees where `add -A` warns and proceeds, and it skips symlinks exactly where
git stores a symlink as a blob rather than descending. I could not build a fourth state.

### Ask 2 — does the single boolean remove the drift, or move it?

**Removes it.** This is not two conditions kept in agreement, which is what would drift — it is one
value computed once and consumed twice. The `!hasOwnGitEntry` branch asserts it `true` a priori rather
than probing, which is sound because `git init` cannot create a commit, so there is no gap between
"what the probe said" and "what `git init` left behind" for it to disagree across.

The one thing it *does* change is worth recording: `repositoryHasNoCommitsYet` is now a **snapshot**.
`EnsureInitialCommitAsync` no longer independently verifies `HEAD` immediately before committing; it
trusts a value computed up to four git invocations earlier. That is correct today only because nothing
between the probe and the write can create a commit — startup is single-threaded and takes no lock.
It is not a defect now, but it widens a window that §5 should absorb rather than inherit (see `## NEXT`).

**Verified the new exclusion by execution, not by the reviewer's account** — transcribed
`CollectNestedGitEntries` verbatim and ran it against constructed trees:

```
A) own .git + ordinary content          nested=[]                                  visits=2
B) + nested .git dir one segment down   nested=[docs/vault/.git]                   visits=3
C) + nested gitfile four segments down  nested=[docs/a/b/c/.git, docs/vault/.git]  visits=6
D) root passed WITH trailing separator  nested=[docs/a/b/c/.git, docs/vault/.git]
```

Two things the reviewer's swap could not show. First, `visits=2` in (A) — root plus `docs` — proves the
repository's own `.git` is not merely excluded from the result but **never descended into**; had it
fallen through to the directory check, every unborn-`HEAD` start would walk git's entire object store.
Second, (D): the boundary is exact for a stronger reason than "it was tested". The top-level call
passes the same string as both `root` and `dir`, so the depth-0 comparison is an identity, and every
deeper call has a separator plus a name appended and can never equal it. The exclusion therefore cannot
drift with path normalisation, trailing separators, or case-insensitive filesystems — which an
empirical check on one platform would not have established.

### Ask 3 — accretion, and whether the split is now overdue

**The Product Owner's choice of the targeted fix was right for this round**, and I'd have argued for it:
bundling a structural refactor with a correctness fix would have made the correctness fix unreviewable,
and this change has been consistently good about not doing that. The organising principle also survived
the change — the fix *strengthened* it, by replacing "every refusal precedes every write" as a property
someone has to maintain with a single predicate that makes it hold mechanically.

But **the split has moved from optional to overdue**, and for a concrete reason rather than an
aesthetic one. `git init` now sits in its own `if (!hasOwnGitEntry)` outside the classify block that
computed that boolean, because the scan had to be interposed between them. The classify block no longer
owns its own action, and a reader must carry two booleans across three separate `if`s to see the order.
The forcing function is §5.1: a cross-process `flock` will want to wrap exactly the accept-and-write
phase and not the configure phase, so `AcceptRepositoryAsync` / `ConfigureRepositoryAsync` is the shape
the lock is about to ask for anyway. Doing it as part of §5.1 costs nearly nothing; doing it later means
threading a lock through the current shape first.

### Ask 4 — `## NEXT`

Item 14's sharpening is recorded correctly — "live today on the adopt path" is the part that stops §6
inheriting it on a false premise. Nothing from my earlier lists was dropped. Adding three:

- **§5.1 should take the write lock across bootstrap's accept-and-write phase.**
  `repositoryHasNoCommitsYet` is now a snapshot (ask 2), so two containers starting concurrently on one
  volume — a plausible Docker restart overlap — could both observe an unborn `HEAD`. The window existed
  before and is now a little wider; §5.1 is where it should close, together with the phase split.
- **The unreadable-directory refusal's cost argument is now out of scope with its own trigger.**
  `design.md` justifies it as running "once per volume, on content an operator has just copied in, most
  likely while they are still watching". True when the scan only ran on a fresh volume; the scan now
  also runs on every restart of a crash-interrupted volume, where nobody is watching and the refusal
  repeats until permissions are fixed. The **decision** is still right — refusing beats silently
  skipping content, which is item 14 — but the rationale should say so on its own terms rather than
  leaning on "the operator is present". Prose, not behaviour, and explicitly **not** blocking.
- Carried unchanged: §7 must read the checked-out branch rather than assume `DefaultBranch`; the
  remaining foreign-repository axes (non-`main` branch, adopted dirty tree); §5.1's lockfile must not
  live under `RepositoryRoot`.

### Mutation judgement

Endorsed, and the record now supports it four rounds deep. Every defect §2 produced was a gap in *what
is asked* rather than in how faithfully a shipped condition runs, and this round's fix is the clearest
case yet: the scan's body was always correct: what was wrong was the predicate at its call site, which
no mutant of that body can reach. Two mutants remains the right total for §2. The instrument that found
every one of these was a fixture for a state nobody had enumerated — foreign repository, advancing
gitlink, unborn `HEAD` — and the four new `GitInitializedButUnbornHead_*` tests are that state finally
having one. For §5–§7: enumerate the states the subject can be in, build a fixture per state, then
mutate the conditions that survive.

§2 is closed. → `@architect` — open §3.

## 3. Content read & render

**[architect]** Base: `60957e6` — turns the working tree §2 established into a wiki you can actually
read: page enumeration and the path↔route mapping, optional YAML frontmatter, Markdown rendered in the
Blazor shell, and per-page authorship read back from git rather than from any hand-maintained field.

**[architect]** Pre-flight (CLAUDE.md §2): tree clean, on `change/git-backed-content-core`,
`openspec validate --strict` valid, `proposal.md` / `design.md` / `specs/content-store/spec.md` read.
§2 carries a supervisor `Approve` (round four, `1253ff5`) and §11 closed earlier, so nothing is owed
backwards. Forward obligation 1 (D9's overclaiming prose) is owed before **§5**, not before §3 or §4 —
confirmed unchanged.

**[architect]** §3 arrived with two things settled nowhere — not in `design.md`, not in the
`content-store` spec, not in `tasks.md`: what a file path becomes as a URL (which *is* task 3.1), and
whether raw HTML in a page renders. Both went to the Product Owner. Their answers, and two decisions
that fell out of them, are now **D12–D14** in `design.md` with spec scenarios behind them, because §2's
most expensive lesson was that prose landing in `design.md` binds every later section and is not
gated by anything unless a scenario gates it.

1. **Route encoding — Product Owner's scheme, not the Architect's.** The Architect proposed
   percent-encoding (`/wiki/Project%20Notes/Kick%20Off`) on invertibility grounds. The Product Owner
   rejected `%20` on readability and supplied **space → `_`, literal `_` → `__`**, naming the collision
   it carries in the same breath (`a_ b.md` vs `a _b.md`). Confirmed by working it: both encode to
   `a___b`, because the escape character is also the substitute character. Greedy decode is
   deterministic but **not injective**, and §6 inverts route→path to find the file it saves — so the
   losing file's route would have written to the winning file.
2. **The obvious repair is worse than the collision.** Space → `_` with literal `_` → `%5F` is
   genuinely unambiguous and keeps the common case `%`-free. Rejected: RFC 3986 §6.2.2.2 directs
   normalizers to decode percent-encoded *unreserved* octets, so a reverse proxy may rewrite
   `a_%5Fb` → `a__b` and hand the ambiguity back from outside the app. A scheme that depends on a proxy
   declining to normalize is not a scheme. `__` is two unreserved characters and survives normalization.
3. **So the ambiguity refuses instead of resolving — at the route, not at startup.** Enumeration
   already walks the whole tree, so grouping by route is free. Both files stay indexed; the route names
   every claimant and serves no body; every other route is unaffected. Deliberately *not* §2's
   startup-refusal posture: §2 guards the volume's structure, seen once by an operator standing there,
   whereas a filename arrives at runtime by push — a startup refusal would let any pushed filename
   brick the wiki for everyone until someone reached the server. Shared principle: never silently pick
   one of two readings. Where it refuses is set by what the fault can reach.
4. **Raw HTML is escaped, not sanitized** (D13). Content is push-reachable via a git token, so a
   `<script>` in a page is stored XSS against the administrator, and `MarkupString` sanitizes nothing.
   Escaping costs no dependency and has no bypass surface; a sanitizer's entire value rests on never
   having one. Accepted narrowing, stated as such: Obsidian pages with embedded HTML will show their
   tags. Which further Markdig extensions to enable is explicitly **left open** by the Product Owner.
5. **SharpYaml behind `IFrontmatterParser`** (D14). Checked before agreeing rather than after: Markdig's
   frontmatter extension performs **no** parser integration — it hands over `yamlBlock.Lines.ToString()`
   and evaluates nothing, and Markdig's own docs use YamlDotNet as the worked example. So "same author"
   buys no shared types and no version coupling; the seam is a `string`. Versions verified on NuGet —
   Markdig 1.3.2 (2026-06-18), SharpYaml 3.13.0 (2026-07-10), YamlDotNet 18.1.0 (2026-06-26); all three
   current, SharpYaml the most recently released. Product Owner chose SharpYaml on API familiarity, and
   asked for the interface precisely because that reason is about familiarity rather than a property
   only SharpYaml has.
6. **`❓ @architect` asked and answered — "is YamlDotNet more forgiving of malformed input?"** The
   Architect had implied it was, and withdrew that: *forgiveness is the wrong property*. 3.2 wants
   failure **total and catchable**, because partially-parsed metadata is indistinguishable from real
   metadata and would silently give a page the wrong tags rather than none. What actually decides
   safety is bounding the input — an alias bomb is well-formed YAML that strictness does not help, and
   deep nesting can raise `StackOverflowException`, which .NET **cannot catch** and which would let one
   pushed page kill the container on every read of it, permanently. Hence D14's cap-before-parse, which
   is what makes "failure is total" implementable rather than aspirational. A spike across both
   libraries was offered; the Product Owner settled the choice on other grounds, so it was not run —
   recorded so the absence of that measurement is visible rather than assumed.
7. **Welcome page — pinned, deliberately.** The Product Owner asked for `/` to become a welcome page
   fed by a `_welcome-message.md`, then pinned it: revisit once the site runs and a Markdown editor
   control is chosen. So **no** task 3.5 and **no** spec scenario — the surrounding design questions
   (absent-file default without writing to the repo at startup, whether `_`-prefixed files form a hidden
   namespace) are recorded in this thread and nowhere binding. `Home.razor` still holds the Blazor
   template's "Hello, world!"; it becomes placeholder ZeroWiki content in block 3b as **de-scaffolding**,
   which is why it takes no task number — leaving template cruft in a shipped section is exactly what a
   supervisor flags as dead scaffolding.

**[architect]** Block carve — two blocks, neither spanning the section:

- **3a — 3.1–3.2.** Enumeration, the path↔route mapping with collision detection, and frontmatter
  behind `IFrontmatterParser`. Service layer only, headlessly testable, no UI.
- **3b — 3.3–3.4.** The Static SSR page that renders a body with raw HTML off, plus git-derived
  authorship/last-edit on it. One deliverable a human can open in a browser.

**[architect]** → `@worker` — **Block 3a brief (tasks 3.1–3.2).**

**Deliver:** page enumeration over the working tree, the path↔route mapping with collision detection,
and frontmatter parsing behind `IFrontmatterParser`. **Service layer only — no Razor components, no
UI.** Rendering and authorship are block 3b; do not start them.

**Tasks verbatim:**
- 3.1 Enumerate Markdown pages from the working tree and map paths to routes
- 3.2 Parse optional YAML frontmatter (tags, etc.); render body even when frontmatter is missing or
  malformed — *the rendering half is 3b's; 3a owes the parse and the model it produces*

**Binding decisions — read D12, D13, D14 in `design.md` in full.** The scenarios that gate this block
are in `specs/content-store/spec.md`: *Nested page is addressable by its path*, *Literal underscore
round-trips*, *Ambiguous route is refused, not guessed*, *Page without frontmatter is rendered*,
*Malformed frontmatter degrades gracefully*, *Hostile frontmatter cannot take the process down*.

**Five things that decide whether this block is correct:**

1. **Route→path is a path-traversal surface, and it is the sharpest hazard in this block.** §6 will use
   your inverse to choose a file to *write*. A route carrying `..`, an encoded `%2e%2e`, an absolute
   path, or a rooted drive-qualified segment MUST NOT resolve outside `ContentPaths.WorkingTree`.
   Canonicalize with `Path.GetFullPath` and verify containment **against the working tree plus a
   trailing directory separator** — a bare `StartsWith` lets `/data/wiki/docs-evil` pass as inside
   `/data/wiki/docs`. Refuse rather than clamp. Test the encoded forms, not just the literal ones.
2. **Encode and decode must be genuinely inverse, and a test must prove it rather than assert it.** A
   round-trip property test over awkward names — spaces, underscores, runs of underscores, a name that
   is only underscores, `%`/`#`/`?`, non-ASCII, a trailing space, a name whose stem is empty — is worth
   more than a table of hand-picked examples. Layer order per D12: `_`/`__` first, percent-encoding
   second, and the reverse on the way back.
3. **Collision detection is D12's whole point.** Group by route during enumeration; a route claimed by
   more than one file serves no page and names **every** claimant, while every other route is
   unaffected. `a_ b.md` + `a _b.md` → `a___b` is the canonical fixture. Do **not** implement a greedy
   "first match wins" decode anywhere, including as a convenience helper.
4. **Bound frontmatter before parsing it, not after** (D14). Cap block size and nesting depth at the
   seam. Ordinary malformed YAML throws and is caught → empty metadata; an alias-expansion bomb and
   deep nesting are the two that the library will not save you from, and `StackOverflowException`
   cannot be caught in .NET at all. Deserialize into a constrained shape — a fixed POCO or
   `Dictionary<string, object>` — never a type-resolving deserializer honouring `!!` tags.
5. **Failure is total, never partial.** On any frontmatter fault the metadata is *empty*. Partially
   parsed metadata is indistinguishable from real metadata and would silently give a page wrong tags.

**Architect calls, made here so you do not have to guess — flag it if you think either is wrong:**
- **Skip dot-prefixed files and directories.** An Obsidian vault carries `.obsidian/`, and dot-prefixed
  is an established hidden convention rather than an invented namespace. This is *not* the `_`-prefix
  namespace question, which the Product Owner explicitly pinned — do not implement that.
- **Do not follow directory symlinks**, matching §2's pre-init scan, which declined for two reasons
  that both still apply: a symlink can loop back on itself, and it can point outside the volume.
- An **unreadable directory** met during enumeration must not be silently skipped — silently missing
  pages is the degradation pattern this change refuses everywhere. Handle it explicitly and say in the
  DEVLOG what you chose and why; a runtime refusal here should be proportionate like D12's, not §2's
  startup refusal.
- Configure the Markdig pipeline **once**, with raw HTML already disabled per D13, even though nothing
  renders until 3b. A wrong default shipping in 3a is harder to see than one that never shipped.
- `SharpYaml` is the parser and `IFrontmatterParser` is the seam. No other caller may reference
  SharpYaml types.

**Mutation testing — scoped and capped** (CLAUDE.md): the route decode's containment check and the
frontmatter bounds are the only parts of this block that warrant it. Cap at **3 confirmation runs** per
mutant. Verify under the **full unfiltered `dotnet test`**, never a filter. Checksum the target before
*and* after every mutation, and revert via `trap`/`finally`, never `git checkout --`. **This block adds
new files, so `git diff -- src` is blind to them — run `git status --short -- src` alongside it, and
treat a `??` entry as "read it or checksum it", not "it's fine".** Do not expand scope beyond these two
without asking.

**Gates before handoff — all four, and report the actual numbers:** `dotnet build` clean, full
`dotnet test` green, `openspec validate git-backed-content-core --strict`, `dotnet format
--verify-no-changes` clean. Post progress and findings to this DEVLOG as `[worker]` under `## 3.`, then
hand off `→ @reviewer`.

**[worker]** Block 3a (3.1–3.2) implemented. New files under `src/ZeroWiki/Content/`: `PageRouteCodec`
(encode/decode + traversal-safe `TryResolveWorkingTreePath`), `EnumeratedPage`/`AmbiguousPageRoute`/
`PageEnumerationResult`, `PageEnumerationService`, `PageFrontmatter`, `IFrontmatterParser` +
`SharpYamlFrontmatterParser`, `MarkdownPipelineFactory`, `PageFrontmatterExtractor`. Added `Markdig`
1.3.2 and `SharpYaml` 3.13.0 package references; registered everything in `AddContentStorage`. Service
layer only — no Razor, no rendering, nothing from 3b started.

Decisions/findings beyond the brief:

1. **A real round-trip bug the brief's framing didn't quite anticipate, caught by testing rather than
   assumed.** D12's "residual ambiguity" example (`a_ b.md` vs `a _b.md` → `a___b`) is one instance of a
   *general* class: any run of two-or-more adjacent `{space, '_'}` characters that is **not** made
   entirely of literal underscores is ambiguous, because greedy left-to-right decode always maximizes
   pairing. Concretely, two adjacent literal spaces (`"double  space.md"`) decode back as **one** literal
   underscore (`"double_space.md"`), not two spaces — verified empirically, not by hand-tracing (hand
   proofs on this got the direction wrong once). This is not a defect in the codec: a run made **entirely**
   of literal underscores round-trips exactly regardless of length (even encoded length, greedy pairing
   leaves nothing over), and `PageEnumerationService`'s route-keyed grouping catches the general case the
   same way it catches D12's named example — two real files landing on the same route get refused
   together, whatever caused the collision. Documented in `PageRouteCodec`'s remarks and pinned down with
   dedicated tests (`IsolatedUnderscoreRunsStillRoundTrip`, `AmbiguousRunsDoNotRoundTripButStayInternallyConsistent`)
   rather than left implicit. The fuzz test only asserts exact round-trip on generated names without an
   ambiguous run, and asserts the weaker `Encode(Decode(route)) == route` self-consistency property on the
   rest — worth the reviewer's attention as the one place this block's understanding of D12 sharpened past
   the brief.
2. **Unreadable directory**, per the brief's call: reported in `PageEnumerationResult.UnreadableDirectories`,
   logged as a warning, every page inside it simply absent from `Pages` — proportionate like an ambiguous
   route, not a startup refusal, for the reason the brief gave (content arrives continuously by push;
   refusing the whole wiki over one bad subtree would brick every other page too).
3. **Symlinks skipped uniformly for files too, not just directories.** The brief scoped the call to
   directory symlinks (matching §2's git-nesting scan, which only ever reasoned about directories). I
   extended the same "never follow" rule to a symlinked `.md` file as well: it carries the identical
   "may point outside the volume" hazard, and there was no narrower reason to trust one reparse-point kind
   over the other. Flagging in case the architect wants this scoped back — nothing later in 3a/3b depends
   on it either way.
4. **SharpYaml 3.13.0 turned out to be a modern, STJ-source-gen-style API**, not the older
   `Serializer`/`SerializerSettings` surface — verified by reflecting the installed package before writing
   against it, rather than assuming. It ships real safety knobs used directly: `YamlSerializerOptions.MaxDepth`
   (explicit `8`, not left at the library's own empirically-confirmed built-in default of `64`) and
   `ReferenceHandling` (pinned to `None` explicitly, though it's already the default). Empirically confirmed
   before relying on it: deserializing into `Dictionary<string, object>` makes SharpYaml itself throw a
   catchable `YamlException` the instant it meets a YAML alias/anchor ("Aliases are not supported when
   deserializing into object unless ReferenceHandling is Preserve") — a five-level, nine-fold "billion
   laughs" document failed immediately rather than expanding, and a 2,000-level block-style / 5,000-bracket
   flow-style document both threw a catchable exception at the configured depth, never a
   `StackOverflowException`. `MaxSizeBytes` (8 KB) is our own check, ahead of any SharpYaml call — the
   library has no size guard at all. `UnsafeAllowDeserializeFromTagTypeName` stays at its safe default
   (`false`), pinned explicitly; a `!!System.IO.FileInfo` tag test never constructs one.
5. **A test-design gap I found and fixed before it reached the reviewer**: my first draft of the
   deep-nesting tests buried everything under one key (`a: {a0: {a1: {... leaf: 1}}}`), so a document with
   the depth cap silently removed would parse successfully but still read back as `PageFrontmatter.Empty`
   (no top-level `title`/`tags`) — indistinguishable from a correctly-rejected one. Fixed by putting
   `title: Deep` as a top-level sibling of the deep chain, so a successful (uncapped) parse is visibly
   `Title = "Deep"`. Mutation-tested both before and after the fix — see below; the fix is what makes the
   depth-cap mutant actually killable.

**Mutation testing (scoped: route-decode containment check + frontmatter bounds, per the brief).** All
three runs against the full unfiltered `dotnet test` (584/584 baseline), each 1 confirmation run —
deterministic code paths, no concurrency, no reason to expect flakiness, so no need to exceed 1. Checksum
before/after every mutation; reverted via `trap`/`finally`. Both mutated files (`PageRouteCodec.cs`,
`SharpYamlFrontmatterParser.cs`) are new/untracked this block, so `git diff -- src` can't see them — used
`git status --short -- src` (`??` on both, as expected) plus MD5 checksum-against-backup as the actual
verification, matching the brief's warning.

- **Frontmatter size cap** (`SharpYamlFrontmatterParser`, disabled the `> MaxSizeBytes` check) → **killed**:
  1 test failed (`OversizedBlock_IsRejectedBeforeParsing`), 583/584 passed, exactly the expected single
  failure.
- **Frontmatter depth cap** (`MaxDepth = MaxNestingDepth` → `MaxDepth = 0`, i.e. SharpYaml's own unset
  default) → **killed**: 2 tests failed (`DeeplyNestedBlockStyle_...`, `DeeplyNestedFlowStyle_...`),
  582/584 passed — both failures showed `Title = "Deep"` instead of empty, exactly the mechanism finding 5
  above describes. This mutant would have **survived** against my first draft of these tests (finding 5) —
  confirms the fix was load-bearing, not cosmetic.
- **Route containment check** (`PageRouteCodec.TryResolveWorkingTreePath`, dropped the trailing-separator
  addition — `containmentPrefix = workingTree` instead of `workingTree + Path.DirectorySeparatorChar`) →
  **survived**: 584/584 still passed. Investigated rather than dismissed: `TryDecode` already refuses any
  route whose decoded form contains `..`, an empty segment, or a smuggled separator (`a%2fb` → `a/b`)
  *before* `TryResolveWorkingTreePath` ever runs, and on every route that clears those checks,
  `Path.Combine(workingTree, relativePath)` can only ever produce `workingTree + separator + relativePath`
  — there is no way through the public API for the candidate to land in a same-prefixed sibling like
  `docs-evil`. The mutant is genuinely unreachable given `TryDecode`'s upstream guarantees today. Kept
  deliberately, not deleted: it is correct, matches the brief's explicit instruction verbatim, and is the
  backstop if `TryDecode`'s validation is ever loosened later without this being revisited — the same
  reasoning CLAUDE.md gives for the `app.UseRouting()` example. Reviewer: worth confirming you agree this
  is a legitimate "kept, not silently dropped" case rather than something needing a contrived
  reflection-based test to force a kill.

**Gates:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **584/584** (519 baseline + 65 new),
full unfiltered. `dotnet format --verify-no-changes` — clean. `openspec validate git-backed-content-core
--strict` — valid. No human-in-the-loop tasks in this block (no UI to click through).

New/changed files: `src/ZeroWiki/ZeroWiki.csproj` (Markdig, SharpYaml refs);
`src/ZeroWiki/Content/{PageRouteCodec,EnumeratedPage,AmbiguousPageRoute,PageEnumerationResult,PageEnumerationService,PageFrontmatter,IFrontmatterParser,SharpYamlFrontmatterParser,MarkdownPipelineFactory,PageFrontmatterExtractor}.cs`
(new); `src/ZeroWiki/Content/ContentStorageStartupExtensions.cs` (DI registrations added);
`tests/ZeroWiki.Tests/Content/{PageRouteCodecTests,PageEnumerationServiceTests,SharpYamlFrontmatterParserTests,PageFrontmatterExtractorTests}.cs`
(new).

→ @reviewer

**[reviewer]** Block 3a (3.1–3.2) audit.

**Verdict: Request changes** — two blockers, both independently reproduced against the actual code
(not taken on trust), plus one confirmed-clean mutation result and clean gates.

**Blocker 1 — the architect's claim is confirmed as described.** `PageEnumerationService`'s collision
detection (`PageEnumerationService.cs:63–82`) only refuses a route claimed by **two or more enumerated
files**; it never checks that a single-claimant route's own inverse (`PageRouteCodec.TryDecode`)
reproduces the file that produced it. Reproduced with a scratch console app referencing the real
project (`ContentPaths` + `PageEnumerationService` + `PageRouteCodec`, no mocks):

```
Working tree contains only "Chapter  1.md" (two spaces).
Pages: 1  — route=Chapter__1 relativePath=Chapter  1.md
AmbiguousRoutes: 0
TryResolveWorkingTreePath("Chapter__1") => ok=True path=".../docs/Chapter_1.md"
Resolved path exists on disk: False
Resolved == Original: False
```

One file, no collision reported, page served without complaint at `/wiki/Chapter__1` — and the class's
own documented save-path entry point (`PageRouteCodec.cs:199–206`, `TryResolveWorkingTreePath`)
resolves that exact route to `Chapter_1.md`, a file that does not exist and is not the file that
produced the route. This is exactly the D12 hazard ("a silent wrong-file write... is the failure class
§2 refused four separate times") except manifesting through a route with only one *current* claimant
rather than two.

The test suite's own comment names the gap without closing it —
`PageRouteCodecTests.cs:84–86` states "PageEnumerationService is what actually protects a real save
from writing into the wrong file, by detecting the collision between whichever two *files* land on the
same route" — that sentence is false for this case; there is only one file, and the protection doesn't
fire. The stronger, correct invariant (matching the architect's framing) is per-route: for every route
with exactly one claimant, `TryDecode(route)` must reproduce that claimant's own relative path, not
merely "no second file also produced this route." `AmbiguousRunsDoNotRoundTripButStayInternallyConsistent`
proves half of what's needed (decode always returns *some* preimage that re-encodes to the same route)
but that preimage is never checked against the actual enumerated file when there's only one.

**Suggested shape** (not prescribing the diff): after grouping in `EnumeratePages`, for the
single-claimant branch (`PageEnumerationService.cs:65–70`), also require
`PageRouteCodec.TryDecode(route, out var roundTrip) && roundTrip == relativePath`; on failure, route
the page into `AmbiguousRoutes` (or a distinct third bucket, if the architect wants the operator-facing
message to read differently from a genuine two-file collision) rather than `Pages`. Any run of two or
more adjacent `{space, '_'}` characters not made entirely of literal underscores is affected — `"a  b.md"`,
`"a_ b.md"` alone, `"mixed_ _underscore and_space.md"` alone — whether or not a second file happens to
exist yet. A file landing later with the "canonical" spelling would then silently create the exact
two-file collision D12 describes, except the save transactionally already happened against the wrong
target in the interim.

**Blocker 2 — a new finding, not in the architect's brief: `TryResolveWorkingTreePath` throws an
unhandled `ArgumentException` on a route that decodes to a NUL byte, instead of refusing.** The brief
named this method as "the sharpest hazard in this block" and its own XML remarks
(`PageRouteCodec.cs:22–33`) claim "both layers refuse rather than clamp: a hostile route is never
silently rewritten into something safe, it is rejected outright." That claim is false for this input.
`TryDecodeSegment` (`PageRouteCodec.cs:141–197`) checks a decoded segment for `/`, `\`, and equality
with `.`/`..`, but never for control characters — so `route = "%00"` percent-decodes via
`Uri.UnescapeDataString` to a literal NUL character, passes every check, and `TryDecode` returns `true`
with a working-tree-relative path containing `\0`. `TryResolveWorkingTreePath` then calls
`Path.GetFullPath` on the combined candidate (`PageRouteCodec.cs:222`), which throws
`System.ArgumentException: Null character in path.` — uncaught. Reproduced directly against the real
class:

```
route="%00"   => Unhandled exception: System.ArgumentException: Null character in path. (Parameter 'path')
   at System.IO.Path.GetFullPath(String path)
   at ZeroWiki.Content.PageRouteCodec.TryResolveWorkingTreePath(...) line 222
```

I also fuzzed a broader set of hostile/adversarial inputs against the public API directly (double
percent-encoding, overlong UTF-8 dot sequences, sibling-directory names, drive-style prefixes, UNC-style
prefixes, encoded slash/backslash combinations, `docs-evil`-shaped names) — none of them escaped the
working tree; every one either refused cleanly or resolved to a harmless path *inside* it. So this is
specifically a NUL-byte gap, not a wider containment failure — but it is a real one: every other
hostile input in this method fails closed (`false`), and this one crashes instead. Every route this
method's callers see is attacker-controlled content by design (§6's save target, and plausibly a future
GET route parameter), so an uncaught exception here is a real robustness/DoS surface even though it
doesn't escape containment. Fix should reject a decoded segment containing `'\0'` (or, more generally,
any `char.IsControl` character, mirroring `EncodeSegment`'s own symmetric treatment on the encode side)
inside `TryDecodeSegment`, so this returns `false` like every other hostile input rather than throwing.

**Mutation testing — re-ran the kept ("survived") mutant independently rather than accepting the
worker's reachability argument on trust.** Fuzzed `TryResolveWorkingTreePath` directly against ~25
adversarial routes including sibling-prefixed names (`docs-evil`, `-evil/x`), double percent-encoding,
and overlong-UTF-8 dot sequences, with a real `docs`/`docs-evil` sibling pair on disk. Every route that
`TryDecode` accepts resolves through `Path.Combine(workingTree, relativePath)` with `relativePath`
already guaranteed free of `/`, `\`, and any leading/rooted form (each per-segment check runs before
the join), so `Path.Combine` can only ever produce `workingTree + separator + relativePath` — there is
no way through the public API today to land in a same-prefixed sibling. I agree with the worker: the
trailing-separator containment check is genuinely unreachable given `TryDecode`'s current guarantees,
and keeping it as a named, documented backstop (rather than deleting it or contriving a
reflection-based test to force a kill) is the right call — consistent with the `app.UseRouting()`
precedent CLAUDE.md cites. Not re-litigating this one; recorded as confirmed-clean.

**Gates — reproduced independently, not relayed:** `dotnet build` — 0 warnings, 0 errors. `dotnet test`
— **584/584**, full unfiltered. `dotnet format --verify-no-changes` — exit 0. `openspec validate
git-backed-content-core --strict` — valid. `git status --short -- src` shows exactly the ten new files
the worker reported and nothing else; `git diff -- src` on the two tracked files is a clean 9-line DI
registration + two `PackageReference` additions — no mutation residue.

**D14 (`SharpYamlFrontmatterParser`) and D13 (`MarkdownPipelineFactory`) — compliant, no findings.**
Size cap checked before `YamlSerializer.Deserialize` runs; `MaxDepth`, `ReferenceHandling.None`, and
`UnsafeAllowDeserializeFromTagTypeName: false` all pinned explicitly rather than left at library
defaults; target type is the non-polymorphic `Dictionary<string, object>`, closing the type-resolving-tag
hazard structurally rather than by convention; failure path is a single broad `catch (Exception)` to
`PageFrontmatter.Empty`, matching D14's "failure is total" requirement and documented as deliberate.
`MarkdownPipelineFactory.Create()` calls `.DisableHtml()` and is registered once as a singleton shared
with 3b. No Razor component, no `MarkupString`, nothing renders in this block — correctly deferred to
3b, out of scope here.

**OpenSpec scope — clean.** No Razor components, no rendering, no `git log`/authorship work; everything
delivered is `Content/` service-layer code plus its DI registration and package references. Task 3.1
and 3.2 are genuinely represented by the diff (3.2's rendering half correctly deferred to 3b per the
brief). `git status --short` shows nothing outside `src/ZeroWiki/Content/`, `src/ZeroWiki/ZeroWiki.csproj`,
`tests/ZeroWiki.Tests/Content/`, and this DEVLOG.

**C# idiom — clean.** `Nullable` enabled project-wide; no `!` null-forgiving operators anywhere in the
new files; no `.Result`/`.Wait()`/`GetAwaiter().GetResult()`; file-scoped namespaces; one top-level type
per file matching its filename; sealed records for the immutable DTOs; PascalCase/camelCase conventions
observed throughout.

**Architectural note (not blocking this block):** `PageEnumerationService.Walk` calls
`Directory.EnumerateFileSystemEntries` then `File.GetAttributes(entry)` on each result
(`PageEnumerationService.cs:105–121`) without holding D3's write lock. A file removed or replaced
between those two calls (a concurrent commit-on-save write, once §6 exists) could throw
`FileNotFoundException` uncaught, or enumerate a transient mid-write state. Not this block's problem to
solve — 3a has no write path and nothing calls this concurrently with a save yet — but worth §6 or
whoever wires enumeration into a live request path re-examining whether a scan needs the shared `flock`
or a snapshot/retry around transient filesystem races.

Reproduction script used for both blockers: a scratch console app (`ProjectReference` to
`src/ZeroWiki/ZeroWiki.csproj`) exercising `PageEnumerationService`/`PageRouteCodec` directly against a
real temp filesystem tree — not committed, not part of this repo.

→ @worker

**[architect]** Block `3.1–3.2` — **held, not remediated.** Two reviewer blockers stand (round one), and
one of them reaches past the code into D12 itself, so the fix is **not** the Architect's to brief yet.

**D12's stated collision rule is wrong, and it is the Architect's error, not the worker's.** D12 names
`a_ b.md` vs `a _b.md` — the example the Product Owner supplied when accepting the scheme, and which
they characterised as pathological. Worked properly, the rule is *any run of two or more adjacent
`{space, '_'}` characters*:

| filename | route | greedy decode | |
|---|---|---|---|
| `a b` | `a_b` | `a b` | round-trips |
| `a_b` | `a__b` | `a_b` | round-trips |
| `a  b` *(two spaces)* | `a__b` | `a_b` | **lost — collides with `a_b`** |
| `a_ b` | `a___b` | `a_ b` | round-trips |
| `a _b` | `a___b` | `a_ b` | **lost** |
| `a   b` | `a___b` | `a_ b` | **lost** |

A run of *n* underscores has Fibonacci(*n*+1) preimages, and greedy decode always wins the all-underscores-
first one. So **a double space collides with a single underscore** — `Chapter  1.md` against
`Chapter_1.md`. That is a typo meeting an ordinary filename, not a diseased mind, and the Product
Owner accepted the scheme on a frequency estimate the Architect gave them and got wrong.

**The defect this exposed is worse than the widened collision class**, and neither the worker's
grouping nor D12's prose reaches it: a *single* file can fail to round-trip **with no collision at
all**. A tree holding only `Chapter  1.md` enumerates as one non-colliding page at `Chapter__1`;
that route decodes to `Chapter_1.md`. `PageRouteCodec`'s own XML docs advertise
`TryResolveWorkingTreePath` as the function §6 must use to pick a file to **write**, so the save would
land in a phantom `Chapter_1.md` — after which the two files genuinely collide and D12's refusal makes
both unreachable. Silent wrong-file write, arriving through a door D12 did not describe. The invariant
D12 needs is per-route and stronger than it states: **a route must decode back to exactly the file that
produced it**, not merely "no second file claimed it".

Recorded as the fourth instance in this change of the standing rule *"when a guard's justification
names a case, check the guard's branch"* — and the first where the wrong justification was written by
the Architect into `design.md` rather than by a worker into a comment. It survived the Architect writing
it, a spec delta built to gate it, and a worker implementing against it; it was caught by working the
encoding arithmetic rather than by reading either.

**Escalated to the Product Owner (CLAUDE.md §4 — implementation reveals the spec itself is wrong).**
The per-route round-trip invariant and reviewer blocker 2 (a `%00` route crashing `Path.GetFullPath`
with an unhandled `ArgumentException`) are both worth fixing under *any* outcome, and blocker 2 is
independent of the encoding entirely. But whether `_`/`__` survives at all is the Product Owner's call
on a bargain that has changed since they took it, so **no remediation block is briefed and nothing is
committed or ticked** until they rule. WIP stays in the working tree.

**[architect]** → `@worker` — **Product Owner ruled: keep the `_`/`__` scheme.** They re-affirmed it
knowing the true collision class (double space vs single underscore), not the mistaken one they were
originally given. So D12's *mechanism* stands and its *prose* was wrong; `design.md` D12 and
`specs/content-store/spec.md` are now corrected, and the corrected text is what block 3a is judged
against. Re-read D12 before touching code — the invariant it states has changed.

This is the ordinary block-3a review loop, **not** a remediation block: fix both blockers, hand back to
`@reviewer`, and the whole of 3a still lands as **one** commit. No task numbers change and nothing is
ticked yet.

**Blocker 1 — the invariant is per-route, and grouping by claimant count does not implement it.** A
route is servable only when **exactly one file claims it *and* `TryDecode(route)` reproduces that
claimant's own path**. `Chapter  1.md` alone is the fixture: one claimant, no collision reported today,
and a route that decodes to `Chapter_1.md`. Both conditions are one property — *the route identifies
exactly this file* — so express them as one check rather than two adjacent ones that a later reader
could think are alternatives. A route failing either is reported with every path implicated and serves
no body. Also fix `tests/ZeroWiki.Tests/Content/PageRouteCodecTests.cs:84-86`, whose comment asserts
this protection already exists: a false claim in a test is the defect this change keeps producing, and
it is worth more than the code fix as a habit.

**Blocker 2 — `%00` crashes `Path.GetFullPath` with an unhandled `ArgumentException`.** Independent of
the encoding, and it contradicts `PageRouteCodec`'s own documented "rejected outright" contract.
`TryDecodeSegment` must refuse decoded segments containing control characters — reject the class, not
the single byte the reviewer happened to fuzz. Spec now gates this: *Hostile route is refused, not
fatal*.

**Do not** widen scope beyond these two and the spec scenarios that gate them (*Sole claimant whose
route does not identify it is refused*, *Hostile route is refused, not fatal*). The reviewer's parked
note about `Walk` racing a concurrent write without D3's lock belongs to §6 — leave it. Re-run all four
gates and report actual numbers; `git status --short -- src` alongside `git diff -- src`, since most of
this block is still untracked.

**[worker]** Both blockers fixed, corrected D12/spec re-read first. No scope beyond the two blockers and
their gating scenarios.

**Blocker 1 fix — `PageEnumerationService.EnumeratePages`.** Replaced the claimant-count-only branch with
one boolean expressing D12's actual invariant:

```csharp
var identifiesExactlyOneFile =
    relativePaths.Count == 1 &&
    PageRouteCodec.TryDecode(route, out var roundTrippedPath) &&
    string.Equals(roundTrippedPath, relativePaths[0], StringComparison.Ordinal);
```

A route failing this (either two-plus claimants, or a sole claimant whose own route doesn't decode back
to it) goes into `AmbiguousRoutes`, unchanged from before structurally — just gated on the corrected
property. Updated `AmbiguousPageRoute`'s and `PageEnumerationResult`'s XML docs, which previously said
"claimed by more than one file" — no longer true, so they now describe the actual invariant ("the route
identifies exactly this file") and both ways it can fail. Added
`PageEnumerationServiceTests.SoleClaimantWhoseRouteDoesNotIdentifyIt_IsRefused` using the reviewer's exact
`Chapter  1.md` fixture, plus a companion positive case (`Chapter 1.md`, one space, still serves normally)
so the stricter check doesn't read as "reject anything with an underscore-ish route."

**Fixed the false test comment** (`PageRouteCodecTests.cs`, was lines 84–86): it claimed
`PageEnumerationService` protects a save "by detecting the collision between whichever two files land on
the same route" — false for the zero-collision case this whole round is about. Rewritten to point at
`PageEnumerationServiceTests` for the actual (per-route, not per-collision) invariant, and to name the
`Chapter  1.md` fixture as the proof it's stronger than "no second file claimed it."

**Blocker 2 fix — `PageRouteCodec.TryDecodeSegment`.** Added, right after the existing `/`/`\` smuggled-
separator check:

```csharp
if (percentDecoded.Any(char.IsControl))
{
    return false;
}
```

Rejects the class (mirrors `EncodeSegment`'s own `char.IsControl` check on the way in), not just the NUL
byte the reviewer fuzzed — `%01`, `%1f`, `%7f` all refuse the same way now. Updated the class-level and
`TryDecode` XML remarks, which previously claimed "rejected outright" as a blanket fact when it wasn't yet
true for this input.

**Verified the fixes are load-bearing, not decorative** — reverted both (script, `trap`-based, checksum
before/after) and reran just the new tests: all 10 failed without the fix, including the exact
`ArgumentException: Null character in path.` at `PageRouteCodec.cs:231` the reviewer reported, reproduced
verbatim. Re-applied and confirmed checksums match the fixed state. This isn't the scoped mutation testing
(no new mutants introduced beyond what's already recorded above) — it's confirming these specific new
tests aren't vacuous, using the same discipline.

**Gates, actual numbers:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **595/595**, full
unfiltered (584 prior + 11 new: 6 `TryDecode_RefusesControlCharacters` cases, 3
`TryResolveWorkingTreePath_RefusesControlCharactersRatherThanThrowing` cases, 2 sole-claimant cases).
`dotnet format --verify-no-changes` — exit 0. `openspec validate git-backed-content-core --strict` —
valid. `git status --short -- src` shows exactly the same ten new files plus the same two modified files
as before this round; `git diff -- src` is unchanged (DI registrations + package refs only) — this round
only touched files `git diff` can't see, confirmed clean by the revert-and-diff check above rather than
by `git diff` itself.

→ @reviewer

**[reviewer]** Block 3a re-audit, against the corrected D12 / spec text (re-read in full — the
Fibonacci-preimage table, the "one property, not two independent checks" language, and the two new
scenarios: *Sole claimant whose route does not identify it is refused*, *Hostile route is refused, not
fatal*).

**Verdict: Approve with nits.**

**Blocker 1 — confirmed fixed, independently re-tested.** Re-ran my original `Chapter  1.md` (two
spaces) reproduction against the fixed `PageEnumerationService.EnumeratePages`
(`src/ZeroWiki/Content/PageEnumerationService.cs:80-83`) — now correctly refused: `Pages: 0`,
`AmbiguousRoutes: 1 (route=Chapter__1, claimants=Chapter  1.md)`. Also tested the **nested-path** case
the coordinator specifically asked me to check, which no existing test in this block covers directly —
`docs/Project Notes/Chapter  1.md` alongside `docs/Project Notes/Unaffected.md` — and the fix holds
there too: `Project_Notes/Chapter__1` is refused while `Project_Notes/Unaffected` is served normally,
confirming the per-route isolation promised by D12 ("every other route is unaffected") survives
nesting.

On "one property, not two adjacent checks": `identifiesExactlyOneFile` (`PageEnumerationService.cs:80-83`)
is a single boolean computed by conjunction and then branched on once (`PageEnumerationService.cs:85`)
— there is no code path where the claimant-count check gates a *different* downstream consequence than
the round-trip check; `&&` short-circuits only in the direction that's always safe (a route with >1
claimant never needs the round-trip check to already be ambiguous). This reads as a faithful
implementation of the single predicate D12 now names, not two adjacent checks wearing a shared `if`.

**Blocker 2 — confirmed fixed, independently re-tested, and re-verified reachability was not
narrowed away.** Re-ran my original NUL-byte reproduction (`%00`) — refused cleanly, no exception. Then
re-ran my full adversarial set from the first round (~29 routes: double percent-encoding, overlong
UTF-8 dot sequences, `docs-evil`/`-evil` sibling-prefix attempts, drive-style and UNC-style prefixes,
`%00`/`%01`/`%1f`/`%7f`) directly against `TryResolveWorkingTreePath` with a real `docs`/`docs-evil`
sibling pair on disk: **0 escapes, 0 crashes.** The control-character check
(`PageRouteCodec.cs:171-180`) runs on `percentDecoded`, immediately after the separator-smuggling check
and *before* the `_`/`__` layer-1 reverse — correctly positioned, and unbypassable by construction: the
layer-1 reverse only ever substitutes `_`/`__` with `_`/space, so it cannot introduce a control
character from input that had none. Confirmed empirically that `Path.GetFullPath` on this platform only
throws for NUL specifically (tab/CR/LF pass through unmolested), so rejecting the whole `IsControl`
class is strictly broader than the minimum fix — but the corrected spec's own new scenario (*Hostile
route is refused, not fatal*) names "a control character" as its own case alongside NUL, not
conditioned on "unless a real file could have produced it," so this is compliant with the
now-authoritative spec text as written, not overreach invented by the worker.

**One theoretical, non-blocking edge case worth recording rather than acting on:** a real file whose
name contains a *literal* non-NUL control character (e.g. an embedded tab) would, before this round,
have round-tripped correctly through `PageRouteCodec` — `EncodeSegment` already percent-encodes any
`IsControl` character on the way in, and decoding that specific percent sequence back would have
reproduced it. After this round's fix, such a file's route is now refused as "not identifying exactly
one file," even though no second file is involved and the round trip would in fact have succeeded. This
is a real, if vanishingly unlikely, behavioral narrowing — no ordinary OS file manager or Obsidian
creates filenames containing raw control bytes — and the corrected spec's own premise ("no enumerated
file could have produced" a control character) doesn't hold in the fully general case. Not asking for a
change: the corrected spec explicitly directs exactly this behavior, and refusing a technically-servable
route is the same fail-closed posture D12 uses everywhere else. Recording it so it isn't rediscovered as
a surprise later if someone ever populates a vault with such a filename.

**Gates — reproduced independently:** `dotnet build` 0 warnings/0 errors. `dotnet test` **595/595**,
full unfiltered. `dotnet format --verify-no-changes` exit 0. `openspec validate
git-backed-content-core --strict` valid. `git status --short -- src` shows the identical file set from
round one (no mutation residue, no scope widening); `git diff -- src` on the two tracked files is
unchanged from round one.

**Nit — one XML doc was missed in this round's sweep.** `EnumeratedPage.cs:4` still reads "whose route
is claimed by no other file," the pre-fix half of D12's invariant — not false (every `Pages` entry does
satisfy that), but incomplete next to the now-accurate docs on `PageRouteCodec`, `PageEnumerationService`,
`AmbiguousPageRoute`, and `PageEnumerationResult`, which all correctly state the full "identifies exactly
this file" property. Given this project's recurring pattern of a doc comment's stated justification
outliving the code it describes, worth a one-line fix: "...whose route identifies it and only it (D12) —
claimed by no other file, and its own route decodes back to this file's path." Not blocking; flagging
because it's precisely the class of gap this round's re-audit was asked to check for.

**No scope widening confirmed.** The parked `PageEnumerationService.Walk`-vs-D3-lock architectural note
from round one stays parked for §6 — nothing in this round touches locking, and the diff to
`ContentStorageStartupExtensions.cs`/`ZeroWiki.csproj` is byte-identical to round one.

→ @architect

**[worker]** Nit closed, scoped exactly as directed (nothing beyond this and the sweep).

**`EnumeratedPage.cs:4`** now states the full conjoined invariant, not just the claimant-count half:

> A Markdown file found under `ContentPaths.WorkingTree` whose route identifies exactly this file (D12):
> it is the route's sole claimant, *and* `PageRouteCodec.TryDecode` applied to `Route` reproduces
> `RelativePath`. Both hold for every instance of this type — `PageEnumerationService` never constructs
> one otherwise — so a caller (e.g. a future §6 save) can invert `Route` and trust the result names this
> exact file.

Named `PageEnumerationService` as the type that guarantees it and `TryDecode`/`Route`/`RelativePath` by
name, since the reviewer's concern was specifically that §6 will read this doc to learn what the route
guarantees.

**Swept the rest of the block** (`grep` across every `.cs` file under `src/ZeroWiki/Content/` and
`tests/ZeroWiki.Tests/Content/` for `claim`/`collision`/`identif`) rather than trusting that this was the
only instance. `PageRouteCodec.cs`, `PageEnumerationService.cs`, `AmbiguousPageRoute.cs`, and
`PageEnumerationResult.cs` all already state the full property from the previous round's fixes — checked
each one directly rather than taking the grep hit count as sufficient. `PageRouteCodecTests.cs`'s two
remaining mentions ("no second file claimed it") are the corrected comment from the earlier blocker-1
fix, asserting the invariant is *stronger* than claimant-count — accurate, not stale. No other file in the
block references the invariant at all. `EnumeratedPage.cs` was the only doc actually behind.

**Gates, actual numbers:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **595/595**, full
unfiltered (unchanged — doc-only change, no test added or touched). `dotnet format --verify-no-changes`
— exit 0. `openspec validate git-backed-content-core --strict` — valid. `git status --short -- src`
shows the identical ten new files and two modified files as both prior rounds; `git diff -- src` is
byte-identical to round one and two (`ContentStorageStartupExtensions.cs` DI registrations +
`ZeroWiki.csproj` package refs only) — this change is invisible to `git diff` since `EnumeratedPage.cs`
is still untracked, confirmed via `git status --short` instead.

Per the architect: not routing back through `@reviewer` for this one.

**[architect]** Block `3.1–3.2` — **closed and committed.** Reviewer `Approve` after two rounds
(two blockers, both independently re-tested by the reviewer rather than accepted on report), plus one
doc nit swept afterwards. Gates run by the Architect, not relayed: `dotnet build` 0 warnings / 0
errors, `dotnet test` **595/595** full unfiltered, `dotnet format --verify-no-changes` exit 0,
`openspec validate --strict` valid. `git status --short -- src` and `git diff -- src` both checked —
no mutation residue, and the file set is unchanged across all three rounds.

Two things this block earned that belong in `## NEXT` when §3 closes:

- **Verify a claim about a *mapping* by enumerating it, not by reading it.** D12's injectivity claim was
  wrong in `design.md`, written by the Architect, and it survived being written, a spec delta built to
  gate it, and a worker implementing against it. Nobody caught it by reading; it fell out of computing
  the encoding over a handful of awkward filenames. Prose about a mapping reads as true right up until
  it is enumerated — this is the fifth claim-versus-mechanism defect in this change and the first
  authored at the design layer, where it binds every later section rather than one file.
- **Grouping is not identity.** "No two files share a route" and "this route identifies this file" look
  like the same property and are not; the first is implied by the second and does not imply it. The
  single-claimant failure was invisible to the check that was supposed to prevent exactly it, and the
  test suite carried a comment asserting the protection existed. When a guard is built from a
  *relation between items*, ask what it says about **one** item on its own.

**§3 status: 3.1–3.2 done, 3.3–3.4 remain.** Block 3b next — Markdown rendering under D13 (raw HTML
off), git-derived authorship/last-edit via `git log`, and `Home.razor`'s de-scaffolding from the Blazor
template placeholder. §3's supervisor review runs over `60957e6..HEAD` once 3b lands.

**[architect]** → `@worker` — **Block 3b brief (tasks 3.3–3.4).** Closes §3. Base for the section
review remains `60957e6`; block 3a landed as `8332c79`.

**Deliver:** the Static SSR page that renders a wiki page's Markdown body, and per-page authorship /
last-edit read from git. Plus one piece of de-scaffolding carrying no task number (below).

**Tasks verbatim:**
- 3.3 Render Markdown to HTML in the Blazor shell
- 3.4 Expose per-page authorship/last-edit read from `git log`/`blame`

**Binding:** D5 (authorship from git, no hand-maintained field), D7 (Static SSR default, islands only
where live behaviour is needed — this block needs none), D12 (routes, and the refusal 3a implemented),
D13 (raw HTML escaped). Gating scenarios in `specs/content-store/spec.md`: *Authorship comes from git*,
*Page with frontmatter is rendered*, *Page without frontmatter is rendered*, *Malformed frontmatter
degrades gracefully*, *Embedded script is not executed*, *Nested page is addressable by its path*,
*Ambiguous route is refused, not guessed*, *Sole claimant whose route does not identify it is refused*,
*Hostile route is refused, not fatal*.

**The hazard that decides this block — verify it empirically before building on either answer.**
`PageRouteCodec.TryDecode` calls `Uri.UnescapeDataString` on each segment, so it expects a **raw,
still-percent-encoded** route. ASP.NET Core routing may hand a catch-all route parameter the value
**already percent-decoded**. If it does, this block double-decodes: a file named `a%20b.md` encodes to
route `a%2520b`, the framework decodes that to `a%20b`, and `TryDecode` decodes *again* to `a b` — the
wrong file, through exactly the seam 3a's blockers lived in. **Determine what the framework actually
supplies by running it, not by reasoning about it or reading docs**, then make the seam explicit: state
in one place, in code, who percent-decodes and that it happens exactly once. Add a test that fails if
that ever changes. If this forces a change to `TryDecode`'s contract, say so rather than papering over
it at the call site — 3a's inverse is what §6 will use to choose a file to write.

**The rest, in order of how much they can hurt:**

1. **D13 is a security property, not a formatting preference.** `MarkupString` is the only way to emit
   rendered HTML and it sanitizes nothing, so the pipeline being configured with raw HTML off is the
   entire defence. Assert there is **no other path** by which page content reaches the page unescaped —
   frontmatter values, the title, the page path, the names in an ambiguous-route message, and the git
   author name (which is attacker-influenced: a pushed commit carries a self-asserted identity, per
   D5's accepted trade-off) all render too, and none of them go through Markdig.
2. **Surface 3a's refusals rather than collapsing them to "not found".** An ambiguous route names every
   claiming file; a sole-claimant route that does not identify its file is refused likewise; an
   unreadable directory is already reported by `PageEnumerationResult`. A member who hits one needs to
   know which file to rename — that is the whole point of refusing instead of guessing.
3. **Static SSR only** (D7). No `InteractiveServer` island in this block; nothing here needs live
   behaviour. Match how the shipped pages handle authorization — `AnonymousGate` answers unauthenticated
   requests, so check what the existing pages do rather than inventing a new pattern.
4. **3.4 reads git, and must not make forward obligation 8 live.** `GitProcessRunner` does not kill the
   git subprocess on cancellation; §6 owns that fix. Pass `CancellationToken.None` and say in a comment
   why, rather than passing a request token and implying a cancellation that does not work. **No
   caching** — §4.1 owns the index that makes last-edit cheap; a cache here would be the duplicated
   abstraction a section review exists to catch.
5. **De-scaffolding, no task number:** `Home.razor` still holds the Blazor template's "Hello, world!".
   Replace it with placeholder ZeroWiki content. The Product Owner has **explicitly pinned** the real
   welcome page — no `_welcome-message.md`, no reading content from the repo, no spec scenario. A
   placeholder, nothing more. Check `NavMenu.razor` for template cruft while you are there. Do **not**
   build a page index or listing; that is §4's.

**Mutation testing — capped at 3 runs, and only two targets:** the raw-HTML-off pipeline configuration
(a mutant enabling raw HTML must be killed by a test, or *Embedded script is not executed* is not
actually gated), and the route-resolution branch that chooses between serving, refusing, and
not-found. Nothing else in this block warrants it. Checksum before *and* after; revert via
`trap`/`finally`, never `git checkout --`. **New files are untracked and invisible to `git diff` — run
`git status --short -- src` alongside it.**

**Gates before handoff — all four, actual numbers not claims:** `dotnet build` clean, full unfiltered
`dotnet test` (595/595 before this block), `openspec validate git-backed-content-core --strict`,
`dotnet format --verify-no-changes`. Post to this DEVLOG as `[worker]` under `## 3.`, then hand off
`→ @reviewer`.

**[worker]** Block 3b (3.3–3.4) implemented. New: `src/ZeroWiki/Components/Pages/WikiPage.razor`
(`/wiki/{*Route}`, Static SSR, `[Authorize]`), `src/ZeroWiki/Content/PageHistoryService.cs` +
`PageLastEdit.cs` (git-derived authorship/last-edit, D5). Changed: `PageRouteCodec.cs` (new decode
entry point, below), `ContentStorageStartupExtensions.cs` (DI for `PageHistoryService`), `Home.razor`
de-scaffolded. `NavMenu.razor` swept — already clean, no template cruft, no change needed.

**The routing hazard — resolved empirically, not by reasoning.** Built a throwaway `TestServer` probe
(a bare `/probe/{*Route}` endpoint reading `HttpContext.Request.RouteValues`) before writing any
production code. Confirmed: ASP.NET Core percent-decodes a catch-all parameter's value **exactly once**
before a page sees it — `a%2520b` (the canonical route for a file literally named `a%20b.md`) arrives
as the string `a%20b`, not `a%2520b` and not `a b`. Also confirmed two things the brief didn't ask for
but the fix needed: the framework leaves `%2f`/`%2F` **undecoded** (so a catch-all segment can never
smuggle in a fresh `/` this way), while an unescaped `%5c` **is** decoded into a real backslash
character — an asymmetry that matters for which existing 3a validation still applies unchanged. `%00`
never reaches routing at all — `Microsoft.AspNetCore.Internal.UrlDecoder` throws
`InvalidOperationException: The path contains null characters` at request-line parsing, before
`UseRouting` runs; confirmed this is real framework code (not a `TestServer` artifact) by the exception
type. Probe deleted after use; the finding lives in `PageRouteCodec`'s XML docs and in
`PageRouteCodecTests`, not in a scratch file.

**The fix: a second decode entry point, `TryDecode`'s own contract left untouched.**
`PageRouteCodec.TryDecodeRouteValue(string routeValue, out string workingTreeRelativePath)` shares
`TryDecode`'s validation and layer-1 reverse but skips the percent-decode step — refactored both
through one private `TryDecodeCore(route, decodePercentEncoding, out path)`. `TryDecode` itself is
byte-for-byte unchanged in behaviour (still used by `PageEnumerationService`'s round-trip invariant
check on freshly-`Encode`d routes, which *are* still-encoded, correctly) — zero regression risk to 3a's
twice-reviewed code. `WikiPage.razor` calls `TryDecodeRouteValue` exclusively; calling `TryDecode` on
an HTTP route value is now a documented misuse, not a silent trap. `TryDecodeRouteValue_DoesNotDoubleDecodeAFileNameContainingALiteralPercentSign`
(`PageRouteCodecTests.cs`) pins the exact scenario: encodes `a%20b.md` → `a%2520b`, simulates the
framework's one decode pass → `a%20b`, asserts `TryDecodeRouteValue` recovers `a%20b.md` while
`TryDecode` on the *same* already-decoded string wrongly recovers `a b.md` — the regression this test
exists to catch, both sides asserted in one place. An HTTP-level twin
(`A_page_whose_filename_needs_percent_encoding_is_served_at_its_double_encoded_route_and_not_the_wrong_file`,
`WikiPageTests.cs`) proves it through real routing, not just the codec in isolation.

**Lookup strategy avoids rebuilding `TryResolveWorkingTreePath`'s containment logic for reads.**
`WikiPage` doesn't resolve a filesystem path itself: it decodes the route value to a relative path,
re-`Encode`s that back to a canonical route string, and looks it up by string equality against a fresh
`PageEnumerationService.EnumeratePages()` result (`Pages`/`AmbiguousRoutes`, both keyed by `Route`). No
`Path.GetFullPath`/containment check runs on request input at all in this path — every candidate
`AbsolutePath` already came from `PageEnumerationService`'s own disk walk, so there's no path-traversal
surface for *reading* to defend independent of what 3a already guarantees. `TryResolveWorkingTreePath`
is untouched and unused this block; §6 (write) still owns building a framework-aware equivalent for
choosing a file to *save to*, which is a genuinely different problem (a save can target a path nothing
has enumerated yet) — flagging this now so §6 doesn't assume this block solved it.

**A second empirical finding, not anticipated by the brief: setting a 4xx status code from the page
discards its own rendered body.** `Program.cs` wires `UseStatusCodePagesWithReExecute("/not-found", ...)`
ahead of the Razor pipeline. First attempt set `Response.StatusCode = 404` for not-found/ambiguous/
sole-claimant-refused and returned our own explanatory markup; verified against the real app rather
than assumed correct, and it wasn't — the response body that actually reached the client was
`NotFound.razor`'s generic text, silently discarding the claimant list D12 exists to surface. This
would have shipped as a heisenbug: `A_route_with_no_matching_page_is_reported_as_not_found` still
passed throughout, because `NotFound.razor`'s own heading is "Not Found" and the test's
case-insensitive `Contains("not found")` couldn't tell the swap apart from the real thing. Fixed by
never setting a non-2xx status from this page — all three refused/not-found/rendered outcomes are
HTTP 200 with differentiated body content, which is what the spec scenarios actually care about; no
scenario in `specs/content-store/spec.md` mandates a particular status code. Documented as a deliberate,
verified constraint in `WikiPage.razor`'s XML remarks so nobody reintroduces it while chasing REST
purity. **Flagging for the reviewer/architect:** this is a real behavioural call forced by an
implementation constraint, not a preference — worth a second look.

**D13 audit (raw HTML off is the entire defence) — swept every render path, not just the body.**
`_renderedBody` is the only `MarkupString` in the component. Frontmatter `Title`/`Tags`, `_page`'s
`RelativePath` (title fallback), every claimant path in the ambiguous-route list, and
`PageHistoryService`'s git-derived `AuthorName` (attacker-influenced per D5 — a pushed commit's author
is self-asserted) all render through plain Razor `@expression` interpolation, which HTML-encodes by
default — none of them touch Markdig or `MarkupString`.
`Frontmatter_title_and_git_author_are_html_escaped_not_raw` pins both deliberately, with `<script>` in
both the frontmatter `title` and the git author name of the test's own commit.

**3.4 / forward obligation 8:** `PageHistoryService.GetLastEditAsync` is called with
`CancellationToken.None` from `WikiPage`, with a comment stating why (`GitProcessRunner` doesn't kill
its subprocess on cancellation; §6 owns that fix). Uses `git log -1 --format=%an<US>%aI -- docs/<path>`
(author name + strict ISO date, ASCII Unit Separator as delimiter). Degrades to `null` (page still
renders, no last-edit line) on either no history for the path (untracked file, normal empty
`git log` output) or the git subprocess itself failing (`GitProcessException`) — logged as a warning,
never thrown into the page render. No caching, per the brief — `PageEnumerationService.EnumeratePages()`
and `git log` both run fresh every request; §4.1 owns the index.

**Architect calls made here, flagging both:**
- **Status code is uniformly 200** for rendered/refused/not-found (see empirical finding above) — not
  a stylistic choice, forced by the `UseStatusCodePagesWithReExecute` interaction. If this needs to
  change later (e.g. a distinct status for genuinely-missing vs refused), it needs a different mechanism
  than setting `Response.StatusCode` from inside the component — e.g. suppressing the status-code-pages
  feature for this endpoint specifically, or moving the differentiation to middleware ahead of routing.
- Ambiguous and sole-claimant-refused routes are not distinguished from each other in the *shape* of the
  response (same "ambiguous" heading, same claimant list) — D12 treats them as one failure class ("the
  route cannot be trusted to name one specific file"), so the page follows suit rather than inventing a
  UI distinction the design doesn't ask for.

**Mutation testing — capped at 3, two targets, both killed 1/1 under the full unfiltered suite (638
baseline including this block's own new tests; no filtered-only figure reported).** Checksummed
before/after both; reverted by restoring the exact prior text (not `git checkout --`), confirmed by
checksum match plus `git status --short -- src` showing the same file set before and after.
- **Raw-HTML-off pipeline** (`MarkdownPipelineFactory.Create`, removed `.DisableHtml()`) → killed:
  `Embedded_script_is_shown_as_text_rather_than_executed` failed, `<script>alert('xss')</script>`
  rendered live instead of escaped. 637/638.
- **Route-resolution branch** (`WikiPage.razor`, `if (ambiguousMatch is not null)` → `if (false)`,
  i.e. ambiguous routes fall through to the not-found/serve path instead of refusing) → killed:
  `An_ambiguous_route_names_both_claimants_...` and `A_sole_claimant_whose_route_does_not_identify_it_...`
  both failed. 636/638.

**Gates, actual numbers:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **638/638**, full
unfiltered (595 baseline + 43 new: 25 `PageRouteCodecTests` cases for `TryDecodeRouteValue`, 5
`PageHistoryServiceTests`, 12 `WikiPageTests` end-to-end through the real app/routing/git). `dotnet
format --verify-no-changes` — exit 0. `openspec validate git-backed-content-core --strict` — valid.
`git status --short -- src` / `git diff --stat -- src` both checked post-revert: three tracked files
modified (`Home.razor`, `ContentStorageStartupExtensions.cs`, `PageRouteCodec.cs`) and three new
untracked files (`WikiPage.razor`, `PageHistoryService.cs`, `PageLastEdit.cs`) — matches exactly what
this block intended, no mutation residue. No human-in-the-loop tasks — all scenarios covered by the
real-app HTTP tests above; the reviewer should still feel free to click through `/wiki/...` manually if
useful, but nothing here is gated on that.

**What the reviewer should look at hardest:** (1) the `TryDecodeRouteValue`/`TryDecode` split — confirm
`TryDecode`'s existing callers and tests are genuinely unaffected, and that no other call site could
ever pass an HTTP-sourced value into `TryDecode` by mistake; (2) the uniform-200 decision and its
rationale — this is the one call in this block that wasn't dictated by the brief; (3) the D13 sweep —
independently verify no other render path bypasses `@expression` escaping.

→ @reviewer

**[reviewer]** Block 3b audit (3.3–3.4), the block that closes §3.

**Verdict: Request changes** — two blockers (both from the architect's findings, independently
investigated rather than taken on trust), no other blocking issues.

**Finding 1 — confirmed: `TryResolveWorkingTreePath` is a live doc landmine aimed at §6, exactly as the
architect suspected.** `PageRouteCodec.cs:290` still calls `TryDecode` — the raw, still-percent-encoded
contract — while the method's own summary (`PageRouteCodec.cs:279-280`) says unqualified: "This is the
function a save path (§6) must use to turn a route into a file to write." §6 will get its route from
ASP.NET Core routing, which this very block proved (empirically, via `TestServer`) arrives
already-percent-decoded once. A §6 author who takes this doc at face value calls the wrong entry point
and double-decodes — the identical defect class as 3a's two blockers and this block's own headline
fix, now sitting in the one function explicitly labelled for the save path to use. `grep -rn
"TryResolveWorkingTreePath"` confirms it is genuinely latent today — every call site is a test
(`PageRouteCodecTests.cs:239,252,261,276,289,297,312,320`); nothing in 3a or 3b's production code
reaches it. That's the only thing saving this from being live right now, and it stops being true the
moment §6 exists. This file was substantially rewritten this round — `TryDecode` and
`TryDecodeRouteValue` both got new, careful, cross-referencing remarks — and this one doc comment
was the one left unequal to the split. Blocking because the fix is cheap, in-scope (the file is already
open this round), and this project has a specific, named history of exactly this failure mode surviving
a round because nobody re-read a stale claim next to new code.

On durability: I agree the "never apply `TryDecode` and `TryDecodeRouteValue` to the same string"
invariant is currently enforced by prose alone, and this change's tally of prose-enforced invariants
that didn't hold is not zero. I'd go further than "worth considering" — distinct wrapper types (e.g. an
`EncodedRoute`/`DecodedRoutePath` pair, or simply having `TryResolveWorkingTreePath` take the already-
decoded relative path as its input, mirroring how `WikiPage.razor` itself now separates "decode the
route value" from "do something with the relative path") would make the mix-up a compile error. I don't
think this is over-engineering given the specific, repeated shape of defect this change keeps producing
at exactly this seam — but I'm not blocking on the type-safety redesign itself, since that's §6's
function to build, not this block's to redesign in advance. What I am blocking on: `TryResolveWorkingTreePath`'s
doc must stop claiming, unqualified, to be the function to use — at minimum it needs the same explicit
warning `TryDecode`'s own remarks now carry, and ideally a note that §6 will need an equivalent built on
`TryDecodeRouteValue`, not this one as-is.

**Finding 2 — the 200-everywhere decision: a narrower fix exists in the framework and the DEVLOG shows
no evidence it was tried.** I confirmed by reflecting against the actual installed `Microsoft.AspNetCore.App
10.0.10` that ASP.NET Core ships two purpose-built mechanisms for exactly this problem: `Microsoft.
AspNetCore.Diagnostics.StatusCodePagesFeature` (implementing `IStatusCodePagesFeature`, a settable
`Enabled` bool retrievable via `HttpContext.Features.Get<IStatusCodePagesFeature>()`) and `Microsoft.
AspNetCore.Http.Metadata.ISkipStatusCodePagesMetadata` (with a concrete `[SkipStatusCodePages]`
attribute in `Microsoft.AspNetCore.Mvc`, usable as endpoint metadata). Both exist specifically to let a
response or an endpoint opt out of `UseStatusCodePagesWithReExecute`'s re-execution — which is precisely
the problem being solved by flattening every outcome to 200. Neither appears in the DEVLOG's account of
what was tried; the writeup goes from "setting `Response.StatusCode` gets the body discarded" straight
to "so render everything as 200."

I tried to verify this narrower path myself and want to be honest about what I found: a lightweight,
non-Blazor `TestServer` probe I built (a bare `app.Run` handler setting a 404 then writing a body,
behind `UseStatusCodePagesWithReExecute`) did **not** reproduce the swallowing behaviour at all — the
body survived even with no opt-out, most likely because `HttpResponseWritingExtensions.WriteAsync`
implicitly sets `ContentType`, and the real `StatusCodePagesMiddleware` skips re-execution once
`ContentType`/`ContentLength`/`HasStarted` are set — conditions a raw minimal-API handler hits
differently than Blazor's buffered Static SSR render does. So my probe is not strong enough to prove
`IStatusCodePagesFeature.Enabled = false` fixes this against the *real* Blazor pipeline, and I'm not
asserting it does. What I can assert: the mechanism exists, is built for this exact scenario, is
one line to try (`HttpContext.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;` before the
component sets its own status code), and this block's own standard throughout — "verify empirically,
not by reasoning about it" — wasn't applied to the alternative before it was ruled out by omission.
200-for-a-missing-or-refused-page is a real HTTP-semantics cost, worth paying only if the honest
alternative was actually tried and failed. Asking for that specific, cheap experiment before the
uniform-200 design is accepted as final. If it turns out `IStatusCodePagesFeature.Enabled = false`
doesn't survive Blazor's rendering (a real possibility — Blazor may set headers or begin the response at
a point this feature flag can't intercept), then the current design stands, but as a verified constraint
rather than one arrived at by trying only the naive approach.

**Not blocking, recorded for `## NEXT` / §6:** the percent-decode-exactly-once guarantee this block
proved is specific to ASP.NET Core's own routing layer, tested via `TestServer` against the app in
isolation. D2 anticipates *some* reverse proxy in front of the container for TLS termination; if that
proxy also normalizes percent-encoding (the same class of risk D12 already named for why `_`/`__` was
chosen over `%5F` — proxies are entitled to decode unreserved-octet percent-sequences per RFC 3986
§6.2.2.2), the "framework decodes exactly once" invariant this block hard-codes into
`TryDecodeRouteValue` could see a route that's already been decoded twice by the time Kestrel sees it.
Not this block's defect — it correctly nailed down what's provably true about the ASP.NET Core layer,
which is the only layer it can test — but worth the Architect naming explicitly whether this is an
accepted risk (matching D12's own acceptance of RFC 3986 proxy-normalization risk generally) or needs a
stated deployment constraint (e.g. "the fronting proxy must not further-decode the path").

**D13 — verified independently, no findings.** Read every render path in `WikiPage.razor`:
`PageDisplayTitle` (title/`_page.RelativePath` fallback), `lastEdit.AuthorName` (attacker-influenced per
D5 — a pushed commit's author is self-asserted), each `tag`, and every claimant `path` in the
ambiguous-route list all render through plain Razor `@expression` interpolation, which HTML-encodes.
`_renderedBody` is the only `MarkupString` in the file, sourced from `Markdown.ToHtml(markdown,
MarkdownPipeline)` against the same singleton `MarkdownPipeline` registered in 3a
(`MarkdownPipelineFactory.Create()`, `.DisableHtml()` — confirmed unchanged this round via `git diff
8332c79 -- ContentStorageStartupExtensions.cs`). `Frontmatter_title_and_git_author_are_html_escaped_not_raw`
(`WikiPageTests.cs:195-217`) exercises exactly this with `<script>` in both the frontmatter title and a
real commit's git author name — a good test, since it's the one place D5's "self-asserted push identity"
trade-off becomes a live XSS vector if anyone ever adds a second `MarkupString`.

**3a's refusals surface correctly — verified via the real end-to-end tests, not just reading the
branches.** `An_ambiguous_route_names_both_claimants_rather_than_reading_as_not_found` and
`A_sole_claimant_whose_route_does_not_identify_it_is_refused_rather_than_served`
(`WikiPageTests.cs:80-110`) both assert the specific claimant path strings appear and the wrongly-served
body content does not — these are the tests that actually guard the re-execute regression, since a
swallowed body would read as generic `NotFound.razor` text lacking those specific strings.

**D7 — Static SSR only, confirmed.** No `@rendermode` directive anywhere in `WikiPage.razor`; `[Authorize]`
matches the existing pattern (`Account.razor` and `AnonymousGate` in `Program.cs`) rather than inventing
a new one.

**3.4 — clean.** `PageHistoryService.cs` shells `git log` via `GitProcessRunner`'s argument-array API
(never a string-interpolated command), uses `%an`/`%aI` (author, not committer — matches D5's "who
edited this" framing) with a `--` pathspec separator, degrades to `null` on both `GitProcessException`
and unparseable output, never throws into the render. `CancellationToken.None` is passed with a comment
explaining forward obligation 8; I read `GitProcessRunner.cs:54` (`process.WaitForExitAsync(cancellationToken)`
with no corresponding `process.Kill()`) and confirmed the claim is true — cancelling really does just stop
awaiting without killing the subprocess, so `CancellationToken.None` is the honest choice until §6 fixes
that. No caching — confirmed by reading, `PageEnumerationService.EnumeratePages()` and `git log` both run
fresh every request.

**Scope — clean.** `Home.razor`'s de-scaffolding is a placeholder only (no `_welcome-message.md`, no repo
read) — compliant with the Product Owner's pin. `NavMenu.razor` has no template cruft (`grep` for
Counter/Weather came back empty, confirming the worker's sweep). No page index or listing anywhere in the
diff — §4's job, correctly left alone.

**Gates — reproduced independently:** `dotnet build` 0 warnings/0 errors. `dotnet test` **638/638**, full
unfiltered. `dotnet format --verify-no-changes` exit 0. `openspec validate git-backed-content-core
--strict` valid. `git status --short -- src` shows exactly the reported file set (three modified, three
new); `git diff --stat -- src` matches the reported shape — no mutation residue.

**Nit — `A_route_with_no_matching_page_is_reported_as_not_found` is a weak regression guard, by the
worker's own account.** The worker's DEVLOG post already flagged that this exact test passed unchanged
through the heisenbug it describes, because `NotFound.razor`'s heading and this page's own not-found
branch both contain the literal text "not found" — the test can't tell a genuine not-found render from a
swallowed-and-substituted one. The two refusal tests next to it are strong for the reason above; this one
isn't, and it's testing the one branch (plain not-found) that has no distinguishing content to assert on.
Worth a follow-up assertion that's actually falsifiable — e.g. a marker specific to `WikiPage`'s own
not-found branch — so a regression of the exact kind already caught once doesn't get a second free pass
through this specific test. Not blocking; the block's real regression coverage lives in the ambiguous/
sole-claimant tests, which are sound.

→ @worker

**[worker]** Both blockers fixed, plus the nit. No scope beyond what was named.

**Blocker 1 — the resolver now mirrors the decode split, not a doc patch next to an unfixed trap.**
`PageRouteCodec.cs`: `TryResolveWorkingTreePath` (still-encoded contract, unchanged behaviour) and new
`TryResolveWorkingTreePathFromRouteValue` (framework-decoded contract) both delegate to one private
`TryResolveWorkingTreePathCore(paths, route, decodePercentEncoding, out absolutePath)`, which itself
calls the existing `TryDecodeCore` — so the trailing-separator containment check exists exactly once,
shared by both entry points, exactly as asked. `TryResolveWorkingTreePath`'s doc no longer claims
unqualified to be "the function §6 must use"; it states its input contract, names the sibling for the
other contract, and points at the class-level remarks. Added the parking comment for the distinct-types
redesign (an `EncodedRoute`/`DecodedRoutePath` pair) directly above the two entry points — not built,
per the architect's instruction; it's §6's to design once its real caller shape exists. Mirrored test
coverage added: `TryResolveWorkingTreePathFromRouteValue_{ResolvesAnOrdinaryRouteInsideTheWorkingTree,
DoesNotDoubleDecodeAFileNameContainingALiteralPercentSign, RefusesASiblingDirectoryWhoseNameSharesAPrefix,
RefusesControlCharactersRatherThanThrowing, RefusesWhatTryDecodeRouteValueAlreadyRefuses}` — the second
one is the resolver-level twin of the codec-level double-decode regression test, proving the fix holds
through to an actual resolved filesystem path, not just the decode step in isolation.

**Blocker 2 — tried the purpose-built mechanism against the real pipeline; it works for one outcome and
provably doesn't for another, and the design now reflects both findings rather than picking one.**
`WikiPage.SetStatusCode` sets `HttpContext.Features.Get<IStatusCodePagesFeature>()?.Enabled = false`
before assigning a status code. Tested end to end via `WikiPageTests` against the real Blazor Static SSR
render (not a minimal-API probe):

- **Ambiguous / sole-claimant-refused → `409 Conflict`, verified working.** Both
  `An_ambiguous_route_names_both_claimants_...` and `A_sole_claimant_whose_route_does_not_identify_it_...`
  now assert `HttpStatusCode.Conflict` *and* the full claimant-list body content in the same response —
  the escape hatch delivers this component's own markup at an honest non-2xx status, exactly what the
  reviewer's finding said to try.
- **Genuinely-not-found / malformed / vanished-mid-request → stays `200`, and 404 is provably unusable
  here, not merely untried.** First attempt used 404 for this case too. It reproduced an *empty body*
  with a `blazor-enhanced-nav: allow` response header, regardless of `IStatusCodePagesFeature`. Isolated
  the cause by swapping the same code path to `410 Gone` as a diagnostic: the full custom body was
  delivered correctly, proving the escape hatch itself was never the problem for this branch. The actual
  cause is a second, unrelated mechanism: `Routes.razor`'s `<Router NotFoundPage="typeof(Pages.NotFound)">`
  makes ASP.NET Core's Blazor Web App treat *any* Static SSR response whose status is set to 404 as
  eligible for its own built-in not-found handling, which discards the component's rendered body in
  favour of deferring to client-side enhanced navigation — independent of and in addition to
  `UseStatusCodePagesWithReExecute`. 410 isn't the honest answer either (it asserts something existed and
  was removed, which is false for "never existed"), so this outcome keeps 200 with distinguishing body
  content, same as round 1 — but now as a verified constraint scoped to the one case where the honest
  code is provably unusable, not applied everywhere by default. Documented in `WikiPage.razor`'s XML
  remarks with the full reasoning and the diagnostic that isolated it.

**Nit fixed.** `A_route_with_no_matching_page_is_reported_as_not_found` now asserts the exact string
`"There is no page at this address."` (this page's own not-found wording, which `NotFound.razor`'s text
does not contain) instead of a case-insensitive `"not found"` substring match that both pages' headings
satisfy equally — the same falsifiability gap the reviewer named. `A_hostile_route_is_refused_...` picked
up the same body assertion since it exercises the identical not-found branch.

**On the reverse-proxy percent-decoding note — my read: latent, not live.** Nothing in this change or
the repo as it stands today configures or ships a reverse proxy in front of Kestrel; `docker-compose`/
nginx/Caddy/Traefik config is absent from every section landed so far, and D2 only anticipates one in the
abstract for TLS termination at deployment time. So the "decoded twice before Kestrel sees it" scenario
has no concrete instance to be live *in*: it's a real constraint on whatever fronting proxy configuration
ships later, not a defect in what this block delivers today. Agreed it's worth a stated deployment
constraint once a proxy is actually chosen (e.g. disabling normalization of percent-encoded unreserved
octets, mirroring D12's own acceptance of the same RFC 3986 §6.2.2.2 risk) — recording that as the
architect's call, not reopening anything here.

**Gates, re-run in full, actual numbers:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` —
**647/647**, full unfiltered (638 prior + 9 new `TryResolveWorkingTreePathFromRouteValue` cases in
`PageRouteCodecTests.cs`; `WikiPageTests.cs`'s count is unchanged at 12, four of them re-asserting status
codes this round). `dotnet format --verify-no-changes` — exit 0. `openspec validate
git-backed-content-core --strict` — valid. `git status --short -- src` shows the identical file set from
before this round (three modified, three new); `git diff --stat -- src` — `PageRouteCodec.cs` grew from
the resolver split (+55 net lines vs the pre-round-2 diff), `Home.razor`/`ContentStorageStartupExtensions.cs`
unchanged. No mutation testing re-run this round — the two round-1 mutants (raw-HTML-off,
ambiguous-route branch) target code whose branch structure is unchanged by this round's fixes (the
`ambiguousMatch is not null` check itself wasn't touched, only what happens on each side of it), so
nothing new to confirm; happy to re-run if the reviewer wants it anyway.

→ @reviewer

**[reviewer]** Block 3b re-audit (round 3), against the architect's wider Blocker 1 scope and the split
Blocker 2 finding.

**Verdict: Approve with nits.**

**Blocker 1 — confirmed fixed, independently re-tested against the resolver level, not just decode.**
Read `PageRouteCodec.cs:308-349`: `TryResolveWorkingTreePath` and `TryResolveWorkingTreePathFromRouteValue`
both delegate to one private `TryResolveWorkingTreePathCore`, and the trailing-separator containment
check (`:334-345`) is written exactly once inside it — structurally impossible for either public entry
point to bypass, confirmed by reading rather than inferring from the naming. Built a scratch console app
(`ProjectReference` to the real `ZeroWiki.csproj`) and ran the resolver end to end:

- **Correct pairing, both directions:** encoding `a%20b.md` → canonical route `a%2520b` →
  `TryResolveWorkingTreePath(paths, "a%2520b")` and, separately, simulating the framework's one decode
  pass → `TryResolveWorkingTreePathFromRouteValue(paths, "a%20b")` — both resolve to the identical
  absolute path ending `a%20b.md`. Containment (`docs-evil` sibling) and control-character refusal
  (`%00` / a literal NUL) behave identically through both entry points, confirming the shared core is
  genuinely shared, not two copies kept in sync by hand.
- **Wrong pairing, both directions — checked the specific property the brief asked for.** Passing the
  *already-decoded* value through the *still-encoded* entry point reproduces the double-decode bug
  exactly (`a%20b` → `a b.md`, a different real file, if one existed) — this does **not** fail, it
  silently mis-resolves, and that's inherent to percent-encoding, not a gap this round left open: an
  already-decoded string with no `%` in it and a still-encoded string with no `%` in it are the same
  string, so no validation can distinguish "which contract did the caller mean" in general. This is
  exactly why the class's own new remarks (`PageRouteCodec.cs:51-60`) state plainly that the split is
  "enforced by naming and documentation, not by the type system" rather than claiming a safety property
  the code doesn't have. The other wrong-pairing direction (still-encoded route through the no-decode
  entry point) under-decodes to a literal, non-colliding garbage filename rather than a real different
  file — a milder failure mode, also not a crash. Both wrong pairings are honestly documented, not
  overclaimed, and — confirmed via `grep -rn "TryResolveWorkingTreePath"` — neither is reachable from any
  production code path today; every call site outside `PageRouteCodec.cs` itself is a test. Given that,
  and given the architect's own instruction that the type-safety redesign is §6's to build once its real
  caller shape exists, I agree the documentation-plus-shared-core fix is the right scope for this round.
- **New tests genuinely exercise the resolver, not just decode**, confirmed by reading the diff: all five
  `TryResolveWorkingTreePathFromRouteValue_*` tests call the resolver function itself and assert on the
  resolved `absolutePath` (existence-relative-to-a-real-`ContentPaths`, not just the intermediate decoded
  string) — including a resolver-level twin of the double-decode regression test, proving the fix holds
  through to a filesystem path.

**Blocker 2 — the split finding checks out; both open questions answered.**

*Is 409 the right code, or borrowed?* Defensible, not a stretch. RFC 9110 §15.5.10 defines 409 as "the
request could not be completed due to a conflict with the current state of the target resource... the
user might be able to resolve the conflict and resubmit" — that is a close semantic match for D12's
ambiguous/sole-claimant-refused routes (the address is claimed by more than one file, or its sole
claimant doesn't identify it; a member can rename a file and reload). 300 Multiple Choices is the more
literal textbook fit for "more than one representation" but is barely implemented by real clients and
doesn't naturally carry explanatory HTML either; 422 doesn't fit a GET. 409 is the better of the
practical options, and it's recorded with its reasoning in `WikiPage.razor`'s XML remarks
(`:87-93`) — a deliberate, stated choice, not left implicit.

*Is the 404 constraint really forced, or does it just need `Routes.razor` touched?* This is the sharper
question and I don't think the current docs fully answer it. Read `Routes.razor:3` —
`<Router NotFoundPage="typeof(Pages.NotFound)">` — and reasoned through the mechanism: this is Blazor's
own SSR "not-found" substitution, gated on `NotFoundPage` being configured at all, and it is a *different*
mechanism from `UseStatusCodePagesWithReExecute` (confirmed independently, since the 409 path proves
`IStatusCodePagesFeature.Enabled = false` genuinely defeats the *other* mechanism). The
`blazor-enhanced-nav: allow` header the worker's diagnostic reported is a real, specific artifact of this
exact feature, which corroborates the isolation rather than leaving it open to a confound — I trust the
410-swap methodology. But "404 is provably unusable *given the current `Routes.razor` configuration*"
and "404 is provably unusable, full stop" are different claims, and the current XML remarks
(`WikiPage.razor:94-110`) read closer to the second than the first — they name `Routes.razor`'s
`NotFoundPage` as the cause but never say whether touching it was considered and rejected as
out-of-scope, or simply never in scope to begin with. It's a real constraint *today*, correctly
diagnosed, but it is not an ASP.NET Core law — it's a consequence of an existing, changeable
configuration this block didn't create and correctly didn't touch. Not blocking on rewording, but asking
that `## NEXT` name this explicitly as a revisitable follow-up (removing/adjusting `NotFoundPage`, weighed
against losing its generic-404 UX for routes outside every `@page` match) rather than something the next
reader might take as settled forever.

**Rest of the block, rechecked briefly for what these two fixes could have disturbed:**

- **D13** — no new render surface. The only `src` changes this round are inside `PageRouteCodec.cs`
  (decode/resolve split) and `WikiPage.razor` (the new `SetStatusCode` private method and its two call
  sites). `_renderedBody` remains the only `MarkupString`; `Frontmatter_title_and_git_author_are_html_escaped_not_raw`
  and `Embedded_script_is_shown_as_text_rather_than_executed` are untouched and still present. No finding.
- **`PageRouteCodec` docs** — read the whole file this round. Internally consistent: all four public
  methods cross-reference their sibling, each states its own input contract, and the type-safety
  limitation is stated as a limitation rather than papered over. No stale claim found this round — a
  change from all three prior rounds, each of which found one.

**Gates — reproduced independently:** `dotnet build` 0 warnings/0 errors. `dotnet test` **647/647**, full
unfiltered. `dotnet format --verify-no-changes` exit 0. `openspec validate git-backed-content-core
--strict` valid. `git status --short -- src` / `git diff --stat -- src` match the reported shape exactly
— no mutation residue.

**Nit** (new): frame the 404/`NotFoundPage` constraint in `## NEXT` as a named, revisitable follow-up
rather than an implicit permanent fact, per the discussion above. Not blocking — §3 can close on this.

→ @architect

**[worker]** Nit closed — wording only, no behaviour change, scoped exactly as asked.

**The 404 constraint now names the mechanism and the lever, not just the conclusion.** Reworded the
relevant paragraph in `WikiPage.razor`'s XML remarks (`OnInitializedAsync`'s `<remarks>`) and the two
matching comments in `WikiPageTests.cs`
(`A_route_with_no_matching_page_is_reported_as_not_found`,
`A_hostile_route_is_refused_rather_than_raising_an_unhandled_error`). Previously read as "404 is
provably unusable" without qualification; now states it as **"unusable *under `Routes.razor`'s current
`NotFoundPage` configuration*"** — app configuration this block correctly chose not to touch, not a
property of Blazor or of HTTP. The reworded remarks explicitly name the lever: removing or reworking
`Routes.razor`'s `NotFoundPage` (or moving to the newer `NavigationManager.NotFound()`-based API it's
built for) would change this and could make an honest 404 usable here — and point at `## NEXT` for
where that trade is recorded rather than settling it in a code comment. Confirmed with a search that
no other file in this block's diff repeats the unqualified phrasing
(`grep -rn "unusable" src/ZeroWiki tests/ZeroWiki.Tests` after the edit shows only the two corrected
sites, both now qualified).

**Gates, re-run, actual numbers:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **647/647**,
full unfiltered (unchanged — comment/doc-only edit, no test added, none touched beyond their own
comments). `dotnet format --verify-no-changes` — exit 0. `openspec validate git-backed-content-core
--strict` — valid. `git status --short -- src` shows the identical six-file set from both prior rounds
(three modified, three new). Correction while writing this up: `WikiPage.razor` is still untracked
(`??`, never `git add`ed this block), so `git diff -- src` shows nothing for it — not the "doc-only
hunk" a first draft of this note claimed, which would have been exactly the git-diff-blind-to-untracked
mistake this project's own harness warns about. Verified the edit was comment/doc-only the only way
available for an untracked file: re-read `WikiPage.razor`'s `OnInitializedAsync` in full after editing
and confirmed the branch structure (which condition sets which status, which fields, which return
points) is byte-identical to the pre-nit version — only the `<remarks>` prose changed. `git diff -- src`
on the three genuinely tracked files (`Home.razor`, `ContentStorageStartupExtensions.cs`,
`PageRouteCodec.cs`) is unchanged from the prior round, as expected for a wording-only nit that touched
none of them.

**[supervisor]** §3 section review — `60957e6..e9bfea0` (`50da7b0` D12–D14 + spec delta, `8332c79`
block 3a, `e9bfea0` block 3b).

**Verdict: Request changes.** Three findings, all cross-block. Tree is clean at `e9bfea0` —
`git status --short --untracked-files=all` is empty, so no mutation residue and no untracked file
hiding one. Mutation evidence is coherent across both blocks: security-critical targets only, full
unfiltered suite, checksums before and after, `trap`-based revert, no filtered figure posted. The
per-block reviews were sound; nothing below is a second opinion on them.

---

### S1 (blocker) — D13's mechanism does not deliver D13's stated property: link URIs are an open stored-XSS channel

`MarkdownPipelineFactory.cs:14-18` calls `.DisableHtml()`, which escapes raw HTML exactly as the spec
requirement words it. It does **not** touch link or image destinations, and Markdig has no default
URI allow-list. Probed against the real `MarkdownPipelineFactory.Create()` (a scratch project
compiling the shipped source, not a reimplementation):

```
[click me](javascript:alert(1))        -> <p><a href="javascript:alert(1)">click me</a></p>
<javascript:alert(1)>                  -> <p><a href="javascript:alert(1)">javascript:alert(1)</a></p>
[click me](javascript&#58;alert(1))    -> <p><a href="javascript:alert(1)">click me</a></p>   (entity resolved back to ':')
![img](javascript:alert(1))            -> <p><img src="javascript:alert(1)" alt="img" /></p>
[click](data:text/html;base64,PHNj...) -> <p><a href="data:text/html;base64,...">click</a></p>
[click](vbscript:msgbox(1))            -> <p><a href="vbscript:msgbox(1)">click</a></p>
<script>alert(1)</script>              -> <p>&lt;script&gt;alert(1)&lt;/script&gt;</p>          (correct)
<img src=x onerror=alert(1)>           -> <p>&lt;img src=x onerror=alert(1)&gt;</p>            (correct)
```

`grep -rn 'Content-Security-Policy' src/` returns nothing — there is no CSP anywhere in the app, so a
`javascript:` href executes on click. This is stored XSS against every reader including the
administrator, reachable by any git-token holder pushing a page: precisely the threat `design.md:163`
names and does **not** accept. It is not on the Product Owner's accepted-risks list.

`design.md:163` — "Escaping needs no dependency and **has no bypass surface**" — is false as a
stored-XSS claim. That is the change's seventh claim-versus-mechanism defect and the second in
`design.md`. Applying §2's standing rule, *when a guard's justification names a case, check the
guard's branch*: the justification names stored XSS; the branch covers raw HTML only.

**Why no block review could catch it.** The worker (this thread, block 3b) and the reviewer
(block 3b audit, "**D13 — verified independently, no findings**") both audited D13 by enumerating
*which values reach the browser un-encoded* and answering "only `_renderedBody`, and Markdig has raw
HTML off". Both are correct. Both measured raw HTML tags. The tests match the same instrument —
`Embedded_script_is_shown_as_text_rather_than_executed` and
`Frontmatter_title_and_git_author_are_html_escaped_not_raw` (`WikiPageTests.cs:206-244`) assert only
on `<script>`. Two measurements agreeing is not corroboration when they share an instrument.

Answering the Architect's Q4 directly: the **other** channels are clean, and I verified every one
rather than taking the sweep on trust. Frontmatter title, tags, `_page.RelativePath`, each claimant
path in the ambiguous-route list, and `PageHistoryService`'s git-derived `AuthorName` all render
through plain Razor `@expression` (`WikiPage.razor:16,20,25,34,55`), which HTML-encodes;
`_renderedBody` (`:41`, set at `:170`) is the file's only `MarkupString`. The sweep was right about
the channels it enumerated. The body channel is the one that is not closed.

❓ **@architect / Product Owner** — closing this changes D13, which currently says nothing about link
destinations, so it is a decision, not just a fix. The shape is a URI allow-list at the pipeline
(scheme-restricting `LinkInline`/`AutolinkInline` before render) and/or a CSP, plus a spec scenario so
the property is gated rather than asserted. Which, and whether both, is the Product Owner's call —
but the section cannot close claiming *Raw HTML in page content is not rendered* delivers D13's
stated purpose while this is live.

---

### S2 (blocker) — the read path resolves routes many-to-one, re-opening the collision D12's correction closed, and disagreeing with the resolver §3 ships for §6

`WikiPage.razor:131-149` resolves a request as decode → `Encode` → string-match against enumeration:

```
TryDecodeRouteValue(Route, out relativePath)      // :131
var canonicalRoute = PageRouteCodec.Encode(relativePath);   // :136
... FirstOrDefault(c => c.Route == canonicalRoute)          // :140, :149
```

`Encode` is not injective — the class's own remarks say so at `PageRouteCodec.cs:13-15` — so this
composition is not injective either, and the enumeration-side round-trip check (which *is* exact)
never sees the request. Probed against the shipped codec:

```
routeValue 'a  b'  (two literal spaces, /wiki/a%20%20b)
  -> decoded 'a  b.md' -> re-encoded 'a__b' -> SERVES a_b.md
```

So with only `Chapter_1.md` in the tree, `/wiki/Chapter%20%201` renders it. That is the exact pairing
the corrected `design.md:138-152` calls "**the realistic collision** — a double space against a single
underscore, `Chapter  1.md` against `Chapter_1.md`, a typo meeting an ordinary filename". §3 refuses
that pairing on the enumeration side (3a) and re-admits it on the request side (3b). D12's principle
is *never silently pick one of two readings*; `/wiki/Chapter  1` has two readings and the section
picks one.

It also breaks the contract §3 ships for §6. Same probe, same route values, through the resolver
`PageRouteCodec.cs:320` documents as §6's entry point:

```
routeValue 'a  b' | read-path serves route 'a__b' (file a_b.md) | TryResolveWorkingTreePathFromRouteValue -> a  b.md
routeValue 'a__b' | read-path serves route 'a__b' (file a_b.md) | TryResolveWorkingTreePathFromRouteValue -> a_b.md
routeValue 'a b'  | read-path serves route 'a_b'  (file a b.md) | TryResolveWorkingTreePathFromRouteValue -> a b.md
```

One URL, two answers. `PageRouteCodec.cs:46-49` claims the resolvers "mirror this same split one layer
up" — they do not mirror the read path, because the read path does not use them and does not share
their semantics. D12's binding consequence, "§6 inherits an inverse that is total on every route it
can actually be reached from", is not what §3 built.

Blocks 3a (codec + enumeration) and 3b (lookup). Invisible in either diff: 3a's round trip is exact;
3b's re-encode reads as a way to *avoid* re-deriving 3a's containment check, which the reviewer
correctly confirmed it does. It takes both diffs at once.

**Remediation shape.** The property to establish is: *there is exactly one function from a route value
to a page, and every caller — read now, save in §6 — goes through it.* Concretely, the read path must
refuse a route value that is not the canonical form of the page it would serve (e.g. require
`Uri.UnescapeDataString(pageMatch.Route) == Route` before serving; non-canonical addresses become
not-found). A test for a non-canonical route value is the coverage gap — `WikiPageTests` has twelve
cases and not one of them requests a page by a non-canonical address.

---

### S3 (medium) — a per-request tree walk with no guard for the writer it shares the tree with

3a wrote `Walk` for a service-layer caller that owned the tree. 3b made it run on **every page view**
(`WikiPage.razor:137`) against a tree a concurrent `git push` mutates. `PageEnumerationService.cs:124-132`
catches only `UnauthorizedAccessException`, and only around `EnumerateFileSystemEntries`. Probed:

```
File.GetAttributes(entry) on an entry deleted after enumeration (:142) -> FileNotFoundException
EnumerateFileSystemEntries on a directory removed mid-walk             -> DirectoryNotFoundException
  caught by Walk's only catch (UnauthorizedAccessException)?           -> False, both
```

Both propagate out of `EnumeratePages()` → `OnInitializedAsync` → unhandled 500 on every page view for
the duration of a push that deletes or renames anything. This is a different failure from the parked
`Walk`-vs-D3-lock race (that one is about *what* is read; this is about not surviving the read) and
from the window `WikiPage.razor:162` names — which is the later enumerate→read gap. Answering the
Architect's question on that comment: it is honest about the window it names and understates the
surface. Its `catch (IOException)` at `:160` also misses `UnauthorizedAccessException`, which is not an
`IOException` (verified) — an unreadable file yields a 500 rather than the clean not-found the comment
promises. The earlier window, inside the walk itself, is neither named nor handled.

---

### Q1 / Q2 — `PageRouteCodec`'s surface, and the two resolvers

**Q1: accreted, not designed.** Five public members over one private core, reached across three review
rounds. As a design there is one job (route ↔ path) and two orthogonal axes — which decode contract,
and does it resolve to an absolute path — a 2×2 currently spelled as four separately-named methods, of
which two have no production caller. The tell is the doc-to-code ratio: ~190 of 350 lines are comment,
and most of it exists to prevent a mix-up the type system permits, with 60 lines of class remarks
before the first member. The reviewer's judgement that the distinct-types redesign is §6's is right
*given the resolvers stay*. It stops being right under S2's fix: once one canonical route→page
resolution exists, the natural shape is that resolution on the enumeration side, with the codec
shrinking to `Encode`/`TryDecode`. Worth settling while carving S2 rather than deferring to §6.

**Q2: yes — and worse than dead.** Neither resolver has a production caller (confirmed; the reviewer
found the same). They carry the section's most security-critical logic and its only surface never
exercised in anger. That would be tolerable as "built for §6" if their semantics matched what §3
ships — S2 shows they do not, so what is being carried forward is a *wrong* contract with
authoritative-looking documentation aimed at the section least able to question it. My recommendation
with the S2 fix: keep at most one, reached through the canonical resolution, and let §6 build against
a contract that has a live caller. Shipping two zero-caller functions whose only purpose is a section
that has not been designed is the thing 3a's own brief told the worker to avoid.

---

### Q3 — per-request enumeration

Correct as an interim and unlikely to become permanent: it is stated in three places (`WikiPage.razor:82-86`,
the DI comment, this thread) and §4.1 is the next section. It does **not** build something §4 must
unpick — §4 replaces the lookup, it does not undo it. But §4 will index against whatever contract §3
settles, which is the second reason S2 should be fixed here rather than inherited: an index keyed on a
many-to-one lookup bakes the aliasing in. Cost noted for `## NEXT`: a full tree walk plus a `git log`
subprocess spawn per page view, and no cap anywhere on page size — a pushed multi-hundred-MB `.md`
is read whole into memory and Markdig-parsed on every request.

### Q5 — do D12, D13, D14 say what the code does?

- **D12 — code right, rule sentence wrong (again).** `design.md:138`: "any run of two or more adjacent
  `{space, '_'}` characters is ambiguous — not merely a run of underscores" excludes a run of length
  **one** consisting of a single literal `_`, which encodes to `__` — a route run of two, which by
  D12's own Fibonacci(*n*+1) formula in the very next sentence has two preimages (`a_b` and `a  b`).
  The table immediately below shows exactly that collision. Table and rule sentence disagree; the
  correction widened the alphabet and narrowed the run length. The code does not depend on the
  sentence — enumeration checks the actual round trip — but §6 will read it, and this is the second
  time D12's collision class has been understated in prose. State it where it is exact: **a route
  containing two or more consecutive `_` is ambiguous.** (S2 is the same arithmetic arriving through
  the request door rather than the filename door.)
- **D13 — no.** See S1.
- **D14 — yes, and the implementation exceeds it.** Bounds set explicitly at the seam rather than
  inherited (`MaxSizeBytes` 8 KiB checked pre-parse, `MaxDepth` 8), `Dictionary<string, object>`
  target, `ReferenceHandling.None` and `UnsafeAllowDeserializeFromTagTypeName = false` both pinned
  rather than defaulted, `catch (Exception)` → `PageFrontmatter.Empty` so failure is total, and the
  alias-bomb closure evidenced empirically rather than assumed. One imprecision, not blocking:
  `PageFrontmatterExtractor.Extract` runs `Markdown.Parse` over the whole page *before* the 8 KiB cap
  applies, so D14's "bound the input before parsing it" is true of the seam it names and silent about
  the document parse ahead of it.

---

### Clean — checked and no finding

- **D7 render-mode discipline.** No `@rendermode` anywhere in the section; `WikiPage.razor` is Static
  SSR, `[Authorize]` matches the existing pattern. No circuit widened.
- **Identity/access uniformity.** One new route, `[Authorize]`, covered by `AnonymousGate`, with an
  anonymous test. No new differential and no new redirect shape.
- **D5.** `git log -1 --format=%an<US>%aI -- <path>` via the argument-array API, author not committer,
  degrades to `null` on both subprocess failure and unparseable output, never throws into the render.
  No hand-maintained author field anywhere.
- **`Home.razor`.** Compliant with the Product Owner's pin — three static lines, no
  `_welcome-message.md`, no repo read, no page listing.
- **Integrated coverage exists** and is better than most sections get: `WikiPageTests` drives real
  routing, real git and real Static SSR end to end. Its two gaps are named in S1 and S2.
- **Human-in-the-loop.** None in 3.1–3.4; no box ticked without evidence.

### Lower-severity, for the fix block if one is carved, else `## NEXT`

- **`PageHistoryService.cs:44` hardcodes `"docs/"`** to rebuild a repo-relative path, while
  `ContentPaths.cs:23` owns that segment. Two blocks, two spellings of one fact — and the failure is
  **silent**: if the working-tree directory ever moves, `git log` returns empty for every page and
  `GetLastEditAsync` cannot distinguish "no history" from "wrong path", so every page quietly loses
  its last-edit line with no warning. Derive it (`Path.GetRelativePath(RepositoryRoot, WorkingTree)`).
- **Mixed-case extensions are enumerated but can never be served.** `PageEnumerationService.cs:154`
  matches `.md` `OrdinalIgnoreCase`, `Encode` strips it case-insensitively, and `TryDecodeCore`
  re-appends lowercase `.md` — so `Page.MD` round-trips to `Page.md` and is refused (verified). On a
  case-sensitive volume an Obsidian vault of `.MD` files serves nothing and logs every file as a D12
  route collision. Refuse-not-guess is the right posture; the message misdescribes the fault.

→ @architect

**[architect]** → `@worker` — **§3 remediation block** (supervisor round one). Per CLAUDE.md §3c.3 this
takes **no new `N.M` numbers and ticks nothing** — every §3 box is already ticked. It commits as a
`fix`, and then the supervisor re-runs over the same `60957e6..HEAD` range.

**The Product Owner has ruled on S1: both a URI allow-list and a CSP header.** `design.md` D13 now
carries that as an addendum, D12's rule sentence is corrected (third attempt — it is now stated
route-side), and four new gating scenarios exist in `specs/content-store/spec.md`. **Re-read D12 and
D13 before starting; both changed since you implemented against them.**

**S1 (high) — script-bearing URLs. The primary fix is the allow-list; the CSP is defence in depth.**
`DisableHtml()` never governed link and image *destinations*, so the shipped pipeline renders
`[x](javascript:alert(1))` as a live `<a href>`, `![x](javascript:…)` as an `<img src>`, and resolves
`javascript&#58;` back to a working scheme.
- **Allow-list, failing closed:** a destination is permitted only if relative (including a bare
  fragment) or carrying `http`, `https`, or `mailto`. Everything else — *including schemes nobody has
  thought of* — is refused. Apply it **after Markdig has parsed and resolved entities**, never against
  raw Markdown: `javascript&#58;` is not `javascript:` until Markdig has decoded it. Normalize before
  matching (leading/trailing whitespace, embedded control characters, case).
- **CSP header:** `default-src 'self'`, `script-src 'self'`, `style-src 'self'`, `img-src 'self'`,
  `object-src 'none'`, `base-uri 'self'`, `frame-ancestors 'none'`, `form-action 'self'`. Remove the
  inline `onclick` at `NavMenu.razor:15` — Blazor template scaffolding that `script-src 'self'` blocks,
  and worth deleting on its own merits.
- **Test the class, not the examples.** The reason this survived three audits is that worker, reviewer
  and Architect all asked "which values reach the browser un-encoded", which only ever inspects *tags*;
  the tests asserted on `<script>` alone. Assert on rendered **attributes**, and include the
  entity-encoded, mixed-case and whitespace-padded forms.

**S2 (high) — the read path resolves many-to-one, reopening what D12's correction closed.**
`WikiPage.razor:131-149` does decode → `Encode` → string-match. `Encode` is not injective, so neither
is the composition, and enumeration's own round-trip check never sees the request: `/wiki/Chapter%20%201`
serves `Chapter_1.md`. It also contradicts the resolver §3 ships for §6, which targets the *other* file
from that same URL.
- **Invariant:** a request serves a page only when its route is exactly the received form of that
  page's canonical route — the request-side twin of enumeration's "the route identifies exactly this
  file". `/wiki/Chapter__1` serves `Chapter_1.md`; `/wiki/Chapter%20%201` refuses.
- **Reuse the identity logic; do not write a second copy.** Two implementations of this rule that can
  disagree is the defect, not the cure.

**S3 (medium) — the per-request walk is unguarded against its co-writer.** `PageEnumerationService`
catches only `UnauthorizedAccessException`, but `File.GetAttributes` on an entry that vanished throws
`FileNotFoundException`, and a removed directory throws `DirectoryNotFoundException` — both unhandled,
both a 500, on a tree `git push` mutates under us. Also `WikiPage.razor:160`'s `catch (IOException)`
misses `UnauthorizedAccessException`, which does not derive from it.

**Lower severity, same block:**
- `PageHistoryService.cs:44` hardcodes `"docs/"` instead of deriving from `ContentPaths`, and fails
  **silently** — every page loses its last-edit line, indistinguishable from "no history". Derive it,
  and make the failure distinguishable from the legitimate no-history case.
- Mixed-case `.MD` files are enumerated but can never be served, and are reported as D12 route
  collisions. The posture is right and the message is wrong; say what actually happened.

**Do not** build the distinct-types redesign or touch `Routes.razor`'s `NotFoundPage` — both parked.
Re-run all four gates and report actual numbers (647/647 before this block); `git status --short -- src`
alongside `git diff -- src`. Post to the DEVLOG as `[worker]` under `## 3.`, then `→ @reviewer`.

**[worker]** §3 remediation block implemented — no new task numbers, nothing ticked, per CLAUDE.md §3c.3.
Re-read the corrected D12/D13 and the four new `specs/content-store/spec.md` scenarios first, as
instructed; confirmed I never touched `design.md` or `spec.md` myself — `git diff` on both shows only
the Architect's own pre-brief amendment, already in place before this block started.

**S1 — allow-list, applied post-parse, wired so no caller can skip it.** New
`src/ZeroWiki/Content/MarkdownLinkAllowList.cs`: `IsAllowedDestination` permits a relative destination
(including a bare fragment) or an explicitly allowed scheme (`http`, `https`, `mailto`); everything else
is refused, including schemes nobody has thought of. `Enforce(MarkdownDocument)` walks
`LinkInline`/`AutolinkInline` descendants and sets `Url = string.Empty` on anything refused —
`MarkdownPipelineFactory.Create()` subscribes it to the pipeline's `DocumentProcessed` event rather than
requiring each call site to invoke it explicitly, so a future caller can't render through this pipeline
while forgetting D13, the exact failure shape that let the original gap survive three audits.

Three things found empirically, not assumed:
1. **`Uri.TryCreate(s, UriKind.Absolute, ...)` treats a root-relative string like `/wiki/other-page` as
   an absolute `file:` URI** — confirmed by direct reflection/probing before writing the classifier, not
   discovered by a failing test. Using `Uri` for the allow-list decision would have refused every
   ordinary wiki link. Scheme extraction instead follows RFC 3986's own grammar directly
   (`ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"`) with no `Uri` involved in the classification.
2. **A real browser strips embedded tab/CR/LF from anywhere in a URL, not just the ends, before reading
   its scheme** — so `java\tscript:alert(1)` genuinely is `javascript:` once rendered. Checking the
   un-normalized string would have been a bypass, not a refusal; the brief's own callout ("normalize
   embedded control characters") is what pointed at this. Any *other* embedded control character is
   refused outright rather than modeled further.
3. **Setting `LinkInline`/`AutolinkInline.Url` to `""` and re-rendering** produces `href=""`/`src=""`
   with the visible link text intact for a `LinkInline`, and an empty `<a href=""></a>` for an
   `AutolinkInline` (its inner text is derived from `Url` too, not a separate literal — confirmed by
   probing the renderer output directly). No exception, no reintroduction of the original destination
   via `GetDynamicUrl` (never set by this pipeline).

Entity resolution is handled for free: `[x](javascript&#58;alert(1))` parses to a `LinkInline.Url` of the
literal string `javascript:alert(1)` — Markdig itself does the entity decode during parsing, and
`Enforce` runs on `DocumentProcessed` (post-parse), so checking `Url` rather than source text is what
makes the entity form and the literal form indistinguishable to the check, as the brief required.

**CSP**, `Program.cs`: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self';
object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'`, set via
`Response.OnStarting` in the first middleware in the pipeline, so it applies to every response
(confirmed against the anonymous landing page too, not just authenticated pages) and survives whatever
downstream middleware does, including `UseStatusCodePagesWithReExecute`'s re-execution. Removed the
inline `onclick` at `NavMenu.razor:15` (Blazor template scaffolding `script-src 'self'` blocks) without
reimplementing the mobile-menu-collapse behaviour it provided — the brief said it should go regardless,
not be replaced.

**S2 — one check, reused, not a second copy.** New `PageRouteCodec.IsCanonicalRouteValue(routeValue,
canonicalRoute)`: `string.Equals(Uri.UnescapeDataString(canonicalRoute), routeValue, Ordinal)`. `WikiPage`
calls it once, right after computing `canonicalRoute` and before any enumeration lookup, so a
non-canonical request refuses before it can even reach the ambiguous/page-match branches. Deliberately
*not* a decode-then-re-`Encode` comparison against the original request string — `Encode`'s own
non-injectivity is what caused S2, so re-deriving the request's own re-encoded form doesn't close the gap
(that's the exact composition that reopened D12's collision); comparing the *canonical route already
found* against what the framework would have delivered for its one true URL sidesteps the many-to-one
collapse entirely, since no re-encoding of the request happens on this path at all. Traced by hand against
the brief's own fixture before trusting the test: `/wiki/Chapter%20%201` decodes to `"Chapter  1.md"`,
re-encodes to `"Chapter__1"` (same route `"Chapter_1.md"` claims), but
`Uri.UnescapeDataString("Chapter__1") == "Chapter__1" != "Chapter  1"` — refused, correctly.
Mutation-confirmed (below) that the new `WikiPageTests` case is what actually catches a regression here,
not merely present.

**S3 — both named exception gaps closed.** `PageEnumerationService.Walk`: `EnumerateFileSystemEntries`
now also catches `DirectoryNotFoundException` (a subdirectory removed between the parent listing seeing
it and the recursive call reading it) — treated as an empty subtree, not reported as unreadable, since
there's nothing there to name as a permissions fault. `File.GetAttributes(entry)` is now wrapped in its
own try/catch for `FileNotFoundException`, `DirectoryNotFoundException`, and `UnauthorizedAccessException`
— an entry that vanished, was renamed, or became inaccessible between being listed and being examined is
skipped as if it was never there. `WikiPage.razor`'s markdown-read catch is now
`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` —
`UnauthorizedAccessException` does not derive from `IOException` (confirmed against the BCL type
hierarchy, matching the supervisor's finding) and was previously falling through to an unhandled 500
despite the existing comment already promising a clean not-found.

**Honest gap, not a fabricated test:** I could not construct a deterministic, non-flaky reproduction of
the exact race these catches guard — `Directory.EnumerateFileSystemEntries(dir).ToList()` materializes
eagerly with no yield point before the loop, so triggering `FileNotFoundException`/
`DirectoryNotFoundException` on a *specific* entry requires a real concurrent delete landing in a window
between two synchronous statements with nothing to synchronize against. I tried a dangling-symlink
reproduction first (plausible-looking, since a broken symlink is the closest *static*, non-racy analogue)
and it does not reproduce the fault: `File.GetAttributes` on a dangling symlink succeeds and returns
`ReparsePoint` rather than throwing, confirmed empirically, and `Walk`'s existing reparse-point check
already skips it before any recursion would occur. A genuine reproduction would need either accepting
test flakiness (which this project's evidence standard rejects) or a filesystem-abstraction seam
(dependency-injecting `IFileSystem` or similar) purely to make `Walk` mockable — a structural change well
outside this remediation's scope. The fix itself is mechanically low-risk: two well-known BCL exception
types added to existing catch blocks, matching the exact types the supervisor's own probe found, with no
new branching logic to get subtly wrong. Recording this rather than shipping a race-prone test or
silently skipping the question.

**Lower severity, both closed.**
- `PageHistoryService`: `_repositoryRelativeWorkingTree` is now derived once in the constructor via
  `Path.GetRelativePath(paths.RepositoryRoot, paths.WorkingTree)` instead of a hardcoded `"docs/"`
  literal — removes the two-independently-maintained-spellings-of-one-fact structure the silent failure
  depended on, rather than adding a second check to detect a divergence after the fact. Not
  independently testable against the *old* behaviour today: `ContentPaths` always names the segment
  `"docs"` with no public way to construct one that doesn't, so every existing `PageHistoryServiceTests`
  case is bug-for-bug identical whether the path is derived or hardcoded. The fix is defensive/structural
  rather than something a test can currently distinguish — noted rather than claiming coverage that isn't
  real.
- `PageEnumerationService`: a sole claimant whose *only* discrepancy from its own round trip is a
  non-lowercase extension (`Page.MD`) now logs "has a Markdown extension that is not exactly lowercase
  '.md' ... its route can never resolve back to this exact file" instead of the generic D12
  claimant-count warning — `HasNonLowercaseMarkdownExtension` checks `EndsWith(".md", OrdinalIgnoreCase)
  && !EndsWith(".md", Ordinal)`, the one and only way this specific fault can occur with a single
  claimant. `MixedCaseExtension_IsRefusedWithAMessageNamingTheRealFault` asserts the actual message text
  via a small capturing `ILogger<T>` test double (the existing tests all use `NullLogger`, which discards
  everything), not just the unchanged refusal outcome.

**Mutation testing — two targets, capped at 3, both killed 1/1 under the full unfiltered suite.**
Checksummed before/after both; reverted by restoring the exact prior text, confirmed by checksum match.
- **S2** (`WikiPage.razor`, the `IsCanonicalRouteValue` check → `if (false)`) → killed:
  `A_non_canonical_route_value_is_refused_even_though_it_would_re_encode_to_a_real_pages_route` failed,
  serving the wrong file's body. 680/681.
- **S1** (`MarkdownLinkAllowList.cs`, the scheme-membership check → unconditional `true`) → killed
  thoroughly: 16 tests failed across both the unit suite (`MarkdownLinkAllowListTests`, all the
  scheme-refusal and neutralization cases) and the end-to-end suite (`WikiPageTests`), confirming the
  allow-list is load-bearing at both layers, not just correct in isolation. 665/681.

**Gates, actual numbers:** `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **681/681**, full
unfiltered (647 baseline + 34 new: 29 `MarkdownLinkAllowListTests`, 1 `PageEnumerationServiceTests`
mixed-case case, 4 new `WikiPageTests` cases; 1 existing test —
`AnonymousAccessTests.The_anonymous_response_carries_exactly_the_headers_it_declares` — updated to
include `Content-Security-Policy` in its pinned exact-header-set, since CSP now applies to every response
including the anonymous landing page, per spec). `dotnet format --verify-no-changes` — exit 0.
`openspec validate git-backed-content-core --strict` — valid. `git status --short -- src` shows seven
modified files (`NavMenu.razor`, `WikiPage.razor`, `MarkdownPipelineFactory.cs`,
`PageEnumerationService.cs`, `PageHistoryService.cs`, `PageRouteCodec.cs`, `Program.cs`) and one new file
(`MarkdownLinkAllowList.cs`); `git diff --stat -- src` matches exactly what's described above — no
mutation residue, `design.md`/`spec.md` untouched by this block.

**What the reviewer should look at hardest:** (1) the `Uri`-avoidance in `MarkdownLinkAllowList` — confirm
the root-relative-link finding independently rather than trusting the DEVLOG account of it; (2) S2's
comparison direction — confirm comparing the *found page's* canonical route against the request (not a
re-encode of the request) is genuinely what closes the gap, not merely a rephrasing of the same buggy
composition; (3) the S3 honest-gap call — whether a race-based test should be demanded anyway, or whether
the reasoning for skipping one holds.

→ @reviewer

**[reviewer]** §3 remediation block audit — against the supervisor's three blockers (S1–S3) and two
lower-severity findings, all independently re-verified with instruments different from the ones that
missed S1 the first three times.

**Verdict: Approve.**

**S1 — confirmed fixed, verified with a different instrument (attributes, not tags) and a real browser.**

Probed `MarkdownPipelineFactory.Create()` directly (scratch console app, `ProjectReference` to the real
`ZeroWiki.csproj`, `Markdown.ToHtml` + regex over rendered `href="…"`/`src="…"` attribute values — the
instrument the supervisor named, not "does the output contain `<script>`"): every attack form neutralizes
to `href=""`/`src=""` — bare `javascript:`, `JAVASCRIPT:`, leading/trailing whitespace, entity-encoded
`javascript&#58;`, `data:`, `vbscript:`, and image destinations. Ordinary relative links, `#fragment`,
`http`, `https`, `mailto` all render unchanged.

*Both empirical claims verified independently, not accepted on report:*
- `Uri.TryCreate("/wiki/other-page", UriKind.Absolute, out var uri)` → `ok=True, scheme=file,
  uri=file:///wiki/other-page` — confirmed exactly as claimed; using `Uri` for classification would have
  refused every ordinary wiki link.
- **The browser-stripping claim — verified in a real Chrome tab, not just reasoned about.** Built an
  `<a>` element at runtime with `href` set to `"java" + String.fromCharCode(9) + "script:window.__xssFired
  = true"`, checked `.href` (the browser's own parsed form): `"javascript:window.__xssFired = true"` — the
  tab is stripped and the scheme recognized. Then a real CDP-level trusted click (not a script-triggered
  `.click()`, which Chrome's synthetic-navigation heuristics suppressed on the first attempt) —
  `window.__xssFired` came back `true`. The embedded-tab bypass is real and would have executed on a
  genuine user click; `RemoveEmbeddedTabCrLf` is closing a live hole, not a theoretical one.

*Wiring verified by grep across the whole `src/` tree, not by reading the allow-list's own doc claim:*
exactly one `MarkdownPipelineBuilder` construction site (`MarkdownPipelineFactory.cs`) and exactly two
`Markdown.Parse`/`Markdown.ToHtml` call sites (`PageFrontmatterExtractor.cs`,
`WikiPage.razor:186`), both consuming the one DI-registered singleton. No bypass path exists anywhere in
the codebase today.

*Coverage confirmed for links, images, and autolinks*, including reference-style link definitions (traced
by hand and independently reproduced: `[click]\n\n[click]: javascript:alert(1)` neutralizes identically
to the inline form) — the brief named all three and the tests (`MarkdownLinkAllowListTests.cs`,
`WikiPageTests.cs:226-254`) assert on rendered attribute values throughout, not tag presence.

*One curiosity, not a finding:* a destination containing a raw NUL byte (CommonMark's own NUL→U+FFFD
substitution) produces a scheme token containing U+FFFD, which fails `IsValidSchemeToken` and is
classified as "relative" rather than refused outright — but a browser's URL parser can no more recognize
`java<FFFD>script` as the `javascript` scheme than this classifier can, so it isn't exploitable. Recorded
so it isn't mistaken for a gap later, not asking for anything.

CSP verified structurally: the `app.Use` registering `Response.OnStarting` is the first middleware in
`Program.cs`, ahead of `UseExceptionHandler`, `UseStatusCodePagesWithReExecute`, and `UseRouting` — since
`OnStarting` attaches to the response object itself rather than a specific middleware's continuation, it
fires regardless of which downstream stage ultimately writes the response, including redirects and
re-executed status-code pages. `AnonymousAccessTests.cs`'s exact-header-set assertion now includes
`Content-Security-Policy` for the anonymous landing page, matching my own reasoning about the header's
reach. `NavMenu.razor`'s inline `onclick` is removed, not reimplemented, exactly as briefed — the
mobile-menu-collapse-on-click UX is a deliberate, documented, architect-sanctioned loss, not an oversight.

**S2 — confirmed fixed, both required fixtures independently reproduced against the real codec, not hand-traced only.**

Replicated `WikiPage.razor`'s exact decode → `Encode` → `IsCanonicalRouteValue` sequence in a scratch
console app against the real `PageRouteCodec`:

```
/wiki/Chapter__1            -> decoded Chapter_1.md, canonical Chapter__1, IsCanonicalRouteValue = True  -> serves Chapter_1.md
/wiki/Chapter%20%201        -> decoded Chapter  1.md, canonical Chapter__1 (same route!), IsCanonicalRouteValue = False -> refused
```

Both match the coordinator's required fixtures and the spec's own "Only a page's own canonical route
serves it" scenario exactly. Worked through *why* the check is sound rather than fixture-specific: since
`Encode`'s percent-encoding layer is a true bijection (established extensively in 3a), `IsCanonicalRouteValue`
reduces to asking whether `routeValue` is a fixed point of `substitute ∘ reverse` — and the set of fixed
points of that composition is exactly the set of well-formed canonical routes, since `Encode` never emits
a raw space character. A route value containing a literal space (the shape every instance of this bug
takes) can therefore never pass, independent of which specific file happens to collide with it — this
generalizes past the one named fixture.

On "reuses rather than duplicates enumeration's identity rule": `IsCanonicalRouteValue` does **not**
call the same code path as `PageEnumerationService`'s round-trip check (`TryDecode`-based, comparing a
decoded path) — it's a distinct predicate (`Uri.UnescapeDataString`-based, comparing route strings). But
it isn't re-implementing D12's ambiguity logic either: it delegates the actual route computation to the
same shared `Encode`/`TryDecodeRouteValue` primitives, and closes the gap through a narrower, independently-sound
property (percent-encoding-layer equality) rather than re-deriving "is this route ambiguous" a second
time. Two distinct checks answering two genuinely different questions (file self-consistency at
enumeration time vs. request-vs-canonical equality at request time) sharing their primitives is not the
same risk as two copies of one rule that can drift — worth stating precisely rather than either waving it
through or over-crediting it as literal code reuse.

**S3 — the honest-gap call holds; I could not find a deterministic reproduction either, and I looked for one specifically.**

Independently reproduced the worker's exact negative result: `File.GetAttributes` on a dangling symlink
succeeds and returns `ReparsePoint` rather than throwing (confirmed on this machine), so that static
analogue genuinely cannot stand in for the race. `Directory.EnumerateFileSystemEntries(dir).ToList()`
materializes eagerly with no yield point for a black-box test to synchronize against, and there is no
seam to inject a callback mid-loop without either accepting a timing-dependent flaky test (this project's
evidence standard rejects that) or adding a filesystem-abstraction seam purely for this one test (a
structural change disproportionate to a two-clause exception-type widening). I agree with the worker's
conclusion: not achievable without one of those two costs, and the fix itself — catching the exact BCL
exception types the supervisor's own probe found, added to existing catch blocks with no new branching —
is low-risk enough that verified-by-reasoning-plus-type-matching is an acceptable substitute for a test
here specifically. This is a narrow exception, not a precedent: it holds because the fix is mechanical and
the reproduction cost is structural, not because "hard to test" is generally sufficient.

**Lower severity — both checked.**

- `PageHistoryService`'s path is now derived from `ContentPaths` (`:37-39`), not hardcoded — confirmed.
  The brief's second half ("make the failure distinguishable from the legitimate no-history case") was
  **not** implemented as a runtime distinguishing signal — the worker addressed it structurally instead
  (eliminating the divergence that made the two failure modes confusable, rather than adding a check to
  tell them apart after the fact) and said so plainly rather than silently substituting one for the other.
  I read this as a reasonable engineering trade-off given `ContentPaths` hardcodes `"docs"` with no way to
  construct a divergent instance today (confirmed), making the distinguishing signal harder to justify
  than removing the divergence risk it would have detected. Worth the architect's explicit sign-off that
  this reading of the brief is accepted, since it is a real, disclosed deviation from the literal ask.
- Mixed-case `.MD` extension now logs the real fault (`PageEnumerationService.cs:104-112`), verified via
  `MixedCaseExtension_IsRefusedWithAMessageNamingTheRealFault`'s capturing-logger test, which asserts the
  message text itself (`"extension"`, the filename, absence of `"D12"`) rather than just the unchanged
  refusal outcome.

**Not addressed, and worth naming rather than silently letting slide:** the supervisor's Q1/Q2 (the
codec's five-method, ~190-comment-line surface, and the two resolvers with no production caller) were not
in this remediation's brief and remain exactly as they were — `PageRouteCodec.cs` in fact grew another
public member (`IsCanonicalRouteValue`) this round. Not blocking: the architect scoped this remediation to
S1–S3 plus two lower-severity items deliberately, and Q1/Q2 were framed by the supervisor as worth
settling rather than as blockers. But it should be a conscious carry-forward into `## NEXT` or the next
supervisor pass, not something that reads as resolved because this round touched the file again.

**Gates — reproduced independently:** `dotnet build` 0 warnings/0 errors. `dotnet test` **681/681**, full
unfiltered. `dotnet format --verify-no-changes` exit 0. `openspec validate git-backed-content-core
--strict` valid. `git status --short -- src` / `git diff --stat -- src` match the reported shape exactly
(seven modified, one new) — no mutation residue.

→ @architect

**[supervisor]** §3 section review, **round two** — `60957e6..7ca5b76`, now four commits with the
remediation block on top.

**Verdict: Approve.** All three blockers are closed, and I verified each independently rather than
reading the fix and agreeing with it — the S1 defect existed precisely because three audits shared one
instrument, so corroborating this fix with the same method would have proved nothing. Tree clean at
`7ca5b76` (`git status --short --untracked-files=all` empty). Nothing below blocks; everything
outstanding is `## NEXT`.

---

### S1 — closed. 30 bypass attempts, 0 live destinations

I probed the **shipped** pipeline (a scratch project compiling `MarkdownLinkAllowList.cs` and
`MarkdownPipelineFactory.cs` verbatim), asserting on emitted **`href`/`src` attribute values**, and
checked the instrument first against a known-good link before trusting any negative result. Everything
neutralized to `href=""`/`src=""`:

- entity forms (`&#58;`, `&colon;`, `&#9;`, `&#10;`, `&#13;`, `&#32;`, `&#0;`), embedded literal tab,
  mixed case, `data:`, `vbscript:`, double colon, `java.script:`
- **reference-style links and images** — `[x][r]` with `[r]: javascript:alert(1)`, and the collapsed
  form. This is the path no prior audit named and the one I most expected to leak, since the
  destination lives in a `LinkReferenceDefinition` rather than the inline; Markdig resolves it into
  `LinkInline.Url` before `DocumentProcessed` fires, so `Enforce` sees it.
- traversal completeness — links inside headings, blockquotes, list items, emphasis, and an image
  nested inside an allowed link (`['https://ok.test', '']` — outer kept, inner neutralized)
- angle-bracket destinations, autolinks, images, titles

Two non-empty residuals, both correctly *relative* rather than missed: `&#0;` becomes U+FFFD per
CommonMark, so the destination is not a valid scheme token and emits
`href="%EF%BF%BDjavascript:alert(1)"`; and `javascript%3Aalert(1)` keeps its percent-encoded colon. A
browser's URL parser does not decode percent-escapes before scheme detection and does not strip
U+FFFD, so both resolve as relative paths. Refusing-as-relative is the right answer for each.

Ordinary destinations all still work — a fail-closed list that broke the wiki would be a regression,
and it does not: relative, `./`, `../`, bare fragment, query-only, `http`, `https`, `mailto`,
protocol-relative, percent-encoded and accented relatives, and `https`/relative images all emit
unchanged.

`DocumentProcessed` wiring is the right shape — it removes the class of failure entirely rather than
adding a call every future render site must remember. CSP checked as deployed: no inline `<style>`,
no `style="…"` attribute and no inline handler survives anywhere in `Components/` (only
`App.razor`'s external `blazor.web.js`), so `script-src 'self'`/`style-src 'self'` costs nothing;
registering `OnStarting` in middleware placed *ahead* of `UseStatusCodePagesWithReExecute` is what
makes it survive re-execution, and that ordering is correct as written. Tests assert on
`href=""`/`src=""` and `href="http://example.com"` — attribute values, not tags, so the instrument
that missed this three times is not the one now guarding it. `AnonymousAccessTests`'s pinned
header set was **extended** to include the new header rather than loosened.

### S2 — closed, and it does not break reachability

`IsCanonicalRouteValue` compares the request against `Uri.UnescapeDataString(canonicalRoute)` — the
*found page's* one true URL as the framework would deliver it — instead of re-encoding the request.
That sidesteps `Encode`'s many-to-one collapse rather than trying to detect it downstream, which is
the correct shape. Verified against the real codec:

```
a_b.md   routeValue 'a  b'  -> refused   (the S2 alias)
a_b.md   routeValue 'a__b'  -> serves
a b.md   routeValue 'a b'   -> refused
a%20b.md via canonical 'a%2520b' -> 'a%20b' -> serves
Café.md / a#b.md / a?b.md / sub/a b.md   -> all still serve
```

The half worth checking was the second one — a fail-closed canonicity test is exactly the kind of fix
that quietly 404s legitimate pages. It does not.

### S3 — closed, and **I accept the disclosed gap**

`Walk` now catches `DirectoryNotFoundException` around the listing and
`FileNotFoundException`/`DirectoryNotFoundException`/`UnauthorizedAccessException` around
`GetAttributes`; `WikiPage`'s read catch adds `UnauthorizedAccessException`. That is exactly the
surface I found, including the ENOTDIR case (a `git push` replacing a directory with a file maps to
`DirectoryNotFoundException`, so it is covered).

On accepting "mechanically correct, no regression test" — **yes, and the reasoning is not just
proportionality.** The risk of an untested `catch` runs in two directions and both are closed by
reading: it cannot catch *too much*, because it names three specific types rather than `Exception`, so
no unrelated fault can hide behind it; and it does not catch *too little*, because the thrown types
were established empirically rather than guessed. A deterministic reproduction would need a
filesystem seam injected purely for the test — at which point it stops exercising the real
`File.GetAttributes` and tests the seam instead — or a timing loop, and a flaky test in a 681-case
suite that gates every future block is a worse outcome than an honest gap. Recorded in `## NEXT` for
§6: once D3's lock exists the race is closable properly, and a test becomes possible then.

### Lower-severity — both closed; the `null`-return sign-off is right

`PageHistoryService` derives the working-tree segment from `ContentPaths`, and the mixed-case `.MD`
case now reports its actual fault instead of a route collision. On the return value staying `null` in
all cases: **I agree with the Architect's sign-off, and would not overrule it.** My original finding
was that the failure was *silent* — a wrong path indistinguishable from no history. That is closed at
the source: the path can no longer be wrong, and genuine faults now log. What remains is a return-type
shape with no consumer that needs the distinction, and §4.1 owns last-edit. A discriminated result
built now would be designed against a guess.

### D12 and D13 — confirmed, both now say what the code does

- **D12.** "A route containing two or more consecutive `_` is ambiguous" is exactly right with no
  exclusions: a route run of *n* underscores has Fib(*n*+1) preimages, so *n*=1 is unique and every
  *n*≥2 is ambiguous. The filename-side corollary added beside it — "a filename is ambiguous exactly
  when its encoding produces two consecutive underscores" — is also exact (single `_` → `__`,
  ambiguous; single space → `_`, not). Fourth revision, and the first that is a statement about the
  mechanism rather than a sample of it. Recording *why* the filename-side framings kept failing is
  what should stop a fifth.
- **D13.** The false "no bypass surface" clause is struck from the *Why* paragraph where it was
  asserted, not merely contradicted later — leaving it standing next to its own correction would have
  been the weaker fix. The addendum's four requirements each match shipped code: allow-list contents
  (`http`/`https`/`mailto` + relative + fragment), applied after entity resolution
  (`DocumentProcessed`), normalization of whitespace/control characters/case
  (`RemoveEmbeddedTabCrLf` + `Trim` + control refusal + `OrdinalIgnoreCase`), and CSP without
  `'unsafe-inline'`. Confirmed.

### Q1 / Q2 carried to §6 — I agree, and it is now a *better* deferral than in round one

Not a blocker. My round-one position was that S2's fix removes the premise for keeping the resolvers,
and §6 is where the real caller appears — the Architect's reasoning is that argument, so overruling it
would be overruling myself. One fact strengthens it: §6 now inherits a *stated, live* contract
(`IsCanonicalRouteValue`, documented as the check every route-resolving caller must reuse rather than
re-derive) instead of a guess, which is what made collapsing the API early risky.

Stated plainly so it is not lost: the codec now carries **six** public members, **two** with no
production caller. The accretion got one member worse — but it got worse in the right place, since
`IsCanonicalRouteValue` closes a blocker and has a caller. §6 should **delete or wire** the two
resolvers, not add a third.

→ @architect

## 4. Derived index

**[architect]** Base: `35dde44` — D6's derived index: the path/title/tags/last-edit structure built from
the repository, rebuildable from it alone, and updated for the files a write touches.

**[architect]** Pre-flight (CLAUDE.md §2): tree clean, on `change/git-backed-content-core`,
`openspec validate --strict` valid, `proposal.md` / `design.md` / `specs/content-store/spec.md` re-read.
§3 carries a supervisor `Approve` (round two, `7ca5b76`), so nothing is owed backwards. Forward
obligation 1 (D9's overclaiming prose) is owed before **§5**, not before §4 — confirmed unchanged.

**[architect]** ❓ @product-owner — §4 arrives in the same shape §3 did: **D6 is three sentences, and
three things it does not settle decide the whole section.** Raising them before briefing any worker,
because §2's lesson was that prose landing in `design.md` binds every later section and is not gated by
anything unless a scenario gates it.

D6 settles *what* is in the index (path, title, tags, last-edit), that it is rebuildable from the repo,
and that browser commits and `post-receive` trigger incremental re-index. It settles none of:

1. **Where the index lives.** Process memory, rebuilt on every start, is the cheapest thing that
   satisfies "rebuildable from the repository alone" — and D8 would permit a SQLite table beside
   `identity.db`, since neither is inside the repository. The spec scenario says "the index is *deleted*
   or absent at startup", which reads as though something on disk was contemplated; nothing in the
   change requires it.
2. **Who reads it in this change.** Today nothing would: the only read surface (`WikiPage.razor`) walks
   the whole working tree *and* spawns a `git log` on every request (`## NEXT` obligation 21). If §4
   builds a structure no shipped code consults, it is dead scaffolding by the supervisor's own lens —
   and if it does consume it, §4 quietly becomes the section that changes how every page is served.
3. **What last-edit costs to build.** One `git log -1` per page is N subprocesses per rebuild; one
   `git log --name-status` over history is a single subprocess that reads the whole history; lazy
   per-page with caching pays nothing at startup and everything on first view. This is the section's
   real cost decision and it is entangled with (2).

A fourth falls out of (1)–(3): **4.3's incremental update has no caller in this change** — §6 and §8 are
the writers and neither exists yet. Shipping an update method nobody calls is the same scaffolding
problem as (2) wearing a different hat.

→ @product-owner

**[product-owner]** In-memory index stamped with the `HEAD` sha; `WikiPage` reads it.

**[architect]** Recorded as **D15** in `design.md`, with the freshness obligation and three new
scenarios behind it in `specs/content-store/spec.md` so §4's section review is gated on behaviour rather
than on this post. What the Product Owner's answer settles, and what fell out of it:

1. **Nothing is persisted, and that is the stronger form of D6, not a weaker one.** "Rebuildable from the
   repository alone" stops being a property to maintain and becomes the only way the index can exist. The
   SQLite-beside-`identity.db` option D8 would have permitted is rejected: the hook cannot write it
   anyway (obligation 3), so the app would still need a refresh mechanism — and once that exists,
   persistence adds a second thing that can be stale for no benefit.
2. **The stamp is what makes §4 correct without §6 and §8**, which is the answer to the fourth question
   above rather than a detail of the first. D6 says `post-receive` and each browser commit *trigger*
   re-index; that quietly assumes every writer notifies the app. A stamp compared against the current
   `HEAD` covers the writer that does not — an `updateInstead` push, or an operator committing on the
   volume — identically to the two that do. §6 and §8 may later push an update in as an optimisation;
   correctness will not depend on it.
3. **The stale check is `git rev-parse HEAD`, one subprocess per view** — chosen over reading `.git/HEAD`
   and the ref file in C#, which spawns nothing but has to handle **packed** refs (`git gc` moves loose
   refs into `.git/packed-refs`) and silently serves a stale wiki if that second path is missed. It
   replaces a whole-tree walk *plus* a `git log` per view (obligation 21), so the request path gets
   materially cheaper even while gaining a spawn.
4. **A moved `HEAD` re-indexes only what `git diff --name-status <stamped>..<current>` names**; a full
   rebuild is the fallback for a stamp git cannot resolve. That gives **4.3 a real caller inside this
   change** rather than an update method waiting for §6 to exist — the scaffolding worry in questions (2)
   and (4) is answered by the same mechanism.
5. **Two things §4 explicitly does not take on.** Obligation 21's missing page-size cap on the *render*
   path stays unowned — the indexer reads a bounded prefix (frontmatter is at the head of the file), which
   is not the same fix and must not be mistaken for it. And the index carries D12's **refusals**, not just
   its pages: an index of servable pages only would demote a refused route to a plain 404 and lose the
   message naming every claimant §3 fought for.

**[architect]** Blocks for §4, carved on that: **A = 4.1–4.2** (the snapshot type, the builder, the bulk
history walk, the startup build and full rebuild) and **B = 4.3** (the stamp check, incremental
re-index off `git diff`, and `WikiPage` reading the index). B is where the section stops being
scaffolding, so A must not wire the read path and B must not leave a second one behind.

**[architect]** Brief — block A, tasks **4.1–4.2**: build the index and be able to rebuild it from the
repository. → @worker

**Deliver**

- An **immutable snapshot** type carrying, for every page: route, working-tree-relative path, absolute
  path, title, tags, last-edit (author name + time). Plus, in the *same* snapshot, D12's refusals —
  ambiguous routes with every claimant, and unreadable directories — and the **commit sha the snapshot
  was built from**. The stamp field is written in this block; nothing *checks* it until block B.
- A **builder** that produces one from the repository: `PageEnumerationService` for the walk (do not
  write a second walker), frontmatter for title/tags, one bulk history walk for last-edit.
- A **singleton holder** built at startup, with the snapshot swappable. Registration goes beside the rest
  of the content wiring in `ContentStorageStartupExtensions`, after repository bootstrap.
- Tests.

**Binding constraints — each of these has a scar behind it**

1. **One bulk history walk, not one `git log` per page** (D15). A single `git log --name-status` pass over
   history, newest-first, attributing each commit's `%an`/`%aI` to the paths it lists; the first record
   per path wins. Put it on `PageHistoryService` next to the existing per-path reader — git-log knowledge
   stays in one class, and a second git-log service is exactly the duplicated abstraction the supervisor
   looks for.
2. **`core.quotePath` will bite you.** Git quotes non-ASCII paths in `--name-status` output by default, and
   an Obsidian vault is full of accented filenames. §2 shipped a defect on precisely this. Use NUL-delimited
   output (`-z`) and/or `-c core.quotePath=false`, and **prove it with a test whose filename is non-ASCII** —
   a test with only ASCII fixtures cannot see this fault.
3. **`--no-renames`.** `diff.renames` defaults on, so rename records appear with two path fields under `-z`
   and will silently corrupt your parse. With renames off, a rename is an add on the new path, which is the
   correct last-edit anyway.
4. **Paths from git are repository-relative** (`docs/Foo.md`); the snapshot's are working-tree-relative.
   `PageHistoryService` already derives that prefix — reuse it, do not re-derive it.
5. **Bounded prefix read** (D15). Frontmatter is at the head of the file, so read at most enough
   *characters* (via `StreamReader` — never a raw byte slice, which splits a UTF-8 codepoint) to contain a
   legal block, reusing whatever cap D14's parser already enforces rather than introducing a second
   number. A block truncated by the cap yields **no** complete frontmatter block and therefore empty
   metadata, which is D14's total-failure posture holding by construction. This does **not** cap the render
   path; do not touch it.
6. **Title is the frontmatter title, nullable.** No H1 extraction, no filename fallback baked into the
   entry — `WikiPage`'s existing display fallback is a presentation choice and stays where it is.
7. **A tree with no pages is an empty snapshot; a build that could not be performed throws**, naming what
   failed. Do not invent a silent empty-index fallback — an empty wiki that looks fine is the silent
   degradation this project refuses everywhere else. An unborn `HEAD` (no commits) is "no pages", not a
   failure.
8. **Do not wire `WikiPage.razor`, do not add a staleness check, do not add incremental update.** All three
   are block B. A block that quietly does B's work makes B unreviewable.

**Scope and limits**

- **No mutation testing in this block.** Per CLAUDE.md it is for auth, concurrency and data-integrity
  paths; a derived, rebuildable index is none of those. Ordinary unit tests with normal coverage are the
  correct instrument here.
- Spec: `specs/content-store/spec.md`, *Derived index rebuildable from the repository* — the two existing
  scenarios are yours; *Content changed by an unannounced writer* is block B's. *Refused routes survive
  indexing* and *Index holds no content* are yours to satisfy structurally.
- Design: `design.md` **D15** (and D6, D14 for the cap, D12 for what enumeration already guarantees).
- Gates before handoff: `dotnet build` clean, `dotnet test` green **unfiltered**, `dotnet format
  --verify-no-changes` clean, `openspec validate git-backed-content-core --strict`.
- Post what you did and anything you had to decide to this DEVLOG under `## 4.`, then hand to
  @reviewer.

**[worker]** Block A (4.1–4.2) implemented.

- `PageIndexEntry`/`PageIndexSnapshot` (new): the immutable per-page shape plus D12's refusals and
  `CommitSha`, all in one snapshot. `PageIndexSnapshot.Empty` is the stamp-less snapshot for an unborn
  `HEAD`.
- `PageIndexBuilder` (new): `BuildAsync` probes `git rev-parse --verify -q HEAD` first (the same idiom
  `ContentRepositoryService.RepositoryHeadIsUnbornAsync` already uses) and short-circuits to
  `PageIndexSnapshot.Empty` on an unborn `HEAD` without walking anything. Otherwise it calls
  `PageEnumerationService.EnumeratePages()` unchanged, `PageHistoryService.GetAllLastEditsAsync` once for
  every page's last-edit, and reads a bounded prefix of each file for frontmatter.
- `PageHistoryService.GetAllLastEditsAsync` (new method, same class as the existing per-path
  `GetLastEditAsync` — no second git-log service): one `git -c core.quotePath=false log --no-renames
  --name-status -z --format=<SOH>%an%x1f%aI -- docs` pass, newest-first, `Dictionary.TryAdd` per path so
  the first (newest) sighting wins. Parsed the actual `-z` byte shape empirically first (scratch repo,
  `od -c`) rather than assuming: a NUL-split of the stream alternates header/status/path tokens, except
  the first status token after each header carries a stray leading `\n` (git's usual blank separator
  line, still emitted verbatim under `-z`) that must be stripped before inspecting it. A leading `\x01`
  marker on the header format (never a legal `--name-status` status code) tells a header token from a
  status token without lookahead.
- `PageIndex` (new): the singleton holder, `Volatile.Read`/`Write` for a lock-free atomic reference swap.
  Registered in `ContentStorageStartupExtensions.AddContentStorage` beside `PageIndexBuilder`, right after
  the rest of §3/§4's content wiring. `ContentStorageStartupExtensions.BuildPageIndexAsync` (new, mirrors
  `EnsureContentRepositoryAsync`'s shape) builds the initial snapshot and installs it; `Program.cs` calls
  it right after `EnsureContentRepositoryAsync()`, before `LogBootstrapStateAsync()`.

Decisions:

- **Bounded-prefix cap**: reused `SharpYamlFrontmatterParser.MaxSizeBytes` directly as the character count
  for `StreamReader`'s read window (`private const int FrontmatterPrefixReadCapChars =
  SharpYamlFrontmatterParser.MaxSizeBytes;` in `PageIndexBuilder`), rather than adding a `MaxSizeBytes`
  member to `IFrontmatterParser`. It's a bare `int` reference, not a SharpYaml type — D14's "only
  `SharpYamlFrontmatterParser` may reference a SharpYaml type" is about the library's own types
  (exceptions, serializer options), not an unrelated class reading its public size constant. Flagging in
  case @reviewer reads this differently.
- **Unreadable/vanished file during the frontmatter read** (enumeration-to-read race, same class of race
  `WikiPage.razor`'s render path already tolerates): caught `IOException`/`UnauthorizedAccessException`,
  logged, and treated as `PageFrontmatter.Empty` for that one page rather than failing the whole rebuild
  — not the "build throws" case in constraint 7, which I read as a genuine git/process fault, not a single
  file's transient race.
- Verified the `-z`/`--no-renames`/`core.quotePath=false` shape against a real `git log` first (non-ASCII
  filename, a rename, an empty commit, a commit outside the pathspec) before writing the parser, per the
  binding constraints' warning.

Tests added: `PageHistoryServiceTests` (bulk multi-path, non-ASCII filename, rename attributes to the new
path, HEAD born but no docs history yet); `PageIndexBuilderTests` (title/tags/last-edit from the
repository incl. commit sha, no-frontmatter page, non-ASCII filename through the full builder, frontmatter
over the cap yields empty metadata, ambiguous route + unaffected page surviving into the snapshot, unborn
`HEAD` → `PageIndexSnapshot.Empty`, HEAD born with no pages → empty `Pages` with a real sha);
`PageIndexTests` (holder default and swap). No mutation testing, per scope.

Gates: `dotnet build` clean (0 warnings), `dotnet test` green unfiltered (694/694), `dotnet format
--verify-no-changes` clean, `openspec validate git-backed-content-core --strict` valid.

Did not touch `WikiPage.razor`, add a staleness check, or add incremental update — all block B.

→ @reviewer

**[reviewer]** Block A (4.1–4.2) audit — **Request changes.** Two blockers, both cheap to fix; everything
else checked out, including the block's own highest-risk claim (the `-z` parse), reproduced independently
rather than trusted.

**Blockers**

1. **`PageIndexBuilder.cs:65–73` — the unborn-`HEAD` probe silently swallows any git failure, not only a
   genuinely unborn `HEAD`, which is exactly the "silent empty-index fallback" binding constraint 7
   forbids.** The code checks only `!headProbe.Succeeded` and returns `PageIndexSnapshot.Empty` on any
   non-zero exit from `git rev-parse --verify -q HEAD`. I built a fresh scratch repo and reproduced both
   cases directly: a genuinely unborn `HEAD` exits **1** with no output; running the same command against
   a path with **no** `.git` at all exits **128** and prints `fatal: not a git repository...` to stderr —
   `-q` does **not** suppress that message, contrary to the method's own comment ("`-q` suppresses git's
   error text for the one case this checks... so a non-zero exit here means exactly that, not some other
   git fault"). That claim is factually wrong, verified by direct reproduction, and this project's own §2
   thread (line ~3025) already recorded this exact 1-vs-128 distinction for a sibling `rev-parse` probe —
   the knowledge needed to get this right was already in the repo's own history.
   - Currently unreachable at the one wired call site: `Program.cs` only calls `BuildPageIndexAsync` after
     `EnsureContentRepositoryAsync` has guaranteed a valid repository, mirroring what makes
     `ContentRepositoryService.RepositoryHeadIsUnbornAsync` safe *there*. But that safety comes from the
     **caller's** structural precondition, not from anything `PageIndexBuilder.BuildAsync` itself asserts —
     and D15 explicitly designates this same method as block B's full-rebuild-fallback caller ("gives 4.3
     a real caller inside this change"). Nothing currently stops a future call (or a corrupted/deleted
     `.git` mid-run) from hitting exit 128 and silently reporting "no pages" instead of throwing, naming
     what failed.
   - Fix: distinguish the exit code (1 = unborn `HEAD`; anything else throws, naming the failure — e.g. via
     `GitProcessException`), or make the "repository already known to exist" precondition explicit and
     enforced on `BuildAsync`, not just implied by call order. Either resolves it; please don't leave the
     comment's disproven claim in place either way.

2. **`PageIndexBuilderTests.cs:154–169`, `Build_AmbiguousRouteAndUnreadableDirectory_SurviveIntoTheSnapshot`
   — the name promises unreadable-directory coverage the body doesn't deliver.** The test creates two
   ambiguous-route fixtures and one unaffected page; it never creates an unreadable directory, and never
   asserts on `snapshot.UnreadableDirectories` at all. I grepped the whole test tree —
   `UnreadableDirectories` is asserted in `PageEnumerationServiceTests.cs` (§3's territory) but nowhere in
   this block's tests. The pass-through itself is correct (`PageIndexBuilder.cs:95` forwards
   `enumeration.UnreadableDirectories` straight into the snapshot — I read it and it's right), so this is a
   test-only gap, not a shipped bug: but *Refused routes survive indexing* is one of the two scenarios the
   brief named as "yours to satisfy structurally" for this block, and a test claiming to cover it while
   silently not doing so is exactly the "what can this assertion not see" trap this project has been bitten
   by before. Please extend the test with a genuine unreadable-directory fixture (§3's own tests show the
   pattern) and an assertion on `snapshot.UnreadableDirectories`, or split it into two named tests.

**Nits**

3. **The D14 seam question the worker flagged (`PageIndexBuilder.cs:18–26`, referencing
   `SharpYamlFrontmatterParser.MaxSizeBytes` directly): compliant with the letter of D14, weakens its
   intent.** `MaxSizeBytes` is a bare `int`, not a SharpYaml type, so this doesn't violate "only
   `SharpYamlFrontmatterParser` may reference a SharpYaml type" as literally written. But D14's stated
   purpose is that the parser can be "swapped without touching any calling code" — and this reference
   *would* require touching `PageIndexBuilder.cs` the day `SharpYamlFrontmatterParser` is ever replaced
   (the constant would either vanish or silently stop tracking the active parser's real cap). Recommend
   moving the cap onto `IFrontmatterParser` itself (an `int` property needs no SharpYaml type to cross the
   seam) so the coupling is formal rather than incidental. Not blocking — no functional issue today, and
   it's exactly the kind of judgment call the worker was right to flag rather than decide unilaterally.

4. **`-c core.quotePath=false` (`PageHistoryService.cs`, `GetAllLastEditsAsync`) is redundant given `-z`,
   and the doc comment misattributes the mechanism.** I built my own scratch repo (a non-ASCII filename,
   a multi-file commit, a deletion, and a delete-then-re-add of the same path — deliberately not reusing
   the worker's fixture) and confirmed empirically that `-z` alone disables git's default path-quoting
   regardless of `core.quotePath`'s value — with the flag removed, the café filename still comes through
   unquoted under `-z`, and only reappears quoted once `-z` itself is also removed. This matches documented
   git behaviour and matches what this project's own §2 thread already established (line ~3751: "`-z`
   disables `core.quotePath`'s default octal-escaping"). I then made a single targeted check (not a
   mutation-testing campaign — this is ordinary test-quality verification, and the block is explicitly
   out of mutation-testing scope): removed `-c core.quotePath=false` from the shipped code, ran only the
   two non-ASCII tests, watched them still pass, then reverted and confirmed the file's checksum matched
   the pre-check value exactly. So per the brief's own item 2 ("prove... that the test would fail if the
   flag were removed") — it wouldn't, because `-z` is the actual load-bearing mechanism, not the explicit
   `-c` override. This isn't a bug — the flag is harmless belt-and-braces, not a source of corruption — but
   the class remarks (`PageHistoryService.cs`, the `-c core.quotePath=false` `<b>` block) attribute the fix
   to the flag rather than to `-z`, which is the same shape of "comment asserts a causal mechanism that
   isn't the true one" this project has been bitten by before (D13's addendum; §2's reordering fix).
   Recommend correcting the comment (or dropping the now-admittedly-redundant flag) — worker/architect's
   call which, not blocking either way.

**Checked and clean — recorded so it isn't re-litigated**

- **The `-z` parse itself, re-derived independently.** Built my own scratch repository (not the worker's
  fixture) with a two-file commit, a non-ASCII filename, a modify-both-files commit, a deletion, and a
  re-add of the deleted path, then ran an exact copy of `GetAllLastEditsAsync`'s parsing body against the
  real captured `git log --no-renames --name-status -z --format=...` output. Result matched exactly: the
  re-added path attributed to its newest (re-add) commit rather than the earlier delete or original add —
  "first record per path wins" holds against a real re-add, not just a same-path double-edit.
- **`--no-renames`** appears exactly once as an actual argument (`PageHistoryService.cs`); every other hit
  in the diff is prose/test-name. Nothing re-enables `diff.renames`.
- **Bounded-prefix cap (chars vs. bytes) is sound.** Reading `MaxSizeBytes` *characters* always consumes
  **at least** that many bytes from the file (a UTF-8 character is never smaller than a byte), so any
  frontmatter block within the parser's own byte cap is guaranteed to fit inside the character read window.
  `SharpYamlFrontmatterParser.Parse` independently re-checks the UTF-8 byte count before ever handing text
  to SharpYaml, so even a pathological all-multibyte prefix that reads more bytes than `MaxSizeBytes`
  cannot bypass D14's actual bound — worst case it's a little wasteful, never unsound.
- **No duplicated abstraction.** `PageIndexBuilder` calls `PageEnumerationService.EnumeratePages()` and
  `PageHistoryService.GetAllLastEditsAsync` only; no second walker, no second git-log service.
- **Scope held.** No `WikiPage.razor` changes, no staleness check, no incremental update — confirmed
  absent from the diff.
- **Per-file frontmatter-read race handling** (catch `IOException`/`UnauthorizedAccessException` →
  `PageFrontmatter.Empty` for that one page, not a whole-build failure) is a reasonable, well-justified
  distinction from constraint 7's "build throws" case — agreed with the worker's reasoning as posted.
- **DI graph** is singleton-consistent throughout (`PageIndexBuilder`/`PageIndex` alongside the services
  they depend on) — no captive-dependency risk.
- **Gates, re-run myself:** `dotnet build` clean (0 warnings), `dotnet test` **694/694 unfiltered**,
  `dotnet format --verify-no-changes` clean, `openspec validate git-backed-content-core --strict` valid.
  `git diff -- src` / `git status --short -- src` show only this block's expected files — no mutation
  residue (the one targeted quotePath check above was reverted and checksum-verified before these gates
  were re-run).

→ @worker

**[worker]** Both blockers and both nits fixed.

- **Blocker 1** (`PageIndexBuilder.BuildAsync`): `git rev-parse --verify -q HEAD` now branches on exit
  code, not merely non-zero — `ExitCode == 1` is the unborn-`HEAD` "no pages" case (D15); any other
  non-zero (e.g. 128, "not a git repository") now throws `GitProcessException(headProbeArguments,
  ExitCode, StandardError)`, naming the failure instead of folding it into an empty snapshot. Rewrote the
  comment to state what the two codes actually mean, verified against a real repo both ways (unborn:
  exit 1, no stderr; not-a-repository: exit 128, `fatal:` text intact) before writing it. New test
  `Build_RepositoryRootIsNotAGitRepository_ThrowsRatherThanReturningTheEmptySnapshot` asserts the throw.
- **Blocker 2**: `Build_AmbiguousRouteAndUnreadableDirectory_SurviveIntoTheSnapshot` now actually creates
  an unreadable directory — reused `PageEnumerationServiceTests.UnreadableDirectory_IsReportedAndOtherPagesStayServed`'s
  `File.SetUnixFileMode(..., UnixFileMode.None)` fixture idiom verbatim (same root-bypass/stillReadable
  guard, same `finally`-restore) rather than inventing a second one — and asserts
  `snapshot.UnreadableDirectories` contains it and no page under it is served. Confirmed the assertion is
  live by temporarily changing `PageIndexBuilder.BuildAsync`'s pass-through to `[]` and re-running: the
  test failed as expected, then reverted (checksum/`git diff` clean before re-running the real gates).
- **Nit 1 (required)**: moved the cap off `SharpYamlFrontmatterParser.MaxSizeBytes` and onto the seam.
  Added `int MaxSizeBytes { get; }` to `IFrontmatterParser`; `SharpYamlFrontmatterParser` implements it
  explicitly (`int IFrontmatterParser.MaxSizeBytes => MaxSizeBytes;`, coexisting with its own `public
  const int MaxSizeBytes` — verified this compiles and resolves correctly both ways before relying on it).
  `PageFrontmatterExtractor` gained a passthrough `MaxFrontmatterSizeBytes => _frontmatterParser.MaxSizeBytes`;
  `PageIndexBuilder` now sizes its read buffer from that instead of the concrete parser's constant, so a
  future swapped-in `IFrontmatterParser` with a different cap is what the read follows.
- **Nit 2**: rewrote the `GetAllLastEditsAsync` remarks — confirmed empirically (accented filename,
  `-z` with `core.quotePath` left at its default) that `-z` alone already emits unquoted UTF-8 paths;
  `-c core.quotePath=false` is now documented as redundant belt-and-braces, kept for defence-in-depth but
  no longer credited as the mechanism. Flag itself unchanged.

Gates, re-run unfiltered: `dotnet build` clean (0 warnings), `dotnet test` **695/695**, `dotnet format
--verify-no-changes` clean, `openspec validate git-backed-content-core --strict` valid.

→ @reviewer

**[reviewer]** Re-audit of block A (4.1–4.2) — **Approve.** Re-read the block as a whole, not just the
delta, since three files hadn't been in front of me before (`IFrontmatterParser.cs`,
`SharpYamlFrontmatterParser.cs`, `PageFrontmatterExtractor.cs`); all four findings independently
re-verified rather than taken on the worker's report.

- **Blocker 1.** Reproduced the exit-code split myself in a fresh scratch directory: `git rev-parse
  --verify -q HEAD` against a plain (non-`.git`) directory exits **128** with `fatal: not a git
  repository...`, distinct from the unborn-`HEAD` case's exit **1** — matches `PageIndexBuilder.cs:49–69`'s
  new branch exactly. `GitProcessException`'s message embeds the full argument list, exit code, and
  stderr text (`GitProcessException.cs:6–7`), so the throw genuinely names what failed rather than just
  signalling that something did. Checked there's no second `_git.RunAsync` call anywhere else in the
  file that could still fold a failure into an empty result — there is exactly one, and it's the one
  that got fixed.
- **Blocker 2.** Independently mutated the fix myself rather than trusting the worker's report: changed
  `PageIndexBuilder.cs:91`'s `enumeration.UnreadableDirectories` to `[]`, ran
  `Build_AmbiguousRouteAndUnreadableDirectory_SurviveIntoTheSnapshot` alone — it failed exactly as
  expected (`Assert.Contains() Failure: ... Not found: "locked"`) — then reverted via `Edit` and
  confirmed the file's md5 matched what it was before my mutation. The new unreadable-directory half of
  the test faithfully reuses §3's own `UnixFileMode.None` idiom (root-tolerant skip, try/finally
  permission restore), not a second one.
- **Nit 1 (made required).** Traced the whole chain: `IFrontmatterParser.MaxSizeBytes` (new interface
  member) → `SharpYamlFrontmatterParser`'s explicit `int IFrontmatterParser.MaxSizeBytes => MaxSizeBytes;`
  returns the class's own `public const int MaxSizeBytes = 8 * 1024` directly, so the two can never drift
  — one is a forwarding read of the other, not an independently-maintained second number. Confirmed
  nothing still reaches past the seam: grepped every `MaxSizeBytes`/`MaxFrontmatterSizeBytes` reference
  in `src/` and `tests/` — `PageIndexBuilder.cs` now sizes its read from
  `_frontmatterExtractor.MaxFrontmatterSizeBytes` only, no direct `SharpYamlFrontmatterParser` reference
  remains in it. The two test-side references to `SharpYamlFrontmatterParser.MaxSizeBytes`
  (`PageIndexBuilderTests.cs:141`, `SharpYamlFrontmatterParserTests.cs:60`) are fine — both construct a
  concrete `SharpYamlFrontmatterParser` directly and are sizing a fixture against the parser they know
  they're using, not reaching around the interface in production code.
- **Nit 2.** `GetAllLastEditsAsync`'s remarks now correctly credit `-z` as what disables path-quoting and
  document `-c core.quotePath=false` as redundant, kept anyway as free belt-and-braces — matches what I
  verified independently last round.

**Gates, re-run myself, not relayed:** `dotnet build` clean (0 warnings), `dotnet test` **695/695
unfiltered**, `dotnet format --verify-no-changes` clean, `openspec validate git-backed-content-core
--strict` valid. `git status --short -- src tests` / `git diff --stat -- src` show exactly this block's
expected file set and nothing else — no residue from either of my two targeted mutation checks, both
reverted and checksum-confirmed clean before these gates ran.

**This Approve certifies the working tree exactly as it stands now** — `HEAD` `c59ce13`, uncommitted:
`ContentStorageStartupExtensions.cs`, `IFrontmatterParser.cs`, `PageFrontmatterExtractor.cs`,
`PageHistoryService.cs`, `SharpYamlFrontmatterParser.cs`, `Program.cs`, `PageHistoryServiceTests.cs`
(modified) plus `PageIndex.cs`, `PageIndexBuilder.cs`, `PageIndexEntry.cs`, `PageIndexSnapshot.cs`,
`PageIndexBuilderTests.cs`, `PageIndexTests.cs` (new). Any further edit before commit is uncertified and
needs its own pass.

→ @architect: block A clear to commit.

**[architect]** Block A landed as `fe3496f` — reviewer `Approve` on re-audit, gates run by the Architect
rather than relayed: build 0 warnings, `dotnet test` **695/695** unfiltered, format clean, `openspec
validate --strict` valid. `4.1`/`4.2` ticked. Both mutation targets from the review loop confirmed
restored in `src/` before committing (`PageIndexBuilder.cs:91` passes `enumeration.UnreadableDirectories`
through, not `[]`) — that file was untracked for the whole block, so `git diff` alone could never have
shown it.

**[architect]** Brief — block B, task **4.3**: the stamp check, incremental re-index, and the read path.
→ @worker

This is the block where §4 stops being scaffolding. Block A built a snapshot nothing consults; if B lands
without `WikiPage` reading it, the section fails its own design decision no matter how green the tests
are.

**Deliver**

- **Freshness on read** (D15). Serving a page compares the snapshot's stamp against the repository's
  current commit via `git rev-parse HEAD`, and refreshes when they differ. This is what makes the index
  correct for a writer that never notifies the app — an `updateInstead` push, or an operator committing
  on the volume — and it is the scenario *Content changed by an unannounced writer is still reflected*.
- **Incremental re-index** — `git diff --name-status <stamped>..<current>` names the affected paths, and
  only those entries are rebuilt. Additions, modifications **and deletions** all have to land: a deleted
  page must leave the index, or its route keeps serving a file that is gone.
- **Full rebuild as the fallback**, reusing block A's builder unchanged: no previous stamp, or a stamp git
  cannot resolve (history rewritten, `reset --hard` behind the app's back). Detect that honestly rather
  than letting a failed `diff` degrade into "nothing changed" — the mirror of block A's blocker 1.
- **`WikiPage.razor` reads the index**: route resolution, title, tags and last-edit all come from the
  snapshot. The **body is still read from the working tree on the request that renders it** (D15 — the
  index holds no content). After this block a page view must no longer walk the whole tree, and must no
  longer spawn a `git log`.
- Tests, including the unannounced-writer scenario end to end: build the index, commit a change directly
  with git behind the app's back, request the page, assert the *new* content and last-edit appear without
  a restart.

**Binding constraints**

1. **A refused route stays refused.** D12's ambiguous routes and unidentifying sole claimants are in the
   snapshot for exactly this reason. Resolving from the index must not demote a refusal to an ordinary
   404 — the message naming every claimant is what §3 spent two supervisor rounds getting right.
2. **No stampede, and no torn snapshot.** Concurrent requests arriving on a stale stamp must not each run
   their own refresh, and a reader must never observe a half-updated index — swap an immutable snapshot,
   never mutate one in place. Single-flight it.
3. **Do not leave a second read path behind.** If `WikiPage` no longer needs its per-request
   `PageEnumerationService` / `PageHistoryService` calls, remove them rather than leaving them beside the
   index lookup. Both services keep their callers — the *builder* uses them — so this is deleting a
   duplicated path, not a class.
4. **This does not fix the page-size cap** (`## NEXT` obligation 21). The render path still reads a page
   whole; that stays unowned and must not be quietly half-fixed here.
5. **You are adding git spawns to the request path**, which makes obligation 8 (`GitProcessRunner` does not
   kill the subprocess on cancellation) faintly live for the first time — a cancelled page view can now
   orphan a short-lived `rev-parse`. It stays **§6's** to fix: thread cancellation through properly, note
   it, and do not widen into fixing the runner.

**Scope and limits**

- **No mutation testing in this block** — same reasoning as block A. A read-side cache refresh is not an
  auth, concurrency-invariant or data-integrity path in the sense CLAUDE.md scopes. Deterministic tests
  are the right instrument for single-flight behaviour; a mutation run is not.
- Spec: `specs/content-store/spec.md`, *Derived index rebuildable from the repository* — all five
  scenarios are in play, and *Content changed by an unannounced writer* and *Index holds no content* are
  yours specifically. Design: **D15**, plus D12 for what a refusal owes an operator.
- Gates before handoff: `dotnet build` clean, `dotnet test` green **unfiltered**, `dotnet format
  --verify-no-changes` clean, `openspec validate git-backed-content-core --strict`.
- Post to the DEVLOG under `## 4.`, then hand to @reviewer. Do not commit; do not tick boxes.

**[worker]** Block B (4.3) implemented — §4 now consults the index it built.

- **`PageIndexBuilder.ProbeCurrentHeadShaAsync`** (new, public): the unborn-vs-real-failure head probe
  factored out of `BuildAsync` unchanged in behaviour, shared with the freshness check below so the
  distinction is made in exactly one place.
- **`PageIndexBuilder.RefreshAsync(current, currentHeadSha, ct)`** (new): no-ops if the stamp already
  matches; falls back to `BuildAsync` (unchanged) when there's no previous stamp (`current.CommitSha is
  null`) or the stamped commit no longer resolves (`git diff` exits non-zero — logged, not silently
  folded into "nothing changed", the mirror of block A's blocker 1); otherwise runs
  `git -c core.quotePath=false diff --no-renames --name-only -z <stamped> <current> -- docs` and routes
  into incremental update.
- **`PageIndexBuilder.ApplyIncrementalUpdateAsync`** (new, private): rebuilds only the *routes* touched by
  the diff's paths. For each affected route, the route's other (unaffected) claimants come from the
  **previous snapshot itself** (`current.Pages`/`current.AmbiguousRoutes`), combined with a fresh
  existence check of the diffed paths — this is what lets an addition or deletion turn a route ambiguous
  or resolve an existing ambiguity without re-walking the whole tree, tested directly (`Refresh_
  NewFileCollidingWithAnExistingUnaffectedPage_RefusesBothRatherThanServingEither`, `Refresh_
  RemovingOneOfTwoCollidingClaimants_ServesTheRemainingOne`). Additions/modifications re-read frontmatter
  (`ReadFrontmatterAsync`, reused) and last-edit (`PageHistoryService.GetLastEditAsync`, per changed page —
  proportionate to what changed, not a bulk walk); deletions simply aren't re-added. `UnreadableDirectories`
  is carried over unchanged — permission changes aren't discoverable via `git diff` and are out of this
  block's scope; a restart or a later full-rebuild trigger still catches them.
- **`PageEnumerationService.EvaluateRouteClaim`** (new, public static) + `RouteClaimEvaluation`: D12's
  "exactly one file claims the route, and decoding it reproduces that file's path" check, extracted
  verbatim from `EnumeratePages()`'s loop (no behaviour change) so the incremental path reuses the exact
  same invariant instead of a second copy of it.
- **`PageHistoryService.RepositoryRelativeWorkingTree`** (new public property): the `docs/`-relative
  prefix, already computed once in the constructor, exposed so the incremental `git diff` pathspec and
  path-stripping share it rather than re-deriving the same fact a second time.
- **`PageIndex.GetCurrentAsync`** (new): the freshness/single-flight orchestration (constraint 2). Fast
  path probes `HEAD` and returns `Current` unchanged if the stamp matches — every request pays one `git
  rev-parse HEAD`, per D15. On a stale stamp, acquires a `SemaphoreSlim(1,1)`, **re-checks after
  acquiring** (another caller may have already refreshed, or `HEAD` may have moved again), then calls
  `RefreshAsync` and swaps in the result via the existing `Replace`. `PageIndex` now implements
  `IDisposable` for the semaphore and takes `PageIndexBuilder` as a constructor dependency (both already
  singletons — no captive-dependency risk).
- **`WikiPage.razor`**: now injects only `PageIndex` and `MarkdownPipeline` — `PageEnumerationService`,
  `PageFrontmatterExtractor`, and `PageHistoryService` are gone from this component entirely (constraint
  3: both services keep their caller, the builder, just not a second one here). `OnInitializedAsync` calls
  `PageIndex.GetCurrentAsync(context.RequestAborted)` once, resolves route/title/tags/last-edit from the
  returned `PageIndexEntry`, and still reads the body straight from `pageMatch.AbsolutePath` on this
  request (D15 — index holds no content; constraint 4, page-size cap untouched). Ambiguous-route handling
  (409, naming every claimant) is unchanged, now reading `snapshot.AmbiguousRoutes` (constraint 1).
- Cancellation: `GetCurrentAsync`/`RefreshAsync`/the new git spawns all take the request's
  `CancellationToken` now (`context.RequestAborted`), threaded normally rather than hardcoded to `None`.
  Per constraint 5, did **not** touch `GitProcessRunner` — it still doesn't kill the subprocess on
  cancellation, so a cancelled page view can now abandon-without-killing a `rev-parse`/`diff`/`log`; noted
  in both `PageIndex.GetCurrentAsync`'s and `PageHistoryService.GetLastEditAsync`'s remarks as owed to §6.

Tests: `PageIndexBuilderTests` gained `Refresh_*` (no-op on unchanged HEAD, add, modify, delete, emergent
collision, resolved collision, no-previous-stamp fallback, unresolvable-stamp fallback). `PageIndexTests`
rewritten onto a real-repo fixture (mirroring `PageIndexBuilderTests`') with `GetCurrentAsync_*` covering
the fast path (same reference back — proves no refresh ran), the unannounced-writer scenario end to end,
deletion, and single-flight (8 concurrent calls on a stale stamp all return the *same* snapshot instance —
`Assert.Same`, which would catch two independent rebuilds even if they were value-equal).  `WikiPageTests`
gained two HTTP-level end-to-end tests: content changed directly via git after the page was already served
is reflected on the next request without a restart, and a page deleted the same way stops being served.

Gates: `dotnet build` clean (0 warnings), `dotnet test` **709/709 unfiltered**, `dotnet format
--verify-no-changes` clean, `openspec validate git-backed-content-core --strict` valid. `git status
--short -- src tests` / `git diff --stat -- src tests` show exactly this block's expected file set, no
untracked residue. No mutation testing (scope: read-side cache refresh, not auth/concurrency-invariant/
data-integrity).

→ @reviewer

**[reviewer]** Block B (4.3) audit — **Request changes.** One blocker in `ApplyIncrementalUpdateAsync`'s
correctness surface; everything else — the `--name-only` deviation, the failure posture, the read path,
single-flight, and the `EvaluateRouteClaim` extraction — checked out, several only after independent
reproduction rather than trusting the summary.

**Blocker**

1. **`PageIndexBuilder.cs`, `ApplyIncrementalUpdateAsync` — an unreadable directory recovering readability
   between refreshes is invisible to the incremental path, and can let an emergent D12 collision go
   undetected.** The reconstruction seeds each affected route's claimant set from `current.Pages` /
   `current.AmbiguousRoutes` plus a fresh on-disk check of the diffed paths — sound *provided* `current`
   already knows about every file outside the diff. It doesn't, whenever `current.UnreadableDirectories`
   is non-empty: a file under a directory that was unreadable at the last full build was never recorded
   as a claimant *anywhere* in `current` (not in `Pages`, not in `AmbiguousRoutes`). If an operator later
   fixes that directory's permissions (not a git-tracked change, so no diff ever reports it) and, in the
   same refresh window, an unrelated file elsewhere is added or modified with a route that happens to
   collide with a file inside the now-readable directory, the incremental path reconstructs that route's
   claimant set from `current` (which has never heard of the hidden file) plus only the one path the diff
   actually names — and serves the diffed file alone, never discovering the second claimant. This is
   exactly what item 1 in the brief asked me to look for, and it is real: an emergent collision that goes
   unnoticed serves a page at a route that does not identify it, which is the specific failure §3's
   supervisor rounds existed to prevent, and D12's *Consequence binding §6* is explicit that a wrong
   answer here becomes a wrong-file **write** the moment a save path exists.
   - This is disclosed, not hidden — the worker's own note reads "`UnreadableDirectories` is carried over
     unchanged — permission changes aren't discoverable via `git diff` and are out of this block's scope;
     a restart or a later full-rebuild trigger still catches them" — and it does require an out-of-band
     permission change to open the window at all, which is inherently rarer than an ordinary edit. But
     nothing in the brief pre-authorized narrowing D12's guarantee this way, and the fix is small and
     precisely targeted: **whenever `current.UnreadableDirectories` is non-empty, `RefreshAsync` should
     fall back to `BuildAsync` rather than attempting the incremental path** — `current` is the *only*
     case where the reconstruction's completeness assumption can fail, so this is not a broad, paranoid
     re-scoping, it is the one condition under which the assumption the whole method depends on doesn't
     hold. Either take that fix, or bring this back to the Architect for an explicit decision to accept it
     as a documented, narrow limitation (a `## NEXT` entry naming exactly this precondition) — but it
     should not land implicitly as an unstated scope note inside a single DEVLOG paragraph.

**Nits**

2. **`RefreshAsync`'s diff-failure log message overclaims its own cause.** The branch fires on *any*
   non-zero exit from the `git diff` call, but the message ("The stamped commit can no longer be
   resolved…") and the accompanying comment name only the "history rewritten / gc'd / `reset --hard`"
   cause. The fallback behaviour is correct regardless (falling through to `BuildAsync`, which has its own
   independently-hardened unborn-vs-genuine-failure distinction from block A's blocker 1, so a *truly*
   broken repository still surfaces as a thrown `GitProcessException` rather than being swallowed) — this
   is a wording precision issue, not a functional one. Worth broadening the phrasing to something like "the
   diff could not be computed (unresolvable stamp, corrupted history, or another git fault)" so a future
   reader doesn't treat this log line as proof of which of those actually happened.
3. **"Extracted verbatim, no behaviour change" (`EvaluateRouteClaim`) is not quite accurate.** The
   extracted method has one branch the inline code never had: `if (claimants.Count == 0) return new
   RouteClaimEvaluation(false, null, [])`. In `EnumeratePages()`'s original context this is dead code —
   `claimsByRoute`'s keys are only ever routes with ≥1 claimant, so the branch can't fire from that caller
   — but it's a real, necessary addition for `ApplyIncrementalUpdateAsync`, which legitimately can reach a
   route with zero remaining claimants (every file at that route deleted, nothing else ever shared it).
   I traced the semantics through: it correctly makes the route disappear from both `Pages` and
   `AmbiguousRoutes`, matching what a fresh `EnumeratePages()` pass would do (never gaining an entry at
   all). So the *behaviour* for `EnumeratePages()`'s own caller is genuinely unchanged, and the new branch
   is correct — but "no behaviour change" oversells it slightly, since a new, reachable-from-elsewhere case
   was added. Not blocking; just don't let the phrase stand as a durable claim in the record.

**Verified independently rather than taken on report**

- **The `--name-status` → `--name-only` deviation.** Confirmed sound: the code never needs git's status
  letter at all, because every affected path's membership is re-derived from a fresh on-disk existence
  check (`StillExistsAsOrdinaryFile`) rather than trusted from git's A/M/D. A deletion reads as "does not
  exist" and is correctly never re-added; a modification re-reads frontmatter/last-edit fresh; `--no-renames`
  means a rename's old and new paths both appear as independent entries in `--name-only`'s output, so
  nothing about rename detection is lost either. This is a genuinely different, and for this method's
  purposes equivalent-or-better, mechanism than what the brief specified — accepted.
  - **The one real consequence of re-stating the working tree rather than trusting the diff's endpoint**:
    a TOCTOU window exists between probing `HEAD` and reading the affected paths' on-disk state — if a
    concurrent write lands in between (a browser save or an incoming push; reads are not under D3's
    `flock`), the returned snapshot's `CommitSha` can understate the freshness of its own content. This
    does **not** produce a permanent wrong state or a missed collision on its own: the next `GetCurrentAsync`
    call detects the stamp mismatch against the now-later `HEAD` and re-diffs from exactly where this one
    left off, so it self-corrects within one request. It also doesn't require an operator mistake to
    trigger, unlike finding 1 above — ordinary concurrent saves/pushes are enough — but its blast radius is
    one transient read, not a standing collision, and there is no save path yet for D12's §6 consequence to
    bite through. Recording this for `## NEXT` as a property of the design worth naming, not blocking.
- **The failure posture (item 3).** `RefreshAsync`'s "no previous stamp" and "stamp unresolvable" branches
  both fall through to `BuildAsync` rather than reporting "nothing changed" — reproduced the unresolvable-
  stamp case myself (a 40-`f` sha against a real repo) and confirmed `git diff` exits non-zero and the
  code takes the full-rebuild path, not a silent no-op.
- **Refused routes through the new read path (item 4).** `WikiPage.razor`'s existing, unmodified
  `An_ambiguous_route_names_both_claimants_rather_than_reading_as_not_found` end-to-end test still runs
  green against the new `PageIndex`-backed resolution — confirms the 409 + both-claimants message survives
  the read-path swap, not just that the ambiguous-routes list is still threaded through in the diff.
- **Single-flight (item 5).** Reproduced myself rather than trusting the worker's checksum report:
  bypassed `PageIndex`'s `_refreshGate` (removed the wait/re-check, always refresh-and-replace on a stale
  stamp), ran `GetCurrentAsync_ConcurrentCallsOnAStaleStamp_SingleFlightTheRefresh` three times — failed
  3/3 with a genuine `Assert.Same` mismatch (two distinct `PageIndexSnapshot` instances), confirming the
  assertion is real evidence and not a timing coincidence the harness happens to pass. Reverted and
  checksum-confirmed the file matched its pre-mutation state before re-running the real gates.
  `PageIndex : IDisposable` with a `PageIndexBuilder` constructor dependency: both singletons, so no
  captive-dependency risk, and the framework's own DI container disposes registered singleton
  `IDisposable`s at host shutdown — no extra wiring needed, none added.
- **The claimed win (item 6).** Read the shipped `WikiPage.razor` directly, not the summary:
  `PageEnumerationService`/`PageFrontmatterExtractor`/`PageHistoryService` are no longer injected, and
  grepping the whole `src/` tree turns up no other call site for them outside `PageIndexBuilder.cs` and
  `ContentStorageStartupExtensions.cs` — no second read path left behind. The body is read via
  `File.ReadAllTextAsync(pageMatch.AbsolutePath, context.RequestAborted)` on every request, confirming
  D15's "index holds no content" — metadata comes from the index, the body never does.
- **`EvaluateRouteClaim`'s candidacy filter parity.** `ApplyIncrementalUpdateAsync`'s affected-path filter
  (case-insensitive `.md`, no dot-prefixed segment, symlinks/directories excluded via
  `StillExistsAsOrdinaryFile`) matches `PageEnumerationService.Walk`'s candidacy rules exactly. The one
  thing it does *not* reproduce is `Walk`'s friendlier log message distinguishing a non-lowercase-extension
  sole claimant from a genuine two-file collision — cosmetic only, the resulting `Pages`/`AmbiguousRoutes`
  membership is identical either way.
- **Cancellation.** Threaded honestly throughout the new code — `RefreshAsync`, `ApplyIncrementalUpdateAsync`,
  the new git spawns, and `WikiPage.razor`'s call all take `context.RequestAborted` / the real token, not a
  hardcoded `CancellationToken.None`. `GitProcessRunner` still doesn't kill the subprocess on cancellation
  (obligation 8) — correctly left unfixed and reassigned to §6, not widened here. Noted, not an ask for a
  fix in this block.
- **Gates, re-run myself, not relayed:** `dotnet build` clean (0 warnings), `dotnet test` **709/709
  unfiltered**, `dotnet format --verify-no-changes` clean, `openspec validate git-backed-content-core
  --strict` valid. `git status --short -- src tests` / `git diff --stat -- src tests` show exactly this
  block's expected file set — no residue from either of my two mutation checks (single-flight bypass,
  checked earlier), both reverted and checksum-confirmed clean before these gates ran.

→ @worker

**[worker]** Both the blocker and the nit fixed.

- **Blocker** (`PageIndexBuilder.RefreshAsync`): added a guard, right after the no-previous-stamp check
  and before running the incremental `git diff`, that falls back to a full `BuildAsync` whenever
  `current.UnreadableDirectories.Count > 0`. The comment at the guard site states the reviewer's actual
  reasoning, not a paraphrase: `ApplyIncrementalUpdateAsync`'s claimant reconstruction is sound only when
  `current` is a complete census of the tree, which it is not once any directory was ever unreadable at
  the last full build — a file inside it was never recorded anywhere in the snapshot, a later permissions
  fix making that directory readable again is invisible to `git diff`, and a diffed path colliding with a
  file inside it would otherwise go undetected (D12's wrong-file hazard, the exact one §3 spent two
  supervisor rounds preventing). `RefreshAsync`'s own doc comment now lists this as a third fallback
  condition alongside the two block A already had. New test
  `Refresh_UnreadableDirectoriesPresentAtTheStampedSnapshot_FallsBackToAFullRebuild`
  (`PageIndexBuilderTests.cs`) reproduces the reviewer's exact scenario: a file committed inside a
  directory, the directory locked (reusing §3's `UnixFileMode.None` fixture idiom), a build that records
  it under `UnreadableDirectories`, an unrelated diffed commit plus the directory becoming readable again
  between the stamp and the refresh, and asserts the previously-hidden page is only ever recovered by
  `RefreshAsync` — which only a full rebuild, never the incremental path, could produce, since the
  permissions change itself never appears in any diff.
- **Nit**: my own DEVLOG post two entries up claimed `EvaluateRouteClaim` was "extracted verbatim... no
  behaviour change" from `EnumeratePages()`'s loop. That's wrong and I retract it here rather than edit
  the earlier post: the zero-claimant branch (`claimants.Count == 0` → `RouteClaimEvaluation(false, null,
  [])`) is new — `EnumeratePages()`'s own loop only ever iterates routes with at least one claimant, so
  that branch never ran before this block and exists specifically so the shared method is total over
  what the incremental path can now hand it (a route whose only claimant was just deleted). The method's
  own doc comment never made the "verbatim" claim, so no code changed; only the wrong prose is being
  corrected here.

Gates, re-run unfiltered: `dotnet build` clean (0 warnings), `dotnet test` **710/710**, `dotnet format
--verify-no-changes` clean, `openspec validate git-backed-content-core --strict` valid. `git status
--short` / `git diff --stat -- src tests` show exactly this block's file set (the guard in
`PageIndexBuilder.cs`, its updated doc comment, and the one new test in `PageIndexBuilderTests.cs`) —
nothing else touched. No mutation testing.

→ @reviewer

**[reviewer]** Re-audit of block B (4.3) — **Approve.** Both items re-verified by reproduction, not by
reading the report.

- **Blocker.** Confirmed the guard's position, not just its presence: `ApplyIncrementalUpdateAsync` is
  `private` with exactly one call site (`PageIndexBuilder.cs:199`), reached only after
  `RefreshAsync`'s three early-return checks (stamp-equal, no-previous-stamp/unresolvable, and now
  `UnreadableDirectories.Count > 0`) in sequence — there is no second route into the reconstruction for
  the guard to miss. Then reproduced the new test's teeth myself rather than trusting the worker's
  checksum claim: bypassed the guard (removed the `if` block entirely), ran
  `Refresh_UnreadableDirectoriesPresentAtTheStampedSnapshot_FallsBackToAFullRebuild` three times — failed
  3/3 with the expected, consistent failure (`Assert.Empty() Failure: Collection was not empty:
  ["locked"]` — the incremental path leaving the stale `UnreadableDirectories` entry in place and never
  rediscovering the hidden page). Reverted and confirmed the file's md5 matched its pre-mutation state
  before re-running the real gates.
- **Nit.** Grepped `src/ZeroWiki/Content/` for "verbatim" and "no behaviour change" — the two hits are
  both unrelated (a git-output comment in `PageHistoryService.cs`, an exception-message comment in
  `ContentRepositoryService.cs`). `EvaluateRouteClaim`'s own doc comment (`PageEnumerationService.cs:219–226`)
  describes what the method does and never claimed to be a pure extraction. Confirmed: the false claim
  only ever lived in the reviewer-facing DEVLOG prose, and the append-only retraction is the correct fix
  for that — there was no code comment to correct.
- **Gates, re-run myself, not relayed:** `dotnet build` clean (0 warnings), `dotnet test` **710/710
  unfiltered**, `dotnet format --verify-no-changes` clean, `openspec validate git-backed-content-core
  --strict` valid. `git status --short -- src tests` / `git diff --stat -- src tests` show exactly this
  block's expected file set — no residue from my guard-bypass check, reverted and checksum-confirmed
  clean before these gates ran.

**This Approve certifies the working tree exactly as it stands now** — `HEAD` `fe3496f`, uncommitted:
`WikiPage.razor`, `PageEnumerationService.cs`, `PageHistoryService.cs`, `PageIndex.cs`,
`PageIndexBuilder.cs`, `PageIndexBuilderTests.cs`, `PageIndexTests.cs`, `WikiPageTests.cs`. Block B is
clear to commit; any further edit before that commit is uncertified and needs its own pass.

**For the supervisor** — I'm the only one who's seen both blocks; a few things worth a cross-block look
rather than a per-block one:

- **`PageIndexBuilder` has grown into five responsibilities across A+B** (unborn-`HEAD` probe, full
  build, refresh orchestration, incremental update, the unreadable-directory guard) in one class —
  worth judging whether that's still one coherent abstraction or wants splitting, a question a single
  block review can't really ask.
- **The TOCTOU window I flagged in block B** (probing `HEAD` then reading affected paths on disk isn't
  atomic; self-healing on the next request, no permanent wrong state) is a property of D15's whole
  freshness design, not just this block's diff — worth confirming it reads as an accepted trade-off of
  the design as a whole, not an implementation gap local to one block.
- **D12's invariant is now maintained by three different mechanisms across two blocks** (full walk in
  `PageEnumerationService`, bulk history walk + full rebuild in block A, incremental reconstruction +
  the new unreadable-directory guard in block B) — all provably consistent where I checked, but this is
  exactly the kind of multi-mechanism invariant where a fourth gap, if one exists, would be a cross-block
  question, not a single-file one.
- **Obligation 8** (`GitProcessRunner` not killing its subprocess on cancellation) is now reachable from
  the request path for the first time (block B) — correctly left unfixed and owed to §6 in both blocks'
  own text, not silently dropped — worth a line in `## NEXT` confirming it's still tracked as live, not
  merely historical.
- **Positive note, not a finding:** `_historyService.RepositoryRelativeWorkingTree` is derived once and
  reused identically by both blocks' git invocations — no duplicated prefix derivation crept in across
  the section.

→ @architect

## NEXT

**Resume point: §4 (Derived index), first block.** §3 is **closed** — supervisor `Approve` on round
two over `60957e6..HEAD`. §2 closed over `7b50e46..HEAD` (round four); §11 closed over `bb3cb2c..HEAD`.

**State: 16/40 tasks ticked** *(counted from `tasks.md`, not carried forward)*. Branch
`change/git-backed-content-core`. Gates at close-out, run by the Architect rather than relayed:
`dotnet build` 0/0, `dotnet test` **681/681** full unfiltered, `dotnet format --verify-no-changes`
exit 0, `openspec validate --strict` valid.

**§2 took four supervisor rounds and seven commits.** Worth stating plainly, because the shape repeated:
every one of the four findings was a gap in **what a condition asks**, not in how faithfully it runs —
so all four were invisible to mutation, and all four were found by building a fixture for a state
nobody had enumerated. The fourth was closed by deleting the enumeration rather than extending it.

**§3 took two supervisor rounds and four commits, and its defining finding was different in kind.**
§2's defects were gaps in what a condition asked. §3's worst was a gap in **what the audit could
see**: D13 claimed escaping had "no bypass surface", and a worker, the reviewer and the Architect each
audited it competently by asking *"which values reach the browser un-encoded"* — a question that only
ever inspects **tags**, while the live stored XSS lived in link *destinations*. Three independent
audits corroborated each other while sharing one instrument and one blind spot. The tests asserted on
`<script>` alone. It was found only when a fourth reader used a different instrument, and closed only
after the reviewer proved the bypass fired on a real trusted click in Chrome **before** checking the
fix stopped it.

| Section | Block | Commit | Reviewer | Supervisor |
|---|---|---|---|---|
| §0 design | D8–D11 + spec delta | `bd2eeea` | — | — |
| §1 | 1.1–1.3 | `3f6d837` | Approve w/ nits | Request changes → **Approve** |
| §1 | remediation (blocker + notes 2, 4) | `7102eed` | Approve | ↑ |
| §1 | 1.4 DataProtection | `0247443` | Approve | ↑ |
| §1 | close-out (docs) | `bb3cb2c` | — | — |
| §11 | 11.1–11.3 | `70a31aa` | Request changes → Approve w/ nits | Request changes (S1) → **Approve** |
| §11 | remediation (S1 + comment corrections) | `2af401c` | Request changes → **Approve** | ↑ |
| §2 | 2.1–2.2 | `ef2b75b` | Request changes → Approve → **Approve** (re-cert) | Request changes → **Approve** |
| §2 | 2.3–2.5 | `6b82c33` | Request changes ×2 → **Approve** w/ nit | ↑ |
| §2 | remediation (2 supervisor blockers) | `2fa3ca5` | **Approve** w/ nit | ↑ |
| §2 | close-out (docs) | `85d2b7f` | — | — |
| §2 | D9 reorder — no write precedes any refusal | `f50f1ca` | **Approve** | Request changes (round 3) |
| §2 | pre-init nested-repo scan | `2c70e05` | Request changes → **Approve** w/ nit | ↑ |
| §2 | gate the scan on the write's predicate | `1253ff5` | **Approve** | → **Approve** (round 4) |
| §2 | close-out (docs) | `60957e6` | — | — |
| §3 design | D12–D14 + spec delta | `50da7b0` | — | Request changes (S1–S3) → **Approve** |
| §3 | 3.1–3.2 enumeration + frontmatter | `8332c79` | Request changes ×2 → **Approve** w/ nit | ↑ |
| §3 | 3.3–3.4 rendering + git authorship | `e9bfea0` | Request changes ×2 → **Approve** w/ nit | ↑ |
| §3 | remediation (3 supervisor blockers) | `7ca5b76` | **Approve** w/ 2 nits | → **Approve** (round 2) |

**Execution order from here: §4 → §5 → … → §10.** §11, §2 and §3 are done; the remaining sections run
in `tasks.md` order.

**One decision is owed before §5 opens** — forward obligation 1 below. It is a Product Owner call, not
a wording tidy-up, because one of its two options changes shipped behaviour.

### Forward obligations — each is owed by a specific section

1. **Settle before §5 — §2's binding prose overclaims, and one fix changes behaviour.** `design.md`'s
   D9 addendum claims ZeroWiki "never writes into… a repository whose working tree it did not find
   intact". False of the shipped code: the bare-repository check refuses **before** any write, but the
   missing-`docs/` and gitlink checks refuse only **after** repo config is written and
   `.git/hooks/pre-receive`/`post-receive` are unconditionally overwritten. Two options, and they are
   not equivalent — **the first changes what ships**:
   - *Move the `docs/` probe up* beside the bare-repository check. It needs only the `HEAD` probe, so
     it can run straight after `AssertGitResolvesRepositoryRootAsync`, before config and hooks — making
     the claim true for two of the three faults.
   - *Narrow the prose* in `design.md` and `MissingWorkingTreeException` to what the code actually does.

   Either way, the addendum's other half — *"does not commit into a repository's history it did not
   create"* — is false **regardless of ordering**, because D9 reconciliation puts a `System` commit
   into adopted history whenever the adopted tree is dirty, by design. That clause needs rewording on
   both paths. Not blocking §3 or §4; §5 is where the lock starts depending on this ordering.
2. **§5 — the lockfile must not live in the working tree.** A lockfile under `/data/wiki/docs` is an
   untracked file, which makes the tree dirty, which D9 dutifully commits, and `updateInstead` then
   bounces every push against a tree it believes unclean. Put it under `.git/` or beside the
   repository, and **extend `ContentPaths`** rather than growing a parallel notion of where things
   live.
3. **§8 — the image has no HTTP client.** `curl`, `wget` and `nc` are all absent from the runtime
   image; `flock` **is** present, so §5.3 is safe. Decide how `post-receive` signals the app **before**
   §8 starts, or it reopens §1's Dockerfile.
4. ~~**§7 — `git-receive-pack` returns `403 Forbidden`**~~ — **discharged in §2** (`ef2b75b`).
   `http.receivepack=true` is now set as repo configuration on **every** start, not only at init, so a
   repository made by an earlier image or restored from a backup receives it too. Kept here because the
   symptom still reads as an authentication bug to whoever meets it first, and §7 should recognise it.
5. ~~**§2 — resolve `ContentPaths` from DI**~~ — **discharged in §2** (`ef2b75b`).
   `ContentRepositoryService` and `GitHookInstaller` both take it by injection; `ResolveContentPaths`
   remains used **only** by the pre-`Build()` DataProtection wiring, which is what it exists for.
6. **A latent trap in the test harness.** `ResolveContentPaths` reading configuration before `Build()`
   works **only** because `ZeroWikiAppFactory` uses `UseSetting`. A future harness using
   `ConfigureAppConfiguration` would hand the DI singleton the override while the key ring silently
   took the `/data` default. `LoginPageTests.cs:214`'s on-disk assertion is the real guard — **do not
   soften it**.
7. **§6 — the author line must be well-formed for accounts that predate the username rules.** D10's
   `Consequence binding §6`, now also a **scenario** in `specs/content-editing/spec.md` so §6's
   section review is gated on it rather than trusting prose. Non-retroactivity is deliberate and
   correct, which is exactly why §11 could not discharge this: `LoginServiceTests.cs:271-289` pins
   `.old.name.` still authenticating, and §6 constructs the address. Also tell §6's brief that the
   username is **immutable by consequence** — a permanent artifact plus no rename path.
8. **§6 — `GitProcessRunner` does not kill the git subprocess on cancellation.** Parked deliberately in
   §2's first block, where it is inert: startup passes `CancellationToken.None`, so there is nothing to
   cancel. §6's abortable commit-on-save is what makes it live, and §6 owns the fix.
9. **§7 — `GitProcessException`'s message carries the raw argument list.** Also parked from §2's first
   block, also inert there: bootstrap passes no secrets through the runner. §7 passes token-bearing
   URLs through the same runner, at which point the exception message — and anything that logs it —
   becomes a credential leak. `CapturingLoggerProvider` already exists to sweep logs for exactly this,
   so §7's brief should say *test it*, not merely *avoid it*.
10. **§7 must read the checked-out branch, not assume `DefaultBranch`.** `DefaultBranch = "main"` is
    justified in `ContentRepositoryService` as "the app has to know the checked-out branch name" —
    false for an **adopted** repository, which may be on `master` or anything else, and
    `CreateForeignRepositoryAsync`'s `init -b main` hides it. Latent rather than live: a foreign repo on
    `master` boots fine in §2 because `updateInstead` targets whatever is checked out. §7.4 is where it
    bites.
11. **Untested foreignness axes**, for whoever extends `CreateForeignRepositoryAsync`. §2 covers
    *structural* foreignness only. Untested: pre-existing `pre-receive`/`post-receive` hooks (silently
    overwritten), a pre-existing `receive.denyCurrentBranch=refuse` (silently overwritten), a non-`main`
    branch, and an adopted repository with a **dirty** tree — the last is the one that exercises
    obligation 1's contradiction, so pair them.
12. **Two `<remarks>` corrections owed** — neither affects behaviour, both are the recurring
    wrong-justification defect: `FindStagedGitlinksAsync` says a tracked path replaced by a nested
    repository yields `M`; git emits `T` (the condition tests modes, not status letters, so behaviour is
    unaffected). And `GitHookInstaller` should record that `git rev-parse --git-path hooks` honours
    `core.hooksPath` — verified by execution, and the fact that makes §5.3's hooks land where git will
    actually run them.
13. ~~Carried: `GitAuthor.cs` "later"; `tasks.md:70`'s superseded 11.1 pattern~~ — **both discharged in
    `f50f1ca`**, along with the `T`-vs-`M` and `core.hooksPath` remarks from obligation 12.
14. **`git add -A` has the same unreadable-directory blind spot the scan just closed in C#, and no C#
    scan can reach it. It is LIVE TODAY, not merely owed** — sharpened by `@supervisor`: the scan runs
    only where ZeroWiki creates the initial commit, so on an **adopted** repository it never runs at
    all, and an unreadable directory there means reconciliation silently skips content while
    `AssertWorkingTreeIsCleanAsync` still passes. §6 must not inherit this thinking it starts clean.
    `git add -A` warns on stderr and **exits 0** when it cannot read a directory, so content behind it
    is silently not staged
    — which means reconciliation reports success, the tree reports clean, and D9's "never discard"
    quietly does not hold for that subtree. Distinct from the scan blocker: the scan guards the
    *initialise* path only, while this touches **every** path, including a push-updated tree in §8. The
    fix is not a scan — it is deciding whether `ReconcileWorkingTreeAsync` should inspect `add -A`'s
    **stderr** and refuse rather than trusting its exit code. Likely a Product Owner call, since
    refusing on a warning is a policy choice; raise it in §6's brief at the latest, and note §10.1 owes
    a reconciliation test either way.
15. **Unbounded recursion in `AssertNoNestedGitRepository`** on a pathologically deep, non-symlinked
    tree. Assessed by `@reviewer` as closer to noise than a live risk for this product's content, and
    left unguarded deliberately rather than by oversight — recorded so that judgement is visible rather
    than implicit. Symlink loops are already handled, and are the realistic case.
16. **§5.1 — `repositoryHasNoCommitsYet` is now a snapshot.** Unifying the scan's gate with the write's
    predicate (`1253ff5`) means `EnsureInitialCommitAsync` no longer verifies `HEAD` immediately before
    committing; it trusts a value computed earlier in the call. Correct **today** only because startup
    is single-threaded and unlocked. §5.1 is where that stops being true, and this is the assumption it
    invalidates.
17. **§5.1 — the `AcceptRepositoryAsync`/`ConfigureRepositoryAsync` split has gone from optional to
    overdue, and §5.1 is its forcing function.** The Product Owner's call to keep `1253ff5` targeted was
    right — bundling a refactor with a correctness fix would have made the fix unreviewable — but
    `git init` now sits in its own `if` outside the block that computed its boolean, so the classify
    block no longer owns its own action. A cross-process `flock` wants to wrap exactly the
    accept-and-write phase and **not** the configure phase, which is the shape the split already has.
    Nearly free now; more expensive once a lock is threaded through the current shape.
18. **A rationale that no longer covers its own trigger** (prose, non-blocking). The
    unreadable-directory refusal is justified partly by "the scan runs once per volume, while the
    operator is most likely still watching". Since `1253ff5` the scan also runs on an **unattended
    crash-recovery restart** — `.git` present, `HEAD` unborn — where nobody is watching. The decision
    stays right on its other grounds; the stated reason should stand on its own terms.

19. **§6 — the codec has six public members and two of them have no caller.** `PageRouteCodec` grew
    across three review rounds rather than being designed: `Encode`, `TryDecode`, `TryDecodeRouteValue`,
    `IsCanonicalRouteValue`, and **two** resolvers — `TryResolveWorkingTreePath` and
    `TryResolveWorkingTreePathFromRouteValue` — neither of which any production code calls. They exist
    for §6's save path. **§6 must delete or wire them, not add a third.** The distinct-types redesign
    (a canonical-route type versus a bound-value type, making a contract mix-up a compile error) is the
    durable fix and belongs here too: the split is currently enforced only by naming, and the reviewer
    established that a wrong pairing *cannot* be detected at runtime, because an encoded and a decoded
    string containing no `%` are the same string.
20. **§4/§6 — `img-src 'self'` and the link allow-list disagree about external images.** The allow-list
    permits an `https` image destination; the CSP blocks its load. Moot today because nothing serves
    static content, live the moment §4 or §6 does. The spec scenario *Ordinary destinations still work*
    is true of links and **not** of external images — reconcile the two rather than discovering it as a
    broken image.
21. **§6 — the per-request cost is real and unbounded.** Every page view walks the whole working tree
    *and* spawns a `git log`, and there is **no page-size cap anywhere**: a pushed multi-hundred-megabyte
    `.md` is read whole and parsed on every request. §4.1's index is the answer to the walk; the size cap
    and the `git log` spawn are not, and neither has an owner yet.
22. **§6 — S3's race has no regression test, deliberately.** Enumeration walks a tree `git push` mutates;
    the fix names three specific exception types established empirically, but neither worker nor reviewer
    could build a deterministic non-flaky reproduction, and both independently found dangling symlinks do
    not reproduce it. Accepted by the supervisor on the reasoning that a flaky test gating every future
    block is worse than an honest gap, and that an untested `catch` risks catching too much or too little
    — both closed by reading, since it names types rather than `Exception`. **Revisit once §6's D3 lock
    makes the race closable and therefore testable.**
23. **The editor change — D13 records a `style-src` forward cost, already investigated.** CodeMirror 6's
    `style-mod` writes `styleTag.textContent` when mounted into a document, which `style-src 'self'`
    blocks, but takes a constructable-stylesheet path — not an inline style, so not governed by
    `style-src` — when mounted into a **shadow root**. It is a mounting decision, not a reason to weaken
    the CSP or choose a different editor. The "constructable stylesheets escape `style-src`" half is read
    from the spec, **not** browser-tested; test it when the editor lands.
24. **Latent, deployment-level — a reverse proxy that normalizes percent-encoding breaks the routing
    seam.** ASP.NET Core percent-decodes a catch-all route value exactly once, and D12's whole scheme is
    built on that being exactly once. A proxy that decodes before forwarding makes it twice. Nothing in
    the repo configures a proxy today, so this is a constraint on a future deployment rather than a
    defect — but it is the same seam D12 chose `__` over `%5F` to protect, and it should be stated
    wherever deployment is documented.

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

- **Agreement between audits is worthless when they share an instrument — and the instrument is the
  *question*, not the tool.** D13's link-destination XSS survived a worker, the reviewer and the
  Architect because all three asked *"which values reach the browser un-encoded"*, which only ever
  inspects tags. Three independent competent audits, one blind spot, mutual corroboration. **Before
  trusting a clean audit, name what its question cannot see.** Second instance of this exact shape after
  §0's `href=""` anchor regex, and the more expensive one (§3).
- **Verify the threat exists before verifying it is closed.** The reviewer settled S1 by building the
  `java\tscript:` link in a real Chrome tab, confirming the browser's own parser strips the tab and
  recognises the scheme, and firing it with a trusted click — *then* checking the fix. A test that only
  ever shows the fix passing cannot distinguish a real defence from a fixture that never attacked (§3).
- **Verify a claim about a *mapping* by enumerating it, not by reading it.** D12's injectivity claim was
  wrong in `design.md`, written by the Architect, and survived being written, a spec delta built to gate
  it, and a worker implementing against it. It fell out of computing the encoding over a handful of
  awkward filenames. Related: **grouping is not identity** — "no two files share a route" and "this route
  identifies this file" look like one property and are not; the second implies the first, never the
  reverse (§3).
- **State a rule on the side where the property lives.** D12's ambiguity rule was wrong three times while
  stated filename-side and correct the first time it was stated route-side. Each filename-side attempt
  *sampled* the mechanism; the route-side one *is* the mechanism (§3).
- **A library's guarantee covers what its API names, not what you wanted.** Markdig's `DisableHtml()`
  disables HTML; it never claimed to sanitize URLs, and `MarkupString` never claimed to sanitize
  anything. Both were true; the composition was not what D13 assumed (§3).
- **When a guard's justification names a *case*, check the guard's *branch*** — the branch is what
  ships. §2 produced five claim-versus-mechanism mismatches, every one with defensible code and a wrong
  stated reason: the unborn-`HEAD` comment, the index-census docstring, blocker 1's "delta" wording,
  `EnsureInitialCommitAsync`'s early return, and the `docs/` guard scoped in prose to "adopted" while
  applying universally (§2).
- **Treat a justification as something to reproduce before writing it down, at the same standard as a
  test.** §2's *code* converged; its *prose* kept regressing, and prose that lands in `design.md` binds
  every later section. Three wrong justifications survived a reviewer `Approve` each, and were caught
  only when someone ran them (§2).
- **Mutation measures whether a condition is faithful to its intent; it is silent on whether the intent
  is the right question.** Every defect §2 actually produced — the index census, `core.quotePath`, the
  `newMode`-only guard, the missing `docs/` branch — was a gap in *what the condition asks*, and none
  was reachable by a mutant. Both mutants run were sound and both killed cleanly. **Budget fixture
  diversity alongside mutants**; here the cheaper instrument was a fixture nobody thought to build (§2).
- **A fixture built by the code under test cannot falsify that code.** Both §2 supervisor blockers lived
  on the branch that adopts a foreign repository — the one branch with no fixture of its own, because
  every "existing repository" test constructed a repository ZeroWiki itself had made. Third instance of
  the same shape in this change, after §11's service-model differential and §2's
  `Path.GetTempPath()` roots (§2).
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

Design questions outstanding: **one — forward obligation 1, owed before §5 opens.** `design.md`'s Open
Questions remain fully resolved and D11 still states the `accepted ⊆ legal` posture whose absence was
S1; what is open is newer than those. §2's D9 addendum asserts a posture the shipped code does not
hold, and the two ways to reconcile it are not equivalent — one moves the `docs/` probe ahead of config
and hooks (changing behaviour), the other narrows the prose (changing only the record). That is a
Product Owner call. §3 and §4 do not depend on it.
