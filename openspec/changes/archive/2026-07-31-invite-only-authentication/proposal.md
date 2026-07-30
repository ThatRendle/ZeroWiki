## Why

ZeroWiki is a private, invite-only wiki for a small trusted group. Before content can be edited or synced, the system needs individual identity: someone to attribute commits to, credentials to protect the git remote, and a closed door so only invited people get accounts. This change delivers that identity and access layer. It is sequenced **before** `git-backed-content-core`, which depends on it for commit authorship, git-remote credential verification, and the git-email → account mapping.

## What Changes

- Introduce an **account model**: username, salted password hash, display name, and one or more associated **git emails** used to attribute push-originated commits back to the account.
- **Bootstrap** the first administrator account on an empty deployment (no accounts yet), so there is someone who can invite others.
- **Invite-only signup**: there is no open registration. An existing member issues a single-use, expiring invitation; the invitee sets their own username and password to create an account. Unused invitations can be revoked.
- **Username/password login** with server-managed sessions and logout.
- **Unauthenticated experience**: the home page shows only a "Login" link; no content or navigation is exposed to anonymous visitors.
- **Per-user git access tokens**: each account can generate one or more revocable tokens (stored hashed, shown once) used as the Basic-auth password for the git remote. The login password is never used for git and never stored on a laptop.
- Expose the primitives `git-backed-content-core` consumes: verify a credential (session, or username + git token) and resolve it to an account; look up an account by git email.

## Capabilities

### New Capabilities
- `user-accounts`: the account model and identity — username, password hash, display name, associated git emails, bootstrap of the first admin, and account lookup (including git-email → account resolution).
- `invitations`: invite-only account creation — issuing single-use expiring invitations, redeeming an invitation to create an account, and revoking unused invitations.
- `authentication`: login/session and access control — username/password login, session lifecycle and logout, the anonymous "Login"-only home experience, and per-user revocable git access tokens plus credential verification for the git remote.

### Modified Capabilities
<!-- None — no existing specs yet. -->

## Impact

- **Platform**: ASP.NET Core 10 Blazor Web App (Static SSR, per the content-core decision). Login, invite, and account pages render as Static SSR; no persistent circuit required.
- **Storage**: a single **SQLite** database file on the mounted volume holding the account/identity store (accounts, git emails, hashed tokens, invitations), separate from the content git repo. This is the account store `git-backed-content-core` references.
- **Security**: password hashing (e.g. a modern KDF), single-use expiring invitations, hashed-at-rest git tokens shown once, and all credential transport over TLS.
- **Downstream**: unblocks `git-backed-content-core` — supplies logged-in identity for commit authorship, credential verification for the Smart HTTP git remote, and git-email → account mapping.
- **Deferred to later changes**: password reset/email flows, external identity providers (OAuth/OIDC), roles beyond the admin/member distinction needed for invites, and multi-factor authentication.
