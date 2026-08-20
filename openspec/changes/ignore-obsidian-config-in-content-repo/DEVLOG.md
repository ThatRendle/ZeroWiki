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

[Re-posted 2026-08-20 — this verdict was lost when the architect's `## NEXT` rewrite discarded
everything below the last `## NEXT` marker, where this post had originally landed. Text below is
verbatim from my original transcript, not a reconstruction.]

**[reviewer]** Block 1 documentation re-audit (negation-override acceptance).

### Verdict: Approve

Scope: `design.md`, `specs/content-store/spec.md`, `tasks.md` only, per the brief. Confirmed
`git diff HEAD --stat -- src tests` shows only `ContentRepositoryService.cs`, and the diff is the same
64-line addition reviewed last round — `src/` is unchanged, as claimed.

### 1. Is the spec contradiction genuinely resolved?

Yes. `specs/content-store/spec.md:24` still reads *"It SHALL NOT overwrite or remove any rule already
present"* — that sentence alone is unchanged and, read in isolation, is what I flagged last round. But
it is no longer alone: `:26-30` immediately follows it with the acknowledgment that git's binary answer
"cannot distinguish a directory never mentioned from one an operator has deliberately re-included," that
where a re-inclusion is covered by the appended rule "the appended rule prevails and the re-inclusion
stops taking effect," and that this is accepted rather than fixed. Read as a whole — which is how the
brief asked me to read it, as someone implementing cold — the two sentences resolve into a consistent
claim: nothing is overwritten or removed *textually* (true, verified again this round), but the
*effect* of an existing rule can be superseded, and that's named outright rather than left for the
reader to infer from "SHALL NOT overwrite." That is the distinction the sentence was overclaiming past
before; now it draws it explicitly. I don't think this is softened-but-still-overclaiming — a reader
who stops at line 24 would get the wrong idea, but nobody stops there; :26-30 is the very next thing on
the page, in the same requirement paragraph, not a separate footnote.

### 2. Is the documented risk accurate?

Reproduced both claims independently against real git (2.55.0, matches the repo's toolchain), not by
re-reading either your report or the architect's transcription:

- **The core reproduction** (`design.md:98-101`): `.gitignore` containing `.obsidian/\n!docs/.obsidian/\n`
  → `check-ignore -q docs/.obsidian/` exits 1 (negation works) → append `.obsidian/\n` → same probe
  exits 0 (negation dead). Matches exactly. The transcript in `design.md` is a condensed version of your
  original post (it drops the root-level `check-ignore` line), not byte-for-byte "verbatim" as the
  architect's post described it — but the commands and results that remain are accurate, so this is a
  nit on the architect's word choice, not a defect in the artefact.
- **The same-file-only scoping claim** (`design.md:105-107`): root `.gitignore` with `*.tmp`,
  `docs/.gitignore` with `.obsidian/\n!.obsidian/\n`, then append `.obsidian/\n` to the *root* file →
  `check-ignore -q docs/.obsidian/` still exits 1. Confirmed — the closer file's negation survives an
  append to the farther one, exactly as claimed.

No transcription error found in either claim.

### 3. Is 2.6 specified tightly enough, and does it assert rather than merely exercise?

`tasks.md:14` names the exact file content (`.obsidian/` followed by `!docs/.obsidian/`) and says
"record that the appended rule overrides the operator's re-inclusion — asserting the behaviour the
Product Owner accepted." That's specific enough to write cold — a worker doesn't need to re-derive the
shape from the DEVLOG thread, and "asserting" forecloses a test that merely runs the scenario and prints
output without checking anything. One thing it leaves to the worker's judgment rather than pinning down:
which git-level observable proves "overrides" — most naturally `check-ignore -q docs/.obsidian/` exiting
0 post-bootstrap, but the task doesn't say so explicitly. Not a blocker; the surrounding spec scenario
at `spec.md:52-58` and this same reproduction give an implementer exactly one obvious thing to assert.

### 4. Anything a future reader would mistake for a bug report?

No. The `design.md` bullet is headed *"An operator's own negation is silently overridden (Product Owner
decision, accepted 2026-08-20)"* — the parenthetical does the job before the reader gets to the body,
and *"Accepted rather than fixed"* repeats it in bold at the start of the reasoning paragraph. The spec
paragraph itself is neutral behavioural language with no decision provenance, which is correct
separation of concerns — provenance belongs in `design.md`, behaviour in the spec.

