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

### Supervisor review — section 1 (blocks 1.1–1.4)

**[supervisor]** Scope: `git diff c4cd7fc..HEAD` — one commit, `81a660c`; one production file touched
(`src/ZeroWiki/Content/ContentRepositoryService.cs`, +132), plus `specs/`, `tasks.md`, this DEVLOG.
Gates read from the thread, not re-run.

Verdict: **Request changes**.

Not on the reviewer's ground. Decision 2's census rule, the `-z` parse, the three shapes and the
harmless case are all correct for the input class they were tested against, and I reproduced none of
them again. What I checked is the class of input the section's *separate* audits never fed it, and the
answer is that the instrument is confidently wrong on two shapes this repository actually contains.

---

#### Blocker 1 — the instrument reports a divergence that does not exist, for an unmodified symlink

`git hash-object -- <path>` **follows a symlink and hashes its target's content**. Git stores a
symlink as a `120000` blob whose content is the *link text*. The two are never the same value, so
`ClassifySuppressedEntryDivergenceAsync` (`ContentRepositoryService.cs:1102`) returns `ContentDiffers`
for a suppressed symlink that nobody has touched.

Measured, not reasoned — scratch repo, `docs/link.md -> target.md`, `docs/target.md` containing
`hello`:

```
$ git ls-files -s
120000 a61f4138792cc53c6bf0badf85ccb88a10979abd 0	docs/link.md
100644 ce013625030ba8dba906f756967f9e9ca394464a 0	docs/target.md

$ git rev-parse <tree>:docs/link.md     # what HEAD holds
a61f4138792cc53c6bf0badf85ccb88a10979abd

$ git hash-object -- docs/link.md       # what this code compares it against
ce013625030ba8dba906f756967f9e9ca394464a   # == the *target file's* blob

$ git update-index --assume-unchanged -- docs/link.md && git ls-files -v
h docs/link.md                          # the census selects it
```

Wired by section 2, that is a **refusal to start over a tree with no divergence in it** — the exact
outcome Decision 4 and spec scenario *"A suppressed index entry over an unchanged file is not a
fault"* exist to forbid, and the requirement's third paragraph states as SHALL NOT.

This is not hypothetical for ZeroWiki: D17 exists precisely because symlinks arrive in `docs/` by
push, and `PageSaveService` carries `AnyPathComponentIsASymlink` to refuse them.

**Why no block review could see it, and why it is mine.** The brief told the worker to reuse
`PageSaveService`'s precedent; the worker did, faithfully; the reviewer verified the invocation shape
is byte-identical to the precedent and concluded "so it holds here too". All three are correct. What
none of them could see is that the precedent's proof carried a **precondition the new call site drops**:
in `PageSaveService`, `WorkingTreeFileMatchesHeadBlobAsync` (`PageSaveService.cs:744`) is only ever
reached for a single route-resolved path that D17's symlink refusal has already cleared. The census
hands the same call **every index entry in the repository**, symlinks and gitlinks included. Worker
self-test, reviewer re-run and the probe in the proposal all used regular files — three audits, one
instrument, one shared blind spot.

#### Blocker 2 — `WorkingTreeFileMissing` is asserted for three different causes, two of them wrong

`ClassifySuppressedEntryDivergenceAsync:1110-1113` maps *any* non-zero `hash-object` exit to
"the working-tree file is gone — a suppressed deletion". Measured, all exit 128:

| on disk | stderr | classified |
|---|---|---|
| file absent | `fatal: could not open '<p>' for reading: No such file or directory` | `WorkingTreeFileMissing` — correct |
| file unreadable (mode 000) | `fatal: could not open '<p>' for reading: Permission denied` | `WorkingTreeFileMissing` — wrong |
| gitlink / directory | `fatal: Unable to hash <p>` | `WorkingTreeFileMissing` — wrong |

Two consequences the section owes section 2:

- **It contradicts §6's stderr discipline, which 2.1 must order this against.** A git invocation that
  could not *read* part of the tree is the one case reconciliation already refuses to translate into a
  known state; here it is silently translated into a specific, false one. Decision 3 requires the
  refusal to name the responsible state; naming "a suppressed deletion" for an EACCES is naming the
  wrong one.
- **A suppressed gitlink lands in this branch.** Confirmed: `git update-index --add --cacheinfo
  160000,<sha>,sub/s` plus `--assume-unchanged` yields `h sub/s` in the census, and `hash-object` on
  it fails. So the very case 2.1 is briefed to order against the **existing** gitlink refusal will
  reach this classifier first and be diagnosed as a deleted file. `hashResult`'s exit code and stderr
  are discarded at :1110, so section 2 cannot recover the distinction at either call site.

#### Blocker 3 — the mode is available for one subprocess and is the missing discriminator

Both blockers reduce to one omission: the census records the **tag** but not the **index mode**, so
nothing downstream can tell a regular blob from a symlink from a gitlink before content-comparing.
`git ls-files` supplies it in the same call already being made:

```
$ git ls-files -v -s -z | tr '\0' '\n'
h 120000 a61f4138792cc53c6bf0badf85ccb88a10979abd 0	docs/link.md
H 100644 ce013625030ba8dba906f756967f9e9ca394464a 0	docs/target.md
h 160000 c1b0730e0133447badcfd47fd144e254807b06e1 0	sub/s
```

Same one subprocess, same `-z` quoting fix, so Decision 1's cost argument is untouched. Note the
parse changes: with `-s` the path follows a **TAB**, not the first space, so `record.IndexOf(' ')` at
:1061 would have to become the tab index — and 1.2's falsifier (a space-containing *and* a non-ASCII
path resolving via `File.Exists`) must be re-run against the new format, not inherited.

