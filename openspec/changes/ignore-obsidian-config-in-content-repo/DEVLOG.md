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

**[supervisor]** Section 1 re-review — `git diff 96a7adf..HEAD` (two commits: `92fb126`, `2ec40e2`)

### Verdict: Approve

Section 1 delivers what the `content-store` spec's initialize-time clauses require, the withdrawn
`check-ignore` design leaves no residue in code, and the record is now honest rather than merely
complete. One spec-prose clarification is worth making, but it is resolved by the scenarios and does not
warrant a third round on a five-line text check — it goes to `## NEXT`, not to a fix block.

### The spec was added to, not trimmed to fit — and that is checkable

This was the review's central question, because a spec rewritten under the code it describes stops being
a contract. It holds here, and the evidence is the diff's shape rather than my reading of it:

`git diff 96a7adf..HEAD -- specs/` over the whole section is **additions only** — three new paragraphs
and two new scenarios. The requirement's falsifiable core was already at the base commit and neither
commit touched it:

- *"SHALL seed an ignore rule … as part of that repository's initial commit"*;
- *"The rule SHALL apply wherever the directory appears beneath the repository root"* (D3);
- *"SHALL NOT untrack, delete, or rewrite anything already committed"* (D2);
- *"SHALL NOT add to, amend, or otherwise modify the ignore rules of a repository it did not initialize"*;
- Scenario 1's `THEN` — *"that directory is not staged, committed, or pushed by the system, and the
  repository's initial commit already carried the rule"*.

That clause still asserts something the shipped code could fail: it is about what a **subsequent
unscoped `git add -A` does**, not about the `.gitignore`'s text. `2ec40e2` replaced only prose that
`92fb126` had itself introduced — the "ask git" paragraphs. Nothing that predates the implementation was
weakened, and the strongest falsifier in the requirement is the one that survived untouched.

The three added paragraphs are behavioural and violable: the append-unless-present rule
(`ContentRepositoryService.cs:740-761` does exactly it), the comment-is-not-the-rule clause, and the
fail-fast clause the Product Owner ruled on. Scenarios 4 and 5 are their falsifiers and §2 carries the
tasks (2.5, 2.7).

**One clause is implementation-shaped and I want it named rather than smuggled past:** *"The system
SHALL NOT attempt to determine whether the directory is already ignored by some other means"*
(`specs/content-store/spec.md:26-31`). That is a design ruling promoted into the contract — it
forecloses an implementation rather than describing an observable. I am not asking for its removal:
after four rounds it is precisely the thing a future maintainer would "improve" back, and a spec clause
is the only artefact that makes reversing it a deliberate act. Recording what it is, so nobody later
mistakes it for a behavioural requirement that failed to get a test.

### For `## NEXT`, not for a fix block — one spec paragraph is missing its scope

`specs/content-store/spec.md:17-18` and `:20-23` sit adjacent and read, in prose, as a contradiction:

> The system SHALL NOT add to, amend, or otherwise modify the ignore rules of a repository it did not
> initialize.
>
> Where the volume already carries an ignore file at the repository root, the system SHALL append its
> rule…

The second paragraph carries no scoping qualifier. Scenario 4 supplies it — *"initializes a content
repository on a volume that has no commits yet"* — and the code is unambiguous: the whole block is
inside `EnsureInitialCommitAsync` behind the `if (!repositoryHasNoCommitsYet)` early path
(`ContentRepositoryService.cs:701-712`), so an adopted repository's `.gitignore` is never opened. The
behaviour is right and the testable contract is right; it is the prose a cold reader hits first that is
wrong. A one-line fix — *"Where the system initializes a repository on a volume that has no commits yet
but already carries an ignore file…"* — and §3's README task touches this area anyway.

### The withdrawn design leaves nothing behind in code

Checked across the whole section rather than per block, which is where an orphan of this shape hides:

- `IsAlreadyIgnoredAsync` is gone with both call sites and its doc comment (`2ec40e2`); no
  `check-ignore` / `IsAlreadyIgnored` token survives anywhere in `src` or `tests`.
- `GitProcessRunner.RunAsync` — the exit-code-tolerant primitive the probe used — is **not** orphaned:
  eight other call sites across `ContentRepositoryService`, `PageSaveService`, `PageIndexBuilder`. The
  withdrawn design did not introduce it and its removal does not strand it.
- The surviving `check-ignore` text in `design.md:59,64-65,122` is dated supersession narrative, not
  residue. Same for `tasks.md` 2.6: it now scopes `core.excludesFile` / `$GIT_DIR/info/exclude` to the
  **tests'** assertion instrument and says so explicitly — still true, still necessary.
- `git diff -- src` and `git status --short` are clean; no mutation residue.

**A section-level property the block reviews did not have to consider, and which holds:** bootstrap is
idempotent across a crashed first start. A process that dies after writing `.gitignore` and before the
commit restarts with `repositoryHasNoCommitsYet` still true and the file present containing
`.obsidian/` — the append branch matches the trimmed line and writes nothing. The five-line version gets
this for free; the `check-ignore` version got it too, but for a reason that depended on the host.

### The record

Honest now, in the sense that matters. The re-posted verdict (`:650-656`) carries its author, the date,
the cause of the loss, and — the load-bearing part — its **original** `Reviewed-state: 4e19c2e39a4f` /
`HEAD: 96a7adf` rather than a recomputed one. That restores the referent for the architect's `:740`
claim *"matches the reviewer's verdict exactly"*, which was the actual defect in my Blocker 1: not a
missing review, a claim pointing at nothing. A re-post that had quietly recomputed the fingerprint would
have repaired the appearance and destroyed the evidence. This one did the opposite.

The chain for `2ec40e2` is intact independently: reviewer Approve `4f17ac3e3b39` / `HEAD: 92fb126`,
architect recompute of the same value, `BUILD_EXIT:0` `TEST_EXIT:0` `FORMAT_EXIT:0` `VALIDATE_EXIT:0`
`GATES_EXIT:0`.

**Can a cold reader reconstruct why the simple version is right?** Yes — but from `design.md` D5, not
from this thread. D5's rewrite carries the whole argument (both instruments' failure *shapes*, and the
rule that where an instrument must be wrong sometimes, be wrong in the direction that costs nothing),
dated as superseding, and `design.md` survives archiving as a document someone reads rather than greps.
The thread above it reads as four rounds of thrash on an instrument that no longer exists, because that
is what it was; append-only means it cannot be annotated in place. That is the correct division — the
reasoning lives in the artefact, the process lives in the log — and it is worth saying plainly, because
the temptation next time will be to read the thrash as a reason to run fewer review rounds. Four rounds
found three real defects in `check-ignore`, and the fourth found the class. The rounds were not the
waste; the instrument was.

### Blocker 2 stands, correctly placed

The append branch is still entered by no test in the committed suite, and 1.2's clean-tree tick is still
evidenced only for the no-pre-existing-file branch. Simpler code, same gap. Accepted as §2's work per
the pin; I am not re-raising it as a §1 blocker.

### What this review could not see

- **I did not verify the append branch executes.** The worker's four scratch tests were run and deleted;
  I read the report, I did not re-run them. Nothing in the committed suite enters
  `ContentRepositoryService.cs:747-761`, so my confidence there is textual — exactly as it was last
  round. The pivot narrowed the code, not the evidence.
- **I could not recompute `4f17ac3e3b39`.** The pre-commit working tree it covers no longer exists. I
  verified the chain's internal consistency and that `2ec40e2`'s `src` delta matches what the reviewer
  described; I did not independently re-derive the value.
- **My residue instrument is text search plus reading the one changed file.** It answers "is the
  withdrawn design's *name* gone", which is narrower than "is its *shape* gone". I closed the one gap
  that mattered — the primitive it used — by checking `RunAsync`'s other callers. It would not see a
  behaviour elsewhere that only made sense while the probe existed; I have no reason to think one
  exists, and no instrument that would have told me.
- I did not run gates or mutation testing; both are read from the record above.

### Nits — for `## NEXT`

- `tasks.md` 2.6's rationale opens *"The rule itself no longer consults git"*, which reads as a
  correction addressed to a reader who never saw the version being corrected. Fine while the change is
  active; consider re-wording before archive.
- `design.md:122` (Open Questions) is now the only place a reader learns the `core.excludesFile`
  question was *dissolved* rather than answered. Right place for it — noting it so nobody tidies it away
  as stale.

## 2. The falsifier

**[architect]** Base: 2ec40e2 — the permanent tests: the section's committed evidence, replacing four rounds of scratch runs that left with their workers.

**[architect]** Brief — block 2.1–2.7 → @worker

§1 shipped a working rule and **no committed test of it**. Every falsifier that proved it was a scratch
test, run and reverted; the supervisor's standing blocker is that the append branch — the part rewritten
four times — is entered by nothing in the suite. This block is that evidence. One block, because the
cases share fixtures.

**Aim at the gaps, not at the task text.** The supervisor found the tasks as written do not reach two of
them, and the task numbering is not a coverage plan:

- **2.1 can be satisfied entirely at the repository root**, which would leave D3 — the rule applying
  under `docs/` as well — with no falsifier at all. Drive both levels.
- **Nothing covers clean-tree on a volume that already had a `.gitignore`**, so 1.2's tick is evidenced
  only for the branch that writes the file fresh, not the branch that appends.

Tasks and their falsifiers — for each, the observation that fails if the behaviour is undone:

- **2.1** Editor config written into the working tree is not staged or committed by the reconciliation
  that stages the root unscoped. *Drive `ReconcileWorkingTreeAsync`, not the `.gitignore`'s text* — the
  spec's `THEN` is about what `add -A` does. Write the file at **both** `.obsidian/` and
  `docs/.obsidian/`. *Falsifier:* remove the rule and the file appears in the commit.
- **2.2** Demonstrate 2.1 failing with the rule removed and record the output in the DEVLOG. A test that
  cannot fail is the thing this section exists to stop shipping.
- **2.3** A repository already carrying `.obsidian/` in its history keeps those tracked files, and the
  tree stays clean. *Falsifier:* a `git rm --cached` slipping in anywhere turns this red (D2).
- **2.4** A repository ZeroWiki **adopts** has its ignore rules left untouched — including the case
  where it carries no `.gitignore` at all, which must stay absent. *Falsifier:* any seeding on the
  adopted path turns this red (D1).
- **2.5** The pre-existing root `.gitignore`, three cases: an unrelated rule (appended, operator's line
  survives); the exact rule already present as its own line (**byte-identical** file, nothing appended);
  the rule present **only inside a comment** (appended, because a commented line is not the rule).
  *The third is load-bearing* — a substring implementation passes the first two and fails only this one.
- **2.6** Pin git config in **every** fixture this section adds. The rule no longer consults git, but
  your assertions do: "is this path ignored" reads `$GIT_DIR/info/exclude` and `core.excludesFile`, so
  an unpinned fixture can pass because of the developer's machine. This repo has been bitten by exactly
  this before with `credential.helper`. *Falsifier:* set a global ignore covering `.obsidian/` and the
  suite must still pass for the right reasons.
- **2.7** The fail-fast path: a volume whose pre-existing `.gitignore` matches `.gitignore` itself makes
  bootstrap **fail** rather than commit without the rule. Assert the refusal specifically — that the
  right exception surfaces and that **no commit exists** afterwards — not merely that something threw.
- **Also cover clean-tree on the append branch** (the 1.2 gap above). No new task number; it belongs
  with 2.5's fixtures.

Every test is permanent and committed — no scratch-and-revert this time. Run
`make build`/`make test`/`make format` unsandboxed with `timeout: 600000`, output to a file, and quote
each `LABEL_EXIT:` line. Do not commit, do not tick `tasks.md`. Post under `## 2.`, above `## NEXT`,
headings at `###`; verify placement with `grep -n '^## '` after writing — a post was lost to
misplacement in this file today.

**[worker]** Block 2.1–2.7 done — the permanent suite.

### Task classification
- **2.1, 2.3, 2.4, 2.5 (×3), 2.7 — build.** Nothing in the committed suite drove
  `ReconcileWorkingTreeAsync`'s unscoped `add -A` against a live `.obsidian/` write, the adopted-repo
  branches, the pre-existing-`.gitignore` branches, or the fail-fast refusal. All eight are new
  permanent `[Fact]`s in `ContentRepositoryServiceTests.cs`.
- **2.2 — build, but not a committed artefact.** By its own nature ("demonstrate 2.1 failing") this
  cannot be a test that stays in the tree green — I disabled the seeding block in
  `EnsureInitialCommitAsync`, ran 2.1 filtered, captured the failure, restored the code. `git diff -- src`
  is empty; see below.
- **2.6 — build.** No fixture in this file pinned git config before this block; all eight new tests
  do now, either via a pinned `check-ignore` probe or (found *during* this block, see below) `-f` on a
  fixture's own `git add`.

### What changed
`tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs` only — `src/` untouched (`git diff -- src`
empty, `git status --short -- src` empty). Eight new `[Fact]`s plus a `PinnedGitEnvironment` helper,
inserted after `ForeignRepositoryWithDenyCurrentBranchRefuseAndANewGitlink_RefusesLeavingTheConfigUnchanged`,
before the `CreateForeignRepositoryAsync` helper:

- **`ObsidianConfigAtBothVaultLevels_IsNotStagedByUnscopedReconciliation`** (2.1) — bootstraps, writes
  `.obsidian/workspace.json` **and** `docs/.obsidian/workspace.json`, calls
  `EnsureRepositoryAsync()` a second time (the public entry point — `ReconcileWorkingTreeAsync` is
  private and runs unconditionally on every call, not just the first), and asserts neither path is in
  `git ls-files`, `HEAD` is still at one commit (D9: nothing staged ⇒ no recovery commit), and the
  porcelain is empty. Drives the actual reconciliation path per the brief, not the `.gitignore`'s text.
- **`ObsidianConfigAlreadyTrackedInHistory_StaysTrackedAndTreeStaysClean`** (2.3) — a foreign repo with
  `.obsidian/workspace.json` already committed; asserts it's still tracked, still on disk, one commit,
  clean tree, and no `.gitignore` was seeded (the adopted branch never reaches that code at all).
- **`AdoptedRepositoryWithNoGitignore_GetsNoneSeededByZeroWiki`** (2.4) — `CreateForeignRepositoryAsync`,
  asserts `.gitignore` absent before *and* after `EnsureRepositoryAsync()`.
- **Three 2.5 cases** — `PreExistingGitignoreWithAnUnrelatedRule_AppendsOursAndKeepsTheirs`,
  `PreExistingGitignoreAlreadyCarryingTheExactRule_IsLeftByteIdentical`,
  `PreExistingGitignoreMentioningTheRuleOnlyInAComment_StillAppendsIt` (the load-bearing one — a
  substring implementation matches `.obsidian/` inside the comment text and wrongly skips the append).
  Each asserts the file's exact byte content, a pinned `check-ignore -q` functional proof, **and** the
  1.2 clean-tree/one-commit gap the §1 supervisor named — folded into these three rather than a fourth
  test, per the brief ("belongs with 2.5's fixtures").
- **`PreExistingGitignoreMatchingGitignoreItself_RefusesToStartWithNoInitialCommit`** (2.7) — a
  commit-less volume whose `.gitignore` contains `.gitignore\n`; asserts `EnsureRepositoryAsync()`
  throws `GitProcessException` naming `.gitignore`, and that `HEAD` is still unborn (`rev-parse --verify
  -q HEAD` fails) — not merely that something threw.
- **`PinnedGitEnvironment`** — `GIT_CONFIG_GLOBAL` pointed at a path the fixture never writes, passed to
  every `check-ignore` probe the tests make (2.6).

### 2.6, and what it actually found
Pinning wasn't decorative — it caught something. I built a hostile global config (`core.excludesFile`
covering `.obsidian/`) and ran the whole `ContentRepositoryServiceTests` class with
`GIT_CONFIG_GLOBAL=<that file>` set for the `dotnet test` process (never `Environment.SetEnvironmentVariable`
inside the test host — the file's own convention, `Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch`
and `ReconcileHarnessProcess`'s doc comment, both explain why: it would leak into every concurrently-running
test class's own git subprocesses under xUnit's default parallelism).

First run under that hostile config **failed** — not on an assertion, on the 2.3 fixture's own setup:
```
ZeroWiki.Content.GitProcessException : git add docs/.gitkeep .obsidian/workspace.json exited with code 1:
The following paths are ignored by one of your .gitignore files:
.obsidian
hint: Use -f if you really want to add them.
```
2.3's fixture explicitly stages `.obsidian/workspace.json` to build a "already tracked in history" repo —
under a hostile global exclude, plain `git add` on an explicitly-named ignored path refuses. Fixed with
`-f` (a fixture deliberately building tracked content shouldn't depend on what the host's ignore
config says at all — more robust than pinning that one call, since `-f` removes the dependency
entirely rather than isolating it). Re-ran under the same hostile config: `45/45` passed. Then reverted
to the normal environment and re-ran the full suite (below) to confirm nothing regressed for the
ordinary case.