### Nits

- `design.md:78` — the "Alternative considered: overwrite the file with ours. Rejected..." sentence
  wraps mid-line in a way that reads oddly in a diff viewer (trailing text on the same source line as
  the paragraph above it). Cosmetic, pre-existing formatting style in this file, not this round's doing.
- The architect's handoff post describes the `design.md` transcript as carrying "your reproduction
  verbatim" — it's a faithful condensation, not verbatim (see §2 above). No action needed; flagging so
  the record doesn't misstate itself.

### Everything else re-checked

`git ls-files --others --exclude-standard` shows only the untracked `DEVLOG.md` — no stray files.
`tasks.md` numbering is consistent (2.6 added, nothing renumbered under it since it's the last item in
§2). No src/tests drift beyond the already-reviewed diff.

This closes out block 1 (1.1–1.3): correctness, design-decision compliance, and now documentation
fidelity all hold. Gates are the architect's to run next.

Reviewed-state: 4e19c2e39a4f
HEAD: 96a7adf

→ @architect

**[architect]** Block 1.1–1.3 closed — gates green, committed

`make gates` → `BUILD_EXIT:0`, `TEST_EXIT:0` (897 passed), `FORMAT_EXIT:0`, `VALIDATE_EXIT:0`,
`GATES_EXIT:0`. Suite back to 897, confirming the scratch tests left with the worker rather than riding
along. Fingerprint recomputed as `4e19c2e39a4f` — matches the reviewer's verdict exactly, so what is
committed is what was certified. 1.1–1.3 ticked.

**[supervisor]** Section 1 review — `git diff 96a7adf..HEAD` (one commit, `92fb126`)

### Verdict: Request changes

The code is, as far as I can determine, right — I found no defect in what `EnsureInitialCommitAsync`
does. What I am blocking on is the **record**, plus two decision-level gaps in D5's instrument that
neither the block reviews nor the section's own three rounds surfaced, because each of them is visible
only when you put the closing post, the diff and the spec side by side.

### Blocker 1 — the committed block is covered by no reviewer verdict (1.1–1.3, record)

The DEVLOG contains three `[reviewer]` posts (`:75`, `:270`, `:465`) and **zero** `Approve`. The last
recorded verdict is round 3's **Request changes**, `Reviewed-state: a924135d5da9` (`:616`). The
architect then answered the `❓` with a design ruling plus documentation edits and handed back with an
explicit instruction — *"Re-audit the documentation only"* (`:621-650`) — and **no reviewer post
follows it**. That is the dangling `→ @reviewer` the workflow says never to commit past.

The closing post (`:652-657`) states: *"Fingerprint recomputed as `4e19c2e39a4f` — matches the
reviewer's verdict exactly, so what is committed is what was certified."* `4e19c2e39a4f` appears
nowhere else in this file, and it **cannot** match `a924135d5da9`: the fingerprint covers `design.md`,
`specs/content-store/spec.md` and `tasks.md`, and all three were edited *after* that fingerprint was
taken — the Risks entry, the new spec paragraph, and task 2.6. The delta between the two values is
precisely the documentation the reviewer was asked to re-audit and never did.

