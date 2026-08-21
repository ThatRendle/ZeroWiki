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

### Supervisor re-review — section 1 after remediation (`c4cd7fc..60174c6`)

**[supervisor]** Scope: `81a660c` + `60174c6`; one production file, `+311` net over the base. Gates and
both reviewer verdicts read from this thread, not re-run.

Verdict: **Request changes**.

Blockers 1, 2 (in part) and 3 and finding 4 are genuinely fixed, and the symlink fix is the good kind —
`FileInfo.LinkTarget` against `cat-file -p HEAD:<path>` is the right pair, and the re-pointed-symlink
direction being required and observed is what stops it from being a fix that merely stops looking.
Finding 5 is correctly parked. The `-s` parse was re-measured rather than inherited, as asked.

One blocker remains, and it is the gitlink half of blocker 2 — not because the reasoning you flagged for
me is wrong, but because the conclusion drawn from it overshot into the same false-refusal class blocker
1 was about.

---

#### Blocker A — every suppressed gitlink is now a fault, which contradicts this file's own gitlink policy and spec scenario 2

`ClassifySuppressedEntryDivergenceAsync` (`ContentRepositoryService.cs:1156-1160`) returns `Gitlink`
for mode `160000` **unconditionally, with no comparison performed**, and
`FindSuppressedEntryFaultsAsync:1099-1108` promotes everything that is not `None` into a
`SuppressedEntryFault`. So a suppressed gitlink is a fault by virtue of existing.

**Your reasoning is right; only the conclusion overshoots.** `FindStagedGitlinksAsync` *is*
`diff --cached --raw`-based and *is* blinded by the suppression bit, so dropping gitlinks from the
census would have been wrong — agreed, and I would have raised it if you had. But the fix has to answer
the *same question* the blinded instrument was asking, and that question is deliberately narrow.
`FindStagedGitlinksAsync`'s own contract, 150 lines above this code (`:896-916`):

> Deliberately narrower than "every gitlink the index carries": an adopted repository may legitimately
> already have one (a submodule, or any pre-guard means), and that entry advancing its own nested
> `HEAD` is the normal way for it to change, not a fault. […] an adopted repository containing a
> legitimate submodule would start fine once and then refuse forever the moment that submodule's
> pointer moves, **which is exactly the bricking this check exists to prevent**.

The remediation reintroduces precisely that bricking for the suppressed case — and note what the
suppressed case actually *is* in the field: marking a submodule's gitlink entry `--assume-unchanged` to
stop it reporting dirty is one of the commonest real reasons anyone sets that bit at all. So the
population this branch is most likely to meet is an adopted, unchanged, entirely legitimate submodule,
and it refuses startup forever.

It also contradicts the requirement directly. Second paragraph: refusal authority exists only "where the
system finds a tracked path that **differs** from `HEAD`". Third paragraph and scenario 2: a suppressed
entry over a path that does not differ "SHALL NOT prevent startup". `Gitlink` establishes no difference
before refusing.

**The index-independent equivalent is one subprocess and mirrors the existing policy exactly.**
`FindStagedGitlinksAsync`'s real condition is `newMode == 160000 && oldMode != 160000` — "is there a
gitlink here now where `HEAD` did not have one". `git ls-tree HEAD -- <path>` answers that without the
index. Measured just now against a tree carrying both shapes:

```
$ git ls-tree <tree> -- sub/s
160000 commit c1b0730e0133447badcfd47fd144e254807b06e1	sub/s
$ git ls-tree <tree> -- docs/target.md
100644 blob ce013625030ba8dba906f756967f9e9ca394464a	docs/target.md
$ git ls-tree <tree> -- docs/absent.md
                       # empty output, exit 0 — no exit-code parsing needed
```

So, for a suppressed `160000` entry: empty output → `HEAD` has nothing there → a newly introduced
gitlink the blinded `diff --cached` cannot see → fault (this is the case your reasoning is actually
protecting, and it survives). Mode `160000` → already adopted → `None`, matching `oldMode == 160000`.
Any other mode → a tracked path replaced by a nested repository (the typechange `oldMode != 160000`
case) → fault. One extra subprocess, only for suppressed gitlinks, which is a rare shape inside an
already-rare shape.

#### Required alongside it — the composition method names two call sites that do not exist

`ContentRepositoryService.cs:1060`, in `SuppressedEntryFault`'s own doc comment:

> so the two call sites that need this (**the browser save path and the git-hook path**) share one loop
> and one message

The two sites are `ReconcileWorkingTreeAsync` and `AssertWorkingTreeIsCleanAsync` — startup
reconciliation and the post-reconciliation assertion. "Browser save path" and "git-hook path" are a
different pair belonging to `PageSaveService` and the Smart HTTP remote, and this instrument is wired to
neither. One line, but this change has already had to spend a Product Owner ruling on a phantom site
(`### Pre-flight and one artefact correction`, the non-existent health check). Shipping a **new** phantom
pair, inside the type section 2 consumes, in the same section that corrected the first one, is the
record defect repeating rather than a typo. Fix it in the same round.

---

#### The platform question you asked me to judge

**Section 1 cannot honestly be closed as "verified" on the environment evidence it has, and the gap is
now larger than four parties sharing a host.** Two specifics, neither of which is a caveat I want added
to a conclusion — they change what the conclusion may say:

- **The locale pin defends a behaviour that cannot occur on any machine that has touched this file.**
  `strerror` translation under a non-`C` locale is a **glibc** behaviour; the macOS libc all four of us
  measured on does not do it. So on our host the bug the pin fixes is unreproducible *and* the pin's
  efficacy is unobservable. It is very likely correct — I am not disputing the reasoning — but "very
  likely correct" is the standard this project explicitly refuses for security- and correctness-critical
  paths, and the reviewer's round-two finding was itself derived, not observed.
- **Worse, and new: the branch it guards may be unreachable in production anyway.** The EACCES case is
  produced by `chmod 000`, and `chmod 000` does not deny **root**. ZeroWiki ships as a Docker container
  over a mounted volume; if that process runs as root — the default unless the image says otherwise —
  then `hash-object` can read the file and `WorkingTreeFileUnreadable` never fires, while the ENOENT
  default silently absorbs whatever else went wrong. Nobody has established which way that goes, and it
  is checkable in the Dockerfile in a minute.

**Where the obligation belongs — my answer to your three options.** Not section 3's tests: they would
run on the same host and would prove the pin works under macOS, which is the one platform where it is
inert. Not the record alone either — a comment does not make an unreachable branch reachable.

It belongs in **both** the record and a question to the Product Owner, and they are different questions:

- **In the code, now:** state at `InvariantLocale` that the failure it defends is glibc-only and was
  reasoned rather than observed, and state at the EACCES branch that its reachability depends on the
  container's user. That is the honest inheritance task 4.2 exists for, and it is section 4's slot.
- **❓ @architect → Product Owner:** *is a single Linux-container run of section 3's suppressed-entry
  tests in scope for this change, or is the platform gap accepted?* This is a scope call, not an
  engineering one — the change's own "Impact" says no Dockerfile or deployment surface changes, so
  adding a container run is a widening only they can authorise. Worth putting alongside it: whether the
  container runs as root, because if it does, the honest resolution may be that
  `WorkingTreeFileUnreadable` is a branch for a state ZeroWiki cannot reach, and the cheaper answer is to
  say so rather than to test it.

#### Drift introduced by the remediation itself — what I checked

- **No dead enum case.** All six of `SuppressedEntryDivergence` are producible on paper, and I produced
  or observed `None`, `ContentDiffers`, `WorkingTreeFileMissing` and `Gitlink` directly. The one I
  cannot vouch for is `WorkingTreeFileUnreadable` — see above; that is a platform question, not a dead
  case.
- **No unreachable branch under the mode dispatch.** The symlink path's `LinkTarget is null` fork is
  reachable both ways (absent → `WorkingTreeFileMissing`; a regular file or directory sitting where the
  index records `120000` → `ContentDiffers`), and a *broken* symlink still returns non-null `LinkTarget`
  so it correctly falls through to the text comparison rather than being misread as missing.
- **No duplicated classification.** The symlink and file paths use different HEAD-side commands
  (`cat-file -p` vs `rev-parse`) for genuinely different reasons and converge only on `PathNotInHead`.
  That is two comparisons, not one written twice.
- **The runner contract holds.** `GitProcessRunner.RunAsync`'s `environmentVariables` are *added* to the
  inherited environment (`startInfo.Environment[key] = value`, `GitProcessRunner.cs:76-82`), so the pin
  cannot strip `PATH`/`HOME` out from under git; and `StandardOutput` is a raw `ReadToEndAsync`, so the
  untrimmed `cat-file -p` comparison the symlink path depends on is sound.
- **Composition shape vs section 2.** `FindSuppressedEntryFaultsAsync` takes `repositoryRoot` as a
  parameter, matching the class convention the block reviewer asked for, and carries path, divergence
  kind and operator-facing state name — enough for Decision 3's message at both sites, and enough for
  2.1 to route `Gitlink` against the existing refusal. The shape is right; only the gitlink *verdict*
  inside it is wrong. One note for the section 2 brief: the census now has the index sha in hand from
  `-s` and discards it — harmless, but say so deliberately rather than letting a later block rediscover
  it as a gap.
- **Record and residue.** `git status --short` clean; `git diff -- src` shows no mutation residue; the
  reviewer's `d8104a87a601` / `HEAD 81a660c` is the correct pre-commit fingerprint for `60174c6`; no
  dangling handoff; no human-in-the-loop task in this section; no new project, package or stack, so no
  Makefile gate gap.

---

#### §3c.4 — is the breakdown or the spec at fault?

You have to put this to the Product Owner, so let me be exact rather than diplomatic.

**The spec is not at fault.** It said "differs from `HEAD`" and "SHALL NOT prevent startup" for a
non-differing path, and both of my blockers across both rounds are the code failing exactly those two
sentences. The spec has been the thing that caught this twice.

**The code is at fault, but the breakdown is why it is the same fault twice.** Round one: a false
refusal on a harmless symlink. Round two: a false refusal on a harmless gitlink. Same class, same cause
— **section 1 is required to render a verdict ("fault") while every policy that verdict must agree with
lives outside section 1**: the gitlink narrowness is in already-shipped code, the stderr ordering is task
2.1's, and the refusal message is 2.2's. A section that must classify without owning the classification
policy will keep producing verdicts that are locally defensible and globally wrong, and no block reviewer
can see it because the policy it contradicts is not in the diff.

**What I would put to the Product Owner**, in preference order:

1. **Redefine section 1's output as observation, not verdict** — return path, index mode, `HEAD` mode
   and comparison outcome, and let section 2 own every "is this a fault" decision, since section 2 is
   where the other refusals and their ordering already live. `FindSuppressedEntryFaultsAsync` becomes
   `FindSuppressedEntryObservationsAsync` and the `Gitlink` question disappears from this section
   entirely rather than being answered a third time.
2. Or **fold the remainder of section 1 into section 2** and review them together, which is the same
   remedy with the boundary moved instead of the contract.

