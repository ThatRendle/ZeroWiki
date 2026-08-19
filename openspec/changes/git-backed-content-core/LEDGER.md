# LEDGER — `git-backed-content-core`

**What this file is.** The permanent home for this change's accumulated obligations, carry-forwards and
standing rules. Everything below was written into `DEVLOG.md`'s `## NEXT` pin between §0 and §11 and was
**relocated here verbatim on 2026-08-19** by Product Owner decision — unchanged in wording, unchanged in
order, nothing pruned.

**Why it exists.** The pin had reached 1,084 lines because three ledgers — *Forward obligations* (32
numbered, most still live), *Close-out items before archive*, and *Standing rules earned in §0–§11* —
were parked **in the pin** rather than in section threads. That made `CLAUDE.md`'s rule ("`## NEXT` holds
only the handover; everything else already lives permanently in the section threads, link to it")
impossible to follow as written: there was nothing to link to, and cutting to ~80 lines would have
**deleted** live obligations rather than moved them. The pin being too long was a legibility problem;
deleting obligations would have been a correctness one. This file is the home that resolves it, and
`## NEXT` is now a real handover that points here.

**How to read it.** This is a relocated pin, not a curated document. It carries discharged items — struck
through, or marked discharged in place — alongside live ones, and the two items its own opening sections
schedule are now **both discharged** (the dmons scaffold migration landed in `80da8a3`; this relocation is
the other). Read strike-throughs as history. Treat an unstruck numbered obligation as **live** until the
section that owns it says otherwise, and verify it against the code before acting on it — several entries
here were written many sections ago and this change's own record shows a stale pin entry riding along
unchallenged more than once.

**How to maintain it.** Append and strike through; do not rewrite and do not prune. It is archived
alongside `DEVLOG.md` when the change is archived, and *Close-out items before archive* below is a
precondition of that archive, not a wish list.

---

### ⚠️ The workflow changed on 2026-08-16 — read this before briefing anything

A retrospective with the Product Owner (after block A) changed how blocks are briefed, reported and
audited. **These are live now and bind the next block, `10.2`+`10.3`.** All are committed; this is the
index, the files are authoritative.

- **Claims carry an instrument.** `e5844c9` — any report that something is complete, exhaustive,
  covered, the only one or unaffected states **claim / instrument / blind spot** as three labelled
  lines, and "blind spot: none" is never correct. The reviewer must re-run the instrument rather than
  read the argument. *Why this shape:* block A's brief already said "derived from the harness, not the
  two tests that flaked" and still came back file-scoped — **an instruction in a brief does not work
  where a required field does.**
- **Tasks get classified before implementing.** `80edc57` — the worker calls each task **build / audit
  / finish** and posts the call before writing code, stopping with `❓ @architect` if it disagrees with
  the brief. `tasks.md` was written before any code existed; `10.1` read as "write these tests" over 73
  existing ones. The reviewer judges that call, and **must not treat an empty diff as an absent
  deliverable** when the analysis is the deliverable.
- **Verdicts name the state they certify.** `5de9c4f` + `9025a78` — every verdict ends with
  `Reviewed-state: <hash>`, and `CLAUDE.md` §3b **step 6** (new; steps renumbered) has the Architect
  recompute it before ticking or committing. `git diff HEAD` alone is blind to untracked files, so the
  fingerprint includes `ls-files --others` content. **Block A predates this** — `4dc6de0` carries no
  fingerprint, which is expected, not drift.
- **Briefs name the falsifier.** `d7a517c` — `CLAUDE.md` §3b.1: alongside "build X", name the
  observation that fails if X is undone. **And rank them against the mutation cap** — block B's brief
  named five falsifiers against a cap of three, leaving the allocation to be discovered by the worker.
  When they outnumber the cap, the brief says which are expected to go unmeasured.
- **The `Reviewed-state` fingerprint excludes the DEVLOG**, at the pathspec level — the rule was
  self-referential and unsatisfiable as written. Product Owner decision, 2026-08-18; the command and its
  reasoning are in `CLAUDE.md` §3b step 6 and `.claude/agents/reviewer.md`. It certifies the **code** the
  reviewer read, not the record.
- **Unreachable code is swept for.** `d7a517c` — reviewer runs `find_uncovered_symbols` over new
  symbols, supervisor across the whole section. Neither tests nor mutation can see code nothing
  reaches; that is exactly how §12's defect shipped.
- **Mutation runs go through `.claude/skills/mutation-testing/mutate.sh`** — `d7a517c`. Revert in a
  `trap` an interruption cannot skip, content-checksum before and after, no-op refused, unique search
  string required, full unfiltered suite, no pipe. Exit `0` killed / `1` survived / `2` harness fault.
  Verified end to end against the CAS compare. **The caps are still yours to obey — the script cannot
  hold them.**
- **`##` is reserved for section headings; posts start at `###`** — `233997a`. ~40 existing posts break
  this; they are left alone because the DEVLOG is append-only.

**Also decided: Stryker.NET is rejected, with measurements.** The `.slnx`/net10.0 disqualifier *passed*
and the instrumented baseline is green, but `src/` yields **1631 mutants** each needing a full suite,
and **Safe Mode removes every mutation in `HandleReceivePackAsync`** (CS0165 under schemata injection),
along with `AcceptRepositoryAsync`, `CollectNestedGitEntries` and `Walk`. **§12's target method is the
one Stryker cannot mutate.** Do not reach for it there.

**Owed at the §10 close, before §12:** distil this pin. It is over 1,000 lines — longer than five of the
twelve sections — against a stated target of ~80. The rule is now in `CLAUDE.md`: NEXT holds only what
the next session needs *before* reading anything else; everything else already lives permanently in the
section threads. Deliberately deferred rather than done mid-section, so nothing block B still needs gets
cut.

### ⚠️ Scheduled at the §10 boundary — migrate the dmons scaffold before opening §12

**Product Owner decision, 2026-08-16.** When §10 closes (supervisor `Approve`) and **before §12 opens**,
run `/dmons:update-scaffold`. This repo is stamped **`0.3.0`** in all three `.claude/agents/*.md` and in
`CLAUDE.md`; the plugin is **`0.5.1`** — **four migrations** behind (`0.3.1`, `0.4.0`, `0.5.0`, `0.5.1`).
The newer templates route every gate through a `Makefile` ("the only way you run a gate", never the
underlying command) and ship `dmons-guard.sh` / `dmons-tripwire.sh`, none of which exist here. That is
the durable fix for this change's recurring shell hazards — the sandboxed `dotnet`, the piped `tail`
whose exit code is `tail`'s — which have been prose warnings that the Architect itself broke while
quoting them.

**Two conditions on the run, both load-bearing:**

- **Verify which version of the skill actually loaded.** `/dmons:update-scaffold` has been observed
  loading an *older cached copy of itself* and reporting "already current" — its `migrations/` stopped at
  the version the repo was stamped with, so the failure presented as success and the repo's own stamps
  agreed, both coming from the same stale source. Check the skill's base directory against
  `ls ~/.claude/plugins/cache/dmon-dev/dmons/`. If it loads stale, read `0.5.1`'s `SKILL.md` and
  `migrations/*.md` from the cache and follow those by hand — following a newer migration note is not the
  same as inventing a migration.
- **Confirm each of the four migrations individually, and re-check `e5844c9` survived.** That commit
  hand-edited all three agent definitions (the claim/instrument/blind-spot field). The skill claims to
  preserve hand edits; four versions of migration over freshly hand-edited files is exactly where that
  claim gets tested. Clean tree before starting, so it is trivially revertible.

**Resume point: §12 — `12.1`–`12.4`, not carved. §10 is CLOSED** (`@supervisor` `Approve` over
`59db4dd..8e3591d`, one remediation round; close-out post at the end of `## 10.`). **§1–§11 are now all
closed with a supervisor `Approve`; §12 is the only open section.** Count tasks from `tasks.md`
(`grep -c '^- \[x\]'`), never from this pin.

**⚠️ Two things are owed BEFORE §12 opens, both Product Owner-scheduled:**

