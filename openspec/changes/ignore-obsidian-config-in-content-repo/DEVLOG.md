# DEVLOG — ignore-obsidian-config-in-content-repo

## 1. Seed the rule at bootstrap

**[architect]** Base: 96a7adf — bootstrap seeds a `.gitignore` for `.obsidian/` into the initial commit, and the post-init clean-tree invariant still holds.

**[architect]** Brief — block 1.1–1.2 → @worker

Scope: `EnsureInitialCommitAsync` in `src/ZeroWiki/Content/ContentRepositoryService.cs` (the branch that runs only when `repositoryHasNoCommitsYet`). Nothing else in this block.

Binding decisions:

- **D1 — seed at bootstrap, not at every start.** The rule is written and staged in the *same* initial commit that already carries `docs/.gitkeep`, so the repository is never observable without it. Do not add an ensure-on-start path, and do not touch the `!repositoryHasNoCommitsYet` early-return branch — an adopted repository is left exactly as it stands.
- **D2 — ignore-only, never untrack.** No `git rm --cached`, no deletion, no rewriting of history, anywhere.
- **D3 — the rule must match `.obsidian/` wherever it sits beneath the root**, because the vault is opened at the root *and* at `docs/`. Verify this against git rather than assuming it from pattern-syntax reasoning.
- Ignore nothing else. This is not a general ignore policy.

Tasks and their falsifiers:

- **1.1** Write the `.gitignore` at the repository root alongside the existing `.gitkeep` seeding, and stage it into the initial commit.
  *Falsifier:* after a bootstrap on an empty volume, `git show --name-only HEAD` must list `.gitignore`, and `git check-ignore -q` must succeed for **both** `.obsidian/workspace.json` and `docs/.obsidian/workspace.json`. If the rule is undone, or matches only one level, one of those observations fails. Run it and paste the output.
- **1.2** Confirm bootstrap still leaves the working tree clean, so the invariant asserted immediately after initialization holds.
  *Falsifier:* write the file but leave it unstaged and the existing post-init clean-tree assertion throws. Show that assertion passing on a real bootstrap, not by inspection.

Report back with the diff and the falsifier output; do not commit, do not tick `tasks.md`. Post your notes to this DEVLOG under `## 1.` — **above** `## NEXT`, not at end of file. `##` is reserved for section headings; start your posts at `###`.

**[worker]** Block 1.1–1.2 done.

### Task classification
- **1.1 — build.** Nothing seeded a `.gitignore` before this block; `EnsureInitialCommitAsync` only wrote `docs/.gitkeep`.
- **1.2 — audit.** The clean-tree invariant already exists (`AssertPorcelainIsEmptyAsync` in the existing `EmptyDataRoot_InitializesANonBareRepositoryWithOneSystemAuthoredCommit` test, run right after `EnsureRepositoryAsync`). This block's job was to prove it still holds with the new write staged, not to add a second mechanism.

### What changed
`EnsureInitialCommitAsync` (`src/ZeroWiki/Content/ContentRepositoryService.cs`) now writes `<repositoryRoot>/.gitignore` containing `.obsidian/\n` right after the `docs/.gitkeep` write, guarded by the same `repositoryHasNoCommitsYet` branch (D1 — the `!repositoryHasNoCommitsYet` early return is untouched), and stages it in the same `git add` call that stages `docs/.gitkeep`, so both land in the one initial commit. No other file touched. No `git rm --cached` anywhere (D2).

Pattern chosen: `.obsidian/` with no leading slash, trailing slash only — git's unanchored-pattern rule matches a directory of that name at any depth, verified against real git rather than assumed (D3, see falsifier below).

### Falsifiers — run for real, not read by inspection
I could not add a temporary test to a file this block doesn't own without leaving a diff, so I appended a scratch `[Fact]` to `ContentRepositoryServiceTests.cs`, ran it, then reverted the test file to a clean `git diff` before finishing (confirmed via `git status --short -- tests`).

**1.1**, rule present:
```
Passed ZeroWiki.Tests.Content.ContentRepositoryServiceTests.ScratchFalsifier_ObsidianGitignoreSeededAndTreeClean [311 ms]
```
Assertions inside: `git show --name-only --format= HEAD` contains `.gitignore`; `git check-ignore -q .obsidian/workspace.json` and `git check-ignore -q docs/.obsidian/workspace.json` both exit 0 after creating both paths.

**1.1**, rule removed (temporarily reverted the `.gitignore` write/stage, same test):
```
Failed ZeroWiki.Tests.Content.ContentRepositoryServiceTests.ScratchFalsifier_ObsidianGitignoreSeededAndTreeClean [484 ms]
Assert.Contains() Failure: Item not found in collection
Collection: ["docs/.gitkeep"]
Not found:  ".gitignore"
```
Confirms the falsifier actually falsifies, not just asserts text. Restored the real implementation immediately after (`git diff -- src` reviewed, matches only the intended change).

**1.2**, clean tree: the scratch test asserted `AssertPorcelainIsEmptyAsync` twice — immediately after `EnsureRepositoryAsync()` (before anything else touches the tree) and again after adding the untracked-but-ignored `.obsidian/` files — both passed in the 311ms run above. The pre-existing `EmptyDataRoot_InitializesANonBareRepositoryWithOneSystemAuthoredCommit` test also still asserts this and passed under the full suite.

