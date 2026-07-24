## 1. Project & container scaffolding

- [ ] 1.1 Create the ASP.NET Core 10 Blazor Web App solution with Static SSR as the default render mode (Interactive Server enabled per-island, not globally)
- [ ] 1.2 Add a Dockerfile that installs `git` and runs the app; document the mounted data volume
- [ ] 1.3 Configure the app to resolve the content volume path from configuration (env/mount)

## 2. Repository bootstrap & invariant

- [ ] 2.1 On startup, detect an existing non-bare git repo on the volume; serve from its `docs/` working tree
- [ ] 2.2 If the volume has no repo, `git init` a non-bare repo, create `docs/`, set `receive.denyCurrentBranch=updateInstead`, and make an initial commit
- [ ] 2.3 Install `pre-receive` and `post-receive` hooks into the repo on bootstrap
- [ ] 2.4 Implement startup reconciliation: if the working tree is dirty, commit-as-recovered or discard per configured policy, leaving a clean tree
- [ ] 2.5 Add a health/self-check that asserts the working-tree-clean invariant

## 3. Content read & render

- [ ] 3.1 Enumerate Markdown pages from the working tree and map paths to routes
- [ ] 3.2 Parse optional YAML frontmatter (tags, etc.); render body even when frontmatter is missing or malformed
- [ ] 3.3 Render Markdown to HTML in the Blazor shell
- [ ] 3.4 Expose per-page authorship/last-edit read from `git log`/`blame`

## 4. Derived index

- [ ] 4.1 Build an index (path, title, tags, last-edit) from the repository
- [ ] 4.2 Support full rebuild of the index from the repo when absent or deleted
- [ ] 4.3 Incrementally update index entries for changed files

## 5. Write lock

- [ ] 5.1 Implement a single cross-process `flock` on a repo lockfile
- [ ] 5.2 Make the app commit path acquire/release the lock around write+commit
- [ ] 5.3 Make the `pre-receive`/`post-receive` hooks acquire/release the same lock

## 6. Commit-on-save

- [ ] 6.1 Save endpoint accepts content plus the declared base revision
- [ ] 6.2 CAS check: reject with 409 when the file has advanced beyond the base revision
- [ ] 6.3 On success, write file, `git add`, and create one commit authored as the logged-in user
- [ ] 6.4 Coalesce rapid edits to a save-point (debounce/explicit save) — one commit per save-point
- [ ] 6.5 Transactional save: on commit failure, restore the file so the tree stays clean

## 7. Smart HTTP git remote

- [ ] 7.1 Map `info/refs`, `git-upload-pack`, `git-receive-pack` routes to the `git http-backend` CGI subprocess (stream stdin/stdout, set env)
- [ ] 7.2 Protect the git routes with wiki authentication (Basic over TLS or per-user token); refuse unauthenticated access
- [ ] 7.3 Verify authenticated clone/fetch/push against the running app
- [ ] 7.4 Confirm `updateInstead` fast-forward push updates the working tree; confirm non-fast-forward push is rejected

## 8. Push reactions & identity

- [ ] 8.1 `post-receive` hook triggers re-index of changed files
- [ ] 8.2 `post-receive` broadcasts a "changed on disk" signal to connected viewers of affected pages (SignalR)
- [ ] 8.3 Implement git-email → account mapping; attribute push-originated edits, falling back to raw identity for unknown emails

## 9. Obsidian sync verification

- [ ] 9.1 Document configuring an Obsidian vault with `obsidian-git` pointed at the ZeroWiki remote using wiki credentials
- [ ] 9.2 End-to-end test: edit in browser → pull in Obsidian; edit in Obsidian → push → see update and broadcast in browser
- [ ] 9.3 Verify a genuine conflict surfaces as a non-fast-forward push rejection resolvable in Obsidian

## 10. Tests

- [ ] 10.1 Unit/integration tests for CAS rejection, transactional rollback, and startup reconciliation
- [ ] 10.2 Concurrency test: interleaved browser save and push are serialized and never leave a dirty tree
- [ ] 10.3 Index rebuild-from-repo test
