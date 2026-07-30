# ZeroWiki

ZeroWiki is a zero-config, invite-only Markdown wiki: point it at a folder and it Just Works, deployed as a Docker container over a mounted volume. Content lives in a **git repository that is the source of truth**, edited from a browser (ASP.NET Core 10, Blazor Web App with Static SSR + interactive islands) and synced to an Obsidian vault on a laptop over a Smart HTTP git remote. Identity is invite-only with per-user git access tokens, backed by SQLite.

Spec-driven development is managed with **OpenSpec** (`openspec/`). All feature work flows through a
change in `openspec/changes/`.

## The DEVLOG — the change's shared working channel

Every active change keeps a **`DEVLOG.md`** next to its `tasks.md`
(`openspec/changes/<name>/DEVLOG.md`). It is **not** a solo journal — it is the **shared channel** the
Analyst/Architect, the worker(s), and the reviewer all write to as they work, like a thread in a chat
room. Conventions:

- Organised by `## N.` **section** (mirroring `tasks.md`), with a pinned `## NEXT` at the bottom.
- Each post is **attributed** — prefixed with the author's role: `[architect]`, `[worker]`,
  `[reviewer]`, `[supervisor]` — and references the **block** (`N.1–N.3`) it concerns.
- **The first post under each `## N.` heading is the section's base commit** —
  `**[architect]** Base: <sha> — <what this section delivers>`. The supervisor's review scope is
  `git diff <sha>..HEAD`, so this post is load-bearing, not ceremony.
- **Questions** are addressed in-thread: `❓ @architect — spec says X but design says Y; which?`, and
  answered by the addressee. Handoffs read `→ @reviewer`. The whole review loop lives here.
- **Append-only** — posts persist forever; only `## NEXT` is rewritten. The DEVLOG is committed with
  each block and moves to the archive (`openspec/changes/archive/YYYY-MM-DD-<name>/`) with the change,
  so a shipped change's DEVLOG is the durable record of *how* it was built, not just *what* it
  specified.

Read it to pick up in-flight context; write to it as you act. The `/devlog` skill maintains it.

When an OpenSpec change is archived, use the `mcp__meko__artifact_put` tool to upload the
DEVLOG.md file to Meko.

## Commands

- Build: `dotnet build` — must be clean.
- Test: `dotnet test` — all green.
- Format: `dotnet format --verify-no-changes` — clean.
- Validate a change: `openspec validate <change-name> --strict`.
- List changes: `openspec list` (or the directories under `openspec/changes/`, excluding `archive/`).

---

## OpenSpec Workflow
<!-- dmons-scaffold: 0.3.0 -->

**This section is authoritative.** If a skill's behavior ever conflicts with what's written here,
**follow this document.**

A change moves through three phases. The `opsx` skills drive the first two; this document spells out
the third in full:

- **Explore** (`opsx:explore`) — **Analyst** hat. Work with the Product Owner to shape *what* to build.
- **Propose** (`opsx:propose`) — **Architect** hat. Shape *how*: the proposal, `design.md`, and
  `tasks.md`.
- **Apply** (`opsx:apply`) — **Architect** hat. Everything below: implement the change **section by
  section, block by block** via the `worker`/`reviewer` split, with a `supervisor` auditing each
  finished section.

### Roles — the Product Owner owns the vision; the main thread never writes feature code

- **Product Owner** = the user. They hold the vision. Every *product* call — what to build, which
  change to apply, how to resolve an ambiguity or a wrong spec — is theirs. You realise their vision;
  you do not decide it for them.
- **Analyst/Architect** = the main thread (you). One role, two hats — and you should know which you're
  wearing:
  - **Analyst** during `opsx:explore` — shaping *what* with the Product Owner.
  - **Architect** during `opsx:propose` and the whole apply below — you shape *how*, then orchestrate
    the build: read specs, carve work into blocks, brief agents, run the gates, tick boxes, and commit.
    **You do not implement feature code directly.**
