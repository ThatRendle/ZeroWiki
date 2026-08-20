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

**D5 — A `.gitignore` already on the volume is appended to, and the question is put to git (Product
Owner decision).** A volume can have no commits yet and still carry a hand-written `.gitignore` —
content copied on before the first start. That is a third case the decisions above did not name: the
repository is one ZeroWiki *initializes*, so D1 applies and the rule must be in the initial commit, but
the file itself is one a human wrote, which is the thing the adopt-as-it-stands posture exists to
protect. Skipping the seed (the first implementation) satisfies neither — it stages the operator's file
and commits without the rule, contradicting the requirement outright.

So: **ask git whether `.obsidian/` is already ignored, and append the rule only if it is not.** The test
is `git check-ignore`, not a text search of the file. An operator's own file may already exclude the
directory by some other pattern — a broader glob, an `.obsidian` entry without the slash, an
`.gitignore` in `docs/` — and a text search would miss all of them and append a redundant duplicate.
`check-ignore` answers the question that actually matters, which is whether git ignores the path, not
whether a particular string appears in a particular file.

The probe must ask about **both** levels — `.obsidian/` and `docs/.obsidian/` — and append unless git
reports both already ignored. An anchored rule (`/.obsidian/`, the shape git itself suggests) matches
the root and not `docs/`, so a root-only probe answers "already ignored" while leaving the `docs/` case
open to reconciliation's unscoped staging. Confirmed against git 2.55.0: `/.obsidian/` gives exit 0 for
`.obsidian/` and exit 1 for `docs/.obsidian/`. Appending the unanchored rule where only one level is
already covered is harmless, so "unless both" is the safe side of the question. *Alternative considered:* overwrite the file
with ours. Rejected for the same reason as D2 — it destroys something the operator wrote, and this
change does not damage user data to tidy up after an editor.

Appending is never a rewrite of history: at this point the file is uncommitted, so the initial commit
carries the operator's rules **and** ours. Nothing is removed from it.

## Risks / Trade-offs

- **An operator wants `.obsidian/` tracked** (a single-user wiki where the vault config is worth
  versioning) → they remove the line; it is an ordinary `.gitignore` in their own repository, and nothing
  in ZeroWiki rewrites it after bootstrap (D1).
- **Existing deployments keep the noise** → accepted, and it is the deliberate consequence of D2. The
  Product Owner's own wiki is one of them.
- **An operator's own negation is silently overridden** (Product Owner decision, accepted 2026-08-20).
  Where a pre-existing root `.gitignore` re-includes the directory for some path — `.obsidian/` followed
  by `!docs/.obsidian/` — appending our unanchored rule to that same file makes the negation stop
  taking effect. Reproduced on git 2.55.0:

  ```
  $ printf '.obsidian/\n!docs/.obsidian/\n' > .gitignore
  $ git check-ignore -q docs/.obsidian/ ; echo $?   # 1 — the negation works
  $ printf '.obsidian/\n' >> .gitignore            # what bootstrap does
  $ git check-ignore -q docs/.obsidian/ ; echo $?   # 0 — the negation is dead
  ```

  Nothing is removed textually, but the operator's configuration is behaviourally reversed on first
  boot, and reconciliation then stops staging content they had deliberately kept tracked. The exposure
  is **same-file only**: a negation in a `docs/.gitignore` is unaffected, because git gives the closer
  file priority regardless of what is appended at the root.

  **Accepted rather than fixed.** `check-ignore` returns a binary answer and cannot distinguish "never
  mentioned" from "explicitly re-included"; telling them apart means parsing `!` lines, which is exactly
  the text inspection D5 exists to avoid — and a partial `!` parser would be a *worse* instrument than
  the one it replaced, wrong in cases nobody enumerated. The operator's remedy is the ordinary one: it
  is their own `.gitignore` in their own repository, nothing rewrites it after bootstrap (D1), and
  re-ordering the negation below our line restores it. §2 carries a characterisation test so the
  behaviour is visible in the suite rather than discovered in the field.

- **The rule silently hides a genuine page** → it cannot: enumeration already skips every dot-prefixed
  entry (`PageEnumerationService.cs:162`), so nothing under `.obsidian/` was ever servable. The ignore
  rule removes it from *history*, not from the served set.
- **Queued behind `git-backed-content-core`'s archive**, along with `fix-changed-on-disk-broadcast` and
  `fix-reconciliation-index-blindness`. Three changes now depend on that event; the ordering between them
  is unconstrained, as they touch disjoint code.

## Open Questions

None.
