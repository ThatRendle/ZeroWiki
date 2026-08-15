## 1. Isolate the cause

- [ ] 1.1 Instrument the live push path and determine which link fails: whether `ReactAsync` runs at all, whether the diff names routes, and whether any subscription is registered under the pushed route
- [ ] 1.2 Record the diagnosis in the DEVLOG with the instrument used and its output, and state explicitly whether it confirms or contradicts the reproduction's finding that the client half is healthy

## 2. The falsifier

- [ ] 2.1 Add a test that registers a subscriber under a page's route, pushes a commit changing that page with the real `git` binary through the Smart HTTP receive-pack endpoint, and asserts the subscriber is invoked — pinning `-c credential.helper=` on client-side git invocations
- [ ] 2.2 Add a test that a push advancing `HEAD` while changing no page file invokes no subscriber and does not fail
- [ ] 2.3 Demonstrate 2.1 failing against the unfixed code and record the output in the DEVLOG — a fix whose test has never been seen to fail is not evidence

## 3. Fix

- [ ] 3.1 Fix the diagnosed cause, explained in the DEVLOG in terms of that diagnosis rather than the green run that follows it
- [ ] 3.2 Confirm 2.1 and 2.2 pass, and that no existing test in the push-reaction or Smart HTTP suites was weakened, narrowed, or removed to make them pass

## 4. Observability

- [ ] 4.1 Log each push reaction's outcome at `Information` — routes named by the diff and subscribers invoked — so a reaction that reaches nobody is distinguishable both from a delivery and from a reaction that named no routes
- [ ] 4.2 Test that a reaction naming no routes and a reaction matching no subscriber each produce their own distinguishable record

## 5. Live confirmation

- [ ] 5.1 Confirm in a real browser circuit that a push from a git client makes the "changed on disk" banner appear without a reload — Product Owner verification, not automated, and not ticked without their report