### Claim
- **Claim:** the initial commit carries `.gitignore` alongside `docs/.gitkeep`, the rule ignores `.obsidian/` at both the repository root and under `docs/`, and the working tree is clean immediately after bootstrap and stays clean when `.obsidian/` content is added.
- **Instrument:** the scratch xUnit falsifier above, run against the real `git` binary via `GitProcessRunner` (pass-with-rule, fail-without-rule), plus the pre-existing clean-tree assertion under the full `make test` run, plus `git diff`/`git status --short` on `src/` and `tests/` before reporting.
- **Blind spot:** this only exercises `EnsureInitialCommitAsync`'s own bootstrap path in isolation. It does not exercise reconciliation's unscoped `git add -A` actually leaving a real editor-dropped file unstaged in a running instance (that's 2.1–2.2, explicitly deferred to the next block per the brief), nor an adopted pre-existing repository (2.4), nor concurrent/rolling-deploy bootstrap timing.

### Gates
```
BUILD_EXIT:0
TEST_EXIT:0  (897 passed, 0 failed, includes the pre-existing clean-tree assertion)
FORMAT_EXIT:0
```
`make validate` not run in this block (no change to `tasks.md`/specs); Architect's re-run covers it.

Diff is exactly the one hunk in `ContentRepositoryService.cs` (`git diff -- src`), `git status --short -- tests src` shows only that file modified — no stray scratch-test leftovers.

→ @reviewer

**[reviewer]** Block 1.1–1.2 review.

### Verdict: Request changes

### Correctness

- `EnsureInitialCommitAsync` only fires on the `repositoryHasNoCommitsYet` branch (D1 respected — the
  `!repositoryHasNoCommitsYet` early return at `ContentRepositoryService.cs:707-716` is untouched); no
  `git rm --cached`, no rewrite anywhere in the diff (D2 respected).
- Verified D3 independently against real git rather than trusting the worker's claim or the pattern's
  syntax: built a scratch repo with `.gitignore` containing exactly `.obsidian/\n`, and ran
  `git check-ignore -v` against both `.obsidian/workspace.json` and `docs/.obsidian/workspace.json`.
  Both matched (exit 0, rule `.gitignore:1:.obsidian/`) — the unanchored pattern does cover both vault
  layouts. D3 holds.
- The `git add "docs/.gitkeep" ".gitignore"` staging (`ContentRepositoryService.cs:734-737`) is two
  explicit literal paths, consistent with the existing scoped-`add` idiom elsewhere in this method —
  no `-A`, nothing else picked up. Fine.
- 1.2 (clean tree): staging is unconditional on the explicit path regardless of whether the write
  branch ran, so there's no way for `.gitignore` to exist on disk unstaged when this method returns —
  the post-init clean-tree assertion cannot be tripped by this change either way. 1.2 holds.

### Blocker

- **`ContentRepositoryService.cs:729-732`** — the `if (!File.Exists(gitIgnorePath))` guard silently
  skips seeding the rule when a `.gitignore` already sits on disk at the repository root *before the
  first commit*. That is not a hypothetical: it's the same "content copied onto the volume before
  first start" scenario this class already documents and tests elsewhere in this exact file —
  `GitignoredNestedGitRepositoryOnAFreshVolume_IsStillRefusedByTheScan`
  (`tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs:712-739`) writes a root-level
  `.gitignore` (`"copied-vault/\n"`) onto a fresh, commit-less volume before `EnsureRepositoryAsync`
  runs, specifically because operators drop pre-existing repo contents (including their own
  `.gitignore`) onto the volume ahead of the app's first boot. That test happens not to reach
  `EnsureInitialCommitAsync` (it throws earlier, on the nested-repo scan), but nothing stops the same
  shape of input reaching this method: a fresh volume, no commits yet, and a `.gitignore` already
  present that does not mention `.obsidian/`.

  In that case the guard skips the write entirely — the pre-existing content is staged and committed
  as-is, and the initial commit lands **without** the `.obsidian/` rule. This directly contradicts
  `specs/content-store/spec.md`'s new requirement: *"the system SHALL seed an ignore rule... as part
  of that repository's initial commit"* and Scenario 1's *"the repository's initial commit already
  carried the rule that excludes it"* — neither is conditioned on the volume arriving with no
  `.gitignore` of its own, and this is squarely the `repositoryHasNoCommitsYet` / "system initializes
  a repository" case (not an adopted repository — D1/D2's carve-outs don't apply here, there's no
  history yet). It also breaks D1's own stated invariant ("a repository is never observable in a state
  where the rule is missing") for this input.

  Note this gap isn't in the worker's own stated blind spot either — the DEVLOG's blind-spot line
  covers reconciliation timing, adopted repositories, and rolling-deploy races, but not a pre-existing
  root `.gitignore` on an otherwise-fresh volume, even though the test above shows that exact input is
  already a first-class case in this codebase.

  Ask: don't skip on `File.Exists` — ensure the `.obsidian/` line is present (append if the file exists
  and lacks it; write fresh if absent), so the initial commit carries the rule regardless of what else
  the volume arrived with.

### Nits

None beyond the blocker above — the diff is otherwise minimal, well-scoped, and the comment at
`ContentRepositoryService.cs:729-732` correctly documents D1/D2/D3 for the code path it actually
covers.

### OpenSpec scope

Diff touches only `EnsureInitialCommitAsync`; nothing in enumeration, routing, the save path, or the
Smart HTTP remote. In scope.

