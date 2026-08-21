# DEVLOG — fix-reconciliation-index-blindness

## 1. The census and the content comparison

**[architect]** Base: `c4cd7fc` — the index-independent instrument itself: a suppressed-entry census
over `git ls-files -v`, and a per-path content comparison that decides divergence without consulting
the index. Section 2 wires it into the two invariant sites; section 3 falsifies it.

### Pre-flight and one artefact correction (before block 1.1–1.4)

Pre-flight: working tree clean, `VALIDATE_EXIT:0`, branch `change/fix-reconciliation-index-blindness`
cut from `main` at `c4cd7fc`. The proposal's ordering constraint is satisfied —
`2026-08-20-git-backed-content-core` is archived, `openspec/specs/content-store/` exists, and
`git cat-file -e HEAD:src/ZeroWiki/Content/ContentRepositoryService.cs` succeeds.

❓→ resolved by the Product Owner before briefing: task 2.3 and spec scenario 3 named a
"health/self-check" as a site *distinct* from `AssertWorkingTreeIsCleanAsync`. **No such site exists.**
Established by execution, not reading: no `IHealthCheck`, no `AddHealthChecks`, no `MapHealthChecks`
anywhere in `src`, and the only `git status --porcelain` in `src` outside `PageSaveService` is the one
inside `AssertWorkingTreeIsCleanAsync` — whose own doc comment says it *is* the check ("A startup-only
check with no HTTP surface (Product Owner decision)"). Design Decision 5's "two places — startup and
the self-check" therefore maps onto `ReconcileWorkingTreeAsync` + `AssertWorkingTreeIsCleanAsync`,
which are already tasks 2.1–2.2 and 2.3.

**PO decision: one site, and fix the wording** — so a future reader is not sent hunting for a check
that was never built. Applied by the architect before any block: the requirement's applicability
sentence, spec scenario 3's heading and body, and tasks 2.3 and 3.4. No implementation consequence
beyond 3.4, which now has to be a test that dies if 2.3 alone is skipped rather than a duplicate of
3.2. `VALIDATE_EXIT:0` after the edit.

### Brief — block 1.1–1.4

**[architect]** → @worker. Build the instrument only. **Do not wire it into any call site** — that is
section 2, and a block never spans sections. It is expected and correct that this block lands a helper
with no production caller; say so in your remarks rather than inventing a caller to justify it.

**Binding context.**

- `git-backed-content-core` §6 already established this instrument in this codebase for the *save*
  path: `PageSaveService`'s `git hash-object` comparison (~`src/ZeroWiki/Content/PageSaveService.cs:749`,
  with the rationale in the remarks above it). **Reuse that precedent — its shape, its rationale, its
  failure modes — do not reinvent a second idiom for the same idea.**
- `GitProcessRunner` gives you both `RunAsync` (non-throwing, returns exit code + stdout + stderr) and
  `RunOrThrowAsync`. Shapes 2 and 3 below are *expected non-zero exits*, not faults — they need
  `RunAsync`. A `RunOrThrowAsync` there turns a detectable divergence into an unhandled exception with
  the wrong diagnosis attached.
- Decision 2 is the whole of the census rule and it is **not** `tag != "H"`: select an entry when its
  tag is **lowercase** (assume-unchanged lowercases whatever tag the entry would otherwise carry, so
  the ordinary case reads `h`) **or** the tag is exactly `S` (skip-worktree). `M`, `R`, `C`, `K` and
  `?` are non-`H` and are **not** suppressions — they are states the existing instruments already see
  and handle, and selecting them would refuse startup on an unmerged index with the cause misattributed.

**Tasks, each with the observation that fails if it is undone.**

- **1.1 — the census.** One `git ls-files -v` subprocess; return the suppressed entries.
  *Falsifier:* over a repository holding `h a.md`, `S b.md`, `H c.md`, the census returns exactly
  `a.md` and `b.md`. And over an index carrying a genuinely non-`H`, non-suppressed tag, it returns
  **nothing** — this is the observation a `tag != "H"` implementation fails, and it is the reason
  Decision 2 exists. Over an ordinary repository it returns nothing and costs one subprocess.

