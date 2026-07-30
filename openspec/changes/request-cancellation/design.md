## Context

The identity services were built taking `CancellationToken` on every public method — correctly — but
no page ever passed one. The plumbing job is trivial and mechanical. The design work is deciding
**where the plumbing must stop**, because "flow it everywhere" is wrong in one specific place, and
wrong in the direction that costs a user something.

## Goals / Non-Goals

**Goals**

- Stop doing work for a client that has gone away, on every path where abandoning is safe.
- Write the rule down once, so the next service inherits a decision instead of guessing.

**Non-Goals**

- Timeouts or deadlines. This change concerns exactly one signal: the client disconnected.
- Cancellation in `git-backed-content-core`'s remote or write path — out of scope, but that change
  should adopt D1 rather than invent its own split.
- Any change to service signatures. They already take the token.

## Decisions

### D1 — Flow cancellation into reads and creates; **never** into de-authorisation

This is the whole design, and the reason this change is not a five-minute find-and-replace.

**Reads** (`IsAvailableAsync`, `ValidateAsync`, the three `ListAsync`) — flow the token. Nobody is
waiting for the answer. There is no state to leave half-finished. This case is unambiguous.

**Creates** (`CreateFirstAdministratorAsync`, `RedeemAsync`, both `IssueAsync`, `AddAsync`) — flow the
token. Every one is transactional, so cancelling rolls back and *nothing happens*: the invitation stays
valid, the bootstrap stays open, no token is issued. The user reconnects and retries. Rollback is the
fail-safe direction for a create.

It is actively better than the alternative for `GitTokenService.IssueAsync`, which is shown-once: a
token committed to the database while the client is gone is a credential the owner can never see and
never use, sitting in their account looking valid. Cancelling before commit is precisely the outcome
you want.

**De-authorisation** (`GitTokenService.RevokeAsync`, `InvitationService.RevokeAsync`,
`GitEmailService.RemoveAsync`) — **do not flow the request's token.** The fail-safe direction inverts
here, and this is the finding that shapes the change.

A user clicks *revoke* on a git token they believe is compromised. Their connection drops between the
POST arriving and the write committing. With the request's token flowed in, the revocation is abandoned
and rolled back — and the user is left believing they revoked a live credential. They have no reason to
check: from their side the request simply failed, which is indistinguishable from the click not
registering. The token stays valid until somebody notices.

That is a security regression introduced by a hygiene change, which is the worst way to acquire one.
The same argument covers invitation revocation (an invite the issuer believes is dead stays redeemable)
and git-email removal (an address the member believes is disassociated keeps attributing their commits).

*Why not just "writes don't cancel"?* Because that gives up the `IssueAsync` benefit above and adds
nothing: creates are safe to abandon precisely because abandoning them leaves no trace. The line is not
read-vs-write, it is **whether the fail-safe direction is to stop or to finish** — and that is a
property of what the operation means, not of whether it writes.

*Alternative considered and rejected:* flow the token everywhere and rely on the user retrying. Rejected
because it depends on the user knowing there is something to retry, and in the revoke case they
specifically do not.

### D2 — De-authorisation uses `CancellationToken.None`, explicitly and with a comment

Rather than omitting the argument and inheriting the default parameter. An omitted argument is
indistinguishable from an oversight — it is exactly what every one of the 15 call sites looked like
before this change. `CancellationToken.None` written out, with one line saying why, is a decision a
reader can see, and one that a future "let's make these consistent" pass has to argue with rather than
silently tidy away.

### D3 — The rule lives with the services, not only in this DEVLOG

A design decision recorded only in a change's DEVLOG is invisible to whoever adds the seventh service.
The rule gets a short XML doc remark at the point a reader will be standing when the question comes up.

## Risks / Trade-offs

- **A de-authorisation now outlives its request.** Deliberate, per D1, and bounded: these are single
  short `UPDATE`/`DELETE` statements under an existing lock, not long-running work. There is no
  unbounded task being spawned.
- **The split has to be learned.** Mitigated by D2 and D3 — the code states it where the question
  arises, so it cannot be inferred wrongly from a glance at neighbouring calls.
- **`IsAvailableAsync` on the bootstrap gate is a read that guards a write.** Flowing cancellation into
  it is still safe: a cancelled check throws rather than returning `false`, so it cannot fail *open* and
  re-admit a bootstrap that should be closed. Worth asserting rather than assuming — see tasks.

## Migration Plan

None. No schema, no data, no configuration. The change is deployable and revertible as a single commit.

## Open Questions

None. D1's split is the one call worth making and it is made above; if the Product Owner prefers
uniform cancellation everywhere, that reverses D1 and is a proposal-level decision rather than an
implementation detail.
