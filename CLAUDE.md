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
  `[reviewer]` — and references the **block** (`N.1–N.3`) it concerns.
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

**This section is authoritative.** If a skill's behavior ever conflicts with what's written here,
**follow this document.**

A change moves through three phases. The `opsx` skills drive the first two; this document spells out
the third in full:

- **Explore** (`opsx:explore`) — **Analyst** hat. Work with the Product Owner to shape *what* to build.
- **Propose** (`opsx:propose`) — **Architect** hat. Shape *how*: the proposal, `design.md`, and
  `tasks.md`.
- **Apply** (`opsx:apply`) — **Architect** hat. Everything below: implement the change **block by
  block** via the `worker`/`reviewer` split.

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

All agents are defined for this repo. Delegate; don't shortcut by writing the implementation yourself.

### 1. Select the change

1. List active changes = directories in `openspec/changes/` **excluding `archive/`**.
2. **Always ask the Product Owner which change to apply**, even when there is exactly one. If there are
   none, say so and stop.
3. Resume point = the **first unticked `- [ ]` task** in that change's `tasks.md`.

### 2. Pre-flight (Architect, before the first block)

1. Read `proposal.md`, `design.md`, and the relevant `specs/<capability>/spec.md` for the section(s)
   you're about to work.
2. **Working tree must be clean** (`git status`). If it's dirty, stop and ask.
3. **Change must validate**: `openspec validate <change-name> --strict`. If it doesn't, stop and ask.
4. **Be on the change branch** `change/<change-name>`. Create it from the default branch if missing:
   `git switch -c change/<change-name>`.

### 3. Implement — block by block

Walk the change's sections (`## N.` sections) in order from the resume point. **The unit of work is
not the whole section — it is a *block*:** a coherent run of tasks within one section (e.g. `N.1–N.3`)
that makes sense to build and review as one deliverable and land as one commit. You (Architect) carve
each section into blocks; a section is one or more blocks, and **a block never spans sections** — if a
block wants to, the section breakdown is wrong.

For each block:

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

   Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
   ```
   Commit the DEVLOG with the block.

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
  ticking that task**.

**On stopping mid-block:** leave the WIP **uncommitted**, do **not** tick the block, do **not** revert.
Log the stop in the DEVLOG and report the **exact task (`N.M`)** that stopped you and why. The WIP stays
in the working tree for the Product Owner to inspect.

### 5. Done

When every task in the change is ticked and the final review is clean:

1. Report status to the Product Owner: blocks landed, commits made, test summary.
2. **Propose archiving** — offer to run `/opsx:archive` and **wait for the Product Owner's
   confirmation**. Do not archive automatically.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->