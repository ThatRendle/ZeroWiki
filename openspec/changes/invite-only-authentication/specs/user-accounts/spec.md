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

### Requirement: Account lookup by git email

The system SHALL resolve a git email to the account it is associated with, and SHALL report no match when the email is not associated with any account.

#### Scenario: Known git email resolves to its account

- **WHEN** a git email associated with an account is looked up
- **THEN** the system returns that account

#### Scenario: Unknown git email returns no match

- **WHEN** a git email not associated with any account is looked up
- **THEN** the system returns no match rather than an error
