## 1. The rule

- [ ] 1.1 Record D1's split (reads and creates cancel; de-authorisation does not) as a short XML doc remark where a reader adding a new service will meet the question, per D3
- [ ] 1.2 Confirm every identity service method already accepts a `CancellationToken`, so no service signature changes in this change

## 2. Flow cancellation into reads and creates

- [ ] 2.1 `Bootstrap.razor` — pass `RequestAborted` to `IsAvailableAsync` and `CreateFirstAdministratorAsync`
- [ ] 2.2 `BootstrapComplete.razor` — pass `RequestAborted` to `IsAvailableAsync`
- [ ] 2.3 `Login.razor` — pass `RequestAborted` to `VerifyCredentialsAsync` (the 64 MiB Argon2id verify is the single most expensive thing this application does for a client that may already be gone)
- [ ] 2.4 `RedeemInvitation.razor` — pass `RequestAborted` to `ValidateAsync` and `RedeemAsync`
- [ ] 2.5 `Invitations.razor` — pass `RequestAborted` to `IssueAsync` and `ListAsync`
- [ ] 2.6 `Account.razor` — pass `RequestAborted` to both `ListAsync` calls, `IssueAsync`, and `AddAsync`

## 3. Hold the line at de-authorisation

- [ ] 3.1 `Account.razor` — `GitTokenService.RevokeAsync` and `GitEmailService.RemoveAsync` take `CancellationToken.None`, written explicitly with the D2 comment saying why
- [ ] 3.2 `Invitations.razor` — `InvitationService.RevokeAsync` takes `CancellationToken.None`, same treatment
- [ ] 3.3 Verify no de-authorisation path anywhere else in `src` reaches a service while carrying a request-scoped token

## 4. Tests

- [ ] 4.1 A cancelled create leaves no record — assert against the store, not the return value
- [ ] 4.2 A cancelled redemption leaves the invitation still redeemable
- [ ] 4.3 Revocation completes under an already-cancelled token — the central assertion of this change, one per de-authorisation path (git token, invitation, git email)
- [ ] 4.4 A cancelled bootstrap availability check fails rather than reporting the store empty (design.md's Risks item — assert it, don't assume it)
- [ ] 4.5 Sweep: no page passes a request-scoped token to a de-authorisation call, and no page omits one from a read or create
