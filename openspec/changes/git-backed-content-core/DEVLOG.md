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

## NEXT

**Resume point: §2 (Repository bootstrap & invariant), first block.** §1 is **closed** — supervisor
`Approve` on round two over `bd2eeea..HEAD`.

**State: 4/39 tasks ticked** *(counted from `tasks.md`, not carried forward)*. Branch
`change/git-backed-content-core`. Gates at close-out: `dotnet build` 0/0, `dotnet test` **394/394**
full unfiltered suite, `dotnet format --verify-no-changes` clean, `openspec validate --strict` valid.

| Section | Block | Commit | Reviewer | Supervisor |
|---|---|---|---|---|
| §0 design | D8–D11 + spec delta | `bd2eeea` | — | — |
| §1 | 1.1–1.3 | `3f6d837` | Approve w/ nits | Request changes → **Approve** |
| §1 | remediation (blocker + notes 2, 4) | `7102eed` | Approve | ↑ |
| §1 | 1.4 DataProtection | `0247443` | Approve | ↑ |

**Execution order from here: §11 → §2.** §11 (username form) is small, isolated, and foundational to
D10's synthetic identity, so it lands before the repository work begins rather than after it.

### Forward obligations — each is owed by a specific section

1. **§5 — the lockfile must not live in the working tree.** *(supervisor note 3, and the one most
   likely to bite.)* A lockfile under `/data/wiki/docs` is an untracked file, which makes the tree
   dirty, which D9 then dutifully commits, and `updateInstead` bounces every push against a tree it
   believes is unclean. Put it under `.git/` or beside the repository, and **extend `ContentPaths`**
   rather than growing a parallel notion of where things live.
2. **§8 — the image has no HTTP client.** *(supervisor note 1.)* `curl`, `wget` and `nc` are all
   absent from the runtime image; `flock` **is** present, so §5.3 is safe. Decide how `post-receive`
   signals the app **before** §8 starts, or it reopens §1's Dockerfile.
3. **§7 — `git-receive-pack` returns `403 Forbidden` in this image.** Found by the supervisor while
   confirming the `safe.directory` fix. It is git's export policy (`http.receivepack` unset), **not**
   an ownership or auth problem — setting `http.receivepack=true` flips the identical invocation to
   200. Recorded because it will read as an authentication bug to whoever meets it first, and the
   hours lost to that diagnosis are the whole reason this note exists. Belongs in §2's repo config.
4. **§2 — resolve `ContentPaths` from DI.** The registered singleton currently has **zero consumers
   in `src/`**; only `ResolveContentPaths` at `Program.cs:88` reads the data root, because
   DataProtection must be configured before `Build()`. §2's brief should say *inject it*, so the
   singleton acquires the consumers that justify it.
5. **A latent trap in the test harness.** `ResolveContentPaths` reading configuration before
   `Build()` works **only** because `ZeroWikiAppFactory` uses `UseSetting`. A future harness using
   `ConfigureAppConfiguration` would hand the DI singleton the override while the key ring silently
   took the `/data` default. `ResolveContentPathsMatchesTheDiRegisteredSingleton` does **not** cover
   this; `LoginPageTests.cs:214`'s on-disk assertion is the real guard — **do not soften it**.

### Standing rules earned in §0–§1

- **Any regex harness must carry an instrument self-check** — Perl interpolated `$\` out of a
  pattern and produced a fully self-consistent wrong answer (§0).
- **`git checkout --` / `git restore --` are not valid revert primitives** for a tree holding
  uncommitted work: they restore from `HEAD` and take uncommitted changes with them. Restore from
  content saved before mutating, and verify by checksum (§1).
- **A raw checksum is not a valid instrument for a live WAL-mode SQLite file** — checkpointing
  churns pages with no logical change. Compare `sqlite3 .dump` or row counts (§1).
- **`safe.directory` is `--system`, set as root before the `USER` switch.** HOME-scoped `--global`
  dies in §7's CGI subprocess. Anything changing the container user or repository ownership has to
  keep this true.
- **Both instrument failures in this change so far were in the harness, not the code.** §2 onward
  carries heavier mutation testing; assume the measurement is wrong before assuming the finding is
  real.

Design questions outstanding: **none.** `design.md`'s Open Questions section is fully resolved
(D8–D11 plus the two inherited as already-resolved).
