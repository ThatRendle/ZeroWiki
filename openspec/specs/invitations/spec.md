# Invitations Specification

## Purpose

Invite-only account creation for ZeroWiki: there is no open registration. An existing member issues a single-use, expiring invitation; the invitee redeems it to create an account with their own username and password. Unused invitations can be revoked, and expired, redeemed, or revoked invitations never create an account.

## Requirements

### Requirement: Invite-only account creation

The system SHALL create new accounts only by redeeming a valid invitation. The system SHALL NOT offer open/self-service registration.

#### Scenario: No open registration

- **WHEN** an anonymous visitor attempts to create an account without an invitation
- **THEN** the system does not create an account and provides no open registration path

#### Scenario: Account created by redeeming an invitation

- **WHEN** a valid, unredeemed, unexpired invitation is redeemed with a chosen username and password
- **THEN** the system creates the account and marks the invitation as redeemed

### Requirement: Issue invitations

The system SHALL allow an existing member to issue a single-use invitation that expires after a bounded time.

#### Scenario: Member issues an invitation

- **WHEN** an authenticated member issues an invitation
- **THEN** the system produces a single-use invitation with an expiry and a redemption link/token

### Requirement: Invitation validity and revocation

The system SHALL reject redemption of an invitation that is expired, already redeemed, or revoked, and SHALL allow an unused invitation to be revoked before redemption.

#### Scenario: Expired invitation is rejected

- **WHEN** an invitation is redeemed after its expiry
- **THEN** the system rejects it and creates no account

#### Scenario: Already-redeemed invitation cannot be reused

- **WHEN** an invitation that has already created an account is redeemed again
- **THEN** the system rejects it and creates no second account

#### Scenario: Revoked invitation cannot be redeemed

- **WHEN** an invitation is revoked and then a redemption is attempted
- **THEN** the system rejects it and creates no account
