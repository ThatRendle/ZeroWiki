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

#### Scenario: Saving a page that does not yet exist creates it

- **WHEN** an authenticated user saves a page at an address where no file exists, declaring that it did not exist at the base revision
- **THEN** the system creates the file and commits it as that user, by the same single-commit and authorship rules as an edit to an existing page

### Requirement: A save refuses an address that does not identify exactly one file

The system SHALL refuse a save whose address does not identify exactly one content file, and SHALL make that refusal in the save itself rather than only in whatever surface submitted it. The refusal SHALL hold when the address became ambiguous after the content being saved was loaded, since an address can acquire a second claimant between a save being prepared and being applied. That second claimant arrives from content entering the repository outside the browser's own addressing — an incoming push, or a change made directly to the content volume — and not from another browser save, which can only ever write to the single file its address resolves to.

A save refused for this reason SHALL be reported distinctly from a save rejected for a stale base revision: the first means the address no longer names one file, the second means the file it names has moved on.

#### Scenario: Save through an ambiguous address is refused

- **WHEN** a save is submitted for an address that more than one content file resolves to
- **THEN** the system refuses the save, writes and commits nothing, and does not silently apply it to one of the claiming files

#### Scenario: An address that becomes ambiguous before the save is applied is refused

- **WHEN** a save was prepared for an address identifying exactly one file, and a second file resolving to that same address arrives before the save is applied
- **THEN** the system refuses the save rather than applying it, because the address no longer identifies the file the save was prepared against

#### Scenario: Save through an address that resolves to no content file is refused

- **WHEN** a save is submitted for an address that does not decode to a path inside the content tree, or that is not the canonical address for the file it resolves to
- **THEN** the system refuses the save and writes nothing, under the same requirement that refuses an ambiguous address — each is a way of failing to identify exactly one file

### Requirement: Browser editing surface

The system SHALL provide an authenticated browser surface that loads a page's Markdown for editing together with the base revision it was loaded at, and submits both back as one explicit save. The surface SHALL open for an address that identifies no existing page, so a page can be created as well as edited; it SHALL NOT open for an address that does not identify exactly one file, which is a different condition from no page existing yet.

The surface SHALL keep every distinct save failure distinguishable to the member — a stale-base conflict, a busy repository, a refused address, a failed save whose working tree was restored, and a failed save whose restore also failed SHALL NOT be reported as one undifferentiated failure, because each calls for a different action. On any failure the surface SHALL re-present the content the member submitted rather than replacing or discarding it.

#### Scenario: Editing surface loads a page with its base revision

- **WHEN** an authenticated user opens the editing surface for an existing page
- **THEN** the system presents that page's current Markdown together with the base revision it was read at, so the save that follows declares what it started from

#### Scenario: Editing surface opens for an address with no page yet

- **WHEN** an authenticated user opens the editing surface for an address that identifies no existing file
- **THEN** the system presents an empty editor declaring that the page did not exist at the base revision, so saving creates it

#### Scenario: Editing surface refuses an address that identifies no single file

- **WHEN** an authenticated user opens the editing surface for an address that is ambiguous, non-canonical, or does not decode to a content path
- **THEN** the system refuses to open an editor for it rather than treating it as a page that does not exist yet

#### Scenario: A rejected save re-presents the member's own content

- **WHEN** a member's save is rejected because the page advanced beyond the base revision they started from
- **THEN** the system re-presents the content that member submitted, reports that the page changed underneath the save and that nothing was written, and does not replace their content with the newer committed content

#### Scenario: A failed save whose rollback also failed is reported distinctly

- **WHEN** a save fails after writing and the system's attempt to restore the working tree also fails
- **THEN** the system reports that outcome distinctly from a failed save that was successfully rolled back, because the working tree may remain dirty and is recovered by startup reconciliation or by an operator rather than by the member retrying

#### Scenario: A save refused for its address is reported distinctly from a stale base

- **WHEN** a member's save is refused because its address does not identify exactly one file
- **THEN** the system reports that distinctly from a stale-base conflict, since retrying the same address cannot succeed while a conflict is resolved by reloading

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
