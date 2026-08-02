## Context

ZeroWiki is a zero-config Markdown wiki deployed as a Docker container over a mounted data volume. Content is authored from two independent surfaces — a browser editor and an Obsidian vault on a laptop — and both write the same Markdown files. Left uncoordinated, that is a classic dual-writer data-loss problem. The design makes the content folder a **git repository that is the single source of truth** for both content and authorship, so conflict handling, edit history, authorship, and laptop sync all fall out of one mechanism.

Platform is ASP.NET Core 10 using a Blazor Web App with Static SSR as the default render mode and Interactive Server islands where live behavior is needed (see D7). `git` is available in the container and the app shells out to it. Scale is small and known (a handful of collaborators, few concurrent editors), which makes a simple serialized write model acceptable.

## Goals / Non-Goals

**Goals:**

- The `docs/` working tree of a non-bare git repo is what the app reads and renders; nothing authoritative lives outside the repo.
- Every browser save is one authored commit; authorship is read back from git history.
- No lost updates: concurrent edits produce an honest conflict, never a silent overwrite.
- A laptop can sync via `obsidian-git` against a Smart HTTP remote using wiki credentials — no bespoke sync engine, no SSH.
- The working tree is always clean, so pushes are always acceptable, and survives crashes.

**Non-Goals:**

- Browser Markdown editor UX, Fountain rendering, and realtime collaborative editing (later changes). Commit-on-save deliberately lays the durable-checkpoint groundwork the realtime layer will sit on top of.
- Full invite-only signup and login UX (later change). This change assumes only enough identity to verify git-remote credentials and stamp commit authors.
- Multi-vault / multi-tenant hosting and high-concurrency throughput.

## Decisions

### D1 — Non-bare repo with `updateInstead`, not a bare hub + checkout

The repo is non-bare; `docs/` is the checked-out working tree the app renders. Pushes target the checked-out branch via `receive.denyCurrentBranch = updateInstead`, which updates the working tree automatically when it is clean.

*Why:* collapses "sync hub" and "render tree" into one directory, and `updateInstead`'s clean-tree precondition is exactly satisfied by commit-on-save. *Alternative considered:* bare repo + `post-receive` `checkout -f` into a separate tree — cleaner separation and better for multi-vault, but adds a second directory to keep in sync for no benefit at this scale.

### D2 — Smart HTTP via `git http-backend`, behind wiki auth

Expose `info/refs`, `git-upload-pack`, `git-receive-pack` by mapping those routes to the `git http-backend` CGI as a subprocess (streaming stdin/stdout, setting `GIT_PROJECT_ROOT`, `PATH_INFO`, `REMOTE_USER`). The routes sit behind the app's authentication (HTTP Basic over TLS, or a per-user token).

*Why:* reuses battle-tested git plumbing instead of reimplementing the pack protocol; reuses one auth mechanism, one port, one reverse proxy. `obsidian-git` runs on `isomorphic-git`, which speaks Smart HTTP + Basic auth and does **not** do SSH well — so HTTP is what the client ecosystem wants. *Alternatives:* SSH + `git-shell` (separate user/key management); a pure-managed git server (immature, reinvents the protocol).

### D3 — Single cross-process write lock (`flock`)

All repository writes are serialized through one `flock` on a lockfile, taken by **both** the app's commit path **and** git's `pre-receive`/`post-receive` hooks.

*Why:* browser commits run in the app process; pushes run in the `git http-backend` subprocess. An in-process `lock` cannot protect against the subprocess, so the shared primitive must be filesystem-level. Without it, a push can land between a save's `write` and `commit` and leave the tree dirty from two sources. At this scale, serialized writes are not a bottleneck.

### D4 — Optimistic concurrency via base-revision CAS

Each save carries the revision it started from; the server rejects (409) if the file has advanced. This one check covers browser-vs-browser and browser-vs-incoming-push collisions identically. The `post-receive` broadcast lets an open editor learn its base went stale *mid-edit* rather than only at save time.

*Why:* prevents lost updates without pessimistic page locks; conflicts become visible, recoverable events. *Alternative:* lock a page while it is open in an editor — worse UX, and doesn't help against Obsidian pushes.

