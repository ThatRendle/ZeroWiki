## Context

`ContentRepositoryService`'s bootstrap path already writes `docs/.gitkeep`, stages exactly that path, and
makes an initial commit under the system identity (`ContentRepositoryService.cs:725-740`). Startup
reconciliation, separately, stages the repository root **unscoped** (`git add -A`, `:792`) — that is
deliberate and is what makes an out-of-band edit recoverable, but it means anything a third-party editor
drops anywhere under the root becomes tracked content.

`ContentPaths` already records the shape of the problem in prose: the DataProtection key ring and the
write-lock file are both placed **outside** `RepositoryRoot` precisely because "a key file written under
the content repo would be picked up by commit-on-save and pushed to every Obsidian vault that clones the
remote". `.obsidian/` is the same hazard arriving from the other direction — we cannot relocate it,
because the editor chooses where it goes.

## Goals / Non-Goals

**Goals:**

- A repository ZeroWiki initializes does not accumulate editor configuration as tracked content.
- The rule is in place from the first commit, before any vault can be opened against it.

**Non-Goals:**

- Cleaning up repositories that already carry `.obsidian/` — explicitly out of scope, see D2.
- Ignoring anything else. This is not a general-purpose ignore policy, and no other editor, OS artefact,
  or tool is being catered for speculatively.
- Changing reconciliation's unscoped staging. That behaviour is load-bearing and correct.

## Decisions

**D1 — Seed it at bootstrap, not at every start.** The `.gitignore` is written and staged alongside
`docs/.gitkeep` in the same initial commit, so a repository is never observable in a state where the rule
is missing. *Alternative considered:* ensure-on-every-start, so existing deployments gain it too.
Rejected — it makes the app mutate a repository it did not create, on every boot, which contradicts the
adopt-as-it-stands posture the `content-store` spec already takes for an existing volume, and would
produce a commit (or a dirty tree) at startup for every operator who had deliberately not wanted one.

**D2 — Ignore-only; never untrack.** A `.gitignore` does not affect already-tracked paths, and this
change deliberately does not add a `git rm --cached`. Untracking would propagate as a **deletion** to
every vault on its next pull, removing a user's real editor configuration — their workspace layout, their
plugin settings — as a side effect of a server upgrade. Noise in history is a smaller harm than silently
deleting someone's local configuration, and the harm is not reversible from the server's side.
*Alternative considered:* untrack and document it in the release notes. Rejected — a destructive act on
user data does not become safe by being announced.

**D3 — Match the directory wherever it sits, because both vault layouts are real.** The Product Owner's
run established that `obsidian-git` works with the vault opened at the repository root *and* at
`docs/`, so `.obsidian/` can appear at either level. The rule must cover both rather than assuming the
documented layout.

**D4 — The falsifier is the unscoped stage, not the file's presence.** The test that matters drives the
path that actually caused the problem: write an editor-config file into the working tree, run the
reconciliation that stages the repository root unscoped, and assert it was **not** staged or committed.
Asserting only that a `.gitignore` exists with the right text would pass against a rule that git does not
apply — which is precisely the kind of green-but-vacuous check this project has been bitten by. *This
test must be shown to fail with the rule removed.*

**D5 — A `.gitignore` already on the volume is appended to unless it already carries our exact line
(Product Owner decision, 2026-08-20 — supersedes the earlier `check-ignore` design).** A volume can have
no commits yet and still carry a hand-written `.gitignore`. That file is adopted, not overwritten: read
its lines, and append `.obsidian/` unless one of them already *is* `.obsidian/`.

**This deliberately asks a narrower question than "is the directory ignored here?"** The earlier design
put that wider question to `git check-ignore`, and it was built, reviewed across four rounds, and
withdrawn. What sank it was not any single defect but the class: `check-ignore` answers whether *this
machine, right now* ignores the path, which is not the property the spec cares about — the spec cares
whether *the repository* ignores it, since that is what travels to every clone. The two coincide on a
clean host and diverge on a configured one, and every divergence found failed in the same direction:

- an anchored `/.obsidian/` covered the root and not `docs/`, so a root-only probe skipped the append;
- appending after an operator's same-file `!docs/.obsidian/` silently killed their negation;
- the host's `core.excludesFile` or `$GIT_DIR/info/exclude` makes the probe answer "already ignored", so
  the repository ships with no rule and is unprotected for everyone who clones it.

Each was fixable; the class was not. **The decisive argument is the shape of each design's failure.**
The precise instrument fails by shipping a repository with no rule, silently, to an operator who
believes they are protected. The blunt one fails by writing a line that was already covered — redundant,
harmless, visible. Where an instrument must be wrong sometimes, be wrong in the direction that costs
nothing.

