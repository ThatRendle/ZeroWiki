# Request Lifecycle Specification

## Purpose

How in-flight server work responds to a client that has gone away. When a visitor closes the tab, hits
Escape, or loses their connection, work already started on their behalf either abandons or runs to
completion — and which it does is a decision, not an accident of whether a `CancellationToken` happened
to be passed. The split falls on what abandonment leaves behind: reads and creates abandon, because an
abandoned one leaves no observable trace and no user waiting on a result; de-authorisation runs to
completion, because a user who asked to withdraw access cannot tell a failed request from a click that
never registered, and will not know to retry. This capability states that rule once so each service
added to the codebase inherits a decision rather than a coin flip.

## Requirements

### Requirement: Abandoned requests stop doing work

The system SHALL stop in-flight work when the client that requested it has disconnected, for any
operation whose abandonment leaves no observable trace — reads, and writes that create. A cancelled
operation of this kind SHALL leave the store exactly as it was before the request arrived, where the
cancellation is observed before the operation's write commits.

The guarantee is that cancellation stops work, not that it reverses work already committed. Two of the
creates — the first-administrator bootstrap and invitation redemption — are transactional: a
cancellation observed at any point before their commit rolls the write back. The other three — issuing
an invitation, issuing a git access token, and adding a git email — write without a surrounding
transaction and do not re-check cancellation afterwards, so their write commits as it is made and a
cancellation arriving after that point does not remove the record.

#### Scenario: A read is abandoned when the client disconnects

- **WHEN** a client disconnects while a request is reading from the identity store
- **THEN** the read is abandoned rather than run to completion

#### Scenario: A cancelled create leaves nothing behind

- **WHEN** a client disconnects while a request is creating an account, an invitation, a git access
  token, or a git email association, and the disconnection is observed before the write commits
- **THEN** the operation is abandoned and no such record exists afterwards

#### Scenario: A create that has already committed is not undone

- **WHEN** a client disconnects after a request has committed the write that creates an invitation, a
  git access token, or a git email association
- **THEN** the record remains, because cancellation stops further work rather than reversing a
  completed write. This is true of any committed write; these three are named because, having no
  surrounding transaction, they commit as the write is made and so offer no window in which a
  cancellation could still take effect

#### Scenario: A cancelled redemption leaves the invitation usable

- **WHEN** a client disconnects while redeeming an invitation
- **THEN** no account is created and the invitation remains valid and redeemable

### Requirement: De-authorisation completes regardless of the client

The system SHALL run an operation that withdraws access to completion even when the requesting client
has disconnected. Revoking a git access token, revoking an invitation, and removing a git email
association SHALL NOT be abandoned because the request was aborted.

A user who asks to withdraw access has no way to distinguish a request that failed from a click that
never registered, and will not know to retry — so abandoning the operation leaves them believing a
credential is dead while it remains live.

#### Scenario: Revoking a git access token survives a disconnect

- **WHEN** a client disconnects after submitting a request to revoke a git access token
- **THEN** the token is still revoked and no longer authenticates

#### Scenario: Revoking an invitation survives a disconnect

- **WHEN** a client disconnects after submitting a request to revoke an unredeemed invitation
- **THEN** the invitation is still revoked and can no longer be redeemed

#### Scenario: Removing a git email survives a disconnect

- **WHEN** a client disconnects after submitting a request to remove a git email from their account
- **THEN** the email is still removed and no longer resolves to that account

### Requirement: The bootstrap gate cannot fail open under cancellation

The system SHALL NOT treat a cancelled availability check as evidence that the first-administrator
bootstrap is closed or open. A cancellation SHALL surface as a failure rather than as a decision.

#### Scenario: A cancelled bootstrap check does not admit a bootstrap

- **WHEN** the check for whether the first-administrator bootstrap is still available is cancelled
- **THEN** the request fails rather than proceeding as though the store were empty
