## Why

ZeroWiki's startup reconciliation (D9) decides whether the working tree diverges from `HEAD` by asking
the git **index** — `git add -A`, then `git diff --cached --quiet`, then `git status --porcelain`. A
single `git update-index --assume-unchanged <path>` (or `--skip-worktree`) recorded in that index blinds
**all three** instruments at once, so reconciliation reports a clean tree over a tracked file that
differs from `HEAD`. That is precisely the divergence D9 exists to repair, and
`SaveOutcome.RollbackFailed` names D9 as its recovery mechanism — so the save path's worst outcome is
documented as recoverable by an instrument that cannot see the case.

Reproduced by execution on git 2.55.0 before this proposal was written, in a throwaway repository shaped
like ZeroWiki's:

| instrument | tracked file modified | same file, `--assume-unchanged` set |
|---|---|---|
| `git status --porcelain` | `M  docs/page.md` | *(empty)* |
| `git diff --cached --quiet` | exit `1` (staged changes) | exit `0` (reconciliation returns early) |
| `git hash-object -- <path>` vs `HEAD:<path>` | differs | **differs** |

`git-backed-content-core` §6 met this same blind spot in the *save* path's rollback guard and replaced
it with the content-level comparison in the right-hand column — `git hash-object`, which never consults
the index. It deliberately did not carry that fix into D9. This change does.

**Two measurements deliberately bound the severity, because the record it inherits overstated it.** Both
were run, not reasoned:

- **Untracked content is not blinded.** `?? docs/newpage.md` is still reported with `--assume-unchanged`
  set on a sibling. D9's load-bearing case — a folder of Markdown copied onto the volume, arriving
  untracked — is unaffected. The blind spot is **tracked divergence only**.
- **`updateInstead` does not share the blind spot, so this is not data loss.** A fast-forward push
  against a blinded-dirty tree was **refused** — `error: Entry 'docs/page.md' not uptodate. Cannot
  merge.` / `! [remote rejected] main -> main` — and the uncommitted local edit survived byte-for-byte.

What the defect actually costs is therefore an **availability and diagnosis** failure, not a destroyed
edit: the app starts, asserts its working-tree-clean invariant, and reports healthy, while every push
from every Obsidian vault is rejected forever by a message naming a file the app's own health check
simultaneously calls clean. Two of this system's own truth-tellers contradict each other and neither is
wrong on its own terms. Nothing in ZeroWiki sets `--assume-unchanged`; the realistic origins are an
operator or an external tool touching the volume's repository, or a damaged index — which is why this is
robustness and honest diagnosis rather than a live incident.

## What Changes

- Reconciliation and the working-tree-clean assertion verify tree-versus-`HEAD` divergence with an
  instrument the index cannot suppress, rather than trusting `add`/`status`/`diff --cached` alone.
- The health/self-check that asserts the invariant reports on the same footing, so "healthy" cannot mean
  "the index told me so".
- When a blinded path is found, the system says so specifically — naming the path and the index bit
  responsible — instead of reporting a clean tree or emitting a generic refusal. An operator meeting the
  bouncing-push symptom gets the cause from ZeroWiki rather than from a git error they must decode.
- **D9's policy is unchanged**: an unclean tree is still always committed, never discarded, with no
  policy switch. This change replaces an *instrument*, not a decision.
- Regression coverage that fails if the index-independent instrument is swapped back for an
  index-consulting one — the defect's whole character is that the ordinary tests still pass.

Not in scope: the save path's own guard (already fixed), the rest of the inherited-git-config class
(`core.safecrlf`, `core.fileMode`, `core.symlinks`, and their siblings — separately owned), and any
change to what reconciliation *does* once divergence is seen.

## Capabilities

### New Capabilities

*(none — this change tightens an existing requirement)*

### Modified Capabilities

- `content-store`: gains an **ADDED** requirement — *Working-tree verification does not depend on the
  git index* — standing alongside the existing **Working-tree-clean invariant** rather than rewriting
  it. It carries the same posture that invariant already takes toward a git invocation that could not
  read part of the tree (*a clean report must mean a clean tree*) to an invocation that ran perfectly
  and was answered by a suppressed index. `ADDED` rather than `MODIFIED` for two reasons: this is a new
  concern about the *instrument* rather than a change to what the invariant demands, and the baseline
  requirement does not exist in `openspec/specs/` yet — it arrives when `git-backed-content-core`
  archives, so a `MODIFIED` delta would have nothing to copy from today.

## Impact

- **Ordering — this change cannot be applied before `git-backed-content-core` archives.** The code it
  repairs (`src/ZeroWiki/Content/ContentRepositoryService.cs`) and the requirement it amends
  (`content-store`) both exist **only** on `change/git-backed-content-core`, unmerged and absent from
  `main` and from `openspec/specs/`. A change branched from the default branch per the apply workflow
  would find neither. Verified: `git cat-file -e main:src/ZeroWiki/Content/ContentRepositoryService.cs`
  fails, and `openspec/specs/` holds only `authentication`, `invitations`, `request-lifecycle`, and
  `user-accounts`.
- **Code**: `ContentRepositoryService.ReconcileWorkingTreeAsync` and `AssertWorkingTreeIsCleanAsync`;
  the startup self-check that asserts the invariant.
- **Precedent to reuse, not reinvent**: `PageSaveService`'s `git hash-object` comparison already
  establishes the instrument, its rationale, and its failure modes in this codebase.
- **Tests**: `tests/ZeroWiki.Tests/Web/ContentRepositoryStartupTests.cs` and the reconciliation fixtures
  — which today construct every "dirty tree" through git's own index, so none of them can currently
  produce the case at all.
- **No API, dependency, or deployment surface changes.** No new package, no Dockerfile change, no
  configuration key.