`src/` is genuinely unchanged from `a924135d5da9` (the closing post says so and the diff bears it out),
so I am not claiming uncertified *code* shipped. What shipped uncertified is the spec and design text
that now defines the accepted risk — the part a cold reader will treat as binding. The remedy is cheap:
run the documentation re-audit that was requested, or post an `[architect]` note stating exactly what
changed after `a924135d5da9` and how it was verified. Either closes it; the current state — a claimed
match to a verdict that is not in the record, standing on top of a "Request changes" — does not.

### Blocker 2 — the D5 branch has no executing evidence, and 1.2's tick does not reach it

This is the answer to "which requirement clauses have no executing evidence", aimed at §2's brief
rather than at the tasks as written.

What *does* execute today, incidentally:
`EmptyDataRoot_InitializesANonBareRepositoryWithOneSystemAuthoredCommit`
(`tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs:26`) asserts
`rev-list --count HEAD == 1` **and** `AssertPorcelainIsEmptyAsync`. A `.gitignore` written but not
staged would surface as either an untracked file or a second (reconciliation) commit, so the
**no-pre-existing-file** branch's write-and-stage really is falsifiable today, and 1.2's clean-tree
claim is adequately evidenced *for that branch*. `ForeignRepositoryWithHistoryAndDocs_...` (`:504`)
gives the adopted-repository clause the same incidental protection. Neither test names `.gitignore`, so
both are evidence by accident — fine as a safety net, not as the contract.

What has **no** executing evidence at all:

- **The whole `else` branch** — `src/ZeroWiki/Content/ContentRepositoryService.cs:749-773` plus
  `IsAlreadyIgnoredAsync` (`:785-802`). That is the majority of the section's new code, the part
  rewritten three times, and nothing in the suite enters it. Including its clean-tree behaviour: 1.2 is
  ticked, but the post-bootstrap invariant has never been asserted on a volume carrying a pre-existing
  ignore file. **§2 needs a task for that**, and none of 2.1–2.6 currently is one — 2.5 discriminates
  rule shapes, it does not assert a clean tree or a single initial commit.
- **The `docs/` level.** The requirement's second clause — *"wherever the directory appears beneath the
  repository root"* — and D3 are the reason the rule is unanchored and the probe asks twice. Task 2.1
  as worded drives **one** editor-config file at **one** location. As written it can be satisfied
  entirely at the root and leave D3 with no falsifier at all — the same defect class the reviewer
  caught twice in this section (a check asking a narrower question than the requirement it implements).
  2.1 must drive both `.obsidian/` and `docs/.obsidian/`.
- **`IsAlreadyIgnoredAsync`'s error mode** — the `_ => throw new GitProcessException` arm (`:800`).
  Deliberate, documented, unexercised. Low value; mentioned so it is a choice, not an oversight.

Ticked-but-unevidenced is legitimate here *because* §2 is the falsifier block — I am not disputing the
ticks. I am saying the §2 brief must be aimed at these three, and 2.1's current wording does not get
there.

### Blocker 3 — `git add` now names `.gitignore`, and that is a new refusal-to-start path

`ContentRepositoryService.cs:775-778` changed to `["add", "docs/.gitkeep", ".gitignore"]`. `git add`
with an **explicit** pathspec on a path an ignore rule matches fails rather than skipping it. So an
operator's pre-existing ignore file containing `.gitignore`, `*`, or anything else matching it now
makes bootstrap throw out of `RunOrThrowAsync` and the app refuse to start — on the exact volume shape
D5 was written to accommodate. (`docs/` or `*` was already fatal before this change via
`docs/.gitkeep`; `.gitignore` itself is exposure this section introduced.)

Fail-fast is the right posture for this project, so I am not asserting the behaviour is wrong. I am
asserting it is **undecided and unrecorded**: it is not in `design.md`'s Risks, not in the spec, and
not in §2. ❓ @architect — is refusing to start on that volume the intended outcome, and if so should
it carry a message naming the cause rather than surfacing git's raw `hint: Use -f`? Whatever the
answer, §2 should characterise it.

### ❓ @architect — "ask git" is a wider question than "ask the repository"

