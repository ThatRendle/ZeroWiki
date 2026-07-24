## ADDED Requirements

### Requirement: Smart HTTP git remote behind wiki auth

The system SHALL expose the content repository as a Smart HTTP git remote (`info/refs`, `git-upload-pack`, `git-receive-pack`) that requires wiki authentication, so a standard git client can clone, fetch, and push using wiki credentials over HTTPS without SSH.

#### Scenario: Authenticated clone succeeds

- **WHEN** a git client requests the remote with valid wiki credentials
- **THEN** the system serves the Smart HTTP protocol and the client can clone the repository

#### Scenario: Unauthenticated access is refused

- **WHEN** a git client requests the remote without valid wiki credentials
- **THEN** the system refuses the request and does not serve repository data

#### Scenario: Authenticated push succeeds

- **WHEN** an authenticated git client pushes commits to the remote
- **THEN** the system accepts the push subject to the working-tree and fast-forward rules below

### Requirement: Accept pushes into the checked-out branch

The system SHALL accept pushes to the branch that is checked out in the working tree (via `receive.denyCurrentBranch = updateInstead`) so that a push updates the files the app renders, provided the working tree is clean.

#### Scenario: Fast-forward push updates the working tree

- **WHEN** a fast-forward push arrives and the working tree is clean
- **THEN** the system updates both the branch and the working tree to the pushed commit

#### Scenario: Non-fast-forward push is rejected

- **WHEN** a push would not fast-forward (the remote and pusher both advanced the same branch)
- **THEN** the system rejects the push so the client resolves the conflict locally (for example, pull and merge in Obsidian) before retrying

### Requirement: Re-index and broadcast on received push

The system SHALL, after a push updates the working tree, re-index the changed files and broadcast a "changed on disk" signal to connected viewers of affected pages.

#### Scenario: Viewers notified after a push

- **WHEN** a push updates one or more pages in the working tree
- **THEN** the system re-indexes the changed files and signals open viewers of those pages that the content changed on disk

### Requirement: Obsidian vault sync compatibility

The system SHALL be compatible as a git remote for the `obsidian-git` community plugin (Smart HTTP with basic authentication) so a laptop vault can sync without a bespoke plugin.

#### Scenario: Obsidian vault pushes and pulls

- **WHEN** a laptop configures its Obsidian vault repository to use the ZeroWiki remote with wiki credentials
- **THEN** the vault can pull remote changes and push local changes using `obsidian-git`

### Requirement: Git identity to account mapping

The system SHALL map the git author identity (email) of incoming commits to a ZeroWiki account so that push-originated edits are attributed to the correct user.

#### Scenario: Known git email maps to an account

- **WHEN** a pushed commit carries a git author email associated with a ZeroWiki account
- **THEN** the system attributes that edit to that account

#### Scenario: Unknown git email is attributed to the raw identity

- **WHEN** a pushed commit carries a git author email not associated with any account
- **THEN** the system attributes the edit to the raw git identity without failing the push
