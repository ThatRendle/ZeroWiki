## 1. Seed the rule at bootstrap

- [x] 1.1 Write a `.gitignore` ignoring `.obsidian/` wherever it appears beneath the repository root, alongside the existing `docs/.gitkeep` seeding, and stage it into the same initial commit
- [x] 1.2 Confirm bootstrap leaves the working tree clean afterwards, so the invariant asserted immediately after initialization still holds
- [x] 1.3 Where the volume already carries an ignore file, ask git whether `.obsidian/` is already ignored and append the rule only if it is not, leaving the operator's own rules intact (D5)

## 2. The falsifier

- [ ] 2.1 Test that an editor-config file written into the working tree is not staged or committed by the reconciliation that stages the repository root unscoped — driving that path, not merely asserting the `.gitignore`'s text
- [ ] 2.2 Demonstrate 2.1 failing with the rule removed, and record the output — a rule git does not actually apply would pass a text assertion
- [ ] 2.3 Test that a repository already carrying the editor's configuration in its history keeps those tracked files, and that the tree stays clean
- [ ] 2.4 Test that a repository ZeroWiki adopts rather than initializes has its ignore rules left untouched
- [ ] 2.5 Test the pre-existing ignore file on a commit-less volume across the rule shapes that discriminate: an unrelated rule (append, both survive); a rule ignoring the directory by a *different pattern* that covers both levels (append nothing); and an *anchored* rule such as `/.obsidian/` that covers the root only (append, because `docs/` is still exposed) — a root-only probe passes the first two and fails the third (D5)
- [ ] 2.6 Characterisation test for the accepted risk: where the pre-existing file carries `.obsidian/` followed by `!docs/.obsidian/`, record that the appended rule overrides the operator's re-inclusion — asserting the behaviour the Product Owner accepted, so a future change to it is a visible test failure rather than a silent one

## 3. Documentation

- [ ] 3.1 Update the README's `.obsidian/` paragraph to say the rule is seeded for new repositories, and that existing ones keep what they already track — stating what happens, without explaining the plugin's or git's internals