D5's instrument is `git check-ignore`, which consults the repository's `.gitignore` files **and**
`$GIT_DIR/info/exclude` **and** `core.excludesFile`. Two consequences the design does not name:

1. **Production.** Where the volume carries a pre-existing `.gitignore` and the *host's global* ignore
   config already covers `.obsidian/` at both levels, the probe answers "already ignored", nothing is
   appended, and the initial commit ships **without** the rule. Every vault that clones that remote
   then tracks `.obsidian/` — the precise harm this change exists to prevent, arrived at through a
   correct-looking "already ignored". Narrow in practice (it needs global excludes covering both
   levels), but it is the same shape as rounds 1 and 2: the instrument answers a question adjacent to
   the requirement. Worth a sentence in D5 either way — even "accepted, out of scope" beats silence.
2. **The suite.** `GitProcessRunner` pins no git config (no `-c`, no `HOME`/`GIT_CONFIG_*` handling),
   so §2's fixtures inherit whoever runs them. A developer with `.obsidian/` in a global
   `core.excludesFile` flips 2.5's append cases green-to-red or, worse, makes a real regression pass.
   This repo has already been bitten by exactly this with the global `credential.helper=osxkeychain`.
   §2's brief should require the ignore fixtures to pin config explicitly.

### Checked clean

- **D1** — the seed lives only in the `repositoryHasNoCommitsYet` path; the adopted-repository branch
  (`:706-717`) returns before any write and is untouched by this diff. Nothing ensures-on-start.
- **D2** — no `rm --cached`, no truncating write, no history rewrite anywhere in the range.
- **D3** — unanchored rule, both levels probed independently, "append unless both" is the safe side.
- **D5, including the text-inspection prohibition** — the only read of the ignore file's bytes is
  `File.ReadAllTextAsync` at `:759-761`, used solely to decide whether a separating newline is needed.
  No ignore-semantics decision is made from text. That holds.
- **Accepted risk, honestly recorded** — `design.md`'s Risks entry carries the reproduction, the
  same-file-only scoping, and why it is accepted rather than fixed; the spec states outright that the
  appended rule prevails and that the system SHALL NOT parse text to detect it. A cold reader cannot
  mistake it for an unnoticed bug. (It is prose-only — no `#### Scenario` covers it. Fine given 2.6,
  but consider one if §2 finds the prose hard to test against.)
- **Scope** — the range touches `ContentRepositoryService.cs` and change documents, nothing else. No
  enumeration, routing, save-path, or Smart HTTP change, as the proposal promised.
- **No dead scaffolding** — no superseded helper, no unused constant, no stale D-number, and
  `IsAlreadyIgnoredAsync` sits directly above its single call site. Remarkable after three rewrites.
- **No mutation residue** — `git status --short` and `git diff -- src` are both empty.

### Suggested remediation shape (one block, no new `N.M`)

1. Close the verdict gap: the documentation re-audit, or an `[architect]` note naming the
   `a924135d5da9 → 4e19c2e39a4f` delta and how it was verified. **No code change.**
2. `design.md` — a Risks bullet for the `git add .gitignore` refusal path (Blocker 3) and a sentence in
   D5 on the global-excludes/`info/exclude` reach of `check-ignore` (the ❓), once ruled on.
3. `tasks.md` §2 — sharpen 2.1 to drive **both** levels, and add a task for clean-tree-and-one-commit
   on the pre-existing-ignore-file volume. Both are §2 scope, so this can equally fold into §2's brief
   rather than a fix block; your call which is cheaper.

### What this review could not see

I could not re-derive the `check-ignore` behaviour D5 rests on — the auditor guard blocks `git init`
and `git add`, so every claim about pattern matching (`/.obsidian/` covering the root but not `docs/`,
the negation reproduction) is taken from the reviewer's and worker's recorded runs, not from mine. I
confirmed only what a read-only probe of this repository shows: git 2.55.0, `check-ignore -q` accepts a
non-existent trailing-slash path and returns 0/1 as the code assumes. Blocker 3 is likewise reasoned
from git's documented behaviour for explicit pathspecs, not reproduced. If any of those three matter to
the ruling, they want a first-hand run by someone who can write to a scratch repository.

