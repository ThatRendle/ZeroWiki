## 1. Identity store

- [x] 1.1 Provision a SQLite identity database file on the mounted volume (accounts, git emails, hashed tokens, invitations), separate from the content git repo
- [x] 1.2 Define the account schema: username (unique), password hash, display name, associated git emails
- [x] 1.3 Define the invitation schema: token, issuer, expiry, redeemed/revoked state
- [x] 1.4 Define the git-token schema: owning account, token hash, created/revoked state

## 2. Password & token hashing

- [x] 2.1 Implement Argon2id password hashing (via a vetted library, tuned parameters); verify against stored hash
- [x] 2.2 Implement high-entropy git token generation; store hashed, return plaintext once
- [x] 2.3 Implement token verification and revocation

## 3. Bootstrap

- [x] 3.1 Detect the empty-store condition (no accounts) on startup
- [x] 3.2 Present a one-time bootstrap flow to create the first administrator account
- [x] 3.3 Make the bootstrap path inert once any account exists

## 4. Invitations

- [x] 4.1 Issue a single-use, expiring invitation as an authenticated member
- [x] 4.2 Redeem an invitation: validate (unredeemed, unexpired, unrevoked), create account with chosen username/password, mark redeemed
- [x] 4.3 Reject expired, already-redeemed, or revoked invitations
- [x] 4.4 Revoke an unused invitation
- [x] 4.5 Ensure there is no open/self-service registration path

## 5. Login & session

- [x] 5.1 Implement username/password login using the framework's auth/session primitives
- [x] 5.2 Reject invalid credentials with a uniform generic error (no username enumeration)
- [x] 5.3 Implement logout that fully invalidates the session

## 6. Anonymous experience & access control

- [x] 6.1 Home page shows only a "Login" link for unauthenticated visitors
- [x] 6.2 Deny anonymous access to content/other pages and redirect to login
- [x] 6.3 Ensure auth pages render as Static SSR (no persistent circuit)

## 7. Git access tokens (account UI)

- [x] 7.1 Account page: generate a git token (shown once), list existing tokens, revoke a token
- [x] 7.2 Manage associated git emails on the account (for email→account mapping)

## 8. Primitives consumed by content-core

- [x] 8.1 Expose credential verification: resolve a username + git token to an account; reject login-password-as-git-credential
- [x] 8.2 Expose account lookup by git email (match / no-match)
- [x] 8.3 Expose the current logged-in identity (for commit authorship in content-core)

## 9. Tests

- [ ] 9.1 Bootstrap: creates first admin only when store empty; inert afterward
- [ ] 9.2 Invitations: single-use, expiry, and revocation all reject correctly; no open registration
- [ ] 9.3 Login: success, uniform failure, logout invalidation
- [ ] 9.4 Git tokens: shown-once, verification success, revocation stops auth, login password rejected for git
- [ ] 9.5 Anonymous: home shows only Login; direct content access denied