- **`worker`** agent — implements each block.
- **`reviewer`** agent — audits each block's diff (one reviewer for the whole change, every stack).
- **`supervisor`** agent — audits each finished `## N.` section as a whole, once all its blocks have
  landed (one supervisor for the whole change, every stack).

**The two auditors have different jobs and must not be swapped.** The `reviewer` is **diff-local** and
runs per block; the `supervisor` is the only agent that ever sees more than one block at a time, and
looks for what block reviews structurally cannot catch — cross-block drift, duplicated abstractions,
dead scaffolding, and whether the section genuinely satisfies its spec rather than merely ticking its
tasks. Neither ever edits code: both report, and a worker fixes.

All agents are defined for this repo. Delegate; don't shortcut by writing the implementation yourself.

### 1. Select the change

1. List active changes = directories in `openspec/changes/` **excluding `archive/`**.
2. **Always ask the Product Owner which change to apply**, even when there is exactly one. If there are
   none, say so and stop.
3. Resume point = the **first unticked `- [ ]` task** in that change's `tasks.md`.
4. **Check the preceding section closed.** Ticked boxes are not proof a section passed its supervisor
   review — a session can end after the last block commits and before the review runs. Before starting
   the resume point's section, read the DEVLOG: if the previous `## N.` has no `[supervisor]` `Approve`
   under it, run that review first (§3c). If it never got a `Base:` post either, reconstruct the range
   from `git log` and say so in the DEVLOG.

### 2. Pre-flight (Architect, before the first block)

1. Read `proposal.md`, `design.md`, and the relevant `specs/<capability>/spec.md` for the section(s)
   you're about to work.
2. **Working tree must be clean** (`git status`). If it's dirty, stop and ask.
3. **Change must validate**: `openspec validate <change-name> --strict`. If it doesn't, stop and ask.
4. **Be on the change branch** `change/<change-name>`. Create it from the default branch if missing:
   `git switch -c change/<change-name>`.

### 3. Implement — section by section, block by block

Walk the change's `## N.` sections in order from the resume point. There are **two nested loops**:

```
OUTER — for each ## N. section, in order
  ├─ post the section's base commit to the DEVLOG
  ├─ INNER — for each block in the section
  │    brief worker → worker implements → reviewer audits → loop until Approve
  │    → gates pass → tick boxes → commit
  └─ SECTION REVIEW — supervisor audits the whole section
       Approve → next section
       Request changes → carve a remediation block, re-enter INNER
```

**The unit of work is not the whole section — it is a *block*:** a coherent run of tasks within one
section (e.g. `N.1–N.3`) that makes sense to build and review as one deliverable and land as one commit.
You (Architect) carve each section into blocks; a section is one or more blocks, and **a block never
spans sections** — if a block wants to, the section breakdown is wrong.

#### 3a. Opening a section (outer loop)

Before briefing the first block of a `## N.` section, post its **base commit** to the DEVLOG as the
first entry under that heading:

```
**[architect]** Base: <sha> — <one line: what this section delivers>
```

`<sha>` is the current `HEAD` (`git rev-parse --short HEAD`). This is what gives the supervisor its
review scope at the end of the section (`git diff <sha>..HEAD`); without it, it has no reliable way to
see the section as a whole. Post it **before** any block of the section is committed.

#### 3b. Each block (inner loop)

1. **Brief the worker.** Post the brief to the DEVLOG (`[architect]`, under the block's `## N.`
   section): the block's tasks (`N.1`…`N.k`), the relevant spec excerpts, the binding design decisions
   that bind them, and the done-gates below. The worker shouldn't need to go hunting.
