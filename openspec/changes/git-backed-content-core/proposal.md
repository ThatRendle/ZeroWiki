## Why

ZeroWiki is a zero-config Markdown wiki: point it at a folder and it Just Works, deployed as a Docker container over a mounted volume. Two independent surfaces need to edit the same Markdown — a browser editor and an Obsidian vault on a laptop — which is a dual-writer problem that naive file I/O silently loses data to. Making the content folder a **git repository that is the source of truth** solves conflict handling, edit history, authorship, and laptop sync with one mechanism instead of four. This change establishes that spine; everything else (browser editor UX, Fountain, realtime collaboration, invite-only signup) builds on top of it.

## What Changes

- Treat the mounted content volume as a **non-bare git repository** whose `docs/` working tree is what the app reads and renders. The repo is the single source of truth for content **and** authorship.
- Read and render Markdown pages with **YAML frontmatter** (tags, etc.); build and maintain a lightweight index derived entirely from the repo so it can be rebuilt from scratch at any time (keeps "drop the folder and go").
- **Commit-on-save**: every browser save writes the file and creates exactly one git commit, authored as the logged-in ZeroWiki user. Authorship is derived from git history (`log`/`blame`) — there is no hand-maintained author field.
- **Optimistic concurrency**: saves carry the base revision they started from; a stale base is rejected (409) rather than clobbering, covering both browser-vs-browser and browser-vs-incoming-push collisions.
- **Single per-repo write lock** (cross-process `flock`) shared by the app's commit path and git's receive hooks, so browser commits and Obsidian pushes are mutually exclusive and the working tree is never left dirty.
- Expose the repo as a **Smart HTTP git remote** behind wiki authentication (via `git http-backend`), so `obsidian-git` on a laptop can clone/pull/push using wiki credentials — no bespoke sync engine, no SSH.
- Accept pushes into the checked-out branch via `receive.denyCurrentBranch = updateInstead`; a `post-receive` hook re-indexes changed files and broadcasts a "changed on disk" signal to open viewers. Genuine conflicts surface as non-fast-forward push rejections resolved in Obsidian.
- Map incoming commit identities (git email) back to ZeroWiki accounts so push-originated edits attribute correctly.
- **Startup reconciliation** + transactional saves guarantee the working-tree-clean invariant survives crashes, so pushes never bounce on a dirty tree.

## Capabilities

### New Capabilities
- `content-store`: git repository as the source of truth for content and authorship — repository layout, reading/rendering Markdown + YAML frontmatter, the derived index, and the working-tree-clean invariant with startup reconciliation.
- `content-editing`: the commit-on-save write path — optimistic concurrency (base-revision CAS), the single per-repo write lock, one-commit-per-save-point with git-derived authorship, and transactional/rollback save semantics.
- `git-sync`: the Smart HTTP git remote behind wiki auth — `git-upload-pack`/`git-receive-pack` endpoints, `updateInstead` push acceptance, re-index/broadcast hooks, `obsidian-git` compatibility, and git-identity → account mapping.

### Modified Capabilities
<!-- None — this is the first change; no existing specs. -->

## Impact

- **Platform**: ASP.NET Core 10, Blazor Web App with **Static SSR** as the default render mode and **Interactive Server** enabled only on the islands that need live behavior (e.g. the "changed on disk" indicator, future realtime). Not global Blazor Server — read/browse pages hold no SignalR circuit. New content, editing, and git-remote subsystems.
- **Runtime dependency**: `git` present in the Docker image; the app shells out to `git` and `git http-backend`.
- **Storage**: mounted volume holds the git working tree (`docs/`) plus a derived index. The account/identity store is delivered by the **`invite-only-authentication`** change, which is a **prerequisite** and must ship first — it supplies the logged-in identity for commit authorship, per-user git-token verification to protect the git remote, and the git-email → account mapping.
- **Deferred to later changes**: full browser Markdown editor UX, Fountain script rendering, realtime collaborative editing, and the invite/login flows. The `post-receive` broadcast intentionally lays groundwork for realtime.