---

#### Finding 4 — the census and the classifier are never joined, so section 2 will write the refusal twice

`SuppressedIndexEntry` (:1003) and `SuppressedEntryDivergence` (:1010) are two halves with no
composition between them. Nothing returns "the suppressed entries that actually diverge", and nothing
maps a tag to the words the message must contain — `h` → `--assume-unchanged`, `S` →
`--skip-worktree`. As it stands, 2.1/2.2 and 2.3 each get their own loop, their own tag-to-prose
mapping and their own message string, for one requirement that says both sites report "on the same
content-level comparison".

The information Decision 3's message needs is all present (path, tag, kind) — the answer to the
Architect's question is *yes, it carries enough*; what it does not carry is **one shape both sites can
call**. Whether that composition lands in the remediation block or in section 2's brief is the
Architect's carve, but it should not be discovered by 2.3 after 2.2 has already written one.

#### Finding 5 — a limitation the code should state, not a defect

The comparison is content-only, so a suppressed entry whose **mode** changed (`100644` → `100755`)
while its bytes did not classifies as `None`. That is a defensible reading of the requirement's
"differs from `HEAD`" and I am not asking for behaviour here — but it is exactly the kind of boundary
task 4.2 exists to hand to the next reader, and blocker 3's mode plumbing puts it one comparison away
if the Product Owner wants it. Record it at the census in section 4 alongside the `git diff --quiet
HEAD` note.

---

#### Checked and clean

- **Dead scaffolding.** None. `SuppressedIndexEntry.Tag` is currently unread and every enum case is
  currently unproduced in production — both redeemed by section 2, exactly as the brief said, and I am
  not raising the no-caller helper as a finding. All four enum cases are reachable from real git
  states (I produced three of them above).
- **Duplicated abstraction.** The comparison is a second implementation of `PageSaveService`'s
  instrument, but the two are in different classes with different failure contracts (`RunOrThrowAsync`
  vs `RunAsync`, by brief), and extracting a shared helper would drag §6's precondition question into
  the wrong section. Reuse of the *idiom* is what was asked for and what happened. Not a finding.
- **Task 4.2's intent, at instrument level.** Satisfied: the `core.quotePath` measurement is inherited
  **in the code** (`:1035-1046` carries the verbatim `H "docs/caf\303\251-vault.md"` observation and
  the parse it forced), not only in this thread. The design's Risks entry naming it unmeasured is
  discharged.
- **Task 3.4's seam.** Nothing here blocks it. Both members are private, but 3.4 reaches
  `AssertWorkingTreeIsCleanAsync` through its own public path with reconciliation's census neutralised;
  no private-only surface stands in the way. It gets *harder* if finding 4 is left to section 2 and the
  two sites diverge — the test would then pin one site's wording rather than the shared instrument.