1. **Run `/dmons:update-scaffold`** — the migration described further down this pin, with both of its
   conditions (verify the skill is not a stale cached copy of itself; confirm each of the four
   migrations, and that `e5844c9`'s hand edits survived).
2. **Distil this pin — and it cannot be done as the rule describes.** `CLAUDE.md` says NEXT holds only
   the handover and everything else "already lives permanently in the section threads". **That is false
   here.** The three ledgers below — *Forward obligations* (26 live), *Close-out items before archive*
   (F1, F2) and *Standing rules earned in §0–§11* (18) — were parked **in this pin**, not in threads, so
   there is nothing to link them to and cutting to ~80 lines would delete them. **Open question with the
   Product Owner, 2026-08-18:** relocate the ledgers verbatim to a permanent home (a `##` heading that
   is not a task section, or a sibling `LEDGER.md` archived alongside) and let NEXT become a real
   handover. **Do not distil until that is settled** — the pin being too long is a legibility problem;
   deleting 26 live obligations is a correctness one.

**Also open with the Product Owner, raised at §10's close:** §10 was the **third** appearance of
claim-versus-mechanism mismatch in this change (§2's five, §11's seven claims-about-why, §10's two).
Both of §10's were found by *reading*, in a section about instrument honesty, and no gate in this repo
would catch a third. The recurring-class rule says the deliverable's rule is wrong rather than the
sentences — so a fourth round of correcting comments is precisely what not to do. Not answered; see the
`[architect]` close-out under `## 10.`

§10's `Base:` post is `59db4dd` and is already in the `## 10.` thread — do **not** post another; the
supervisor's review scope for the whole section runs from it.

**Block A landed in `4dc6de0`** — `10.1` plus the credential-helper pin (no task number, Product Owner
ruled it into §10). `10.1` is a **gap analysis that deliberately added no test**: the 7 spec scenarios
behind *Optimistic concurrency on save*, *Transactional save* and the working-tree-clean invariant (D9)
were each stated as a one-line `src/` falsifier and the existing test it kills, and all 7 held. Reviewer
`Approve` after independently re-running the instruments. **Do not re-open `10.1` by writing tests over
it.**

**§10 landed in three commits:** block A `4dc6de0` (`10.1` gap analysis + credential pin), block B
`3e22b8f` (`10.2`+`10.3`), remediation `8e3591d` (the supervisor's S1 — a spec scenario witnessed by a
test that could not fail). Its `Base:` was `59db4dd`.

**§10 is no longer the last section — §12 was added 2026-08-16 by Product Owner decision** (`12.1`–`12.4`,
the push→viewer broadcast; the full reasoning and its escape hatch are in the `## 12.` thread above, read
it before opening that section). **41 → 45 tasks.** Count from `tasks.md`, never from
this pin. §12 runs *after* §10: §10 is bounded and carved, §12's root cause is not isolated, so keeping
§10 first means the change is never more than one bounded section from releasable.

**§9 is CLOSED** — supervisor `Approve`, all of F1–F4 resolved, `9.1`–`9.3` ticked on the Product
Owner's explicit confirmation (`9aee333`, ticks in the commit that follows it). **§1–§9 and §11 are all
closed with a supervisor `Approve`.** Nothing in §9 is owed to anyone; do not reopen it.

**Read the `[architect]` close-out post at the end of `## 9.` before starting §10.** It records what the
three ticks do and do not assert — `9.1` is ticked as *documented*, with **only macOS verified against a
running ZeroWiki** — and the instrument lesson that cost that section five review verdicts: **a third
party's prose documentation cannot answer a question about that third party's capability; only its
source can.** §10 is a testing section, so the sibling rule matters here too: **a run proves a path
works, never that it is the only path.**

**Nothing is owed to the Product Owner.** Every human-in-the-loop obligation this change carried has
been discharged: the Obsidian round trip, the rejection and its recovery, the conflict resolved in
Obsidian, and the vault setup are all confirmed by their own run; the clone/edit/push cycle and the
documented route are proven by automation. **Do not ask them to re-verify any of it.**

**Working tree CLEAN. State: 41/45 tasks ticked** — the four open are §12's `12.1`–`12.4`. **Count from
`tasks.md`, never from this pin** (`grep -c '^- \[x\]'`): a wrong figure written here once rode along
through two blocks, and this pin has been wrong about its own resume point three times.

**Three changes are queued behind this one's archive**, each amending code or a capability that exists
only on this branch, and none applicable before it archives. They touch disjoint code, so their order is
free:

- **`fix-changed-on-disk-broadcast`** (`fe7ecc8`) — the push→viewer broadcast does not reach a live
  circuit. `git-sync` specifies it, so **this change is on course to archive claiming a requirement its
  code does not satisfy.** Surface that at the archive decision; the supervisor asked for it explicitly.
- **`ignore-obsidian-config-in-content-repo`** (`f2dc957`) — bootstrap seeds a `.gitignore` for
  `.obsidian/`. Ignore-only by decision: untracking would delete a user's editor configuration out of
  every vault on its next pull.
- **`fix-reconciliation-index-blindness`** — D9's startup reconciliation verifies the tree with
  instruments an `--assume-unchanged` index entry blinds.

**One follow-on parked with the Product Owner, belonging to no section:**

- **`credential.helper = osxkeychain` is active on this machine at *system* scope** — Homebrew git's own
  config, `file:/opt/homebrew/etc/gitconfig`, **not** `--global`, which prints nothing and reads as "no
  helper set". Under the full parallel suite a credential cached by one real-git test can be offered to
  another's git process; `UnauthenticatedClone_FailsAtTheClient` failed once with `Authentication failed`
  instead of the expected `terminal prompts disabled`. It predates §9 and is inherent to §7's real-git
  pattern. **The suite can write real credentials into the developer's OS keychain.** Pinning
  `-c credential.helper=` on client-side git invocations closes both. **The Product Owner has ruled this
  IN SCOPE for §10** (2026-08-16). It carries no task number of its own — `10.1`–`10.3` are unchanged —
  so carve it into a block explicitly rather than expecting a numbered task to cover it, and say so in
  the block brief.

**§10's carve, proposed but not yet briefed — the Architect's call, revisit it rather than inherit it.**
Two blocks: `10.1` alone, then `10.2`+`10.3`, with the keychain pin folded into whichever block touches
the real-git client invocations. The reasoning: `10.2`'s concurrency test (interleaved browser save and
push, serialized, never a dirty tree) is the one most likely to need iterating, and pairing it with a
third task rather than with `10.1`'s three separate concerns keeps a failing block small. **Post §10's
`Base:` sha before briefing anything** — the supervisor's review scope depends on it, and §9's own base
post is what made its review possible.

**A caution for §10 specifically, earned in §9.** This is a testing section, and its tasks name
properties — CAS rejection, transactional rollback, reconciliation, serialization, rebuild-from-repo —
every one of which a test can appear to cover while asserting nothing. This change's whole record says a
green suite is not evidence: **brief the falsifier, not the deliverable**, and require each new test to
be *watched failing* against a mutant before it is believed. The `> 0` floor added to
`ReadmeGitRemoteDocumentationTests` in §9's remediation is the pattern in miniature — a guard whose only
job is to stop the assertion above it iterating zero times and passing vacuously.

### ⚠️ Harness fact from §9 — run every `dotnet` command UNSANDBOXED

**A sandboxed `dotnet` fails after exactly `00:05:00` with `Build FAILED`, `0 Warning(s)`, `0 Error(s)`
and no other message.** The real cause only surfaces in a `dotnet test` log:
`SocketException (13): Permission denied` on a **named-pipe bind** — the sandbox blocks the Unix domain
sockets MSBuild's out-of-proc nodes and the Roslyn compiler server need. `0 Error(s)` alongside
`Build FAILED` is the tell; the five-minute round number is the second tell.

**`sandbox.excludedCommands: ["dotnet"]` in `~/.claude/settings.json` does NOT take effect** — a *bare*
`dotnet build` was still sandboxed and still failed. So the exclusion cannot be relied on; pass
`dangerouslyDisableSandbox: true` on every dotnet Bash call and brief agents to do the same.

Two further self-inflicted failures from the same session, both worth not repeating: **never run
`dotnet build` and `dotnet test` concurrently** (they contend over restore and MSBuild nodes, producing
`Restore operation failed` and internal MSBuild errors that look like real gate failures), and
**never pipe a gate to `tail`** — the pipeline's exit code is `tail`'s, so a failed run reports success.
Capture each gate's own `$?`.

> **Correction, made at §8's close-out and recorded rather than silently fixed.** The first version of
> this paragraph — and the `2942a61` commit body — said **"41/41 tasks ticked, every numbered task is
> done."** That was **wrong**: `grep -c '^- \[x\]'` gives **35**, `grep -c '^- \[ \]'` gives **6**. The
> error was the Architect's, made by arithmetic in prose rather than by counting the file, and it is the
> same defect class this change has punished all the way through — **a claim about an artefact, not
> derived from the artefact.** The commit body is left as it stands (history is not rewritten for this);
> **this paragraph is authoritative over it.** §10 in particular is not optional cleanup — it is three
> unticked tasks with their own supervisor review still to come.

Branch `change/git-backed-content-core`, HEAD **`03107e2`**. Gates at §8's close, run in the foreground by the Architect **unsandboxed**: `dotnet build`
0/0; `dotnet test` **876/876** unfiltered in 3m15s; `dotnet format --verify-no-changes` exit 0;
`openspec validate --strict` valid; no `MUTANT` residue.

**§8 landed in four commits:** A `7df5e87` (D19, design only), B `e972577` (8.1 + 8.2, and SignalR enters
the app), C `f985c5e` (8.3), then one supervisor remediation `03107e2`. Two supervisor rounds.

### ⚠️ Two things are owed to the Product Owner and neither can be discharged by an agent

1. **The "changed on disk" banner's live behaviour** — does the circuit connect, does the banner appear
   without a reload. Recipe is in the §8 thread. **Nobody has yet watched a circuit connect**; the
   supervisor named this as the honest limit of §8's evidence.
2. **The git remote's clone/edit/push cycle from a real client**, recipe in the §7 thread (port 5171).

### The standing rule §8 earned, and the briefing change that follows from it

**An assertion on a string the page renders for another reason is not an assertion.** Both attribution
tests asserted `Contains("alice", body)` while `NavMenu.razor:38` renders `Sign out alice` on every page —
unfailable, and they would have passed with attribution deleted entirely.

**The briefing fix, from `@supervisor`'s own diagnosis and worth more than the finding that prompted it.**
§8's failures were **not** a testing-skill gap. In all four cases the *deliverable* was verified and the
*claim about* the deliverable was not — and the pattern tracks how each was briefed: **where a brief
framed something as a property** (the resolver ordering, the shared dot-atom grammar, the trigger point)
**the evidence was excellent; where it framed something as a deliverable, nothing asked what would have to
be false.** One sentence per task closes it: **alongside "build X", name the observation that fails if X
is undone.** Three of §8's five findings would then have been block-level catches. Apply this to every §9
and §10 brief.

### §8 carry-forwards

- **The zero-consumer shape appeared three times in one section** — a resolver nobody called (caught by
  the Architect), and a component nobody's test reached (caught by the supervisor, one link further down
  the same chain). **Tests cannot catch it: they test what exists, not whether anything reaches it.** Ask
  of every new type: what fails if I delete its *usage*, not its *implementation*?
- **Note 4 is FIXED IN CODE — strike it, do not carry it.** The `/_blazor*` exemption is now asserted from
  endpoint metadata for all four routes.
- **Two residuals from the remediation itself.** The `IsInteractive` guard is the one verification no
  auditor independently re-ran — the unseen case is a DI spy that never won its registration, which would
  make `SubscribeCallCount == 0` hold vacuously. And `InteractiveComponentSurfaceTests`' surface list is
  **hand-written** — six of eleven `@page` routes, with `/bootstrap` and `/bootstrap/complete` unchecked.
  Driving it from the router's own route table is the durable form, and it will matter when the editor
  surface lands.
- **One behavioural delta recorded and deliberately not fixed:** on shutdown, a cancelled index warm now
  lets one doomed `git diff` launch. The obvious tidy-up would re-couple exactly what the fix decoupled.
  Leave it.
- **A push whose client disconnects between the response being written and the after-probe completing
  skips that push's reaction entirely** — and the exception escapes the endpoint *after* the response is
  written. D15's lazy stamp check still covers correctness; the freshness signal is what is lost.
- **The two-line `catch` in `WikiPage.razor`'s attribution path is genuinely untested** code on a
  page-rendering path. Stated plainly by the worker, the reviewer and the supervisor rather than papered
  over.

### The Architect's own error in §8, recorded because the rule already existed

**I committed code no reviewer certified.** After `@reviewer` approved block C, I sent back a flake fix
that **rewrote the very assertions the security evidence rested on**, and committed it without re-audit —
so a 5/5 mutation result ended up describing a test that no longer existed in that form. **Third instance
of this gap in this change; first with a concrete consequence.** A post-approval fix that touches an
assertion goes back to the reviewer, however small it looks.

### ⚠️ Harness fact that will cost the next agent an hour if it is not read

**`dotnet` fails inside the sandbox on this machine, and the failure looks exactly like a code failure.**
`dotnet build` returns *"Build FAILED. 0 Warning(s) 0 Error(s)"* after stalling five minutes;
`dotnet format` throws a `FormatCommandCommon.FormatAsync` stack trace. **Run every gate unsandboxed and
in the foreground** — ~1s, ~5s, ~3m. And **never background `dotnet test` and wait on it**: seven agents
in this change have stalled that way.

