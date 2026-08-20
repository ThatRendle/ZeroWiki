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

Where a volume the system is initializing — one with no commits yet — already carries an ignore file at
the repository root, the system SHALL append its rule unless that file already carries the same rule as
a line of its own, and SHALL NOT overwrite or remove anything already present. The comparison is against the file's lines, and a line that is
commented out does not count as the rule being present.

The system SHALL NOT attempt to determine whether the directory is already ignored by some other means
— a differently shaped pattern, an ignore file elsewhere in the tree, or the host's global ignore
configuration. Where one of those already covers the directory, the appended rule is redundant and
harmless. Being wrong in that direction is deliberate: a redundant line costs an operator nothing,
while failing to add the rule ships a repository that is not protected and whose owner believes it is.

Where the volume's own ignore rules would prevent the seeded rule from being staged, the system SHALL
fail to start rather than complete an initial commit without it. Proceeding would leave an operator
holding a repository that looks initialized and is not protected.

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

#### Scenario: An ignore file already on the volume is added to, not replaced

- **WHEN** the system initializes a content repository on a volume that has no commits yet but already
  carries an ignore file written by an operator
- **THEN** the operator's existing rules survive into the initial commit, and that commit also carries a
  rule excluding the editor's configuration directory — unless that file already carries the same rule
  as a line of its own, in which case nothing is appended

#### Scenario: An ignore rule that would hide the seeded rule refuses the start

- **WHEN** the system initializes a content repository on a volume whose own ignore file matches the
  ignore file itself, so that the seeded rule cannot be staged
- **THEN** the system fails to start, and no initial commit is made that lacks the rule