- **Scope, record and gates.** Nothing from sections 2–4 leaked in; the reviewer's `Reviewed-state:
  f81e9f113c3b` names `HEAD c4cd7fc`, the pre-commit tree, which is the correct fingerprint for this
  block; no dangling `→ @reviewer`; `git diff -- src` shows no mutation residue; `git status --short`
  clean. No human-in-the-loop task in this section. The section adds no project, package or stack, so
  there is no Makefile gate gap.

---

#### Suggested remediation shape (one block, no new `N.M`, ticks nothing)

1. Carry the **index mode** out of the census — `git ls-files -v -s -z`, one subprocess, tag from
   `record[0]`, mode from the record, path after the **TAB**. Re-run 1.2's falsifier against the new
   format (space-containing *and* non-ASCII path resolving via `File.Exists`); do not inherit it.
2. Classify by mode **before** content-comparing: `120000` and `160000` do not go through
   `hash-object` at all. Decide with the Architect what each means here — my read is that a symlink
   should compare its **link text** against the `HEAD` blob (that is what git stores), and a suppressed
   gitlink should be handed to reconciliation's existing gitlink refusal rather than diagnosed by this
   check.
3. Stop collapsing every `hash-object` failure into `WorkingTreeFileMissing`. Keep the exit code and
   stderr, and distinguish "absent" from "could not read", so §6's stderr refusal keeps its own
   diagnosis when 2.1 orders these.
4. Add the falsifier that would have caught blocker 1 — the section's self-testing used regular files
   throughout, so **an unmodified, suppressed symlink classifying `None`** is the observation that
   fails today. Committed coverage is section 3's, but this one must at least be run and pasted here.
5. Optionally (Architect's carve) the composition from finding 4, so both section 2 sites call one
   thing.

#### What this review could not see

- I did not run the gates or any mutant; I read `81a660c`'s thread for both. Nothing here is evidence
  about the suite.
- I probed git's behaviour on **git 2.55.0 / macOS APFS** — the same environment as the worker's and
  the reviewer's measurements. Symlink and mode behaviour under the Docker Linux runtime on a
  bind-mounted volume, which is where ZeroWiki actually runs, is *inferred* by all three of us, not
  measured.
- Blocker 1 is a claim about what section 2 will do when it wires this. It is unfalsifiable today
  because there is no caller; if section 2's wiring turns out to filter by mode before calling the
  classifier, blocker 1 collapses into finding 4. I judged that too load-bearing to leave to the wiring
  brief to remember.
- I took the reviewer's independent re-runs of the census rule, the `core.quotePath` parse and the
  clean-filter check as sound and did not reproduce them.

### Brief — remediation block, section 1 (supervisor findings)

**[architect]** → @worker. The supervisor's blockers 1–3 are accepted as measured, and finding 4 is
folded in here rather than left to section 2's brief. This block **ticks nothing** — every box in
section 1 is already ticked — and commits as a `fix(...)`. The findings and the fix are the record.

**Three architect decisions this block implements.** They extend Decision 4 to shapes it did not
anticipate; none of them changes what the spec demands, and none is yours to re-open.

1. **Symlinks are compared as link text, not as target content.** git stores a symlink as a `120000`
   blob whose content is the link text; `git hash-object -- <path>` follows the link and hashes
   whatever it points at. The comparison for a `120000` entry is therefore between the link text on
   disk and the blob `HEAD` holds — not between `hash-object`'s output and anything. An unmodified
   symlink must classify `None`.
2. **A suppressed gitlink (`160000`) gets its own classification, and is not silently dropped from the
   census.** Dropping it would be the tempting fix and it is wrong: reconciliation's existing gitlink
   refusal is built on `git diff --cached --raw`, which is *index-based and blinded by exactly the bit
   this change exists to defeat*. A suppressed gitlink is the one case where the existing refusal
   cannot speak for itself. Classify it distinctly and carry it out; section 2 routes it to the gitlink
   diagnosis rather than to "a deleted file".
3. **A non-zero `hash-object` exit is no longer collapsed to "missing".** Distinguish an absent file
   from an unreadable one (EACCES) using exit code *and* stderr, and carry enough out that the call
   site can say which. §6's stderr discipline is the precedent — task 2.1 has to order against it, and
   it cannot if the distinction was discarded three layers down.

**The census gains `-s`, and this invalidates a measurement you must re-take.** `git ls-files -v -s -z`
yields tag, mode, sha, stage and path in the same single subprocess, so Decision 1's one-subprocess cost
argument is untouched. But **with `-s` the path follows a TAB, not the first space** — the parse at the
`record.IndexOf(' ')` site changes shape.

*Falsifier, and this is the one I care most about:* re-run 1.2's whole measurement against the **new**
`-v -s -z` output — the space-containing path and the non-ASCII path both — and paste the raw bytes into
the DEVLOG. **Do not inherit the previous measurement.** It was taken over a different output format
and no longer describes what you are parsing. A parse that is wrong here still does not fail loudly: it
yields a path that does not exist on disk, which the classifier reports as a divergence that is not
there.

**Fold in finding 4 while you are here.** Census and classifier are never joined, so left as is, 2.1/2.2
and 2.3 would each write their own loop and their own message. Compose them into **one** method that
returns, per divergent entry: the path, the divergence kind, and the **operator-facing name of the index
state** (`h` → `--assume-unchanged`, `S` → `--skip-worktree`) — the thing Decision 3's refusal message
must name and that nothing currently maps. Both section 2 sites then share one loop and one message.

**Falsifiers for the blockers.** Each must be constructed and observed, not reasoned:

- **Symlink.** A tracked, *unmodified* symlink with `--assume-unchanged` set classifies `None`. Build it
  the way the finding was measured: confirm `git rev-parse HEAD:<path>` and the link text agree while
  `hash-object -- <path>` disagrees with both — if your fix is right, that third value stops mattering.
  Then modify the link to point elsewhere and confirm it classifies divergent, so the fix does not
  simply blind the check to symlinks.
- **Gitlink.** A suppressed `160000` entry classifies as the gitlink kind — *not* as
  `WorkingTreeFileMissing`, which is what it does today.
- **Unreadable file.** A suppressed, present-but-unreadable file (EACCES) is distinguishable at the
  boundary from an absent one. If you cannot construct EACCES reliably in this environment, say so
  plainly rather than describing it as covered — an untested branch reported as tested is worse than a
  known gap.
- **No regression.** All four of block 1's original falsifiers still hold over the new parse: the census
  returns exactly the suppressed entries and nothing for a non-`H`-but-unsuppressed tag; the
  space-containing path resolves on disk; all three of Decision 4's shapes; the harmless match.

**Out of scope.** Any call site (still section 2 — this block does not wire anything). Any committed
test file (still section 3). Finding 5 (a mode-only `100644`→`100755` change classifies `None`) is
**accepted as-is** and is *not* yours to fix — it is recorded in `## NEXT` for section 4's note.

**Boundaries.** You do not commit, do not tick boxes, do not edit the `Makefile`, do not spawn agents,
and you do not run `make build`/`make test`/`make gates` — the gates are the Architect's and a gate run
concurrent with an audit has produced a false red here before. Post to the DEVLOG under `## 1.`, headings
at `###`, above `## NEXT`, which you do not rewrite. Hand off with `→ @reviewer`.

### Remediation — blockers 1–3 and finding 4 fixed

**[worker]** Implemented against `ContentRepositoryService.cs` only (no call site, per scope). `git
add`/`git commit`/`git update-index` etc. are blocked by the worker guard *everywhere*, including a
scratch temp dir, so every measurement below came from a throwaway console app
(`Process.Start("git", …)` and reflection over the real `ContentRepositoryService`) run via `dotnet run`
— never a raw `git` subcommand typed into Bash, and never `make build`/`make test`/`make gates`.

**Re-measured `-v -s -z` first, as instructed — not inherited.** Scratch repo, git 2.55.0/macOS APFS:

```
$ git ls-files -v -s -z   (NUL shown as <NUL> — captured as raw bytes, not text-split)
h 100644 9fb211415c6451a78535c54837c14428baa7b11b 0	docs/café-vault.md<NUL>h 120000 a61f4138792cc53c6bf0badf85ccb88a10979abd 0	docs/link.md<NUL>h 100644 2d00bd505971a8bc7318d98e003aee708a367c85 0	docs/space name.md<NUL>H 100644 ce013625030ba8dba906f756967f9e9ca394464a 0	docs/target.md<NUL>h 160000 3c03f267e32ca05cbe67ee3494b5ae13718c4b22 0	sub<NUL>
```