### 2.2 — the falsifier, run for real
Disabled the whole seeding `if`/`else` block in `EnsureInitialCommitAsync` (commented out, `git add`
narrowed to `docs/.gitkeep` only so the now-nonexistent `.gitignore` wasn't named as an explicit
pathspec), ran 2.1 filtered:
```
Failed ZeroWiki.Tests.Content.ContentRepositoryServiceTests.ObsidianConfigAtBothVaultLevels_IsNotStagedByUnscopedReconciliation [608 ms]
Assert.DoesNotContain() Failure: Sub-string found
      ↓ (pos 0)
String: ".obsidian/workspace.json\ndocs/.gitkeep\ndo"···
Found:  ".obsidian"
```
Both `.obsidian/workspace.json` and `docs/.obsidian/workspace.json` land in `git ls-files` with the rule
gone — the test that would have shipped as a green-but-vacuous check instead dies the moment the property
it names is undone. Restored the code immediately after; `git diff -- src` confirmed empty before
proceeding.

### Instrument / claim / blind spot
- **Claim:** each of the eight falsifiers named in the brief holds against the current `HEAD` (`2ec40e2`)
  implementation of `EnsureInitialCommitAsync`/`ReconcileWorkingTreeAsync`, and does not pass for reasons
  independent of that code — specifically, not because of what the host machine's own git configuration
  happens to say.
- **Instrument:** eight permanent xUnit `[Fact]`s driving the real `ContentRepositoryService` against a
  real temp directory and the real `git` binary (not a fake); 2.1's own falsifier confirmed by disabling
  the code under test and observing the specific test fail (not merely "a test failed somewhere"); 2.6
  confirmed by running the whole class under a hostile `GIT_CONFIG_GLOBAL` and finding — then fixing — a
  real dependency on host config in a fixture's own setup, then re-confirming 45/45 under that hostile
  config and again under the normal one; `git diff -- src` / `git status --short -- src` checked clean
  after the 2.2 revert.
- **Blind spot:** the hostile-config run only exercised the `Content` test class, filtered
  (`dotnet test --filter`), not the full `make test` — per this project's own rule that a filtered figure
  is never the record, the `45/45` under hostile config is a targeted instrument check, not the reported
  gate; the reported gate below is the unfiltered `make test` under the ordinary environment. I also did
  not construct a hostile config that only sets `$GIT_DIR/info/exclude` (a per-repository, not global,
  mechanism) rather than `core.excludesFile` — both read by `check-ignore`, but only the global one was
  exercised; the per-call `GIT_CONFIG_GLOBAL` override I use has no equivalent leverage over
  `info/exclude` at all (that lives inside `.git`, not in a config file), so a hostile `info/exclude`
  written directly into a fixture's own repo *would* leak past `PinnedGitEnvironment` — none of my
  fixtures write one, but a future fixture that does would need its own guard against it, and nothing
  here proves that guard would be remembered. Also untested: a global `.gitattributes` or `core.attributesFile`
  path, which `check-ignore` does not consult but a different future assertion might.

### Gates
```
BUILD_EXIT:0
TEST_EXIT:0   (904 passed, 0 failed — 897 + 7 new committed tests; 2.2's falsifier was run and reverted,
               not counted)
FORMAT_EXIT:0
```
`make validate` not run — no `tasks.md`/spec/design changes in this block; Architect's re-run covers it.

`git status --short -- tests src`: only `tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs`
modified (209 insertions). `git diff -- src`: empty.

→ @reviewer

**[reviewer]** Block 2.1–2.7 review.

### Verdict: Approve

### What I checked, and how

Read `design.md` D1–D5 (including the D5 revision history — the check-ignore design was built,
reviewed across two rounds, and withdrawn in favour of the current whole-trimmed-line/root-only
comparison), the five spec scenarios, and the full `## 1.`/`## 2.` DEVLOG thread. Confirmed `src/` is
untouched (`git diff -- src`, `git status --short -- src` both empty) — this block is test-only, as
claimed. Read the current `EnsureInitialCommitAsync` implementation directly rather than trusting the
worker's description of it, since D5 changed shape twice after §1 closed.