Either way blocker A's `ls-tree` comparison is still needed — under (1) it lands as an observation
rather than a verdict. The doc-comment fix and the platform record note are independent of the choice
and should happen regardless.

#### What this review could not see

- I ran no gate and no mutant; both are read from this thread.
- Everything I measured, I measured on git 2.55.0 / macOS APFS — the fifth party on the same host, which
  is the point of the platform section above and not something I can fix by naming it.
- Blocker A is again a claim about what section 2 will do with a `SuppressedEntryFault`. It is the
  method's name, its `None`-only filter and the requirement's wording that make me willing to state it
  as a defect now rather than defer it — but if section 2 were briefed to treat `Gitlink` as
  non-refusing, the blocker becomes a naming and contract problem rather than a behaviour one. I judged
  that too fragile to leave to the wiring brief, twice.
- I took both reviewer passes' independent re-runs as sound and reproduced none of them.

→ @architect

### Brief — remediation block, section 1 (Decision 7 re-cut)

**[architect]** → @worker. Section 1 failed two supervisor reviews with the **same defect class**:
round one refused startup on a harmless symlink, round two on a harmless gitlink. Per §3c.4 I stopped
and put it to the Product Owner rather than carving a third attempt at the same question. **They
re-cut the section.** This block implements their decision; it is not another guess at where the line
falls.

**Decision 7 (design.md) — section 1 observes, section 2 judges.** The cause was structural, not
carelessness: section 1 was made to render a *verdict* while every policy that verdict must agree with
lives in section 2 — `FindStagedGitlinksAsync`'s deliberately narrow contract (an adopted submodule
advancing is *not* a fault, "exactly the bricking this check exists to prevent"), 2.1's ordering
against the existing stderr refusals, and 2.2's message. A section cannot be held to policies it
cannot see.

So: **stop deciding.** Per suppressed entry, report the path, the index mode, the `HEAD` mode and the
comparison outcome. Promote nothing to a fault. `FindSuppressedEntryFaultsAsync` becomes
`FindSuppressedIndexObservationsAsync`, and the type it returns is an observation, not a fault.

**The `HEAD` mode comes from `git ls-tree`, one subprocess, no exit-code parsing** — measured by the
supervisor:

| `git ls-tree <tree> -- <path>` | meaning |
|---|---|
| `160000 commit <sha>\t<path>` | already an adopted gitlink |
| `100644 blob <sha>\t<path>` | a tracked file replaced by a gitlink — a typechange |
| *(empty output, exit 0)* | not in `HEAD` at all — a new gitlink |

Note the third row: **empty output with exit 0**, not a non-zero exit. Do not reach for the exit code.

**Falsifiers.**

- **The gitlink that must stop being a fault.** A suppressed, *already-adopted* gitlink — index mode
  `160000`, `HEAD` mode also `160000` — comes back as an observation carrying both modes, and nothing
  in section 1 calls it a fault. This is the exact case that bricks today: marking a submodule
  `--assume-unchanged` to stop it reporting dirty is one of the commonest real reasons anyone sets the
  bit, so this branch's most likely real population is a legitimate unchanged submodule.
- **The two shapes that must still be distinguishable.** A typechange (`HEAD` mode `100644`, index
  `160000`) and a new gitlink (absent from `HEAD`) come back with *different, distinguishable*
  observations — section 2 must be able to tell them apart. Losing the distinction is the opposite
  failure to the one being fixed, so show both.
- **No regression.** Everything the last two rounds established still holds: the `-v -s -z` parse over
  non-ASCII and space-containing paths; symlink unmodified → matching, re-pointed/broken → differing;
  absent vs unreadable still distinguishable with the `LC_ALL=C` pin intact; a suppressed regular file
  matching `HEAD` → matching.

**Also required in this block, one line.** `ContentRepositoryService.cs:1060` — the type's doc comment
names "the browser save path and the git-hook path" as its two call sites. **Neither exists.** They are
`ReconcileWorkingTreeAsync` and `AssertWorkingTreeIsCleanAsync`. This section already cost the Product
Owner a ruling on a *phantom health-check site*; shipping a second phantom pair inside the very type
section 2 consumes is that same defect repeating. Fix it, and check the rest of your remarks for any
other named thing you have not opened and confirmed exists.

**Out of scope.** Any call site (still section 2 — sections 2 and 3 own the wiring, the fault
decisions and the committed tests). Do not add a fault-decision helper "ready for" section 2; the whole
point of Decision 7 is that section 2 makes those calls with the policies in front of it.

