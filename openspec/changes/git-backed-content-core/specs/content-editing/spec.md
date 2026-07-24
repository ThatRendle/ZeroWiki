## ADDED Requirements

### Requirement: Commit-on-save

The system SHALL persist every browser save by writing the file to the working tree and creating exactly one git commit per save-point, authored as the logged-in ZeroWiki user. The system SHALL NOT create a commit per keystroke; rapid edits SHALL be coalesced to a save-point (for example by debounce or explicit save) before committing.

#### Scenario: Save creates one authored commit

- **WHEN** an authenticated user saves a page
- **THEN** the system writes the file and creates a single git commit whose author is that user

#### Scenario: Rapid edits coalesce to one commit

- **WHEN** a user makes many keystroke-level changes within a short editing burst
- **THEN** the system creates one commit for the resulting save-point rather than one commit per change

### Requirement: Optimistic concurrency on save

The system SHALL require each save to declare the base revision it started from and SHALL reject the save with a conflict result when the file has advanced beyond that base revision, rather than overwriting the newer content.

#### Scenario: Save on current base succeeds

- **WHEN** a user saves a page whose declared base revision matches the current revision of that file
- **THEN** the system accepts the save and commits it

#### Scenario: Save on stale base is rejected

- **WHEN** a user saves a page whose declared base revision is older than the current revision (because another browser save or an incoming push changed it)
- **THEN** the system rejects the save with a conflict result and does not overwrite the newer content

### Requirement: Single per-repo write lock

The system SHALL serialize all repository writes — browser commits and git push receipt — through a single cross-process lock so that no two writers mutate the repository concurrently and the working tree is never left dirty by an interleaving.

#### Scenario: Push waits for an in-progress save

- **WHEN** a browser save holds the write lock and a git push arrives
- **THEN** the push waits until the save releases the lock before updating the working tree

#### Scenario: Save waits for an in-progress push

- **WHEN** a git push holds the write lock and a browser save is submitted
- **THEN** the save waits until the push releases the lock before writing and committing

### Requirement: Transactional save

The system SHALL treat write-then-commit as an atomic operation: if the commit fails, the system SHALL restore the working tree so no uncommitted change remains.

#### Scenario: Failed commit rolls back the write

- **WHEN** the file has been written but the commit step fails
- **THEN** the system restores the affected file to its committed state, leaving the working tree clean
