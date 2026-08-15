## ADDED Requirements

### Requirement: An initialized repository ignores editor configuration

Where the system initializes a content repository itself, it SHALL seed an ignore rule for the Obsidian
editor's configuration directory (`.obsidian/`) as part of that repository's initial commit, so that
configuration written into a vault by an editor is not staged by the system's own unscoped staging of
the repository root, committed under the identity that reconciled it, or propagated to every other vault
that clones the remote.

The rule SHALL apply wherever the directory appears beneath the repository root, since the vault may be
opened at the repository root or at its working tree.

The system SHALL NOT untrack, delete, or rewrite anything already committed. A repository that already
carries editor configuration in its history keeps it — removing it would delete a user's local editor
configuration out of every vault on its next pull.

The system SHALL NOT add to, amend, or otherwise modify the ignore rules of a repository it did not
initialize. A volume presented with an existing repository is adopted as it stands.

#### Scenario: A freshly initialized repository ignores editor configuration

- **WHEN** the system initializes a content repository on an empty volume, and an editor subsequently
  writes its configuration directory into the working tree
- **THEN** that directory is not staged, committed, or pushed by the system, and the repository's
  initial commit already carried the rule that excludes it

#### Scenario: Editor configuration already in history is left alone

- **WHEN** a content repository already has the editor's configuration directory committed in its
  history
- **THEN** the system leaves those tracked files exactly as they are — it does not untrack or delete
  them, and the working tree remains clean

#### Scenario: An adopted repository's ignore rules are not touched

- **WHEN** the system starts against a volume that already contains a git repository, whatever ignore
  rules that repository does or does not carry
- **THEN** the system does not create, append to, or amend an ignore file in it