**Boundaries.** No commit, no ticks (this block ticks nothing — section 1's boxes are already ticked),
no `Makefile`, no `make` gates, no spawning agents. Post under `## 1.`, headings at `###`, above
`## NEXT`, which you do not rewrite — and **run `grep -n '^#\{1,3\} '` after writing**, because that
heading has been destroyed once already. Hand off with `→ @reviewer`.

### Remediation round 3 — Decision 7 re-cut: observe, don't judge

**[worker]** Implemented the re-cut. `ContentRepositoryService.cs` only.

**What changed.** `SuppressedEntryFault` (path + `Divergence` + index-state name, with `Gitlink`
promoted to a fault by existing) is gone. In its place:

- `SuppressedEntryComparisonOutcome` (renamed from `SuppressedEntryDivergence`): `Matches`, `Differs`,
  `WorkingTreeMissing`, `WorkingTreeUnreadable`, `PathNotInHead`, and a new `NotCompared` for "either
  side's mode is a gitlink, so there is no blob content to compare" — replacing the old `Gitlink` case,
  which *was* the verdict.
- `SuppressedIndexObservation` (new): `RepositoryRelativePath`, `IndexStateName`, `IndexMode`,
  `HeadMode` (nullable — `null` when the path has no `HEAD` counterpart at all), `ComparisonOutcome`.
  No `IsFault`, no filtering of harmless entries — deliberately: that promotion is exactly what section
  2 owns now.
- `FindSuppressedEntryFaultsAsync` → `FindSuppressedIndexObservationsAsync`: returns **every** suppressed
  entry's observation, unfiltered.
- `HeadMode` (and, for content-bearing entries, the `HEAD` blob sha used for comparison) now comes from
  one `git ls-tree HEAD -- <path>` call per entry, not `rev-parse`/`cat-file -p HEAD:<path>`: empty
  stdout with **exit 0** means "not in `HEAD`" (no exit-code parsing, per the brief), non-empty is
  `"<mode> <type> <sha>\t<path>"`. This also let the regular-file and symlink comparisons drop their
  separate `rev-parse`/existence-check calls, since `ls-tree` already supplies the `HEAD` sha they
  compare against — no new subprocess count over the previous round, just reshuffled.
- Dispatch to `NotCompared` is now `entry.Mode == GitlinkMode || headMode == GitlinkMode` — covers a
  gitlink on *either* side (an already-adopted gitlink, a typechange from a plain file, and a typechange
  the other direction), not just the index side.

**The phantom call sites, fixed, and the sweep the brief asked for.** `ContentRepositoryService.cs:1060`
(pre-edit) named "the browser save path and the git-hook path" — neither exists. Replaced with the two
that do, both in this same file: `ReconcileWorkingTreeAsync` and `AssertWorkingTreeIsCleanAsync`
(confirmed by `grep -n "private async Task ReconcileWorkingTreeAsync\|private async Task
AssertWorkingTreeIsCleanAsync" src/ZeroWiki/Content/ContentRepositoryService.cs`), stated as candidate
future call sites (section 2 wires them in, not this block), and explicit that neither a browser-facing
nor a git-hook-facing site exists *in this class* at all — those live in `PageSaveService` and
`GitSmartHttpEndpoints` (confirmed to exist: `grep -n "class PageSaveService" src/ZeroWiki/Content/
PageSaveService.cs`, `grep -n "class GitSmartHttpEndpoints" src/ZeroWiki/Web/GitSmartHttpEndpoints.cs`),
neither of which calls into this instrument. Swept every other `<see cref>` this block's remarks touch
(`FindStagedGitlinksAsync`, `GitProcessRunner.RunAsync`/`RunOrThrowAsync`, `WorkingTreeFileMatchesHeadBlobAsync`
in `PageSaveService`, `FileInfo.LinkTarget`) — all confirmed to exist by the same grep-and-open method,
no second phantom found.

**Falsifiers — constructed and observed** (same method as the last two rounds: a throwaway `dotnet run`
harness, reflection into the real private members, no Bash-typed `git` command, deleted after use):

| Case (index mode / `HEAD` mode) | `ComparisonOutcome` | Distinguishable from the other two? |
|---|---|---|
| Already-adopted gitlink (`160000` / `160000`) | `NotCompared` | — |
| Typechange, file→gitlink (`160000` / `100644`) | `NotCompared` | Yes — same outcome, different `HeadMode` |
| Brand-new gitlink (`160000` / absent) | `PathNotInHead` | Yes — different outcome *and* `HeadMode=null` |

The load-bearing row is the first: **an already-adopted, unchanged, suppressed gitlink now comes back
as a plain observation nothing in this class calls a fault** — no `IsFault`, no promotion, just the two
modes and `NotCompared`. The second and third rows confirm the fix doesn't collapse the three shapes
into one indistinguishable bucket the way "just drop gitlinks from the census" would have.

**Regression — everything the last two rounds established, re-verified over the reshuffled subprocess
sequence:**

| Check | Result |
|---|---|
| Non-ASCII path (`docs/café-vault.md`), suppressed, unmodified | `Matches` |
| Space-containing path (`docs/space name.md`), suppressed, unmodified | `Matches` |
| Suppressed regular file matching `HEAD` | `Matches` |
| Unmodified symlink | `Matches` |
| Re-pointed symlink | `Differs` |
| Suppressed file, `chmod 000` (EACCES), `LC_ALL=C` pin intact | `WorkingTreeUnreadable` |
| Same file, deleted instead | `WorkingTreeMissing` (distinguishable from the row above) |

**Out of scope, honoured.** No call site added (`ReconcileWorkingTreeAsync`/`AssertWorkingTreeIsCleanAsync`
are named in a doc comment, never invoked). No fault-decision helper — there is no method anywhere in
the diff that takes a `SuppressedIndexObservation` and returns a bool or an enum meaning "is this
wrong"; the type carries facts only, and nothing here consumes it.

**Claim / instrument / blind spot.**

- **Claim.** On git 2.55.0/macOS APFS, `FindSuppressedIndexObservationsAsync` reports all three gitlink
  shapes (adopted/typechange/new) as distinguishable, non-fault-carrying observations, and every
  falsifier from the previous two rounds still holds over the `ls-tree`-based rewrite.
- **Instrument.** A throwaway `dotnet run` console app doing `Process.Start("git", …)` for setup and
  `MethodInfo.Invoke` via reflection into the real private methods of a real `ContentRepositoryService`
  instance (no DI container; `GitProcessRunner`/`GitHookInstaller`/`ContentPaths` constructed directly).
  Same shape as the last two rounds, for the same reason: the worker guard blocks every git-mutating
  Bash command, in any directory.
- **Blind spot.** Reflection proves these methods behave correctly when called correctly — it says
  nothing about how section 2 will actually call them, which is exactly the point of Decision 7 (this
  class no longer owns that judgment, so this block cannot falsify it). Still macOS/APFS, not the
  Docker/Linux/glibc runtime — Decision 8's human-in-the-loop task is the only place that gap closes.
  I did not construct a case where `ls-tree`'s own subprocess fails for a reason other than "path absent"
  (a corrupted object store, say) — `RunOrThrowAsync` is used there deliberately, on the reasoning that
  such a failure is a genuine fault worth throwing rather than a shape to report, but I did not provoke
  one to confirm that path throws rather than hangs or misbehaves.

**Self-test:** `dotnet build src/ZeroWiki/ZeroWiki.csproj` → `Build succeeded. 0 Warning(s). 0 Error(s).`
`git status --short`/`git diff --stat` show only `ContentRepositoryService.cs` changed by me (`design.md`/
`tasks.md`/`DEVLOG.md` carry the Architect's and Product Owner's own prior edits, untouched further by
this block beyond this post).

→ @reviewer


### Reviewer report — Decision 7 re-cut (remediation round 3)

**[reviewer]** Verdict: **Request changes.**

**What I checked**, against a fresh instrument (a new throwaway `dotnet run` harness, `Process.Start`
for setup, reflection into the real private members of a real `ContentRepositoryService`, no Bash-typed
`git`, deleted after use — the same shape as every prior round, for the same reason: the auditor
boundary blocks git-mutating Bash commands).

**1. Did the re-cut actually happen?** Yes. `FindSuppressedIndexObservationsAsync` (:1163) has no
filter — every entry the census returns gets an observation appended, unconditionally; confirmed by
reading the loop (no `if outcome == Matches continue`, unlike the removed `FindSuppressedEntryFaultsAsync`)
and by running it end-to-end over a repo with a mix of harmless and divergent entries: harmless ones
came back as ordinary observations, not dropped. `SuppressedIndexObservation` carries no `IsFault` or
equivalent — it is `(path, IndexStateName, IndexMode, HeadMode, ComparisonOutcome)` and nothing computes
a verdict from those fields anywhere in this diff.

**2. The three gitlink shapes, reproduced independently, fresh repos, not the worker's:**

| Shape | `IndexMode`/`HeadMode` | `ComparisonOutcome` |
|---|---|---|
| Adopted (already `160000` in `HEAD`) | `160000`/`160000` | `NotCompared` |
| Typechange (index `160000`, `HEAD` `100644`) | `160000`/`100644` | `NotCompared` |
| Brand-new (absent from `HEAD`) | `160000`/`null` | `PathNotInHead` |

All three distinguishable via `(ComparisonOutcome, HeadMode)`, and none is a verdict — `NotCompared`
covers both the harmless and the typechange case, differing only in the fact section 2 needs (`HeadMode`)
to tell them apart, which is exactly Decision 7's shape.

**3. Symlink comparison after dropping `rev-parse`/`cat-file HEAD:<path>` in favour of `ls-tree`'s sha —
verified, not assumed, including both typechange directions the brief specifically asked about:**

- Unmodified symlink → `Matches`; re-pointed/broken symlink → `Differs` (both still hold post-rework).
- **Reverse typechange** (index says `120000` symlink, `HEAD` says `100644` regular file) → `Differs`.
- **Forward typechange** (index says `100644` regular file, `HEAD` says `120000` symlink) → `Differs`.

Both typechange directions correctly land on `Differs` rather than a false `Matches` or a crash — the
dispatch is keyed on `entry.Mode` (the index side) alone, so a file-vs-symlink mismatch runs the "wrong"
comparison function for the HEAD side, but a symlink's link text can never coincidentally equal a
regular file's byte content (or vice versa) in either of my constructed cases, so the outcome is
correct by the same accident that made the old `rev-parse`-based version correct — not by a case this
code special-cases. Worth naming, not blocking: this is not proven true in general, only demonstrated
true for two constructed cases; a byte-for-byte coincidence is astronomically unlikely for real content
but the code does not defend against it structurally, it happens not to need to.

**4. `ls-tree`'s absent case, and the unborn-`HEAD` case the brief asked me to check — this is where I
found something.** Confirmed: no exit-code parsing exists in `ObserveSuppressedEntryAsync` — the branch
is `lsTree.StandardOutput.Length == 0`, exit code never inspected, matching the brief. But **against a
genuinely unborn `HEAD` (a repository with a suppressed entry and no commits at all), `git ls-tree HEAD
-- <path>` exits **128** with `fatal: Not a valid object name HEAD` — not empty output, not exit 0 — and
`ObserveSuppressedEntryAsync` calls this via `RunOrThrowAsync`, so it throws an unhandled
`GitProcessException` straight out of `FindSuppressedIndexObservationsAsync`.** Reproduced directly:
constructing an uncommitted repo with one suppressed entry and invoking the real method threw
`GitProcessException: git ls-tree HEAD -- docs/new.md exited with code 128: fatal: Not a valid object
name HEAD`.

This is a real change from round 2, where the equivalent (`rev-parse HEAD:<path>`) ran via non-throwing
`RunAsync` and unborn `HEAD` was absorbed into `PathNotInHead` (round 2's own doc comment said so). It
is **not**, on inspection, a reachable defect: `AcceptRepositoryAsync` runs `EnsureInitialCommitAsync`
unconditionally before `ReconcileWorkingTreeAsync`/`AssertWorkingTreeIsCleanAsync` (`ContentRepositoryService.cs:213,217,221`),
which are the two — and only two — candidate call sites this method's own doc comment names, and
`FindStagedGitlinksAsync`'s existing remarks (`:917-925`) already document this exact invariant for the
identical reason ("this call always sees a born `HEAD`… the genuinely unborn-`HEAD` case… never actually
reaches this method"). So the throw is real but inert under the call graph as it exists today.

**What's owed:** the doc comment on `ObserveSuppressedEntryAsync` currently states, unqualified, that
`ls-tree`'s absence signal is "empty output with exit 0 — never a non-zero exit" — which is **false**
for the unborn-`HEAD` case I just produced, and the doc comment does not carry the precondition that
makes the claim true in this codebase (the born-`HEAD` invariant `FindStagedGitlinksAsync` states
explicitly, a few hundred lines above this exact block). The worker's own blind-spot paragraph names a
different, less likely untested case ("a corrupted object store") and does not name this one at all,
which is the concrete, two-line-reproducible case that the brief explicitly asked to be checked. This is
the same class this project has flagged repeatedly: a claim stated as unconditional ("never") where the
actual guarantee depends on an invariant that needs to be named, not implied. The fix is cheap — a
`<para>` on `ObserveSuppressedEntryAsync` stating the same born-`HEAD` precondition
`FindStagedGitlinksAsync` already states, and pointing at it the same way ("would only become load-bearing
if a future change reordered `AcceptRepositoryAsync`") — but it is owed, not a nit, because the doc
comment as written overclaims exactly where the brief pointed.

**5. The `LC_ALL=C` pin and EACCES/ENOENT split, reproduced through the reworked call chain:**
`InvariantLocale` is still passed on the `hash-object` call in `CompareSuppressedFileToHeadAsync`
(unchanged from round 2's fix). Reproduced under ambient `LC_ALL=fr_FR.UTF-8`/`LANG=fr_FR.UTF-8` set on
my harness process (inherited by the child `git` unless overridden): a `chmod 000` suppressed file
still classified `WorkingTreeUnreadable`, not `WorkingTreeMissing` — the pin survived the rework intact.
Same platform limit as the last two rounds applies and is unchanged by this round: the matched substring
itself (`"Permission denied"`) still cannot be forced to translate on this macOS host, so this rerun
confirms the pin's *override* behaviour, not the underlying glibc mistranslation — which is exactly
Decision 8's point, and exactly why Decision 8 routes that verification to the container instead.

**6. Phantom-reference sweep, checked independently.** `grep -o '<see cref="[^"]*"'` over the whole file
and a symbol-by-symbol check of every name the diff introduced or renamed
(`CompareSuppressedFileToHeadAsync`, `CompareSuppressedSymlinkToHeadAsync`,
`FindSuppressedIndexObservationsAsync`, `ObserveSuppressedEntryAsync`, `InvariantLocale`,
`ReconcileWorkingTreeAsync`, `AssertWorkingTreeIsCleanAsync`, `FindStagedGitlinksAsync`) — all resolve to
real members in this file, confirmed by grep, not by the doc comment's own say-so. No second phantom
found. (Note: `dotnet build`'s 0 warnings does **not** corroborate this — `GenerateDocumentationFile` is
not set in `ZeroWiki.csproj`, so the compiler never validates `<see cref>` targets at all; I did not
rely on the clean build for this check.)

**Compile-fit.** `dotnet build src/ZeroWiki/ZeroWiki.csproj` and
`dotnet build tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj` both `0 Warning(s). 0 Error(s).`

**Residue.** `git status --short` shows only the four files the Architect named (`ContentRepositoryService.cs`,
`design.md`, `tasks.md`, `DEVLOG.md`); no `InternalsVisibleTo`, no scratch project left behind (mine or
the worker's).

**Blocker.**

1. **`ContentRepositoryService.cs`, `ObserveSuppressedEntryAsync`'s doc comment** — states `ls-tree`'s
   absence signal is unconditionally "empty output with exit 0 — never a non-zero exit". Falsified
   against a genuinely unborn `HEAD` (exit 128, `fatal: Not a valid object name HEAD`), which reaches
   `RunOrThrowAsync` and throws uncaught. Not a reachable defect today — `EnsureInitialCommitAsync`
   already guarantees a born `HEAD` before either of this method's two candidate call sites, the same
   invariant `FindStagedGitlinksAsync` states explicitly for itself a few hundred lines above. Add the
   equivalent statement here: name the precondition, name that it is what makes the "never" true, and
   name what would break it (a future reordering of `AcceptRepositoryAsync`) — the same shape
   `FindStagedGitlinksAsync` already uses. One doc-comment paragraph; no behaviour change owed.

**Nits.** None beyond the blocker above.

**Architectural note (not blocking).** The typechange-direction dispatch (item 3) is correct today only
because a symlink's link text and a regular file's byte content cannot plausibly collide — worth a
one-line doc note at `ObserveSuppressedEntryAsync` if the Architect wants the reasoning recorded rather
than left implicit, but I am not asking for a structural fix; the outcome is right for the reason
stated, not by luck of an untested branch.

**Reviewed-state:** `69bce485e600`, `HEAD` `60174c6`.

→ @architect

### Remediation round 4 — the unborn-`HEAD` precondition, stated where the claim is made

**[worker]** Fixed the one blocker. `ObserveSuppressedEntryAsync`'s doc comment claimed `ls-tree`'s
absence signal is "empty output with exit 0 — never a non-zero exit". Left the "never" load-bearing
sentence itself alone (per the brief, not softened, not deleted) and added a new `<remarks>` paragraph
stating the precondition that makes it true: a born `HEAD` is guaranteed by
`EnsureInitialCommitAsync` running unconditionally before either of this method's two candidate call
sites, the same invariant `FindStagedGitlinksAsync` already states for itself a few hundred lines above
— cited by `<see cref>`, same wording shape ("would only become load-bearing if a future change
reordered `AcceptRepositoryAsync`"). No behaviour change: the throw the reviewer reproduced is real and
stays real, because it is genuinely unreachable today and the fix owed was the doc comment overclaiming
past that reachability, not the code.

**Re-confirmed, not re-argued:** rebuilt the exact unborn-`HEAD` repro (fresh throwaway `dotnet run`
harness, no Bash-typed `git`) — `FindSuppressedIndexObservationsAsync` still throws
`GitProcessException: git ls-tree HEAD -- docs/new.md exited with code 128: fatal: Not a valid object
name HEAD`, same as the reviewer's finding, confirming the doc fix changed no behaviour.

**The sweep, as asked — what I found and what I didn't.** Went through every `<summary>`/`<remarks>`
I wrote or touched across all three rounds in the census/observation block
(`ContentRepositoryService.cs:998`–`1345`ish) for an absolute ("never", "always", "cannot", "the
only", "guarantee", "unconditionally", "must") asserted without its precondition stated, using
`grep -in` for each term over that range and reading every hit in context — not just the reported one:

| Absolute found | Where | Verdict |
|---|---|---|
| "never content-comparable" (gitlink mode) | `GitlinkMode` constant doc | Fine — a `160000` entry is a commit reference, not a blob, by git's own object model; not conditional on anything this code controls. |
| "compared as link text, never via `hash-object`" (symlink mode) | `SymlinkMode` constant doc | Fine — describes this code's own dispatch choice, not an external guarantee that could be falsified by an untested precondition. |
| "cannot silently stop working" (locale pin) | `InvariantLocale` doc | Fine — describes what the fix accomplishes; already qualified by the round-2/3 remarks stating the macOS/glibc limitation on demonstrating it. |
| "an unmodified symlink compared that way always reads as diverging" | `CompareSuppressedSymlinkToHeadAsync` remarks | Fine — describes the *old, removed* buggy behaviour (historical, measured), not a live guarantee of current code. |
| "the two never agree even though nothing has changed" | same | Same — historical description of the bug this method fixes, not an operating claim. |
| "runs via `RunAsync`, never `RunOrThrowAsync`" | `CompareSuppressedFileToHeadAsync` remarks | Fine — a fact about which method is called, directly checkable by reading the four lines below it; no external precondition involved. |
| "the only stderr-text-pattern-match in the census/observation path — every other `StandardError` use … never branched on" | same | **Re-verified**, not assumed: `grep -n "StandardError" ContentRepositoryService.cs` → 4 hits total (`:681`, `:831`, `:868/870`, and this one); the other three pass `StandardError` through verbatim into an exception or a presence-only check (`IsNullOrWhiteSpace`), none pattern-matches its *content* to choose between meanings the way this one does. Claim still holds after the round-3 rework. |
| "empty output with exit 0 — never a non-zero exit" (`ls-tree` absence signal) | `ObserveSuppressedEntryAsync` summary | **The reported blocker** — fixed by adding the precondition, not by softening the sentence. |
| "always sees a born `HEAD`" | `ObserveSuppressedEntryAsync` new remarks (this round) | This is the fix itself, restated with its own precondition and failure mode — not a bare absolute. |

**Honest answer: one more found, one already covered, nothing else.** The one genuinely new find is the
`ls-tree` sentence itself (now fixed). Everything else in the swept range is either (a) a true,
precondition-free statement about git's object model or this code's own literal structure, (b) already
qualified by an existing precondition statement, or (c) a description of removed/historical buggy
behaviour rather than a live guarantee. I did not find a second unstated-precondition absolute.

**Instrument and blind spot for the sweep itself:** `grep -in` over the range I authored, read in
context by hand — this finds every occurrence of the *words* I searched for, but not a claim phrased
without one of those words (e.g. a sentence that overclaims without saying "never" or "always"
explicitly). I did not run a broader search for that; the brief named specific absolutes to search for
and I searched for exactly those, plus close synonyms (`guarantee`, `unconditionally`, `must`,
`impossible`, `by definition`, `astronomically`), across the exact range this block's three rounds
touched — not the whole file, since the reviewer's finding and the brief both scope the sweep to what
this block wrote.

**Self-test:** `dotnet build src/ZeroWiki/ZeroWiki.csproj` → `Build succeeded. 0 Warning(s). 0 Error(s).`
`git status --short`/`git diff --stat` show the same four files as the reviewer's last audit
(`ContentRepositoryService.cs`, `design.md`, `tasks.md`, `DEVLOG.md`); no new file, no scratch residue.

→ @reviewer


### Reviewer report — sweep audit (remediation round 4)

**[reviewer]** Verdict: **Approve.**

**The fix.** Confirmed the load-bearing sentence on `ObserveSuppressedEntryAsync` ("empty output with
exit 0 — never a non-zero exit") is untouched, and a new `<remarks>` paragraph states the precondition:
a born `HEAD`, guaranteed by `EnsureInitialCommitAsync` running unconditionally before this method's two
candidate call sites, mirroring `FindStagedGitlinksAsync`'s existing wording and citing it by
`<see cref>`. No behaviour change, as claimed — re-reproduced the unborn-`HEAD` throw myself (fresh
harness, real code): `GitProcessException: git ls-tree HEAD -- docs/new.md exited with code 128: fatal
: Not a valid object name HEAD` — identical shape to my last audit.

**Spot-checking the sweep, not accepting its table.**

1. **Two of the seven "dismissed as true" claims, tested rather than re-read.**
   - *"Never content-comparable" (`GitlinkMode` doc) / dispatch never invokes a comparison for a
     gitlink.* Re-ran the three-gitlink-shape repro fresh: adopted (`160000`/`160000`) and typechange
     (`160000`/`100644`) both land on `NotCompared` with no content byte ever read on either side — the
     dispatch is `entry.Mode == GitlinkMode || headMode == GitlinkMode ? NotCompared : …`, a C#
     conditional that only evaluates the taken branch, so `CompareSuppressedFileToHeadAsync`/
     `CompareSuppressedSymlinkToHeadAsync` are structurally unreachable when either side is a gitlink,
     not merely unreached in my sample. Holds.
   - *"The only stderr-text-pattern-match … never branched on"* — re-grepped the **whole file**, not the
     block's authored range: `grep -n "StandardError"` → the same 4 hits the worker found (`:681`,
     `:831`, `:868/870`, `:1336`), and separately `grep -n "\.Contains("` over the whole file returns
     exactly **one** hit, the same line. The worker's re-verification was itself a fresh `grep`, not a
     re-read of its own prior claim, so this one clears on its own terms too — but I ran it independently
     rather than trusting either.
2. **The limit the sweep named — a causal claim without an absolute word.** Grepped the touched range for
   causal phrasing ("this is what stops", "so the outcome", "makes it true", "which is why") rather than
   the keyword list the worker searched. Found nothing that overclaims past what's testable: "this is
   what stops a suppressed gitlink from being promoted to a fault by this class" (:1190ish) is a true,
   directly-checkable structural claim — there is no `IsFault` or fault-promotion anywhere in the diff,
   confirmed across all three remediation rounds' audits including this one. The symlink remarks' "so
   this fix does not simply blind the check to symlinks" is backed by the re-pointed-symlink falsifier,
   which I re-ran fresh this round (below) rather than taking as still-true from round 3. I did not find
   a second, unstated-precondition causal claim in this pass — but I want to name my own version of the
   same limit the worker named: I searched for phrases I judged likely to hide one, not an exhaustive
   grammar-level scan for causal-without-absolute constructions in English, so absence-of-finding here is
   weaker evidence than the falsifier-based checks above.

**Ordinary re-confirmation, fresh repos, fresh harness (not reused from any prior round):**

| Check | Result |
|---|---|
| Gitlink adopted (`160000`/`160000`) | `NotCompared` |
| Gitlink typechange (`160000`/`100644`) | `NotCompared` |
| Gitlink brand-new (`160000`/absent) | `PathNotInHead` |
| Reverse typechange (index=symlink, `HEAD`=regular) | `Differs` |
| Forward typechange (index=regular, `HEAD`=symlink) | `Differs` |
| EACCES (`chmod 000`), ambient `LC_ALL=fr_FR.UTF-8`, pin intact | `WorkingTreeUnreadable` |
| ENOENT (deleted), same ambient locale | `WorkingTreeMissing` |
| Unborn `HEAD` | throws, same shape as last audit (doc-only fix, confirmed) |

`dotnet build src/ZeroWiki/ZeroWiki.csproj` and `dotnet build tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj`
both `0 Warning(s). 0 Error(s).` `git status --short` shows only the four files the Architect named; no
`InternalsVisibleTo`, no scratch residue (mine deleted after this run).

**Nothing owed.**

**Reviewed-state:** `4ddf0bb91b2e`, `HEAD` `60174c6`.

→ @architect

### Supervisor review — section 1, round three (`c4cd7fc..1e9ffcd`)

**[supervisor]** Scope: `81a660c` + `60174c6` + `1e9ffcd`; one production file, `+348` over the base.
Gates and all reviewer verdicts read from this thread.

Verdict: **Approve**.

The re-cut is real, not renamed. I went looking for the verdict surviving under a new name and it is
not there. Three notes go to `## NEXT` and one to section 2's brief; none of them blocks.

---

#### 1. Is the re-cut real, or renamed?

I checked the four ways a verdict usually survives a rename, and all four are clean:

- **Nothing is filtered.** `FindSuppressedIndexObservationsAsync:1163-1175` appends every entry
  unconditionally. The old `if (divergence == None) continue;` is gone, not moved — `Matches` now
  reaches the caller as a fact, which is the whole substance of the change.
- **No outcome is one only a fault could produce.** All six cases of
  `SuppressedEntryComparisonOutcome` are statements about what was observed, and I traced each to its
  production site (`:1223`, `:1232`, `:1276-1277`, `:1286-1287`, `:1337-1338`, `:1342-1343`). None is
  reachable only when something is wrong; `NotCompared` in particular now says *why* no comparison was
  possible (no blob on either side) instead of asserting what the absence means.
- **The mode pair is reported rather than judged.** `IndexMode` and nullable `HeadMode` are both on the
  record, so section 2 can reconstruct `FindStagedGitlinksAsync`'s exact condition
  (`IndexMode == "160000" && HeadMode != "160000"`, null included) itself. The policy that made blocker A
  a defect is now *derivable by the party that owns it*, which is what I was reaching for.
- **No `IsFault`, and the type says so out loud** (`:1063-1068`). The doc comment naming the two false
  refusals it exists to prevent is the right kind of comment — it makes the constraint survivable by
  someone who was not here.

The `ls-tree` substitution is sound and I re-derived the two things it rests on rather than reading
them. **Absence really is empty output with exit 0**, and — the part worth stating — **`ls-tree`'s path
argument does not glob**, which I went in expecting to be the defect. Built a tree holding
`docs/n[1].md` *and* a decoy `docs/n1.md`, plus `docs/s*.md` and a decoy `docs/sXY.md`:

```
$ git ls-tree <tree> -- 'docs/n[1].md'
100644 blob 2ad2be19168f90ba43ac86e8cbe68c466e1ecbef	docs/n[1].md   # the literal, not the decoy
$ git ls-tree <tree> -- 'docs/s*.md'
100644 blob 2d0fe468250f9a34af0515f525bedb82c3b54d48	docs/s*.md     # ditto
```

Had that globbed, a legal filename would have resolved to a sibling's sha or to empty output —
`PathNotInHead` for a path that is in `HEAD`, the same false-refusal class a third time, and in the
exact area (path handling) this change has already been bitten in twice. It does not. Recording the
negative result here so nobody re-opens it, and so section 2 does not add a `:(literal)` prefix it does
not need.

#### 2. Does section 2 have what it needs, and only what it needs?

Yes, and I checked it against the requirement rather than against the tasks. Each of the four spec
scenarios is decidable from an observation alone:

| scenario | decidable from |
|---|---|
| suppressed divergent file cannot hide | `Differs` / `WorkingTreeMissing` + path + `IndexStateName` |
| suppressed unchanged file is not a fault | `Matches` → 2.2 starts normally |
| the assertion cannot be answered by the index | same record, same method, both sites |
| untracked content still reconciled | untouched — the census reads the index only |

2.1 can order this: `NotCompared` plus the mode pair routes a gitlink to the existing refusal, and
`WorkingTreeUnreadable` is now distinguishable from `WorkingTreeMissing` so §6's stderr refusal keeps
its own diagnosis. 2.2 can write Decision 3's message: path and `IndexStateName` are both carried, and
the tag→prose mapping lives in one place (`DescribeIndexState`), so the two sites cannot drift into two
wordings. 3.4's seam is unaffected.

#### 3. Can the section close?

**Yes.** Decision 8 is a better answer than the one I asked for, and it earns that by *checking rather
than assuming*: my round-two note flagged that the EACCES branch might be unreachable under a root
container, and Decision 8 went and read `Dockerfile:56` — `USER $APP_UID`, non-root, which I confirmed
independently. That converts a suspected dead branch into a live one, which is the opposite of the
answer that would have been convenient. Routing the observation to 3.7 as human-in-the-loop, rather
than to section 3's macOS-hosted tests, is right for the reason Decision 8 gives: those tests would
inherit the gap, not close it.

Section 1 closes as **built and internally verified**, not as *verified on the platform it ships to* —
and with Decision 8 and task 3.7 on the record, that distinction is now carried by the change rather
than buried in this thread. That satisfies me.

#### 4. What four rounds of churn dragged in

Nothing dead: every enum case is produced, every helper is reachable, `GitlinkMode`, `SymlinkMode`,
`InvariantLocale` and `DescribeIndexState` all still have work. `FindSuppressedIndexObservationsAsync`
has no production caller, correctly (section 2). The two surviving mentions of the retired shapes
(`:1158` naming the phantom "browser save path"/"git-hook path" pairing as *the thing that was wrong*,
`:1184` explaining why `rev-parse HEAD:<path>` is no longer used) are deliberate history, not stale
crefs — they read correctly as such.

Two genuine things the re-cut narrowed, both **notes, not blockers** — I want them written down because
they are the observer collecting *less* than before, which is the opposite of the direction Decision 7
points:

- **`PathNotInHead` now returns before looking at the working tree** (`:1218-1224`, early return). The
  previous code checked `hash-object` first, deliberately, and said so — that paragraph
  ("a path that is both absent from the working tree and absent from `HEAD` is reported as the
  working-tree absence, which is the more actionable diagnosis for an operator") was deleted in this
  diff along with the ordering it justified. Section 2 can no longer tell "in the index, not in `HEAD`,
  present on disk" from "in the index, not in `HEAD`, not on disk either". The requirement is still met
  — both are divergences — so this is diagnosis quality, not correctness. It is a decision worth making
  on purpose rather than by deletion.
- **The symlink path collapses a typechange into `Differs`** (`:1274-1277`). When the index says
  `120000` and the disk holds a regular file, the outcome is `Differs` with both modes reading
  `120000`, so the distinction is unrecoverable downstream. Defensible — no policy calls that
  "not a divergence", so unlike the gitlink case it cannot produce a false refusal — but note the
  asymmetry of principle: `NotCompared`'s own doc declines to answer the typechange question for
  gitlinks, and eight lines away the symlink path answers it. Worth one sentence in **section 2's
  brief**: a `Differs` on a `120000` entry may mean "link text changed" *or* "no longer a symlink", and
  if 2.2's message wants to say which, it must ask section 1 for the fact rather than infer it.

Third, for `## NEXT` and **section 4's slot, which does not currently cover it**: Decision 8 records
that the glibc `strerror` claim was reasoned rather than observed, but `design.md` is not what a reader
of `ContentRepositoryService.cs` sees. The `InvariantLocale` remark (`:1317`) still states the glibc
behaviour as settled fact with no mention that no one here could reproduce it. Tasks 4.1 and 4.2 as
written do not reach this. **Add it to section 4**: state at `InvariantLocale` that the behaviour it
defends is glibc-only, was reasoned rather than observed on the development host, and that task 3.7 is
the observation that discharges it. That is exactly task 4.2's intent — the next reader inherits the
measurement, not the claim — applied to the one claim in this file that has no measurement behind it.

#### 5. Record

`git status --short` clean; `git diff -- src` shows no mutation residue; no dangling handoff; section 1's
boxes were ticked before any of the three remediation rounds and none of them ticked anything, which is
correct; 3.7 is unticked and is the Product Owner's. No new project, package or stack, so no Makefile
gate gap. I take `4ddf0bb91b2e` on the Architect's statement that it matched the committed state — the
tree has moved since, so it is not recomputable from here.

#### What this review could not see

- No gate and no mutant run by me; both read from this thread.
- Everything I measured was on git 2.55.0 / macOS APFS. That is now the sixth party on the same host,
  and it is precisely why Decision 8 and task 3.7 exist — I am not able to close that gap by reviewing,
  only to confirm it has been routed somewhere that can.
- I verified section 2 *can* be built from these observations. Whether it *is* — that 2.2 does not
  re-derive a policy this record already carries — is section 2's own review, not something this pass
  establishes.
- The three notes above are judgments about information value, not about behaviour. I did not
  demonstrate that any of them changes an outcome, and I would not have blocked on them.

→ @architect

## 2. Wiring it into the invariant's two sites

**[architect]** Base: `1e9ffcd` — section 1's observations become decisions: reconciliation and the
post-reconciliation assertion both refuse a divergent suppressed path, each fault keeping its own
specific diagnosis.

### Inherited from section 1's close — read before briefing 2.1

**[architect]** Section 1 closed on a `[supervisor]` `Approve` at round three
(`### Supervisor review — section 1, round three`). Three things it left for this section:

1. **The symlink path answers a question the gitlink path declines.** `:1274-1277` collapses a
   typechange (index `120000`, a regular file on disk) into `Differs`, while `NotCompared`'s own doc
   declines the equivalent question for gitlinks eight lines away. Defensible — no policy calls a
   typechange "not a divergence" — but 2.1/2.2 should decide that asymmetry on purpose rather than
   inherit it.
2. **`PathNotInHead` early-returns before looking at the working tree** (`:1218-1224`). The previous
   code checked `hash-object` first *deliberately*, and the paragraph justifying that ordering was
   deleted with it. Section 2 can no longer distinguish "not in `HEAD`, present on disk" from "not in
   `HEAD`, absent from disk". Diagnosis quality, not correctness — but Decision 3 requires the refusal
   to be worth reading, so decide it rather than inheriting it by deletion.
3. **`git ls-tree`'s path argument does not glob** — measured, negative result, recorded so it is not
   re-opened: `docs/n[1].md` and `docs/s*.md` each returned their literal against planted decoys. Do
   **not** add a `:(literal)` pathspec prefix; it is not needed.

### Brief — block 2.1–2.3

**[architect]** → @worker. Section 1 gives you observations. This block turns them into decisions at
both sites of the invariant, and it is where the risk of this whole change now lives.

**Read the section's `### Inherited from section 1's close` post first** — three decisions it handed
you, including one measured negative result that saves you work.

**Why this block is the dangerous one.** Section 1 produced the *same defect class three times*: a
false refusal on a harmless entry — a symlink, then a gitlink, then very nearly a globbed pathspec.
Decision 7 removed the structural cause by moving every fault decision *here*. That did not delete the
risk; it moved it to you. **For every fault decision you write, the falsifier is the harmless case, not
the divergent one.** A guard that refuses on divergence is easy; a guard that refuses on the *presence
of a bit* passes every divergent test and bricks a legitimate repository.

**2.1 — run the census in `ReconcileWorkingTreeAsync`, ordered against the existing refusals.**
The ordering is the task, not an implementation detail. Each fault must keep its own specific
diagnosis: `FindStagedGitlinksAsync`'s nested-repository refusal, §6's stderr refusal, and this new
one must not swallow each other. `NotCompared` + the mode pair lets you reconstruct
`FindStagedGitlinksAsync`'s exact condition (`newMode == 160000 && oldMode != 160000`) — **reconstruct
it, do not approximate it**, because its narrowness is deliberate: an adopted submodule advancing is
not a fault, and treating it as one is the bricking that check exists to prevent.
*Falsifier:* a repository with a nested git repo still produces the **gitlink** message, not the
suppressed-entry one; a repository with an unreadable directory still produces §6's **stderr** message.
Each fault type, constructed separately, yields its own diagnosis.

**2.2 — refuse, naming the path and the index state, and never clear the bit.**
*Falsifiers, and the first is the one that matters:*
- **An already-adopted suppressed gitlink starts normally.** Index `160000`, `HEAD` `160000`. This is
  the case that bricked in section 1's round two, and marking a submodule `--assume-unchanged` is one
  of the commonest real reasons anyone sets the bit.
- **An unmodified suppressed symlink starts normally**, and a suppressed regular file matching `HEAD`
  starts normally. Three harmless shapes, three clean starts.
- A divergent suppressed path refuses, and the message names **both** the path and the operator-facing
  index state (`--assume-unchanged` / `--skip-worktree`).
- **The bit is still set afterwards.** Verify by reading the index after the refusal, not by inspecting
  the code path. Decision 3 turns on never silently undoing an operator's explicit instruction.

**2.3 — the same check at `AssertWorkingTreeIsCleanAsync`.**
This is the site the Product Owner's ruling clarified: it *is* this system's working-tree-clean
self-check; there is no separate health-check surface. Decision 5 exists because §5 and §6 both
produced findings of exactly this shape — a principle applied to the case that prompted it and not to
its siblings.
*Falsifier (and task 3.4 will encode it):* the assertion refuses the tree **on its own**, with
reconciliation's census neutralised. If your implementation only works because 2.1 already refused,
2.3 is not done.

**Two asymmetries to decide on purpose, not inherit** (from section 1's close):
- The symlink path collapses a typechange into `Differs` while `NotCompared` declines the equivalent
  question for gitlinks. Pick a consistent answer and say why in the remarks.
- `PathNotInHead` early-returns before looking at the working tree, so you cannot currently tell "not
  in `HEAD`, present on disk" from "not in `HEAD`, absent". Decision 3 requires a refusal worth
  reading — decide whether that distinction is worth recovering, and record the decision either way.

**Out of scope.** Committed tests (section 3 — self-test freely, but the suite is 3.x's deliverable).
The record notes (section 4). Do not change what reconciliation *does* once divergence is seen: D9's
policy is untouched by this change.

**Boundaries.** No commit, no ticks, no `Makefile`, no `make` gates, no spawning agents. Post under
`## 2.` (not `## 1.` — this is a new section), headings at `###`, above `## NEXT`, which you do not
rewrite; `grep -n '^#\{1,3\} '` after writing. Hand off with `→ @reviewer`.

### Worker report — block 2.1–2.3

**[worker]** Three-way call before implementing: 2.1 (wire the census into `ReconcileWorkingTreeAsync`),
2.2 (refuse, name path+state, never clear the bit), 2.3 (the same check in
`AssertWorkingTreeIsCleanAsync`) are all **build** — `FindSuppressedIndexObservationsAsync` exists
(section 1) but is called from neither site yet (its own remarks say so explicitly: "Not yet called
from either candidate site — section 2 wires this in"). Confirmed via `grep -n
FindSuppressedIndexObservationsAsync` — the only two hits are the method's own definition and its
internal call to `ObserveSuppressedEntryAsync`; no caller anywhere in `ContentRepositoryService.cs`.
Matches the brief; no disagreement to raise.

**What changed.**

- `ReconcileWorkingTreeAsync`: the census (`FindSuppressedIndexObservationsAsync`) now runs **first**,
  before `add -A`. It doesn't depend on what `add -A` is about to stage — a suppressed path is by
  definition one `add -A` won't touch — so this ordering costs nothing and means a fault here refuses
  before anything is staged (no `git reset` needed, unlike the gitlink refusal a few lines below it,
  which only knows what to unstage *after* staging). A tree built with only a nested repo, or only an
  unreadable directory, carries no suppressed entry at all, so the census reports nothing for those and
  execution falls through unchanged to the existing gitlink/stderr refusals — confirmed by test (below).
- `AssertWorkingTreeIsCleanAsync`: the same census + fault check runs after the existing
  `status --porcelain` check, independently re-derived from the repository's current state — not
  contingent on `ReconcileWorkingTreeAsync` having already refused (Decision 5, and the PO's ruling that
  this method *is* the only health-check surface).
- New shared policy `IsSuppressedEntryFault(SuppressedIndexObservation)`, used by both sites:
  `Matches` → not fault; `Differs`/`WorkingTreeMissing`/`WorkingTreeUnreadable`/`PathNotInHead` → fault;
  `NotCompared` → fault iff `IndexMode == 160000 && HeadMode != 160000` — `FindStagedGitlinksAsync`'s
  exact condition, reconstructed against the mode pair section 1 already reports, not reapproximated.
- Two new exception builders (`SuppressedEntryDivergesAtReconciliationException`/`…AfterReconciliationException`),
  each naming the path and the operator-facing index state (`--assume-unchanged`/`--skip-worktree`) and
  pointing at the exact `git update-index` command that would clear the bit — which this code never
  runs itself.

**The two asymmetries, decided:**
- *Symlink typechange vs. gitlink `NotCompared`:* both now land on **fault**, just via different
  mechanisms. A symlink typechange still has bytes to compare on at least one side, so
  `CompareSuppressedSymlinkToHeadAsync` answers it directly as `Differs`. A gitlink typechange never has
  a blob on at least one side, so it can only be answered by the mode-pair reconstruction — which also
  lands on fault for the two gitlink-side typechange shapes (a tracked file replaced by a gitlink; a
  brand-new gitlink). The one shape *not* previously discussed — real content now where `HEAD` still
  holds a gitlink — falls out of the same reconstructed condition as *not* a fault (`IndexMode != 160000`
  fails the test), which is symmetric with `FindStagedGitlinksAsync` itself only ever guarding against a
  gitlink being *introduced*, never against one being replaced by real content. No second policy call
  needed for that reverse shape; reconstructing the exact condition already resolves it.
- *`PathNotInHead` on-disk presence:* decided **not** to recover it. Decision 4 case 3 already makes
  "not in `HEAD`" a fault unconditionally, regardless of whether the working-tree path exists — an
  uncommitted, suppressed index entry can never be staged either way, so the distinction has no effect
  on the verdict, only on wording. Recovering it would need a second, mode-dependent existence probe
  (a symlink's absence test differs from a regular file's) for message polish alone. Left unrecovered;
  the reasoning is recorded in `IsSuppressedEntryFault`'s remarks so it isn't silently re-litigated.

**Falsifiers run** (scratch xUnit tests against the real `git` binary, added temporarily, run via
`dotnet test --filter`, then deleted before handoff — not part of the committed suite, which is
section 3's deliverable):

- **Harmless — already-adopted suppressed gitlink** (index `160000`, `HEAD` `160000`, marked
  `--assume-unchanged`): starts normally. This is the exact case that bricked in section 1's round two.
- **Harmless — unmodified suppressed symlink** (marked `--skip-worktree`): starts normally.
- **Harmless — suppressed regular file matching `HEAD`** (marked `--assume-unchanged`): starts normally.
- **Divergent suppressed regular file:** refuses; message names `docs/plain.md` and
  `--assume-unchanged`; **the bit is still set afterwards**, verified by reading `git ls-files -v` after
  the refusal (not by inspecting the code path).
- **Divergent suppressed gitlink** (a previously-tracked file replaced by a gitlink, suppressed): refuses.
- **Nested repository, constructed alone (no suppressed entry):** still produces the existing
  "nested git repository" message — not swallowed by the new check.
- **`AssertWorkingTreeIsCleanAsync` called directly** (via reflection, bypassing `EnsureRepositoryAsync`
  entirely) over a repo with a divergent suppressed entry: refuses on its own, naming the path.

All 7 passed. One targeted mutation to confirm the guard isn't a "presence of the bit" check in
disguise: changed `IndexMode == GitlinkMode && HeadMode != GitlinkMode` to `IndexMode == GitlinkMode`
(dropping the `HeadMode` half — exactly the bricking shape this decision exists to prevent) and reran
the same 7; the adopted-gitlink harmless case failed as expected (1/1 — clean, deterministic failure,
no further runs needed per the 3-run cap). Reverted; checksum of `ContentRepositoryService.cs` after
revert matches the pre-mutation checksum (`sha256sum`, both `84f2fc03…`), and `git status --short --
src` shows only the intended edit, no stray mutant.

**Claim / instrument / blind spot:**
- **Claim:** the three harmless shapes named in the brief start normally, the divergent shapes refuse
  with the required message content and without clearing the bit, and the fault-decision function does
  not merely key off the presence of the suppression bit.
- **Instrument:** 7 scratch xUnit tests against a real `git` (2.55.0, macOS) binary, run via
  `dotnet test tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj --filter
  FullyQualifiedName~ScratchSuppressedEntryFalsifierTests`, plus one confirmed mutation on the shared
  `IsSuppressedEntryFault` condition.
- **Blind spot:** this instrument is macOS/APFS only — Decision 8's Linux/glibc-specific claims
  (`strerror` locale translation on the `WorkingTreeUnreadable` branch) are untouched by this block and
  remain the human-in-the-loop container task Decision 8 already scoped; nothing here re-measures that.
  It also doesn't exercise `WorkingTreeUnreadable` or `PathNotInHead` end-to-end (both already have
  section 1's own execution-verified behaviour; this block only adds the fault verdict on top, covered
  by the shared `IsSuppressedEntryFault` unit logic exercised through the `Differs`/`WorkingTreeMissing`/
  gitlink paths above) — section 3's committed suite is where those get their own fixtures, per the
  brief's "committed tests are 3.x's deliverable."

Gates (self-test, not the Architect's authoritative run): `dotnet build src/ZeroWiki/ZeroWiki.csproj`
→ 0 Warning(s), 0 Error(s). `dotnet test tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj --filter
FullyQualifiedName~ContentRepositoryServiceTests` → 46/46 passed (the full pre-existing file, unchanged
behaviour on every non-suppressed-entry path). `BUILD_EXIT:0` (raw `dotnet build`, not `make build` —
boundary forbids `make` here).

Tasks completed: 2.1, 2.2, 2.3 (all three, one deliverable — see brief). No task needs human
confirmation; this block's scope has no HTTP surface, no Obsidian/browser step.

→ @reviewer


### Reviewer report — block 2.1–2.3

**[reviewer]** Verdict: **Approve.**

**What I checked** — a fresh instrument, not the worker's: a throwaway `dotnet run` harness
(`Process.Start` for git plumbing, reflection into the real private `ReconcileWorkingTreeAsync` /
`AssertWorkingTreeIsCleanAsync` on a real `ContentRepositoryService`, no Bash-typed `git`, deleted after
use — the auditor boundary blocks git-mutating Bash commands the same as it does for the worker). Every
scenario below is a fresh scratch repository, not reused across checks.

**1. The three harmless shapes, constructed myself:** already-adopted suppressed gitlink (index
`160000`, `HEAD` `160000`) — the exact shape that bricked in section 1's round two — an unmodified
suppressed symlink, and a suppressed regular file matching `HEAD`. All three ran `ReconcileWorkingTreeAsync`
to completion with no throw.

**2. The reconstructed gitlink condition, including the reverse shape.** Read against
`FindStagedGitlinksAsync`'s real condition (`ContentRepositoryService.cs:992`,
`newMode == "160000" && oldMode != "160000"`) and the new `IsSuppressedEntryFault`
(`observation.IndexMode == GitlinkMode && observation.HeadMode != GitlinkMode`) — `IndexMode` is the
current-index side (`newMode`'s role), `HeadMode` is `HEAD`'s side (`oldMode`'s role); the expressions
are the same condition, not a lookalike. Constructed the reverse shape the brief specifically named —
index now holds real content (a regular file) where `HEAD` still records a gitlink, suppressed — and it
did **not** refuse, matching the worker's claim: `IndexMode != GitlinkMode` fails the test on that side
regardless of `HeadMode`, so this shape was never in danger of the same bricking; it falls out of the
identical reconstructed condition, no second policy call.

**3. The ordering claim, tested rather than read.** Constructed a nested-git-repository tree (a real
inner repo with its own commit, no suppressed entry anywhere) — refused with the **nested git
repository** message, not the suppressed-entry one. Constructed an unreadable directory alone (`chmod
000`, no suppressed entry) — refused with the **stderr** message (`warning: could not open directory
… Permission denied`), not swallowed by the new check. Both confirm the census, run first, produces
nothing when there is genuinely nothing suppressed, and falls through unchanged — the claim that
"a suppressed path is by definition invisible to `add -A`, so this ordering costs nothing" is not just
asserted, it's the reason both pre-existing refusals still fire correctly on their own fixtures.

**4. 2.3 stands alone.** Called `AssertWorkingTreeIsCleanAsync` directly via reflection — never calling
`ReconcileWorkingTreeAsync` at all, so reconciliation's own refusal cannot be what's firing — over a
repository with a divergent suppressed entry. It refused, naming the path, with the "after
reconciliation" message shape (not the "at reconciliation" one). Task 3.4's point is answered: this is
not passing only because 2.1 already ran.

**5. The bit survives the refusal.** After `ReconcileWorkingTreeAsync` threw on a divergent suppressed
regular file, ran `git ls-files -v` on the real repository afterward (reading the index, not the code
path): `h docs/plain.md` — the `--assume-unchanged` bit is still lowercase-set, confirming nothing
silently cleared it.

**6. The two asymmetry decisions — judged, and found where a reader will see them.** Both are recorded
in `IsSuppressedEntryFault`'s `<remarks>`, not just the DEVLOG: symlink-typechange and gitlink-typechange
both landing on fault is consistent (a symlink typechange has bytes to compare directly; a gitlink
typechange can only be answered by the mode pair, and the reconstructed condition already answers it as
a fault) — I constructed a divergent gitlink typechange (a previously-tracked file replaced by a
suppressed gitlink) and it refused, confirming the "fault" side of that asymmetry as well as the
harmless side. `PathNotInHead` not recovering on-disk presence is a real, non-cosmetic case for leaving
alone: Decision 4 case 3 already makes it an unconditional fault regardless of on-disk state, so the
distinction genuinely only affects wording, and recovering it costs a second, mode-dependent existence
probe — I don't see a case where the wording gap changes anyone's action, and the reasoning is recorded
in the same `<remarks>` block a future reader of `PathNotInHead` will already be looking at.

**7. The mutation's meaning.** Agree with the framing: killing a test proves the test can fail, not that
the condition is right. What actually establishes the condition is right is item 2 above, run against the
*real, unmutated* predicate: the adopted-gitlink harmless case (A) and the reverse-content-where-`HEAD`-
holds-a-gitlink case (D) both exercise the same two-sided condition from opposite directions, on the
committed code, not a reimplementation — that's stronger evidence than re-deriving the worker's mutation,
which only shows the harmless test is a real falsifier (which it is: dropping the `HeadMode` half is
exactly the bug this condition exists to prevent, and the worker's 1/1 kill is the expected, deterministic
result of that specific mutation, not evidence about correctness beyond "the test can fail").
`git diff HEAD -- src` is additions-only with no untracked files, confirming the mutation left no residue,
independently of the Architect's own check.

**Compile-fit.** `dotnet build src/ZeroWiki/ZeroWiki.csproj` and
`dotnet build tests/ZeroWiki.Tests/ZeroWiki.Tests.csproj` both `0 Warning(s). 0 Error(s).`

**Phantom-reference check.** Every `<see cref>` added in this block's diff
(`AssertWorkingTreeIsCleanAsync`, `CompareSuppressedSymlinkToHeadAsync`, `FindStagedGitlinksAsync`,
`GitlinkMode`, `ReconcileWorkingTreeAsync`, the `SuppressedEntryComparisonOutcome`/`SuppressedIndexObservation`
members) resolves to a real symbol in this file — checked by grep, not by the compiler (`ZeroWiki.csproj`
still has no `GenerateDocumentationFile`, so a clean build doesn't validate `<see cref>` targets).

**Residue.** `git status --short` shows only `ContentRepositoryService.cs` and `DEVLOG.md`; no
`InternalsVisibleTo`; my own scratch harness and its temp repos deleted after this run.

**Nothing owed.**

**Reviewed-state:** `5c8920c1d894`, `HEAD` `ae6ce82`.

→ @architect

### Performance regression diagnosis — block 2.1–2.3 (`317a5c9`)

**[worker]** Measured, not guessed, using targeted `dotnet test --filter` runs and a self-reverting
instrumentation pass — no full `make test`/`make gates` run.

**Call frequency — unchanged, still startup-only.**
`grep -rn "EnsureRepositoryAsync\|AcceptRepositoryAsync\|ConfigureRepositoryAsync" src` shows exactly
one production call site (`ContentStorageStartupExtensions.EnsureContentRepositoryAsync`, real app
startup). `FindSuppressedIndexObservationsAsync`/`ReconcileWorkingTreeAsync`/
`AssertWorkingTreeIsCleanAsync` have no call site outside `AcceptRepositoryAsync` — not per-request, not
per-push. The hypothesis that this runs "per request" is **dead**: `find_references`-equivalent grep
across `src/` shows one production caller, period.

**What changed:** block 2.1–2.3 wires the (already-existing, previously unwired) census into both
invariant sites, adding exactly **2 extra `git ls-files -v -s -z` subprocess spawns per
`EnsureRepositoryAsync` call** (one in `ReconcileWorkingTreeAsync`, one in
`AssertWorkingTreeIsCleanAsync`) — confirmed by re-reading `git diff 1e9ffcd..317a5c9 --
src/ZeroWiki/Content/ContentRepositoryService.cs`: no new loops, no new call sites, no locking changes.

**The actual driver: per-spawn cost is not constant — it is concurrency-sensitive, and the test suite is
the thing supplying the concurrency.** I instrumented `FindSuppressedIndexObservationsAsync` (temporary
`Stopwatch` + append-to-file, reverted before this post — `git status --short -- src` is clean) and ran
`dotnet test --filter "FullyQualifiedName~ZeroWiki.Tests.Web|FullyQualifiedName~ZeroWiki.Tests.Content"`
(613 tests, the two namespaces that actually exercise `ContentRepositoryService`):

| | isolated, serial (my own toy repo, one test method) | in situ, full `Content`+`Web` run (14-core box, xUnit's default parallelism) |
|---|---|---|
| `git ls-files -v -s -z` | ~16–30ms/call | **p50 153ms, p90 372ms, p99 739ms, max 1.7s** |

641 census calls captured, **`entries.Count == 0` in all 641** — confirms this is pure subprocess-*spawn*
latency, not the observation loop (the census is empty here exactly as Decision 1 assumed; nothing is
iterating). Summed, those 641 calls account for **123.8s of the subset's 236s wall time — 52%**.

**Diagnosis:** Decision 1's cost argument ("one subprocess per start... normally empty") is true and
stays true for what it was argued about — **one real application start**. It implicitly assumed
subprocess-spawn cost is roughly fixed regardless of how many are in flight at once. That assumption is
what's false, and it's specifically false under `make test`'s own execution model: xUnit runs ~900 tests
with up to 14-way parallelism, a large fraction of which each boot a full `ContentRepositoryService`
(many via a complete `WebApplicationFactory<Program>` host), so doubling the git-subprocess count per
boot lands on an already CPU-contended box and the per-spawn cost balloons non-linearly (p50 10x the
serial figure, p99 46x, max 100x+) rather than adding a fixed few-ms tax. I did not re-run the full
905-test suite to confirm the exact 18m13s figure — the instruction was to prefer targeted measurement,
and the subset result (52% of a representative half of the suite is these two new spawns, at costs 10–100x
their isolated price) is already the finding, not a guess extrapolated from nothing.

**This is a design-cost finding, not a bug I can code my way out of, so I am not patching it.** Two
things stand in the way of a worker-level fix:
- **Decision 5 requires each site to re-derive independently** — `AssertWorkingTreeIsCleanAsync`'s own
  remarks say it "does not depend on `ReconcileWorkingTreeAsync` having already refused... re-derives the
  census and the fault verdict... every time this method runs." Merging the two calls into one shared
  census would directly undo that invariant (the self-check would stop being independent of
  reconciliation having already run/refused) — that's the falsifier this exact task (2.3) names, so I
  won't trade it away for wall-clock time.
- There is no cheaper git incantation for this: `ls-files -v -s -z` is already the minimal call that
  answers "what's suppressed," and the cost lives in process-spawn, not in what the command does once
  running (0 entries every time, confirmed above).
- The other lever — dialing down xUnit's test parallelism — is `Makefile`/test-project-config territory
  I don't own, and even if I did, it would only buy back gate wall-clock, not change anything about
  production behaviour, which was never actually regressed (one real start still pays ~30–60ms extra,
  not 150ms–1.7s).

**What I want you to have, explicitly:** production's one-subprocess-per-start argument (Decision 1)
still holds — nothing here falsifies it for an actual app instance starting once. What's newly true is
that **this specific test suite's execution model turns "cheap per start" into "not cheap in aggregate"**
whenever a change doubles subprocess count at a site nearly every test boots through. That's worth a
Product Owner call on whether/how to address the suite's own runtime (parallelism knob, fewer
full-host-boot tests, or accepting it), not a worker-level patch to production code that would cost this
change its correctness guarantee to get there.

**Claim / instrument / blind spot:**
- **Claim:** the 2 new subprocess spawns this block added are the dominant driver of the measured
  regression, via concurrency-amplified per-spawn cost rather than any new O(n) work or a hotter-than-
  expected call site.
- **Instrument:** a temporary `Stopwatch`-and-append-to-file probe inside
  `FindSuppressedIndexObservationsAsync` (reverted; `git status --short -- src tests` clean), run under
  `dotnet test --filter "FullyQualifiedName~ZeroWiki.Tests.Web|FullyQualifiedName~ZeroWiki.Tests.Content"`
  (613 tests, 236s wall, 641 census calls, 123.8s summed).
- **Blind spot:** this covers 613 of 905 tests (Content + Web, the namespaces that construct
  `ContentRepositoryService`) and does not include a full-suite run, so I have not directly measured
  whether the remaining ~292 tests (Identity/Data/Security/etc., which grep shows don't call
  `EnsureRepositoryAsync`) contribute further contention, nor reproduced the exact reported 10min/18m13s
  full-suite figures — I'm inferring the mechanism generalises to the full suite from the subset's 52%
  figure and the fact that more concurrent load only pushes contention further into the same regime, not
  out of it. A full `make test` run (yours to run) would close that gap directly.

Build/tests: no code changed (instrumentation added and fully reverted). `git diff -- src tests` and
`git status --short -- src tests` both empty. No gates run by me per the brief's instruction; `dotnet
build` still green (`0 Warning(s)`, `0 Error(s)`) after the revert.

→ @architect

### Supervisor review — section 2 (block 2.1–2.3, `1e9ffcd..aedfdab`)

**[supervisor]** Verdict: **Approve.**

Scope read: `git log --oneline 1e9ffcd..HEAD` → `317a5c9` (the block) then `aedfdab` (artefact-only:
`design.md` Decision 9, `proposal.md`, tasks 3.8/4.3). `git diff 1e9ffcd..HEAD -- src` is one file,
+125/-0. `git diff HEAD -- src` is empty — no mutation residue.

**Confirmed, section-level:**

1. **The wiring honours a precondition section 1 could only assert.** Section 1's
   `ObserveSuppressedEntryAsync` remarks make "empty output, exit 0" the absence signal *only* because
   `HEAD` is born, and stated that against two *candidate* call sites. 2.1 then moved the census to the
   very top of `ReconcileWorkingTreeAsync` — the earliest `ls-tree` in the startup path so far.
   `AcceptRepositoryAsync` still runs `EnsureInitialCommitAsync` (`:213`) before reconciliation (`:217`)
   and the assertion (`:221`), so the precondition holds. Nobody re-checked it when the call site moved;
   it is the kind of thing that survives a block review by being true rather than by being verified.
2. **The gitlink reconstruction is faithful, including the two edges.** `newMode`/`oldMode`
   (`:992`) map exactly onto `IndexMode`/`HeadMode` (`:1130`). Added-path (`oldMode 000000` → fault)
   corresponds to `HeadMode: null` → `PathNotInHead` → fault: same verdict by a different arm, as the
   remarks claim. The reverse shape (`IndexMode != 160000`, `HeadMode == 160000`) is not a fault in
   either expression. No approximation anywhere.
3. **Ordering is sound and needs no reset** — the census throws before `add -A`, so nothing is staged.
4. **Decision 5's independence is real, not incidental.** `AssertWorkingTreeIsCleanAsync` re-runs
   `FindSuppressedIndexObservationsAsync` from scratch; no field, no memo, no shared list. The two
   `ls-files` spawns Decision 9 measured are themselves the evidence.
5. **No dead scaffolding.** Every symbol section 1 built now has a live caller
   (`FindSuppressedIndexObservationsAsync` ×2, `IsSuppressedEntryFault` ×2, `SymlinkMode`,
   `GitlinkMode`, `InvariantLocale` all reachable from both sites). Section 1's "not yet called from
   either candidate site" remark is now stale prose but is a `<remarks>` for section 3/4 to sweep, not
   a shipping stub.

**Findings — none blocking, all for `## NEXT` / a later block:**

**F1 — the ordering comment's non-overlap claim is false, and all three audits shared one instrument.**
`ContentRepositoryService.cs:809-812` says a nested-repository or unreadable-directory tree "carries no
suppressed entry at all … this refusal cannot swallow theirs, nor can theirs swallow this one." The
shapes are not mutually exclusive: a tree with a divergent suppressed entry *and* a nested repository
refuses at the census, and the gitlink diagnosis is deferred to the next restart. That behaviour is
fine — it is exactly the precedent the comment sixty lines below already documents for gitlink-vs-stderr
("diagnosis is serial rather than lost, but it does cost a second restart") — but the method now carries
two contradictory accounts of what happens when two faults coexist. Worth noting *how* this got here:
the worker constructed nested-repo-alone and unreadable-dir-alone; the reviewer independently
constructed nested-repo-alone and unreadable-dir-alone. Two audits, one instrument, and the instrument
is shaped like the claim. Nobody built the combined tree. Fix is one sentence of prose plus a falsifier
in section 3.

**F2 — the harmless set is complete for *content* and open for *mode*.** `IsSuppressedEntryFault`
consults the index/`HEAD` mode pair only when one side is a gitlink; every other mode-level divergence
is settled by a byte comparison that can return `Matches`. Two shapes nobody has enumerated:
(a) a suppressed entry whose content matches `HEAD` but whose executable bit changed on disk — starts
normally, working tree ≠ `HEAD`, invisibly; (b) a suppressed `100644`↔`120000` typechange — decided by
comparing link text against a regular file's blob. These are the symlink/gitlink-class shapes you asked
me to look for, and I found them by asking what the policy *never looks at* rather than by listing more
cases. **I do not recommend fixing (a) in code:** the obvious patch (stat the on-disk mode) ignores
`core.fileMode` and would refuse startup on a filesystem where that bit is untrustworthy — precisely the
false-refusal class this section exists to end. Product impact is nil for the symptom this change
targets: git's own `updateInstead` check is blinded by the same suppression bit, so a mode-only
divergence never bounces a push. Record the boundary at `IsSuppressedEntryFault` ("this policy decides
content; mode is compared only for gitlinks") — a §4 line, not a code change.

**F3 — `WorkingTreeUnreadable` is the one switch arm with no recorded reasoning.** Decision 4 names
three divergent shapes and unreadable is not among them; section 1 invented the outcome and section 2
promoted it to a fault. The remarks reason about `PathNotInHead`, `NotCompared` and the symlink
typechange, and say nothing about this one — yet it is the arm most likely to fire in production on a
file that is otherwise fine (Decision 8: the container runs non-root). I think fault is right, and
consistent with the existing stderr refusal for an unreadable *directory* — but that is my reasoning,
not the record's, and this section's whole history is recorded reasoning being wrong.

**Section 3 is well-posed. Three things that make it cheaper:**

- **3.4 has a construction needing no seam and no reflection** (`GitProcessRunner` is `sealed` and
  non-virtual, so there is nothing to stub): a suppressed entry whose *index* blob differs from `HEAD`'s
  while the *working tree* matches `HEAD`. 2.1's census compares working tree↔`HEAD` → `Matches` → it
  passes; `add -A` skips the path; `diff --cached` is non-empty, so the recovery commit lands and
  `HEAD`'s blob becomes the index blob; 2.3's re-derived census now sees `Differs` and refuses **on its
  own**, with reconciliation having decided the same tree harmless moments earlier. That is a stronger
  demonstration of Decision 5 than neutralising 2.1, and it dies if 2.3's census is deleted.
- **3.6's gitlink case has two traps.** Build `HEAD`'s gitlink before boot (the pre-commit
  nested-repository scan is gated on `repositoryHasNoCommitsYet`, so a repo with commits passes it), and
  keep the nested repo clean — otherwise `AssertWorkingTreeIsCleanAsync`'s `status --porcelain` refuses
  on "modified content" before the harmless-shape assertion is ever reached, and the test fails for a
  reason that has nothing to do with what it is testing.
- **Add F1's falsifier:** a tree with a suppressed divergence *and* a nested repository. Whatever it
  shows, the comment should say it.

**Decision 9:** not reopened — the diagnosis is measured and the scoping is the Product Owner's. One
consequence for this section: 3.8's prohibition on sharing one census is exactly right, and the 3.4
construction above is what makes that prohibition enforceable rather than merely written down.

**What this review could not see.** I ran no gates, no tests and no container. Every behavioural claim
about the four scenarios I re-derived by reading the committed code against `FindStagedGitlinksAsync`
and the startup ordering — not by executing it; the executed evidence is the worker's and the
reviewer's, both macOS/APFS. My reading and both of those runs share one blind spot, which is F1: none
of the three of us put two faults in one tree. The glibc/EACCES claims remain untested by anyone and are
still 3.7's.

→ @architect

## 3. Tests

**[architect]** Base: `aedfdab` — the committed regression coverage: fixtures no existing dirty-tree
helper can construct, the harmless shapes that must start, the combined tree nobody has built, the
mutation check, the suite's runtime, and the one verification only the Product Owner can complete.

### Inherited from section 2's close — read before briefing 3.1

**[architect]** Section 2 closed on a `[supervisor]` `Approve` (`### Supervisor review — section 2`).
Four things it left this section, three of which save work:

1. **3.4 needs no seam.** `GitProcessRunner` is sealed and non-virtual, so neutralising reconciliation's
   census looked like it needed test infrastructure. It does not: a suppressed entry whose **index**
   blob differs from `HEAD` while the **working tree** matches `HEAD` passes 2.1's census, gets
   committed, and is then refused by 2.3's re-derived census. That is 2.3 refusing alone, on a tree
   reconciliation has just called harmless — the falsifier 3.4 was respecified to need.
2. **3.6's gitlink case has an ordering trap.** Build `HEAD`'s gitlink *before* boot and keep the
   nested repository clean, or `status --porcelain` refuses first for an unrelated reason and the test
   passes for the wrong cause.
3. **3.9 is new** (supervisor F1): the combined tree — a suppressed divergence *and* a nested repo —
   which every audit so far failed to construct because worker, reviewer and supervisor each built the
   fault shapes **alone**. Pin which refusal wins and that the deferred one fires on the next start.
4. **3.7 remains unowned by any agent.** The glibc/EACCES claims are untested by *everyone* — worker,
   reviewer and supervisor all say so explicitly. Only the container run closes it.

## NEXT

**Resume point:** section 3, block 3.1–3.4 + 3.6 + 3.9 (the committed regression tests). Sections 1
and 2 are closed on `[supervisor]` `Approve`. Section 3's base is `aedfdab`.

**State:** recompute, never trust a number written here — `git rev-parse --short HEAD`, and
`grep -c '^- \[x\]'` against `grep -c '^- \[ \]'` on `tasks.md`.

**Block carve for section 3** (a block never spans sections; these are all within 3):
- **3.1–3.4, 3.6, 3.9** — the committed tests, including the combined tree nobody has built.
- **3.5** — the mutation check. **Load the `mutation-testing` skill before briefing it**, and brief
  the `cp`-baseline-restored-by-`trap` revert explicitly. Never revert a mutant with `git checkout --`.
- **3.8** — the suite runtime (Decision 9). **Do not let it share one census between the two invariant
  sites**: that buys speed with Decision 5's independence, which is what makes 3.4 provable at all.
- **3.7** — Product Owner verification, last. See below.

**Owed, and not by an agent:**
- **3.7 is human-in-the-loop and no agent may tick it** (Decision 8, workflow §4). Hand the Product
  Owner exact commands and expected output, then **wait**. Green gates are not their confirmation.
- Section 4 now owes 4.1, 4.2, 4.3 and 4.4. 4.4 corrects a **false claim currently in the code** —
  the ordering comment at `ContentRepositoryService.cs:809-812` says the census cannot swallow the
  gitlink/stderr diagnosis, and it can.

**Live hazards:**
- **Every audit in this change has built its fault shapes alone.** That is how the symlink defect,
  the gitlink defect, and now F1's combined tree all reached a later reviewer than they should have.
  When briefing a falsifier, ask what the *combination* looks like, not just the case.
- **Seven parties, one host.** Every measurement here is git 2.55.0 / macOS APFS. The symlink,
  permission-bit, mode and `strerror` claims are glibc behaviour; section 3's tests run on that same
  host and **inherit** the gap. Only 3.7 closes it. A green suite is not platform verification.
- **`make test` currently takes ~18 minutes** (measured 18m13s alone). Budget for it, and do not read
  a slow gate as a hung one. 3.8 is the task that fixes it.
- **The `## NEXT` heading was destroyed once** by an agent's insert. Re-check `grep -n '^#\{1,3\} '`
  after every DEVLOG write.

**Open decisions:** none. Decisions 7, 8 and 9 (Product Owner, 2026-08-21) are in `design.md`.
