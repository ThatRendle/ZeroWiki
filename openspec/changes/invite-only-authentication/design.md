## Context

ZeroWiki is a private, invite-only wiki for a small trusted group, deployed as a Docker container over a mounted volume. This change delivers the identity and access layer that everything else depends on. It is sequenced **before** `git-backed-content-core`, which consumes three things from it: the logged-in identity used to stamp commit authors, credential verification for the Smart HTTP git remote, and git-email → account resolution for push-originated edits.

Platform is ASP.NET Core 10 as a Blazor Web App with Static SSR (per content-core decision D7). Auth surfaces — login, invite redemption, account/token management — are Static SSR pages with form POSTs; none require a persistent circuit. Scale is small (a handful of trusted members).

## Goals / Non-Goals

**Goals:**

- Individual accounts with username/password login and server-managed sessions.
- A closed door: accounts exist only via single-use, expiring invitations; no open registration.
- A bootstrap path for the first admin on an empty deployment.
- Anonymous visitors see only a "Login" link — no content leaks.
- Revocable, hashed-at-rest per-user git access tokens that authenticate the git remote; the login password is never usable for git.
- Expose the credential-verification and git-email→account primitives content-core needs.

**Non-Goals:**

- Password reset / email delivery flows, external IdPs (OAuth/OIDC), MFA — later changes.
- A full roles/permissions model beyond the admin/member distinction needed to issue invitations.
- The git remote itself, commit authorship, and content rendering — those live in `git-backed-content-core`.

## Decisions

### D1 — Invitation-only, single-use, expiring tokens

Account creation requires redeeming a valid invitation. Invitations are single-use, carry an expiry, and can be revoked before redemption; redemption both creates the account and marks the invitation consumed.

*Why:* keeps the wiki closed to exactly the intended people, with no standing open-registration surface to abuse. *Alternative:* admin manually creates accounts and sets passwords — simpler, but forces the admin to transmit an initial password out-of-band; invitations let the invitee set their own secret.

### D2 — First-admin bootstrap, then disabled

When no accounts exist, a one-time bootstrap creates the first administrator; once any account exists the bootstrap path is inert.

*Why:* resolves the chicken-and-egg (invites require an inviter) without leaving a permanent privileged backdoor. *Alternatives:* a seeded default account (must be changed, easily forgotten — risky) or an env-var-provisioned admin (viable; bootstrap page chosen for a clearer first-run UX, env provisioning can be added later).

### D3 — Per-user git access tokens, not password-over-Basic

The git remote is authenticated with a username + a git access token (used as the Basic password). Tokens are generated per account, stored hashed, shown once, and independently revocable. The login password is explicitly rejected for git.

*Why:* the token lives in the laptop credential store, not the login password; a leaked or rotated token is revoked without disrupting login, and git access is scoped and auditable. *Alternative considered and rejected:* reuse the login password over Basic — simplest, but puts the primary secret on every laptop and couples revocation to a password change (which also breaks login).

### D4 — Framework auth primitives; hashing

Use the framework's session primitive (cookie authentication) rather than hand-rolling, and **not** the full ASP.NET Core Identity UI stack — its deferred surface (email confirmation, 2FA, external logins, role UI, scaffolded pages) is dead weight for a ~10-user invite-only wiki. Passwords are hashed with **Argon2id** via a vetted library (e.g. `Konscious.Security.Cryptography` or a libsodium binding) with tuned memory/iteration/parallelism parameters. Git tokens are high-entropy random values stored as hashes; the plaintext is shown once at creation and never recoverable.

*Why:* password hashing and session handling are exactly what you must not reinvent — but here the two primitives come from different places. Sessions use the framework's cookie auth. Passwords do **not** use the framework's `PasswordHasher<T>`, because that implements PBKDF2; Argon2id (memory-hard) is the stronger modern choice, so it comes from a dedicated Argon2 library instead. Full Identity brings machinery this change excludes, so we stop at these primitives over our own minimal schema. "Shown once" removes any reason to store token plaintext. *Alternatives considered and rejected:* framework `PasswordHasher<T>` / PBKDF2 — fine, but not memory-hard; full ASP.NET Core Identity — batteries we don't need and a prescribed schema our custom entities would sit awkwardly beside.

