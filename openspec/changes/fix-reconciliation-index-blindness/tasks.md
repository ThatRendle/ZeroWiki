## 1. The census and the content comparison

- [ ] 1.1 Add a suppressed-index-entry census over the repository — `git ls-files -v`, selecting entries whose tag is lowercase (assume-unchanged) or `S` (skip-worktree), and no other tag (Decision 2)
- [ ] 1.2 Parse the census output safely for paths containing spaces, and establish by execution what `core.quotePath` does to those paths before relying on the parse (Risks)
- [ ] 1.3 For each suppressed entry, decide divergence by comparing `git hash-object -- <path>` against `git rev-parse HEAD:<path>`, covering all three divergent shapes: both resolve and differ, the working-tree file is absent, the path is not in `HEAD` (Decision 4)
- [ ] 1.4 Treat a suppressed entry whose content matches `HEAD` as no fault, so a harmless bit cannot refuse startup (Decision 4, spec scenario 2)

## 2. Wiring it into the invariant's two sites

- [ ] 2.1 Run the census in `ContentRepositoryService.ReconcileWorkingTreeAsync`, ordered against the existing gitlink and stderr refusals so each fault still produces its own specific diagnosis
- [ ] 2.2 Refuse startup on a divergent suppressed path with a message naming the path and the index state responsible, and never clear the operator's bit (Decision 3)
- [ ] 2.3 Apply the same check at `AssertWorkingTreeIsCleanAsync` and at the working-tree-clean health/self-check, so no site is left answering from the index (Decision 5)

## 3. Tests

- [ ] 3.1 Fixture that sets `--assume-unchanged` and `--skip-worktree` via real `git update-index` calls, then modifies the file on disk — the case no existing dirty-tree fixture can construct (Decision 6)
- [ ] 3.2 Test each divergent shape from 1.3 refuses startup and names the path; test the matching-content case starts normally
- [ ] 3.3 Test that untracked content is still reconciled into a recovery commit unchanged, so D9's load-bearing path is provably untouched (spec scenario 4)
- [ ] 3.4 Test the health/self-check reports the invariant violated rather than healthy for the same tree (spec scenario 3)
- [ ] 3.5 Mutation check, capped at 3 runs per the project's mutation policy: swap the content comparison back to `git status --porcelain` and confirm 3.2 dies; record the result and revert via a `cp` baseline restored by `trap`, never `git checkout --`

## 4. Record

- [ ] 4.1 State in the code, at the census, that `git diff --quiet HEAD` is *also* blinded — the reflex fix that reads index-free and is not (Context table)
- [ ] 4.2 Update `git-backed-content-core`'s archived DEVLOG reference or this change's DEVLOG with the measured table, so the next reader inherits the measurement rather than the claim
