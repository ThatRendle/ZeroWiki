## Context

D9's startup reconciliation establishes the working-tree-clean invariant that `receive.denyCurrentBranch
= updateInstead` depends on. It does so through the git **index**: `git add -A`, then
`git diff --cached --quiet` to decide whether anything was staged, then `AssertWorkingTreeIsCleanAsync`'s
`git status --porcelain` as the closing assertion. Every one of those asks the index what changed.

An index entry marked `--assume-unchanged` or `--skip-worktree` tells git to stop trusting the
filesystem for that path. Measured on git 2.55.0 in a repository shaped like ZeroWiki's, with one
tracked file modified on disk:

| instrument | ordinary dirty file | same file, entry suppressed |
|---|---|---|
| `git status --porcelain` | `M  docs/page.md` | *(empty)* |
| `git diff --cached --quiet` after `add -A` | exit `1` | exit `0` |
| `git diff --quiet HEAD` | exit `1` | exit `0` |
| `git hash-object -- <path>` vs `git rev-parse HEAD:<path>` | differs | **differs** |
| `git ls-files -v` tag for the path | `H` | `h` / `S` |

Four instruments blinded, two that still see. `git diff --quiet HEAD` is on that list deliberately: it
is the natural "just compare the working tree to `HEAD`" reflex, it reads as index-free, and it is not —
anyone fixing this by reaching for it would produce a patch that passes review and changes nothing.

`git-backed-content-core` §6 met the same blind spot in the save path's rollback guard and replaced it
with the content-level comparison (`PageSaveService`'s `git hash-object`). This change carries that
choice into D9 without disturbing D9's policy.

## Goals / Non-Goals

**Goals:**

- A clean report from reconciliation, its assertion, and the health check means a clean tree — not "the
  index said so".
- An operator meeting the symptom (every push rejected with `Entry '<path>' not uptodate. Cannot
  merge.`) learns the cause from ZeroWiki naming the path and the index state, not by decoding git.
- Cost nothing on the overwhelmingly normal repository, where no entry is suppressed at all.
- Regression coverage that dies if the index-independent instrument is swapped back for an
  index-consulting one.

**Non-Goals:**

- Changing D9's policy. An unclean tree is still always committed, never discarded, no policy switch.
- Making reconciliation *commit* a suppressed path's divergence (see Decision 3 — it refuses instead).
- The save path's own guard (already fixed in §6) and the rest of the inherited-git-config class.
- A general index-free reimplementation of `git status`. This targets exactly the suppression bits.

## Decisions

### Decision 1 — Keep `add -A`/`status --porcelain`; add a suppressed-entry census beside them

Not a replacement. For every path whose index entry is *not* suppressed, the existing instruments are
correct, cheap, and already carry §6's hard-won stderr and gitlink handling. Rewriting reconciliation
around a content-level whole-tree walk would pay a per-file `hash-object` on every start to close a case
that is normally empty, and would put §6's accumulated refusals through a rewrite they did not ask for.

The census is one subprocess: `git ls-files -v`, which tags each index entry. Any suppressed entry is
then content-compared, and only then. On a repository with no suppression the census produces no rows
and the added cost is a single `git ls-files` invocation per start — measured empty in the probe.

### Decision 2 — The census rule is "tag is lowercase, or tag is `S`" — not "tag is not `H`"

`git ls-files -v`'s tags are not two-valued. `H` is a cached entry, `S` is skip-worktree, and
**assume-unchanged is expressed by lowercasing whatever tag the entry would otherwise have** — so an
ordinary assume-unchanged entry reads `h`. But `M` (unmerged), `R` (removed), `C` (modified/created),
`K` (to be killed) and `?` (other) are also non-`H`, and none of them is a suppression: they describe
states the existing instruments already see and already handle.

A census written as `tag != "H"` would therefore refuse to start on an unmerged index — a condition
reconciliation has its own answer for — and the refusal would be attributed to the wrong cause. The rule
is the specific one: **lowercase tag (assume-unchanged) or `S` (skip-worktree)**. Both forms were
produced and observed in the probe (`h docs/a.md`, `S docs/b.md`, `H docs/c.md`).

### Decision 3 — A suppressed path that diverges refuses startup; it is not silently reconciled

Two candidates, and the deciding argument is whose intent gets overridden:

- **Clear the bit and commit** (D9's instinct — never discard, always commit) requires
  `git update-index --no-assume-unchanged` on a bit *the operator set deliberately*. ZeroWiki never sets
  these bits, so their presence is always someone's decision. Reconciliation would be silently undoing
  an external actor's explicit instruction to git, on startup, without asking.
- **Refuse to start, naming the path and the tag** matches the precedent already established for every
  other structurally-odd tree: a nested git repository refuses, an unreadable directory refuses. Both
  are cases where committing would produce a technically-clean tree that misrepresents what is on disk —
  exactly this case. Nothing is discarded: the working tree is left untouched for the operator.

Chosen: **refuse**. It is also the only option that keeps the diagnosis honest, since the operator
who set the bit is the one person able to say what they intended it to protect.

*Recorded because it is a live alternative, not a strawman:* if the Product Owner would rather a
suppressed-but-divergent path be committed, this decision — not the spec's invariant — is the thing to
change, and the spec scenario *"A suppressed index entry cannot hide a divergent file"* would need its
**THEN** rewritten with it.

### Decision 4 — Divergence is decided per path, and three shapes count as divergent

For each suppressed path, compare `git hash-object -- <path>` against `git rev-parse HEAD:<path>`:

1. **Both resolve and differ** — the ordinary case, and the one measured.
2. **The working-tree file is absent** — `hash-object` fails. A suppressed deletion is still a
   divergence, and is exactly the shape that makes `updateInstead` bounce.
3. **The path does not exist in `HEAD`** — `rev-parse HEAD:<path>` fails. The entry is in the index but
   uncommitted; suppressed, it will never be staged or committed by reconciliation.

A suppressed entry whose content *matches* `HEAD` is not a fault and must not refuse startup — the
invariant it protects is intact, the bit is merely set. The spec pins this as its own scenario precisely
because a guard that refuses on the presence of a bit rather than on divergence would brick startup for
a harmless configuration.

### Decision 5 — The health/self-check reports on the same instrument

The invariant is asserted in two places — startup and the self-check — and a fix applied to one leaves
the other answering from the index. §5 and §6 both produced findings of exactly this shape (a principle
applied to the case that prompted it and not to its siblings), so both sites move together in one block.

### Decision 6 — The regression test must construct the case without git's index cooperation

Every existing "dirty tree" fixture builds its dirtiness through git itself, so none of them can produce
this case — the same "a fixture built by the code under test cannot falsify that code" shape this
project has now hit three times. The test sets the bit with a real `git update-index` call, modifies the
file on disk, and asserts the refusal. The mutation that must kill it: swap the content comparison back
to `status --porcelain` and confirm the test dies.

### Decision 7 — Section 1 observes; section 2 judges (Product Owner decision, 2026-08-21)

Added after section 1 failed two supervisor reviews with the *same defect class*: round one refused
startup on a harmless symlink, round two on a harmless gitlink. The cause was not carelessness — it is
structural. Section 1 was required to render a **verdict** ("is this a fault?") while every policy that
verdict must agree with lives in section 2: `FindStagedGitlinksAsync`'s deliberately narrow contract
(an adopted submodule advancing is *not* a fault — "exactly the bricking this check exists to
prevent"), 2.1's ordering against the existing stderr refusals, and 2.2's refusal message. A section
cannot be held to policies it cannot see.

**Section 1's output is therefore an observation, not a verdict.** Per suppressed entry it reports the
path, the index mode, the `HEAD` mode, and the comparison outcome. It decides nothing.
`FindSuppressedEntryFaultsAsync` becomes `FindSuppressedIndexObservationsAsync`. **Section 2 owns every
fault decision**, which is where the policies already are.

This is why the gitlink question is not answered a third time here: section 1 reports that a path is a
`160000` entry whose `HEAD` mode is or is not `160000`, and section 2 decides what that means. The
index-independent equivalent of the existing `newMode == 160000 && oldMode != 160000` test is one
subprocess and needs no exit-code parsing — measured:

| `git ls-tree <tree> -- <path>` | meaning |
|---|---|
| `160000 commit <sha>\t<path>` | already an adopted gitlink |
| `100644 blob <sha>\t<path>` | a tracked file replaced by a gitlink — a typechange |
| *(empty output, exit 0)* | not in `HEAD` at all — a new gitlink |

### Decision 8 — The Linux/glibc claims are observed once in the container, not inferred (Product Owner decision, 2026-08-21)

Everything this change measured — symlinks, permission bits, index modes, and the `LC_ALL=C` pin on the
EACCES/ENOENT discriminator — was measured on git 2.55.0 / macOS APFS, by the worker, the reviewer and
the supervisor alike. Five parties, one host. Two of those claims are *specifically* about a platform
none of them can see: `strerror` translation is a **glibc** behaviour, so on macOS the bug the locale
pin fixes is unreproducible **and** the pin's efficacy is unobservable. Section 3's tests run on that
same host, so they are the wrong home for it — they would inherit the gap, not close it.

The branch is reachable in production: the container runs as **non-root** (`USER $APP_UID`,
`Dockerfile:56`), so an unreadable file is a real state, not one only root's exemption could hide.

The change therefore carries one **human-in-the-loop** task: build the image and exercise the
suppressed-entry paths (symlink, gitlink, EACCES, non-ASCII) once inside the Linux/glibc container. It
is verification, not new behaviour, and per the workflow's §4 it is **not ticked without the Product
Owner's confirmation** — the Architect hands over exact commands and what to expect, and waits.

### Decision 9 — The suite's own execution model is what pays, and it is what gets fixed (Product Owner decision, 2026-08-21)

Block 2.1–2.3 took `make test` from ~2m34s–3m10s to **18m13s** — reproduced alone, with no agents and
no other gate running, 905 passing in every run. Diagnosed by measurement rather than inspection:

- Call frequency is **unchanged** — one production call site (real app startup), not per-request or
  per-push. The block adds exactly **two** `git ls-files -v -s -z` spawns per repository start.
- The census is **correctly empty**: `entries.Count == 0` in all 641 instrumented calls, exactly as
  Decision 1 assumed.
- Per-spawn cost is **not constant**. ~16–30ms measured serially; under the suite's ~14-way xUnit
  parallelism, p50 **153ms**, p90 372ms, p99 739ms, max 1.7s — 52% of the instrumented subset's
  runtime.

**Decision 1 is not falsified.** One real app start pays two extra spawns and nobody notices. What was
not anticipated is that doubling subprocess count at a site *nearly every test boots through* is
amplified non-linearly by the suite's own concurrency.

**The fix goes in the suite, not the product.** The tempting patch — share one census between the two
sites — would undo precisely what Decision 5 requires: the assertion re-deriving state independently
rather than trusting reconciliation, which is the property that makes 2.3 able to refuse on its own.
Buying gate speed with the change's own correctness guarantee is the wrong trade.

This adds test-infrastructure work (task 3.8) that the original proposal did not scope. It is here
because the Product Owner scoped it, not because the change grew on its own.

## Risks / Trade-offs

- **One more subprocess per start.** `git ls-files -v` on every startup, whose output is normally empty.
  Accepted: startup already spawns several git processes, and this one is bounded by index size.
- **`git ls-files -v` output parsing.** The tag/path split is whitespace-delimited and paths may contain
  spaces; a naive split on all whitespace mangles them. The parse takes the tag as the first character
  and the path as everything after the first space, and `core.quotePath`'s effect on the path encoding
  must be checked — this codebase has already been bitten by `core.quotePath` once.
- **Refusing startup is a hard failure.** An operator who set `--skip-worktree` on a file they then
  edited will find the app refuses to boot where it previously ran. That is the intended trade (fail
  fast, never degrade), and it replaces a state where the app ran while every push was rejected — but it
  is a behaviour change an operator can meet, so the refusal message has to be worth reading.
- **Applies only after `git-backed-content-core` archives.** The code and the baseline requirement both
  live on that change's branch. Applying this earlier is not a scheduling choice; there is nothing to
  apply it to.
