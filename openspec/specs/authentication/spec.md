# Authentication Specification

## Purpose

Login, session, and access control for ZeroWiki: username/password login with server-managed sessions and logout, the anonymous "Sign in"-only home experience that keeps content closed to visitors, and per-user revocable git access tokens plus the credential verification the Smart HTTP git remote depends on.

## Requirements

### Requirement: Username/password login

The system SHALL authenticate a user by verifying a submitted username and password against the stored salted password hash, and SHALL establish a server-managed session on success. It SHALL reject invalid credentials without revealing whether the username exists.

#### Scenario: Successful login

- **WHEN** a user submits a username and password that match a stored account
- **THEN** the system establishes an authenticated session for that account

#### Scenario: Invalid credentials rejected uniformly

- **WHEN** a user submits an unknown username, or a known username with the wrong password
- **THEN** the system rejects the login with the same generic error in both cases and establishes no session

### Requirement: Session lifecycle and logout

The system SHALL maintain an authenticated session for a logged-in user and SHALL allow the user to log out, after which the session is no longer authenticated.

#### Scenario: Logout ends the session

- **WHEN** an authenticated user logs out
- **THEN** subsequent requests are treated as unauthenticated

### Requirement: Anonymous home shows only Sign in

The system SHALL present unauthenticated visitors a home page that exposes only a "Sign in" link, and SHALL NOT expose wiki content or navigation to anonymous visitors.

#### Scenario: Anonymous visitor sees Sign in only

- **WHEN** an unauthenticated visitor loads the home page
- **THEN** the page shows a "Sign in" link and no wiki content or navigation

#### Scenario: Anonymous access to content is denied

- **WHEN** an unauthenticated visitor requests a content page directly
- **THEN** the system denies access and directs them to log in

### Requirement: Per-user git access tokens

The system SHALL allow an authenticated user to generate one or more git access tokens, store them hashed at rest, display each token value only once at creation, and allow the user to revoke a token. A revoked token SHALL no longer authenticate.

#### Scenario: Token generated and shown once

- **WHEN** an authenticated user generates a git access token
- **THEN** the system stores the token hashed and displays its plaintext value exactly once

#### Scenario: Revoked token stops working

- **WHEN** a user revokes a git access token
- **THEN** that token no longer authenticates against the system

### Requirement: Credential verification for the git remote

The system SHALL verify a git-remote credential presented as a username plus a git access token, resolve it to the owning account, and reject the request when the token is missing, unknown, or revoked. The login password SHALL NOT be accepted as a git-remote credential.

#### Scenario: Valid username + token resolves to the account

- **WHEN** a git request presents a username and a valid, unrevoked git access token belonging to that account
- **THEN** the system authenticates the request as that account

#### Scenario: Login password rejected for git

- **WHEN** a git request presents a username and the account's login password instead of a git token
- **THEN** the system rejects the credential

#### Scenario: Missing or invalid token rejected

- **WHEN** a git request presents no token, or an unknown or revoked token
- **THEN** the system rejects the request and does not serve repository data