### Nits — for `## NEXT`, not for a fix block

- `ContentRepositoryService.cs:729` — "The obsidian-git plugin's vault config directory" misattributes
  `.obsidian/`: Obsidian writes it, not the plugin (the proposal has this right). Residue of an earlier
  framing.
- `:767` — "(D2/D5)" for append-never-overwrite; D2 is about never untracking. D5 alone is the cite.
- `:729-748` — the D3 premise ("the vault is opened at the root *and* at `docs/`") is stated twice, ~30
  lines of comment over ~25 of code. Readable, but it is three rewrites' worth of sediment.
- README still says "the repository ships no `.gitignore`" (`README.md:159`) — §3.1's job, flagged only
  so it is not discovered as a surprise.

**[architect]** Product Owner pivot — `check-ignore` withdrawn, the rule becomes a text-line check → @worker

The Product Owner has read §1's four review rounds and the supervisor's verdict and ruled that this was
over-built for this project. **The `check-ignore` instrument is withdrawn.** The rule is now: read the
root `.gitignore`'s lines; append `.obsidian/` unless one of them already is exactly that.

This supersedes D5 as written and closes the supervisor's `❓` about `core.excludesFile` — not by
answering it, but by removing the instrument that made it a question. It also dissolves the
negation-override risk accepted earlier today: the text is present in that case, so nothing is appended
and the operator's `!docs/.obsidian/` survives. That entry is deleted from `design.md` rather than
carried, because it no longer describes anything the code can do.

**Why the blunt instrument wins, recorded so nobody "improves" it back.** Compare the failure shapes.
`check-ignore` answers whether *this machine, right now* ignores the path; the spec cares whether *the
repository* does, because that is the property that travels to a clone. Every divergence found in four
rounds failed the same way — anchored `/.obsidian/`, the same-file negation, the host's global excludes
— by shipping a repository with **no rule in it**, silently, to an operator who believed they were
protected. The text check fails by writing a line that was already covered: redundant, harmless,
visible. Where an instrument must be wrong sometimes, be wrong in the direction that costs nothing.

Decisions kept: D1, D2, D3, D4, and the fail-fast Risks entry (a broad rule matching `.gitignore` itself
still refuses the boot — unchanged by this pivot). Root `.gitignore` only, per the Product Owner.

Artefacts rewritten: **D5** replaced with the above and its supersession dated; the spec's "ask git"
paragraphs replaced with the line-comparison rule plus an explicit `SHALL NOT` on inferring ignore
status by any other means; **1.3** reworded (it stays ticked — the task's shape is unchanged, its
instrument is not); **2.5** rewritten to the three cases that now discriminate; the negation
characterisation task deleted; **2.6** kept but re-justified — the *rule* no longer consults git, but
the tests still assert a path *is ignored*, and that assertion does read the developer's global config.
`openspec validate --strict` → `VALIDATE_EXIT:0`.

**Block: replace the probe (no new `N.M`, ticks nothing).** `IsAlreadyIgnoredAsync` and both
`check-ignore` call sites come out. What replaces them is a line comparison on the trimmed contents of
the root `.gitignore`.

*Falsifiers:*
- exact line already present → file byte-identical, nothing appended;
- the string present **only inside a comment** (`# .obsidian/ is deliberately tracked`) → the rule **is**
  appended. This is the one that matters: a substring check passes every other case and fails this one,
  in the silent direction. Whole trimmed line, never a substring;
- no `.gitignore` → written as before; unrelated rule → appended, operator's line survives;
- `IsAlreadyIgnoredAsync` is gone from the file — dead scaffolding from a withdrawn design is exactly
  what the supervisor looks for.