Consequences accepted knowingly: a differently shaped rule (`.o?sidian/`), a rule in `docs/.gitignore`,
or a global ignore all produce a duplicate line. None of them break anything.

**What this revision does *not* change is the negation override** — see the Risks entry below. An
operator whose file happens to carry the literal line `.obsidian/` alongside a `!` re-inclusion is now
left alone, because the text is present and nothing is appended; but any other shape has no matching
line, so we append and our rule wins, exactly as before. The override is a property of appending to the
end of someone else's file, which this design does as much as the withdrawn one. An earlier draft of
this decision claimed the revision dissolved it. It does not.

**Match a whole trimmed line, never a substring.** A substring search matches
`# .obsidian/ is deliberately tracked` inside a comment and skips the append — a silent failure in the
one direction this decision exists to avoid. A commented line does not count as the rule being present.

**Root `.gitignore` only** (Product Owner decision): the simplest thing that works. A rule in
`docs/.gitignore` is not consulted, and the resulting duplicate is harmless per the above.


## Risks / Trade-offs

- **An operator wants `.obsidian/` tracked** (a single-user wiki where the vault config is worth
  versioning) → they remove the line; it is an ordinary `.gitignore` in their own repository, and nothing
  in ZeroWiki rewrites it after bootstrap (D1).
- **Existing deployments keep the noise** → accepted, and it is the deliberate consequence of D2. The
  Product Owner's own wiki is one of them.
- **An operator's own negation is silently overridden** (Product Owner decision, accepted 2026-08-20;
  **restored 2026-08-20** after being deleted on a wrong argument — see below). Where a pre-existing
  root `.gitignore` re-includes the directory for some path, appending our rule to the end of that file
  makes the re-inclusion stop taking effect. Reproduced on git 2.55.0 with an anchored rule, one of the
  three shapes D5 names:

  ```
  $ printf '/.obsidian/\n!docs/.obsidian/\n' > .gitignore
  $ git check-ignore -q docs/.obsidian/ ; echo $?   # 1 — the negation works
  $ printf '.obsidian/\n' >> .gitignore            # what bootstrap appends
  $ git check-ignore -q docs/.obsidian/ ; echo $?   # 0 — the negation is dead
  ```

  **This survives D5's revision, and the reasoning that deleted it was wrong.** The deletion argued that
  a text check finds the operator's `.obsidian/` line and appends nothing. That holds only for a file
  whose exclusion is written as exactly `.obsidian/`. Any other shape — anchored, globbed, or without
  the trailing slash — has no matching line, so we append, and our line being last wins. **The override
  was never a property of `check-ignore`; it is a property of appending to the end of someone else's
  file, which both the withdrawn design and the current one do.**

  Still accepted, for the reason it was accepted originally: detecting it means parsing `!` lines, and a
  partial `!` parser is a worse instrument than the one it would protect. The exposure is same-file
  only — a negation in `docs/.gitignore` is unaffected, since git gives the closer file priority. The
  operator's remedy is to move their negation below our line.

- **A pre-existing ignore rule matching `.gitignore` itself refuses the boot** (Product Owner decision,
  accepted 2026-08-20). Bootstrap stages by explicit pathspec (`git add docs/.gitkeep .gitignore`), and
  `git add` on an explicit pathspec that an ignore rule matches **fails** rather than skipping. So a
  volume whose hand-written ignore file contains `.gitignore` or `*` makes bootstrap throw and the app
  refuse to start — on precisely the volume shape D5 exists to accommodate.

  **Fail fast is the intended behaviour**, consistent with this project's standing posture: refuse to
  start on a filesystem or configuration fault rather than degrade. The alternative — skipping the
  stage and committing without the rule — is the silent failure this change exists to prevent, and it
  would leave an operator believing they were protected. A refusal names the problem at the only moment
  anyone is looking, and the remedy is a line in the operator's own file.

- **The rule silently hides a genuine page** → it cannot: enumeration already skips every dot-prefixed
  entry (`PageEnumerationService.cs:162`), so nothing under `.obsidian/` was ever servable. The ignore
  rule removes it from *history*, not from the served set.
- **Queued behind `git-backed-content-core`'s archive**, along with `fix-changed-on-disk-broadcast` and
  `fix-reconciliation-index-blindness`. Three changes now depend on that event; the ordering between them
  is unconstrained, as they touch disjoint code.

## Open Questions

None. The `check-ignore` approach and its open question about the host's global ignore configuration
were withdrawn with D5's revision; nothing about the text-line rule depends on the answer.