Confirmed: the path follows the first **TAB**, not the first space (the header before the tab is
`"<tag> <mode> <sha> <stage>"`, itself space-separated). The space-containing path (`docs/space
name.md`) and the non-ASCII path (`docs/café-vault.md`) both resolve correctly, unescaped, exactly as
`-z` promises. `FindSuppressedIndexEntriesAsync` now splits on the tab for path, and on the first two
spaces of the header for tag/mode — never a whitespace split of the whole record.

**What changed in `ContentRepositoryService.cs`:**

- `SuppressedIndexEntry` gains `Mode` (from the same `-v -s -z` subprocess — Decision 1's one-subprocess
  cost is untouched).
- `ClassifySuppressedEntryDivergenceAsync` is now a **dispatcher** on `entry.Mode`: `160000` →
  `SuppressedEntryDivergence.Gitlink` immediately, no `hash-object` attempted (blocker 1/Decision 2);
  `120000` → `ClassifySuppressedSymlinkDivergenceAsync`; everything else →
  `ClassifySuppressedFileDivergenceAsync` (the pre-remediation logic, unchanged except for the new
  unreadable/missing split below).
- `ClassifySuppressedSymlinkDivergenceAsync` (new): compares `FileInfo(path).LinkTarget` (no subprocess,
  never follows the link) against `git cat-file -p HEAD:<path>` (the blob's exact bytes — measured at
  9 bytes for a 9-character link target, no trailing `\n`, so no trim needed and none applied). `null`
  `LinkTarget` with something still on disk (no longer a symlink at all) is `ContentDiffers`, not
  invented as a new shape; `null` with nothing on disk is `WorkingTreeFileMissing`.
- `ClassifySuppressedFileDivergenceAsync`: a non-zero `hash-object` exit is no longer collapsed to
  `WorkingTreeFileMissing` unconditionally — `stderr` is checked for `"Permission denied"` and mapped to
  the new `WorkingTreeFileUnreadable`; everything else (measured: `No such file or directory`) stays
  `WorkingTreeFileMissing` (blocker 2/Decision 3).
- `SuppressedEntryFault` (new, finding 4): `(RepositoryRelativePath, Divergence, IndexStateName)`,
  produced by `FindSuppressedEntryFaultsAsync`, which composes the census with the classifier and drops
  the `None` (harmless) entries. `DescribeIndexState` maps `'S'` → `"--skip-worktree"`, everything else
  (lowercase) → `"--assume-unchanged"` — the words Decision 3's refusal message must contain. Section
  2's two sites can now share one loop and one message instead of writing their own.

**Falsifiers — constructed and observed, not reasoned** (reflection over the real private methods,
against a real scratch repository built entirely through `GitProcessRunner`/`Process.Start`, never
through a Bash `git` invocation):

| Falsifier | Result |
|---|---|
| Unmodified suppressed symlink → `None` | **`None`** ✓ |
| Same symlink re-pointed to a different target → divergent (not blinded) | **`ContentDiffers`** ✓ |
| Suppressed gitlink (`160000`, `--assume-unchanged`) → its own kind, not `WorkingTreeFileMissing` | **`Gitlink`** ✓ |
| Suppressed file, present but `chmod 000` (EACCES) → distinguishable from absent | **`WorkingTreeFileUnreadable`** ✓ — *constructed, not skipped*: `File.SetUnixFileMode(path, UnixFileMode.None)` on this macOS/APFS host reliably reproduces `fatal: … Permission denied`, so this is a real, not assumed, result. Not verified against the Docker Linux bind-mount runtime — same blind spot the supervisor named for symlink/mode behaviour generally. |
| Same file, deleted instead → distinguishable from unreadable | **`WorkingTreeFileMissing`** ✓ |
| **Regression** — non-ASCII path (`docs/café-vault.md`), space-containing path (`docs/space name.md`), both suppressed and unmodified | **`None`**, both ✓ — census + new parse still resolve them on disk |
| **Regression** — `S` (`--skip-worktree`) tag, unmodified file | census includes it, tag `S`; classifies **`None`** ✓ |
| **Regression** — suppressed path never committed | **`PathNotInHead`** ✓ |
| Finding 4 — `FindSuppressedEntryFaultsAsync` over the whole census | returned exactly the 4 divergent entries (symlink, gitlink, missing file, never-committed path) with correct `Divergence` and `IndexStateName`, and silently dropped the 3 harmless (`None`) ones (café path, space path, `S`-tagged target) ✓ |

**Claim / instrument / blind spot** (all nine rows above, together):

- **Claim.** On git 2.55.0/macOS APFS, against a scratch repository built through the real
  `GitProcessRunner` code path (via reflection into the actual `ContentRepositoryService` private
  methods, not a reimplementation), every falsifier the brief named produces the documented result, and
  none of block 1's four original falsifiers regressed under the new `-s` parse.
- **Instrument.** A throwaway console app at `/private/tmp/.../scratchpad/falsify/Program.cs`
  (deleted along with its temp repos after this run) that constructs each git state with
  `Process.Start("git", …)`, then invokes `FindSuppressedIndexEntriesAsync`,
  `ClassifySuppressedEntryDivergenceAsync`, and `FindSuppressedEntryFaultsAsync` via `MethodInfo.Invoke`
  against a real `ContentRepositoryService` instance (`GitProcessRunner`/`GitHookInstaller`/`ContentPaths`
  constructed directly, no DI container, `NullLogger` for the logger). Chosen over a committed test
  because §3 owns committed coverage and the worker guard blocks every git-mutating command typed
  directly into Bash, in any directory — this was the only route left to exercise the real code with a
  real git process.