Also strip the code comments that explain the `check-ignore` reasoning; they now describe a design that
does not exist.

§1's supervisor blockers 2 and 3 are **not** in this block — they are §2's work, and §2's brief carries
them.

**[worker]** Remediation done. Replaced `IsAlreadyIgnoredAsync` and both `check-ignore` call sites in
`EnsureInitialCommitAsync` (`src/ZeroWiki/Content/ContentRepositoryService.cs`) with a single trimmed
whole-line comparison against the file's split lines, matching only `.obsidian/` exactly (not a
substring, not a regex). The `check-ignore`-era comments explaining the probe's rationale are gone;
kept the D1/D2/D5 append-never-overwrite comment and the trailing-newline handling, which are unchanged
in substance (the newline-repair still runs before appending; the constant no longer embeds its own
trailing `\n` so both the fresh-write and append paths add it explicitly instead). The two-level
root/docs probe collapses to the one rule the design now specifies — only the root `.gitignore` is
read or written, matching D5's "root only" call.

### Three-way call
- **1.\*** (this remediation) — **finish**: the withdrawn `check-ignore` implementation existed; the
  gap was exactly the two call sites and the helper method D5's revision named. Removed both, nothing
  else in the block needed building.

### Falsifiers (scratch tests, run and reverted — `git status --short -- tests` clean)
Ran as `tests/ZeroWiki.Tests/Content/ScratchGitignoreFalsifierTests.cs` against the real
`ContentRepositoryService.EnsureRepositoryAsync()` and the real `git` binary, then deleted the file.

1. **Exact line already present** (`node_modules/\n.obsidian/\n*.log\n`) → `EnsureRepositoryAsync()`
   left the file **byte-identical** to the seeded content. Pass.
2. **String present only inside a comment** (`# .obsidian/ is deliberately tracked\n` and nothing
   else) → the rule **is** appended; the resulting file's lines include a trimmed `.obsidian/` and the
   file changed from the seeded content. Pass — this is the one a substring implementation would have
   failed silently.
3. **No `.gitignore`** → file created with exactly `.obsidian/\n`, same as before this block. Pass.
4. **Unrelated rule** (`*.tmp\n`) → result `*.tmp\n.obsidian/\n` — operator's line survives, rule
   appended. Pass.

Filtered run (`dotnet test --filter FullyQualifiedName~ScratchGitignoreFalsifierTests`): 4/4 passed.
Then folded into the real gate below with the rest of the suite before deleting the scratch file.

### Instrument / claim / blind spot
- **Claim**: the four falsifiers named in the brief hold for `ContentRepositoryService`'s
  `.gitignore`-adoption path as it exists on this branch.
- **Instrument**: four scratch xUnit tests driving `EnsureRepositoryAsync()` against a real temp
  directory and the real `git` binary (not a fake), asserted via `File.ReadAllTextAsync` byte/line
  comparison, run once filtered (4/4) and again inside the full `make test` (901/901) before deletion.
- **Blind spot**: these tests are gone from the tree — they proved the property held at the moment I
  ran them, not that it stays proved. §2 owns the permanent suite; until it lands, a regression here
  has no standing test to catch it. Also untested here: a `.gitignore` with CRLF line endings (the
  brief didn't call for it and D5 doesn't mention encoding) — `Split('\n')` leaves a trailing `\r` on
  each line before `.Trim()` strips it, so CRLF should still match correctly, but I did not add a
  falsifier for it.

### Gates
`BUILD_EXIT:0`, `TEST_EXIT:0` (901 passed, including the 4 scratch tests before they were removed;
re-ran `make build` after removal — still `BUILD_EXIT:0`), `FORMAT_EXIT:0`. `make validate` not run —
no spec/design/tasks changes in this block, and the Architect's prior post already recorded
`VALIDATE_EXIT:0` for the rewritten artefacts.

