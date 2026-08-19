# User Accounts Specification

## Purpose

The account model and identity layer for ZeroWiki: username, salted password hash, display name, and the associated git emails used to attribute push-originated commits back to an account. Covers bootstrap of the first administrator on an empty deployment, member-owned git email management, and git-email → account resolution.

## Requirements

### Requirement: Account model

The system SHALL represent each user as an account with a unique username, a salted password hash, a display name, and zero or more associated git emails. The system SHALL NOT store passwords in plaintext or with a reversible transformation. The system SHALL compare git emails case-insensitively wherever they are matched — for uniqueness and for lookup alike — so that addresses differing only in case denote the same identity.

#### Scenario: Account has required identity fields

- **WHEN** an account is created
- **THEN** it has a unique username, a salted password hash, and a display name

#### Scenario: Duplicate username is rejected

- **WHEN** account creation is attempted with a username that already exists
- **THEN** the system rejects it and does not create a second account with that username

### Requirement: First-administrator bootstrap

The system SHALL provide a way to create the first administrator account when no accounts exist, so that the initial member can invite others. Once any account exists, the bootstrap path SHALL NOT create additional accounts.

#### Scenario: Bootstrap on empty deployment

- **WHEN** the system starts with no accounts and the bootstrap step is completed
- **THEN** exactly one administrator account exists

#### Scenario: Bootstrap disabled once populated

- **WHEN** at least one account already exists
- **THEN** the bootstrap path does not create a new account

### Requirement: Git email management

The system SHALL allow an authenticated member to add, list, and remove the git emails associated with their own account, and SHALL NOT allow a member to add to or remove from the git emails of any other account. A git email SHALL be associated with at most one account. When a member adds a git email already associated with another account, the system SHALL refuse it and SHALL report that the address is already associated with another account, without identifying which account.

#### Scenario: Member adds a git email to their own account

- **WHEN** an authenticated member adds a git email not associated with any account
- **THEN** the email becomes associated with that member's account and appears in their list of git emails

#### Scenario: Git email already associated with another account is refused

- **WHEN** an authenticated member adds a git email already associated with a different account
- **THEN** the system refuses it, reports that the address is already associated with another account, and does not identify that account

#### Scenario: Member removes a git email from their own account

- **WHEN** an authenticated member removes a git email associated with their own account
- **THEN** the email is no longer associated with that account, including when it was the only one

#### Scenario: Git email differing only in case is treated as the same address

- **WHEN** an authenticated member adds a git email that differs only in letter case from one already associated with another account
- **THEN** the system refuses it on the same terms as an exact match, and does not associate a second copy of the address

#### Scenario: Member cannot modify another account's git emails

- **WHEN** an authenticated member attempts to add to or remove from the git emails of an account that is not their own
- **THEN** the system does not modify that account's git emails

### Requirement: Account lookup by git email

The system SHALL resolve a git email to the account it is associated with, and SHALL report no match when the email is not associated with any account.

#### Scenario: Known git email resolves to its account

- **WHEN** a git email associated with an account is looked up
- **THEN** the system returns that account

#### Scenario: Unknown git email returns no match

- **WHEN** a git email not associated with any account is looked up
- **THEN** the system returns no match rather than an error

#### Scenario: Git email resolves regardless of the case it was stored in

- **WHEN** a git email is looked up that differs only in letter case from the address associated with an account
- **THEN** the system returns that account, rather than reporting no match

### Requirement: Username form

The system SHALL require a chosen username to begin and end with an alphanumeric character, to contain only ASCII letters, digits, dots, hyphens and underscores, to contain no two consecutive dots, and to be at least 3 and at most 64 characters long. The system SHALL report a username that is too short or too long as a length problem and a username of the wrong shape as a shape problem, so that one fault produces one message. Where a username breaks both a length rule and the shape rule, the system SHALL evaluate length first, so that any surface reporting a single fault reports the length rule.

The system SHALL enforce these rules wherever a user chooses a username — the first-administrator bootstrap and invitation redemption — and SHALL enforce them at the service boundary rather than only in form validation.

#### Scenario: Well-formed username is accepted

- **WHEN** a user chooses a username of at least 3 and at most 64 characters that begins and ends with a letter or digit, contains no two consecutive dots, and otherwise uses only letters, digits, dots, hyphens and underscores
- **THEN** the system accepts it

#### Scenario: Username with a leading or trailing separator is refused

- **WHEN** a user chooses a username beginning or ending with a dot, hyphen or underscore
- **THEN** the system refuses it and reports the shape rule, and no account is created

#### Scenario: Username containing consecutive dots is refused

- **WHEN** a user chooses a username containing two or more dots in a row
- **THEN** the system refuses it and reports the shape rule, and no account is created

#### Scenario: Username below the minimum length is refused

- **WHEN** a user chooses a username shorter than 3 characters
- **THEN** the system refuses it and reports the length rule, and no account is created

#### Scenario: A username that is both too short and wrongly shaped reports the length rule

- **WHEN** a user chooses a username that is both shorter than 3 characters and does not begin and end with an alphanumeric character
- **THEN** the system refuses it, and where it reports a single fault it reports the length rule

#### Scenario: Existing accounts are unaffected

- **WHEN** an account whose username predates these rules signs in
- **THEN** the system authenticates it normally, because the rules govern only the choosing of a username
