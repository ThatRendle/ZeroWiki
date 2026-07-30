## Why

Every service in the identity layer accepts a `CancellationToken`, and every page calls those services
without passing one. `HttpContext.RequestAborted` is already on the cascading parameter each page
holds. The result is that when a visitor closes the tab, hits Escape, or loses their connection, the
server carries on: an Argon2id verify at 64 MiB continues to completion for a response nobody will
read, and a query keeps a SQLite connection busy for a request that no longer exists.

This was found by `invite-only-authentication`'s §7 review (as N6), confirmed by its §9 sweep — one
`RequestAborted` reference exists in the whole of `src`, in `AnonymousLandingPage` — and judged by
worker, reviewer and supervisor alike as worth doing, low priority, and belonging to its own change
rather than being smuggled into a test block. This is that change.

It is deliberately small: no new features, no schema change, no new dependency, and no change to any
behaviour a *connected* visitor can observe.

## What Changes

- **Flow `HttpContext.RequestAborted` from every page into the service calls it makes** — 15 call sites
  across six pages (`Account`, `Invitations`, `Login`, `Bootstrap`, `BootstrapComplete`,
  `RedeemInvitation`).
- **Except for de-authorisation.** Revoking a git token, revoking an invitation, and removing a git
  email are operations whose fail-safe direction is *completing*, not abandoning — see D1. Those keep
  running on a token that is not tied to the client's connection, and the reason is written down at
  each call site so a future reader does not "finish the job" by making them uniform.
- **State the rule once, in one place**, so the next service added to this codebase inherits a decision
  rather than a coin flip.

Nothing else moves. Service signatures already take the token; none needs to change.

## Capabilities

### New Capabilities
- `request-lifecycle`: how in-flight server work responds to a client that has gone away — which
  operations abandon when the request is aborted, which run to completion regardless, and why the
  split falls where it does.

### Modified Capabilities
<!-- None. The affected pages and services belong to capabilities delivered by
     `invite-only-authentication`, which is complete but not yet archived; this change alters no
     requirement any of them states. -->

## Impact

- **Affected code**: the six Razor pages listed above. No service, entity, migration or configuration
  changes.
- **Risk**: low, and one-directional. The read and create paths gain the ability to stop early for a
  client that is gone; the de-authorisation paths are explicitly unchanged. There is no path where a
  connected visitor sees different behaviour.
- **Not in scope**: cancellation in the git Smart HTTP remote or the content write path — those live in
  `git-backed-content-core` and should adopt the rule this change writes down rather than inventing
  their own. Timeouts, request deadlines, and any form of server-side cancellation not originating
  from the client disconnecting.
