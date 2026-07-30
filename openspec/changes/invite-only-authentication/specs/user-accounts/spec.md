## ADDED Requirements

### Requirement: Account model

The system SHALL represent each user as an account with a unique username, a salted password hash, a display name, and zero or more associated git emails. The system SHALL NOT store passwords in plaintext or with a reversible transformation.

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