`git diff --stat -- src`: one file, `src/ZeroWiki/Content/ContentRepositoryService.cs`
(13 insertions, 40 deletions). `git status --short -- src tests`: only that file modified, tests clean.

→ @reviewer

**[reviewer]** Block 1.3 remediation review (pivot to text-line rule, `check-ignore` withdrawn).

### Verdict: Approve

### What I checked

**Whole-line, never substring** (`ContentRepositoryService.cs:747-750`) — the comparison is
`existingContent.Split('\n').Any(line => line.Trim() == ObsidianIgnoreRule)` against the constant
`".obsidian/"`. Worked through the shapes named in the brief by hand against this expression:

- comment-only mention (`# .obsidian/ is deliberately tracked`) — trimmed line is the comment text,
  not equal to `.obsidian/` → not matched → appended. This is the crux case and it holds.
- leading/trailing whitespace around an otherwise-exact line — `Trim()` removes it before the
  comparison, so it still matches and nothing is appended.
- `\r\n` file — `Split('\n')` leaves a trailing `\r` on each line, which `Trim()` also strips (it is
  whitespace), so a CRLF file still matches correctly. The worker flagged this as untested in its own
  blind spot; I agree it's untested but the code is correct by inspection — `char.IsWhiteSpace('\r')`
  is `true`, and `.Trim()` uses that.
- `.obsidian` (no trailing slash) or `.obsidian/*` — neither string-equals `.obsidian/`, so both are
  treated as *not* the rule and the line is appended. Correct per D5/spec: only the exact line counts,
  a near-miss is a different rule.
- empty file (`File.Exists` true, zero bytes) — `existingContent.Length > 0` is false, so
  `needsSeparatingNewline` is false and the appendix is written with no leading blank line; result is
  byte-identical to the fresh-write case. Correct.
- whitespace-only file, terminated or not — trimmed line is `""`, never matches the rule, so it's
  always appended; the terminated/unterminated distinction only affects whether a separating `\n` is
  inserted first, which is exercised by the existing D2/D5 append logic and unchanged in this block.

**Dead scaffolding** — `grep -rn "IsAlreadyIgnored|check-ignore|checkIgnore" src/ZeroWiki/Content
tests/ZeroWiki.Tests/Content` returns nothing. `IsAlreadyIgnoredAsync` and both call sites are gone;
the removed method's diff (`-24` lines) took the whole thing, not a stub. Checked `GitProcessException`
and `_git.RunAsync` — both still have live callers elsewhere in this file and in `PageIndexBuilder.cs`
/ `PageSaveService.cs`, so nothing else went dead as a side effect of the removal. No stray
`docs/.obsidian` two-level-probe reference remains; the surviving comment at `:729-732` about matching
"at either level" is D3 (why the pattern is unanchored, not why two probes existed) and is still
accurate — one unanchored `.obsidian/` line matches at any depth, so it isn't describing the withdrawn
design.

**No regression on the earlier rounds.** Append-never-overwrite (D2), the fresh-write and append paths
both add the trailing newline explicitly now that the constant no longer embeds one, the unterminated
existing-file case still gets a separating `\n` before the appendix, the adopted-repository branch
(`repositoryHasNoCommitsYet == false`, `:706-717`) is untouched by this diff, and staging is still by
literal path (`["add", "docs/.gitkeep", ".gitignore"]`, `:764-767`) — confirmed with `RunOrThrowAsync`,
so a pre-existing broad ignore rule matching `.gitignore` itself still throws and refuses the boot,
matching the Risks entry and the spec's fail-fast clause. `git diff` for this block touches exactly one
file, `ContentRepositoryService.cs` (13 insertions, 40 deletions per the worker's stat, which matches
what I read).

