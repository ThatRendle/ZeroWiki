## 1. Seed the rule at bootstrap

- [ ] 1.1 Write a `.gitignore` ignoring `.obsidian/` wherever it appears beneath the repository root, alongside the existing `docs/.gitkeep` seeding, and stage it into the same initial commit
- [ ] 1.2 Confirm bootstrap leaves the working tree clean afterwards, so the invariant asserted immediately after initialization still holds

## 2. The falsifier

- [ ] 2.1 Test that an editor-config file written into the working tree is not staged or committed by the reconciliation that stages the repository root unscoped — driving that path, not merely asserting the `.gitignore`'s text
- [ ] 2.2 Demonstrate 2.1 failing with the rule removed, and record the output — a rule git does not actually apply would pass a text assertion
- [ ] 2.3 Test that a repository already carrying the editor's configuration in its history keeps those tracked files, and that the tree stays clean
- [ ] 2.4 Test that a repository ZeroWiki adopts rather than initializes has its ignore rules left untouched

## 3. Documentation

- [ ] 3.1 Update the README's `.obsidian/` paragraph to say the rule is seeded for new repositories, and that existing ones keep what they already track — stating what happens, without explaining the plugin's or git's internals
