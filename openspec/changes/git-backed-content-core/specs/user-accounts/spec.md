## ADDED Requirements

### Requirement: Username form

The system SHALL require a chosen username to begin and end with an alphanumeric character, to contain only ASCII letters, digits, dots, hyphens and underscores, and to be at least 3 and at most 64 characters long. The system SHALL report a username that is too short or too long as a length problem and a username of the wrong shape as a shape problem, so that one fault produces one message.

The system SHALL enforce these rules wherever a user chooses a username — the first-administrator bootstrap and invitation redemption — and SHALL enforce them at the service boundary rather than only in form validation.

#### Scenario: Well-formed username is accepted

- **WHEN** a user chooses a username of at least 3 characters that begins and ends with a letter or digit and otherwise uses only letters, digits, dots, hyphens and underscores
- **THEN** the system accepts it

#### Scenario: Username with a leading or trailing separator is refused

- **WHEN** a user chooses a username beginning or ending with a dot, hyphen or underscore
- **THEN** the system refuses it and reports the shape rule, and no account is created

#### Scenario: Username below the minimum length is refused

- **WHEN** a user chooses a username shorter than 3 characters
- **THEN** the system refuses it and reports the length rule, and no account is created

#### Scenario: Existing accounts are unaffected

- **WHEN** an account whose username predates these rules signs in
- **THEN** the system authenticates it normally, because the rules govern only the choosing of a username
