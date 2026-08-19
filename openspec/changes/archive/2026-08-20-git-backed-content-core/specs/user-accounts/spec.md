## ADDED Requirements

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