### D5 — Authorship derived from git; no hand-maintained author field

"Who edited this" is answered by `git log`/`blame`. Browser commits are stamped with the logged-in user (author enforced server-side); incoming push commits carry a self-asserted git identity mapped back to an account by email. No `authors:` frontmatter is maintained.

*Why:* a hand-maintained field always drifts from reality; git is already the truth. Self-asserted identity on the Obsidian side is acceptable for an invite-only trusted cast. *Alternative considered and rejected:* a separate creative-credit field — deferred; not needed for this change.

### D6 — Derived index, rebuildable from the repo

Tags, titles, and last-edit metadata live in a lightweight index built from the repo, rebuildable from scratch. `post-receive` and each browser commit trigger incremental re-index of changed files.

*Why:* preserves "drop the folder and go" — nothing authoritative outside git.

### D7 — Blazor Web App / Static SSR with interactive islands, not global Blazor Server

The app is a Blazor Web App whose **default render mode is Static SSR** (components render server-side per request, no SignalR circuit). Only the components that need live behavior opt into **`InteractiveServer`** — initially the "changed on disk" indicator, later presence/realtime. Read, browse, and login/invite surfaces stay Static SSR. The Markdown editor is a CodeMirror JS island regardless of render mode.

*Why:* ZeroWiki is read-mostly and self-hosted; global Blazor Server would make every reader hold a SignalR circuit for interactivity they aren't using, which is at odds with a lightweight wiki (and adds reconnection-overlay UX and server-memory UI state). Static SSR gives the same C# component model with no circuit, and the render mode can be flipped on per-component, so realtime slots in later without a rewrite. *Alternatives considered:* full **Blazor Server** — rejected (circuit-per-reader cost for no benefit here); plain **Razor Pages/MVC + hand-rolled JS** — legitimately leaner on read pages, but gives up the component model and makes the future realtime path hand-wired SignalR + JS instead of a render-mode flip.

### D8 — One mounted volume at `/data`: `identity.db` beside a `wiki/` repository

Production is a single Docker volume mounted at `/data`. The identity store is `/data/identity.db`; the content repository is `/data/wiki`, a non-bare git repo whose `docs/` directory is the working tree the app renders — so `.git` lives at `/data/wiki/.git` and pages at `/data/wiki/docs/*.md`. Everything below `wiki/` is git-backed; `identity.db` deliberately is not. Both paths are configuration, defaulting to `/data`, so development points at a local gitignored folder without a second code path.

*Why:* one mount is the entire deployment story — `docker run -v zerowiki:/data` and nothing else, which is what "zero-config" has to mean for an operator. Keeping SQLite outside the repository keeps D6 honest for *content* without committing a binary database into page history, where it would bloat every clone and conflict on every push.

### D9 — A dirty tree at startup is always committed, never discarded, with no policy switch

*(Resolves the "commit-as-recovered vs discard" Open Question.)* Startup reconciliation always commits an unclean working tree as a recovery commit. There is no configured policy.

*Why:* a dirty tree can contain **untracked** files — content git has never seen. The obvious way to populate a new ZeroWiki is to copy a folder of Markdown onto the volume, which "point it at a folder and it Just Works" actively invites, so this is the normal case rather than an edge one. Discarding destroys that irrecoverably; an unwanted recovery commit is a `git revert`. One failure mode is permanent and the other is not, which decides it. A switch whose only alternative setting deletes user content on startup is a footgun, and zero-config argues against a knob at all.

Recovery commits are authored **`System <system@zerowiki.org>`** — a fixed identity belonging to the software rather than to the deployment, so it reads identically in every install and can never be mistaken for a member. This is the one author line that does *not* use D10's deployment domain.

### D10 — Commit authorship is a synthetic per-account identity, not a registered git email

Browser saves are authored `<Username> <username@<configured host domain>>`. The address is synthetic and need not be deliverable; a member is never required to disclose a real email address to edit the wiki. The domain is **configured for the deployment**, never read from the request's `Host` header — the header varies per request and is client-supplied, so deriving from it would both split one person's history between environments and let a spoofed `Host` write an attacker-chosen author line into history this design commits to never rewriting.

