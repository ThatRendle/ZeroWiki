## 1. The rule

- [x] 1.1 Record D1's split (reads and creates cancel; de-authorisation does not) as a short XML doc remark where a reader adding a new service will meet the question, per D3
- [x] 1.2 Confirm every identity service method already accepts a `CancellationToken`, so no service signature changes in this change

## 2. Flow cancellation into reads and creates

- [x] 2.1 `Bootstrap.razor` — pass `RequestAborted` to `IsAvailableAsync` and `CreateFirstAdministratorAsync`
- [x] 2.2 `BootstrapComplete.razor` — pass `RequestAborted` to `IsAvailableAsync`
- [x] 2.3 `Login.razor` — pass `RequestAborted` to `VerifyCredentialsAsync` (the 64 MiB Argon2id verify is the single most expensive thing this application does for a client that may already be gone)
- [x] 2.4 `RedeemInvitation.razor` — pass `RequestAborted` to `ValidateAsync` and `RedeemAsync`
- [x] 2.5 `Invitations.razor` — pass `RequestAborted` to `IssueAsync` and `ListAsync`
- [x] 2.6 `Account.razor` — pass `RequestAborted` to both `ListAsync` calls, `IssueAsync`, and `AddAsync`

## 3. Hold the line at de-authorisation

- [x] 3.1 `Account.razor` — `GitTokenService.RevokeAsync` and `GitEmailService.RemoveAsync` take `CancellationToken.None`, written explicitly with the D2 comment saying why
- [x] 3.2 `Invitations.razor` — `InvitationService.RevokeAsync` takes `CancellationToken.None`, same treatment
- [x] 3.3 Verify no de-authorisation path anywhere else in `src` reaches a service while carrying a request-scoped token

## 4. Tests

- [x] 4.1 A cancelled create leaves no record — assert against the store, not the return value
- [x] 4.2 A cancelled redemption leaves the invitation still redeemable
- [ ] 4.3 Each de-authorisation service honours its own token — assert `RevokeAsync`/`RemoveAsync` throw under an already-cancelled token, one per path (git token, invitation, git email). This is **half** of this change's central assertion, and it is what makes 4.5 load-bearing: it proves the parameter is live, so a sweep showing every caller passes `CancellationToken.None` means something rather than nothing. The other half is 4.5. The property "revocation survives a disconnect" lives in the **caller** (D1) and cannot be asserted at the service level, because there the service correctly cancels — asserting it there would assert the opposite of the requirement
- [x] 4.4 A cancelled bootstrap availability check fails rather than reporting the store empty (design.md's Risks item — assert it, don't assume it)
- [ ] 4.5 Sweep: no page passes a request-scoped token to a de-authorisation call, and no page omits one from a read or create. The other half of 4.3, and the **only** mechanical evidence §3's work exists — §3 changed no runtime behaviour, since the omitted argument it replaced already bound to `CancellationToken.None` via the services' `= default`