2. **Worker implements the block** and reports back, posting to the DEVLOG as it goes.
3. **Audit.** Spawn `reviewer` on the block diff (correctness, design-decision compliance, OpenSpec
   scope, C# idiom, auth/crypto correctness and git-integrity hazards). The reviewer posts its verdict
   to the DEVLOG.
4. **Review loop.** Worker and reviewer resolve findings **in the DEVLOG thread** — reviewer posts
   findings, worker fixes and responds, reviewer re-audits. **Repeat until the reviewer signs off.**
5. **Gates — all must pass before ticking any box:**
   - `dotnet build` clean (no errors)
   - `dotnet test` green — the block's new tests **and** all existing tests
   - `openspec validate <change-name> --strict`
   - `dotnet format --verify-no-changes` clean
   A block commits green. If a block must land with a failing test for a sound technical reason (e.g. a
   red test a later block in the same section turns green), that is a deliberate Architect call — state
   the reason in the DEVLOG **and** the commit body. Otherwise a failed gate sends you back to step 4,
   not to a commit.
6. **Tick the boxes.** Mark every `- [x] N.M` in the block in `tasks.md`.
7. **Commit — one conventional commit per block:**
   ```
   feat(<change-name>): <block summary> (N.1–N.3)

   - N.1 <task summary>
   - N.2 <task summary>
   ...

   Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
   ```
   Commit the DEVLOG with the block.

#### 3c. Closing a section — the supervisor review

When the **last block of a `## N.` section** has landed (reviewer approved, gates green, boxes ticked,
committed), the section is not done yet. Run the section review before opening the next one.

1. **Spawn `supervisor`** on the section's full range — `git diff <base-sha>..HEAD`, where `<base-sha>`
   is the one you posted in 3a. Point it at the section's spec requirements, not just its tasks. It
   posts its verdict to the DEVLOG under the section's heading as `[supervisor]`.
   - Run it for **every** section, including a single-block one — the lens is different from the
     reviewer's, not merely wider.
2. **`Approve`** → the section is closed. Roll any architectural notes into `## NEXT` and move to the
   next section.
3. **`Request changes`** → carve a **remediation block** from the findings and re-enter the inner loop
   (3b) with it: brief a worker, `reviewer` audits it, gates, commit.
   - The remediation block gets **no new `N.M` numbers** and ticks nothing — every box in the section
     is already ticked. The findings and the fix live in the DEVLOG; that is the record.
   - Commit it as a fix, not a feature:
     ```
     fix(<change-name>): address supervisor findings (section N)

     - <finding> — <what changed>
     ...

     Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
     ```
   - Then **re-run the supervisor** on the same `<base-sha>..HEAD` range (now including the fix).
4. **Two rounds, then stop.** If the supervisor still requests changes after one remediation block,
   **do not carve a third** — stop and put it to the Product Owner (§4). A section that won't converge
   in two rounds usually means the section breakdown or the spec is wrong, and more fixing won't
   resolve either.

**Do not open the next section until the current one has a supervisor `Approve`** (or the Product Owner
has explicitly waved it on). The whole point of the outer loop is that drift is caught before it is
built on.

### 4. Stop and ask — do not push on

These are the **Product Owner's** calls, not yours. Stop **immediately** and ask (do not improvise a
fix) when:

- a spec/design is **ambiguous**, or two specs **contradict** each other;
- doing the task properly needs changes **outside this change's scope** (its proposal/specs);
- a task is **blocked by an unresolved Open Question** in `design.md`;
- implementation or tests reveal the **spec itself is wrong** (not just the code);
- a task **requires human-in-the-loop verification** that can't be settled by automated gates — e.g.
  logging in or redeeming an invite through a real browser, confirming an Obsidian vault
  clones-pulls-pushes against the Smart HTTP git remote, or checking the first-run admin bootstrap UX.
  Implement and self-test as far as possible, then hand the Product Owner a precise, copy-pasteable way
  to verify (exact command, what to do, what they should see) and **wait for their confirmation before
  ticking that task**;
- the **supervisor still requests changes after one remediation block** (§3c.4) — report its findings
  and ask whether to remediate again, re-cut the section, or fix the spec.

**On stopping mid-block:** leave the WIP **uncommitted**, do **not** tick the block, do **not** revert.
Log the stop in the DEVLOG and report the **exact task (`N.M`)** that stopped you and why. The WIP stays
in the working tree for the Product Owner to inspect.

### 5. Done

When every task in the change is ticked and the **final section has a supervisor `Approve`**:

1. Report status to the Product Owner: sections closed, blocks landed, commits made, test summary, and
   any architectural notes the supervisor parked in `## NEXT`.
