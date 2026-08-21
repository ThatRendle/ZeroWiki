## 1. The census and the content comparison

- [x] 1.1 Add a suppressed-index-entry census over the repository — `git ls-files -v`, selecting entries whose tag is lowercase (assume-unchanged) or `S` (skip-worktree), and no other tag (Decision 2)
- [x] 1.2 Parse the census output safely for paths containing spaces, and establish by execution what `core.quotePath` does to those paths before relying on the parse (Risks)
- [x] 1.3 For each suppressed entry, decide divergence by comparing `git hash-object -- <path>` against `git rev-parse HEAD:<path>`, covering all three divergent shapes: both resolve and differ, the working-tree file is absent, the path is not in `HEAD` (Decision 4)
- [x] 1.4 Treat a suppressed entry whose content matches `HEAD` as no fault, so a harmless bit cannot refuse startup (Decision 4, spec scenario 2)

## 2. Wiring it into the invariant's two sites

> **Decision 7 (Product Owner, 2026-08-21):** section 1 *observes* — path, index mode, `HEAD` mode,
> comparison outcome — and decides nothing. **Every fault decision below is section 2's**, because
> every policy a verdict must agree with lives here: `FindStagedGitlinksAsync`'s deliberately narrow
> contract, 2.1's ordering against the existing stderr refusals, and 2.2's message.

> The re-cut itself (`FindSuppressedEntryFaultsAsync` → `FindSuppressedIndexObservationsAsync`, plus
> the `HEAD` mode via `git ls-tree`) lands in **section 1's** remediation, not here — closing section 1
> around a verdict its own supervisor has ruled wrong, and repairing it afterwards, would make its
> `Approve` mean nothing. It ticks no box: section 1's boxes are already ticked.

- [x] 2.1 Run the census in `ContentRepositoryService.ReconcileWorkingTreeAsync`, ordered against the existing gitlink and stderr refusals so each fault still produces its own specific diagnosis
- [x] 2.2 Refuse startup on a divergent suppressed path with a message naming the path and the index state responsible, and never clear the operator's bit (Decision 3). **A suppressed entry that is merely present is not a fault** — an already-adopted gitlink whose `HEAD` mode is also `160000`, like a symlink whose link text matches, starts normally; the two false refusals section 1 produced were both this mistake (Decision 7, spec scenario 2)
- [x] 2.3 Apply the same check at `AssertWorkingTreeIsCleanAsync` — which *is* this system's working-tree-clean self-check, there being no separate health-check surface — so the assertion is not left answering from the index after reconciliation has stopped doing so (Decision 5)

## 3. Tests

- [ ] 3.1 Fixture that sets `--assume-unchanged` and `--skip-worktree` via real `git update-index` calls, then modifies the file on disk — the case no existing dirty-tree fixture can construct (Decision 6)
- [ ] 3.2 Test each divergent shape from 1.3 refuses startup and names the path; test the matching-content case starts normally
- [ ] 3.3 Test that untracked content is still reconciled into a recovery commit unchanged, so D9's load-bearing path is provably untouched (spec scenario 4)
- [ ] 3.4 Test that `AssertWorkingTreeIsCleanAsync` itself refuses the same tree — reached directly, with reconciliation's own census neutralised, so the test dies if only 2.1/2.2 were fixed and 2.3 was not (spec scenario 3)
- [ ] 3.5 Mutation check, capped at 3 runs per the project's mutation policy: swap the content comparison back to `git status --porcelain` and confirm 3.2 dies; record the result and revert via a `cp` baseline restored by `trap`, never `git checkout --`

- [ ] 3.6 Test that a suppressed entry which is merely *present* and unchanged starts normally, for **each** shape section 1 got wrong: an already-adopted gitlink (`HEAD` mode also `160000`) and an unmodified symlink. These are the two false refusals; a test that only covers regular files is what let both through (Decision 7)
- [ ] 3.7 **Human-in-the-loop, not tickable by an agent (Decision 8, workflow §4):** build the image and exercise the suppressed-entry paths — symlink, gitlink, EACCES, non-ASCII — once inside the Linux/glibc container, because `strerror` translation is a glibc behaviour that this host can neither reproduce nor observe, and the EACCES branch is reachable in production (the container runs non-root, `Dockerfile:56`). The Architect hands the Product Owner exact commands and expected output and **waits for their confirmation**

## 4. Record

- [ ] 4.1 State in the code, at the census, that `git diff --quiet HEAD` is *also* blinded — the reflex fix that reads index-free and is not (Context table)
- [ ] 4.2 Update `git-backed-content-core`'s archived DEVLOG reference or this change's DEVLOG with the measured table, so the next reader inherits the measurement rather than the claim
- [ ] 4.3 State at `InvariantLocale` that the glibc `strerror` translation it defends against was **reasoned, not observed** — Decision 8 records this, but `design.md` is not what a reader of `ContentRepositoryService.cs` sees, and the remark there still states it as settled fact. This is 4.2's own intent applied to the one claim in the file with no measurement behind it (supervisor, section 1 round three)