**`git add -N` a new file the moment a block creates it.** `git diff` reports *nothing at all* for a file
git has never tracked — §7 block B had seven such files, including both mutation targets.

### §7's carry-forwards — still live, still worth reading before §9

**Resume point was §8; §7 is CLOSED**: supervisor `Approve` over `cb81b47..ef91bd4` at the second section
round.

**State at §7's close: 36/41 tasks ticked.** HEAD was **`ef91bd4`**. Gates then, run in the foreground by the
Architect **unsandboxed**: `dotnet build` 0/0; `dotnet test` **826/826** unfiltered in 2m49s;
`dotnet format --verify-no-changes` exit 0; `openspec validate --strict` valid; no `MUTANT` residue.

**§7 landed in four commits:** A `f80601d` (D18, design only), B `fb8904a` (7.1 + 7.2 + 7.5), C `03afeff`
(7.3 + 7.4), then one supervisor remediation `ef91bd4`. **Two supervisor rounds, three block-A review
rounds.**

### ⚠️ Harness fact that will cost the next agent an hour if it is not read

**`dotnet` fails inside the sandbox on this machine, and the failure looks exactly like a code failure.**
`dotnet build` returns *"Build FAILED. 0 Warning(s) 0 Error(s)"* after stalling five minutes;
`dotnet format` throws a `FormatCommandCommon.FormatAsync` stack trace. **Run every gate unsandboxed and
in the foreground** — ~1s, ~5s, ~2m50s. A "FAILED" with zero errors is this fault, not a regression. And
**never background `dotnet test` and wait on it**: seven agents in this change have now stalled that way,
and "not obtained" is a better answer than a hang.

### §7 carry-forwards — read before carving §8

1. **The section's defining lesson, and the sharpest form this change has produced: three artefacts can
   agree because they share one source rather than because anything was measured.** Both supervisor
   blockers were shipped-code defects that **no block review could structurally have caught** — the
   reviewer checks the code against `design.md`, `design.md` states the claim, and the code's own doc
   comment restates it. The shared instrument was **a sentence**. This is the "agreeing audits share an
   instrument" rule (§0, §3) in its purest form yet, and the only lens that caught it was the section
   review. **Before trusting agreement between a design document, an implementation, and its own
   comments, ask what measured the running system.**
2. **Prefer a structural guarantee to an enumerated one — this is what actually closed blocker 1.**
   The fix is `Environment.Clear()` plus an explicit allowlist, not "remember to unset `HOME`". Under an
   overlay, a test asserting one variable does not leak would have been an overclaim standing for a
   universal; under `Clear()`, it is a legitimate witness that the clear happened, and the universal
   follows from the mechanism rather than from enumeration. **The supervisor's own framing of why the
   evidence base is sound rests on this: the answer to repeated instrument failure is not more
   measurement, it is fewer claims that depend on having thought of everything.**
3. **⚠️ Load-bearing for §8: the image still has no HTTP client** (`curl`, `wget`, `nc` all absent), and
   §8.2 must broadcast from a `post-receive` hook to the running app. **Decide how the hook signals the
   app before §8 starts, or it reopens §1's Dockerfile.** Note what §7 has now changed about this: the
   CGI subprocess's environment is **cleared** and rebuilt from an allowlist, so a hook inherits only
   `PATH` plus D18 §2's set — any signalling mechanism that expected an ambient variable will not find
   one. And per D16 and §5's Product Owner decision, **no hook may acquire `RepositoryWriteLock`**, on
   pain of deadlocking against its own parent.
4. **A false positive announces itself; a false negative does not.** Five instrument failures in this
   change were all false *positives*, which is why they were caught. Blocker 2 — every clone silently
   running protocol v0 — was a false **negative**: 822 green tests, nothing anomalous to notice, and the
   §7 thread mentioned protocol version zero times in 1182 lines. **A clean suite is evidence about the
   cases you wrote, and silent about the axis you never named.**
5. **One wide claim survives, decorative and deliberately not fixed:** D18 §2's *"every git client since
   2.26 sends `Git-Protocol: version=2`"*. The supervisor measured it rather than assert it (a real
   `git 2.55.0` with global/system config neutralised does send it by default — and its *first* probe
   forced `-c protocol.version=2` and therefore proved nothing, which it caught and re-ran). **No
   decision rests on it** — the code forwards when present and adds nothing when absent, correct for any
   client — so narrowing the wording is a `## NEXT` item, not a third round. The transferable shape:
   **a wide claim is only dangerous when something structural depends on it.**
6. **Two items routed here rather than fixed, both confirmed by the supervisor as non-blocking:** the
   ~35 lines of kill-tree boilerplate duplicated from `GitProcessRunner` **without** its injectable
   `_killEntireProcessTree` seam (D18's argument for a *distinct type* is correct; only the boilerplate
   duplicates), and an unverified mechanism claim in `NoOpenRegistrationTests.cs` about method-mismatch
   endpoint selection — flagged rather than asserted, and it should be proven or reworded.
7. **A transcript-only imprecision, recorded so it is never cited as a count:** a `[worker]` container
   transcript says "6 vars total" where the reviewer independently measured **8**. `design.md` does not
   repeat the number, and the substantive conclusion (no `LANG`/`TZ` needed) is unaffected.
8. **`git add -N` a new file the moment a block creates it.** Seven of block B's eleven files were
   untracked for the block's whole life — including **both mutation targets**. `git diff` reports
   *nothing at all* for a file git has never tracked, so the mandated pre-commit diff would have come
   back clean over the two files a mutation run had been editing, exactly as it did for §7b's
   `GitEmailService.cs`. This converts the project's most-repeated blind spot into an ordinary diff, and
   costs nothing.

**Named limits on §7's own evidence, recorded rather than closed.** The supervisor ran nothing in the
Ubuntu 24.04/git 2.43.0 container itself — those facts are the worker's and the block reviewer's,
cross-reproduced but not by the section reviewer. And **no one has exercised a client vanishing mid-push
while git updates the working tree.**

### §7's carve — Product Owner decision, three blocks (as executed)

- **A — D18, the Smart HTTP surface. Design only; no feature code, ticks nothing.** What it must settle:
  the route shape and how ASP.NET maps it onto `git http-backend`; the CGI contract (`GIT_PROJECT_ROOT`,
  `PATH_INFO`, `QUERY_STRING`, `REQUEST_METHOD`, `CONTENT_TYPE`, `CONTENT_LENGTH`, the gzipped-request-body
  case, `REMOTE_USER`, and export policy — `GIT_HTTP_EXPORT_ALL` versus per-repo config); the streaming
  contract and **why a new subprocess host is needed rather than `GitProcessRunner`** (see the live inputs
  below); the auth scheme (HTTP Basic carrying the per-user git token, the `401` +
  `WWW-Authenticate` challenge, and the `AnonymousGate` opt-out seam); and the **lock policy per verb** —
  the spec's unbounded wait is stated for *a push*, so D18 owes an explicit answer for `git-upload-pack`
  (clone/fetch): whether it takes the write lock at all, and if not, what makes an unlocked read safe
  against a concurrent save. Also carries obligations 9 and 10 (below), and states plainly that §7 does
  **not** inherit D9's verification instrument as sound.
- **B — 7.1 + 7.2 + 7.5: the complete server mechanism, landing as one commit.** The streaming CGI host,
  the authentication, and the write lock wrapping the whole invocation.
- **C — 7.3 + 7.4: verification against a real `git` client** driven at a really-listening Kestrel —
  clone, fetch, push; `updateInstead` fast-forward updating the working tree; non-fast-forward rejected.
  Obligation 10 bites here.

**Why B is one block and not three.** 7.1, 7.2 and 7.5 are one mechanism, and splitting them ships an
intermediate whose safety property is knowingly false: a commit where `git-receive-pack` is reachable but
either unauthenticated (7.2's whole content) or unserialized against browser saves (7.5's). In-branch
only, never deployed — but this change's record is that a knowingly-wrong intermediate state costs more
than a long review, and the alternative buys nothing except three smaller diffs. The Product Owner chose
this over the four-block carve on exactly that trade.

### §7's live inputs — established at the carve by reading the code, not by re-reading this file

- **`GitProcessRunner` cannot host `http-backend`, and this is the block's sharpest edge.** It captures
  stdout via `ReadToEndAsync` **as a string** and never redirects **stdin** (`GitProcessRunner.cs:56-116`).
  A packfile through a text decoder is corrupt, and a push has no input channel at all. §7 needs a
  byte-stream subprocess host as a distinct thing — not a parameter added to the existing runner.
- **`GitTokenService.VerifyAsync` already exists and was built for exactly this** — username plus
  presented token, hashed and looked up among issued git tokens only, so a login password has no path in
  (`GitTokenService.cs:59-76`). 7.2 consumes it; it does not need writing.
- **`AnonymousGate`'s own remarks already name the git routes as its opt-out seam** — `[AllowAnonymous]`
  metadata plus a real `401`/`WWW-Authenticate` answer, "without this mechanism changing"
  (`AnonymousGate.cs:23-24`). The seam is designed; §7 uses it as designed or says why not.
- **The `safe.directory` standing rule is DISCHARGED** — `Dockerfile:54` already runs
  `git config --system --add safe.directory '*'` as root before the `USER` switch, which is what the rule
  demanded. Do not re-brief it as owed work; verify it still holds and move on.
- **7.5 needs no spec delta.** `specs/content-editing/spec.md:107-129` already carries *Single per-repo
  write lock* in full, including the SHALL that forbids bounding a push's wait and the scenario
  *"Push's wait for the lock has no ceiling"*. §7's spec obligation is to satisfy it, not to write it.
- **Obligations 9 and 10 are live for §7** and belong in B's and C's briefs respectively: 9 —
  `GitProcessException`'s message carries the raw argument list, which becomes a credential leak once
  token-bearing values pass through; `CapturingLoggerProvider` exists, so the brief says *test it*.
  10 — read the **checked-out** branch, never assume `DefaultBranch = "main"`; an adopted repository may
  be on `master`, and 7.4 is where it bites.

### Carry-forward 1 (D9's instrument) is ESCALATED to its own OpenSpec change — Product Owner decision

D9's startup reconciliation still verifies the tree with `git add -A` + `git status --porcelain`
(`ContentRepositoryService.cs:792-806`), which a single `git update-index --assume-unchanged` blinds — and
D9 is `RollbackFailed`'s named recovery mechanism, so the save path's worst outcome is documented as
recoverable by an instrument that shares the blind spot. §6 fixed its **own** guard (`git hash-object`)
and did not fix D9. Fixing it touches a guarantee `specs/content-store/spec.md`'s *Working-tree-clean
invariant* names, which is why it is not §7's to absorb quietly.

**Status: PROPOSED and QUEUED as a follow-on change — `openspec/changes/fix-reconciliation-index-blindness`,
all four artifacts complete, `openspec validate --strict` valid.** It is committed on **this** branch and
is therefore deliberately in this change's diff while being **out of §7's scope**; that is the Product
Owner's call, recorded here so the section review is not surprised by it. §7 resumes now.