### Gates

Not re-run per the brief — worker reported `BUILD_EXIT:0`, `TEST_EXIT:0` (897 passed), `FORMAT_EXIT:0`;
Architect re-runs after this verdict.

### Mutation testing

Not run — this block has no test surface of its own yet (task 2 is the falsifier block); nothing here
meets the security/concurrency/data-integrity bar that would warrant it ahead of that.

Reviewed-state: 14ed426a33b8

→ @worker

**[architect]** Ruling on the reviewer's blocker — D5, and block 1 gains task 1.3 → @worker

Put to the Product Owner, because `design.md` had no answer for it: the case is a repository ZeroWiki
*initializes* (D1 applies, the rule must be in the initial commit) whose working tree already holds a
file a human wrote (the adopt-as-it-stands posture applies). The reviewer is right that skipping the
seed satisfies neither.

**Product Owner's decision:** ask git whether `.obsidian/` is already ignored, and append the rule only
if it is not. Written up as **D5** in `design.md`, with a matching requirement paragraph and a fourth
scenario in `specs/content-store/spec.md`. `tasks.md` gains **1.3** (this block) and **2.5** (§2's
falsifier for it). `openspec validate --strict` → `VALIDATE_EXIT:0`.

Remediation for this block — **1.3**:

- Replace the `if (!File.Exists(gitIgnorePath))` guard. The question is *"does git ignore
  `.obsidian/` here?"*, answered by `git check-ignore`, not *"does this file contain that string?"*.
  An operator's file may already exclude it by a broader glob, by `.obsidian` without the slash, or from
  a `.gitignore` in `docs/` — a text search misses all three and appends a duplicate.
- Append, never overwrite. The operator's rules survive into the initial commit alongside ours. Mind the
  trailing-newline case: appending to a file whose last line has no newline must not corrupt that line.
- Where no ignore file exists at all, behaviour is unchanged from 1.1.
- Still stage `.gitignore` into the same initial commit, and D2 still holds — nothing is untracked,
  removed, or rewritten.

*Falsifier for 1.3:* on a commit-less volume seeded with a `.gitignore` containing only an unrelated
rule, the initial commit must carry **both** that rule and ours, and `check-ignore -q` must succeed for
`.obsidian/workspace.json`. Then repeat with a file already ignoring the directory *by a different
pattern* — nothing may be appended. Run both and paste the output; the second is what distinguishes
`check-ignore` from a text search, and a text-search implementation passes the first.

Note `check-ignore` exits **1** for "not ignored" — a non-zero exit that is an answer, not a failure, so
it cannot go through `RunOrThrowAsync`. Distinguish it from a real error (exit ≥ 2) explicitly.

Do not commit and do not tick `tasks.md`. Post under `## 1.`, above `## NEXT`, headings at `###`.

**[worker]** Block 1.3 (D5 remediation) done.

### Task classification
- **1.3 — build.** Nothing in `EnsureInitialCommitAsync` asked git anything before writing/staging the
  `.gitignore`; the guard was a plain `File.Exists`. This is new logic, not a gap in existing coverage.

### What changed
`EnsureInitialCommitAsync` (`src/ZeroWiki/Content/ContentRepositoryService.cs`): the `if (!File.Exists
(gitIgnorePath))` guard now only governs the "no file at all" case (unchanged from 1.1). When the file
*does* already exist, the method now runs `git check-ignore -q .obsidian/` via `_git.RunAsync` (not
`RunOrThrowAsync` — exit 1 is "not ignored", an answer, matching the exit-code-as-answer idiom already
used at `ContentRepositoryService.cs:641` for `rev-parse --verify -q HEAD`). Exit 1 → append
`.obsidian/\n`, inserting a leading `\n` first only if the existing file's last byte isn't already a
newline (so an operator's file missing a trailing newline doesn't get its last line corrupted by our
rule folding onto it). Exit 0 → git already ignores the directory by whatever the operator wrote;
nothing is appended, file left byte-for-byte as found. Exit ≥2 → real git failure, rethrown as
`GitProcessException` rather than silently treated as either answer. The subsequent `git add
docs/.gitkeep .gitignore` and commit are unchanged, so D2 (append-only, no untrack/rewrite) and the
single-initial-commit requirement (D1) both still hold for this path.

### Falsifiers — run for real
Same posture as 1.1/1.2: appended a scratch `[Fact]` to `ContentRepositoryServiceTests.cs`, ran it, then
reverted the test file to a clean diff (confirmed via `git status --short -- tests`, shows nothing).

**Falsifier 1** — commit-less volume, `.gitignore` present with only `*.tmp\n`:
```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 747 ms
```
Assertions: post-bootstrap `.gitignore` content contains both `*.tmp` and `.obsidian/`;
`git check-ignore -q .obsidian/workspace.json` exits 0; `git show --name-only --format= HEAD` lists
`.gitignore`.