### D5 — Uniform auth failures; anonymous sees only Login

Login rejects unknown-username and wrong-password with the same generic error. Anonymous visitors get a home page with only a "Login" link, and direct requests to content are denied and redirected to login.

*Why:* avoids username enumeration and avoids leaking the existence or structure of content to the public.

### D6 — Identity store: SQLite on the volume

Accounts, associated git emails, hashed tokens, and invitations live in a single **SQLite** database file on the mounted volume, separate from the content git repo — identity is not versioned content. It is server-less and self-contained (no external database), consistent with ZeroWiki's zero-config, single-volume deployment.

*Why:* the dataset is tiny (dozens of rows) so query power and performance are not factors; the deciding factor is that SQLite gives ACID transactions and its own concurrency handling for free, so invite-redemption and token-revocation flows cannot half-apply and no bespoke file lock / atomic-write code is needed. Keeping it separate from the synced content repo keeps secrets (even hashed) out of the git history that reaches laptops. *Alternative considered and rejected:* a plain JSON/structured file on the volume — maximally dependency-free and human-inspectable (very on-ethos), but it puts write-locking and atomic-write correctness on us; SQLite removes that concern entirely for a negligible dependency. *Auth machinery is a separate axis (see D4):* the store choice does not imply full ASP.NET Core Identity — we use the framework's crypto/session primitives over this SQLite store, not the full Identity UI stack.

## Risks / Trade-offs

- **Lost/again-needed bootstrap** → Bootstrap is inert once populated; recovery for a locked-out sole admin is a documented operational procedure (e.g. re-run against a store with no accounts), not an in-app backdoor.
- **Token sprawl / stale tokens** → Tokens are individually listable and revocable per account; shown-once storage means a leaked store yields only hashes.
- **Self-asserted git author identity on push** → Accepted for a trusted group (consistent with content-core D5); the git-email→account mapping attributes correctly and unknown emails fall back to the raw identity without failing the push.
- **Secrets on the mounted volume** → The SQLite store is separate from the synced content repo so credentials never enter git history; rely on host/volume protection and TLS in transit.
- **Username enumeration via invitation redemption** → **Accepted risk, weighed and accepted by the Product Owner (2026-07-27).** Redemption tells the invitee when their chosen username is already taken, so a holder of a live, unredeemed invitation can resubmit with different names and learn which usernames exist. This is the enumeration that D5's uniform login failure closes, reached from a direction D5 does not cover. Accepted because the two reasons are specific to this surface: the prober must **possess a live invitation** — they are someone the system is actively granting membership to, not an anonymous stranger, which is what the login form's oracle did not require — and user-chosen unique usernames cannot be offered at all without telling the user their choice is taken, so every alternative leaves a genuine invitee unable to get in and unable to learn why. **Neither reason generalises**: this is not precedent for naming a reason on any other surface. Bounded deliberately — the invitation is *not* consumed by a name clash (that would punish the invitee for a collision they could not predict), and the reason is reachable only after the presented token has matched a stored hash. Closing it later means trading a legitimate invitee's ability to complete signup; the code records the trade at `InvitationRedemption.UsernameTaken`.
- **Session handling correctness** → Use the framework's authentication/session primitives rather than a bespoke scheme; logout must fully invalidate the session.

## Migration Plan

Greenfield; no data to migrate. First deployment starts with an empty identity store and presents the one-time bootstrap to create the initial admin. This change must ship and be deployable before `git-backed-content-core` is enabled, since the git remote depends on token verification. Rollback is redeploying the prior image; the identity store on the volume is untouched.

## Open Questions

- ~~KDF choice~~ **Resolved**: Argon2id via a vetted library (see D4). Remaining detail: tune memory/iteration/parallelism parameters at implementation.
- ~~Identity store technology~~ **Resolved**: SQLite (single file on the volume) — see D6.
- Invitation delivery — copy-a-link handoff vs sending email — email is out of scope for now, so default to a copyable redemption link.
- Whether the admin/member distinction needs any persisted role beyond "can issue invitations" for this change.
