## Context

`git-sync`'s **Re-index and broadcast on received push** requirement is implemented across three pieces
that are individually tested and collectively broken:

- `GitSmartHttpEndpoints.HandleReceivePackAsync` brackets the push with two `HEAD` probes and, when the
  shas differ, fires `PushReactionService.ReactAsync` off-request via `Task.Run`.
- `PushReactionService` warms the index, diffs `before..after` scoped to the working tree, maps changed
  paths to routes, and calls `PageChangeNotifier.NotifyChangedAsync`.
- `ChangedOnDiskIndicator`, an `InteractiveServer` island mounted by `WikiPage.razor`, subscribes under
  its own route and renders the banner.

Observed against the running app: a push over Smart HTTP lands (`exit 0`), the working tree updates, and
no viewer is notified. `PushReactionServiceTests` and `PageChangeNotifierTests` cover the second and
third pieces directly; `HandleReceivePackAsync` and its call to `ReactAsync` have **no covering tests**.

**What the reproduction already establishes**, so the fix does not re-litigate it: the client half is
healthy. The server-rendered HTML carries the `{"type":"server","prerenderId":…}` boundary, so the island
is mounted and prerendered; `blazor.web.js` loads and `window.Blazor._internal` is present, so the
circuit is started. The mount guard (`WikiPage.razor:30-37`) is satisfied — confirmed independently by
the last-edit block, which reads the same `_page` object.

**What it does not establish, and cannot from outside the process:** whether the subscription exists,
whether `ReactAsync` ran, and whether the diff produced routes. All three fail silently, because both
services log only on their failure paths.

## Goals / Non-Goals

**Goals:**

- A push through the Smart HTTP endpoint notifies viewers of the pages it changed.
- The seam between endpoint and notifier is covered by a test that drives the endpoint.
- A reaction that reaches nobody is visible in ordinary application output.

**Non-Goals:**

- Redesigning the notifier, the render-mode split, or the off-request reaction. D7, D16 and D19 stand;
  this change repairs an implementation against them, it does not revisit them.
- Broadcasting for app-side saves. `PageSaveService`'s own path is out of scope.
- Any client-side change, unless diagnosis contradicts the evidence above — in which case that
  contradiction is itself reported before any component is touched.

## Decisions

**D1 — Diagnose before fixing; the first deliverable is a failing test, not a patch.** The root cause is
not isolated. The plausible causes (the reaction never firing, the diff naming nothing, the route sets
not matching, the subscription never registering) are indistinguishable from outside the process and
have different fixes. Writing a speculative patch risks the outcome this change exists to correct: code
that looks right and is never falsified. *Alternative considered:* patch the most likely suspect
(`HandleReceivePackAsync`'s sha bracketing) and see if the banner appears. Rejected — "the banner
appeared once" is exactly the grade of evidence that let the defect ship.

**D2 — The falsifier drives a real `git` push through the HTTP endpoint.** The covering test registers a
subscriber under a page's route, pushes a commit changing that page with the real `git` binary against
the Smart HTTP endpoint, and asserts the subscriber was invoked. It reuses the real-client harness
established in `GitSmartHttpRealClientTests`. **The test MUST be demonstrated to fail against the
unfixed code before the fix lands**, and that demonstration recorded — a test written after a fix, that
has never been seen to fail, is not evidence. Client-side git invocations pin `-c credential.helper=`
(the suite otherwise reads and writes the developer's OS keychain). *Alternative considered:* call
`HandleReceivePackAsync` with a fake backend. Rejected — the untested thing is the wiring to real
`git http-backend`, and a fake reintroduces exactly the part-wise coverage that missed this.

**D3 — Observability at a level that is on by default.** The reaction logs its outcome — routes named,
subscribers invoked — at `Information`, not `Debug`. `Debug` is off in ordinary configuration, which is
precisely why the failure presented as "no warnings in the app output" and was read as success.
*Alternative considered:* `Debug`, keeping normal output quiet. Rejected — a signal only visible to
someone who already suspects the fault is not observability. Volume is bounded by pushes, not requests.

**D4 — The live browser confirmation stays a Product Owner step and no test claims it.** The banner
rendering in a real circuit is the one assertion no test in this repo reaches, and the place this defect
hid. The change is not done when the suite is green; it is done when the banner has been seen.

## Risks / Trade-offs

- **The fix passes the new test for the wrong reason** → the test must first be observed failing (D2),
  and the fix must be explained in terms of the diagnosed cause, not merely correlated with a green run.
- **Diagnosis contradicts the client-half evidence above** → the two corrected measurements are recorded
  in `git-backed-content-core`'s DEVLOG along with the two wrong ones that preceded them. Re-derive
  rather than trust; if the client half is implicated after all, say so explicitly and revise this
  design before touching the component.
- **`Information`-level logging per push is noise in a busy wiki** → bounded by push frequency, and one
  structured line per reaction. Accepted deliberately: the alternative already cost a section.
- **This change cannot be applied until `git-backed-content-core` archives** — it amends code and a
  capability that exist only on that branch. Queued behind the same event as
  `fix-reconciliation-index-blindness`.

## Open Questions

- **Where the break actually is.** Deliberately unresolved: this is what task 1 answers, and answering
  it by argument rather than by instrument is the failure mode this change was carved to correct.
