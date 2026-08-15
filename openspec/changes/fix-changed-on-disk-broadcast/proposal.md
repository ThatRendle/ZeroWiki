## Why

`git-sync`'s **Re-index and broadcast on received push** requirement is not met by the shipped code: a
push over Smart HTTP updates the working tree, but no connected viewer is ever told. Reproduced
first-hand against the running app — push exit 0, live circuit, no banner — and independently by the
Product Owner during `git-backed-content-core` §9's Obsidian verification.

The defect survived §8's review because the one link in the chain has no test:
`GitSmartHttpEndpoints.HandleReceivePackAsync` — which brackets the push with two `HEAD` probes and
fires the reaction — has no covering tests, and neither does its call to `PushReactionService.ReactAsync`.
Both halves either side of it are tested directly, so a green suite proved the parts and never the
wiring.

It also cannot be *seen* to fail. `PushReactionService` and `PageChangeNotifier` log only on their
failure paths, so a zero-route diff, a zero-subscriber match, and a correct delivery are
indistinguishable in the app's output. The absence of warnings was read as evidence the reaction ran; it
was not.

## What Changes

- **Fix the broadcast** so a push that touches a page notifies open viewers of that page. Root cause is
  not yet isolated — the client half is proven healthy (the `InteractiveServer` boundary is present in
  the server-rendered HTML and the circuit is started), so the break is server-side, between
  `HandleReceivePackAsync` and `PageChangeNotifier`.
- **Cover the wiring**, not just the halves: a test that drives a real push through the Smart HTTP
  endpoint and asserts a subscriber registered under the pushed page's route is invoked. This test must
  fail against the current code before it passes — a fix whose test cannot fail is what produced this
  situation.
- **Make the reaction observable**: log the outcome of each reaction — routes diffed, subscribers
  matched, callbacks invoked — so a silent no-op is distinguishable from a delivery in the app's own
  output.
- **Confirm the fix in a live browser circuit**, not only under test. The banner's rendering is the one
  claim in `git-backed-content-core` no automated test ever reached, and it is exactly where the defect
  hid.

## Capabilities

### New Capabilities

None. The behaviour is already specified; the implementation does not satisfy it.

### Modified Capabilities

- `git-sync`: **Re-index and broadcast on received push** — unchanged in intent, extended with a
  scenario requiring the reaction's outcome to be observable, so that a broadcast which reaches nobody
  is detectable rather than silent.

## Impact

- `src/ZeroWiki/Web/GitSmartHttpEndpoints.cs` — `HandleReceivePackAsync`, the untested wiring and prime
  suspect.
- `src/ZeroWiki/Content/PushReactionService.cs`, `src/ZeroWiki/Content/PageChangeNotifier.cs` —
  success-path observability.
- `src/ZeroWiki/Components/Pages/ChangedOnDiskIndicator.razor` — only if the subscription side proves at
  fault; currently exonerated by first-hand evidence, not by assumption.
- Tests: new coverage for the endpoint-to-notifier path.

**Sequencing constraint.** This change amends code and a capability that exist **only on
`change/git-backed-content-core`** — `git-sync` has not been promoted to `openspec/specs/`, and `main`
has neither the endpoint nor the notifier. It cannot be applied before `git-backed-content-core`
archives. The same constraint already applies to `fix-reconciliation-index-blindness`; these two are
queued behind the same event.
