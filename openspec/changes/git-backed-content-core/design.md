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
- Startup reconciliation policy for a dirty tree: commit-as-recovered vs discard — which is the safer default?
- Attachments/binary assets (pasted images): confirm they live in the repo alongside Markdown and are covered by the same commit-on-save path.