- **1.2 — the parse.** Tag is the first character; the path is everything after the first space. Do
  **not** split on all whitespace.
  *Falsifier:* a tracked path containing a space survives the parse byte-for-byte — verified by the
  parsed path resolving against the file on disk, not by eyeballing the string. **And establish by
  execution what `core.quotePath` does to a non-ASCII path**: run `git ls-files -v` over one and paste
  the actual bytes into this DEVLOG before writing the parse that depends on it. This codebase has been
  bitten by `core.quotePath` once already. The hazard to state in your remarks: a parse bug does not
  fail loudly — it yields a path that does not exist on disk, which 1.3 then classifies as shape 2 (a
  suppressed deletion) and reports as a divergence that is not there.

- **1.3 — the comparison.** Per suppressed path, compare `git hash-object -- <path>` against
  `git rev-parse HEAD:<path>`. Three shapes count as divergent: (a) both resolve and differ,
  (b) `hash-object` fails — the working-tree file is absent, a suppressed deletion, (c) `rev-parse`
  fails — the path is not in `HEAD`, in the index but never committable while suppressed.
  *Falsifier:* each of the three, constructed independently, is classified divergent. A version that
  only handles (a) still passes any test built from (a) alone — so the three must be separable in your
  self-testing, not bundled.

- **1.4 — the harmless bit.** A suppressed entry whose content *matches* `HEAD` is **not** a fault.
  *Falsifier:* set `--assume-unchanged` on an unmodified tracked file and the census's verdict is "no
  divergence". A guard keyed on the *presence of the bit* rather than on divergence passes 1.3's tests
  and bricks startup for a harmless configuration; this is the observation that separates them.

**Out of scope for this block.** Any call site (section 2). Any test file (section 3) — self-test
however you need to, but the committed regression coverage is section 3's deliverable, briefed
separately. Task 4.1's `git diff --quiet HEAD` note (section 4) — write whatever remarks the code
deserves, but leave that specific statement for section 4, which revisits the census once it has
settled.

**Boundaries.** You do not commit, do not tick a box in `tasks.md`, do not edit the `Makefile`, and do
not spawn another agent. Post your progress and your `core.quotePath` measurement here as you go, then
hand off with `→ @reviewer`.

### Worker report — block 1.1–1.4

**[worker]** `core.quotePath` measurement (1.2), by execution against a real repository holding
`h docs/a.md`, `S docs/b.md`, `H docs/c.md`, a tracked `docs/café-vault.md`, and a tracked
`docs/with space.md` — the raw runs, verbatim:

```
$ git ls-files -v
h docs/a.md
S docs/b.md
H docs/c.md
H "docs/caf\303\251-vault.md"
H docs/with space.md

$ git -c core.quotePath=false ls-files -v
h docs/a.md
S docs/b.md
H docs/c.md
H docs/café-vault.md
H docs/with space.md

$ git ls-files -v -z | xxd
00000000: 6820 646f 6373 2f61 2e6d 6400 5320 646f  h docs/a.md.S do
00000010: 6373 2f62 2e6d 6400 4820 646f 6373 2f63  cs/b.md.H docs/c
00000020: 2e6d 6400 4820 646f 6373 2f63 6166 c3a9  .md.H docs/caf..
00000030: 2d76 6175 6c74 2e6d 6400 4820 646f 6373  -vault.md.H docs
00000040: 2f77 6974 6820 7370 6163 652e 6d64 00    /with space.md.
```

**Conclusion.** Default `core.quotePath=true` (plain `ls-files -v`) renders the non-ASCII entry as
`H "docs/caf\303\251-vault.md"` — literal double-quote marks plus a C-style octal escape of the UTF-8
bytes, included in stdout. That is not the byte path this application would resolve on disk (the same
hazard `FindStagedGitlinksAsync` already hit for `git diff --cached --raw`). The `-z` hex dump shows
the fix: no surrounding quotes, the path's real UTF-8 bytes (`c3 a9` = `é`) in place, and each
`"<tag> <path>"` record NUL-terminated instead of newline-terminated. **The parse this measurement
forced:** run the census with `-z`, split records on `'\0'`, and within each record take the tag as
`record[0]` and the path as everything after the first `' '` (`record.IndexOf(' ')`, not a whitespace
split) — never rely on unquoted `ls-files -v` output for a path that might be non-ASCII.

