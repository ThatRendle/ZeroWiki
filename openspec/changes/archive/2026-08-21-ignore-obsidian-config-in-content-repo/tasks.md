## 1. Seed the rule at bootstrap

- [x] 1.1 Write a `.gitignore` ignoring `.obsidian/` wherever it appears beneath the repository root, alongside the existing `docs/.gitkeep` seeding, and stage it into the same initial commit
- [x] 1.2 Confirm bootstrap leaves the working tree clean afterwards, so the invariant asserted immediately after initialization still holds
- [x] 1.3 Where the volume already carries a root `.gitignore`, append `.obsidian/` unless one of its lines already is exactly that, leaving the operator's own rules intact (D5)

## 2. The falsifier

- [x] 2.1 Test that an editor-config file written into the working tree is not staged or committed by the reconciliation that stages the repository root unscoped — driving that path, not merely asserting the `.gitignore`'s text
- [x] 2.2 Demonstrate 2.1 failing with the rule removed, and record the output — a rule git does not actually apply would pass a text assertion
- [x] 2.3 Test that a repository already carrying the editor's configuration in its history keeps those tracked files, and that the tree stays clean
- [x] 2.4 Test that a repository ZeroWiki adopts rather than initializes has its ignore rules left untouched
- [x] 2.5 Test the pre-existing root `.gitignore`: an unrelated rule (append, both survive); the exact rule already present as its own line (append nothing, file left byte-identical); and the rule present only inside a comment (append, because a commented line is not the rule) — the third is the one a substring check fails (D5)
- [x] 2.6 Pin git configuration in every fixture this section adds. The rule itself no longer consults git, but the tests still assert that a path *is ignored*, and that assertion reads `$GIT_DIR/info/exclude` and `core.excludesFile` — an unpinned fixture inherits the developer's machine and can pass for a reason that has nothing to do with the code
- [x] 2.7 Test the fail-fast path: a volume whose pre-existing ignore file matches `.gitignore` itself makes bootstrap fail rather than commit without the rule — asserting the refusal, not merely that something threw

## 3. Documentation

- [x] 3.1 Update the README's `.obsidian/` paragraph to say the rule is seeded for new repositories, and that existing ones keep what they already track — stating what happens, without explaining the plugin's or git's internals