**Why it could not be applied first, which is a constraint rather than a preference.** The code it
repairs (`ContentRepositoryService.cs`) and the requirement it amends (`content-store`) exist **only** on
`change/git-backed-content-core` — 47 commits ahead of `main`, unmerged.
`git cat-file -e main:src/ZeroWiki/Content/ContentRepositoryService.cs` fails, and `openspec/specs/` holds
only `authentication`, `invitations`, `request-lifecycle`, `user-accounts`. A change branched from the
default branch per CLAUDE.md §2.4 would find nothing to fix. It applies after this change archives.

**⚠️ Correction to carry-forward 1 above — it overstated the severity, and the correction came from
running it.** Four measurements on git 2.55.0, in a repository shaped like ZeroWiki's:

- **Untracked content is NOT blinded** — `?? docs/newpage.md` is still reported with `--assume-unchanged`
  set on a sibling. D9's load-bearing case, a folder of Markdown copied onto the volume, is unaffected.
  The blind spot is **tracked divergence only**, which no previous statement of this said.
- **`updateInstead` does NOT share the blind spot, so this is not data loss.** A fast-forward push against
  a blinded-dirty tree was **refused** — `error: Entry 'docs/page.md' not uptodate. Cannot merge.` /
  `! [remote rejected] main -> main` — and the uncommitted local edit survived byte-for-byte. The real
  cost is an availability and diagnosis failure: the app reports its invariant healthy while every push
  from every vault is rejected forever, naming a file the health check simultaneously calls clean.
- **A fourth instrument is blinded that nobody had named: `git diff --quiet HEAD` exits 0.** It is the
  natural "just compare the tree to `HEAD`" reflex and it reads as index-free. Anyone fixing this by
  reaching for it would ship a patch that passes review and changes nothing.
- **`git ls-files -v` is the instrument that sees the bits**, tagging `h` for assume-unchanged and `S`
  for skip-worktree against `H` for a normal entry — with the trap that lowercase-or-`S` is the correct
  rule and `!= "H"` is not, since `M`/`R`/`C`/`K`/`?` are non-`H` states that are not suppressions.

This is the same standing rule paying out again: **a claim about a mechanism is not evidence until it is
executed.** The carry-forward was written by reading §6's finding rather than reproducing it, and three
of these four facts contradict or sharpen what it said.