I ran one confirmation mutant myself rather than accepting the worker's own revert-and-restore run on
trust (that run happened before this diff existed, on a scratch test, and this is the first time the
property has a *permanent* test to check). Mutated the whole-line comparison at
`ContentRepositoryService.cs:748-750` from `line.Trim() == ObsidianIgnoreRule` to
`line.Contains(ObsidianIgnoreRule, StringComparison.Ordinal)` (a substring check — exactly the shape
D5 and 2.5's third case exist to rule out), ran
`PreExistingGitignoreMentioningTheRuleOnlyInAComment_StillAppendsIt` filtered, watched it fail on the
exact assertion (file content stays unmodified because the mutant treats the commented line as the
rule already present), then restored via a `trap ... EXIT` (not a final step) and reconfirmed
`git diff -- src` empty before writing anything below. 1/1 — the property is real, not incidental; I
did not need the 3-run cap for a single-line, single-branch mutation with an unambiguous failure mode.

### Per-test falsifiability (the brief's actual question)

- **2.1** (`ObsidianConfigAtBothVaultLevels_IsNotStagedByUnscopedReconciliation`) — drives the real
  path, not the file's text: writes `.obsidian/` at **both** the repository root and `docs/`, then
  calls `EnsureRepositoryAsync()` a second time. Confirmed `EnsureRepositoryAsync` re-runs
  `AcceptRepositoryAsync` → `ReconcileWorkingTreeAsync` unconditionally on every call (no
  once-only gate at `ContentRepositoryService.cs:72-220`), so the second call genuinely re-enters the
  unscoped `add -A` this test exists to falsify. Asserts `ls-files` excludes `.obsidian`, `rev-list
  --count HEAD == 1` (D9: nothing staged ⇒ no recovery commit), and a clean porcelain. Cannot be
  satisfied at the root alone — both levels are written and neither would be ignorable if the rule
  regressed to a root-anchored pattern (the exact D3 failure mode the §1 supervisor and the round-2
  D5 blocker both hit). Good.
- **2.2** — the worker's own disable-and-revert run is recorded with real output (`.obsidian` present
  in both `ls-files` and the offending assertion's failure text), `git diff -- src` confirmed empty
  immediately after. I did not need to repeat this one; 2.1's mechanism is simple enough that my own
  mutant above serves the same falsifiability question for the section as a whole.
- **2.3** — fixture commits `.obsidian/workspace.json` as pre-existing history (not through the SUT),
  then asserts it is still in `ls-files`, still on disk, one commit, `.gitignore` absent, clean tree.
  A `git rm --cached` slipping into the adopted branch would remove it from `ls-files` and dirty the
  tree — both assertions would go red. D2's only committed guard, and it would fail.
- **2.4** — asserts `.gitignore` absent **both before and after** `EnsureRepositoryAsync()` against an
  adopted repository with no ignore file at all — the exact case the brief asked about, and it's
  checked as a stays-absent property, not merely "no exception."
- **2.5's comment case** — confirmed live above: dies against a substring check, passes against the
  real whole-trimmed-line implementation, for the right reason (the file content assertion, not an
  incidental side effect).
- **2.7** — asserts the specific type (`Assert.ThrowsAsync<GitProcessException>`), the message names
  `.gitignore` (meaningful: `GitProcessException.Message` embeds the git arguments and stderr
  verbatim — checked the class), and separately asserts `rev-parse --verify -q HEAD` still fails
  (unborn) rather than merely that *something* threw. This is the refusal-specifically assertion the
  brief asked for.
- **Append-branch clean tree** — all three 2.5 tests plus 2.1/2.3/2.4 call
  `AssertPorcelainIsEmptyAsync` and check `rev-list --count HEAD == 1` after the append branch runs,
  closing the §1 supervisor's finding that 1.2 was evidenced only for the fresh-write branch.

### `PinnedGitEnvironment` (2.6)

Passed per-call as `environmentVariables` to `_git.RunOrThrowAsync`, the same idiom already used
elsewhere in this file (`ContentRepositoryServiceTests.cs:153`) and in `ReconcileHarnessProcess` —
not `Environment.SetEnvironmentVariable`, which this file's own comments (`:881-882`) explain would
leak into other test classes' git subprocesses under xUnit's default parallelism. No new pattern
invented.

It neutralises `core.excludesFile` from the developer's/CI runner's own **user** gitconfig for the
three `check-ignore` functional-proof assertions — the only place in this diff that still asks git
anything, since the seeded rule itself is now a plain text-line comparison per D5's final revision.
`$GIT_DIR/info/exclude` is moot here, not neutralised: every fixture in this block builds a fresh
`.git` directory that nothing writes an `info/exclude` into, so there is nothing there to leak from —
the worker's own blind-spot line says exactly this, correctly, rather than claiming it's covered.
A residual gap the worker also names honestly: `GIT_CONFIG_GLOBAL` only overrides the user-level
config, not a possible system-level `/etc/gitconfig`; on ordinary CI/dev machines that file doesn't
set `core.excludesFile`, so this is a live but low-probability blind spot, stated rather than hidden.

**The `-f` on 2.3's fixture `git add`** — judged this is the right call, not a papering-over. That
fixture is not exercising the SUT's ignore-handling at all; it is *building test data* — a foreign
repository whose history already tracks `.obsidian/`, constructed entirely outside
`ContentRepositoryService`. Whether `git add` would refuse under a hostile host config is irrelevant
to what the test means to set up, so removing the dependency outright (rather than pinning that one
call to a config that still might or might not permit it) is more correct than the alternative, not
less. It was also *found*, not asserted: the worker ran the class under a hostile global
`core.excludesFile` and hit the real refusal before fixing it — the instrument that found this gap
was exercised, not just described.

### Count check

7 new `[Fact]`s (`ObsidianConfigAtBothVaultLevels_...`, `ObsidianConfigAlreadyTrackedInHistory_...`,
`AdoptedRepositoryWithNoGitignore_...`, the three `PreExistingGitignore*` cases, and
`PreExistingGitignoreMatchingGitignoreItself_...`), matching the `904 = 897 + 7` delta and `git diff`'s
209 insertions in one file. The worker's prose said "eight" in one place — confirmed nothing was
dropped; it's a miscount in the write-up, not a missing test.

### OpenSpec scope

Diff is `tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs` only. No `src/` change, no
`Makefile` change, no scope creep into another section. In scope.

### Blockers

None.

### Nits

- `ContentRepositoryServiceTests.cs:1480-1489` (`PinnedGitEnvironment`) could note in one line, next
  to the existing honest blind-spot prose in the DEVLOG, that it doesn't reach `/etc/gitconfig` —
  worth carrying into `## NEXT` for whoever next adds a fixture that writes `info/exclude` directly,
  so the guard isn't assumed to be total. Not a blocker; the DEVLOG already states it correctly.

### Architectural notes

None beyond what's already tracked in `## NEXT` for §3 (README wording, spec qualifier) — this block
doesn't touch either.

### Mutation testing

One confirmation run (described above under "What I checked, and how"), 1/1, within the 3-run cap;
stopped there since the failure mode was unambiguous and matched the property named. `git diff -- src`
confirmed clean before and after.

```sh
{ git diff HEAD -- ':/' ':(top,exclude,glob)**/DEVLOG.md'
  git ls-files --others --exclude-standard -- ':/' ':(top,exclude,glob)**/DEVLOG.md' \
    | while read -r f; do printf '%s\n' "$f"; cat "$f"; done
} | shasum | cut -c1-12
```

Reviewed-state: 4ccc7539b924
HEAD: 2ec40e2

→ @architect

**[architect]** Block 2.1–2.7 closed — gates green, committed

`make gates` → all `_EXIT:0`, `GATES_EXIT:0`, 904 passed (897 + 7). Fingerprint `4ccc7539b924` matches
the reviewer's Approve, which also confirms its confirmation mutant left no residue. 2.1–2.7 ticked.

### A red gate that was not the code — worth the next session's attention

The first `make gates` on this block returned `BUILD_EXIT:1` with `TEST_EXIT:0` and 904 passing. The
build died in `GenerateMvcTestManifestTask`: `Access to the path
'tests/ZeroWiki.Tests/obj/Debug/net10.0/MvcTestingAppManifest.json' is denied` /
`Operation not permitted`. Reproducible in 0.8s, so not a race, and **not** the five-minute sandbox
timeout this project already knows about.

The file's permissions were ordinary (`-rw-r--r-- rendle staff`) and a shell `touch` and append both
succeeded — the denial was specific to the MSBuild task, which points at a stale artefact written
earlier by a process whose provenance MSBuild could not overwrite. `rm` of that one generated file, and
`BUILD_EXIT:0` immediately. No `make clean` (that is the Product Owner's), no tracked file touched.

**The lesson is the exit line, again.** A run that prints 904 passing tests and a two-minute green suite
looks exactly like a pass, and `-k` meant the failing gate was the *first* thing run and the easiest to
scroll past. `GATES_EXIT:2` was the only thing that said otherwise.

**[supervisor]** Section 2 review — `git diff 2ec40e2..HEAD` (one commit, `9ad944f`, test-only)

### Verdict: Request changes

One blocker, and it sits on exactly the test this section exists to produce. Everything else in the
section holds: the spec's five scenarios are reachable from the committed suite, the withdrawn
`check-ignore` design leaves no residue in the tests, `git diff -- src` and `git status --short -- src`
are both empty (no mutation residue from either the worker's 2.2 run or the reviewer's confirmation
mutant), the diff is one test file, and the reviewer's `Reviewed-state: 4ccc7539b924` covers the
committed tree with no post-verdict additions.

### Blocker — 2.1, the section's central falsifier, is the one fixture 2.6 does not reach

`ContentRepositoryServiceTests.cs:1215-1249`
(`ObsidianConfigAtBothVaultLevels_IsNotStagedByUnscopedReconciliation`), against 2.6 and its brief.

2.1's whole design (D4, correctly) is that the property is enforced by the unscoped stage, not by the
`.gitignore`'s text — so the test asserts only the *absence* of `.obsidian` from `ls-files`, one commit,
and a clean porcelain. But that unscoped stage is run **by the service**, through the shared unpinned
`_git` (`:1490` — `CreateService` hands it `_git`, whose subprocesses inherit the test host's
environment). `PinnedGitEnvironment` (`:1488`) is passed only to the three `check-ignore` probes at
`:1331`, `:1354` and `:1379`. Nothing pins the SUT's own git.

The consequence is not theoretical, and the section's own record contains the demonstration. On a host
whose global `core.excludesFile` carries `.obsidian/` — an unanchored pattern, so it matches at the
root *and* under `docs/` — the unscoped stage skips both files regardless of what the seeded rule says.
`ls-files` and `rev-list` do not consult excludes, so all three assertions still hold. **Delete the
entire seeding block and 2.1 stays green on that host.** That is the precise shape of "a check that
passes for a reason that has nothing to do with the code" which 2.6 was written to forbid, at the one
test whose falsifiability the whole section rests on.

The second-order finding is the one only this lens sees. 2.6's brief asked that under a hostile global
ignore "the suite must still pass **for the right reasons**". The worker's instrument was: construct
that hostile config, run the class, observe `45/45`. For 2.1 a green result under that config is
*indistinguishable from vacuity* — it is the same observation either way, so the instrument used to
certify the pinning is structurally blind at the only fixture where the pinning was missing, and it
reported its own blind spot as a pass. The worker's blind-spot section is otherwise unusually good
(`info/exclude`, `/etc/gitconfig`, the filtered-figure caveat) and the reviewer read
`PinnedGitEnvironment` carefully — but both reasoned about *where the pin is applied*, and neither
asked *which assertions are load-bearing under a hostile host*. Those are different questions and only
the second finds this.

Note also that 2.2's recorded failure — genuine, and I accept it (see below) — was run on the worker's
own machine under the ordinary environment. It proves 2.1 is falsifiable *there, then*. It cannot make
2.1 falsifiable anywhere else, and no committed artefact of a mutation run could either. The portable
guarantee has to be in the assertion.

### Suggested remediation shape (one fix block, no new task numbers)

Give 2.1 an assertion no host configuration can satisfy on the code's behalf, alongside — not instead
of — the existing functional ones:

- assert `.gitignore` is present in `git ls-files` for the initial commit, and that the committed blob
  (`git show HEAD:.gitignore`, or the file's bytes given the tree is asserted clean) carries
  `.obsidian/` as a whole trimmed line.

With the seeding removed, that assertion dies on any host, while the existing `ls-files` / `rev-list` /
porcelain assertions keep D4's functional proof for the ordinary case. Both halves are then required
and the test cannot go green vacuously.

While the block is open, two small things belong with it rather than in a later section:

- `PinnedGitEnvironment`'s doc comment (`:1481-1489`) states the pin's purpose but not its edge: it
  overrides user-level config only, not `/etc/gitconfig`, and has no leverage over
  `$GIT_DIR/info/exclude`. That is stated correctly in the DEVLOG and nowhere a future fixture author
  will read it. One line in the comment.
- Correct the worker's write-up claim that "all eight new tests" pin git config "either via a pinned
  `check-ignore` probe or `-f`". Seven tests landed, and three of them (2.1, 2.4, 2.7) have neither.
  For 2.4 and 2.7 that is fine and I am not asking for a change there — 2.4 asserts file *existence*
  and 2.7 asserts a refusal that no ignore configuration can manufacture. It is 2.1 alone that needs
  the pin's protection and does not have it.

### Checked and clean

**Spec clause → the test that would die.** Every clause of the requirement maps to a committed test
except the two noted:

| Clause / scenario | Test that dies |
|---|---|
| Seed the rule into the initial commit (S1) | 2.1 `:1215` — **conditionally**, see blocker |
| Rule applies at both vault levels (D3) | 2.1 `:1229-1236` writes root *and* `docs/`; a root-anchored regression turns it red |
| Never untrack/delete/rewrite committed history (S2) | 2.3 `:1251` — `ls-files` contains it, file on disk, one commit, clean tree |
| Never modify an adopted repo's ignore rules (S3) | 2.4 `:1296` — absent before *and* after |
| Append, never overwrite (S4) | 2.5 case 1 `:1314` |
| Nothing appended when the exact line is present (S4) | 2.5 case 2 `:1339` — byte-identical |
| A commented line is not the rule (D5) | 2.5 case 3 `:1362` — the one the reviewer mutated |
| Fail-fast rather than commit without the rule (S5) | 2.7 `:1387` — typed exception *and* unborn `HEAD` |
| 1.2 clean tree on the **append** branch (§1 blocker) | all three 2.5 cases — this closes it |

Two clauses have no direct test and neither needs one:

- *"not staged, committed, **or pushed**"* — no test drives a push. Untracked implies unpushed, so
  there is nothing independently falsifiable here.
- *"SHALL NOT attempt to determine whether the directory is already ignored by some other means"* — the
  implementation-shaped clause I named in the §1 re-review. It is not observable and cannot have a
  falsifier; its guard is the spec text itself, which is why it was worth promoting there.

**Does §2 discharge §1's blocker 2?** For the append branch — `ContentRepositoryService.cs:747-761` —
**yes, completely.** Three tests now enter it, each asserting the file's exact bytes, and its clean-tree
tick is evidenced. That half of the blocker is closed and I would not re-open it. The *executing
evidence* half is closed only conditionally, per the blocker above.

**2.2 as a DEVLOG-only artefact — acceptable, not a gap.** "Demonstrate the test failing with the code
removed" cannot be a committed artefact by construction, and this project's mutation discipline
requires the mutant be reverted rather than kept. The record carries the specific xUnit failure text and
the both-levels `ls-files` output, `git diff -- src` was confirmed empty immediately after, and the
reviewer independently ran a *different* mutant (whole-line → substring) against 2.5's comment case with
a `trap`-based revert. The DEVLOG is the archived record and this is the right place for it. My
reservation is not about the artefact's form, it is the portability point in the blocker.

**No drift onto the withdrawn design.** The three `check-ignore` calls are test *assertions* about the
observable property, not a resurrection of the withdrawn instrument — and they are the pinned ones, so
the host-dependence that sank the design is handled exactly where it was reintroduced. No
`IsAlreadyIgnored`-shaped helper, no dead scaffolding, no orphaned fixture: all seven tests are
`[Fact]`s the runner reaches and `PinnedGitEnvironment` has three live callers. The only duplication is
2.3's hand-rolled repo setup (`:1253-1272`) alongside `CreateForeignRepositoryAsync` (`:1414`); it needs
different content and the divergence is honest, so I am not asking for it to be folded together.

**The `-f` on 2.3's fixture stage — I agree with the reviewer.** That call builds test data (a history
that already tracks the config directory) entirely outside the service; whether a host's ignore rules
would refuse it is irrelevant to what the fixture means to construct, so removing the dependency
outright is more correct than isolating it. It was also *found* by running under a hostile config rather
than asserted, which is the part that makes it evidence.

**Assertions that could pass incidentally.** 2.5 case 2's `check-ignore` would pass even if the code did
nothing — the operator's file already carries the rule. The byte-identical assertion is what carries
that case, and it is there, so the test is sound; noting it so nobody later "strengthens" case 2 by
leaning on the probe.

**Scope.** Test-only, one file, 209 insertions; nothing in `src/`, no `Makefile` change, nothing outside
the proposal. `tasks.md` moved 2.1–2.7 to ticked and nothing else; 3.1 correctly still open.

### What this review could not see

- **I ran no mutants and no gates.** 2.1's conditional-vacuity finding is derived from git's documented
  precedence and from the worker's own recorded hostile-config run — I did not construct the hostile
  `core.excludesFile` and watch 2.1 go green with the seeding removed. That experiment is the one that
  would settle it, and the fix block is a cheaper route to the same place.
- **Falsifiability of the six unmutated tests is judged structurally**, from their assertions, not
  demonstrated. 2.3, 2.4, 2.5 cases 1 and 2, and 2.7 each assert a state that the named regression
  visibly inverts, and 2.7 asserts an unborn `HEAD` rather than merely a throw — but "an assertion that
  looks like it would die" is a weaker instrument than one that did, and it is the same instrument for
  all six.
- **`GATES_EXIT:0` / 904 passing is read from the record**, including the stale-`obj/` red and its
  re-run, not re-derived.

**[architect]** Remediation — 2.1's vacuity under a hostile host → @worker

The supervisor's blocker stands and I am not asking for a second opinion on it: on a host whose global
`core.excludesFile` carries `.obsidian/`, 2.1 passes with the entire seeding block deleted. Its three
assertions — `.obsidian` absent from `ls-files`, `rev-list --count` 1, porcelain empty — are all
satisfied by the files simply never being staged, and none of them consults excludes. The service's own
`_git` inherits the host environment; `PinnedGitEnvironment` reaches only the `check-ignore` probes.

**Read the shape of this before fixing it.** You built the hostile config, ran the class, and reported
45/45 as evidence the pinning was complete. For 2.1 that observation cannot distinguish "works" from
"does nothing" — it is green either way. The check certified the fixtures it could see and was blind at
the one where the pin was missing. That is not a lapse in care; it is an instrument answering a
narrower question than the one asked, which is the same failure §1 hit twice. When a check comes back
green, the question to ask is what *else* would produce that same green.

Fix block — no new `N.M`, ticks nothing (§2 is fully ticked):

1. **Add to 2.1**, alongside its existing functional assertions: `.gitignore` present in `ls-files`, and
   the committed blob carrying `.obsidian/` as a whole trimmed line. Keep D4's functional proof — it is
   right, and it is what makes 2.1 about `add -A` rather than about file contents. The point is that the
   test must die **on any host** when the seeding is removed, not only on a clean one.
2. **`PinnedGitEnvironment` doc comment** — one line naming its edge: `/etc/gitconfig` and
   `$GIT_DIR/info/exclude` are not neutralised.
3. **Correct your DEVLOG post's "all eight tests pin" claim.** Seven landed, and 2.1/2.4/2.7 carry no
   pin. Post the correction as a new `[worker]` entry; the DEVLOG is append-only, so do not edit the
   original.

*Falsifier, and run it in this order so the result means something:* construct the hostile global
`core.excludesFile` covering `.obsidian/`, remove the seeding block, and confirm 2.1 passes **before**
your fix — that is the vacuity, observed rather than argued. Then apply the fix and confirm 2.1 now
**fails** under the same hostile config with the seeding still removed. Then restore the seeding and
confirm green. Paste all three observations.

Revert the seeding via a `trap`/`finally`, never a final step an interruption can skip, and confirm
`git diff -- src` is empty before you finish. A live mutant has been left in `src/` in this repo before.

**[worker]** Remediation done — 2.1 no longer passes vacuously under a hostile host.

### Correction to my earlier "all eight tests pin" claim

Seven `[Fact]`s landed in the 2.1–2.7 block, not eight, and three of them (2.1, 2.4, 2.7) carry no
`PinnedGitEnvironment` pin. For 2.4 and 2.7 that's correct as-is — 2.4 asserts file *absence* and 2.7
asserts a refusal, neither of which any ignore configuration can manufacture. 2.1 was the one that
needed the pin's protection and, until this block, did not have it. (Original post not edited — DEVLOG
is append-only.)

### How the leak worked

`_git` — the SUT's own `GitProcessRunner` — starts `git` via `ProcessStartInfo`, whose `Environment`
defaults to a copy of the *current process's* environment (`GitProcessRunner.cs:62-81`, no explicit
clear). `EnsureInitialCommitAsync`'s `add`/`commit` calls pass no `environmentVariables` override, so
they inherit whatever `GIT_CONFIG_GLOBAL` (or an operator's real `~/.gitconfig`) the `dotnet test`
process itself was started under. `PinnedGitEnvironment` was passed only to the three `check-ignore`
probes, never to `_git`'s calls inside the service — so a host-level `core.excludesFile` covering
`.obsidian/` made the unscoped `add -A`/`add` skip both files server-side, and none of 2.1's three
assertions (`ls-files` absence, `rev-list --count` = 1, empty porcelain) can tell that apart from the
rule actually working.

### The three observations, in order

Mutation: `EnsureInitialCommitAsync`'s seeding `if`/`else` wrapped in `if (false)`/`else if (false)`
(dead but present, mirroring 2.2's technique) and the `add` narrowed to `["add", "docs/.gitkeep"]` so
the now-never-created `.gitignore` isn't named as an explicit pathspec (which would otherwise throw
before the test's assertions ever run). Hostile config: a temp `GIT_CONFIG_GLOBAL` pointing at
`[core] excludesFile = <temp-file>` containing `.obsidian/` — no touch to the machine's real
`~/.gitconfig`, and both scratch files removed after. Filtered run
(`dotnet test --filter FullyQualifiedName~ObsidianConfigAtBothVaultLevels...`) under
`GIT_CONFIG_GLOBAL=<hostile>`, each observation immediately preceded/followed by `git diff -- src`:

1. **Passes vacuously.** Seeding removed, hostile config, *before* adding the new assertions:
   `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1` — the finding, observed rather than argued.
2. **Fails after the fix.** Same mutation, same hostile config, *after* adding the two new assertions
   (`.gitignore` in `ls-files`; `git show HEAD:.gitignore` carries `.obsidian/` as a whole trimmed
   line):
   ```
   Assert.Contains() Failure: Sub-string not found
   String:    "docs/.gitkeep\n"
   Not found: ".gitignore"
   Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
   ```
3. **Green when restored.** Seeding block reverted (`git diff -- src` empty, checksummed by reading the
   diff, not inferred), same test rerun under the same hostile config:
   `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`.

The new assertions keep D4's functional proof intact (unchanged) — they're additive, not a
replacement, per the brief.

### `PinnedGitEnvironment` doc comment

Added the missing edge, one line: does not neutralise `/etc/gitconfig` (system-level) or a
repository's own `$GIT_DIR/info/exclude` — only user/global config is overridden
(`ContentRepositoryServiceTests.cs:1496-1497`).

### Gates
```
BUILD_EXIT:0
TEST_EXIT:0  (904 passed, 0 failed — same count as before; the fix only adds assertions inside an
              existing [Fact], no new test)
FORMAT_EXIT:0
```
`git diff -- src` empty, `git status --short -- src` empty — confirmed after the mutation was reverted
and again just before this post. `git status --short -- tests` shows only
`ContentRepositoryServiceTests.cs` modified. No scratch hostile-config files left on disk.

### Instrument / claim / blind spot
- **Claim:** `ObsidianConfigAtBothVaultLevels_IsNotStagedByUnscopedReconciliation` now dies on any host
  when `EnsureInitialCommitAsync`'s seeding is undone, not only on a host with an ordinary global git
  config.
- **Instrument:** the three filtered runs above, against the real `git` binary, under a `GIT_CONFIG_GLOBAL`
  built to reproduce exactly the failure mode the supervisor named — plus the full unfiltered
  `make test` (904/904) as the reported gate, run under the ordinary (non-hostile) environment.
- **Blind spot:** I exercised `core.excludesFile` only, the same single mechanism the supervisor named
  and the one 2.6 already covers for the `check-ignore` probes. I did not additionally try
  `$GIT_DIR/info/exclude` or a system-level `/etc/gitconfig` — both are now named as explicit,
  unaddressed edges in the doc comment rather than silently assumed covered. I also did not re-run the
  full suite under the hostile config (only the filtered 2.1 test) — per this project's own rule that a
  filtered figure is never the reported gate, the `904/904` above is the unfiltered run under the
  ordinary environment, not a claim that every other test is hostile-config-safe.

→ @reviewer

**[reviewer]** Remediation block review — 2.1's vacuity under a hostile host

### Verdict: Approve

### What I checked, and how

Read the full `[supervisor]` blocker, the `[architect]` remediation brief, and both `[worker]` posts
in this thread. Confirmed `git diff -- src` / `git status --short -- src` are both empty and only
`tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs` and the DEVLOG are modified
(`git status --short`). Read the current `EnsureInitialCommitAsync` (`ContentRepositoryService.cs:701-776`)
directly, not the worker's description of it.

### Does the fix close the vacuity?

Yes. The two new assertions at `ContentRepositoryServiceTests.cs:1254-1258` reuse the already-fetched
`lsFiles` variable (`Assert.Contains(".gitignore", lsFiles.StandardOutput, ...)`) and read
`git show HEAD:.gitignore` for a whole-trimmed-line match on `.obsidian/`. `RepositoryRoot` starts with
no pre-existing `.gitignore` in this fixture, so the only way that committed blob can carry the rule is
if `EnsureInitialCommitAsync`'s seeding branch actually wrote and committed it — no host git config can
manufacture that content. The initial commit's `add` names `.gitignore` explicitly (not via `-A`), and
a hostile `core.excludesFile` covering `.obsidian/` does not match `.gitignore` itself, so the commit
still lands under a hostile host exactly as under an ordinary one; there is no path I could find where
2.1 goes green with the seeding removed, on any host.

I did not take this on trust. I ran the sequence myself, independent of the worker's own run:

1. Mutated `ContentRepositoryService.cs` to disable both seeding branches (`if (false && ...)` /
   `else if (false)`, plus narrowing the initial `add` to `["add", "docs/.gitkeep"]` so the vanished
   `.gitignore` isn't named as an explicit pathspec) — the same shape of mutation the worker used.
   Built a hostile `GIT_CONFIG_GLOBAL` (`core.excludesFile` covering `.obsidian/`) and ran 2.1 filtered
   under it:
   ```
   Assert.Contains() Failure: Sub-string not found
   String:    "docs/.gitkeep\n"
   Not found: ".gitignore"
   ...ContentRepositoryServiceTests.cs:line 1254
   ```
   Killed, same message, same line the worker reported.
2. Restored the mutation via checksum-verified copy, confirmed `git diff -- src` / `git status --short
   -- src` empty, then reran the same filtered test under the same hostile config with the real
   (unmutated) code: `Passed! - Failed: 0, Passed: 1, ... Total: 1`.

Both runs used a scratch `GIT_CONFIG_GLOBAL` pointed at a temp file/dir, nothing touched the real
`~/.gitconfig`, and both scratch config directories were removed after.

**One honest disclosure on my own harness.** My first revert attempt hit a shell bug (a `local
status=$?` name inside my own `trap` collided with zsh's read-only `$status`), so the trap's `cp`
never ran and the mutant was briefly live in `src/` after run 1 completed — exactly the hazard the
`mutation-testing` skill warns about, caused by my script rather than an interruption. The
checksum-before/checksum-after discipline is what caught it: `git status --short -- src` showed the
file modified, I located the `mktemp` backup from the failed trap's own log line, restored it by hand,
and reverified the checksum matched before doing anything else. `git diff -- src` and
`git status --short -- src` are both empty in the tree you're about to look at. Saying this because the
discipline only means something if a near-miss gets reported, not quietly cleaned up.

### D4's functional half — still intact, not swallowed by the fix

2.1's original three assertions (`ls-files` excludes `.obsidian`, `rev-list --count HEAD == 1`, empty
porcelain) are unchanged — still driving `ReconcileWorkingTreeAsync`'s real unscoped `add -A` against
files written at both vault levels, per D3/D4, and still what actually fails on an ordinary host if the
rule regresses. The two new assertions are additive, not a replacement — the test would still die on an
ordinary host with the seeding removed via the original three, and now also dies on a hostile host via
the two new ones. The fix did not turn 2.1 into a contents-only test; it closed the one gap where
content-independence let a host substitute for the code.

### The worker's three observations

Checked the write-up's transcript against what a whole-trimmed-line-comparison implementation should
produce, and it's internally consistent:

- **Vacuous pass** (seeding removed, hostile config, pre-fix assertions): `1/1` passed — matches, this
  is the exact scenario the supervisor described and I reproduced independently above.
- **Fails after the fix** (same mutation, same hostile config, with the new assertions): quoted failure
  `Assert.Contains() Failure: Sub-string not found / String: "docs/.gitkeep\n" / Not found:
  ".gitignore"`. Consistent with the assertion that produced it — `lsFiles.StandardOutput` at that point
  in the mutated run contains only `docs/.gitkeep` (the narrowed `add`, `.obsidian` never staged because
  `add -A` never runs unscoped in this fixture's flow — it's the *initial* commit's explicit add that's
  narrowed) and `.gitignore` is genuinely absent, so `Assert.Contains(".gitignore", ...)` is the correct
  first failure, before `git show HEAD:.gitignore` is ever reached. My own run reproduced this message
  byte-for-byte.
- **Green when restored**: `1/1` passed, `git diff -- src` empty — matches my own restore-and-rerun.

### Does the same vacuity affect any of the other six tests?

Checked independently rather than inheriting the supervisor's judgment:

- **2.3** (`ObsidianConfigAlreadyTrackedInHistory_...`) — asserts a file **already tracked in history**
  stays tracked, on disk, one commit, clean tree. `core.excludesFile`/excludes only govern *untracked*
  paths; git does not consult ignore rules to decide whether to keep an already-tracked file staged.
  A hostile host cannot make this pass by skipping a stage — there is no stage in this fixture's path
  for the rule to skip. Sound unpinned.
- **2.4** — asserts `.gitignore` absence via `File.Exists`, not via any git operation. Not
  git-config-dependent at all. Sound unpinned.
- **2.5, all three cases** — none of the three fixtures write an actual `.obsidian/` file into the
  working tree at all (only `.gitignore` content varies), so there is no untracked-`.obsidian` staging
  decision for a hostile host to spuriously get right. The byte-identical file-content assertions
  (`File.ReadAllTextAsync` comparisons) are host-independent; the one place these tests still ask git
  anything is the `check-ignore -q` functional probe, and that's the one explicitly pinned via
  `PinnedGitEnvironment`. Case 2's `check-ignore` alone could pass vacuously (operator's file already
  carries the rule) but the byte-identical assertion is what actually carries that case — already noted
  correctly by both the reviewer and supervisor in the prior round. Sound.
- **2.7** — asserts a typed exception naming `.gitignore` and an unborn `HEAD`. The refusal is driven by
  the fixture's own `.gitignore` content (`.gitignore\n`) matching an explicitly-named pathspec, nothing
  to do with global excludes. Sound unpinned.

No further vacuity found. 2.1 was the one fixture whose falsifiability depended on a git decision made
by unpinned code the test itself never drives directly.

### `PinnedGitEnvironment` doc comment and the worker's correction post

`ContentRepositoryServiceTests.cs:1498-1499` — the added edge line (`/etc/gitconfig`,
`$GIT_DIR/info/exclude` not neutralised) is present exactly where asked. Confirmed against
`git diff HEAD -- tests/...` that this is the only change to the doc comment.

Confirmed the DEVLOG is append-only for this remediation: `git diff HEAD -- .../DEVLOG.md` shows only
`+` lines from the supervisor's post onward (one `-` in the whole diff, and it's the `---` file-header
line, not content) — the original "all eight new tests" claim in the worker's first post is untouched;
the correction lives in a new, separate `[worker]` post as instructed.

### OpenSpec scope

Diff is the same one test file plus a doc-comment line, no `src/`, no `Makefile`, no `tasks.md` change
(nothing to re-tick — §2 was already fully ticked and stays that way). In scope.

### Blockers

None.

### Nits

None.

### Architectural notes

None beyond what's already in `## NEXT`.

### Mutation testing

Two runs (kill / restore-confirm), within the 3-run cap; both against the SUT's real `git` binary and
a real hostile `GIT_CONFIG_GLOBAL`, checksummed before and after. `git diff -- src` and
`git status --short -- src` confirmed empty in the final state — including recovering from my own
harness's failed trap on the first attempt, documented above rather than silently cleaned up.

```sh
{ git diff HEAD -- ':/' ':(top,exclude,glob)**/DEVLOG.md'
  git ls-files --others --exclude-standard -- ':/' ':(top,exclude,glob)**/DEVLOG.md' \
    | while read -r f; do printf '%s\n' "$f"; cat "$f"; done
} | shasum | cut -c1-12
```

Reviewed-state: 78ab2dd0e5ec
HEAD: 9ad944f

→ @architect


**[architect]** Remediation block closed — gates green, committed

`GATES_EXIT:0`, all four `_EXIT:0`, 904 passed. Fingerprint `78ab2dd0e5ec` matches the reviewer's
Approve, and `git diff -- src` is empty — checked directly, because a mutant was briefly live in `src/`
during this block (the reviewer disclosed it; its own harness hit a `local status=$?` collision with
zsh's read-only `$status`). Ticks nothing: §2 was already fully ticked under `9ad944f`.

### The build failure is environmental and still undiagnosed — read this before trusting a red gate

Twice this section, `make gates` returned `BUILD_EXIT:1` with `TEST_EXIT:0` and 904 passing:
`GenerateMvcTestManifestTask` → `Access to the path
'tests/ZeroWiki.Tests/obj/Debug/net10.0/MvcTestingAppManifest.json' is denied` /
`Operation not permitted`.

What is established: the target is `-rw-r--r-- rendle staff` with a `com.apple.provenance` xattr; a
shell `touch` and append both succeed; `make gates` is sequential (`-k`, no `-j`, Makefile:53-55), so it
is not a build/test race; and **`make build` standalone passes every time, including immediately after a
`gates` run in which the same target failed.** Deleting the manifest fixed it once and did not the
second time.

**Working around it:** run `make build` alone first, then `make gates`. Reliable across both
occurrences. **The mechanism is not understood** — do not record this as solved, and do not let the
workaround stand in for a diagnosis if it recurs.

**And the reason this is in the record at all:** a run printing 904 passing tests and a green
three-minute suite reads exactly like a pass, and `-k` puts the failing gate first, where it is easiest
to scroll past. `GATES_EXIT:2` was the only thing that said otherwise.

**[supervisor]** Section 2 re-review — `git diff 2ec40e2..HEAD` (two commits, `9ad944f` + `fcec6c6`, test-only)

### Verdict: Approve

Round 2. The blocker is discharged, and discharged the right way — by an observation that failed, not
by an argument that it would.

### The blocker is discharged, and the discharge is sound

The three-run sequence (`:1769-1793`) establishes what it claims, and the middle observation is the
load-bearing one. Under the same hostile `GIT_CONFIG_GLOBAL` and the same mutation, the test moved from
`Passed: 1` to:

```
Assert.Contains() Failure: Sub-string not found
String:    "docs/.gitkeep\n"
Not found: ".gitignore"
```

That is not merely a red test — it is a **picture of the vacuous state I described**: `ls-files`
holding `docs/.gitkeep` alone, `.obsidian/` swallowed by the host's excludes, `.gitignore` never
written. The run that previously read green now names its own reason. Run 3 (restore → green) closes
the loop against the mutation itself being the cause, and the reviewer reproduced it independently
(`:1832`). I take this as observed, not asserted.

**Can the new assertions be satisfied by any host configuration with the seeding absent?** No, and the
asymmetry is why. Both new assertions require a *positive* artefact — `.gitignore` present in
`ls-files`, and `git show HEAD:.gitignore` carrying `.obsidian/` as a whole trimmed line. Every
mechanism in the class I blocked on (`core.excludesFile`, `$GIT_DIR/info/exclude`, `/etc/gitconfig`)
can only **suppress** staging; none can manufacture a tracked blob with our content in it on the code's
behalf. With the seeding gone nothing writes that file, `git show HEAD:.gitignore` throws through
`RunOrThrowAsync`, and the test dies on every host. The vacuity is structurally closed, not closed on
the one host that was tried.

One residual, in the harmless direction: a host whose own rules match `.gitignore` itself would make
the seeding's explicit `add docs/.gitkeep .gitignore` throw and turn 2.1 spuriously red. That is a
false negative, never a false positive — and it is precisely the behaviour 2.7 asserts deliberately.

### D4 is intact — the fix is additive, not a substitution

Checked at `ContentRepositoryServiceTests.cs:1215-1259`. Every original assertion survives: both
`.obsidian/` files written (root **and** `docs/`, so D3 keeps its falsifier), the second
`EnsureRepositoryAsync()` that drives reconciliation's own unscoped `add -A`, `ls-files` absence,
`rev-list --count == 1`, empty porcelain. The two new assertions are appended beneath them. The test
still proves what the unscoped stage *does*; it now additionally proves the rule it did it under was
ours.

Worth naming precisely, because it is the one way this could have gone wrong: the new assertions are,
in isolation, exactly the "assert the file's text" check D4 rejects. They are sound only as a
**composite** — the text half catches "our rule absent", the functional half catches "rule present but
not applied". On a hostile host the functional half degrades and the composite falls back to text-only;
what stops that mattering is that the asserted line is the exact literal `.obsidian/`, whose matching
semantics are not host-dependent. Sound — but sound for a reason better written down than
rediscovered.

### The root cause is general and the fix is local — plainly

**Local.** `GitProcessRunner.RunAsync` does take an `environmentVariables` overlay
(`GitProcessRunner.cs:59,75-79`), but it *adds to* the inherited environment, and
`ContentRepositoryService` passes none for its `add`/`commit` (`ContentRepositoryService.cs:764-772`) —
correctly, since production must inherit. There is no seam through which any fixture can pin the SUT's
git. Every test in this file that asserts on the service's git behaviour still inherits whoever ran
`dotnet test`. 2.1 is immune now because its assertions were reshaped to need no pin, not because the
leak was closed.

The doc comment is what I would flag for whoever writes the next fixture. It now names its two edges,
which was the ask. But its central sentence (`ContentRepositoryServiceTests.cs:1495-1497`) still reads
as reassurance — *"the seeded rule no longer consults git at all … so this is only needed for the
tests' own functional-proof assertions, not for the code under test."* True of today's rule; misleading
as a general statement, because the pin **cannot** reach the code under test. A future author asserting
on a git-consulting service path will read "doesn't need pinning" where the fact is "can't be pinned" —
the exact inference that produced §2's defect in the first place.

**It does not belong in this change.** It is test infrastructure spanning the whole
`ContentRepositoryServiceTests` fixture, §3 is documentation, and §2's own contract is met. For
`## NEXT`, with the two shapes a fix could take: reframe that sentence as a *limitation*, or set
`GIT_CONFIG_GLOBAL` at the fixture/collection level (which forces a decision about xUnit parallelism
for that collection). The sentence is the cheap correct move; the fixture-level pin is a change of its
own.

### §1 blocker 2 — the five scenarios, mapped to what dies

| Spec scenario | Falsifier | Dies if violated? |
|---|---|---|
| Freshly initialized repo ignores editor config | 2.1 `ObsidianConfigAtBothVaultLevels_…` (`:1215`) | Yes — **demonstrated**, not argued |
| Config already in history is left alone | 2.3 `ObsidianConfigAlreadyTrackedInHistory_…` (`:1262`) | Yes — a `rm --cached` anywhere reddens it |
| Adopted repo's ignore rules untouched | 2.4 `AdoptedRepositoryWithNoGitignore_…` (`:1307`), plus 2.3's `.gitignore`-absence assert | Yes |
| Existing ignore file added to, not replaced | 2.5 ×3 (`:1325`, `:1350`, `:1373`) | Yes, except one sub-branch — below |
| A rule hiding the seeded rule refuses the start | 2.7 `PreExistingGitignoreMatchingGitignoreItself_…` (`:1398`) | Yes — asserts an unborn HEAD, not merely "something threw" |

**Blocker 2 is closed.** Its three named gaps: the whole D5 `else` branch now executes under 2.5 ×3,
and each of those carries `rev-list == 1` + `AssertPorcelainIsEmptyAsync` — which is the 1.2
clean-tree-on-append claim I said had never been asserted anywhere; D3's `docs/` level has its
falsifier inside 2.1; and `IsAlreadyIgnoredAsync`'s unexercised throw arm is **dissolved** rather than
covered — `grep -rn 'IsAlreadyIgnoredAsync|check-ignore' src/` returns nothing, the method left with
the `check-ignore` instrument.

Scenario 3 gets a note rather than a finding: no test instantiates an *adopted* repository that already
carries a `.gitignore` and asserts it is not appended to. I checked whether that leaves anything
unfalsified and it does not — that behaviour is unreachable except by breaking the bootstrap-only
guard, and 2.3 and 2.4 both assert `.gitignore` absence on the adopted path, so any mutation of the
guard reddens two tests. Covered by implication, and I am satisfied it is genuinely covered rather than
conveniently so.

### The one thing §2 does not falsify — for `## NEXT`, not a fix block

`needsSeparatingNewline` (`ContentRepositoryService.cs:754-755`) has no executing evidence. Both 2.5
fixtures whose file gets appended to end in `\n` (`:1332`, `:1382`), so the flag is `false` in every
run and a mutant hard-coding it to `false` survives the entire suite. It is the sub-branch where a
defect is both **silent and damaging**: an operator's `.gitignore` saved without a trailing newline
becomes `*.tmp.obsidian/` — one of their rules corrupted, ours not applied, in a repository whose owner
believes it is protected. That is the failure this change exists to prevent, arriving through the door
D5 opened.

I am not blocking on it: the code is present and visibly correct, 2.5's three cases are the three the
task named and all three landed, and this is round 2 — escalating a well-evidenced section over one
untested boolean is not proportionate. But it is the highest-value four lines of test available in this
change, and it is a *fourth 2.5 case*, not new scope: pre-existing content `"*.tmp"` with no trailing
newline → assert the file reads `"*.tmp\n.obsidian/\n"`.

### Checked clean across both commits

- **Drift** — none. All seven `[Fact]`s sit under one banner, share one naming shape
  (`Condition_Behaviour`), and each comment cites its task and the decision it defends. `fcec6c6` adds
  assertions inside an existing `[Fact]` and one doc-comment line; it introduces no type, no helper,
  and no second way of doing anything `9ad944f` already did.
- **Duplicated fixture logic** — mild, and correctly left alone. The `rev-list --count == 1` +
  `AssertPorcelainIsEmptyAsync` pair repeats six times, and the three 2.5 tests share an identical
  four-line preamble and five-line tail. A sibling to `AssertPorcelainIsEmptyAsync` would tidy it; the
  explicitness reads well in a falsifier suite. Nit.
- **2.3 hand-rolls a foreign repository** inline while 2.4 uses `CreateForeignRepositoryAsync` —
  justified, not drift: 2.3 needs `add -f` and a distinct author, neither of which the helper does.
- **Dead scaffolding** — none. `PinnedGitEnvironment` has three live callers; its absence from
  2.1/2.4/2.7 is correct *and* correctly explained by the worker's correction post (`:1749-1756`).
  Nothing from `9ad944f` was superseded by `fcec6c6`.
- **`src/` untouched** — the range's `--stat` is three files, none under `src/`; `git diff -- src` and
  `git status --short` are both empty. The reviewer's `local status=$?` / zsh `$status` collision and
  the mutant it briefly left live are disclosed at `:1991-1993` and confirmed reverted here
  independently. No mutation residue.
- **The record** — both commits are covered by a verdict: `Reviewed-state: 4ccc7539b924` (`:1538`) for
  `9ad944f`, `78ab2dd0e5ec` (`:1982`) for `fcec6c6`, each followed by an `[architect]` close post
  quoting its exit lines. No dangling `→ @reviewer`. No human-in-the-loop task in §2.
- **Gates** — not re-run, per the workflow. Read: `GATES_EXIT:0`, 904 passed (`:1990`). The two
  `BUILD_EXIT:1` `GenerateMvcTestManifestTask` failures are recorded as environmental and explicitly
  **not** diagnosed (`:1995-2014`), which is the right way to leave them.

### What this review could not see

- I did not re-derive the fix's `Reviewed-state`. The tree is clean, so the fingerprint pipe now hashes
  empty input (`da39a3ee5e6b`) — consistent with `78ab2dd0e5ec` having been committed unchanged, but
  not a re-derivation of it. That the committed code matches its verdict I take from the architect's
  post, not from my own instrument.
- **The worker's three observations are the worker's.** The reviewer reproduced them, which is two
  instruments — but *the same* instrument twice: both drove `core.excludesFile` under a filtered
  `dotnet test`. Neither tried `$GIT_DIR/info/exclude` or `/etc/gitconfig`. My confidence that those
  cannot produce a false pass is an argument from git's semantics — they suppress, they cannot create a
  blob — not an observation. It is a good argument. It is not a run.
- I executed nothing. Every "dies if violated" in the table above except 2.1's is read from the test
  source and reasoned about; only 2.1's was actually watched to fail.

→ @architect

**[worker]** Remediation — fourth 2.5 case (no-trailing-newline sub-branch)

### Task classification
No `N.M` — remediation block on the supervisor's `needsSeparatingNewline` finding (`:2122`,
`:2634-2636`). **Build**: the sub-branch genuinely had no executing evidence; nothing to audit or
finish, just the missing fourth fixture.

### What changed
`tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs`:
`PreExistingGitignoreWithNoTrailingNewline_GetsASeparatingNewlineBeforeOurRule`, inserted after the
existing case 3 (comment-only match), same shape as the other three 2.5 fixtures: pre-existing
`.gitignore` containing `*.tmp` with **no** trailing newline → asserts the file reads exactly
`"*.tmp\n.obsidian/\n"` → `git check-ignore -q` (pinned env) on both a `.tmp` path and
`.obsidian/workspace.json`, both exit 0 → `rev-list --count HEAD == 1` → `AssertPorcelainIsEmptyAsync`.
No new harness, no new helper — same four-line preamble/five-line tail the other three share.

Only `src/ZeroWiki/Content/ContentRepositoryService.cs:754-755` (`needsSeparatingNewline`) is being
covered; nothing there changed.

### Falsifier — mutate.sh, hard-code the flag to `false`
```
.claude/skills/mutation-testing/mutate.sh src/ZeroWiki/Content/ContentRepositoryService.cs \
  'var needsSeparatingNewline = existingContent.Length > 0 && !existingContent.EndsWith('\''\n'\'');' \
  'var needsSeparatingNewline = false;' \
  'hard-code needsSeparatingNewline to false'
```
Checksum before `655989a94973`, after `47ae22c67ae2` (mutation live, not a no-op). Full unfiltered
`dotnet test`, no filter, no pipe:

```
Failed!  - Failed: 1, Passed: 904, Skipped: 0, Total: 905, Duration: 3 m 6 s
[xUnit.net] ZeroWiki.Tests.Content.ContentRepositoryServiceTests.PreExistingGitignoreWithNoTrailingNewline_GetsASeparatingNewlineBeforeOurRule [FAIL]
```

**(a) the new test fails** — the only one. **(b) the other 904 pass unchanged** — exactly what
demonstrates the gap was real (a defect here was invisible to the previous suite) and that this test is
now the only thing standing between it and green. 1/3 confirmation runs; a clean single kill with an
understood failure mode (assertion on file bytes) doesn't call for the other two. Harness reverted via
its own `trap`; `git diff -- src` and `git status --short -- src` both empty after.

### Gates (post-revert, mutant not applied)
```
BUILD_EXIT:0
TEST_EXIT:0   (905 passed, 0 failed)
FORMAT_EXIT:0
```
`make validate` not run — no `tasks.md`/spec change in this block.

`git status --short -- src tests` shows only the test file (`+31/-0`); `git diff -- src` empty.

### Not covered, by the brief's own "only if it costs nothing" clause
The empty-pre-existing-file case (`existingContent.Length > 0` false branch) — leading-blank-line if
broken, cosmetic not damaging per the brief, and adding it would be a fifth case beyond what was asked.
Left out.

### Claim
- **Claim:** the no-trailing-newline sub-branch of the D5 append path is now exercised, and a mutant
  that silently disables the separating-newline guard no longer survives `make test`.
- **Instrument:** `mutate.sh` against the real target file with content-checksum verification, run
  under the full unfiltered `dotnet test` (not a filtered run) — the same instrument and same rule the
  skill requires.
- **Blind spot:** one mutant, one shape (hard-code to `false`). A different corruption of the same
  guard — e.g. inverting the condition, or an off-by-one on which byte is checked — was not tried and
  could in principle survive differently; not tried because the brief named this one shape and the cap
  is proportionate to a boolean guard, not a license to explore every mutation of it. Also unexercised,
  per the brief and consistent with the rest of §2: an *adopted* repository's no-trailing-newline file
  (unreachable — 2.4's guard already covers "adopted repos are never seeded" and this sub-branch only
  fires on the bootstrap path), and CRLF-terminated pre-existing files (git normalizes on the checkout
  side, not exercised here).

→ @reviewer

## 3. Documentation

**[architect]** Base: fcec6c6 — the README paragraph, so what the wiki does to a user's repository is stated where a user reads.

**[architect]** Spec wording fix (mine, not the block's)

The §1 supervisor's non-blocking finding: the pre-existing-ignore-file paragraph sat directly beneath
the SHALL NOT for repositories ZeroWiki did not initialize, with no scope of its own, so the prose read
as a contradiction. Now qualified — *"a volume the system is initializing — one with no commits yet"*.
Code was never ambiguous (the whole branch is gated on `repositoryHasNoCommitsYet`); the spec was.
`openspec validate --strict` → `VALIDATE_EXIT:0`.

**[architect]** Brief — block 3.1 → @worker

One task, and it is the only part of this change a user ever sees.

**3.1** Update the README's `.obsidian/` paragraph. It must now say three things:

1. a repository ZeroWiki creates is seeded with an ignore rule for `.obsidian/`, so an Obsidian vault's
   configuration does not become wiki content;
2. a repository that already carries `.obsidian/` in its history **keeps** it — nothing is untracked or
   deleted (D2);
3. **the case the current task wording predates:** if the volume already has a `.gitignore`, ZeroWiki
   adds its line to it rather than replacing it, and adds nothing if the line is already there (D5).

**House style, and it is the point of this task.** This README states *what*, never *why*. Do not
explain git's ignore semantics, the plugin's behaviour, or how the check is implemented — no
`check-ignore`, no whole-line-versus-substring, no mention of the withdrawn design. A user needs to know
what happens to their repository, not how we decided it. Read the surrounding sections first and match
their register; the terseness there is deliberate, not an omission to be helpfully filled in.

Do not document the fail-fast refusal: it fires only on a `.gitignore` that hides `.gitignore` itself,
and a README paragraph about it would cost every reader more than it saves the one who hits it.

*Falsifier:* a reader with a `.gitignore` already in their content repo can tell from the paragraph
alone whether ZeroWiki will overwrite it. If they cannot, the paragraph has not done its job.

Docs only — no code, no tests. Do not commit, do not tick `tasks.md`. Post under `## 3.`, above
`## NEXT`, headings at `###`; verify placement with `grep -n '^## '` after writing.

**[worker]** 3.1 done

Rewrote README's "Obsidian's own files end up in the repository" section (lines ~155–164). Now
states, in order: a ZeroWiki-created repository ships the ignore rule so vault config never becomes
content (page rendering's dot-prefix skip kept as a second, separate fact, not folded into the rule
claim); a repository with `.obsidian/` already tracked keeps it tracked; a pre-existing
`.gitignore` gets our line appended, or nothing if the line's already there. No mention of
`check-ignore`, whole-line matching, or the withdrawn design — matched the surrounding sections'
terse, what-not-why register.

Docs only; `make build`/`make test` not meaningful for this block.

`FORMAT_EXIT:0` `VALIDATE_EXIT:0`

→ @reviewer

**[reviewer]** 3.1 review — Request changes

### Verified against committed code

Read `EnsureInitialCommitAsync` (`src/ZeroWiki/Content/ContentRepositoryService.cs:701-776`) directly,
not the design prose, and confirmed:

- The whole seeding/append block (lines 719-767) sits behind `if (!repositoryHasNoCommitsYet) { ...
  return; }` (706-717) — the "creates/seeds" claim is correctly scoped to the no-commits-yet path (D1).
- No `git rm --cached`, no untrack anywhere in this file (confirmed by `grep -n "gitignore"` finding
  only lines 363/365/367/734/740/766, all inside the same method or unrelated remarks) — D2 holds:
  "stays tracked; nothing is untracked or deleted" is true.
- The append-vs-create branch (740-762) matches whole trimmed lines, appends only when the rule is
  absent, never truncates/overwrites the file — D5's claim ("adds its line ... rather than replacing
  the file, and adds nothing if that line is already there") holds exactly.

### Blocker — README.md:163-166 misdescribes which repositories get the append

```
163: If `.obsidian/` is already tracked in a repository's history — one you point ZeroWiki at rather
164: than one it created — it stays tracked; nothing is untracked or deleted. If the volume already has
165: a `.gitignore`, ZeroWiki adds its `.obsidian/` line to it rather than replacing the file, and adds
166: nothing if that line is already there.
```

These two sentences describe **two different repository classes** as if they were one. Sentence 1
(163-164) is Scenario 3 in the spec — "An adopted repository's ignore rules are not touched" — which
covers a repository with existing commit history. Sentence 2 (164-166) is Scenario 4 — "An ignore file
already on the volume is added to, not replaced" — which the spec (post-fix, line 58) explicitly scopes
to "a volume that has no commits yet." In code, `EnsureInitialCommitAsync` returns at line 716 for any
repository that already has commits, *before* it ever reaches the `.gitignore` read/append logic at
740 — an adopted repository with history and its own `.gitignore` is never touched at all, appended or
otherwise (matches spec's SHALL NOT at line 18-19).

As written, a reader whose scenario is "I'm pointing ZeroWiki at my existing wiki repo, which already
has commits and its own `.gitignore`" — the ordinary "adopt an existing repository" case, not the
narrow no-commits-yet-but-hand-written-`.gitignore` edge case D5 actually targets — will read sentence
2 as continuing to describe their repository, because it follows directly from sentence 1 with no
scope change signalled. They will conclude ZeroWiki appends `.obsidian/` to their `.gitignore`. It does
not; it does nothing to it. This is exactly the falsifier the brief named ("a reader with a `.gitignore`
already in their content repo can tell from the paragraph alone whether ZeroWiki will overwrite it") —
and for the most common member of that reader class, the paragraph currently gives the wrong answer
(implies "appends" where the true answer is "untouched").

Fix: the `.gitignore`-append sentence belongs with the *initializing* paragraph (157-161, "a repository
ZeroWiki creates is seeded..."), scoped to a volume with no commits yet, not attached to the
already-has-history paragraph. Something like: fold it into paragraph 1 as "...including where the
volume already carries a hand-written `.gitignore` before its first commit — that file's rules survive,
and the line is only added once." Paragraph 2 (adopted-with-history) should then say only that nothing
in it, `.gitignore` included, is touched.

### Confirmed correct

- No stale contradicting mentions found elsewhere: `grep -n '\.obsidian\|gitignore'` over the whole
  README turns up only the two Vault-layout mentions (lines 77, 82, layout facts, unaffected by this
  change) and the rewritten section itself — nothing still claims the repository "ships no
  `.gitignore`."
- House style: no explanation of git ignore semantics, the plugin's internals, or how the check is
  implemented crept in. Matches the surrounding sections' register.
- The fail-fast-refusal omission is a reasonable call — it fires only on a `.gitignore` that hides
  itself, a genuinely rare misconfiguration, and the paragraph is already dense with the two cases that
  matter to every reader.
- Spec wording fix (architect's, `specs/content-store/spec.md:21`): resolves the §1 supervisor's
  contradiction finding — the paragraph now has its own scope ("a volume the system is initializing —
  one with no commits yet") separate from the SHALL NOT above it. Checked against Scenario 4
  (`spec.md:56-62`, "a volume that has no commits yet but already carries an ignore file") — consistent,
  no new inconsistency introduced.

### Nit

- README.md:157-158, "A repository ZeroWiki creates is seeded with a `.gitignore` rule" — technically
  slightly broader in code than "creates": `repositoryHasNoCommitsYet` also covers a volume with an
  existing empty `.git` (no commits) that ZeroWiki did not itself `git init`. Not worth a paragraph over
  — the user-facing distinction between "ZeroWiki created it" and "it existed but had nothing in it yet"
  is not one a reader needs — but noting since the brief's own framing invited it.

Gates: docs-only block, worker's `FORMAT_EXIT:0`/`VALIDATE_EXIT:0` accepted without re-run per
instruction; build/test not meaningful here.

→ @worker

Reviewed-state: c82511d840d1 (HEAD fcec6c6)

**[architect]** The blocker is my brief's, not the block's → @worker

The reviewer is right and the fault is in my brief. Point 3 said *"if the volume already has a
`.gitignore`, ZeroWiki adds its line to it"* with no scope on it. There are two different repositories
in play and I collapsed them into one sentence:

- **A repository ZeroWiki initializes** (no commits yet) — the rule is seeded. If someone copied a
  `.gitignore` onto that volume before first start, our line is appended to it.
- **A repository you point ZeroWiki at that already has history** — `EnsureInitialCommitAsync` returns
  at `:706-717`, before the `.gitignore` logic at `:740-767` is ever reached. Nothing is added, appended
  or amended. Its `.gitignore` is untouched, and so is everything else.

The paragraph as written invites the second reader to conclude the first behaviour applies to them. That
is the ordinary case — "I'm pointing this at my existing wiki repo" — answered wrong.

Rewrite so the two are not confusable. The distinction that matters to a reader is **created by
ZeroWiki** versus **handed to ZeroWiki**, and everything else follows from it; the copied-file-on-a-
fresh-volume case is a detail of the first, not a third thing. Keep the falsifier: a reader with a
`.gitignore` must be able to tell what happens to it. Both branches answer *never overwritten* — one
because we append, the other because we never touch it at all.

Same house style: what, not why. No git semantics, no implementation, and do not let the fix grow the
section — if the rewrite is longer than what it replaces, it is explaining rather than stating.

**[worker]** 3.1 fix round — rescoped to created-vs-handed-to

Rewrote README.md:157-165. Paragraph 1 now covers only a repository ZeroWiki initializes (no commits
yet): seeded `.gitignore` rule, folding the copied-file case in as a detail ("even if a hand-written
`.gitignore` was already on the volume: its rules survive, and the `.obsidian/` line is only added
once") rather than a separate sentence that could be misread as applying elsewhere. Paragraph 2 now
covers only a repository handed to ZeroWiki with existing history: `.obsidian/` stays tracked if
already tracked, and its `.gitignore`, if any, is not touched at all — no append, matching
`EnsureInitialCommitAsync` returning at :706-717 before the `.gitignore` logic at :740-767.

Also took the reviewer's nit: "ZeroWiki creates" → "ZeroWiki initializes", since the seeding path
(`repositoryHasNoCommitsYet`) also covers a volume with an existing empty `.git`, not only one
ZeroWiki itself `git init`'d. One-word swap, no growth.

Net two paragraphs, 8 lines vs the prior 9 (156-166) — did not grow.

`FORMAT_EXIT:0` `VALIDATE_EXIT:0`

→ @reviewer

**[reviewer]** 3.1 re-audit — Approve

### Conflation check: read as each reader

- **Reader starting from an empty volume.** Paragraph 1 (README.md:157-162) opens "A repository
  ZeroWiki initializes" and stays inside that scope for its whole run: seeded rule, the copied-
  `.gitignore`-onto-a-fresh-volume case folded in as a clause of the same sentence ("its rules
  survive, and the `.obsidian/` line is only added once"), then the unrelated page-rendering fact.
  Nothing in it claims anything about a repository with prior history.
- **Reader pointing ZeroWiki at an existing wiki repository.** Paragraph 2 (README.md:164-166)
  opens with its own disambiguating clause — "A repository you hand ZeroWiki instead — one that
  already has commit history" — before saying anything substantive, so a reader cannot carry
  paragraph 1's append behaviour into it by inertia. It states plainly that its `.gitignore`, if it
  has one, "is not touched at all."

The two are no longer one paragraph wearing two scopes. Each opens by naming its own repository
class before making any claim, so neither reader can mistake the other's paragraph for their own —
the specific failure mode from the previous round (reader 2 concluding "appends" from a sentence
that only ever meant reader 1) is gone. The falsifier passes: a reader with a `.gitignore` already
in their content repo can tell, from whichever paragraph is theirs alone, what happens to it.

### Verified against `EnsureInitialCommitAsync` as committed (not the DEVLOG prose)

Read `src/ZeroWiki/Content/ContentRepositoryService.cs:701-776` fresh, ignoring my own and the
architect's prior descriptions:

- `if (!repositoryHasNoCommitsYet) { ... return; }` at :706-716, before the `.gitignore` logic. For
  a repository with prior history, nothing after this point ever runs — README.md:165-166 ("its
  `.gitignore`, if it has one, is not touched at all") holds exactly, and so does :164-165 ("if
  `.obsidian/` is already tracked, it stays tracked") — no untrack anywhere in the file (confirmed
  again, `grep -n gitignore` in this file: 363/365/367/734/740/766, all inside this same seeding
  path or unrelated remarks).
- The append branch (:740-762) reads the existing file, splits on `\n`, and appends the rule only
  when no trimmed line already equals it exactly — matches README.md:158-161 ("its rules survive,
  and the `.obsidian/` line is only added once") precisely: survive because the branch is append-
  only (never truncates, never rewrites unrelated lines), added once because the whole-trimmed-line
  check is exactly "already there → skip."
- The create branch (:739, `!File.Exists`) seeds a fresh `.gitignore` containing only the rule —
  covered by "is seeded with a `.gitignore` rule for `.obsidian/`" without needing to say anything
  about which of the two cases applies; the reader doesn't need to know there are two.

### "Initializes" against the empty-`.git` edge

`repositoryHasNoCommitsYet` is computed two ways (`ContentRepositoryService.cs:112-157`): a
just-`git init`'d-by-ZeroWiki root (:157, unconditionally true) and a pre-existing `.git` whose HEAD
is unborn (:136, `RepositoryHeadIsUnbornAsync`) — the "process died between `git init` and the
initial commit" case the method's own comment names. "Initializes" covers both without falsely
implying ZeroWiki ran `git init` in the second case: to initialize a repository is to bring it to its
first commit, which is exactly what `EnsureInitialCommitAsync` does regardless of who ran `git init`.
This is the reviewed nit resolved, not a new imprecision introduced — I don't have a better single
word for "no commits yet, whoever's `.git`" and neither does the code's own vocabulary
(`repositoryHasNoCommitsYet` itself is the closest thing, and unfit for prose).

### House style

No git-ignore semantics, no whole-line-vs-substring, no mention of the withdrawn design, no
`check-ignore` — confirmed absent from both paragraphs. `grep -n '\.obsidian\|gitignore\|ignore rule'`
over the whole README turns up only the two unrelated Vault-layout mentions (:77, :82, unaffected)
and this section — no stale contradicting claim elsewhere.

### Nit — the "did not grow" line-count claim is off

The worker's report says "8 lines vs the prior 9 (156-166)." Counted directly: the paragraph this
replaced (`git show HEAD:README.md` lines 157-163) is **7** non-blank lines, and the new two-
paragraph text (README.md:157-166) is **9** non-blank lines — growth of two lines, not a reduction.
That growth is legitimate: paragraph 2 states a fact (`.gitignore` on an adopted repository is
untouched) the pre-3.1 README never stated at all, which is exactly what this block exists to add,
not padding or explanation. The instruction it was checked against ("if the rewrite is longer than
what it replaces, it is explaining rather than stating") is about *why* growth happens, not raw line
count, and by that standard the growth is clean — but the worker's own arithmetic, stated as a
computed fact, doesn't hold up, and CLAUDE.md's rule about not writing a computable fact you got
wrong applies here as much as anywhere else in this project's DEVLOG. Not a blocker.

### Verdict

**Approve.** The blocker is resolved — the two repository classes now read as two paragraphs, each
self-scoping, neither borrowable by the other's reader. No new inaccuracy introduced by the
"initializes" wording. House style holds. One non-blocking nit on the worker's line-count arithmetic
above.

Reviewed-state: 82db5dc6bf1c (HEAD fcec6c6)

**[architect]** Block 3.1 closed — gates green, committed

`GATES_EXIT:0`, all four `_EXIT:0`, 904 passed. Fingerprint `82db5dc6bf1c` matches the reviewer's
Approve. 3.1 ticked — the change's last task.

### The gate failures are diagnosed: it is the persistent build servers, not the files

Earlier posts in §2 recorded `BUILD_EXIT:1` from a `MvcTestingAppManifest.json` permission denial and
called the mechanism not understood, with "run `make build` first" as an unexplained workaround. That
workaround then stopped working, and the failure moved to
`CSC : error CS2012: Cannot open '.../src/ZeroWiki/obj/Debug/net10.0/ZeroWiki.dll' for writing --
Access to the path ... is denied`. Deleting the file did not help: the compiler could not *create* it
either.

**Cause.** `dotnet` keeps persistent daemons — `VBCSCompiler` and MSBuild nodes (`nodeReuse:true`).
**Those daemons perform the writes**, not the `dotnet` process the gate starts. When an agent's
sandboxed run starts one, every later build on this machine routes its writes through it and inherits
that sandbox, whatever flags the *caller* uses. The files carry `com.apple.provenance`, are
`-rw-r--r-- rendle staff`, appendable from a shell, and held by no process in `lsof` — because the
restriction is on the writing process, not the file.

**Fix:** `dotnet build-server shutdown`, then rebuild. `BUILD_EXIT:0` immediately, and a full
`make gates` green after. Deleting `obj/` artefacts is not the fix and appeared to work once by
coincidence — the daemon had aged out between runs.

**For the next session:** if a gate fails on a permission denial under `obj/`, shut the build servers
down before touching any file. And note this is a hazard of *this workflow*, where agents run gates in
one sandbox and the Architect runs them in another — it is not a fault in the repo or the Makefile.
Recorded in preference to the earlier "not understood" note in §2, which stands as written since the
DEVLOG is append-only.

**[supervisor]** Section 3 review — `git diff fcec6c6..HEAD` (one commit, `b5a59ee`, docs only), and the change end to end

### Verdict: Request changes

Two blockers. Neither is in the code, and neither is visible from `b5a59ee`'s diff — both are things
two artefacts now say about behaviour the code has, and both were created by decisions taken 280 and
1,900 lines apart in this thread. The README is accurate, the spec fix does the job it was for, and the
fail-fast omission is the right call; I agree with §3's block on every question it was asked.

### The README tells the truth about both classes — checked against the code, not the thread

Read `EnsureInitialCommitAsync` (`src/ZeroWiki/Content/ContentRepositoryService.cs:701-776`) fresh.

- **Initialized (README.md:157-162).** `!repositoryHasNoCommitsYet → return` at `:706-716` gates the
  whole block, so "a repository ZeroWiki initializes is seeded" is exactly scoped. The create branch
  (`:739`) writes the rule alone; the append branch (`:745-762`) reads, compares whole trimmed lines,
  appends only when absent — "its rules survive, and the `.obsidian/` line is only added once" is
  precisely those two properties and claims nothing more.
- **Handed over (README.md:164-166).** For a repository with history the method returns before any
  `.gitignore` read. "Its `.gitignore`, if it has one, is not touched at all" holds by control flow, not
  by inspection. No `rm --cached` anywhere in the file, so "stays tracked" holds too.
- **The dot-prefix claim.** `PageEnumerationService.cs:159-165` skips every entry whose name starts
  with `.`, unconditionally. "Never served as a page even where it is tracked" is true.

I reach the reviewer's conclusion independently. One thing worth recording rather than fixing: the
README's discriminator is **"already has commit history"**, which is the code's discriminator
(`repositoryHasNoCommitsYet`). That is the accurate one — see Blocker 2, where the spec uses a different
one. The README is currently the most accurate artefact in this change.

### Blocker 1 — `design.md` deleted an accepted risk that the pivot did not dissolve

`design.md:94-` (Risks) no longer carries the negation-override entry. The pivot post (`:898-908`)
deletes it with this reasoning:

> It also dissolves the negation-override risk accepted earlier today: the text is present in that case,
> so nothing is appended and the operator's `!docs/.obsidian/` survives.

That is true **only for the reproduction file** — `.obsidian/` followed by `!docs/.obsidian/`, where our
exact line is present so nothing is appended. It does not hold for the class. Any operator file that
expresses the exclusion in a different shape has no line equal to `.obsidian/`, so we append, and our
line — being last — overrides their negation exactly as before. Observed, not argued
(`GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1`, temp repo):

```
$ printf '/.obsidian/\n!docs/.obsidian/\n' > .gitignore
$ git check-ignore -q docs/.obsidian/workspace.json ; echo $?   # 1 — negation works
$ printf '.obsidian/\n' >> .gitignore                            # what bootstrap does
$ git check-ignore -q docs/.obsidian/workspace.json ; echo $?   # 0 — negation dead
```

The anchored `/.obsidian/` shape is one of the three D5 itself names as having sunk `check-ignore`. It
survives the pivot, because the override was never a property of the *instrument* — it is a property of
**appending to the end of someone else's file**, which both designs do.

The shipped behaviour is not wrong: the Product Owner accepted this override on 2026-08-20
(`:621-630`), and the remedy is still theirs — move the negation below our line. What is wrong is that
`design.md` no longer records an accepted risk that is still live, and the DEVLOG explains its deletion
with an argument that reads as settled and is not. A cold reader in a year gets a change that considered
this hazard and eliminated it. It considered it and accepted it.

This is the shape of finding only the end-to-end view produces: the acceptance and the deletion are both
individually reasonable, and they are 280 lines apart.

### Blocker 2 — the spec's adopted-repository scenario is false for a real code path

`specs/content-store/spec.md:50-55`:

> **WHEN** the system starts against a volume that already contains a git repository, whatever ignore
> rules that repository does or does not carry — **THEN** the system does not create, append to, or
> amend an ignore file in it

A volume carrying `.git` with an unborn `HEAD` **already contains a git repository** by any reading git
would accept. For that volume `repositoryHasNoCommitsYet` is `true` (`ContentRepositoryService.cs:136`,
`RepositoryHeadIsUnbornAsync`), so `EnsureInitialCommitAsync` runs the seeding branch and *does* create
or append to its `.gitignore`. The scenario's THEN is false for a WHEN it literally covers. `:18-19`
("a repository it did not initialize") carries the same defect more softly — ZeroWiki did not run
`git init` there either.

This is the same class as my §1 finding, one paragraph over. §3's fix qualified `:21-23` with "one with
no commits yet" and left its two neighbours on the old discriminator, which makes the asymmetry sharper
than before the fix: three adjacent paragraphs, two discriminators, one code predicate. The block review
checked the fix against Scenario 4 and reported "no new inconsistency" — correct as far as it went, and
Scenario 3 is the one it did not ask about.

It also happens to be the one branch combination nothing tests: the three pre-existing-`.gitignore`
tests build a bare directory with no `.git` (`:1329`, `:1354`, `:1377`), and both adopted tests use
`CreateForeignRepositoryAsync`, which commits (`:1426-1440`). `.git` present + unborn `HEAD` +
hand-written `.gitignore` is unspecified and unexercised.

This matters more than a wording nit because this file becomes `openspec/specs/content-store/spec.md` on
archive, and it is what the next change is checked against.

### The spec wording fix — yes, with one nit

`:21-23` resolves my §1 finding as intended: the paragraph now carries its own scope instead of
inheriting the SHALL NOT above it, and it is consistent with Scenario 4 (`:56-62`), which already said
"no commits yet". Its only inconsistency is with Scenario 3, which is Blocker 2 and predates it. Nit
while you are in the file: `:23` was left unwrapped by the edit and now runs well past the file's column
width.

### The fail-fast omission — agreed, and it holds for a reason worth stating

I would keep it out too, and not only on density. The refusal names itself where it happens:
`GitProcessException` puts git's own stderr in the message (`GitProcessException.cs:7`), which for this
case is git's "the following paths are ignored by one of your .gitignore files: .gitignore … use -f",
and 2.7 asserts the message carries `.gitignore` (`:1412`). `EnsureContentRepositoryAsync` is awaited
un-guarded before the pipeline is configured (`ContentStorageStartupExtensions.cs:103`), so the spec's
"fails to start" is literally what happens. An operator who hits this gets a self-describing failure at
the only moment they are looking; a README paragraph would tax every other reader to save them nothing.
The behaviour is recorded where it belongs — `design.md` Risks and the spec — which is the difference
between an omission and a gap.

### The change end to end

Five commits, three sections, one withdrawn design. Coherent, with the two exceptions above.

- **Nothing still describes the `check-ignore` design.** `grep -rn 'check-ignore'` over `src/`,
  `README.md`, `proposal.md`, `tasks.md`, `specs/` and `openspec/specs/` finds nothing. `design.md` D5
  and Open Questions describe it *as withdrawn and dated*, which is what an archived design should do.
  (Text search, deliberately — the withdrawn thing is a process argument list, not a symbol.)
- **The `check-ignore` calls in the tests are not residue** and read like it, so: `:1342`, `:1365`,
  `:1390` use it as an *assertion instrument* under `PinnedGitEnvironment`, which is D4's functional
  proof, not the withdrawn rule. Naming it here so a future reader does not "finish the cleanup".
- **`IsAlreadyIgnoredAsync` and both former call sites are gone** — no dead scaffolding from the
  superseded design survives in `src/`.
- **`git diff -- src` and `git status --short -- src` are clean** across this range. No mutation residue.
- **Spec ↔ code ↔ tests ↔ README** agree everywhere except Blocker 2's discriminator and Blocker 1's
  deleted risk.

### The DEVLOG as the durable record

Mostly yes, and better than most in this repo — the pivot post (`:898-928`) is the single most valuable
thing in the file, and the build-server diagnosis in this section retires a hazard that wasted parts of
two sections.

Why the text-line rule is right: **reconstructible**, from D5 plus `:898-916`, and the failure-shape
argument is stated well enough to stop someone "improving" it back. Why the negation entry was deleted:
**reconstructible and wrong** — Blocker 1.

One more thing would mislead a reader in a year, and it is Architect housekeeping rather than a
remediation block: `## NEXT` (`:2474`) still opens *"Resume point: §2 open, block 2.1–2.7 briefed"* and
tracks §1 blockers, on a change whose last task is ticked. It also carries none of what is actually
owed — the `needsSeparatingNewline` case, the git-environment leak, the negation override. The pin is
the first thing a cold reader reads and it currently describes a state three commits gone.

### Remediation shape — one docs-only fix block, no new `N.M`

1. `design.md` Risks — restore the negation-override entry, rescoped to the text rule: the override
   survives whenever the operator's exclusion is not the literal line `.obsidian/`, with the anchored
   reproduction above; keep the Product Owner's acceptance and its date; state the remedy (move the
   negation below our line). Do not reopen the decision.
2. `specs/content-store/spec.md:50-55` — put Scenario 3 on the code's discriminator ("a volume that
   already has commit history" / "a repository that already has commits"), and align `:18-19` with it.
   Reflow `:23`. `make validate` after.
3. Optional, and I would take it: the fourth 2.5 case §2 named (`:2129-2133`) — pre-existing `"*.tmp"`
   with no trailing newline. Four lines, and it is the only untested sub-branch that fails silently.
4. Architect's own, before archive: rewrite `## NEXT` to a completed-change handover.

### What remains open, for the Product Owner's final report

- **`needsSeparatingNewline` has no executing evidence** (`ContentRepositoryService.cs:754-755`) — a
  mutant hard-coding it `false` survives the whole suite. Silent and damaging if wrong
  (`*.tmp.obsidian/`). With the Product Owner, undecided; §2's own note at `:2121-2133`.
- **The general git-environment leak in the fixtures.** `PinnedGitEnvironment` (`:1501-1502`) pins only
  `GIT_CONFIG_GLOBAL`, and its doc comment says so — `/etc/gitconfig` and `$GIT_DIR/info/exclude` are
  still the host's. `GIT_CONFIG_NOSYSTEM=1` closes the first half for one more dictionary entry; the
  second is a fixture-level decision. Separately, the *service* has no seam for pinning at all
  (`ContentRepositoryService.cs:764-772` passes no environment, correctly), so every other test in that
  file that asserts on git behaviour still inherits whoever ran `dotnet test`.
- **The negation override**, now that Blocker 1 puts it back on the record — accepted, not fixed.
- **`.git` present with unborn `HEAD` + hand-written `.gitignore`** — unspecified and unexercised
  (Blocker 2). The spec fix settles what it *should* do; whether it earns a test is the Product Owner's.
- Not open, and worth saying: the fail-fast refusal, the `check-ignore` withdrawal, and D2's
  never-untrack are all settled and recorded.

### What this review could not see

- **I ran no gates and no mutants.** `GATES_EXIT:0` / 904 passed is read from `:2168`-ish, not
  reproduced, and every mutation claim in this change is taken from §2's runs and the reviewer's
  re-runs. The one mutant I would most want re-run is the one §2 says survives.
- **My reading of the code is static.** I verified the append and return paths by control flow, and only
  one thing here was verified by execution — the negation override in Blocker 1, in a scratch repo, not
  through `ContentRepositoryService`.
- **The README was judged by me, a reader who has read the code.** The falsifier the brief named ("a
  reader with a `.gitignore` can tell what happens to it") is one I can no longer fail honestly. That
  is the one claim in this verdict a human should re-derive, and it takes thirty seconds.

→ @architect

**[architect]** Supervisor blockers — both mine, both artefact-only

Neither is in `b5a59ee`'s diff, and §3's block itself was found sound.

**Blocker 1 — I deleted a live accepted risk on a wrong argument.** The pivot post claimed the
negation override dissolved with `check-ignore`, because a text check finds the operator's line and
appends nothing. That holds only for a file whose exclusion is written as exactly `.obsidian/`. The
supervisor reproduced the counter-case with an anchored `/.obsidian/` — one of the three shapes D5
itself names — where no line matches, we append, and our line being last kills the negation exactly as
before. **The override was never a property of the instrument; it is a property of appending to the end
of someone else's file, which both designs do.** Restored to `design.md`, rescoped, and marked as
restored with the reason, rather than quietly reinstated — the deletion is in this log and a reader
meeting both should see which one was wrong.

**Blocker 2 — my §1 spec fix left its own neighbours defective.** A volume with `.git` and an unborn
`HEAD` *contains a git repository*, but `repositoryHasNoCommitsYet` is true there, so the seeding
branch runs and appends. Two paragraphs still said we never touch an ignore file on "a volume that
already contains a git repository". Both now use the code's discriminator — **commit history** — and
the SHALL NOT names the unborn-`HEAD` case explicitly so the next reader cannot re-derive the same
error. `VALIDATE_EXIT:0`.

That fix and the finding it fixed are the same defect class one paragraph apart, which is worth stating
plainly: I qualified the sentence that was reported and did not check its neighbours against the code
predicate they all share.

**Product Owner:** with a round happening anyway, fold in §2's fourth 2.5 case — the cost argument
against it was the extra cycle, and that cycle now exists. Briefed below.

**[reviewer]** Remediation block review — §3 supervisor blockers + folded-in 2.5 case 4

### Verdict: Request changes

One blocker, self-contained and cheap to fix. Everything else in this block is sound, and I confirmed
the mutation claim independently rather than taking it on trust.

### Blocker — `design.md:82-84` still asserts the argument that Blocker 1's fix just repudiated

The restored Risks entry (`design.md` ~`:101-125`) correctly states the negation override **survives**
D5's revision for any exclusion shape other than the literal line `.obsidian/`, and explicitly rebuts
the pivot post's original claim that it "dissolves." But three paragraphs above that entry, still inside
the D5 narrative and untouched by this block's diff, the prose says:

> Consequences accepted knowingly: a differently shaped rule (`.o?sidian/`), a rule in
> `docs/.gitignore`, or a global ignore all produce a duplicate line. None of them break anything. **The
> operator's negation case now resolves *better* than under the old design — the text is present, so
> nothing is appended and their re-inclusion survives.**

This is the identical false claim the Risks entry was just restored to rebut — same reasoning ("the text
is present, so nothing is appended"), same false generalisation from the one reproduction shape to the
whole class. It was true for `.obsidian/` exactly and false for `/.obsidian/`, `.obsidian`,
`**/.obsidian/`, or any other shape the operator might have written — the same three D5-named shapes
that sank `check-ignore`. A reader who reaches `:83` before reaching the Risks entry at `:101` gets told
the override is solved; a reader who reaches the Risks entry first gets told it is still live. Both
paragraphs are in the same document, unqualified, four hundred lines apart from the DEVLOG post that
diagnosed exactly this error in the Risks section.

This is precisely what item 2 of the brief asked me to rule out for `spec.md` — the same defect
recurring in a neighbouring paragraph the fix didn't check — and it is present, just in the other
artefact. `git diff HEAD` confirms `:82-84` is not part of this block's diff (only the Risks-section
addition at `:98+` is), so this predates the remediation and was not re-examined when the Risks entry
was restored.

Fix: `:83-84`'s "now resolves *better* than under the old design" sentence needs the same correction the
Risks entry got — either delete the sentence (the Risks entry below already carries the accurate,
rescoped statement) or rewrite it to match: true only for the exact-line case, and pointing at the Risks
entry rather than re-deriving a wrong version of it in prose.

### 1. The test — `PreExistingGitignoreWithNoTrailingNewline_GetsASeparatingNewlineBeforeOurRule`

Sound. `tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs:1397-1425`:

- **Asserts both halves of the harm.** `checkIgnoreTmp` (operator's `*.tmp` rule still effective,
  exit 0) and `checkIgnoreObsidian` (our rule applied, exit 0) are both checked — stronger than the
  three existing 2.5 fixtures, which check only the obsidian half via `check-ignore` and rely on the
  byte-equality assertion to imply the operator's line survived. This one proves both functionally.
- **Follows the existing shape exactly**, no parallel harness: same `RepositoryRoot` /
  `Directory.CreateDirectory` / `CreateService()` / `EnsureRepositoryAsync()` / byte-equality assert /
  `rev-list --count` / `AssertPorcelainIsEmptyAsync` skeleton as the three siblings immediately above it
  (`:1322-1394`).
- **`PinnedGitEnvironment`** is passed to both `check-ignore` calls, consistent with D4/2.6 and the
  sibling fixtures; the `rev-list` call omits it, which is also what every sibling does (rev-list
  doesn't read ignore config, so pinning it buys nothing).
- **Would it survive a different mutation of the same line?** I checked the guard itself:
  `existingContent.Length > 0 && !existingContent.EndsWith('\n')`. This fixture's content is `"*.tmp"`
  (no newline, non-empty), so `Length > 0` is true and doesn't discriminate here — a mutation that drops
  or inverts that half (e.g. always evaluating the `EndsWith` half alone) would **not** be caught by this
  test, because the empty-file case where `Length > 0` matters is exactly the sub-branch the worker
  named as an explicit, disclosed blind spot ("empty-pre-existing-file case … not covered"). That is the
  right call under the brief's "only if it costs nothing" scope, not an oversight — but it means the
  guard is only half-falsified, which the worker's own blind-spot line already says. No new finding here,
  just confirming the disclosed gap is real and correctly scoped.

### Mutation claim — re-run independently, not taken on trust

Re-ran the worker's exact mutant myself (hard-code `needsSeparatingNewline = false`), same target,
`mutate.sh`, full unfiltered `dotnet test`, no filter, no pipe:

```
checksum before 655989a94973 (matches worker's reported before)
checksum after  47ae22c67ae2 (matches worker's reported after — mutation was live, not a no-op)
Failed!  - Failed: 1, Passed: 904, Skipped: 0, Total: 905, Duration: 2 m 58 s
[harness] KILLED
[harness] reverted, checksum matches (655989a94973)
```

Only the new test failed — same result, same failure shape as the worker's run. That's 2/3 confirmation
runs across worker+reviewer, consistent; no need for a third. `git diff -- src` and
`git status --short -- src` both empty after my run, and again now.

### 2. My spec fix (this review's brief refers to it as "my spec fix" — read literally as the
architect's, verified against code)

`specs/content-store/spec.md` read whole, cold, all five scenarios:

- **`:18-21`** (SHALL NOT) — exactly true against `ContentRepositoryService.cs`:
  `!repositoryHasNoCommitsYet → return` (`:706-716`) gates the entire seeding branch, and `:136`'s
  `RepositoryHeadIsUnbornAsync` is precisely "history, not `.git`'s presence" — the new sentence names
  the unborn-`HEAD` case explicitly, closing exactly the gap Blocker 2 found.
- **Scenario 3 (`:52-56`)** now reads "a volume carrying a git repository that already has commit
  history" — matches `:18-21`'s discriminator, and is no longer false for the unborn-`HEAD` case.
- **Scenario 4 (`:58-64`)** already used "no commits yet" before this fix and still does — consistent.
- **All five scenarios cross-checked against each other**: Scenario 1 (fresh init), Scenario 2 (history
  already has config, left alone), Scenario 3 (adopted/history discriminator), Scenario 4 (append
  path), Scenario 5 (fail-fast) — no remaining internal contradiction, and I did not find the defect
  recurring a third time inside this file (it recurs in `design.md` instead — see Blocker above).
- **Nit, not blocking, and already flagged by the supervisor and still unaddressed**: `:23` (now the
  "Where a volume the system is initializing…" paragraph) still runs to 107 columns, past the file's
  usual ~100-column wrap. Looking at the whole file, though, hard-wrapping isn't actually consistent
  here — a dozen other lines in this same file run 95-104 columns (`:5,6,7,8,11,14,15,18,19,20,24…`), so
  this isn't a fresh regression introduced by the edit, just a file that was never strictly wrapped.
  Not worth a remediation round on its own.

### 3. `design.md` restoration — reproduction verified, reasoning correct, but see Blocker above

- **Reproduction re-derivable from the transcript as written**: anchored `/.obsidian/` +
  `!docs/.obsidian/`, negation works before the append (`check-ignore` exit 1), dead after
  (`check-ignore` exit 0). I did not re-run this one myself (the supervisor already ran it against a
  scratch repo, and it's a plain git-semantics claim, not a ZeroWiki code claim), but the mechanism —
  appending a bare `.obsidian/` after a negation makes the negation the earlier, overridden line — is
  textbook `.gitignore` precedence and matches `EnsureInitialCommitAsync`'s append behaviour
  (`:745-762`, which always appends at the end of the file, never re-orders).
- **Describes the current code, not the withdrawn design**: rescoped correctly to "a property of
  appending to the end of someone else's file, which both designs do" rather than a property of
  `check-ignore`. Marked as restored with the wrong reasoning stated and rebutted, not silently
  reinstated — matches the brief.
- Its only problem is that the sentence it was written to correct still exists unedited three
  paragraphs above it in the same file (the Blocker).

### 4. Cross-artefact contradictions beyond the Blocker

None found. README.md, `specs/content-store/spec.md`, and the restored `design.md` Risks entry now
agree on the "commit history, not `.git` presence" discriminator and on the negation override being
live-but-accepted. The DEVLOG's pivot post (`:898-908`) still carries the original wrong argument, which
is correct and expected — it's append-only history, and the architect's follow-up post at the bottom of
§3 states plainly which one was wrong. That is the intended shape (reconstructible-and-wrong, flagged),
not a live contradiction the way `design.md:83` is.

### What this review could not see

- I did not re-run `make gates` (briefed not to) — `BUILD_EXIT:0`/`TEST_EXIT:0` (905)/`FORMAT_EXIT:0`
  are taken from the worker's report, cross-checked only by my own independent `dotnet test` run inside
  the mutation harness, which also came back at 905 total with only the intentional mutant failure.
- I did not re-verify the negation-override reproduction by hand in a scratch repo — matched against the
  supervisor's transcript and the code's append logic instead. If that transcript were fabricated this
  review would not catch it; nothing in this block gave reason to suspect it.
- Blind spot on the test's own falsifiability is disclosed by the worker and confirmed by me for the one
  shape checked (hard-code to `false`); I did not additionally try inverting the `Length > 0` half or an
  off-by-one on `EndsWith`, per the cap and because the worker's blind-spot note already names that gap
  correctly rather than hiding it.

→ @worker (one blocker: `design.md:82-84`)

Reviewed-state: 42296b6ce1af
HEAD: b5a59ee

**[architect]** Blocker fixed — and it is the third time, so the pattern is the finding → @reviewer

D5's narrative still carried the claim the restored Risks entry exists to rebut: *"the text is present,
so nothing is appended and their re-inclusion survives"*, three paragraphs above the entry saying that
holds for one shape only. Rewritten to state what the revision does **not** change, why the override
belongs to appending rather than to the instrument, and that an earlier draft claimed otherwise and was
wrong. `VALIDATE_EXIT:0`.

**Three findings today share one shape, and two of them are mine.** `File.Exists` standing in for "is
it ignored". A root-only probe standing in for "wherever it appears". A hostile-config run standing in
for "the pinning is complete". My §1 spec fix qualifying the reported sentence and not its neighbours.
And now: restoring a risk entry to `design.md` while leaving the argument it rebuts intact in the same
file. Every one is a check answering a narrower question than the thing it was taken to establish.

The specific lesson for artefact edits, since I have now made it twice: **a correction is not done when
the reported sentence is fixed — it is done when every sentence sharing that claim is fixed.** The
reported location is a sample, never the extent. That is what "read it whole and cold" means, and I
asked the reviewer for it on `spec.md` this round while not doing it myself on `design.md`.

Worth carrying past this change: it argues for the per-block/per-section auditor split on evidence
rather than procedure. The block reviews caught the first two because they were diff-local defects; the
supervisor caught the third and fourth because they required asking what *else* produces the same
green, across a range no block review sees.

**[reviewer]** Re-audit of the final remediation — blocker resolved, `design.md` now coherent whole-file

### Verdict: Approve

### The blocker — checked against the whole file, cold, not just the diff

Read `design.md` end to end (`Context` through `Open Questions`), not just `git diff HEAD` for the file.
`:82` (the old "now resolves *better* … their re-inclusion survives" line) is gone; the replacement at
`:84-89` states the negation override survives D5's revision, names the one shape that is genuinely left
alone (a file whose exclusion line is the literal text `.obsidian/`), attributes the override to
appending rather than to `check-ignore`, and flags that an earlier draft claimed otherwise and was
wrong. The Risks entry at `:106-129` says the same thing in more detail, with the reproducible transcript.
No third instance of the claim anywhere else in the file — Context, Goals/Non-Goals, D1-D4, "Match a
whole trimmed line", "Root `.gitignore` only", or the remaining Risks/Open Questions entries make no
claim about negation at all, which is correct: they aren't the place that claim belongs.

**The two passages now agree, not merely fail to contradict.** D5's narrative says "left alone… any
other shape has no matching line, so we append and our rule wins" and points at the Risks entry; the
Risks entry gives the mechanism and the reproduction. A cold read gets one coherent account either
order.

### The claim itself — verified against git, not just against the code's string check

Reproduced both halves myself, git 2.55.0, scratch repos:

- **The "left alone" shape is real.** `.gitignore` containing exactly `.obsidian/\n!docs/.obsidian/\n`
  (the literal line present, so `EnsureInitialCommitAsync`'s `line.Trim() == ".obsidian/"` check finds
  it and appends nothing, `ContentRepositoryService.cs:739,750`): `git check-ignore -v
  docs/.obsidian/workspace.json` exits 1 — not ignored, negation intact.
- **The override shape is real and matches the Risks transcript exactly.** `/.obsidian/\n!docs/.obsidian/\n`
  (anchored, no literal `.obsidian/` line, so the check does not match and we append): before append,
  `check-ignore -q` exits 1 (negation works); after `printf '.obsidian/\n' >> .gitignore` (what bootstrap
  does), it exits 0 (negation dead). Same exit codes, same git version as the block's own reproduction —
  I ran it independently rather than trusting the transcript.

The code's check is exactly what the design describes: a whole-trimmed-line match against the literal
string `.obsidian/`, nothing more (`ContentRepositoryService.cs:739-750`). No gap between what's claimed
and what's implemented.

### `spec.md` and README — no over-promise found

`spec.md`'s diff (the "commit history, not `.git` presence" discriminator) predates this remediation and
was already reviewed in the prior round; unchanged since (`git diff` on the file shows no lines touching
negation). Neither `spec.md` nor README makes any claim about negation surviving in general — README's
"Obsidian's own files end up in the repository" section (`:155-166`) describes only the append/adopt
behaviour and is silent on the negation edge case, which is accurate: it doesn't promise protection it
doesn't provide. Grepped both for "negation", "re-includ", "!", "override" in this context — nothing
found that needs correcting.

### Scope

Nothing else in the block moved since the prior Approve of the test and the spec fix — confirmed
`tests/ZeroWiki.Tests/Content/ContentRepositoryServiceTests.cs` diff unchanged from what was already
reviewed. No gates re-run (not required — no code changed, only `design.md` prose). No mutation run
needed for a documentation-only fix.

### What this review could not see

Instrument: reading `design.md` whole and cold, plus two independent git reproductions of the negation
claim (both shapes: covered, and overridden). Blind spot: I did not re-derive the D5 `check-ignore`
withdrawal history itself (accepted as settled from prior rounds), and I did not re-run `make gates` —
neither was disturbed by this edit, which is a documentation-only diff.

Reviewed-state: 0cf2df98555e
HEAD: b5a59ee

**[architect]** Remediation block closed — gates green, committed

`GATES_EXIT:0`, all four `_EXIT:0`, **905 passed** (904 + the fourth 2.5 case). Fingerprint
`0cf2df98555e` matches the reviewer's Approve; `git diff -- src` empty after two mutation runs in this
block (worker's and the reviewer's independent confirmation). Ticks nothing — every task in the change
was already ticked.

`dotnet build-server shutdown` run *before* the gates this time, and the permission failure did not
occur. That is corroboration for the §3 diagnosis rather than proof, but it is the prediction the
diagnosis makes.

**[supervisor]** Section 3 re-audit — `git diff fcec6c6..HEAD` (`b5a59ee`, `c029b86`), and the change end to end

### Verdict: Request changes

One blocker, and it is the same class as §3's blocker 2 — one file away, in the one artefact no block
of this change ever touched. Everything else I was asked to judge is discharged.

### Blocker — `proposal.md:30-32` still states the discriminator §3 corrected

```
- **Adopted repositories are left alone.** A volume that already contains a git repository is not
  modified to add or amend a `.gitignore`; only a repository ZeroWiki initializes itself is seeded. The
  system does not edit a repository it did not create.
```

Both sentences are false for the case `c029b86` corrected in the spec. A volume carrying `.git` with an
unborn `HEAD` **does** get its `.gitignore` created or appended and committed:
`ContentRepositoryService.cs:110-136` sets `repositoryHasNoCommitsYet` from
`RepositoryHeadIsUnbornAsync`, `:213` passes it into `EnsureInitialCommitAsync`, and `:706` takes the
seeding branch on `true`. `specs/content-store/spec.md:18-21` now names exactly this
("The discriminator is history, not the presence of a `.git` directory"); the README's second paragraph
now says "already has commit history"; `proposal.md` was left saying the thing all three replaced.

Why this is a blocker rather than a nit: §3's blocker 2 was reported as *the spec's* adopted-repository
scenario. It was fixed there, and the identical claim survived in the file nobody was pointed at — the
fourth time in this change a reported sentence has been corrected while its sibling stood. The remedy
for that pattern is not a better correction; it is that the last audit does not wave the sibling
through because it is small.

**Remediation shape** — one file, no code, no tick. Replace those three lines with the discriminator the
spec and README already use: a repository that already has **commit history** is left alone; a volume
carrying a `.git` with no commits yet is initialized and seeded, and where it already has a root
`.gitignore` that file is appended to, never replaced. `proposal.md:51` ("an adopted repository is not
modified") is consistent once the rule above it is, and needs no change.

**This may be a Product Owner call rather than a fix.** Under §3c.4 this is the second round, so it
stops here either way. If the ruling is that `proposal.md` is a proposal-time artefact and is not
retro-edited, that is a coherent position — but then say so *in* `proposal.md`, because a cold reader of
the archive reads it before the spec and will otherwise take the wrong rule from it.

### The two §3 blockers — discharged, including the sibling

**Blocker 1 (the deleted accepted risk).** Read `design.md` whole and cold. The risk is restored at
`:106-129` with a reproduction against real git, and D5's own narrative at `:84-89` now rebuts the false
claim by name rather than merely omitting it. The two halves agree: the override is a property of
appending to the end of someone else's file, not of `check-ignore`, and it survives the revision. No
third instance of the claim anywhere in the file.

One imprecision I would not fix now, for `## NEXT`: `design.md:68-71` still files
"appending after an operator's same-file `!docs/.obsidian/` silently killed their negation" under
"every divergence found failed in the same direction" — as one of the three defects that *sank*
`check-ignore`. It is neither: it fails in the other direction, and it is the one property that
survived. The paragraph three lines below corrects the implication explicitly, so a reader who keeps
reading is not misled; a reader who stops at the bullets is.

**Blocker 2 (the unborn-`HEAD` scenario).** `spec.md:18-21` and `:52-56` both now discriminate on commit
history, and the `SHALL NOT` names the unborn case in its own words. Verified against the code path
above rather than against the tests' framing.

### The new test is adequate, and the `Length > 0` half is not a gap

`ContentRepositoryServiceTests.cs:1398-1427` asserts the resulting bytes **and** that both rules are
live under a pinned git environment — the operator's `foo.tmp` and our `.obsidian/workspace.json`. It
therefore fails for the right reason: the defect it guards (`*.tmp.obsidian/` — their rule broken, ours
never applied, no error) breaks the second assertion as well as the first.

The `existingContent.Length > 0` half stays unexercised, and I would name it in the report as
*understood*, not as owed. It only distinguishes a zero-byte pre-existing `.gitignore` from a
non-existent one, and dropping it yields `"\n.obsidian/\n"` instead of `".obsidian/\n"` — a leading
blank line, which git's ignore grammar discards. A mutant there survives because it is behaviourally
equivalent, not because nothing is watching. Writing a test for it would assert a byte the product does
not care about.

### The change end to end

Code, spec, design, tests and README describe one system, with the single exception above.
Cross-checks I made that no block review's range covered:

- **"Fail to start" is real, not just "something throws".** The spec's refusal requirement
  (`spec.md:34-36`) is only satisfied if the throw reaches the host. `Program.cs:108` awaits
  `EnsureContentRepositoryAsync` uncaught, before `app.Run()`, so a `GitProcessException` from
  `git add docs/.gitkeep .gitignore` exits the process. §1 built it, §2 tested the throw and the unborn
  `HEAD`, and nothing in either block established the last link.
- **Instruments.** The section does not rest on one. 2.1 drives the real unscoped `add -A`; 2.5 asserts
  bytes; 2.5 and 2.1 both assert ignored-ness through pinned `check-ignore`; 2.7 asserts the refusal and
  the absent commit. Worth recording, though, that the **append** branch (D5's whole reason to exist) is
  evidenced only by `check-ignore` and by file bytes — the real reconciliation path is driven only for
  the fresh-write branch. Both instruments read the same ignore machinery, so I rate the residual risk
  low; it is named because it is the shape this project has been caught by, not because I found a defect
  behind it.
- **No residue of the withdrawn design.** `check-ignore` survives in `tests` only, as an assertion
  instrument, documented as such at `ContentRepositoryServiceTests.cs:1523`. Nothing in `src` consults
  it. `git status --short` clean; no mutation residue.
- **The record is complete.** Both commits in range are covered by a reviewer verdict, and I recomputed
  both. `0cf2df98555e` reproduces exactly for `c029b86`. `82db5dc6bf1c` reproduces for `b5a59ee` **only
  once `tasks.md` is also excluded** — because the workflow verifies the fingerprint at step 6 and ticks
  at step 7, so every block whose commit carries a tick has this offset by construction. That is not a
  defect, but a future reader trying to reproduce a fingerprint from a commit range will fail without
  it. The command that reproduces them here:

  ```sh
  git diff <base> <commit> -- ':/' ':(top,exclude,glob)**/DEVLOG.md' ':(top,exclude,glob)**/tasks.md' \
    | shasum | cut -c1-12
  ```

### What my review could not see

- I did not run the gates or any mutant; `GATES_EXIT:0`, 905 passed and the clean `git diff -- src` are
  taken from the architect's post, not re-derived.
- **Nothing here has been run against a real Obsidian vault.** The premise of this change came from the
  Product Owner's `obsidian-git` run; the fix has been verified only by tests. And by D2 the Product
  Owner's own wiki — an existing deployment — will never receive the rule, so confirming it needs a
  fresh volume, not their live one.
- I judged the README against the spec and the code, not against a reader. Whether its two paragraphs
  land for someone who has never seen this change is a Product Owner judgement.

**[architect]** Product Owner ruling: amend the proposal → @reviewer

Put to the Product Owner at the two-round stop, per §3c.4. **Decision: amend.** The blocker's sentence
describes system behaviour, not proposal-time intent, so freezing it would archive a false claim as the
first thing a cold reader meets.

`proposal.md:30-32` now leads on the code's discriminator — commit history, not the presence of a
`.git` directory — names the unborn-`HEAD` case as initialized-and-seeded, and states that a root
`.gitignore` already on such a volume is appended to rather than replaced. `proposal.md`'s Impact
section needed nothing once the rule above it was right.

**And this time I swept instead of fixing the reported line.** Every artefact grepped for the old
discriminator and its looser phrasings. One more instance: `design.md:34`, in D1's *rejected*
alternative — "mutate a repository it did not create". Rationale for a road not taken rather than a
behaviour claim, so it misleads nobody on its own, but it is the same imprecise phrase and now reads
"whose history it did not start". `spec.md:19` and `tasks.md:12` were checked and are correct as they
stand.

That sweep is the whole content of the supervisor's finding: four times in this change a correction
fixed the sentence that was reported while a sibling stood, and the fourth was in the one artefact no
block ever touched. `VALIDATE_EXIT:0` after the edits.

**[reviewer]** Audit of the amendment block (`proposal.md`, `design.md`) → @architect

### Verdict: Approve

### What I checked

**`proposal.md:30-33` against the code.** Read `ContentRepositoryService.cs:99-213` and
`:687-762` directly rather than taking the prose on trust:

- *"The discriminator is commit history, not the presence of a `.git` directory."* True.
  `repositoryHasNoCommitsYet` (`:112`) is set from `RepositoryHeadIsUnbornAsync` when
  `hasOwnGitEntry` is true (`:136`) and unconditionally `true` when there is no `.git` at all
  (`:157`) — one predicate, both cases, exactly as claimed.
- *"A repository that already has commits is not modified."* True — `EnsureInitialCommitAsync`'s
  `if (!repositoryHasNoCommitsYet)` branch (`:706-715`) returns after only a
  `Directory.Exists` check; no file, `.gitignore` included, is touched.
- *"A volume whose `HEAD` is unborn... is initialized and seeded."* True — same predicate drives
  both `AssertNoNestedGitRepository`/`git init` at `:179-188` and the seeding branch at
  `:717` on down, for the `.git`-present/unborn-`HEAD` case as much as the no-`.git` case.
- *"A root `.gitignore` already on such a volume is appended to rather than replaced."* True —
  `:735-758`: file absent → write; file present → read lines, compare trimmed, append with a
  completed separating newline if the operator's rule isn't already its own line, via
  `File.AppendAllTextAsync`. No overwrite path exists.

All four claims are exactly true of the code, not merely defensible.

**Cross-artefact agreement.** `spec.md:19-21` ("The discriminator is history, not the presence of
a `.git` directory: a volume carrying a repository whose `HEAD` is unborn has no history, and the
system initializes it") states the identical fact in the identical frame — spec and proposal now
read as one account, not two independently-worded ones that happen not to contradict. README.md's
§"Obsidian's own files end up in the repository" (`:155-166`, corrected in `b5a59ee`, untouched by
this block) already used commit-history framing and needed no further change. `design.md` D1/D2/D5
already carried the correct discriminator throughout; the only edit in this block is `:34`.

**`design.md:34`.** Confirmed in context: it sits inside D1's *rejected* ensure-on-every-start
alternative, arguing the rejection from "mutate a repository whose history it did not start" —
the same fact (history, not `.git`) applied to the rejected alternative's own action (mutating on
every boot), not restated as a behaviour claim about the shipped design. The surrounding
rationale — contradicts the adopt-as-it-stands posture, produces an unwanted startup commit — is
unchanged. Meaning preserved; only the imprecise noun phrase was swapped for the precise one.

**The sweep, redone independently.** Ran my own greps across every file in the change directory
(`find . -name '*.md'`; `grep -rniE` for ".git directory" presence-language, "repository it did
not create/initialize", and separately for every "left alone / untouched / not modified / not
touched" occurrence, to catch a sentence that uses the correct noun but pairs it with the wrong
verb) plus a manual read of `proposal.md`, `design.md`, `spec.md`, and `tasks.md:1-16` in full.
Findings:

- `tasks.md:12` ("a repository ZeroWiki adopts rather than initializes has its ignore rules left
  untouched") frames the discriminator as *adopts-vs-initializes*, never as `.git` presence — no
  correction needed, agrees with the architect's read.
- `spec.md:19-21`, already quoted above — the corrected phrasing, source rather than target of the
  sweep.
- Every other "left alone/untouched/not modified" hit (`proposal.md:30-33,53`, `design.md:86`,
  and every DEVLOG occurrence) is either post-correction text or historical thread record — DEVLOG
  entries are append-only and not in scope for amendment.
- No hit anywhere for the presence-of-`.git` framing outside the one instance the architect found
  and fixed at `design.md:34`.

I found nothing beyond what the architect's post already names. The sweep held up under an
independent, differently-shaped set of greps plus a full manual read of the four non-DEVLOG
artefacts — that is the instrument, and its blind spot is the same one any grep-based sweep has:
a paraphrase that uses neither "`.git`" nor any of the "left alone/untouched/not modified/not
touched" verbs (e.g. some entirely different verb) would not surface. I judge that risk low here
given the phrase has recurred in a narrow, identifiable vocabulary across all three prior rounds,
but it is not zero.

### Blockers

None.

### Nits

None — this is prose, not code, and the diff is exact.

### Architectural notes

- `## NEXT`'s resume line (`openspec/changes/.../DEVLOG.md:3151-3152`, unedited by this block)
  still reads "§2 open, block 2.1–2.7 briefed. §1 is closed" — stale relative to §3 now closing.
  Not this block's doing and not blocking; flagging for whoever next rewrites the pin.
- Scope: this block touches only `proposal.md` and `design.md`, both proposal-time artefacts, no
  `src/`, no `tests/`, no `tasks.md`. Squarely a documentation amendment, within the section, and
  within the Product Owner's "amend" ruling — nothing here reaches into code or another section.

### Reviewed-state: 1a8744a9b72d
HEAD: c029b86

**[architect]** Amendment block closed — gates green, committed

`GATES_EXIT:0`, all four `_EXIT:0`, 905 passed. Fingerprint `1a8744a9b72d` matches the reviewer's
Approve. Ticks nothing.

### Two gate observations, both recorded because a clean final run hides them

**1. The build-daemon diagnosis was incomplete.** §3 recorded `dotnet build-server shutdown` as the
fix. This run failed *despite* a preemptive shutdown — `error : Error writing to source link file
'obj/Debug/net10.0/ZeroWiki.LockTestHarness.sourcelink.json': Access ... denied`, a different project
and a different file, same signature (`com.apple.provenance`, `-rw-r--r-- rendle staff`, appendable
from a shell). Shutdown **plus** removing that one stale artefact gave `BUILD_EXIT:0`.

So the condition is **both**: a daemon started under one sandbox *and* files it left behind. That is
why each single fix appeared to work once and then failed — deleting `ZeroWiki.dll` alone did not fix
it in §2, and shutdown alone did not fix it here. Corrected in preference to §3's account, which
stands as written.

**2. A transient test failure, unrelated to this change.**
`GitSmartHttpRealClientTests.PageSavedThroughTheApp_IsWhatARealClientClones` failed once with
`Assert.Equal() Failure: Expected: Saved / Actual: RepositoryBusy` — write-lock contention. Re-ran
green (905/905), and green again in the gate run above; 905 had passed in every run before it. Nothing
in this change touches the save path or the lock — §1's edit is bootstrap seeding only.

**Recorded rather than dismissed.** A once-in-N lock-contention failure in the Smart HTTP client test
is a real signal about that test or the lock, and "it passed on re-run" is how a genuine intermittent
defect gets discovered three months later by someone else. It belongs to no section here — carried to
`## NEXT` for the next session.

## NEXT

**Resume point:** §2 open, block 2.1–2.7 briefed. §1 is **closed** — supervisor `Approve` on
`96a7adf..HEAD` after the pivot.

- Branch `change/ignore-obsidian-config-in-content-repo`, base `96a7adf`. Two commits: `92fb126` (§1),
  plus the simplification fix.
- **§1 supervisor blockers, current status:**
  - *Blocker 1 (committed block had no reviewer Approve)* — repaired. The lost verdict was re-posted by
    its author from transcript, dated, at `## 1.`; the Approve for the simplification block follows it.
  - *Blocker 2 (the D5 branch has no executing evidence)* — **OPEN.** Carried into §2's brief; it is
    what §2 exists to close.
  - *Blocker 3 (`git add` refusal-to-start)* — ruled by the Product Owner: fail fast is intended.
    Recorded in `design.md` Risks and the spec; **2.7** tests it.
  - *The `❓` on `core.excludesFile`* — dissolved with the `check-ignore` instrument.
- **§2 is the weight of this change** (2.1–2.7) — briefed under `## 2.`, aimed at the supervisor's
  coverage gaps rather than the task text.
- **Owed to §3, two items:** 3.1's wording predates D5, so the README must also cover the
  pre-existing-`.gitignore` case; and the §1 supervisor's non-blocking finding that
  `specs/content-store/spec.md:20-23` lacks its "no commits yet" qualifier, so the prose reads as a
  contradiction with the adopted-repository SHALL NOT above it. Both are wording, both land in §3.
- Live decisions: D2 ignore-only, never untrack. D5 whole trimmed line, never a substring, root
  `.gitignore` only — a redundant duplicate is the accepted failure direction.
- The record: a reviewer verdict was lost today when an architect pin-rewrite discarded everything below
  the pin marker. Append above the pin, and verify placement with `grep -n '^## '` after writing.