2. **Propose archiving** — offer to run `/opsx:archive` and **wait for the Product Owner's
   confirmation**. Do not archive automatically.

## Mutation testing — capped and scoped

Mutation testing is this project's evidence standard: a green suite is not proof a security property
holds, so break the property and check a test dies. It has earned its place — it has caught a live
concurrency defect, a `BootstrapConcurrencyTests` that only half-worked, an assertion that compared
only a URL's path, and a hasher recorder blind to the password. **It is also easy to run far past
the point of usefulness**, so it is bounded. **ZeroWiki is a wiki for a small trusted group, not a
system that warrants unbounded verification.**

1. **Cap confirmation runs at 3.** A mutant that dies 3/3 with a consistent, understood failure mode
   is confirmed. Exceed 3 **only** when results are genuinely flaky or nondeterministic and
   characterising that variance *is* the finding.
2. **Mutate security- and correctness-critical paths only** — auth, concurrency, and data integrity
   (in practice `BootstrapService`, `InvitationService`, `LoginService`, `GitTokenService`, the
   anonymous gate). **Not** general CRUD or wiki-page logic: ordinary unit tests with normal coverage
   are correct there.
3. **No polling loops with sleep plus background processes.** If a run must be backgrounded, use a
   bounded wait with a short timeout (~2 min) and report if it has not resolved.
4. **Stop and summarise when the mutant at hand is resolved.** Do not expand to other files without
   an explicit go-ahead. A genuine finding is **not** licence to keep digging in the same area — fix
   it and move on.

**Brief agents with these limits in the block brief itself.** Reining an agent in afterwards is what
made the rule necessary.

### Rules that make a mutation result mean something

- **Verify under the full `dotnet test`, never a filter.** A filtered run measures a condition the
  gate never runs in: `BootstrapConcurrencyTests` reported 3/3 filtered and 7/13 under the real
  parallel suite. A filtered figure is not wrong, it is *irrelevant* — never post one as the record.
- **Checksum the target before *and* after.** A no-op mutation is indistinguishable from a surviving
  mutant. A `\n`-vs-CRLF mismatch once silently modified nothing across three mutations.
- **Check your instrument before believing it.** Test any pattern you measure with against
  known-present markup first. Two agents once shared a blind spot — both anchor regexes required
  `href="…"` while Blazor renders `href=""` bare — so they corroborated each other while both were
  wrong. Two measurements agreeing is not corroboration when they share an instrument.
- **A surviving mutant may be correct.** Record it deliberately with the reason (an explicit
  `app.UseRouting()` whose removal changes nothing is kept because the ordering dependency is a
  security property). Never silently drop the result or edit the code to make it die.

### Hazard: an interrupted mutation run leaves a live mutant in `src/`

`BootstrapService.cs` was once found with `deferred: false` → `true` still applied after an agent was
stopped mid-run — the mutation that breaks "exactly one administrator", sitting in production code
with the working tree looking entirely ordinary.

- **Always `git diff -- src` before committing** anything that followed a mutation run. This is not
  ceremony.
- **Run `git status --short -- src` alongside it — the diff is blind to untracked files.** `git diff`
  reports only on files git already tracks; a file that has never been `git add`ed is not shown as
  unchanged, it is not shown *at all*. §7b mutated `GitEmailService.cs`, which was brand new and
  untracked for the whole block, so the mandated diff came back clean over a file it had never looked
  at. A new file is the *normal* case for a block that adds a service, which is exactly when mutation
  testing is most likely to run.
- **A `??` entry means "read it or checksum it", not "it's fine".** `git status` surfaces that an
  untracked file exists; it cannot show content, and for a new file git has no baseline to diff
  against, so **no git command can verify a mutant inside it**. Git gives you visibility here, not
  verification.
- **The content check is what actually protects you** — checksum the target before *and* after each
  mutation (already required above), revert via the harness, and have a second pair of eyes re-read or
  re-run the mutants. In §7b that discipline, not git, is what confirmed the file was clean.
- **Mutation harnesses must revert via `trap`/`finally`**, never a final step that an interruption
  can skip.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