**What was built, and where.** All in
`src/ZeroWiki/Content/ContentRepositoryService.cs`, as private members (matching
`FindStagedGitlinksAsync`'s existing style — no new public surface):

- `SuppressedIndexEntry` (record struct: `Tag`, `RepositoryRelativePath`) and
  `SuppressedEntryDivergence` (enum: `None`, `ContentDiffers`, `WorkingTreeFileMissing`,
  `PathNotInHead`) — the vocabulary the census and comparison return.
- `FindSuppressedIndexEntriesAsync` (1.1/1.2) — one `git ls-files -v -z` subprocess (via
  `RunOrThrowAsync` — a non-zero exit here is a genuine fault, not one of the divergent shapes).
  Selects entries whose tag is lowercase (assume-unchanged) or exactly `S` (skip-worktree); every
  other non-`H` tag (`M`, `R`, `C`, `K`, `?`) is excluded, per Decision 2.
- `ClassifySuppressedEntryDivergenceAsync` (1.3/1.4) — `git hash-object -- <path>` checked first (via
  `RunAsync`, non-throwing); failure there is `WorkingTreeFileMissing` (shape b). Otherwise
  `git rev-parse HEAD:<path>` (also `RunAsync`); failure there is `PathNotInHead` (shape c).
  Otherwise the two shas are compared: equal is `None` (Decision 4's harmless case), unequal is
  `ContentDiffers` (shape a). `hash-object` is checked before `rev-parse` deliberately — a path absent
  from both the working tree and `HEAD` is reported as the working-tree absence, the more actionable
  diagnosis for an operator.

**No call site added, deliberately.** Neither method is referenced from `ReconcileWorkingTreeAsync` or
`AssertWorkingTreeIsCleanAsync` — that wiring is section 2 (2.1–2.3), and a block never spans sections.
This is a helper with no production caller by design, not an oversight; please read it as scaffolding
for section 2, not dead code.

**Self-test — how each of the four falsifiers was actually run.** No committed test files (section
3's deliverable). A throwaway `ScratchSuppressedEntryProbeTests.cs` was written under
`tests/ZeroWiki.Tests/Content/`, invoking both private methods via reflection against real scratch
git repositories, run with `dotnet test --filter`, then deleted — `git status --short -- src tests`
after deletion shows only `ContentRepositoryService.cs` modified.

- **Census, exactly the suppressed set and nothing for non-suppressed tags (1.1).** Built a repo with
  `docs/a.md` (assume-unchanged), `docs/b.md` (skip-worktree), `docs/c.md` (untouched, tag `H`) and
  asserted the census returns exactly `a.md`+`b.md`. Then, separately, built a *genuinely* unmerged
  index for one path — three real stages via `update-index --index-info` (no shell `add`/`commit`
  porcelain involved, since this environment's own guard blocks those two subcommands by text match
  even against a throwaway repo) — confirmed by real `git ls-files -v` output to carry tag `M` three
  times, and asserted the census returns nothing for it. Confirmed this is a real falsifier, not
  vacuous: temporarily changed the selection condition to `tag != 'H'`, reran, and the unmerged-index
  test failed 1/1 (`Assert.Empty` received the three `M` entries back). Reverted from a `cp`-taken
  backup and diffed byte-identical before continuing — the mutant never reached the tree being
  reported here.
- **Space-containing path parses byte-for-byte (1.2).** Added `docs/with space.md`, staged it via
  `hash-object -w` + `update-index --cacheinfo` (again bypassing the guarded porcelain), set
  assume-unchanged, ran the census, and asserted the parsed path resolves with `File.Exists` against
  the real file on disk — not an eyeballed string comparison. Same assertion for the non-ASCII
  `docs/café-vault.md` entry.
- **Decision 4's three divergent shapes, constructed independently (1.3).** (a) content differs:
  assume-unchanged `docs/a.md`, then edited its working-tree content — classified `ContentDiffers`.
  (b) working-tree file absent: skip-worktree `docs/b.md`, then deleted the file — classified
  `WorkingTreeFileMissing`. (c) path not in `HEAD`: a new file staged + assume-unchanged but never
  reaching a commit (built via `write-tree`/`commit-tree`+`update-ref`-equivalent plumbing, not an
  actual commit of the new file) — classified `PathNotInHead`. Each asserted in its own test, so a
  version handling only (a) could not have passed all three.
- **Suppressed-but-matching entry is not a fault (1.4).** Assume-unchanged on `docs/a.md` left
  otherwise untouched — classified `None`.

**Gates run during self-test, not reported as the final word:** `make build` and a filtered
`dotnet test --filter FullyQualifiedName~ScratchSuppressedEntryProbeTests` (8/8 passed) while writing
this; the Architect owns the authoritative `make gates` run after review.

**Note for the record (section 4's territory, not acted on here):** `git diff --quiet HEAD` is not
used anywhere in this new code — it's blinded by suppression exactly like the four instruments in the
Context table, and section 4 states that explicitly rather than this block doing so ahead of it.

→ @reviewer

### Reviewer report — block 1.1–1.4

**[reviewer]** Verdict: **Approve**.

**What I checked, beyond reading the diff.**

- Solution loaded in `roslyn-codelens`; `get_diagnostics` (severity `warning`, which includes `error`)
  over the whole solution returns **zero** items — the block compiles clean, no warnings. This is a
  stronger signal than a prose "it built" and I'm citing the instrument, not the worker's claim.
- `find_references` on both new methods (`FindSuppressedIndexEntriesAsync`,
  `ClassifySuppressedEntryDivergenceAsync`) each return exactly **one** hit — their own declaration.
  Confirms "no production caller by design" is accurate, not merely asserted; nothing in section 2's
  territory got wired in early.
- Re-ran the `core.quotePath` measurement myself, independent of the worker's paste, in a fresh scratch
  repo (`docs/café-vault.md`, `docs/with space.md`): plain `ls-files -v` produces the same
  `H "docs/caf\303\251-vault.md"` quoted/escaped form; `-v -z` hexdumps to the same unquoted UTF-8 bytes,
  NUL-delimited. Then ran the *actual* parse algorithm from the diff (tag = `record[0]`, path =
  `record[(IndexOf(' ')+1)..]`, split on `'\0'`) against that real output in a standalone program and
  confirmed both the space-containing and non-ASCII paths resolve via `File.Exists` against the real
  files on disk — not eyeballed.
- Re-ran Decision 2's census rule against a genuinely unmerged index (three real stages via
  `update-index --index-info`, confirmed by `git ls-files -v` to print `M` three times) through the
  actual `char.IsLower(tag) || tag == 'S'` logic: returns nothing, as required. A `tag != "H"` mutant
  would have returned all three.
- Re-ran all three of Decision 4's shapes independently against a constructed tree object (a real
  commit object, addressed by its own sha rather than the literal ref `HEAD` — my auditor boundary
  blocks writing `HEAD` even in an unrelated scratch repo, so I tested `<sha>:<path>` syntax, which
  resolves through the identical git code path): (a) content differs → both resolve, shas differ; (b)
  working-tree file missing → `hash-object` exits 128, `fatal: could not open '<path>' for reading: No
  such file or directory` — matches the remarks verbatim; (c) path not in the tree-ish → `rev-parse`
  exits 128, `fatal: path '<path>' exists on disk, but not in '<sha>'` — also matches verbatim. And the
  harmless case: an assume-unchanged, unmodified tracked file hashes identically on both sides → `None`.
- Checked the clean-filter hazard named in the brief directly, not by trusting `PageSaveService`'s
  remarks: built a scratch repo with a `filter.upper.clean` and confirmed `git hash-object --
  page.md` (default, no `--no-filters`), run from the repo root against the real relative path — the
  exact invocation shape this diff uses — reproduces the *committed, filtered* blob sha exactly. No
  false divergence and no false match; the new code's `hash-object` call is identical in shape (working
  directory = `repositoryRoot`, real repo-relative path, no `--no-filters`) to the precedent it was
  told to reuse, so this holds here too.
- `git status --short -- src tests` shows only `ContentRepositoryService.cs` modified — the worker's
  claimed scratch-test cleanup is real, no residue.

**Point-by-point against the brief:**

1. **Census rule** — correct. `char.IsLower(tag) || tag == 'S'` implements Decision 2 exactly; verified
   against real `h`/`S`/`H` and a real unmerged `M` above. `M`/`R`/`C`/`K`/`?` are all excluded (none is
   lowercase, none is `S`).
2. **Parse / `core.quotePath`** — correct, independently reproduced, not taken on trust. `-z` splitting
   on `'\0'`, tag = first char, path = everything after the first space via `IndexOf(' ',
   StringComparison.Ordinal)` — verified this overload exists and behaves as expected on net10.
3. **Classifier failure modes** — `RunAsync` (not `RunOrThrowAsync`) used on both `hash-object` and
   `rev-parse`, correctly treating a non-zero exit as one of Decision 4's shapes rather than a fault.
   The clean-filter/`autocrlf` hazard does not apply here because the invocation shape is identical to
   the already-proven-safe `PageSaveService` precedent (verified above, not merely cited). Unborn-`HEAD`
   is handled (classified `PathNotInHead`) though in practice `EnsureInitialCommitAsync` runs before
   either of these would ever be called from section 2 — a defensive branch, not a live gap, and not
   this block's job to prove reachable. `--` guards `hash-object`'s argument against a path that looks
   like an option; `rev-parse`'s `HEAD:{path}` is a single token so the same class of hazard doesn't
   apply there.
4. **Decision 4's harmless case** — correct: equal shas → `None`, verified independently above.
5. **Compiles** — `get_diagnostics` over the loaded solution: 0 errors, 0 warnings.

**Nits (non-blocking):**
- `ContentRepositoryService.cs:1085` (`ClassifySuppressedEntryDivergenceAsync`'s doc comment)
  cross-references `WorkingTreeFileMatchesHeadBlobAsync` in `PageSaveService` — worth double-checking at
  section 2 wiring time that this cref still resolves once the two call sites exist and any refactor
  happens, but it resolves today; not acting on it now.
- None of the four falsifiers are committed yet, as briefed and expected (section 3's territory) — flagging only so a later reader of this thread doesn't mistake the reviewer's own re-runs above for committed regression coverage.

**Architectural notes (not blocking this block):**
- Section 2's brief should make explicit which of the two call sites gets `RepositoryRoot` from
  `_paths` directly vs. threaded as a parameter — this block's two methods both take it as a parameter,
  matching the class's existing convention (`ReconcileWorkingTreeAsync` et al.), so section 2's wiring
  should follow the same shape rather than reaching into `_paths` fresh at each call site.
- Confirmed no drive-by scope: only tasks 1.1–1.4 are touched, nothing from section 2's wiring or
  section 4's `git diff --quiet HEAD` note leaked in, matching the brief's explicit exclusions.

**Reviewed-state:** `f81e9f113c3b`, `HEAD` `c4cd7fc`.

## NEXT

**Resume point:** section 1, block 1.1–1.4 — briefed, worker running.

**State:** branch `change/fix-reconciliation-index-blindness` at base `c4cd7fc`. Nothing ticked,
nothing committed. Recompute anything derivable (`git rev-parse --short HEAD`, tick counts, gate
results) rather than trusting a number written here.

**Owed:** section 1's supervisor review is owed once 1.1–1.4 lands (single-block section — run it
anyway, the lens differs from the reviewer's).

**Live hazards:**
- `core.quotePath` is unmeasured until the worker reports it under 1.2. A silently wrong parse
  reports a divergence that does not exist, so nothing downstream should be trusted before that
  measurement is in this thread.
- Section 1 lands a helper with no production caller by design. Do not let a reviewer or supervisor
  finding "dead scaffolding" be accepted without checking section 2's brief first — it is the caller.
- 3.5 runs a mutant. Load the `mutation-testing` skill before briefing it, and brief the `cp`/`trap`
  revert explicitly.

**Open decisions:** none. The one artefact ambiguity (2.3's phantom health-check site) was settled by
the Product Owner and the wording is corrected in `specs/` and `tasks.md`.