*Why:* the `GitEmails` table exists for the **inbound** direction — resolving a pushed commit's author back to an account. It is many-to-one by design, carries no primary flag, and has no `CreatedAt`, so "which address should we stamp?" has no answer the schema can give: alphabetical is the only available order, and it would silently rewrite a member's author line the day they add an address sorting earlier. A synthetic identity is deterministic, always exists (zero git emails is a legal account state), and is stable for the life of the account because no username-rename path exists. *Alternative considered:* a repo `.mailmap` maintained by the app, collapsing each member's registered addresses onto the canonical identity — genuinely tidier for `git shortlog`, but it makes the identity store a second writer into the repository, which is a coupling this change should not buy yet.

**Consequence binding §8.3:** the git-identity resolver MUST match the synthetic form **first**, ahead of the registered `GitEmails` rows. `/account` lets a member register any address, including another member's synthetic one; resolving synthetic-first means a squatted row can never capture someone else's attribution, and needs no change to the shipped `GitEmailService`.

### D11 — Username form: first and last character alphanumeric, minimum three characters

`CredentialPolicy.UsernamePattern` becomes `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z`, and a new `MinimumUsernameLength = 3` carries the length floor alongside the existing `MaximumUsernameLength = 64`.

*Why:* D10 makes the username an email localpart, and a leading or trailing dot is not a legal RFC 5322 dot-atom — so the shape has to be fixed where usernames are chosen, not patched at the point of use. The regex governs **shape only** and its bound is deliberately looser than the length cap, so an over-long username reports a length error rather than also being told its charset is wrong; the floor is a length rule for the same reason, mirroring how `MinimumPasswordLength` already works. Both quantifier bounds stay finite and both ends stay anchored, so matching remains constant-time (in fact one bounded run where there were two).

This amends the **archived** `user-accounts` capability and is carried as a spec delta in this change. It is not retroactive: the pattern runs only at bootstrap and invitation redemption, never at login, so no existing account stops working.

## Risks / Trade-offs

- **Dirty tree blocks all pushes** → Transactional save (`git checkout -- <file>` on commit failure, under lock) plus startup reconciliation (commit-as-recovered or discard) guarantee the tree returns to clean.
- **Push lands between write and commit** → D3's shared `flock` makes browser save and push mutually exclusive.
- **Commit-per-keystroke history explosion** → Debounce/coalesce to save-points; exactly one commit per save-point. No `--amend` after a commit could have been fetched, because rewriting published history breaks sync.
- **Self-asserted push identity** → Accepted for a trusted invite-only group; email→account mapping attributes correctly, unknown emails fall back to the raw identity without failing the push.
- **CGI subprocess resource/latency** → Small user base; acceptable. Bound concurrency with the same lock and normal request limits.
- **Serialized writes are a ceiling** → Known and accepted for this scale; revisited only if multi-vault or high-concurrency ever arrives.

## Migration Plan

This is greenfield; there is no data to migrate. Deployment: ship a container image with `git` installed; on first start against an empty volume, `git init` a non-bare repo, create `docs/`, set `receive.denyCurrentBranch=updateInstead`, install the `pre-receive`/`post-receive` hooks, and make an initial commit. Rollback is to redeploy the previous image; the volume (git repo) is untouched and remains the source of truth.

## Open Questions

- ~~Credential form for the git remote~~ **Resolved**: per-user revocable git access tokens (Basic-auth password), decided in the `invite-only-authentication` change; the login password is rejected for git.
- ~~Startup reconciliation policy for a dirty tree: commit-as-recovered vs discard~~ **Resolved (D9)**: always commit-as-recovered, authored `System <system@zerowiki.org>`, with no policy switch.
- ~~Attachments/binary assets (pasted images)~~ **Resolved**: binary assets live in the repository alongside the Markdown and ride the same commit-on-save path and the same write lock. Page enumeration and the derived index stay Markdown-only; `git http-backend` serves every blob regardless of type. **No work in this change** — there is no editor UX here, so the upload path arrives with the editor change that needs it.
- ~~Which git email stamps a browser save, and what stamps one for an account with none~~ **Resolved (D10)**: neither — authorship is a synthetic per-account identity.