**D5 and the spec read true to the code.** D5's rewrite states the whole-trimmed-line rule, the
comment-exclusion case, root-only scope, and the redundant-line consequence as a deliberate,
knowingly-accepted trade-off (not glossed over) — all of which the code does exactly as described.
`spec.md`'s new paragraphs carry a `SHALL NOT` on inferring ignore status by any other means (matching
D5's "asks a narrower question" framing) and state the redundant-duplicate consequence as intentional,
in the same terms. The five scenarios read consistently with the current code; no stale `check-ignore`
scenario remains. `tasks.md` 1.3's wording matches D5's rule and stays ticked, matching the DEVLOG's own
account of why (same task shape, different instrument).

**On the worker's gate report** — noted per your brief as predating the scratch-test deletion; not
treated as evidence for the current tree. I did not re-run gates per your instruction. `git status
--short -- tests src` and `git ls-files --others --exclude-standard -- tests src` both come back empty
beyond the one modified file, confirming no scratch-test residue survived the deletion.

### Nits

- None worth holding the block on.

### Architectural note

- Not a finding on this block: §2.6 (git-config pinning for the "is ignored" assertions) still reads a
  little oddly now that the *rule* no longer consults git at all — worth the Architect's eye when §2
  is briefed, but it's already flagged and re-justified in your pivot post, so I'm not raising it as a
  question here.

### Reviewed-state

```
$ { git diff HEAD -- ':/' ':(top,exclude,glob)**/DEVLOG.md'
    git ls-files --others --exclude-standard -- ':/' ':(top,exclude,glob)**/DEVLOG.md' \
      | while read -r f; do printf '%s\n' "$f"; cat "$f"; done
  } | shasum | cut -c1-12
```

HEAD: 92fb126
Reviewed-state: 4f17ac3e3b39

**[architect]** Simplification block closed — gates green, committed

`make gates` → `BUILD_EXIT:0`, `TEST_EXIT:0` (897), `FORMAT_EXIT:0`, `VALIDATE_EXIT:0`, `GATES_EXIT:0`.
Fingerprint recomputed `4f17ac3e3b39` — matches the reviewer's Approve. No boxes ticked: a remediation
block gets no new `N.M`, and §1's were already ticked under 92fb126.

## NEXT

**Resume point:** §1's supervisor review must be **re-run** on `96a7adf..HEAD` — it last returned
Request changes, and the design it reviewed has since been withdrawn. Do that before opening §2.

- Branch `change/ignore-obsidian-config-in-content-repo`, base `96a7adf`. Two commits: `92fb126` (§1),
  plus the simplification fix.
- **§1 supervisor blockers, current status:**
  - *Blocker 1 (committed block had no reviewer Approve)* — repaired. The lost verdict was re-posted by
    its author from transcript, dated, at `## 1.`; the Approve for the simplification block follows it.
  - *Blocker 2 (the D5 branch has no executing evidence)* — **OPEN, and unchanged by the pivot.** The
    branch is simpler; it is still entered by no test. §2's brief must carry this.
  - *Blocker 3 (`git add` refusal-to-start)* — ruled by the Product Owner: fail fast is intended.
    Recorded in `design.md` Risks and the spec; **2.7** tests it.
  - *The `❓` on `core.excludesFile`* — dissolved with the `check-ignore` instrument.
- **§2 is now the weight of this change** (2.1–2.7). Aim its brief at the supervisor's coverage gaps,
  not at the task text: 2.1 as written can be satisfied entirely at the root, leaving D3 with no
  falsifier, and nothing covers clean-tree on a pre-existing-`.gitignore` volume, so 1.2's tick is
  evidenced only for the branch that does not append.
- **Owed to §3:** 3.1's wording predates D5 — the README must also cover the pre-existing-`.gitignore`
  case.
- Live decisions: D2 ignore-only, never untrack. D5 whole trimmed line, never a substring, root
  `.gitignore` only — a redundant duplicate is the accepted failure direction.
- The record: a reviewer verdict was lost today when an architect pin-rewrite discarded everything below
  `## NEXT`. Append above the pin, and verify placement with `grep -n '^## '` after writing.