**Working tree CLEAN. State: 31/41 tasks ticked** (§6's 6.1–6.6 all done). Branch
`change/git-backed-content-core`, HEAD **`da6ed4f`**. Gates at §6's close, run in the foreground by the
Architect with an explicit 10-minute timeout: `dotnet build` **0/0**; `dotnet test` **801/801**
unfiltered in 2m12s; `dotnet format --verify-no-changes` exit 0; `openspec validate --strict` valid; no
`MUTANT` residue; no stray mounts or attached images.

**§6 landed in ten commits:** A `52ea5c6`, B `b3d0d44`, C1 `ae3d963`, C2 `618e8fa`, D1 `245bad2`,
D2 `9aaf8da`, D3 `9099d72`, D4 `751cf95`, then two supervisor remediations — `8d0bffb` (rounds one and
two combined) and `bfcb843` (round three).

**§6 took three supervisor rounds and two Product Owner escalations at §3c.4.** Its three original
blockers closed in the first remediation; the second and third rounds each fixed a defect the *previous
remediation had introduced*, in a descending series — wrong subsystem, then right subsystem with an
order-dependent lookup, then an existential check with no order to be wrong about. The Product Owner
sanctioned rounds two and three explicitly rather than either being carved on the Architect's authority.

### §6 carry-forwards — read before carving §7

1. **⚠️ Load-bearing for §7: D9's reconciliation still uses the instrument §6 rejected.**
   `ContentRepositoryService.cs:792-806` verifies the working tree with `git add -A` + `git status
   --porcelain`. §6 established by execution that a single `git update-index --assume-unchanged` blinds
   **both** of those *and* `add`, so D9 cannot see a divergence of exactly the kind it exists to repair
   — **and D9 is `RollbackFailed`'s named recovery mechanism**, so the save path's worst outcome is
   documented as recoverable by a mechanism that shares the blind spot. §6 fixed its own guard
   (`git hash-object`, which never consults the index); it did **not** fix D9. §7 must not inherit this
   as sound.
2. **The inherited-git-config class is unowned.** §6 pinned `core.autocrlf=false` on the content
   repository after an inherited host `core.autocrlf=input` made an ordinary browser save refuse
   startup. `core.quotePath` was the same defect fixed as a one-off earlier. Unverified remainder, in
   rough severity order: `commit.gpgsign` (measured as failing *loudly* — a fresh repo refuses to start,
   a running app fails saves honestly with a clean tree, so it is not silent), then `core.safecrlf`,
   `core.symlinks`, `core.fileMode`, `core.ignoreCase`, `core.precomposeUnicode`,
   `core.protectNTFS`/`protectHFS`. **The general rule: this app's correctness must not depend on
   configuration inherited from the operator's `~/.gitconfig`.**
3. **Two approach-level properties of the case-exact guard, both confirmed by execution at §6's close,
   neither blocking.** A Unicode NFC/NFD false-**refusal** on macOS-family volumes (fails in the safe
   direction; absent on Linux), and `PageSaveService.cs:848-855`'s `catch` returning `true` — a
   false-**accept** under a `--x` parent directory, backstopped by the `hash-object` check into the
   spec's own `RollbackFailed` scenario.
4. **Two test-harness limits recorded honestly rather than closed.** The case-sensitive volume harness
   leaks if `hdiutil create` succeeds but `attach` then throws (tracking fields unassigned, so
   `Dispose()` has nothing to clean) — found by reading, not reproduced. And **the Linux no-op path has
   never been executed on the deployment target**; it is fail-closed against its own premise (if ext4
   were not case-sensitive the assertions fail loudly rather than passing vacuously), which is why it
   cleared, but it remains an assumption rather than a result.

**`EditDraftStore` notes for a later change**, neither blocking: it is per-instance state that does not
survive a restart or a second process, and it has no background expiry sweep (entries expire lazily on
read and under cap pressure).

### §6 progress — read this before the numbered obligations below

| Block | Commit | Ticked | Delivered |
|---|---|---|---|
| A | `52ea5c6` | — | D17 + spec deltas (`content-store` ×3, `content-editing` ×2) |
| B | `b3d0d44` | — | One posture on git exit codes; fixed a **live data-loss defect** |
| C1 | `ae3d963` | — | Author identity total over accounts; codec distinct types |
| C2 | `618e8fa` | 6.2, 6.3, 6.5, 6.6 | The save service |
| D1 | `245bad2` | — | D17 address correction + 3 PO decisions; found a **live save/read asymmetry** |
| D2 | `9aaf8da` | — | Obligation 8: `GitProcessRunner` kills its subprocess on cancellation |
| D3 | `9099d72` | — | The save path enforces D12; `LoadForEditAsync` |
| D4 | `751cf95` | 6.1, 6.4 | The `?edit` surface, the save post, five outcomes |
| rem. 1+2 | `8d0bffb` | — | Supervisor blockers 1–3; the `hash-object` guard |
| rem. 3 | `bfcb843` | — | The case-exact guard asks for an exact match |

**Block D was re-carved into D1/D2/D3** — see the `[architect]` post under `## 6.`. The original single
block D, and the `/wiki/{*Route}/edit` address it named, are both **retracted**: that template is not
routable (a catch-all may only be the last segment, verified by execution), and the address is now
`/wiki/{*Route}` with an `edit` query flag.

**Obligations 7, 8, 14, 19, 25, 26, 27 and 31 are DISCHARGED — do not act on their numbered entries
below.** They are left unstruck only because striking eight long entries by hand is itself an error
surface; **this list is authoritative over them.** Discharged in §6: **7** (author identity total over
accounts, C1), **8** (`GitProcessRunner` kills its subprocess on cancellation, D2), **14** and **27**
(one posture on git exit codes, B), **19** (the codec's callerless resolvers, C1), **25** (rollback
invalidates the index, C2), **26** (closed at the section review — its trigger never fires, since the
save's file choice never routes through `ApplyIncrementalUpdateAsync`), **31** (the write-lock timeout
split, C2).

**This is the decay obligation 1 demonstrated** — it spent three sections asserting a Product Owner
decision was owed after the decision had shipped, because `## NEXT` is append-mostly and unticked
entries rot silently while the code moves. **Re-derive any forward obligation from the code before
acting on it.** §6 also produced the sharper form of the same rule: **name an obligation's owner by
what it owns, not by its label** — an instruction addressed to a block *number* rots the moment the
carve changes, which happened here when D3 split into D3/D4 and a design-doc instruction silently
addressed the wrong block.

**Nothing else is owed by §6 — it is closed.** The remaining numbered entries below are owed by §7–§10
or by a later change; check each against the code before acting.

**`SaveOutcome.RollbackFailed`'s spec scenario is DISCHARGED** — D1 wrote it, along with the correction
that there are **five** failure outcomes at the surface, not four. `Refused` was omitted from every
previous tally in this DEVLOG; it is reachable at *write* time (the symlink refusal, and a base-revision
probe git cannot answer blob-or-absent), not only at resolve time.

**Standing rule earned by D1, four times in one block:** *a design paragraph containing a literal a
machine consumes — a route template, an encoder input, a filename pair — does not ship until that
literal has been fed to that machine and the output pasted back.* D1's four blockers were a false
guarantee, a false witness, and a false vector list, each sitting beside the fix for the one before it.
All four were caught by a reviewer **executing** the claim; none by anyone reading the sentence,
including the Architect writing it, who re-derived each before accepting and changed no verdict. "Check
your examples" is not the rule — that is what was believed to be happening all four times.

**New in §6, for the section review and for §7/§8:** obligation **32** (a detached `HEAD` at a valid-
looking but nonexistent object id exits 0 and bypasses block B's refusal — diagnosis-quality, not
integrity; `git commit` itself refuses) is recorded at the end of the numbered list.

### §6's transferable lessons so far — five review rounds' worth

- **Every one of §6's findings has been a *right mechanism with a wrong stated reason*, not a wrong
  mechanism** — six instances, and **two of them were the Architect's own reasoning**, not a worker's.
  The most dangerous was a "property argument" that had the right form, cited the right precedent, and
  rested on a premise about the code that was never traced. **A property argument is not self-certifying;
  its premises are claims about the code and must be traced like any other.**
- **Re-run the mutants after the *fix*, not just after the defect.** C2's blocker fix was correct and the
  restructuring that delivered it silently dropped the property the fix existed to protect. Found only
  because the worker re-ran the mutation instead of reasoning that a refactor was behaviour-preserving.
- **A green total is not an answer to "did coverage change".** C1 hid a coverage drop behind an unrelated
  increase **twice**; forcing an end-to-end reconciliation against the baseline found that one deleted
  case was the only test exercising a live path-traversal branch.
- **Adjudicate "which seam", not "this thing: yes or no".** The `InternalsVisibleTo` reversal was settled
  by discovering the internals-based tests never exercised the race at all — the grant bought *less*
  coverage than the alternative. Asking the binary question would have missed that.
- **An interrupted mutation run left a live mutant in `src/` for the second time in this project.** Five
  agents have now stalled on a backgrounded `dotnet test`. What protected the tree was the out-of-repo
  `cp` baseline plus `trap` — `git checkout --` would have destroyed the block's whole uncommitted work.
  **Agents must run the suite in the foreground or report "not obtained".**

**§5 took two supervisor rounds and five commits, and its two blockers shared one shape: a principle
applied to the case that prompted it and not to its siblings.** The section ruled — on a Product Owner
decision — that a guarantee carried in prose alone was unsafe, and re-homed 5.3 to a numbered 7.5 on
exactly that basis. The same requirement's third SHALL clause was then left on the footing the section
had just condemned, **in the same edit**. Its companion: `WriteLockTimeout`'s doc described the consumer
the author was thinking about rather than the only one that exists.

**§5's other lesson is about measurement, and it cost hours.** Three full-suite runs of 1h23m–1h34m,
one with a failure, produced a written theory that block C's raw `open()` P/Invoke leaked file
descriptors. It did not. The machine had been suspended mid-run; wall clock counts sleep and CPU time
does not. **Check `%cpu` before interpreting a slow run, and bisect the environment before theorising
about the diff** — running the *previous commit* in a worktree exonerated the code in one step and
should have been the first move, not the fifth. The one test that failed was the one that measures real
elapsed time, and it was not lying.

**§4 took two supervisor rounds and four commits, and every blocker in both rounds was prose.** The
mechanism was right from the first pass; what was wrong was what the record *said* about it. Round one's
three blockers were all D15 having drifted from the code it binds — D15 landed in `c59ce13`, **before**
block B changed the mechanism, and `design.md` was in no block's diff afterwards, so no block review
could structurally have seen it. That is the section's transferable lesson: **a design decision written
mid-section is not covered by the block reviews that follow it**, because the artefact that changed and
the artefact that binds are in different diffs.

**The sharper one is where the wrong count came from.** Round one's B1 called the unreadable-directory
guard "a fourth" full-rebuild trigger. There are three. The supervisor had counted against D15's old
*flat* list rather than against `RefreshAsync`'s branches; the remediation correctly regrouped two
conditions into one and inherited the stale ordinal; the Architect propagated "fourth" into the worker's
brief straight from the supervisor's post; the worker wrote it. It was caught only when `@reviewer`
checked the count against the code's three `BuildAsync()` call sites instead of against the prose's own
internal consistency — and the supervisor then recorded, in its own approving post, that it had broken
this change's own standing rule (*when a guard's justification names a case, check the guard's branch*)
on its own arithmetic. **The section review is not exempt from the defect class it exists to catch**, and
a finding inherited from an audit is not evidence until it is re-derived from the code.

**Two claims about §4's win were overstated before they were right.** The Architect's first rewrite of
obligation 21 said a page view is now "one `git rev-parse`, no walk, no `git log`". True only on the
fast-path stamp match: an incremental refresh spawns a `git log -1` per affected page, and any of the
three fallback rebuilds pays the whole-tree walk plus the bulk `git log`, **synchronously inside the
triggering request**. The accurate form — carried in obligation 21 now — is that the walk and the
`git log` are *displaced from every read onto the first read after a write*. Caught by `@reviewer`
tracing `WikiPage.razor`'s awaited call chain; it also caught the same overstatement surviving in D15's
own cost sentence, which is carried as a close-out item below rather than spending a third round.

**A harness note worth carrying: three agents in §4 stalled waiting on backgrounded `dotnet test` runs**
— two reviewers and a worker, none of which posted a figure. Resolved by the Architect running the suite
in the foreground as the record and instructing agents to report "not obtained" rather than wait. Consistent
with this change's oldest standing rule: every instrument failure so far has been in the harness, not the
code.

**§2 took four supervisor rounds and seven commits.** Worth stating plainly, because the shape repeated:
every one of the four findings was a gap in **what a condition asks**, not in how faithfully it runs —
so all four were invisible to mutation, and all four were found by building a fixture for a state
nobody had enumerated. The fourth was closed by deleting the enumeration rather than extending it.

**§3 took two supervisor rounds and four commits, and its defining finding was different in kind.**
§2's defects were gaps in what a condition asked. §3's worst was a gap in **what the audit could
see**: D13 claimed escaping had "no bypass surface", and a worker, the reviewer and the Architect each
audited it competently by asking *"which values reach the browser un-encoded"* — a question that only
ever inspects **tags**, while the live stored XSS lived in link *destinations*. Three independent
audits corroborated each other while sharing one instrument and one blind spot. The tests asserted on
`<script>` alone. It was found only when a fourth reader used a different instrument, and closed only
after the reviewer proved the bypass fired on a real trusted click in Chrome **before** checking the
fix stopped it.

| Section | Block | Commit | Reviewer | Supervisor |
|---|---|---|---|---|
| §0 design | D8–D11 + spec delta | `bd2eeea` | — | — |
| §1 | 1.1–1.3 | `3f6d837` | Approve w/ nits | Request changes → **Approve** |
| §1 | remediation (blocker + notes 2, 4) | `7102eed` | Approve | ↑ |
| §1 | 1.4 DataProtection | `0247443` | Approve | ↑ |
| §1 | close-out (docs) | `bb3cb2c` | — | — |
| §11 | 11.1–11.3 | `70a31aa` | Request changes → Approve w/ nits | Request changes (S1) → **Approve** |
| §11 | remediation (S1 + comment corrections) | `2af401c` | Request changes → **Approve** | ↑ |
| §2 | 2.1–2.2 | `ef2b75b` | Request changes → Approve → **Approve** (re-cert) | Request changes → **Approve** |
| §2 | 2.3–2.5 | `6b82c33` | Request changes ×2 → **Approve** w/ nit | ↑ |
| §2 | remediation (2 supervisor blockers) | `2fa3ca5` | **Approve** w/ nit | ↑ |
| §2 | close-out (docs) | `85d2b7f` | — | — |
| §2 | D9 reorder — no write precedes any refusal | `f50f1ca` | **Approve** | Request changes (round 3) |
| §2 | pre-init nested-repo scan | `2c70e05` | Request changes → **Approve** w/ nit | ↑ |
| §2 | gate the scan on the write's predicate | `1253ff5` | **Approve** | → **Approve** (round 4) |
| §2 | close-out (docs) | `60957e6` | — | — |
| §3 design | D12–D14 + spec delta | `50da7b0` | — | Request changes (S1–S3) → **Approve** |
| §3 | 3.1–3.2 enumeration + frontmatter | `8332c79` | Request changes ×2 → **Approve** w/ nit | ↑ |
| §3 | 3.3–3.4 rendering + git authorship | `e9bfea0` | Request changes ×2 → **Approve** w/ nit | ↑ |
| §3 | remediation (3 supervisor blockers) | `7ca5b76` | **Approve** w/ 2 nits | → **Approve** (round 2) |
| §4 design | D15 + spec delta | `c59ce13` | — | Request changes (B1–B3) → **Approve** |
| §4 | 4.1–4.2 index + full rebuild | `fe3496f` | Request changes → **Approve** | ↑ |
| §4 | 4.3 freshness + index-backed read path | `ad7934f` | Request changes ×2 → **Approve** | ↑ |
| §4 | remediation (3 supervisor blockers, prose only) | `8dc1646` | Request changes → **Approve** | → **Approve** (round 2) |
| §5 design | D16 + spec delta | `172c622` | Approve w/ 3 nits → Request changes → **Approve** | Request changes (S1, S2) → **Approve** |
| §5 | accept/configure split (obligation 17) | `87ff1cc` | Request changes → **Approve** | ↑ |
| §5 | 5.1–5.2 lock primitive + app write path | `e6ef77a` | Request changes → **Approve** | ↑ |
| §5 | 5.3 struck → 7.5; deadlock hazard disarmed | `e31d91a` | Request changes ×2 → **Approve** | ↑ |
| §5 | remediation (S1 + S2 + startup-refusal test) | `fe9dab5` | — | → **Approve** (round 2) |
| §5 | close-out (docs, mutant 3 re-valued) | *this commit* | — | — |

**Execution order from here: §7 → §8 → §9 → §10.** §11, §2, §3, §4, §5 and §6 are done; the remaining
sections run in `tasks.md` order.

**No decision is owed before §7 opens.** `design.md`'s Open Questions remain resolved, and the four §6
carry-forwards above are engineering work or recorded assumptions rather than Product Owner calls —
with one caveat: **carry-forward 1 (D9's instrument) may become one**, because fixing it touches D9's
recovery guarantee, which the spec names. Raise it when §7 is carved, not before.

### Forward obligations — each is owed by a specific section

1. ~~**Settle before §5 — §2's binding prose overclaims, and one fix changes behaviour.**~~ —
   **discharged in §2, and this entry was stale for three sections.** Verified against the code at §4's
   close-out, not against this record: `ContentRepositoryService.EnsureRepositoryAsync` now calls
   `ApplyRepositoryConfigurationAsync` and `InstallHooksAsync` **last** (`:168-169`), after
   `EnsureInitialCommitAsync`, `ReconcileWorkingTreeAsync` and `AssertWorkingTreeIsCleanAsync` — so
   every refusal the method can raise genuinely precedes every write, on both the adopt and the
   initialise paths. That was `f50f1ca` ("D9 reorder — no write precedes any refusal"), with `2c70e05`
   and `1253ff5` closing the initialise path behind it; all three are in this file's own block table.
   The second half was fixed too: `design.md` carries a **"What the posture is not, regardless of
   ordering"** paragraph stating plainly that ZeroWiki *does* commit into adopted history via D9
   reconciliation, and giving the accurate posture instead. **Nothing is owed to the Product Owner
   here.** Recorded rather than deleted because the lesson is the point: this entry survived §3 and §4
   asserting a Product Owner decision was owed, and the decision had already been taken and shipped —
   `## NEXT` is append-mostly and its *unticked* entries decay silently while the code moves. **Re-derive
   a forward obligation from the code before spending a decision on it**; a stale obligation costs more
   than a missing one, because it is acted upon.
2. ~~**§5 — the lockfile must not live in the working tree.** A lockfile under `/data/wiki/docs` is an
   untracked file, which makes the tree dirty, which D9 dutifully commits, and `updateInstead` then
   bounces every push against a tree it believes unclean. Put it under `.git/` or beside the
   repository, and **extend `ContentPaths`** rather than growing a parallel notion of where things
   live.~~ — **discharged.** `ContentPaths.cs:25`, `LockFilePath = <DataRoot>/wiki.lock`, a `DataRoot`
   sibling of `RepositoryRoot`, extending `ContentPaths` exactly as demanded. (§5 remediation, round one.)
3. **§8 — the image has no HTTP client.** `curl`, `wget` and `nc` are all absent from the runtime
   image. Decide how `post-receive` signals the app **before** §8 starts, or it reopens §1's Dockerfile.
   **Correction (§5 remediation, round one):** this obligation used to also say *"`flock` **is** present,
   so §5.3 is safe"*. §5.3 does not exist — it was struck in §5 and its lock acquisition moved to §7.5 —
   and per §5's Product Owner decision the generated hooks must **never** attempt to acquire
   `RepositoryWriteLock` themselves, on pain of deadlocking against their own parent process (see
   `GitHookInstaller`'s remarks and design.md D16's "why no hook may attempt this acquisition itself").
   An §8 implementer reading the old wording would have taken it as license for a hook-side `flock`; the
   surviving clause here — no HTTP client in the image — is the only one that was ever true and it is
   still §8's to solve.
4. ~~**§7 — `git-receive-pack` returns `403 Forbidden`**~~ — **discharged in §2** (`ef2b75b`).
   `http.receivepack=true` is now set as repo configuration on **every** start, not only at init, so a
   repository made by an earlier image or restored from a backup receives it too. Kept here because the
   symptom still reads as an authentication bug to whoever meets it first, and §7 should recognise it.
5. ~~**§2 — resolve `ContentPaths` from DI**~~ — **discharged in §2** (`ef2b75b`).
   `ContentRepositoryService` and `GitHookInstaller` both take it by injection; `ResolveContentPaths`
   remains used **only** by the pre-`Build()` DataProtection wiring, which is what it exists for.
6. **A latent trap in the test harness.** `ResolveContentPaths` reading configuration before `Build()`
   works **only** because `ZeroWikiAppFactory` uses `UseSetting`. A future harness using
   `ConfigureAppConfiguration` would hand the DI singleton the override while the key ring silently
   took the `/data` default. `LoginPageTests.cs:214`'s on-disk assertion is the real guard — **do not
   soften it**.
7. **§6 — the author line must be well-formed for accounts that predate the username rules.** D10's
   `Consequence binding §6`, now also a **scenario** in `specs/content-editing/spec.md` so §6's
   section review is gated on it rather than trusting prose. Non-retroactivity is deliberate and
   correct, which is exactly why §11 could not discharge this: `LoginServiceTests.cs:271-289` pins
   `.old.name.` still authenticating, and §6 constructs the address. Also tell §6's brief that the
   username is **immutable by consequence** — a permanent artifact plus no rename path.
8. **§6 — `GitProcessRunner` does not kill the git subprocess on cancellation. No longer inert — §4 made
   it live, and the justification recorded here was true only until `ad7934f`.** Parked in §2's first
   block on the grounds that startup passes `CancellationToken.None`, so there was nothing to cancel.
   That is now false: §4's freshness check puts git subprocesses on the **request path**, so a cancelled
   page view can orphan one today. **Which** subprocesses depends on the branch taken, and the first
   version of this entry named only the cheapest two — a `git rev-parse` always, plus on a stale stamp a
   `git diff` **and a `git log -1` per affected page**, or on a fallback rebuild a whole-tree walk plus a
   bulk `git log --name-status`. A cancelled request during a rebuild can therefore orphan a long-running
   `git log`, not merely a `rev-parse`. The spawns are still short-lived in the common case, which is why
   §4 was briefed to thread cancellation honestly and
   **not** widen into fixing the runner — but "inert" was the reason this was safe to park, and that
   reason has expired while the fix has not moved. §6 still owns it; it is no longer waiting on §6 to
   become reachable. *(Every shipped code comment states this correctly — the stale claim was here, in
   the record, which is where §2's five wrong justifications also lived.)*
9. **§7 — `GitProcessException`'s message carries the raw argument list.** Also parked from §2's first
   block, also inert there: bootstrap passes no secrets through the runner. §7 passes token-bearing
   URLs through the same runner, at which point the exception message — and anything that logs it —
   becomes a credential leak. `CapturingLoggerProvider` already exists to sweep logs for exactly this,
   so §7's brief should say *test it*, not merely *avoid it*.
10. **§7 must read the checked-out branch, not assume `DefaultBranch`.** `DefaultBranch = "main"` is
    justified in `ContentRepositoryService` as "the app has to know the checked-out branch name" —
    false for an **adopted** repository, which may be on `master` or anything else, and
    `CreateForeignRepositoryAsync`'s `init -b main` hides it. Latent rather than live: a foreign repo on
    `master` boots fine in §2 because `updateInstead` targets whatever is checked out. §7.4 is where it
    bites.
11. **Untested foreignness axes**, for whoever extends `CreateForeignRepositoryAsync`. §2 covers
    *structural* foreignness only. Untested: pre-existing `pre-receive`/`post-receive` hooks (silently
    overwritten), a pre-existing `receive.denyCurrentBranch=refuse` (silently overwritten), a non-`main`
    branch, and an adopted repository with a **dirty** tree — the last is the one that exercises
    obligation 1's contradiction, so pair them.
12. ~~**Two `<remarks>` corrections owed** — neither affects behaviour, both are the recurring
    wrong-justification defect: `FindStagedGitlinksAsync` says a tracked path replaced by a nested
    repository yields `M`; git emits `T` (the condition tests modes, not status letters, so behaviour is
    unaffected). And `GitHookInstaller` should record that `git rev-parse --git-path hooks` honours
    `core.hooksPath` — verified by execution, and the fact that makes §5.3's hooks land where git will
    actually run them.~~ — **discharged in `f50f1ca`.** Obligation 13 already recorded this discharge;
    this entry had been left live with a stale §5.3 reference while 13 said it was done — struck now for
    consistency, and the reference corrected for anyone who reads this one first: `core.hooksPath` is the
    fact that makes the *installed* hooks (there is no §5.3) land where git will actually run them, not
    "§5.3's hooks". Both `<remarks>` read correctly today in `ContentRepositoryService.cs` and
    `GitHookInstaller.cs`. (§5 remediation, round one.)
13. ~~Carried: `GitAuthor.cs` "later"; `tasks.md:70`'s superseded 11.1 pattern~~ — **both discharged in
    `f50f1ca`**, along with the `T`-vs-`M` and `core.hooksPath` remarks from obligation 12.
14. **`git add -A` has the same unreadable-directory blind spot the scan just closed in C#, and no C#
    scan can reach it. It is LIVE TODAY, not merely owed** — sharpened by `@supervisor`: the scan runs
    only where ZeroWiki creates the initial commit, so on an **adopted** repository it never runs at
    all, and an unreadable directory there means reconciliation silently skips content while
    `AssertWorkingTreeIsCleanAsync` still passes. §6 must not inherit this thinking it starts clean.
    `git add -A` warns on stderr and **exits 0** when it cannot read a directory, so content behind it
    is silently not staged
    — which means reconciliation reports success, the tree reports clean, and D9's "never discard"
    quietly does not hold for that subtree. Distinct from the scan blocker: the scan guards the
    *initialise* path only, while this touches **every** path, including a push-updated tree in §8. The
    fix is not a scan — it is deciding whether `ReconcileWorkingTreeAsync` should inspect `add -A`'s
    **stderr** and refuse rather than trusting its exit code. Likely a Product Owner call, since
    refusing on a warning is a policy choice; raise it in §6's brief at the latest, and note §10.1 owes
    a reconciliation test either way.
15. **Unbounded recursion in `AssertNoNestedGitRepository`** on a pathologically deep, non-symlinked
    tree. Assessed by `@reviewer` as closer to noise than a live risk for this product's content, and
    left unguarded deliberately rather than by oversight — recorded so that judgement is visible rather
    than implicit. Symlink loops are already handled, and are the realistic case.
16. ~~**§5.1 — `repositoryHasNoCommitsYet` is now a snapshot.** Unifying the scan's gate with the write's
    predicate (`1253ff5`) means `EnsureInitialCommitAsync` no longer verifies `HEAD` immediately before
    committing; it trusts a value computed earlier in the call. Correct **today** only because startup
    is single-threaded and unlocked. §5.1 is where that stops being true, and this is the assumption it
    invalidates.~~ — **discharged in mechanism, not in evidence.** `ContentRepositoryService.cs:107`
    acquires `RepositoryWriteLock` before `:111` classification and `:136` derivation, so the value is
    computed after the lock is held, not trusted stale from before it. But block C's mutant reverting
    exactly this ordering **survives 716/716** under the full unfiltered suite — struck here with that
    qualification, not plainly, because "discharged" must not read as "regression-protected"; it is
    protected by review only. ~~**§5's remediation block (round one) added a new test
    (`StartupWriteLockHeldByAnotherProcess_RefusesToStartNamingTheLockFile`) — it does not change this.**
    That test pins `AcquireStartupWriteLockAsync`'s timeout-refusal path (lock held, wait, fatal refusal);
    it never exercises the lock-before-classify ordering this obligation is about, so it is a different
    mutant and this one is still unprotected by anything but review.~~
    — **that paragraph was wrong, and is corrected by execution: obligation 16 is now DISCHARGED IN
    EVIDENCE TOO.** `@supervisor` disputed it at round two from the code — `Directory.CreateDirectory(
    repositoryRoot)` (`:109`) sits **between** the acquire (`:107`) and classification (`:111`), so any
    mutant moving the acquire later runs that directory creation unlocked, and the new test's *third*
    assertion — `Assert.False(Directory.Exists(RepositoryRoot))`
    (`ContentRepositoryServiceTests.cs:130`) — catches it. It flagged this as a hypothesis it had reasoned
    but not run. **The Architect ran it: mutant 3 re-applied to `ContentRepositoryService.cs`, full
    unfiltered suite, `Failed: 1, Passed: 716` in 1m45s; filtered confirmation names the failing test and
    the failing assertion as `Assert.False() Failure`.** Reverted from an out-of-repo baseline copy;
    checksum matched byte-for-byte (`2e828b97…781515`), `git diff -- src` empty, `--untracked-files=all`
    empty, no `MUTANT` residue.
    **The transferable lesson, and it is new:** the first two assertions of that test are mutant-blind
    and the third is not, so a reader checking "does this test cover the ordering?" against the test's
    *stated purpose* concludes no, while the code says yes. **A test written to close one gap can close a
    second silently, and nobody re-measures the old mutants after adding coverage.** Both the worker and
    I asserted the survival unchanged; only re-running it settled it. Round one's own rule — *a finding
    inherited from an audit is not evidence until it is re-derived from the code* — applied to a
    **survival** claim, which is the harder direction to remember because a survivor feels like the
    absence of a result rather than a result.
17. ~~**§5.1 — the `AcceptRepositoryAsync`/`ConfigureRepositoryAsync` split has gone from optional to
    overdue, and §5.1 is its forcing function.** The Product Owner's call to keep `1253ff5` targeted was
    right — bundling a refactor with a correctness fix would have made the fix unreviewable — but
    `git init` now sits in its own `if` outside the block that computed its boolean, so the classify
    block no longer owns its own action. A cross-process `flock` wants to wrap exactly the
    accept-and-write phase and **not** the configure phase, which is the shape the split already has.
    Nearly free now; more expensive once a lock is threaded through the current shape.~~ — **discharged.**
    `87ff1cc` made exactly this split: `AcceptRepositoryAsync` holds the lock for the whole accept-and-write
    phase, `ConfigureRepositoryAsync` runs after it unlocked (`ContentRepositoryService.cs:74-75`).
18. **A rationale that no longer covers its own trigger** (prose, non-blocking). The
    unreadable-directory refusal is justified partly by "the scan runs once per volume, while the
    operator is most likely still watching". Since `1253ff5` the scan also runs on an **unattended
    crash-recovery restart** — `.git` present, `HEAD` unborn — where nobody is watching. The decision
    stays right on its other grounds; the stated reason should stand on its own terms.

19. **§6 — the codec has six public members and two of them have no caller.** `PageRouteCodec` grew
    across three review rounds rather than being designed: `Encode`, `TryDecode`, `TryDecodeRouteValue`,
    `IsCanonicalRouteValue`, and **two** resolvers — `TryResolveWorkingTreePath` and
    `TryResolveWorkingTreePathFromRouteValue` — neither of which any production code calls. They exist
    for §6's save path. **§6 must delete or wire them, not add a third.** The distinct-types redesign
    (a canonical-route type versus a bound-value type, making a contract mix-up a compile error) is the
    durable fix and belongs here too: the split is currently enforced only by naming, and the reviewer
    established that a wrong pairing *cannot* be detected at runtime, because an encoded and a decoded
    string containing no `%` are the same string.
20. **§4/§6 — `img-src 'self'` and the link allow-list disagree about external images.** The allow-list
    permits an `https` image destination; the CSP blocks its load. Moot today because nothing serves
    static content, live the moment §4 or §6 does. The spec scenario *Ordinary destinations still work*
    is true of links and **not** of external images — reconcile the two rather than discovering it as a
    broken image.
21. **§6 — the per-request cost. Two-thirds discharged by §4 (`ad7934f`); the remaining third is the one
    with no owner.** As written, this said every page view walks the whole working tree *and* spawns a
    `git log`, with **no page-size cap anywhere**. §4 closed the first two **for the steady state, not for
    every request** — an earlier version of this entry said flatly "a page view is now one `git rev-parse`,
    no walk, no `git log`", and `@reviewer` traced the call chain and showed that is true only of the
    fast path. Accurately: when the stamp matches `HEAD` — every read between writes, which is the
    overwhelming majority — a page view is one `git rev-parse` and nothing else. The **first** request
    after any `HEAD` advance pays the refresh **synchronously, inside that request**: an incremental
    refresh spawns a `git diff` plus a `git log -1` per affected page, and a fallback rebuild (no previous
    stamp, an unresolvable stamp, **or** a previous snapshot holding an unreadable directory) pays the
    whole-tree walk plus a bulk `git log --name-status`. The walk and the `git log` are therefore
    *displaced from every read onto the first read after a write*, which is the real win and a large one —
    but "no walk, no `git log`" as an unqualified claim is wrong, and §5 and §6 must not inherit it.
    **What remains is the size cap, and it is unchanged
    and still unowned**: a pushed multi-hundred-megabyte `.md` is still read whole and parsed on every
    request that renders it, because D15 deliberately keeps the *body* off the index. §4's indexer reads
    a bounded prefix, which is a different fix for a different path and must not be mistaken for this
    one. **One new cost §4 introduced, recorded so §5 sees it:** while any directory under the working
    tree is unreadable, every `HEAD` advance forces a **full** rebuild (whole tree + whole history)
    rather than an incremental one — the guard that closed §4's supervisor blocker. Correct, deliberate,
    and unlike the other rebuild triggers it recurs rather than firing once.
22. **§6 — S3's race has no regression test, deliberately.** Enumeration walks a tree `git push` mutates;
    the fix names three specific exception types established empirically, but neither worker nor reviewer
    could build a deterministic non-flaky reproduction, and both independently found dangling symlinks do
    not reproduce it. Accepted by the supervisor on the reasoning that a flaky test gating every future
    block is worse than an honest gap, and that an untested `catch` risks catching too much or too little
    — both closed by reading, since it names types rather than `Exception`. **Revisit once §6's D3 lock
    makes the race closable and therefore testable.**
23. **The editor change — D13 records a `style-src` forward cost, already investigated.** CodeMirror 6's
    `style-mod` writes `styleTag.textContent` when mounted into a document, which `style-src 'self'`
    blocks, but takes a constructable-stylesheet path — not an inline style, so not governed by
    `style-src` — when mounted into a **shadow root**. It is a mounting decision, not a reason to weaken
    the CSP or choose a different editor. The "constructable stylesheets escape `style-src`" half is read
    from the spec, **not** browser-tested; test it when the editor lands.
24. **Latent, deployment-level — a reverse proxy that normalizes percent-encoding breaks the routing
    seam.** ASP.NET Core percent-decodes a catch-all route value exactly once, and D12's whole scheme is
    built on that being exactly once. A proxy that decodes before forwarding makes it twice. Nothing in
    the repo configures a proxy today, so this is a constraint on a future deployment rather than a
    defect — but it is the same seam D12 chose `__` over `%5F` to protect, and it should be stated
    wherever deployment is documented.

25. **§6 — the index goes stale without `HEAD` moving, and §6 is the section that does it.** D15's
    freshness rests on an invariant it did not state until §4's remediation block: *every content-changing
    event advances `HEAD`*. `specs/content-editing/spec.md:54` requires the path that breaks it — write →
    a refresh reads the new frontmatter → the commit fails → `git checkout --` restores the file →
    **`HEAD` never moved**. The index keeps the aborted save's metadata, and the next `HEAD` advance takes
    the *incremental* branch, which never re-reads that path, so the wrong title and tags persist
    indefinitely. Inert today because no save path exists; live the moment §6 lands, and §6 will be
    briefed from D15 — which is exactly why it is written down there and not only here.
26. **§6 — `ApplyIncrementalUpdateAsync` inherits a mutation obligation the moment a save resolves through
    it.** §4's mutation scoping (none — a derived, rebuildable index is not an auth, concurrency or
    data-integrity path) was deliberate and the section review upheld it. That scoping stops holding when
    D12's *Consequence binding §6* takes effect: once a save inverts a route to choose the file it
    **writes**, the incremental path's claimant reconstruction becomes a data-integrity path, and a
    surviving mutant there is a wrong-file write. The guard §4 added (full rebuild whenever the previous
    snapshot held an unreadable directory) is the specific condition worth mutating.
27. **§5 — `ContentRepositoryService.RepositoryHeadIsUnbornAsync` reads exit 128 as "unborn".** The exact
    defect block A was blocked on and fixed in `PageIndexBuilder`: `git rev-parse --verify -q HEAD` exits
    1 on an unborn `HEAD` but 128 on "not a git repository". The same question is now answered two ways in
    two classes, and the §2 one is the wrong way. **Unreachable today, but the previously-recorded reason
    was wrong (corrected in §5's remediation block, round one).** This entry used to say
    `AssertGitResolvesRepositoryRootAsync` "runs first", so 128 could never arrive — false:
    `AssertGitResolvesRepositoryRootAsync` runs at `:199`, **after** `RepositoryHeadIsUnbornAsync` is
    called at `:136`. What actually makes 128 unreachable is earlier: `RepositoryHeadIsUnbornAsync` is
    only ever called from the `hasOwnGitEntry` branch (`:114-136`), and that same branch's own bare-probe
    — `_git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "--is-bare-repository"], …)` at `:119` — throws
    on "not a git repository" *before* `RepositoryHeadIsUnbornAsync`'s own `rev-parse --verify -q HEAD`
    (which uses the non-throwing `RunAsync`) ever runs. §5 restructured this method twice (obligations 16,
    17) without touching this line, so the wrong-way answer is still live in unreachable code.
    **Owner: §6, attached to obligation 14's resolution — and the reason matters, because the first
    reason given was wrong.** The remediation block proposed §6 on the grounds that "a worker will already
    be in this file for obligations 7 and 14"; `@supervisor` rejected that at round two as a *scheduling*
    argument and half false — obligation 7 is `GitAuthor`/`LoginService`, **not this file** — and this
    change has already ruled convenience the wrong reason to bundle (`1253ff5`, where the Product Owner
    kept a correctness fix unbundled from a refactor). The right reason is that **14 and 27 are one
    question**: 14 asks whether `git add -A`'s exit code may be trusted over its stderr, and 27 asks
    whether `rev-parse`'s exit code may be trusted to mean what the caller assumes. One defect class, one
    file, and they should yield **one stated posture on what a git exit code is allowed to mean** rather
    than two ad-hoc fixes. **Attach 27 to 14's resolution rather than listing it separately**, or it slips
    the way obligation 1 did — carried unticked across three sections while the code moved underneath it.
    If §6's brief has no natural point to attach it,
    the alternative is a close-out nit at archive time, since the defect is unreachable and low-risk.
    ❓ **@architect** — confirm §6, name a different owner, or park it as a close-out nit; not decided by
    this entry.
28. **§3's `NotFoundPage` trade was never recorded here, though the code says it was.**
    `WikiPage.razor:114` states that whether §4 or a later change should rework `Routes.razor`'s
    `NotFoundPage` (or move to the `NavigationManager.NotFound()` API) "is recorded in `## NEXT`" — and it
    was not. Recording it now makes the comment true. **The trade:** today a page that does not exist
    cannot use a bare 404, because `NotFoundPage` intercepts it and delivers the wrong body, so `WikiPage`
    returns 200 with distinguishing content for that one case. Reworking `NotFoundPage` would make 404
    usable and is application configuration no §3 or §4 block had reason to touch. Not owed by any
    section; it becomes live whenever someone wants honest status codes on content routes.
29. **Accepted and now stated in D15: a TOCTOU window between probing `HEAD` and reading the affected
    paths.** The refresh probes `HEAD`, then reads files from the working tree, and the two can disagree
    if a commit lands in between — so a snapshot can transiently understate its own freshness. It
    self-corrects on the very next request and leaves no permanent wrong state, and closing it properly
    needs the D3 lock, so it is §5's to revisit rather than a gap. Raised by `@reviewer` as a property of
    D15's whole design rather than of block B's diff, and confirmed as such by the section review.

30. **§5's opening commit carries a D15 docs correction — the same overstatement, in the one place it
    binds.** D15's new cost sentence says the third rebuild trigger "fires on every `HEAD` advance …
    costs **every page view** a whole-tree-plus-whole-history rebuild". A `HEAD` advance is not a page
    view: a read on a matching stamp still takes the fast path. This is exactly the overstatement
    `@reviewer` caught in obligation 21's first rewrite and fixed *there*, so the same commit is accurate
    in `## NEXT` and inaccurate in `design.md` — the artefact that actually briefs §5, §6 and §8. The
    supervisor's post carries exact replacement wording, plus two smaller precision points in D15's B2
    paragraph (notably: "no writer in this change can fail a commit yet" is wrong — §2's reconciliation
    commit can; what makes the staleness inert today is that no such writer runs *while the index is
    live*). Deliberately **not** a third supervisor round — CLAUDE.md caps at two, and a docs correction
    does not justify spending one.

31. **§6.6 — whether `ContentStorage:WriteLockTimeout` is shared between startup and a save, or splits.**
    Today the value has exactly one consumer: `ContentRepositoryService`'s startup accept-phase
    acquisition, whose expiry is a fatal refusal to start. §6.6 adds the second consumer — a browser
    save's acquisition, whose expiry must instead be a per-request "repository busy" result (D16,
    `spec.md:52-55`). §5's remediation block (round one) fixed `ContentStorageOptions.WriteLockTimeout`'s
    doc and D16's "Ceiling" paragraph to describe today's reality and name the operator consequence of
    the gap (lowering it as a save-latency knob also shortens how long a rolling deploy's overlap is
    tolerated before an incoming instance refuses to boot; raising it does the reverse), but deliberately
    did **not** decide whether the two acquisitions should keep sharing one configured value or get
    separately configurable ones — that is a configuration-surface decision the supervisor flagged as the
    Product Owner's to make (S2, §5 review), not something to settle by editing a doc comment. **§6.6 must
    settle this explicitly, not inherit the shared default silently**: either state in code and doc why
    one value is right for both failure modes, or introduce a second option and default it sensibly.

    **The first argument on the merits — from the laptop-suspension episode, which nobody connected to
    this until `@supervisor` did at round two.** `AcquireAsync` measures with `Stopwatch`, which counts
    host suspension, and so do `Environment.TickCount64` and `DateTime.UtcNow` — **there is no clock that
    does not**, so this is recordable rather than fixable. The consequence is asymmetric in exactly the way
    that decides this obligation: after a host resumes, a **save** whose bound elapsed during suspension
    fails "repository busy" and the user retries — benign. A **startup** whose bound elapsed during
    suspension **refuses to boot** — an outage, on a machine that did nothing wrong but sleep. Two failure
    modes with materially different costs, driven by one number, and the sleep case makes the gap concrete
    rather than theoretical. That is evidence for splitting; it is not a decision, and §6.6 still owns it.
    Recorded here so it reaches §6.6 as **input**, rather than surviving as an anecdote about a closed lid.

32. **A detached `HEAD` at a syntactically-valid but nonexistent object id exits 0, so it never reaches
    §6 block B's refusal at all.** Found by `@reviewer` while testing block B's *own* error-message
    fallback, which is the notable part: the gap is a sibling of the dangling-symref defect block B
    fixed, and it sits on the one branch the fix does not cover. `git rev-parse --verify -q HEAD` exits
    **0** for such a `HEAD` — no exit 1, so `RepositoryHeadIsUnbornAsync` returns `false` and startup
    proceeds. **Traced end to end and it is not data loss:** `git commit` itself refuses with
    `fatal: could not parse HEAD`, so nothing is orphaned. What it produces is a raw, unhandled git error
    instead of the clean D17-style refusal every neighbouring fault now gets — a diagnosis-quality gap,
    not an integrity one, which is why it was not made a blocker on a block that had already found and
    fixed a live one.
    **Also recorded, because it is the same defect class one level down:** block B's
    `"a detached, unresolvable commit"` fallback string — the `symbolic-ref`-failed branch — appears to
    be **dead for every input the reviewer could construct**. It is deliberately *left in place* rather
    than deleted: "no triggering input was found" is not "no triggering input exists", and removing a
    cheap defensive branch on the strength of a failed search is the wrong trade on a startup path. But
    an unreachable string that describes a state is exactly the shape this change keeps producing, so it
    should be either proven reachable or removed on evidence, not left ambiguous forever. Owner: a D17
    addendum, or close-out — **not** §6, which has fixed what was live here.

### Close-out items before archive

- **F2 (nit, pre-existing from `bd2eeea`)** — `specs/user-accounts/spec.md`, scenario *"Well-formed
  username is accepted"*: its WHEN is satisfied by `a..b`, which the new scenario refuses, and it
  already omitted the 64-character cap before this change touched it. One clause. It has always been
  illustrative rather than a description of the accepted set.
- **F1 (low)** — `CredentialPolicy.cs:85-91`'s slack claim ("an over-long name is reported as a length
  problem rather than also being told its charset is wrong") holds at the **service** at every length,
  but at the **form** only to 127; a 128-character alphanumeric name gets two messages for one fault.
  Name the surface it holds on, or its bound. **Unverified by the Architect** — reproduce before
  fixing.
- Page-test matrices are asymmetric: `BootstrapPageTests.cs:83-94` covers `"café"` and `"admin\tx"`;
  `RedeemInvitationPageTests.cs:262-271` omits both.

### Standing rules earned in §0–§11

- **Agreement between audits is worthless when they share an instrument — and the instrument is the
  *question*, not the tool.** D13's link-destination XSS survived a worker, the reviewer and the
  Architect because all three asked *"which values reach the browser un-encoded"*, which only ever
  inspects tags. Three independent competent audits, one blind spot, mutual corroboration. **Before
  trusting a clean audit, name what its question cannot see.** Second instance of this exact shape after
  §0's `href=""` anchor regex, and the more expensive one (§3).
- **Verify the threat exists before verifying it is closed.** The reviewer settled S1 by building the
  `java\tscript:` link in a real Chrome tab, confirming the browser's own parser strips the tab and
  recognises the scheme, and firing it with a trusted click — *then* checking the fix. A test that only
  ever shows the fix passing cannot distinguish a real defence from a fixture that never attacked (§3).
- **Verify a claim about a *mapping* by enumerating it, not by reading it.** D12's injectivity claim was
  wrong in `design.md`, written by the Architect, and survived being written, a spec delta built to gate
  it, and a worker implementing against it. It fell out of computing the encoding over a handful of
  awkward filenames. Related: **grouping is not identity** — "no two files share a route" and "this route
  identifies this file" look like one property and are not; the second implies the first, never the
  reverse (§3).
- **State a rule on the side where the property lives.** D12's ambiguity rule was wrong three times while
  stated filename-side and correct the first time it was stated route-side. Each filename-side attempt
  *sampled* the mechanism; the route-side one *is* the mechanism (§3).
- **A library's guarantee covers what its API names, not what you wanted.** Markdig's `DisableHtml()`
  disables HTML; it never claimed to sanitize URLs, and `MarkupString` never claimed to sanitize
  anything. Both were true; the composition was not what D13 assumed (§3).
- **When a guard's justification names a *case*, check the guard's *branch*** — the branch is what
  ships. §2 produced five claim-versus-mechanism mismatches, every one with defensible code and a wrong
  stated reason: the unborn-`HEAD` comment, the index-census docstring, blocker 1's "delta" wording,
  `EnsureInitialCommitAsync`'s early return, and the `docs/` guard scoped in prose to "adopted" while
  applying universally (§2).
- **Treat a justification as something to reproduce before writing it down, at the same standard as a
  test.** §2's *code* converged; its *prose* kept regressing, and prose that lands in `design.md` binds
  every later section. Three wrong justifications survived a reviewer `Approve` each, and were caught
  only when someone ran them (§2).
- **Mutation measures whether a condition is faithful to its intent; it is silent on whether the intent
  is the right question.** Every defect §2 actually produced — the index census, `core.quotePath`, the
  `newMode`-only guard, the missing `docs/` branch — was a gap in *what the condition asks*, and none
  was reachable by a mutant. Both mutants run were sound and both killed cleanly. **Budget fixture
  diversity alongside mutants**; here the cheaper instrument was a fixture nobody thought to build (§2).
- **A fixture built by the code under test cannot falsify that code.** Both §2 supervisor blockers lived
  on the branch that adopts a foreign repository — the one branch with no fixture of its own, because
  every "existing repository" test constructed a repository ZeroWiki itself had made. Third instance of
  the same shape in this change, after §11's service-model differential and §2's
  `Path.GetTempPath()` roots (§2).
- **A differential is only as good as the surface it models.** When a rule is enforced on two
  surfaces, run the differential on **both** — two 666k-input runs missed F1 because both classified
  through the service model, in the same section that discovered the form disagrees with it (§11).
- **Produce every counterfactual on the engine that ships.** `new Regex(...)` is **not**
  `[GeneratedRegex]`; they disagreed by ~80x here and produced a fully self-consistent wrong answer
  (§11).
- **A timing comment may assert a direction or an order of magnitude, never a precise value**, and
  every timing figure names its engine. A precise value measures a machine and rots silently on
  someone else's (§11).
- **An assertion's justification is a claim about a counterfactual**, and the only instrument that
  checks it is the mutation that produces it. Seven claims-about-why were wrong in §11 while the code
  was fine; every one was caught by mutating, none by reading (§11).
- **A mutation harness must `cp` the target aside and restore from that copy.** `git checkout --` /
  `git restore --` restore from `HEAD` and take an uncommitted block's own edits with them — it
  happened here despite the rule already being written down, and the **checksum-before-and-after** is
  what caught it, twice (§1, §11).
- **A raw checksum is not a valid instrument for a live WAL-mode SQLite file** — checkpointing churns
  pages with no logical change. Compare `sqlite3 .dump` or row counts (§1).
- **Any regex harness must carry an instrument self-check** — Perl interpolated `$\` out of a pattern
  and produced a fully self-consistent wrong answer (§0).
- **`safe.directory` is `--system`, set as root before the `USER` switch.** HOME-scoped `--global`
  dies in §7's CGI subprocess.
- **Every instrument failure in this change so far has been in the harness, not the code.** Assume the
  measurement is wrong before assuming the finding is real.

Design questions outstanding: **none.** `design.md`'s Open Questions remain fully resolved; D11 still
states the `accepted ⊆ legal` posture whose absence was S1; D15 now records §4's index decisions. The
one entry that claimed a Product Owner call was owed before §5 — forward obligation 1 — was **stale**,
and was verified against `EnsureRepositoryAsync`'s actual ordering at §4's close-out rather than
re-read from this file: §2 had already shipped the fix in `f50f1ca`, and `design.md` already carries the
corrected posture. §5 opens on the Product Owner's go-ahead, not on a design question.