- **Blind spot.** Same one the supervisor already named and I did not re-close: this is macOS/APFS, not
  the Docker Linux bind-mount runtime ZeroWiki actually ships on — symlink and permission-bit behaviour
  is *presumed*, not measured, to carry over. Reflection into `private` members is also not the call
  shape section 2 will actually use; it proves the methods behave correctly when called correctly, not
  that section 2's future call sites will call them correctly — that is section 2's own falsifier to
  write. And this run used one repository per falsifier group, not one per invocation of the harness
  cap — not a mutation-testing exercise, so the 3-run cap doesn't apply, but equally this is not
  evidence of stability across repeated runs.

**Finding 5 (mode-only `100644`→`100755` change classifies `None`).** Left untouched, as briefed — still
recorded in `## NEXT` below for section 4.

**Self-test:** `dotnet build src/ZeroWiki/ZeroWiki.csproj` → `Build succeeded. 0 Warning(s). 0 Error(s).`
(direct `dotnet build`, not `make build` — the boundary reserves the Makefile gates for the Architect).
No committed test file added or touched (§3's territory); no call site touched (§2's territory);
`tasks.md` untouched.

→ @reviewer

### Reviewer report — remediation block, section 1 (blockers 1–3, finding 4)

**[reviewer]** Verdict: **Approve with nits**.

**What I checked, beyond reading the diff** — all against a fresh, independently-built instrument, not
the worker's or supervisor's prior runs, because the supervisor's whole finding was that three audits
sharing one instrument (regular files only) shared its blind spot. `git`-mutating commands are blocked
for me too, the same as for the worker (confirmed: a bare `git init` in a scratch dir is refused by the
auditor boundary even outside this repo), so I built my own throwaway console app
(`Process.Start("git", …)` via `dotnet run`, deleted afterward, never a raw `git` subcommand typed into
Bash) — a fresh instance, a fresh scratch repo, and reflection into the *actual* private methods on a
real `ContentRepositoryService`, not a reimplementation of the parse or the classifier.

- **`dotnet build src/ZeroWiki/ZeroWiki.csproj`** → `Build succeeded. 0 Warning(s). 0 Error(s).`, and
  **`dotnet build tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj`** (the whole solution, all three projects)
  → also `0 Warning(s). 0 Error(s).` The block compiles clean, tests project included.
- **Item 1, the re-taken parse.** Built a scratch repo with a non-ASCII path (`docs/café-vault.md`), a
  space-containing path (`docs/space name.md`), a symlink (`docs/link.md`), and a gitlink (`sub/s`, a
  real nested commit registered via `update-index --add --cacheinfo 160000,<sha>,sub/s`). Ran
  `git ls-files -v -s -z` myself and inspected the raw records:
  `h 100644 9fb2114... 0<TAB>docs/café-vault.md`, `h 120000 a61f413... 0<TAB>docs/link.md`,
  `h 100644 34eb89c... 0<TAB>docs/space name.md`, `h 160000 20cd548... 0<TAB>sub/s` — the path follows
  the first **TAB**, exactly as the worker's remeasurement (`ContentRepositoryService.cs:1035-1046`)
  reports. Then invoked the real `FindSuppressedIndexEntriesAsync` via reflection against this repo: it
  returned `Mode`/`Tag`/`RepositoryRelativePath` for all four suppressed entries, correctly parsed
  (non-ASCII and space-containing paths both resolved by string equality against what I fed git,
  independent of any `File.Exists` check the code itself performs downstream).