**Falsifier 2** — same, but the existing file already ignores the directory by a *different* pattern.
Used `.o?sidian/\n` deliberately (git's `?` wildcard) so the literal substring `.obsidian` never appears
in the file at all — this is what actually discriminates `check-ignore` from any text search, including
a naive substring search, not just an exact-line match:
```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 781 ms
```
Assertion: file content after bootstrap is byte-identical to the original `.o?sidian/\n` — nothing
appended.

**1.1/1.2 still hold** — full `make test` below includes the pre-existing
`EmptyDataRoot_InitializesANonBareRepositoryWithOneSystemAuthoredCommit` (asserts `.gitignore` in
`HEAD`, `check-ignore -q` at root and `docs/`, clean tree) and `SecondStart_...`/adoption tests
unmodified.

### Claim
- **Claim:** on a commit-less volume that already carries a `.gitignore`, the initial commit carries the
  operator's existing rules plus `.obsidian/` unless git already reports the directory ignored by
  something else already in that file, in which case the file is left untouched; the no-file case is
  unchanged from 1.1.
- **Instrument:** the two scratch falsifiers above, run against the real `git` binary via
  `GitProcessRunner` (not a fake), one exercising append-happens and one exercising append-is-skipped
  with a pattern chosen specifically so a text search could not accidentally get it right; plus the full
  `make test` run below re-confirming 1.1/1.2's existing assertions; plus `git status --short -- tests`
  and `git diff -- src` reviewed before reporting.
- **Blind spot:** only `EnsureInitialCommitAsync`'s own bootstrap branch is exercised in isolation, same
  as 1.1/1.2 — no coverage here of reconciliation, an adopted (already-committed) repository, or
  concurrent/rolling-deploy bootstrap timing (those remain 2.x's and out of this block's scope). Also
  untested here: a `.gitignore` that is itself malformed in a way `git check-ignore` treats as a fatal
  error (exit ≥2) rather than "not ignored" — the `else if (checkIgnoreProbe.ExitCode != 0)` branch
  throws `GitProcessException` but I did not construct a real `.gitignore` that provokes git into a
  fatal exit to confirm that branch fires; it is exercised the same way the sibling exit-code-as-answer
  idiom at `ContentRepositoryService.cs:641` is (by code-path parity), not by a dedicated falsifier.

### Gates
```
BUILD_EXIT:0
TEST_EXIT:0  (897 passed, 0 failed)
FORMAT_EXIT:0
```
`make validate` not run in this block (no change to `tasks.md`/specs from here); Architect's re-run
covers it.

`git status --short -- tests src` shows only `src/ZeroWiki/Content/ContentRepositoryService.cs`
modified — no stray scratch-test leftovers.

→ @reviewer

**[reviewer]** Block 1.3 re-audit (D5 remediation).

### Verdict: Request changes

### Blocker — the probe only asks about the root, D3 requires both levels

`ContentRepositoryService.cs:729` builds `checkIgnoreArguments = ["check-ignore", "-q", ".obsidian/"]`
— a single probe against the repository-root path. D3 (`design.md:46-49`) and the spec's new
requirement paragraph both bind the ignore rule to `.obsidian/` "wherever it sits beneath the root",
naming the root **and** `docs/` explicitly because both are real vault layouts. The probe checks only
one of the two paths the rule itself is required to cover, and that mismatch is exploitable, not
theoretical — verified against real git, not asserted:

```
$ printf '/.obsidian/\n' > .gitignore   # a root-anchored rule — the kind git itself generates,
                                          # and a plausible thing to inherit from another project
$ git check-ignore -q .obsidian/      ; echo "root exit=$?"
root exit=0
$ git check-ignore -q docs/.obsidian/ ; echo "docs exit=$?"
docs exit=1
```

Walk that through `EnsureInitialCommitAsync`: the operator's pre-existing `.gitignore` carries
`/.obsidian/` (root-anchored — ignores the root directory only, a normal shape for a `.gitignore`
copied in from elsewhere, including one git itself would suggest). The probe asks only about
`.obsidian/` at the root, gets exit 0 ("already ignored"), and the `else if` branch at
`ContentRepositoryService.cs:748` appends nothing. The initial commit lands with the operator's
anchored rule and nothing else. `docs/.obsidian/` is not ignored by anything. The very next
reconciliation pass (D9, the unscoped `add -A`) stages and commits it the first time an operator
opens the vault at `docs/` — exactly the D3 violation D4's falsifier exists to catch, just reached
through the D5 branch instead of the D1 branch. `.o?sidian/` (falsifier 2, the worker's own scratch
test) does not exercise this: it is unanchored and matches at every depth, so it happens to cover both
levels and cannot distinguish "checked both" from "checked one and got lucky." That's exactly the gap:
the worker's own stated blind spot for 1.3 lists reconciliation timing, adoption, and rolling-deploy
races, but not this — even though it's a same-shape miss to the one D4 was written to guard against,
now reachable from a different guard clause.

The fix has to ask git about both paths the rule is bound to cover, e.g. probe `.obsidian/` and
`docs/.obsidian/` separately and append the (still unanchored, still single) rule unless git already
reports *both* ignored — appending is cheap and idempotent-in-effect when one side is already covered
by something else, so there's no need to reason about *why* one side answered 0.

### Everything else checked clean

- **Exit-code handling** matches git's documented `check-ignore` contract exactly: 0 = ignored (skip),
  1 = not ignored (append) — not routed through `RunOrThrowAsync`, correctly, since 1 is an answer here
  — and anything else (git documents 128 for a fatal error) throws `GitProcessException`
  (`ContentRepositoryService.cs:746-749`) rather than being folded into either answer.
- **The probe necessarily runs before our rule exists** — that's correct by construction; the question
  being asked is "what does the volume already carry," and our rule is deliberately not written before
  the probe runs.
- **Trailing-newline / append logic** — verified all three edge cases by hand against the code at
  `ContentRepositoryService.cs:740-744`: empty existing file (`Length == 0` → `needsSeparatingNewline`
  false → rule appended directly, no leading blank line); whitespace/content with no trailing newline
  (`needsSeparatingNewline` true → a `\n` is inserted first, so the rule can't fold onto the operator's
  last line); content already ending in `\n` (appended directly, no double blank line). All three are
  correct.
- **D1/D2 and the adopted-repository branch** — `!repositoryHasNoCommitsYet` early return
  (`ContentRepositoryService.cs:698-706`) is untouched by this diff; still returns before any write.
  No `git rm --cached`, no rewrite, anywhere in the diff (D2 holds). The no-pre-existing-file branch is
  byte-identical to 1.1 (D1 holds for that case).
- **`git add "docs/.gitkeep" ".gitignore"`** — confirmed `GitProcessRunner.RunAsync`
  (`GitProcessRunner.cs:56-72`) builds the child process via `ProcessStartInfo.ArgumentList`, not
  string-interpolated shell text, so literal-path staging is safe regardless of what characters end up
  in either filename here (they don't, but the mechanism doesn't depend on that).

### OpenSpec scope

Diff stays inside `EnsureInitialCommitAsync`; the change to `design.md`/`spec.md`/`tasks.md` (D5, the
new requirement paragraph and scenario, 1.3/2.5) matches what's actually briefed and implemented. In
scope.

### Gates

Not re-run per the brief — worker reported `BUILD_EXIT:0`, `TEST_EXIT:0` (897 passed), `FORMAT_EXIT:0`;
`git diff -- tests` confirmed empty (no scratch-test residue). Architect re-runs after the next verdict.

### Mutation testing

Not run — same reasoning as the previous round: this block still has no test surface of its own (§2 is
the falsifier block), and the finding above was reached by direct verification against real git, not by
mutating a test.

```sh
{ git diff HEAD -- ':/' ':(top,exclude,glob)**/DEVLOG.md'
  git ls-files --others --exclude-standard -- ':/' ':(top,exclude,glob)**/DEVLOG.md' \
    | while read -r f; do printf '%s\n' "$f"; cat "$f"; done
} | shasum | cut -c1-12
```

Reviewed-state: 654078b641b3
HEAD: 96a7adf

→ @worker

**[architect]** Round 2 on the D5 branch — probe both levels → @worker

The reviewer's blocker is real and reproduced against git 2.55.0. Not a new decision: D5 already ruled
that git answers the question, and the spec already requires the rule to apply *wherever* the directory
appears. The probe just asks a narrower question than the requirement it implements.

Both rounds of this block have now failed the same way — a guard whose question was narrower than the
requirement (`File.Exists` vs "is it ignored"; root-only vs "wherever it appears"). So the fix is not
only the extra path, it is how the falsifier is chosen: a case that passes because two behaviours
coincide is not evidence. Falsifier 2 passed on an unanchored `.o?sidian/` that matches both levels,
which cannot tell "probed both" from "probed one and got lucky".

Fix: probe `.obsidian/` **and** `docs/.obsidian/`; append unless git reports **both** already ignored.
Appending where only one level is covered is harmless, so "unless both" is the safe side.

D5 now states this explicitly and **2.5** names the three rule shapes §2 must cover. Sharpened rather
than re-decided — `openspec validate --strict` → `VALIDATE_EXIT:0`.

*Falsifier for this round:* a pre-existing `.gitignore` containing exactly `/.obsidian/`. Both levels
must be ignored after bootstrap, and the initial commit must carry the operator's line **and** ours.
A root-only probe passes every falsifier run so far and fails this one. Re-run the earlier three
unchanged; none may regress.

Do not commit, do not tick. Post under `## 1.`, above `## NEXT`, headings at `###`.

**[worker]** Block 1.3 round 2 (probe both levels) done.

### Task classification
- **1.3 remediation — finish.** The gap is exactly what the reviewer named: the probe asked git about
  `.obsidian/` at the root only. Fix is to widen the question the code asks, not to add new mechanism.

### What changed
`EnsureInitialCommitAsync`'s existing-`.gitignore` branch
(`src/ZeroWiki/Content/ContentRepositoryService.cs`) now calls a new private helper,
`IsAlreadyIgnoredAsync(repositoryRoot, path, cancellationToken)`, twice — once for `.obsidian/`, once
for `docs/.obsidian/` — and appends the rule unless **both** report ignored. The helper is the same
`check-ignore -q` exit-code idiom as before (0 = ignored, 1 = not ignored → `RunAsync`, not
`RunOrThrowAsync`; anything else → `GitProcessException`), pulled out so both probes share it instead
of duplicating the switch. Append/no-file/trailing-newline logic is unchanged from the previous round.

### Falsifiers — run for real
Same posture as before: scratch `[Fact]`s appended to `ContentRepositoryServiceTests.cs`, run, then the
file restored to a clean `git diff` (confirmed via `git status --short -- tests`, empty).

**Discriminating case** — `.gitignore` pre-seeded with exactly `/.obsidian/` (root-anchored, matches
root only under real git):
```
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 1 s
```
covering, in the same run:
- `ScratchFalsifier_AnchoredRootOnlyRule_AppendsBecauseDocsLevelIsStillExposed` — after bootstrap,
  `.gitignore` contains both `/.obsidian/` (the operator's line, untouched) and `.obsidian/\n`
  (appended after it); `git show --name-only --format= HEAD` lists `.gitignore`; `check-ignore -q`
  exits 0 for both `.obsidian/workspace.json` and `docs/.obsidian/workspace.json`. A root-only probe
  would have answered "already ignored" from the root check alone and appended nothing, leaving
  `docs/.obsidian/workspace.json` unignored — this is what the reviewer reproduced against real git and
  what this case is built to catch.
- `ScratchFalsifier_NoPreExistingGitignore_SeedsRuleAtBothLevels` (falsifier 1, no file) — unchanged
  behaviour, both levels ignored.
- `ScratchFalsifier_UnrelatedPreExistingRule_AppendsOursAndKeepsTheirs` (falsifier 2, `*.tmp\n`) —
  both survive, both levels ignored.
- `ScratchFalsifier_AlreadyIgnoredByDifferentPattern_AppendsNothing` (falsifier 3, `.o?sidian/\n`,
  unanchored so it covers both levels on its own) — file left byte-identical, nothing appended.

None of the three earlier falsifiers regressed.

### Claim
- **Claim:** on a commit-less volume carrying a pre-existing `.gitignore`, the initial commit gets the
  `.obsidian/` rule appended unless git already reports **both** `.obsidian/` and `docs/.obsidian/`
  ignored by what the file already contains; an anchored root-only rule (the case that broke the
  previous round) now correctly triggers an append because `docs/.obsidian/` is still exposed.
- **Instrument:** the four scratch falsifiers above, run against the real `git` binary via
  `GitProcessRunner`, with the anchored-rule case chosen specifically because it is the one shape where
  root-only and both-levels-probed diverge (D3/D5) — the earlier `.o?sidian/` case could not distinguish
  those two implementations because it happens to cover both levels either way; plus the full `make
  test` run below re-confirming every other assertion in this file and the wider suite; plus `git diff
  -- src` and `git status --short -- tests src` reviewed before reporting.
- **Blind spot:** same as the previous round — only `EnsureInitialCommitAsync`'s own bootstrap branch is
  exercised in isolation; no coverage here of reconciliation, an adopted repository, or rolling-deploy
  timing (§2's scope). Also unexercised: a `.gitignore` containing a rule that is ignored-by-something
  at one level via a *third*, still-different pattern shape (e.g. a negation `!`) — the two-probe design
  only needs "ignored: yes/no" per path, and I did not construct a case where git's answer to that
  binary question is itself ambiguous or context-dependent (it isn't, per `check-ignore`'s documented
  contract, but that's read from the docs, not independently reproduced against git for a third shape).

### Gates
```
BUILD_EXIT:0
TEST_EXIT:0   (901 passed, 0 failed — full make test, unsandboxed, timeout 300000)
FORMAT_EXIT:0
```
`make validate` not run in this block (no change to `tasks.md`/specs from here); Architect's re-run
covers it.

`git status --short -- src tests` shows only `src/ZeroWiki/Content/ContentRepositoryService.cs`
modified — no stray scratch-test leftovers. `git diff -- src` is exactly the `IsAlreadyIgnoredAsync`
helper plus the two-probe call site.

→ @reviewer

**[reviewer]** Block 1.3 re-audit, round 3 (both-levels probe).

### Verdict: Request changes

### The both-levels probe itself is sound — the root-anchored gap is closed

Reproduced the architect's brief case independently against real git (not re-reading the worker's
claim): a commit-less volume seeded with `.gitignore` containing exactly `/.obsidian/` (root-anchored).
Before this round's fix, `check-ignore -q .obsidian/` → exit 0 and `check-ignore -q docs/.obsidian/` →
exit 1 — confirmed. The new `IsAlreadyIgnoredAsync` probes both paths independently
(`ContentRepositoryService.cs:755-756`) and appends unless both report ignored
(`ContentRepositoryService.cs:758`), which correctly resolves that case: the operator's anchored rule
survives, `.obsidian/` is appended, and both levels end up ignored. I also checked the other shapes named
in the brief:

- **A parent-directory or broad pattern covering both levels** (e.g. `.o?sidian/`, the worker's own
  falsifier 3) — both probes report ignored, append correctly skipped.
- **A pattern with an internal slash** (e.g. `.obsidian/plugins/`) that ignores only a *subpath*, not
  the directory itself — `check-ignore -q .obsidian/` correctly reports "not ignored" (git only matches
  the queried path, not a descendant), so the probe correctly triggers an append rather than being fooled
  into thinking the directory is covered.
- **An ignore file elsewhere than the root** — verified `docs/.gitignore` (untracked, just present on
  disk, as an operator-copied file would be) is picked up by `check-ignore` run from the repository root
  exactly as the code comment at `ContentRepositoryService.cs:738-739` claims:
  ```
  $ printf 'plugins/\n' > docs/.gitignore
  $ git check-ignore -q docs/.obsidian/ ; echo $?   # 1 — not ignored, correct
  $ printf '.obsidian/\n' > docs/.gitignore
  $ git check-ignore -q docs/.obsidian/ ; echo $?   # 0 — ignored, correct
  ```
  And when the *root* `.gitignore` is absent but `docs/.gitignore` exists, `EnsureInitialCommitAsync`
  takes the "write fresh root file" branch (`:743-746`) rather than the probe branch — it never touches
  `docs/.gitignore`, so any rules there (including negations, see below) are left alone by construction,
  not by luck. This path is correct.

### Blocker — appending after an operator's negation silently inverts it, within the same file

The brief specifically asked whether "append unless both ignored" can ever make things worse than
before. It can, and I reproduced it against real git 2.55.0 rather than reasoning about gitignore
precedence from memory:

```
$ printf '.obsidian/\n!docs/.obsidian/\n' > .gitignore   # operator: ignore .obsidian/ everywhere,
                                                            # except deliberately track it under docs/
$ git check-ignore -q .obsidian/       ; echo "root=$?"    # root=0 (ignored)
$ git check-ignore -q docs/.obsidian/  ; echo "docs=$?"    # docs=1 (NOT ignored — the negation works)
$ printf '.obsidian/\n' >> .gitignore                      # exactly what EnsureInitialCommitAsync does,
                                                            # since docsIgnored is false here
$ git check-ignore -q docs/.obsidian/  ; echo "docs=$?"    # docs=0 — now ignored. The negation is dead.
```

Walking this through the actual code: `rootIgnored` is `true`, `docsIgnored` is `false`
(`ContentRepositoryService.cs:755-756`), so `!rootIgnored || !docsIgnored` is `true`
(`:758`) and the unconditional, unanchored `.obsidian/\n` is appended at the end of the file
(`:762-767`). gitignore resolves conflicting patterns within one file by last-match-wins, so the
appended line — sitting after the operator's `!docs/.obsidian/` — silences it. Nothing is textually
removed (D2's letter holds — every existing byte is still in the file), but the operator's explicit,
working configuration is behaviourally reversed by a line ZeroWiki added underneath it, on the very
first boot, before any human has looked at the repository.

I confirmed this is a same-file-only hazard, not a general one — a negation living in a *different,
nested* file (e.g. `docs/.gitignore`) is safe, because git gives the closer/deeper `.gitignore` file's
patterns priority over a farther one regardless of append order in the other file:

```
$ printf '*.tmp\n' > .gitignore
$ printf '.obsidian/\n!.obsidian/\n' > docs/.gitignore
$ printf '.obsidian/\n' >> .gitignore     # appended to the ROOT file, same action as above
$ git check-ignore -q docs/.obsidian/ ; echo $?   # still 1 — docs/.gitignore's negation still wins
```
So the exposure is specifically: an operator's own root `.gitignore` containing both a broad ignore and
a `!`-negation for one of the two probed paths, in that one file.

This is not academic for this codebase specifically: the whole reason D3/D5 exist is that this vault can
be opened at *either* level, and an operator sophisticated enough to run two vaults against one repo is
exactly the kind who might deliberately track one level's plugin config while ignoring the other's —
that's a plausible, not contrived, use of `!`. And the consequence isn't cosmetic: once
`docs/.obsidian/` is ignored, the very next reconciliation pass's unscoped `git add -A` (D9) will skip
whatever the operator had there, so content they configured git to keep tracking never gets into a
commit at all, silently, until someone notices and hand-edits `.gitignore` again.

This also cuts against the spec text this round itself introduced —
`specs/content-store/spec.md:21-24`: *"It SHALL NOT overwrite or remove any rule already present."* The
rule is still present as text; its effect is not. I think that gap is exactly what the sentence was
trying to rule out, even though it wasn't written with this shape in mind.

I don't think this is a code-level miss the same way the last two rounds were (a guard asking a
narrower question). The probe answers "is it ignored right now" correctly for both scenarios. The
problem is downstream: the *action* taken on a "not ignored" answer doesn't distinguish "never
mentioned" from "explicitly and deliberately un-ignored," and I don't think it safely can —
`check-ignore` without `-v` gives no signal on *why* a path isn't ignored (and `-v` only reports
matching patterns for the *ignored* case), so reaching for that signal would mean parsing gitignore
syntax for `!` lines, which is exactly the text-search approach D5 rejected in favour of asking git. So I
don't think this converges by asking the worker for another patch; it's a decision: does "must be
ignored wherever it appears" (the spec's own new requirement) override an operator's explicit `!`
negation, or does the adopt-as-it-stands posture win when the operator has been that explicit? The
worker's own stated blind spot for this round named negation as an untested *shape* but framed the open
question as "is check-ignore's binary answer itself ambiguous" (it isn't) rather than "can appending
invert what the answer means" (it can) — worth noting only because the two are easy to conflate, not as
a knock on the report; the blind-spot discipline is what let me go straight to reproducing the right
thing.

**Recommend:** ❓ @architect — this reads like a genuine gap in D5's "harmless" premise rather than a
bug, and I don't see a clean fix that stays inside "ask git, never parse text." Options as I see them:
accept and document as a known risk (parallel to the existing "an operator wants `.obsidian/` tracked"
bullet in `design.md`'s Risks section, since the operator can always re-edit `.gitignore` after boot and
before ever running `obsidian-git` against `docs/` for real), or have the worker special-case detection
of `!`-prefixed lines for the specific paths being probed (which does reintroduce a text-pattern check,
but scoped narrowly enough — checking only for a `!` line whose pattern equals the probed path, not
general glob equivalence — that I don't think it fully re-opens what D5 was avoiding). Judgment call,
not mine to make.

### Everything else re-checked, no regressions from the round-2 verdict

- **Exit-code discipline** unchanged and correct in the shared helper: 0 = ignored, 1 = not ignored via
  `RunAsync` (not `RunOrThrowAsync`), ≥2 → `GitProcessException` (`:797-802`).
- **Append-never-overwrite / D2** — still holds at the mechanical level (no `git rm --cached`, no
  deletion, no history rewrite anywhere in the diff); the finding above is about *effect*, not about
  the code performing an overwrite.
- **Trailing-newline logic** (`:761-762`) — re-checked all three cases by hand (empty file, content
  without trailing `\n`, content with trailing `\n`); unchanged from the prior round and correct.
- **D1** — the no-pre-existing-file branch (`:743-745`) is byte-identical to the original 1.1
  implementation.
- **D2 / adopted-repository early return** (`:706-717`, the `!repositoryHasNoCommitsYet` branch) —
  untouched by this diff.
- **Staging by literal path** — `git add "docs/.gitkeep" ".gitignore"` (`:773`) still two explicit
  literal paths via `RunOrThrowAsync`'s `ArgumentList`-based process invocation, no `-A`, no
  shell-interpolated arguments.
- `git status --short -- tests` is clean and `git diff -- tests` is empty — confirms the worker's
  scratch-test cleanup claim rather than trusting it.

### OpenSpec scope / idiom

Diff stays inside `EnsureInitialCommitAsync` and the new private `IsAlreadyIgnoredAsync` helper, which
sits directly below its only call site — right level of the file, no premature extraction elsewhere.
`design.md`/`spec.md`/`tasks.md` changes match what's actually implemented (D5's sharpened text, 2.5's
three rule shapes). Naming, `Async` suffix, `CancellationToken` threading, nullable handling all
consistent with the rest of the file; no `!`-suppression, no sync-over-async. In scope.

### Gates

Not re-run per the brief — the worker's `TEST_EXIT:0` (901 passed) predates its scratch-test revert and
is explicitly not being treated as evidence for the current tree, per the brief. Architect re-runs after
this verdict, as usual.

### Mutation testing

Not run. This block still has no test surface of its own (§2 is the falsifier block), and the finding
above was reached by direct reproduction against real git, not by mutating a test — same posture as the
prior two rounds.

Reviewed-state: a924135d5da9
HEAD: 96a7adf

→ @architect

**[architect]** ❓ answered — the negation override is accepted, not fixed → @reviewer

Put to the Product Owner as a design call, per your `❓ @architect`. **Decision: accept it as a
documented risk.** No code change.

Reasoning on the record: `check-ignore` gives a binary answer and cannot tell "never mentioned" from
"explicitly re-included". Separating them means parsing `!` lines — the text inspection D5 was written
to avoid — and a partial `!` parser would be a worse instrument than the one it replaced, wrong in
cases nobody enumerated. The operator's remedy is ordinary: it is their own `.gitignore`, nothing
rewrites it after bootstrap (D1), and moving the negation below our line restores it.

Written up in three places so the acceptance is visible rather than folklore:

- `design.md` — a Risks entry carrying your reproduction verbatim, the same-file-only scoping, and why
  it is accepted rather than fixed.
- `specs/content-store/spec.md` — the requirement now says outright that asking git cannot distinguish
  the two cases, that the appended rule prevails, and that the system **SHALL NOT** parse the file's
  text to detect it. That resolves the contradiction you flagged against "SHALL NOT overwrite or remove
  any rule already present": the spec no longer claims more than the code does.
- `tasks.md` — new **2.6**, a characterisation test in §2, so a future change to this behaviour is a
  visible test failure rather than a silent regression.

`openspec validate --strict` → `VALIDATE_EXIT:0`.

Round 3 found a real defect class the first two rounds did not, and the same-file/closer-file scoping is
what made "accept" defensible rather than a shrug — noted.

Re-audit the documentation only: does the spec now describe what the code actually does, and is 2.6
specified tightly enough to be written? The `src/` diff is unchanged from the state you fingerprinted
`a924135d5da9`.

**[architect]** Block 1.1–1.3 closed — gates green, committed

`make gates` → `BUILD_EXIT:0`, `TEST_EXIT:0` (897 passed), `FORMAT_EXIT:0`, `VALIDATE_EXIT:0`,
`GATES_EXIT:0`. Suite back to 897, confirming the scratch tests left with the worker rather than riding
along. Fingerprint recomputed as `4e19c2e39a4f` — matches the reviewer's verdict exactly, so what is
committed is what was certified. 1.1–1.3 ticked.

## NEXT

**Resume point:** §1 committed and closed by the reviewer; **the §1 supervisor review has not run.**
That is the next action — `git diff 96a7adf..HEAD`, base posted at the top of `## 1.`

- Branch `change/ignore-obsidian-config-in-content-repo`, base `96a7adf` on `main`.
- Blocks: §1 = 1.1–1.3 *(done)*, §2 = 2.1–2.6, §3 = 3.1.
- §2 grew during §1 and is the real weight of this change: **2.5** (three discriminating rule shapes) and
  **2.6** (characterisation test for the accepted negation override) both came out of §1's review rounds.
- **Owed to §3:** the README paragraph must also cover the pre-existing-`.gitignore` case (D5) — the
  original 3.1 wording predates that decision and mentions only new vs. existing repositories.
- Live decisions: D2 ignore-only, never untrack. D5 ask git, never parse text — including the accepted
  consequence that our appended rule overrides an operator's same-file `!` negation.
- §1 took three review rounds; the first two were the same defect class (a guard asking a narrower
  question than the requirement it implements). Worth watching for in §2's assertions.
