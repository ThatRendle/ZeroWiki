## ADDED Requirements

### Requirement: Commit-on-save

The system SHALL persist every browser save by writing the file to the working tree and creating exactly one git commit per save-point, authored as the logged-in ZeroWiki user. The system SHALL NOT create a commit per keystroke; rapid edits SHALL be coalesced to a save-point (for example by debounce or explicit save) before committing.

The author identity the system records SHALL be a well-formed address for **every** account, including one whose username predates the username form rules. The username form rules govern only the choosing of a username, so they cannot make this true on their own.

Where a save's content is identical to what is already committed, the system SHALL report the save as successful and SHALL NOT create a commit recording no change.

#### Scenario: Save creates one authored commit

- **WHEN** an authenticated user saves a page
- **THEN** the system writes the file and creates a single git commit whose author is that user

#### Scenario: Rapid edits coalesce to one commit

- **WHEN** a user makes many keystroke-level changes within a short editing burst
- **THEN** the system creates one commit for the resulting save-point rather than one commit per change

#### Scenario: Saving unchanged content records nothing and still succeeds

- **WHEN** a user saves a page whose content is byte-identical to the committed content
- **THEN** the system reports the save as successful, adds no commit to the history, and does not report a failure — leaving the working tree exactly as clean as it already was, by the same invariant every other save path relies on rather than by a check unique to this one

#### Scenario: An account predating the username rules still commits a well-formed author

- **WHEN** a user whose username would not satisfy the current username form rules saves a page
- **THEN** the system still records a well-formed author address for that commit, rather than emitting a malformed one or refusing the save

### Requirement: Optimistic concurrency on save

The system SHALL require each save to declare the base revision it started from and SHALL reject the save with a conflict result when the file has advanced beyond that base revision, rather than overwriting the newer content.

#### Scenario: Save on current base succeeds

- **WHEN** a user saves a page whose declared base revision matches the current revision of that file
- **THEN** the system accepts the save and commits it

#### Scenario: Save on stale base is rejected

- **WHEN** a user saves a page whose declared base revision is older than the current revision (because another browser save or an incoming push changed it)
- **THEN** the system rejects the save with a conflict result and does not overwrite the newer content

### Requirement: Single per-repo write lock

The system SHALL serialize all repository writes — browser commits and git push receipt — through a single cross-process lock so that no two writers mutate the repository concurrently and the working tree is never left dirty by an interleaving. The system SHALL bound how long a browser save waits to acquire this lock, and SHALL fail the save cleanly — without writing anything — if that bound is exceeded. The system SHALL NOT apply any such bound to a git push's wait for the same lock.

#### Scenario: Push waits for an in-progress save

- **WHEN** a browser save holds the write lock and a git push arrives
- **THEN** the push waits until the save releases the lock before updating the working tree

#### Scenario: Save waits for an in-progress push

- **WHEN** a git push holds the write lock and a browser save is submitted
- **THEN** the save waits until the push releases the lock before writing and committing

#### Scenario: Save's bounded wait for the lock expires

- **WHEN** a git push holds the write lock for longer than the configured write-lock wait bound and a browser save is submitted
- **THEN** the system stops waiting once the bound is reached, fails the save with a distinct "repository busy" result rather than the stale-revision conflict result, and does not write or commit anything

#### Scenario: Push's wait for the lock has no ceiling

- **WHEN** a git push has waited for the write lock held by a browser save for longer than the configured write-lock wait bound
- **THEN** the push continues waiting rather than being rejected, and updates the working tree once the save eventually releases the lock

### Requirement: Transactional save

The system SHALL treat write-then-commit as an atomic operation: if the commit fails, the system SHALL restore the working tree so no uncommitted change remains.

#### Scenario: Failed commit rolls back the write

- **WHEN** the file has been written but the commit step fails
- **THEN** the system restores the affected file to its committed state, leaving the working tree clean
