## Why

Opening a ZeroWiki clone as an Obsidian vault makes Obsidian write a `.obsidian/` configuration
directory into the vault — including `workspace.json`, which it rewrites whenever a pane moves. Startup
reconciliation stages the repository root unscoped (`ContentRepositoryService.cs:792`, `git add -A`) and
the repository ships no `.gitignore`, so one editor's local UI state becomes wiki content, is committed
under whichever identity reconciled it, and is pushed to every other vault that clones the remote.

Established by the Product Owner's `obsidian-git` verification run: `.obsidian/` and its plugin payload
landed in the content repository within minutes of opening the vault, and the vault then produced a
steady stream of commits carrying nothing but editor state.

It is not a correctness fault — `PageEnumerationService.Walk` skips dot-prefixed entries
(`PageEnumerationService.cs:162`), so none of it is ever served as a page. It is a fault against the
product's premise: the repository is meant to be the authoritative store of *page content and
authorship*, and it is currently accumulating one user's window layout as tracked history that everyone
else pulls.

Fixing it by telling users to add a `.gitignore` themselves would be config — the thing this product
exists not to have.

## What Changes

- **Repository bootstrap seeds a `.gitignore`** ignoring `.obsidian/`, committed as part of the initial
  commit that already seeds `docs/.gitkeep`.
- **Ignore-only. Nothing already tracked is removed.** A repository that has already committed
  `.obsidian/` keeps it. Untracking it would delete those files out of every vault on its next pull,
  which is a destructive act taken on a user's local editor configuration without asking — a far worse
  outcome than the noise it cleans up.
- **Repositories with history are left alone.** The discriminator is commit history, not the presence
  of a `.git` directory: a repository that already has commits is not modified to add or amend a
  `.gitignore`, while a volume whose `HEAD` is unborn has no history and is initialized and seeded. The
  system does not edit a repository whose history it did not start. Where such a volume already carries
  a root `.gitignore`, our line is appended to it and nothing already there is replaced.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `content-store`: **Git repository is the source of truth** — the initialization scenario gains the
  seeded ignore rule, so a freshly initialized repository does not accumulate editor configuration as
  tracked content.

## Impact

- `src/ZeroWiki/Content/ContentRepositoryService.cs` — the bootstrap path that already writes
  `docs/.gitkeep` and makes the initial commit.
- Tests: bootstrap coverage asserting the seeded rule exists, that an editor-config path is ignored
  rather than staged by the unscoped `git add -A`, and that an adopted repository is not modified.
- No change to enumeration, routing, the save path, or the Smart HTTP remote.

**Sequencing constraint.** This change amends code and a capability that exist only on
`change/git-backed-content-core` — `content-store` has not been promoted to `openspec/specs/`, and
`main` has neither the bootstrap path nor the reconciliation it protects. It cannot be applied before
that change archives, and is queued behind the same event as `fix-changed-on-disk-broadcast` and
`fix-reconciliation-index-blindness`.