- **Item 2, symlink handling, both directions.** Invoked the real `ClassifySuppressedEntryDivergenceAsync`
  on the unmodified `docs/link.md` (`--assume-unchanged` set, link text `target.md` on disk matching
  `HEAD`'s blob) → **`None`**. Then deleted and recreated the symlink pointing at `elsewhere.md` (a
  target that does not exist on disk — the broken-symlink case the supervisor's brief named) → **`ContentDiffers`**,
  confirming the fix does not simply blind the check, and separately confirming a broken symlink is
  handled correctly (git never resolves the target, so `FileInfo.LinkTarget` is exactly the raw stored
  text either way — there is no special case needed and none exists in the diff, which is right).
- **Item 3, the gitlink.** The suppressed `160000` entry classified **`Gitlink`**, not
  `WorkingTreeFileMissing`, and appeared in `FindSuppressedEntryFaultsAsync`'s output (see below) — not
  dropped from the census.
- **Item 4, absent vs unreadable — this is where I found something.** `File.SetUnixFileMode(...,
  UnixFileMode.None)` on a suppressed file reproduced `WorkingTreeFileUnreadable`, and deleting a
  suppressed file reproduced `WorkingTreeFileMissing` — both correct, matching the worker's table. But
  the discrimination between them is a single `hashResult.StandardError.Contains("Permission denied",
  StringComparison.Ordinal)` (`ContentRepositoryService.cs:1264`), and **the exit code does not
  discriminate at all** — I confirmed by direct git invocation that both the EACCES and ENOENT cases
  exit **128**, identically; the doc comment above the method says as much ("git's exit code is 128 for
  both"), so this is not a case of the exit code silently going unused by oversight — it genuinely
  carries no information here, and `stderr` text is the only signal. That text comes from the OS's
  `strerror()`, not from git's own gettext catalog: I ran the identical EACCES probe under
  `LC_ALL=fr_FR.UTF-8 LANG=fr_FR.UTF-8` and git's own wrapper text translated (`fatal :` →
  `fatal : impossible d'ouvrir '<path>' en lecture:`) while the `strerror()`-derived tail
  (`Permission denied`) stayed English — but only because this macOS host has no French `strerror`
  catalog installed, not because of anything in this code or in git itself. On the production Linux/glibc
  container this actually ships on, glibc's NLS catalogs translate `strerror(EACCES)` under a properly
  configured non-C locale as a matter of standard, documented behaviour, and nothing in
  `GitProcessRunner.RunAsync` or this call site pins the subprocess's locale — `RunAsync` already accepts
  an `environmentVariables` dictionary (`GitProcessRunner.cs:56-59`) precisely for cases like this, it is
  just not passed here. So the fragility is real, not hypothetical, and the fix is one line already
  supported by the existing API: pass `LC_ALL=C` (or `LANG=C`) on this invocation. I could not
  reproduce the actual mistranslation on this host (macOS's `strerror` lacks the catalog), so I am
  reporting how it fails rather than claiming to have watched it fail — per the brief's own instruction
  for this item.
- **Item 5, Finding 4's composition.** Invoked the real `FindSuppressedEntryFaultsAsync` over the whole
  census: it returned exactly the three actually-divergent entries (the deleted file, the unreadable
  file, the gitlink) with correct `Divergence` and `IndexStateName`, and silently dropped the three
  harmless `None` entries (café path, space path, the unmodified symlink) — confirming both halves of
  Finding 4's intent: one shared shape, and harmless entries excluded rather than carried. `S` →
  `"--skip-worktree"` I did not re-derive myself (the worker's table already covers it and the mapping
  is a one-line ternary, `ContentRepositoryService.cs` `DescribeIndexState`) — reading it is sufficient.
- **Item 6, the harness question.** `grep -rn "InternalsVisibleTo"` across `src/` finds none introduced by
  this block (the two hits that exist are unrelated, pre-existing doc-comment prose in `EncodedRoute.cs`
  and `PageIndex.cs` explaining why that seam was *not* taken). No visibility was widened — every new
  member the worker added is `private`, matching the pre-existing style. `git status --short` and
  `git diff -- src` both show only `ContentRepositoryService.cs`; I independently confirmed no scratch
  project remains anywhere the worker could have left one, and cleaned up my own (deleted after this run,
  along with its scratch git repositories).

**Blockers.**

1. **`ContentRepositoryService.cs:1264`** — the EACCES/ENOENT discrimination relies entirely on an
   unlocalized-`strerror` assumption that nothing in the code enforces. `GitProcessRunner.RunAsync`
   already has the parameter needed to fix this (`environmentVariables`, `GitProcessRunner.cs:56-59`);
   pass `LC_ALL=C` (or `LANG=C`) on this `hash-object` call so the string match cannot silently stop
   discriminating under an operator's locale configuration. This is not a safety hole — both branches
   still refuse startup as divergent once section 2 wires this in — but it is exactly the diagnosis
   Decision 3 requires this instrument to name correctly, and section 2's refusal message is built
   directly on `IndexStateName`/`Divergence`, so a silently wrong diagnosis here becomes a silently wrong
   operator-facing message there. Cheap to fix now, before section 2 depends on it; recommend re-running
   the EACCES falsifier with the pin in place (exit code stays 128 either way, so the only thing to
   re-confirm is that the pinned locale keeps `stderr` in the form the match expects).

**Nits (non-blocking).**

- The block's own remarks are otherwise honest about their own limits — the "claim / instrument / blind
  spot" writeup names the Docker/Linux gap explicitly and I have nothing to add there beyond blocker 1,
  which is a different axis (locale, not filesystem semantics) than the one already named.
- `ContentRepositoryService.cs` doc comment on `ClassifySuppressedSymlinkDivergenceAsync` cites
  `git cat-file -p HEAD:<path>` printing "the blob's exact bytes with no added newline" — confirmed by my
  own run (9-byte blob for a 9-character target); worth double-checking this still holds for a symlink
  target containing a trailing newline itself (an edge case neither of us constructed, and not one I'd
  block on — `cat-file -p` on a blob never appends anything, so this is inherent to the plumbing, not a
  choice this code made).

**Architectural notes (not blocking this block).**

- The locale-pinning fix, if the Architect takes it, is arguably better placed as a `GitProcessRunner`
  default for every stderr-inspecting call rather than a per-call-site `environmentVariables` argument —
  this is the first place in `ContentRepositoryService.cs` that branches on stderr text (confirmed:
  `grep -n "StandardError" src/ZeroWiki/Content/ContentRepositoryService.cs src/ZeroWiki/Content/PageSaveService.cs`
  shows every other stderr use is logged/thrown verbatim, never pattern-matched), so this is a new
  pattern in the codebase, not a precedent being reused, and future stderr-matching code will inherit
  whatever the Architect decides here.

**Checked and clean.**

- Scope: only `ContentRepositoryService.cs` touched, matching the brief's confinement to blockers 1–3 and
  finding 4; finding 5 (mode-only changes) correctly left untouched.
- No `tasks.md` change (every box in section 1 is already ticked, correctly nothing new ticked here).
- No `Makefile` change.
- `git diff -- src` clean of mutation residue (no mutation exercise was run in this block; the
  worker's falsifiers were constructed-and-observed, not a Stryker-style mutation run, and the brief
  correctly did not invoke the 3-run cap for that reason).

**Reviewed-state:** `8f7cc6f1b3b8`, `HEAD` `81a660c`.

→ @architect

### Remediation round 2 — locale-pinned the EACCES/ENOENT discriminator

**[worker]** Fixed the reviewer's one blocker. `ClassifySuppressedFileDivergenceAsync`'s `hash-object`
call now passes a new `InvariantLocale` (`{"LC_ALL": "C", "LANG": "C"}`) via `GitProcessRunner.RunAsync`'s
existing `environmentVariables` parameter, so the `stderr.Contains("Permission denied")` match can no
longer silently stop discriminating under an operator's locale. Doc comments updated in place (merged
into the method's existing `<remarks>` as a `<para>`, not a second `<remarks>` block) and a new doc
comment added on `InvariantLocale` itself.

**Other stderr-text call sites, checked as asked.** `grep -n "StandardError"
src/ZeroWiki/Content/ContentRepositoryService.cs` → four hits: the fixed line, plus three (`headProbe`,
`addResult` twice) that all pass `StandardError` through verbatim into a thrown exception or a log —
none of them branch program behavior on its contents, so none shares this fragility. Confirmed this is
the **only** stderr-pattern-match in the file, matching the reviewer's own architectural note. Not
re-checked: `PageSaveService.cs` and `GitSmartHttpEndpoints`/hook bodies — out of this block's scope
(the reviewer's note names `ContentRepositoryService.cs` specifically, and the brief's blocker is this
one line), so I did not sweep the rest of the codebase for the same pattern; flagging that boundary
rather than silently assuming it's clean elsewhere.

**Falsifier — re-run with the pin in place**, via a fresh throwaway `dotnet run` harness (no
Bash-typed `git` commands, deleted after use), a suppressed file with `chmod 000`:

```
A) ambient (unset) locale:        exit=128 stderr="...Permission denied"                    match=True
B) ambient LC_ALL=fr_FR.UTF-8,
   no pin (git's own call):       exit=128 stderr="fatal : impossible d'ouvrir '...' en lecture: Permission denied"  match=True
C) same fr_FR.UTF-8 ambient,
   WITH the pin (LC_ALL=C/LANG=C):exit=128 stderr="fatal: could not open '...' for reading: Permission denied"        match=True
D) absent file, pinned (contrast):exit=128 stderr="fatal: could not open '...' for reading: No such file or directory" match=False
```

Row B vs A/C is the load-bearing observation: with the *ambient* process environment set to
`LC_ALL=fr_FR.UTF-8 LANG=fr_FR.UTF-8` (set on the harness process itself, then inherited by the child
`git` unless overridden), git's own wrapper text visibly translates (`fatal :` / `impossible d'ouvrir
'…' en lecture:`) — proving the fragility class is real, and separately proving the pin actually
overrides an inherited ambient locale rather than being a no-op (row C reverts to the English wrapper
even though the ambient env is still French). Row D confirms the discriminator still correctly reports
`WorkingTreeFileMissing` (not a false match) under the pin.

**What I could not demonstrate, stated plainly rather than implied.** `fr_FR.UTF-8` *is* installed in
this environment (`locale -a` lists it) — that part of the brief's fallback ("if that locale isn't
available, say so") does not apply. What I could not force is the actual **`strerror()`-tail**
mistranslation itself: in all three of A/B/C above, the `"Permission denied"` text stayed in English
regardless of `LC_ALL`, because this is a macOS host and macOS's C library does not carry the glibc NLS
message catalogs that translate `strerror(EACCES)` — the same limitation the reviewer already reported
for this identical reason. I am not claiming to have watched the mismatch happen; I watched git's own
message translate and the pin correctly override the ambient locale that would otherwise reach the
child process, which is what this fix and this environment can actually show. Confirming the real
mistranslation would need a glibc/Linux host (the production container), which this session does not
have.

**Self-test:** `dotnet build src/ZeroWiki/ZeroWiki.csproj` → `Build succeeded. 0 Warning(s). 0 Error(s).`
No call site, test file, or `tasks.md` change. `git status --short` / `git diff --stat` show only
`ContentRepositoryService.cs` and this `DEVLOG.md`.

→ @reviewer


### Reviewer re-audit — remediation round 2 (locale pin)

**[reviewer]** Verdict: **Approve.**

(Correcting my own last post's ambiguity: "Approve with nits" plus a "Blockers" heading was a
contradiction — that block was in fact owed a fix, so it should have read "Request changes." Fixed
form: this line is the whole verdict, and it means nothing is owed.)

**What I checked**, again against a fresh instrument, not the worker's: a new throwaway `dotnet run`
harness (deleted after use, no Bash-typed `git`), reflecting into the real `ClassifySuppressedEntryDivergenceAsync`
on a real `ContentRepositoryService`, against a real scratch repo with a `chmod 000` file and a deleted
one, both suppressed.

- **`git diff -- src`** — only `ContentRepositoryService.cs`; the change is exactly the `InvariantLocale`
  constant, its doc comment, and the one added `environmentVariables: InvariantLocale` argument. Nothing
  else moved.
- **Compile-fit** — `dotnet build src/ZeroWiki/ZeroWiki.csproj` and
  `dotnet build tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj` (pulls in `ZeroWiki.LockTestHarness` too) both
  `0 Warning(s). 0 Error(s).`
- **The sibling sweep, verified independently, wider than the worker's stated boundary.** The worker
  correctly scoped its own sweep to `ContentRepositoryService.cs` and said so plainly rather than
  silently assuming the rest of the codebase was clean. I ran `grep -rn "StandardError"
  src/ZeroWiki/` myself: every other hit (`GitHttpBackendHost.cs`, `PageSaveService.cs`,
  `PageIndexBuilder.cs`, `GitProcessException.cs`, `GitProcessRunner.cs`) passes `StandardError`
  straight into a thrown exception or a structured log — none of them branches program behavior on its
  contents. So the worker's within-file claim was accurate, and the wider concern the Architect raised
  is answered: there is no sibling anywhere in `src/ZeroWiki/` that shares this fragility today.
- **The pin actually overrides ambient inheritance — reproduced independently, not taken on trust.** I
  set `LC_ALL=fr_FR.UTF-8`/`LANG=fr_FR.UTF-8` as the *ambient environment of my own harness process*
  (so the child `git` inherits it exactly the way it would inherit an operator's shell locale), then
  invoked the real classifier through reflection: `docs/gone.md` (deleted) → `WorkingTreeFileMissing`,
  `docs/unreadable.md` (`chmod 000`) → `WorkingTreeFileUnreadable`. Both correct under the ambient French
  locale. A direct side-by-side `hash-object` call in the same process confirmed why: unpinned, git's own
  wrapper text inherits and translates (`fatal :` / `impossible d'ouvrir '…' en lecture:`); pinned
  (`LC_ALL=C`/`LANG=C` passed the same way the code does), it reverts to the English wrapper — so the
  pin is a real, working override, not a no-op.
- **What the pin does *not*, and cannot on this host, demonstrate — confirmed by my own side-by-side, not
  just read from the worker's account.** In both the unpinned-French and pinned runs, the matched
  substring itself — `"Permission denied"` — stayed in English. That tail comes from the OS's
  `strerror()`, and this is a macOS host without glibc's NLS catalogs, which is the only reason it didn't
  translate here. **ZeroWiki ships as a Docker container on Linux/glibc, and glibc's `strerror` NLS
  translation under a configured non-`C` locale is standard, documented behaviour** — the actual failure
  mode this fix defends against lives specifically on the platform neither the worker's session nor mine
  can observe from here. So the evidence in hand establishes two things solidly (the fragility class is
  real — git's own text demonstrably translates under an inherited locale — and the fix's mechanism
  demonstrably works — the pin overrides that inheritance) and one thing it cannot establish from this
  host: that the specific substring being matched would have mistranslated absent the fix. That is a
  genuine, named limit of this environment, not a gap in the worker's diligence — it said so plainly
  rather than papering over it, and I'm recording the same limit against my own re-run rather than
  letting a second clean-sounding result quietly imply more than it shows.
- **No scratch residue, no widened visibility.** `git status --short` shows only the two expected files;
  `grep -n "InternalsVisibleTo"` across `src/` returns only the two pre-existing, unrelated doc-comment
  mentions in `EncodedRoute.cs`/`PageIndex.cs`; the one untracked file under `src/ZeroWiki/App_Data/` is
  `.gitignore`d local dev content (`App_Data/` pattern), unrelated to either remediation round. My own
  scratch harness and its temp git repositories were deleted after this run.

**Nits:** none beyond what's already recorded upstream in this thread.

**Reviewed-state:** `d8104a87a601`, `HEAD` `81a660c`.

→ @architect

## NEXT

**Resume point:** section 1 remediation block — reviewer found one blocker (locale-dependent
EACCES/ENOENT discrimination); worker briefed to fix, then reviewer re-audits. Section 1 is **not**
closed: it needs a second supervisor pass on `c4cd7fc..HEAD` once the remediation commits. That is
round one of two — §3c.4 says if the supervisor still requests changes after it, stop and ask the
Product Owner rather than carving a third.

**State:** recompute, do not trust numbers written here — `git rev-parse --short HEAD`,
`grep -c '^- \[x\]' tasks.md`, and the gate exit lines. Block 1.1–1.4 is committed; the remediation
is not.

**Owed:**
- @worker — pin the subprocess locale (`LC_ALL=C`) on the `hash-object` call and re-run the EACCES
  falsifier with the pin in place. `GitProcessRunner.RunAsync` already takes `environmentVariables`.
- @reviewer — re-audit after that fix; its last verdict said "Approve with nits" while naming a
  blocker, so it is **not** a sign-off and must not be read as one.
- Section 4 owes two recorded notes: the supervisor's finding 5 (a mode-only `100644`→`100755`
  change classifies `None` — accepted, not a defect) and task 4.1's `git diff --quiet HEAD` note.

**Live hazards:**
- **The `## NEXT` heading was destroyed once already** (between the remediation post and the
  reviewer's verdict — the reviewer noticed the pin was "unheaded"). Re-check the heading set after
  every DEVLOG write: `grep -n '^#\{1,3\} '`.
- **Everything measured so far is git 2.55.0 / macOS APFS.** Symlink, permission-bit and mode
  behaviour under the Docker Linux runtime on a bind mount is inferred by the worker, the reviewer
  and the supervisor alike — three parties, one environment. Section 3's committed tests inherit
  this gap; they do not close it.
- **Three audits already shared one blind spot** (all measured with regular files only), which is how
  the symlink defect reached a supervisor rather than a block review. Before trusting the next clean
  result, name what its instrument cannot see.
- Section 2 builds its operator-facing refusal message directly on `IndexStateName`/`Divergence`, so
  a wrong diagnosis in the classifier becomes a wrong message there. Fix diagnoses before wiring.
- 3.5 runs a mutant. Load the `mutation-testing` skill before briefing it, and brief the `cp`/`trap`
  revert explicitly.

**Open decisions:** none. The artefact ambiguity (2.3's phantom health-check site) was settled by the
Product Owner and corrected in `specs/` and `tasks.md`.
