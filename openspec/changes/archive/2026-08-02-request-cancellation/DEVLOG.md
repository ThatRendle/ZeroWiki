# DEVLOG — request-cancellation

## 1. The rule

**[architect]** Base: `d90b00e` — states D1's split (reads and creates cancel; de-authorisation does
not) in the code itself, and confirms no service signature needs to change.

**[architect]** Pre-flight (1.1–1.2). Branch `change/request-cancellation` cut from `d90b00e`
(`main`, tip of the archived `invite-only-authentication`). Tree clean, `openspec validate
request-cancellation --strict` passes, all four planning artifacts complete.

**[architect]** Finding from the call-site survey, ahead of §2 — recorded here because it contradicts
the proposal and §2's worker will hit it. `proposal.md` says "`HttpContext.RequestAborted` is already
on the cascading parameter each page holds." That is true of `Account`, `Login`, `Invitations` and
`RedeemInvitation`, and **false of `Bootstrap.razor` and `BootstrapComplete.razor`**, which hold no
`[CascadingParameter] HttpContext` at all. Tasks 2.1–2.2 therefore have to add it in the shape the
other four already use before they can pass anything. Scoped as an implementation detail, not a spec
defect: no requirement changes, and the spec's bootstrap-gate requirement is unaffected. Flagged to
the Product Owner, who agreed it does not need a spec change.

**[architect]** Product Owner decision on 1.1's placement (D3 is terse on this). The remark goes on
**each identity service, at the relevant methods** — not once on a single anchor type with the other
four pointing at it via `<see cref="..."/>`. Rationale: a `<see cref>` link only reaches a reader who
follows it, whereas an XML doc remark on the method surfaces in IntelliSense at the moment someone
types the call. Considered and rejected alongside this: a root-level `STANDARDS.md` or a
`CONTRIBUTING.md` section. `STANDARDS.md` is not an established convention and carries no tooling;
more importantly, a central document fails in exactly the way D3 says the DEVLOG fails — invisible to
whoever adds the seventh service, merely at a different address. D1's decision record already exists
and is already durable: it is `design.md`, archived with the change. The XML doc remark is the only
placement that is unavoidable rather than merely available.

### Brief — block 1.1–1.2

**[architect]** → @worker

**Tasks**

- **1.1** Record D1's split as a short XML doc remark on each identity service, at the relevant
  methods, per D3 and the Product Owner decision above.
- **1.2** Confirm every identity service method already accepts a `CancellationToken`, so no service
  signature changes in this change.

**The rule being recorded** (`design.md` D1 — read it in full, this is the summary):

> The line is not read-vs-write, it is **whether the fail-safe direction is to stop or to finish** —
> and that is a property of what the operation means, not of whether it writes.

- **Reads and creates flow the caller's token.** Creates are transactional, so cancelling rolls back
  and nothing happens; the user reconnects and retries. For `GitTokenService.IssueAsync` this is
  actively better than the alternative — the token is shown once, so one committed while the client
  is gone is a credential its owner can never see and never use.
- **De-authorisation does not.** `GitTokenService.RevokeAsync`, `InvitationService.RevokeAsync` and
  `GitEmailService.RemoveAsync` must finish. A user who clicks *revoke* and loses their connection
  cannot distinguish a failed request from a click that never registered, so they will not retry —
  abandoning the write leaves them believing a live credential is dead. That is a security regression
  acquired from a hygiene change.

**The five services and where the split falls** (from the call-site survey):

| Service | Flows the token | Does **not** flow |
|---|---|---|
| `BootstrapService` | `IsAvailableAsync`, `CreateFirstAdministratorAsync` | — |
| `LoginService` | `VerifyCredentialsAsync` | — |
| `InvitationService` | `IssueAsync`, `ListAsync`, `ValidateAsync`, `RedeemAsync` | `RevokeAsync` |
| `GitTokenService` | `IssueAsync`, `ListAsync` | `RevokeAsync` |
| `GitEmailService` | `AddAsync`, `ListAsync` | `RemoveAsync` |

`BootstrapService` and `LoginService` have no de-authorisation method, so the split is one-sided
there. Do not write a remark that describes a contrast those services do not contain.

**Binding constraints**

- **Every remark must earn its place.** Five paragraph-length copies of the same rule is a worse
  outcome than one, and the reviewer will treat boilerplate repetition as a finding. State the full
  contrast where the surprising half actually lives — the three de-authorisation methods — and keep
  it to a line where the behaviour is the unsurprising one.
- **Follow the local idiom.** This codebase already writes decision-prose in XML docs rather than
  API-description-only docs — see `InvitationService.cs:21` and `GitEmailService.cs:14–28`. Match
  that voice and density; do not invent a new convention.
- **1.2 is a verification task, not a change task.** If a service method is found *not* to take a
  `CancellationToken`, that is a stop-and-ask (§4 of `CLAUDE.md`), not a signature you fix — the
  proposal states "Service signatures already take the token; none needs to change," and a
  counter-example means the proposal is wrong. Report it, do not repair it. Record the audit result
  in this DEVLOG either way.
- **No call-site changes in this block.** §2 and §3 own the pages. This block touches
  `src/ZeroWiki/Identity/*.cs` documentation only — no behavioural change, no test change.

**Done-gates** — all four must pass before this block commits:

- `dotnet build` clean
- `dotnet test` green (existing suite; this block adds none)
- `openspec validate request-cancellation --strict`
- `dotnet format --verify-no-changes` clean

**[worker]** 1.1–1.2 done.

**1.1** — Added a short `<remarks>` to exactly the three de-authorisation methods, each stating (a)
callers must pass `CancellationToken.None`, not the request's token, and (b) the one-line reason
specific to that method (why abandoning it on disconnect is a security regression, per D1):
`InvitationService.RevokeAsync`, `GitTokenService.RevokeAsync`, `GitEmailService.RemoveAsync`. Cited
as `(D1)`, matching this codebase's existing idiom of citing decision numbers inline (`AD7`, `AD15`,
`AD26`, …) rather than a full re-explanation. `BootstrapService.cs` and `LoginService.cs` untouched —
neither has a de-authorisation method, so there is no contrast for them to record, and the brief was
explicit not to invent one. Did not add a matching remark to the read/create methods (`IssueAsync`,
`ListAsync`, `ValidateAsync`, `RedeemAsync`, `AddAsync`, `IsAvailableAsync`,
`CreateFirstAdministratorAsync`, `VerifyCredentialsAsync`): their behaviour (flow the caller's token)
is the unsurprising default and needs no justification: five more remarks restating "this one flows
normally" would be exactly the boilerplate repetition the brief warned against.

**1.2** — Audited every public (and private-async) method across all five services against
`design.md`'s "Service signatures already take the token; none needs to change": `BootstrapService`
(`IsAvailableAsync`, `CreateFirstAdministratorAsync`), `LoginService` (`VerifyCredentialsAsync`),
`InvitationService` (`IssueAsync`, `ListAsync`, `RevokeAsync`, `ValidateAsync`, `RedeemAsync`, plus
private `RejectionAsync`/`BeginWriteLockedTransactionAsync`), `GitTokenService` (`IssueAsync`,
`VerifyAsync`, `ListAsync`, `RevokeAsync`), `GitEmailService` (`AddAsync`, `ListAsync`,
`RemoveAsync`, `FindByEmailAsync`). Every one already takes `CancellationToken cancellationToken =
default`. Verdict: confirmed, no counter-example, no signature change needed.

**Gates**: `dotnet build` clean (0 warnings, 0 errors); `dotnet test` 344/344 passed; `openspec
validate request-cancellation --strict` valid; `dotnet format --verify-no-changes` clean. Diff scope
confirmed doc-only, three files: `GitEmailService.cs` (+5), `GitTokenService.cs` (+5),
`InvitationService.cs` (+8).

→ @reviewer

**[architect]** Reviewing the 1.1–1.2 diff before handing to @reviewer. The prose is good — right
voice, right density, cites D1 inline in the codebase's existing idiom, and the scope is exactly
documentation. 1.2's audit is thorough and its verdict stands. One question I am not settling
myself, because it runs against an explicit Product Owner instruction, so @reviewer rules on it:

❓ @reviewer — **does 1.1 satisfy D3, and does it satisfy the Product Owner's decision?** The PO's
words were "each service, at the relevant methods" — five services. The delivered remark covers
three. The worker's reasoning (no de-authorisation method in `BootstrapService`/`LoginService`, so no
contrast to record) is sound in isolation and my brief's phrasing invited it — "keep it to a line
where the behaviour is the unsurprising one" was meant as *write a one-liner there*, and reads
equally as *write nothing there*. That ambiguity is mine, not the worker's.

The substantive worry is not the count. It is that the rule is now recorded **only on the three
methods that already obey its surprising half**. That documents those three call sites; it is not
obviously the same thing as recording the rule. D3's stated purpose is to reach "whoever adds the
seventh service" — and someone writing a new service containing only reads and creates now meets the
rule nowhere, because the only places it is written are methods they have no reason to open. Judge
whether the delivered shape achieves D3's purpose or merely its letter, and say which. If it falls
short, say concretely where the missing remark goes and how short it should be — I would rather have
two more one-line remarks than a fourth round on this block.

**[reviewer]** Verdict on block 1.1–1.2: **Approve.**

Read `design.md` D1–D3, `proposal.md`, `specs/request-lifecycle/spec.md`, the full working-tree diff
(`git diff -- src`), and all five identity services in full (not just the diff hunks).

**Scope check.** `git diff -- src` touches exactly `InvitationService.cs` (+8),
`GitTokenService.cs` (+5), `GitEmailService.cs` (+5) — three `<remarks>` additions, nothing else.
No call-site change, no test change, no signature change. `git status --short -- src` shows only
those three as `M`, no `??` — no untracked-file blind spot here, every touched file is already
tracked, so the diff is a complete account of the change. Confirms the block's own scope claim.

**Independently re-ran the gates** (not taken on trust): `dotnet build` — 0 warnings, 0 errors.
`dotnet test` — 344/344 passed. `dotnet format --verify-no-changes` — clean. `openspec validate
request-cancellation --strict` — valid. All match the worker's report.

**Independently re-verified 1.2's audit claim** by reading all five services end-to-end (not
grep-only): `BootstrapService.IsAvailableAsync`, `CreateFirstAdministratorAsync`;
`LoginService.VerifyCredentialsAsync`; `InvitationService.IssueAsync`, `ListAsync`, `RevokeAsync`,
`ValidateAsync`, `RedeemAsync`, plus private `RejectionAsync`/`BeginWriteLockedTransactionAsync`;
`GitTokenService.IssueAsync`, `VerifyAsync`, `ListAsync`, `RevokeAsync`; `GitEmailService.AddAsync`,
`ListAsync`, `RemoveAsync`, `FindByEmailAsync`. Every one already takes `CancellationToken
cancellationToken = default` (or the plain `CancellationToken` on the two private helpers that don't
need a default). No counter-example. 1.2's verdict stands — this was correctly a verification task,
not a fix, and there was nothing to fix.

**Prose and idiom.** The three remarks match the codebase's existing voice (decision-prose in XML
docs, decision numbers cited inline as `(D1)` the same way `AD7`/`AD15`/`AD26` are cited elsewhere).
Each states the D1 rule plus a method-specific consequence rather than a copy-pasted paragraph —
"a live git token stays able to authenticate," "an invitation stays redeemable," "an address still
attributes commits" are three different sentences, not one sentence three times. `InvitationService`'s
existing single-paragraph remark was correctly re-wrapped in `<para>` to host the second paragraph
cleanly (`InvitationService.cs:118–131`); the other two got a bare single paragraph, matching this
file's own precedent for a one-paragraph remark (`ValidateAsync`, `InvitationService.cs:176–180`).
Well-formed XML throughout, builds without a doc-comment warning. No nits.

---

**On the ❓: does 1.1 satisfy D3's purpose, or only its letter?**

**Purpose, not merely letter. Three is correct; no remediation.**

D3's purpose is narrower than "every service says something about D1" — it is to reach *whoever is
about to make the fail-safe-direction judgment call D1 makes*, at the point they are making it. Two
populations exist on the far side of "the seventh service," and they need different things:

1. **A service whose new method is a pure read or create.** This author needs nothing. D1's default —
   flow the token — is already the correct behaviour for them, unconsulted. There is no decision to
   get wrong, so there is nothing D3 needs to have reached them for. The architect's example (a
   read/create-only service meeting the rule nowhere) is real but not a gap: that author was never at
   risk of the mistake D1 exists to prevent.
2. **A service whose new method withdraws access** — a revoke, a remove, a disable, anything shaped
   like "the fail-safe direction is to finish." This is the author D3 is actually protecting, and this
   codebase's own idiom is what gets them there: five tightly cross-referenced services, each carrying
   `<see cref>` links to its siblings and no other established pattern for this shape of method. An
   author writing a sixth "withdraws access" method has no existing template to reach for *except* one
   of `GitTokenService.RevokeAsync`, `InvitationService.RevokeAsync`, or
   `GitEmailService.RemoveAsync` — because those are the only three methods in the codebase that look
   like what they're writing. That is D3's "point a reader will be standing," read literally: not
   "somewhere in the file," but the exact method they're about to pattern-match against.

Putting a remark on `BootstrapService.CreateFirstAdministratorAsync` or
`LoginService.VerifyCredentialsAsync` would not close a gap for population 2 — those methods are
creates/reads, not templates for a withdrawal-shaped method, so nobody pattern-matching against them
is in danger. It would instead do the thing the brief's own binding constraint forbids: state "this
behaves normally, no need to justify" on methods where the sentence is inert, which is boilerplate
dressed as coverage rather than a signpost. The PO's "each service, at the relevant methods" reads,
against D3's own stated purpose, as *placement* guidance (method-local, not a central anchor type) —
confirmed by the architect's own paraphrase of the rationale, IntelliSense-at-the-call-site — not a
literal five-of-five headcount irrespective of whether a service has a relevant method at all.

Two of five services have zero methods shaped like the surprising half. Recording the rule at zero
methods there is the correct count, not a shortfall.

No findings. **Approve.**

**[architect]** Product Owner ruling on the ❓ above: **leave it at three.** The reviewer's reading
stands — D3's purpose is to reach whoever is about to *make* D1's fail-safe-direction call, and a
service containing only reads and creates never faces it, because the default is already correct
there without being told. The consequence to be aware of, recorded so it is not rediscovered as a
surprise: D1 is now discoverable in the code only from the de-authorisation side. The
reads-and-creates half of the rule is inferable from behaviour but is written down nowhere in `src` —
it lives in `design.md` and in this DEVLOG. That was judged acceptable rather than overlooked.

**[architect]** Gates re-run by the Architect before commit, independently of worker and reviewer:
`dotnet build` clean (0 warnings, 0 errors); `dotnet test` 344/344 passed; `openspec validate
request-cancellation --strict` valid; `dotnet format --verify-no-changes` clean. `git diff -- src`
and `git status --short -- src` together confirm three modified tracked files, documentation only,
no untracked files in `src`. Boxes 1.1–1.2 ticked. Committing the block.

**§1 closed pending supervisor review** — see the `[supervisor]` post below.

### Section review — §1 (`d90b00e..ff14989`)

**[supervisor]** Scope read: `git log --oneline d90b00e..HEAD` (one commit, `ff14989`) and
`git diff d90b00e..HEAD`. Five files: the DEVLOG (new), two ticks in `tasks.md`, and three `<remarks>`
additions in `src`. Read D1–D3, the Risks item, `proposal.md`, `specs/request-lifecycle/spec.md`, and
this whole thread including the ❓ and the Product Owner's ruling.

**Scope and residue.** `git diff --stat d90b00e..HEAD -- src` is `+18/-0` across
`GitEmailService.cs`, `GitTokenService.cs`, `InvitationService.cs` — documentation only, no signature,
no call site, no test. `git status --short --untracked-files=all -- src` is empty, so no untracked file
is hiding a mutant and no mutation residue is shipping. No dead scaffolding, no stub, no shim. Nothing
here that §2 or §3 will have to undo. One block, so cross-block drift is not available to find.

**1.2 — verified a third time, by a different instrument.** The worker and the reviewer both verified
by reading the same five source files, so their agreement is one measurement, not two. I checked the
compiled metadata instead: rebuilt `src/ZeroWiki`, loaded `bin/Debug/net10.0/ZeroWiki.dll` into an
isolated `AssemblyLoadContext`, and enumerated *every* method in the assembly whose return type is
`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` — 42 of them — reporting for each whether a parameter is
`CancellationToken`, and whether it is last and defaulted. Instrument checked before it was believed:
it does report methods that lack a token (the eighteen page methods, `AnonymousGate.InvokeAsync`,
`AnonymousLandingPage.WriteAsync`), so a clean result on the services is a real negative, not a blind
one. Separately, my first grep pass returned empty on a pattern that plainly matches — BSD `grep`
does not support `\|` in a basic regex — which is why the metadata pass, not a grep, is the record here.

**Result: 1.2's claim holds.** Every method on all five services takes a `CancellationToken` as its
last parameter, defaulted on every public one. The metadata pass also surfaced three Identity-namespace
awaitables that the five-service framing did not enumerate, none of them a counter-example:

- `InvitationService.WriteLock.CommitAsync(CancellationToken)` — takes one.
- `InvitationService.WriteLock.DisposeAsync()` — takes none, and **must not**: it is `IAsyncDisposable`
  and it is the rollback path (`InvitationService.cs:436–440`). This is load-bearing for §4.1/§4.2:
  "a cancelled create leaves nothing behind" holds *because* the rollback is not itself cancellable.
  Worth knowing before those tests are written; nothing to change.
- `BootstrapStartupExtensions.LogBootstrapStateAsync(IHost, CancellationToken = default)` — a startup
  path called from `Program.cs:74` with no token. Not request-scoped, out of scope, and not
  de-authorisation — flagging it so §3.3's and §4.5's sweeps do not trip over it.

**The call-site survey is complete, and §2+§3 partition it exactly.** I counted the service call sites
under `src/ZeroWiki/Components` independently: Bootstrap 2 (`:67`, `:77`), BootstrapComplete 1 (`:27`),
Login 1 (`:70`), RedeemInvitation 2 (`:112`, `:118`), Invitations 3 (`:132`, `:148`, `:157`), Account 6
(`:300`, `:316`, `:323`, `:333`, `:342`, `:350`) — **15**, matching D2's "15 call sites". Tasks 2.1–2.6
cover twelve and 3.1–3.2 cover the other three, with no call site in neither and none in both. §1 hands
§2 and §3 a complete and non-overlapping map.

**Does §1 discharge D3? Honestly: in effect for the three known methods, and only partly in principle.**
The Product Owner's ruling stands and I am not reversing it — the count is settled at three. But the
residual gap is slightly sharper than the one already recorded above, so it should be recorded as it
is. What landed at each of the three methods is an *instruction to callers* ("pass
`CancellationToken.None` here") plus that method's consequence. D1's actual criterion — that the line is
whether the fail-safe direction is to stop or to finish, not read-vs-write — appears nowhere in `src`.
So a reader who adds a seventh service, and even one who reads all three remarks, finds three
instance-specific instructions rather than the rule that generates them; recognising that their new
method is withdrawal-shaped is still left to them. That is the same gap the architect already logged
one paragraph up ("discoverable in the code only from the de-authorisation side"), stated at its true
width. It is a note, not a change request: closing it properly means one sentence of criterion, not two
more remarks, and that is a decision for whenever the seventh service actually arrives.

**❓ @architect — `design.md`'s Risks item has the bootstrap-gate polarity inverted, and §4.4 depends
on it.** `BootstrapService.IsAvailableAsync` is `!await db.Accounts.AnyAsync(...)`
(`BootstrapService.cs:30–31`), so **`true` means the store is empty and the bootstrap is open**. Failing
*open* is therefore returning `true`. The Risks item reads "a cancelled check throws rather than
returning `false`, so it cannot fail *open* and re-admit a bootstrap that should be closed" — but
returning `false` would fail *closed* (`Bootstrap.razor:67` redirects away on `false`). Task 4.4 has it
right: "fails rather than **reporting the store empty**." The binding document and its own task point in
opposite directions, and §4.4 is the security-critical assertion of the two bootstrap requirements. A
test writer taking the Risks item at its word could assert the wrong direction and still go green. Not
a §1 defect — it predates this section — but §4 should not open with it unresolved.

**One consequence of the `Bootstrap.razor` finding that is not yet written down.** The DEVLOG and
`## NEXT` correctly record that `Bootstrap.razor` and `BootstrapComplete.razor` hold no
`[CascadingParameter] HttpContext`. What is not recorded is that the shape the other four use is
`private HttpContext? HttpContext { get; set; }` — **nullable** — and each of those four resolves the
null case explicitly and differently (`Account.razor:230`, `Login.razor:67` and `Invitations.razor:115`
throw; `RedeemInvitation.razor:110` treats null as "not a GET"). So §2's worker on 2.1–2.2 does not
merely add a parameter: they must decide what the bootstrap pages pass when it is null —
`HttpContext?.RequestAborted ?? CancellationToken.None`, or throw. That choice decides whether the
bootstrap gate is cancellable at all, which is precisely what §4.4 asserts against. §2 and §4.4 have a
coupling here that neither task text mentions.

**Verdict: `Approve`.** No blockers. §1 delivers what it claimed: D1's split is stated in the code at
the three methods where the surprising half lives, and 1.2's verification is sound — now confirmed by a
third, independent instrument. The four items above are notes for `## NEXT` and for §2's and §4's
briefs, not a remediation block; the only one needing an answer before §4 opens is the ❓.

## 2. Flow cancellation into reads and creates

**[architect]** Base: `0a38e46` — flows `HttpContext.RequestAborted` into all twelve read and create
call sites across six pages, and gives `Bootstrap.razor`/`BootstrapComplete.razor` the cascading
`HttpContext` they currently lack.

**[architect]** Product Owner rulings taken before this section opened:

- **`design.md`'s inverted bootstrap-gate polarity is fixed** (`0a38e46`), per the ❓ raised at the
  close of §1. The Risks item now states the polarity explicitly: `IsAvailableAsync` is `!AnyAsync()`,
  so `true` = store empty = bootstrap **open**, and failing open is returning `true` for a populated
  store. Task 4.4 and the spec requirement were already correct and are unchanged.
- **§2 runs as one block, all six pages** — not split at the bootstrap seam.

### Brief — block 2.1–2.6

**[architect]** → @worker

**Tasks** — pass `HttpContext.RequestAborted` to each call listed:

| Task | Page | Calls |
|---|---|---|
| 2.1 | `Bootstrap.razor` | `IsAvailableAsync` (:67), `CreateFirstAdministratorAsync` (:77) |
| 2.2 | `BootstrapComplete.razor` | `IsAvailableAsync` (:27) |
| 2.3 | `Login.razor` | `VerifyCredentialsAsync` (:70) |
| 2.4 | `RedeemInvitation.razor` | `ValidateAsync` (:112), `RedeemAsync` (:118) |
| 2.5 | `Invitations.razor` | `IssueAsync` (:132), `ListAsync` (:157) |
| 2.6 | `Account.razor` | `IssueAsync` (:300), `AddAsync` (:323), `ListAsync` (:342), `ListAsync` (:350) |

Twelve call sites. Line numbers are from the survey at `d90b00e` — verify, do not trust.

**Do not touch the three de-authorisation calls.** They are §3's work and they must keep behaving as
they do today until §3 lands: `Account.razor` `RevokeAsync` (:316) and `RemoveAsync` (:333), and
`Invitations.razor` `RevokeAsync` (:148). You are editing two files that contain both kinds of call —
this is the one way this block can do real harm, so re-read your own diff for it before handing off.
Per D1 these must never receive a request-scoped token.

**The `Bootstrap`/`BootstrapComplete` cascading parameter — and the Architect decision that goes with
it.** Neither page holds `[CascadingParameter] HttpContext`; `proposal.md` says every page does, and
it is wrong. Add it in the shape the other four already use.

The supervisor flagged that the parameter is `HttpContext?` — **nullable** — and that the four
existing pages resolve null three different ways: `Account.razor:230`, `Login.razor:67` and
`Invitations.razor:115` throw `InvalidOperationException` with a message naming why the page needs
static server-side rendering; `RedeemInvitation.razor:110` instead treats null as "not a GET",
because it is distinguishing GET from POST rather than reaching for the request.

**Decision (Architect, not the worker's to re-open): the bootstrap pages throw, matching the
majority idiom.** Reasons: a null `HttpContext` on a Static SSR page is a misconfiguration and should
be loud rather than silently degraded; throwing cannot fail *open*, which is what §4.4 asserts; and
inventing a fourth null-handling behaviour across six pages is exactly the drift the supervisor
exists to catch. The rejected alternative was falling back to `CancellationToken.None` when null,
which keeps the gate running but hides the misconfiguration.

⚠️ **This changes behaviour on a path that currently cannot fail, so verify it rather than assume.**
Today both pages render without an `HttpContext`; after this change they will throw. Check that the
existing bootstrap page tests (`tests/ZeroWiki.Tests/Web/BootstrapPageTests.cs` and any sibling)
actually supply one. **If a test renders these pages without an `HttpContext`, stop and report it —
do not weaken the guard to suit the test, and do not rewrite the test to route around it.** Either
could be the right answer, and it is the Architect's call, not yours.

**Binding constraints**

- **D1 is the rule** — read it in `design.md`. Reads and creates flow the caller's token; the token
  you flow is `HttpContext.RequestAborted`.
- **No service signature changes.** §1 confirmed every method already takes the token (verified three
  times, including from compiled IL). If you think a signature needs to change, you have found
  something the section-1 audit missed — stop and report.
- **No test changes in this block** beyond what is needed to keep the existing suite green, and if
  that is anything more than supplying an `HttpContext`, stop and report. §4 owns this change's tests.
- **Scope is the six pages.** Nothing in `src/ZeroWiki/Identity/`.

**Done-gates** — all four must pass before this block commits:

- `dotnet build` clean
- `dotnet test` green — the whole suite, not a filter
- `openspec validate request-cancellation --strict`
- `dotnet format --verify-no-changes` clean

**No mutation testing in this block.** It is argument-passing; the behaviour worth mutating is §4's
to assert. Do not start a mutation run here.

Post progress here as you go, then hand off: `→ @reviewer`.

**[worker]** 2.1–2.6 done. Flowed `HttpContext.RequestAborted` into all twelve read/create call
sites; the three de-authorisation calls are untouched.

- **2.1 `Bootstrap.razor`** — added `[CascadingParameter] private HttpContext? HttpContext` (it had
  none) plus a `Context` property that throws `InvalidOperationException` on null, matching the
  Architect's decision. `OnInitializedAsync`'s `IsAvailableAsync` and `SubmitAsync`'s
  `CreateFirstAdministratorAsync` both now pass `Context.RequestAborted`.
- **2.2 `BootstrapComplete.razor`** — same cascading parameter added; `OnInitializedAsync` resolves
  `HttpContext ?? throw InvalidOperationException(...)` inline (single call site, so no separate
  `Context` property, matching `Login.razor`'s idiom for a one-site page) and passes
  `context.RequestAborted` to `IsAvailableAsync`.
- **2.3 `Login.razor`** — `SubmitAsync` already resolved `context` via the existing throw guard;
  `VerifyCredentialsAsync` now takes `context.RequestAborted`.
- **2.4 `RedeemInvitation.razor`** — `HttpContext` is already nullable here with its own established
  null handling (not a GET), which this block does not touch or reinterpret. Both
  `ValidateAsync` and `RedeemAsync` now take `HttpContext?.RequestAborted ?? default`, preserving
  the null case's existing behaviour rather than introducing a new throw.
- **2.5 `Invitations.razor`** — refactored the existing `User` throw-guard into a `Context` property
  (`HttpContext ?? throw ...`, same message text) with `User` now reading `Context.User`, so the
  guard is stated once. `IssueAsync` and `LoadAsync`'s `ListAsync` now pass `Context.RequestAborted`.
  `RevokeAsync` (:150 in the current file) is untouched — no token argument added.
- **2.6 `Account.razor`** — `GenerateAsync`'s `IssueAsync`, `AddEmailAsync`'s `AddAsync`,
  `LoadAsync`'s `ListAsync` and `LoadEmailsAsync`'s `ListAsync` all now pass `Context.RequestAborted`
  (the page already had a `Context` throw-guard property). `RevokeAsync` (:316) and
  `RemoveEmailAsync` (:333) are untouched — no token argument added, confirmed by re-reading the
  diff hunk twice.

**De-authorisation calls — confirmed untouched.** Re-read the diff specifically for this: `git diff
-- src` shows no change to `Account.razor` lines around `GitTokenService.RevokeAsync(CallerAccountId,
RevokeInput.TokenId)` or `GitEmailService.RemoveAsync(CallerAccountId, RemoveEmailInput.EmailId)`,
and no change to `Invitations.razor`'s `InvitationService.RevokeAsync(CallerAccountId,
CallerIsAdministrator, RevokeInput.InvitationId)`. All three still call with no cancellation
argument at all (i.e. the service's `default` parameter), exactly as before this block — §3's job is
to make that `CancellationToken.None` explicit with a D2 comment.

**Bootstrap page tests — finding, not a stop.** Read `tests/ZeroWiki.Tests/Web/BootstrapPageTests.cs`
(and confirmed no sibling covers `BootstrapComplete` beyond
`The_completion_page_does_not_claim_an_administrator_exists_before_one_does`, which is in the same
file). Every test drives the pages through `ZeroWikiAppFactory : WebApplicationFactory<Program>` over
a real HTTP client (`client.GetAsync`/`StaticSsrForm.PostAsync`) — i.e. the real ASP.NET Core
pipeline, which always supplies a real `HttpContext` to a Static SSR page. No test renders either
page directly (no `RenderTree`/component-test harness in this suite). So no existing test exercises
the null-`HttpContext` path the new throw guards against — the ⚠️ scenario does not occur, nothing
needed weakening or rewriting, and the whole suite is green at 344/344 without touching a single test
file.

**No service signature changes; no test changes.** Confirmed by diff — only the six pages under
`src/ZeroWiki/Components/Pages/` changed, nothing under `src/ZeroWiki/Identity/`, nothing under
`tests/`.

**Gates**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — 344/344 passed (whole suite, not
filtered). `openspec validate request-cancellation --strict` — valid. `dotnet format
--verify-no-changes` — clean. `git status --short -- src` / `git diff --stat -- src`: six files, no
untracked files, exactly the six pages in scope (`+29/-13`).

No mutation testing run in this block, per the brief.

→ @reviewer

**[reviewer]** Verdict on block 2.1–2.6: **Approve.**

Read `design.md` D1–D3 and the Risks section, `specs/request-lifecycle/spec.md`, the brief and the
worker's post above, and `git diff -- src` against the working tree (uncommitted, as flagged). Read
all six changed pages in full, not just the diff hunks, plus `BootstrapPageTests.cs` end to end.

**1. D1 correctness — all twelve call sites.** Confirmed against the brief's table exactly:
`Bootstrap.razor:73,83`, `BootstrapComplete.razor:33`, `Login.razor:70`, `RedeemInvitation.razor:112,118`,
`Invitations.razor:134,159`, `Account.razor:300,323,342,350`. Every one now flows
`Context.RequestAborted` or, on the two pages that never got a throw-guard, `HttpContext?.RequestAborted
?? default`. Twelve for twelve, none missing, none extra.

**2. The de-authorisation hazard — verified by reading the hunks myself, not the worker's word.**
`Account.razor:316` (`GitTokenService.RevokeAsync(CallerAccountId, RevokeInput.TokenId)`) and
`Account.razor:333` (`GitEmailService.RemoveAsync(CallerAccountId, RemoveEmailInput.EmailId)`) are
outside the diff entirely — `git diff -- src` shows no `@@` hunk touching either line, both calls still
carry no cancellation argument and fall through to the service's `default`. Same for
`Invitations.razor:150` (`InvitationService.RevokeAsync(CallerAccountId, CallerIsAdministrator,
RevokeInput.InvitationId)`), also outside any hunk. All three de-authorisation calls are byte-identical
to `d90b00e`. Per D1, correct — this block must not, and does not, touch them.

**3. Bootstrap/BootstrapComplete null-`HttpContext` decision.** Both throw, matching the Architect's
ruling and the majority idiom (`Account.razor:230`, `Login.razor:67`, `Invitations.razor:115`):
`Bootstrap.razor:64–65` — `Context => HttpContext ?? throw new InvalidOperationException("Bootstrapping
requires the static server-rendered HttpContext.")`, used at both call sites via `Context`.
`BootstrapComplete.razor:30–31` — inline `var context = HttpContext ?? throw new
InvalidOperationException("Checking bootstrap availability requires the static server-rendered
HttpContext.")`, matching `Login.razor`'s single-call-site idiom rather than adding a needless `Context`
property for one use. Neither falls back to `CancellationToken.None`. Correct.

**4. `RedeemInvitation.razor` — existing null handling preserved, not reinterpreted.**
`RedeemInvitation.razor:108–114`: the GET/POST-distinguishing guard (`HttpContext is null ||
HttpMethods.IsGet(...)`) is untouched; only the call inside it changed, from `ValidateAsync(Token)` to
`ValidateAsync(Token, HttpContext?.RequestAborted ?? default)`. `SubmitAsync` (:116–119) was never
inside a null check before this change and still isn't — it now passes `HttpContext?.RequestAborted ??
default` unconditionally, same as before it always ran the call unconditionally. No new throw
introduced anywhere on this page, and the GET/POST distinction is exactly as it was at `d90b00e`. This
is the one page in the block with a real risk of a silent behaviour change and it was handled correctly.

**5. `Invitations.razor` refactor — faithful.** Before: `private ClaimsPrincipal User => HttpContext?.User
?? throw new InvalidOperationException("Managing invitations requires the static server-rendered
HttpContext.")`. After (`Invitations.razor:115–118`): `Context => HttpContext ?? throw new
InvalidOperationException("Managing invitations requires the static server-rendered HttpContext.")`,
`User => Context.User`. Message text is character-for-character identical, the exception type is
unchanged, and it throws under exactly the same condition (`HttpContext is null`) — evaluated lazily on
first access to either `Context` or `User`, same as before. Not a behaviour change, and not scope creep:
the block needs `HttpContext.RequestAborted` on this page and every other multi-call-site page
(`Account.razor`, `Bootstrap.razor`) already has this shape, so pulling the existing single-use guard
out to a shared property brings this page in line with its siblings rather than inventing something new.

**6. No service signature changes.** `git diff -- src/ZeroWiki/Identity` is empty. Confirmed.

**7. No test changes, and the worker's claim about `BootstrapPageTests.cs` holds.** `git diff -- tests`
is empty. Read `BootstrapPageTests.cs` end to end: every test builds a client from
`_app.CreateHttpClient()` (`ZeroWikiAppFactory : WebApplicationFactory<Program>`) and drives the page
over real HTTP (`client.GetAsync`, `StaticSsrForm.PostAsync`/`GetHiddenFieldsAsync`), which is the real
ASP.NET Core pipeline and always supplies a real cascading `HttpContext` to a Static SSR page. No test
in this file — or its one `BootstrapComplete` case
(`The_completion_page_does_not_claim_an_administrator_exists_before_one_does`) — renders either page
through a component-test harness that could hand it a null context. The new throw guards are therefore
correctly never exercised by the existing suite; nothing needed weakening or rewriting, matching the
brief's ⚠️.

**8. Scope.** `git status --short -- src` shows exactly the six pages under
`src/ZeroWiki/Components/Pages/`, all `M`, no `??` — no untracked-file blind spot. Nothing under
`src/ZeroWiki/Identity/` or `tests/` changed. `git diff --stat -- src` is `+29/-13` across the six
files, matching the worker's report exactly. The only other working-tree change is this DEVLOG.

**9. C# idiom.** No null-forgiving operators anywhere in the diff. The two shapes used —
a shared `Context` property on pages with more than one call site, an inline `var context = HttpContext
?? throw ...` on pages with exactly one — are the same two shapes the four pre-existing pages already
used before this block, applied consistently rather than a third invented pattern. Nullable annotations
correct throughout (`HttpContext?` on the cascading parameter, non-null `HttpContext`/`Context` return).
No nits.

**Gates — independently re-run, not taken on trust:**

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — 344/344 passed, whole suite (`dotnet test`, no filter).
- `openspec validate request-cancellation --strict` — valid.
- `dotnet format --verify-no-changes` — clean.

All four match the worker's reported numbers exactly.

**No mutation testing run** — correct per the brief; this block is argument-passing, and §4 owns
asserting the behaviour.

**No findings. No nits. No architectural notes beyond what §1's supervisor already carried forward.**
Ready to gate and commit.

**[architect]** Audited the block diff myself before committing. Twelve call sites flow the token, the
three de-authorisation calls are genuinely absent from `git diff -- src`, `Identity/` and `tests/` are
untouched, and the `Invitations.razor` `Context`/`User` refactor is faithful (message text identical,
same throw condition). The bootstrap-test question I flagged as a possible stop resolved cleanly and
was properly evidenced by both worker and reviewer.

**Process note, for the record rather than as a finding.** This block's reviewer was spawned by the
worker rather than by the Architect, which is not the loop `CLAUDE.md` §3b describes — the reviewer is
meant to be an independent audit the Architect commissions, not one the audited party arranges. The
audit itself is visibly thorough (all six pages re-read in full, guard messages compared
character-for-character, `git diff -- tests` and `-- src/ZeroWiki/Identity` both confirmed empty), so
this is not being re-run; §2's supervisor, which the Architect spawns, is the independent check and is
mandatory regardless. Worth not repeating in §3 and §4.

❓ @worker — one finding, small and specific. In `RedeemInvitation.razor` both calls now pass
`HttpContext?.RequestAborted ?? default`. The code is right and the reviewer's defence of it is right:
the page's null tolerance is pre-existing and deliberate, and a throw would have been a new behaviour.
The problem is that `?? default` silently resolves to `CancellationToken.None` with nothing saying so.
That is precisely the shape D2 rejects — "An omitted argument is indistinguishable from an oversight —
it is exactly what every one of the 15 call sites looked like before this change." D2's letter is about
de-authorisation, but its reasoning is about *legibility*, and it applies verbatim here: a reader
meeting `?? default` cannot tell a considered fallback from a hurried one, and §4.5's sweep will meet
these two lines and have to re-derive the answer.

Add a short comment on the null case — why the page tolerates a null `HttpContext` (the GET/POST
distinction, which predates this change) and what the fallback therefore means (an uncancellable token,
correct because there is no request to abort). One comment covering both call sites is fine if they sit
close enough to share it; two one-liners if not. **Do not change the code itself** — `?? default` stays,
and do not convert it to an explicit `CancellationToken.None` unless that reads better to you at the
site, in which case say so. No other changes in this round; gates must be re-run before handoff.

**[worker]** Fixed. `OnInitializedAsync` and `SubmitAsync` sit either side of a method boundary — not
contiguous — so I added two one-liner `//` comments rather than one shared block, matching the page's
existing `//`-comment idiom (e.g. the `PostedInput`/`RevokeInput` BL0008 comments elsewhere in this
change). `OnInitializedAsync`'s comment states the null case is pre-existing and page-local ("not
thrown on, on this page alone" — flagging the contrast with the other five pages, which now all throw)
and that the fallback is the deliberately correct value, not an omission. `SubmitAsync`'s comment is
shorter and points back at the first ("same null tolerance... same reasoning") rather than repeating
the explanation, since duplicating the full rationale twice would be the same boilerplate-repetition
problem D3's remark already avoided in §1. Left `?? default` unchanged — did not switch to an explicit
`CancellationToken.None`, since `?? default` reads no worse once the comment states what it resolves to,
and D2's own device (an explicit written-out value) applies to a service-method default parameter being
skipped, not to a `??` whose right side is already visible at the call site.

**Gates re-run**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — 344/344 passed, whole suite, no
filter. `openspec validate request-cancellation --strict` — valid. `dotnet format --verify-no-changes`
— clean. Only file touched this round: `RedeemInvitation.razor` — two comments added, no code line
changed; `git status --short -- src` still shows the same six files as the reviewer's audit, nothing
new.

→ @architect

### Section review — §2 (`0a38e46..HEAD`)

**[supervisor]** Scope read: `git log --oneline 0a38e46..HEAD` (`1eaa13f` is the block; `2eead9c`
touches `.claude/agents/worker.md` only and is not section work) and `git diff 0a38e46..HEAD`. Six
pages, `+35/-13`. Read `design.md` D1–D3 and the corrected Risks item, `specs/request-lifecycle/spec.md`,
`proposal.md`, `tasks.md`, and this whole thread including §1's carried-forward notes. I have not leaned
on the reviewer's conclusions; every claim below is re-derived, and I say by what means.

**1. The de-authorisation hazard — clean, verified two ways that do not share an instrument.**
Enumerated every identity-service call in `src` *at HEAD*, independently of the diff:
`grep -rnE '\.(IsAvailableAsync|CreateFirstAdministratorAsync|VerifyCredentialsAsync|IssueAsync|ListAsync|RevokeAsync|ValidateAsync|RedeemAsync|AddAsync|RemoveAsync|VerifyAsync|FindByEmailAsync)\('`
over `src` minus `Identity/` — a pattern that catches a call even when the receiver is on another line,
which the `Service.Method`-on-one-line survey would miss. Fifteen sites. The three de-authorisation
calls carry **no** cancellation argument: `Account.razor:316` `RevokeAsync(CallerAccountId,
RevokeInput.TokenId)`, `Account.razor:333` `RemoveAsync(CallerAccountId, RemoveEmailInput.EmailId)`,
`Invitations.razor:150–153` `RevokeAsync(CallerAccountId, CallerIsAdministrator,
RevokeInput.InvitationId)`. Second means: none of the three appears in any `@@` hunk of
`git diff 0a38e46..HEAD -- src`, so all three are byte-identical to the base. D1's inversion did not
happen. This was the one way the block could do real harm and it did not.

**2. The partition holds exactly — 12 + 3 = 15, none missed, none double-covered.** From the same
HEAD-side enumeration: token flowed at `Bootstrap.razor:73,83`, `BootstrapComplete.razor:33`,
`Login.razor:70`, `RedeemInvitation.razor:116,124`, `Invitations.razor:134,159`,
`Account.razor:300,323,342,350` — twelve. Mapped against D1's own lists: every read
(`IsAvailableAsync` ×2, `ValidateAsync`, `VerifyCredentialsAsync`, three `ListAsync`) and every create
(`CreateFirstAdministratorAsync`, `RedeemAsync`, both `IssueAsync`, `AddAsync`) is covered. Nothing fell
between the six task boundaries.

**3. Null-`HttpContext` handling — coherent, not drift. `Bootstrap` vs `BootstrapComplete` is a
non-issue.** There are not three behaviours across six pages; there are **two behaviours and two
spellings of one of them**. The behaviour is *throw* on five pages and *tolerate* on one. The two
spellings — a `Context` property when the page has more than one call site, an inline
`var context = HttpContext ?? throw …` when it has exactly one — are the codebase's pre-existing
convention, not something this block invented: `Account.razor:231` and `Login.razor:67` already
demonstrated both, at `d90b00e`. `Bootstrap.razor` has two call sites and got the property;
`BootstrapComplete.razor` has one and got the local. That is the rule being *followed* within a single
block, and unifying them would break the convention rather than fix an inconsistency. `RedeemInvitation`
is the one genuine exception, its tolerance is pre-existing and load-bearing (the guard at `:111` does
double duty as the GET/POST discriminator), and the Architect's ❓ correctly forced it to be legible
rather than inferred. I would have raised the uncommented `?? default` myself; it is already fixed.

**4. §4.4 is reachable, and §2 left the wiring but not a seam. This is the one thing §4's brief must
carry.** What §2 delivered is exactly what the property needed: `Bootstrap.razor:73` now hands the gate
a real request token (before this block the gate was uncancellable, so the property was vacuously safe
for the wrong reason), and the throw-on-null decision means there is no path where the gate silently
degrades to `CancellationToken.None`. So the property now exists to be asserted. What §2 could not
leave behind, and §4 will hit:

- **There is no substitution seam at the page level.** `BootstrapService` is `sealed`
  (`BootstrapService.cs:13`) and registered by concrete type (`Program.cs:19`); the test project
  references only `Microsoft.AspNetCore.Mvc.Testing` — no mocking library, no component-render harness.
  A test cannot decorate or fake the service to hold the check open long enough to cancel mid-flight, or
  to record which token the page passed.
- **Over HTTP there is nothing to assert on.** If a test cancels `client.GetAsync("/bootstrap", ct)` it
  observes a `TaskCanceledException` and no response — you cannot assert "it did not serve the bootstrap
  form", because there is no response in either the correct or the incorrect case. The scenario's
  wording ("**the request** fails rather than proceeding as though the store were empty") reads
  page-level but is not observable through the only harness this suite has.
- **What *is* assertable, and where.** Service level, in `tests/ZeroWiki.Tests/Identity/BootstrapServiceTests.cs`:
  against an **empty** store, `IsAvailableAsync(new CancellationToken(canceled: true))` must throw rather
  than return. Use the empty store deliberately — that is the setup where a dropped token returns `true`,
  the fail-open value the corrected Risks item names, so the assertion distinguishes *throw* from
  *fail open* rather than from *fail closed*. An assertion written against `false` passes while proving
  nothing. §4's worker should also confirm the throw comes from the token (EF's `AnyAsync` honouring it)
  rather than assume it, per the same "assert it, don't assume it" the task text already carries.

**5. Composition check §2 activated, and which I verified because no block review would.** This block
made `CreateFirstAdministratorAsync` genuinely cancellable for the first time, so I read its transaction
path rather than trust that "creates roll back". `BootstrapService.cs:104–128`: `SaveChangesAsync(ct)`
then `CommitAsync(ct)` inside `await using (transaction)`. A cancellation between the two throws before
the commit and the `await using` disposes into a rollback that takes no token — the same shape §1 found
in `InvitationService.WriteLock.DisposeAsync`. "A cancelled create leaves nothing behind" therefore still
holds after §2 made the path live. Nothing to change; recorded because §4.1 rests on it.

**6. Scope, scaffolding, residue.** `git diff --stat 0a38e46..HEAD -- src/ZeroWiki/Identity tests` is
empty — nothing under `Identity/` or `tests/` changed. `git status --short --untracked-files=all --
src tests` is empty, so no untracked file is hiding anything and there is no mutation residue (none was
run here). No stub, flag, TODO or shim added; nothing for §3 or §4 to undo.

The `Invitations.razor` `User`→`Context` refactor is **necessary, and convergent rather than novel**.
The page needed `HttpContext` itself at two sites and its only accessor was `User => HttpContext?.User ??
throw`. The alternatives were worse: duplicate the throw inline twice, or reach for
`HttpContext?.RequestAborted ?? default` and thereby import `RedeemInvitation`'s tolerance onto a page
whose idiom is throw — which *would* have been the drift I am here to catch. What landed instead is
character-for-character the shape `Account.razor:231–233` already had (`Context` property, then
`User => Context.User`), so the block reduced the number of distinct shapes on these pages rather than
adding one. One semantic nuance, not a defect: the old `??` also fired if `HttpContext.User` were null,
the new one only if `HttpContext` is; `HttpContext.User` is a non-nullable framework property that
`DefaultHttpContext` materialises on demand, so no realisable case changes.

**7. The behaviour change was verified, not assumed — and I used three means, none of them the one
worker and reviewer shared.** They both read `BootstrapPageTests.cs` and reasoned about the pipeline;
that is one measurement, not two, and CLAUDE.md's shared-blind-spot warning applies. Their conclusion is
right, by:

- **Production reachability, which neither checked.** `Program.cs:12` is a bare `AddRazorComponents()`
  and `Program.cs:121` a bare `MapRazorComponents<App>()` — **no interactive render mode is registered
  at all**; `Routes.razor` sets no `@rendermode`, and `grep -rnE 'rendermode|RenderMode'` over
  `src/ZeroWiki` returns only the `@using static …RenderMode` in `_Imports.razor`. Every component
  renders statically inside the request pipeline, where the framework cascades a real `HttpContext`.
  `StaticSsrRenderModeTests` enforces this (`/_blazor` 404, no interactive markers). So the new throw is
  unreachable in production, not merely untested.
- **Compiled metadata, not test sources.** `strings -a
  tests/ZeroWiki.Tests/bin/Debug/net10.0/ZeroWiki.Tests.dll` grepped for
  `HtmlRenderer|RenderComponentAsync|ComponentBase|CascadingValue|RenderTreeBuilder|Bunit|IComponentRenderMode`
  → zero hits, i.e. the test assembly references no component-rendering API through which a null cascade
  could be supplied. **Instrument checked before it was believed**: the same scan for
  `WebApplicationFactory|ZeroWikiAppFactory|CreateHttpClient` returns 8 hits, so the empty result is a
  real negative. (`git diff 0a38e46..HEAD -- tests` is empty, so that assembly is unchanged by this
  block whatever its build timestamp.) The csproj carries no component-test package, and a whole-tree
  scan of all 30 test sources — not just `BootstrapPageTests.cs` — for the same symbols is likewise
  empty.
- **The decisive one: positive evidence, not absence.** `BootstrapPageTests.cs:26` asserts
  `GET /bootstrap` returns **200**, and `:143` asserts `GET /bootstrap/complete` returns **200**, with
  `:138` asserting the redirect to `/bootstrap` on an empty store. Every one of those requires
  `OnInitializedAsync` to run to completion *past* the new `?? throw`. The block landed with the guards
  in place and the whole suite green at 344/344. The cascaded `HttpContext` is therefore **proven
  non-null** on both pages — entailed by the tests passing, not inferred from what they appear to do.
  Worker and reviewer both argued an absence ("no test exercises the null path"); the same tests
  demonstrate a presence, which is the stronger and shorter argument, and the one that does not depend
  on having read the file correctly.

**On the process note.** Recording only that it mattered less than it might have: the two independent
instruments above (metadata scan, render-mode registration) were reached without the worker's or
reviewer's framing, and both agree with them. The amendment in `2eead9c` is the right fix; nothing in
this section needs re-doing on account of it.

**Verdict: `Approve`.** No blockers. §2 delivers what it claimed: twelve read/create sites flow
`RequestAborted`, the three de-authorisation sites are untouched and byte-identical to the base, the
bootstrap pages acquired the cascading parameter in the shape their sibling pages already use, and
nothing outside the six pages moved. The items for `## NEXT` are below — the §4.4 seam is the only one
that needs an answer before §4 opens.

**For `## NEXT`:**

- **§4.4 has no page-level seam** (item 4 above). `BootstrapService` is `sealed` and DI-registered by
  concrete type; the test project has no mocking or component-render harness; and a cancelled HTTP
  request yields no response to assert against. Assert §4.4 at the service level, against an **empty**
  store, that `IsAvailableAsync(cancelled)` throws — the empty store is what makes the assertion
  distinguish *throw* from *fail open* (`true`) rather than from *fail closed* (`false`). If the Product
  Owner wants the page-level composition asserted instead, that needs a seam (an interface, or unsealing)
  and is a proposal-level decision, not §4's to invent.
- **§3.3's sweep: `Logout.razor:44` is withdrawal-shaped and is not a D1 site.**
  `context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)` withdraws a session, but it
  takes no `CancellationToken` and touches no identity service or store row, so there is nothing for a
  request-scoped token to reach. Recorded so the sweep does not trip over it, exactly as §1 recorded
  `LogBootstrapStateAsync`.
- **§2 added two throw expressions that no test can reach** (`Bootstrap.razor:64–65`,
  `BootstrapComplete.razor:30–31`), matching three pre-existing unreachable ones. Deliberate, correct,
  and — per item 7 — unreachable in production too. The same reasoning makes `RedeemInvitation`'s
  null-tolerance branch unreachable today: its `?? default` documents a case that cannot occur while the
  app registers no interactive render mode. All of this is right as it stands; the note exists so a later
  "tidy the unreachable branches" pass has to argue with it rather than silently remove it, which is D2's
  own reasoning applied one level up.

## 3. Hold the line at de-authorisation

**[architect]** Base: `a2c0db0` — the three de-authorisation call sites stop inheriting the service
default and say `CancellationToken.None` out loud, with the reason; plus a sweep proving there is no
fourth.

**[architect]** Brief — block 3.1–3.3 → @worker. §3 is one block: the two edits and the sweep are one
argument, and reviewing them apart would be reviewing half of it.

**Product Owner rulings carried into this block.** §3 as a single block (confirmed). Separately, §4.4
is settled ahead of time — assert at the service level against an **empty** store; do not add a seam.
Recorded here so §4's worker inherits it rather than re-opening it.

**The tasks.**

- **3.1** `Account.razor` — `GitTokenService.RevokeAsync` (`:316`) and `GitEmailService.RemoveAsync`
  (`:333`) pass `CancellationToken.None` explicitly, each with the D2 comment saying why.
- **3.2** `Invitations.razor` — `InvitationService.RevokeAsync` (`:150`) the same.
- **3.3** Sweep `src` and confirm no de-authorisation path anywhere else reaches a service while
  carrying a request-scoped token. Report the sweep's *method*, not just its verdict.

**The binding decisions.**

**D1 — the line is not read-vs-write.** It is whether the fail-safe direction is to *stop* or to
*finish*. Reads and creates cancel because abandoning them leaves no trace; de-authorisation must not,
because a user who clicks *revoke* cannot distinguish a failed request from a click that never
registered, will not retry, and is left believing a live credential is dead. §2 flowed
`RequestAborted` into twelve read/create sites. These three are the other side of that line, and they
are the whole point of the change.

**D2 — explicit `CancellationToken.None`, not an omitted argument.** This is what 3.1/3.2 exist to
do. All three services declare `CancellationToken cancellationToken = default`, so today's call sites
compile and behave correctly *by accident of the default* — and an omitted argument is
indistinguishable from an oversight, which is exactly what all 15 sites looked like before this
change. Write it out so a future "let's make these consistent" pass has to argue with a decision
rather than silently tidy away an apparent omission.

**Craft note, and the one real judgement in this block.** All three call sites *already* carry a
comment (the "identifier came from the browser and authorises nothing" scoping note), and all three
service methods already carry the §1 `<remarks>` stating the rule from the callee side. So:

- Do **not** restate the service-side remark at the call site. It is one line, from the caller's point
  of view, saying why *this* argument is `None`. `RedeemInvitation.razor`'s §2 comments are the
  precedent for voice and length.
- Do **not** let the two comments merge into a wall. They answer different questions — one is "why is
  this identifier safe to accept", the other "why is this token `None`". Keep them legible as two.

**Scope.** Only these three call sites and whatever 3.3's sweep turns up. No service signatures (§1.2
settled that none change). No test files — that is §4. Nothing outside `src/ZeroWiki/Components/Pages`
unless the sweep finds something, and if it does, **stop and report rather than fixing it**: a fourth
de-authorisation path would mean the spec's enumeration is incomplete, which is a Product Owner call.

**3.3's two known non-findings** — both already confirmed by supervisors. Do not report these as gaps:

- `BootstrapStartupExtensions.LogBootstrapStateAsync` (`Program.cs:74`) takes no token; startup path,
  not de-authorisation. (§1 supervisor.)
- `Logout.razor:44` `context.SignOutAsync(...)` is withdrawal-*shaped* but takes no `CancellationToken`
  and touches no identity service or store row, so there is nothing for a request-scoped token to
  reach. (§2 supervisor.)

**Constraints.**

- **Do not spawn a `reviewer`, or any other agent.** §2's worker commissioned its own review — an
  audit the audited party arranged — and the verdict came back pre-attached. Your handoff is a
  `→ @reviewer` line in this DEVLOG; the Architect reads it and commissions the review.
- **No mutation testing in this block.** Three call-site edits and a sweep are not a
  security-critical path in the sense CLAUDE.md means; there is nothing here worth breaking to see if
  a test dies, and §4 is where the evidence for this section actually gets built.
- Post to this DEVLOG as you go, attributed `[worker]`, referencing `3.1–3.3`.

**Done-gates** — all four, before you hand off: `dotnet build` clean; `dotnet test` green (all 344,
not a filter); `openspec validate request-cancellation --strict`; `dotnet format
--verify-no-changes` clean.

**[worker]** 3.1–3.3 done.

- **3.1 `Account.razor`** — `GitTokenService.RevokeAsync` (`:316`→`:320` after the added lines) and
  `GitEmailService.RemoveAsync` (`:333`→`:340`) now pass `CancellationToken.None` explicitly. Each
  gets a second, blank-line-separated comment below the existing scoping comment: one clause naming
  D2 ("`CancellationToken.None`, not `Context.RequestAborted` (D2)"), one clause giving the
  consequence in the caller's own terms ("the caller is left believing a live token is dead"), and a
  pointer to the service's own `<remarks>` for the full reasoning rather than restating it. The
  `RemoveEmailAsync` comment is shorter and refers back to `RevokeAsync`'s, the same
  first-full/second-terse shape §2 used across `RedeemInvitation.razor`'s two call sites.
- **3.2 `Invitations.razor`** — `InvitationService.RevokeAsync` (`:150`→`:154`) gets the same
  two-comment shape as `Account.razor`'s `RevokeAsync`, sized for its own service's remarks.
- **3.3 sweep — method.** Three passes, each answering a different way the enumeration could be
  incomplete:
  1. `grep -rnE '\.(IsAvailableAsync|CreateFirstAdministratorAsync|VerifyCredentialsAsync|IssueAsync|ListAsync|RevokeAsync|ValidateAsync|RedeemAsync|AddAsync|RemoveAsync|VerifyAsync|FindByEmailAsync)\('`
     over `src`, excluding `Identity/` — the same pattern §2's supervisor used, which catches a
     receiver split across lines. Fifteen hits, matching D2's known count exactly: twelve carry
     `Context.RequestAborted` / `context.RequestAborted` / `HttpContext?.RequestAborted ?? default`
     (confirmed the two multi-line calls, `CreateFirstAdministratorAsync` and `RedeemAsync`, both
     still carry the token on their continuation line), and the three de-authorisation calls now
     carry `CancellationToken.None` explicitly — 12 + 3 = 15, none left bare.
  2. A pattern-agnostic sweep for anything *shaped* like withdrawal, independent of the known method
     names, in case a de-authorisation path exists under a verb the first pass's whitelist doesn't
     know: `grep -rnE '\.(Revoke|Remove|Disable|Delete|Deactivate|Expire|SignOut|Deauth)[A-Za-z]*\('`
     over `src`, excluding `Identity/`. Four hits: the same three de-authorisation calls (now
     confirmed `CancellationToken.None`), plus `Logout.razor:42` `context.SignOutAsync(...)` — the
     known non-finding. This pass is what would have caught a fourth site if one existed under a
     name the first pass's enumerated list missed; it found nothing new.
  3. Confirmed there is no de-authorisation call site outside `.razor` pages at all:
     `grep -rlnE 'GitTokenService|GitEmailService|InvitationService|LoginService|BootstrapService'`
     over `src/**/*.cs` excluding `Identity/` returns nothing — the only reference to any identity
     service from outside `Identity/` is the `<see cref>` on `AuthenticatedAccount.cs:8` pointing at
     `GitTokenService.VerifyAsync`, a doc link, not a call. `VerifyAsync` itself has no call site
     anywhere in `src` yet (git Smart HTTP is a different, not-yet-implemented capability) — flagging
     only so it isn't mistaken for a missed site; there is nothing there to sweep.

  **Verdict: no fourth de-authorisation site.** Both known non-findings reconfirmed and not reported
  as gaps: `BootstrapStartupExtensions.LogBootstrapStateAsync` (`Program.cs:74`, no token, startup
  path) and `Logout.razor:42` `SignOutAsync` (withdrawal-shaped, no `CancellationToken` parameter, no
  identity-service or store row touched).

**Did not spawn a `reviewer` or any other agent.** Handoff below is a DEVLOG line only.

**Gates**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — 344/344 passed, whole suite, no
filter. `openspec validate request-cancellation --strict` — valid. `dotnet format
--verify-no-changes` — clean. `git status --short -- src` shows exactly the two files in scope
(`Account.razor`, `Invitations.razor`); nothing under `Identity/` or `tests/` touched.

No mutation testing run in this block, per the brief.

→ @reviewer

**[reviewer]** 3.1–3.3 audited (`git diff a2c0db0 -- src` and `-- openspec`). Verdict: **Approve with
nits.**

**Correctness / binding-decision checks.**

- Argument binding confirmed at all three sites by reading the callee signatures directly, not
  assuming from position: `GitTokenService.RevokeAsync(Guid, Guid, CancellationToken = default)`,
  `GitEmailService.RemoveAsync(Guid, Guid, CancellationToken = default)`, and
  `InvitationService.RevokeAsync(Guid, bool, Guid, CancellationToken = default)`. The `Invitations.razor`
  call — the one with the `bool` in the middle — passes `(CallerAccountId, CallerIsAdministrator,
  RevokeInput.InvitationId, CancellationToken.None)`, four positional arguments against four
  parameters in the same order. `CancellationToken.None` binds to `cancellationToken` at all three
  sites, not to something else by position.
- D1/D2 compliance: all three sites now write `CancellationToken.None` explicitly rather than omitting
  the argument, each with a caller-side comment. Matches `design.md`'s D1 (fail-safe direction is to
  *finish*, not stop) and D2 (explicit, not an inherited default) exactly, and the spec's "De-authorisation
  completes regardless of the client" requirement.
- Scope: `git diff a2c0db0 --stat -- src tests openspec` touches exactly `Account.razor` (+11/-3),
  `Invitations.razor` (+7/-3), and `DEVLOG.md`. No test files, no service signatures, nothing under
  `Identity/`. `tasks.md` is untouched (still `[ ]` on 3.1–3.3, correctly left for the Architect).

**3.3 sweep — independently re-derived, different instrument.** The worker's three passes were all
regex over `src`. I used `codegraph_explore`, this repo's indexed semantic call graph (AST/symbol-based,
not pattern-based) — a genuinely different instrument, so an agreement here is not two measurements
sharing a blind spot:

- Queried callers of `GitTokenService`, `GitEmailService`, `InvitationService`, `LoginService`,
  `BootstrapService` as a group: every de-authorisation-shaped method (`GitTokenService.RevokeAsync`,
  `GitEmailService.RemoveAsync`, `InvitationService.RevokeAsync`) resolves to callers confined to
  `Account.razor` / `Invitations.razor` and nowhere else — no test-only or hidden caller, no second
  page.
- Queried `InvitationService`'s methods specifically: `RevokeAsync`, `IssueAsync`, `ListAsync` each
  show **exactly 1 caller**, all in `Invitations.razor`. This is the strongest single data point — a
  call-graph edge count, not a grep hit count, so it isn't fooled by a receiver split across lines or a
  method name reused elsewhere the way a regex whitelist could be.
- Queried the two known non-findings directly and read the actual source (not just grepped for it):
  `Logout.razor:42` calls `context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)` —
  this is `AuthenticationHttpContextExtensions.SignOutAsync`, a framework extension method with **no
  `CancellationToken` overload**, so there is structurally nothing for a request-scoped token to
  reach; it also never touches `db` or any identity service. `BootstrapStartupExtensions.
  LogBootstrapStateAsync` (`Program.cs:74`) is a one-shot startup read (`IsAvailableAsync`) that logs
  and discards its answer — not a write, not de-authorisation. Both exclusions are reasoned, not
  accidental.

**Verdict on 3.3: confirmed independently.** No fourth de-authorisation site exists. My instrument
(semantic call-graph traversal) would have surfaced a fourth caller of any of the three methods, or a
caller of `SignOutAsync`/`LogBootstrapStateAsync` outside their known callers, as a distinct entry in
the blast-radius listing — it found none.

**Nits (not blocking).**

1. `Account.razor:313–319`, `:334–340`, `Invitations.razor:148–153` — the brief asked for the
   pre-existing scoping comment and the new D2 comment to stay "legible as two," not merge into a
   wall. The worker separated them with a bare `//` line rather than a real blank line. Checked this
   codebase's own precedent for that exact syntax: `Program.cs:86–111` uses bare `//` between
   paragraphs to structure **one** long multi-paragraph comment justifying **one** line
   (`app.UseAuthentication()`/`app.UseRouting()`), not to demarcate two independently-authored
   comments answering different questions. Applying the same syntax here produces the same visual
   result — one contiguous, same-colour comment block with an internal paragraph break — which reads
   as one comment on a skim, not two. It is a defensible reuse of house style, but it does not clearly
   satisfy "legible as two" as the brief specifically asked. If the Architect wants unambiguous
   separation, a true blank line (no `//`) between the two would do it; if the multi-paragraph-comment
   reading is fine, no change needed. Worker/Architect call, not a re-implementation demand from me.
2. `Account.razor:338` — the `RemoveEmailAsync` D2 comment reads "same reasoning as `RevokeAsync`
   above," a positional back-reference that rots if the two methods are ever reordered. Low risk: it
   mirrors this file's own pre-existing scoping-comment precedent (`:336`, "the same shape as
   `RevokeAsync` above"), so it's consistent with established local style rather than a new pattern
   introduced by this block. Flagging for awareness, not requesting a change.

**No blockers.** Correctness, argument binding, D1/D2 compliance, and OpenSpec scope all check out on
direct inspection; the 3.3 sweep holds up under an instrument the worker didn't use.

**Approve.**

**[architect]** Taking nit 1, declining nit 2. Nit 1: the reviewer's own precedent check is right —
`Program.cs:86–111`'s bare `//` structures paragraphs *within* one comment, not two independently
authored ones, so the brief's "legible as two" isn't clearly met. → @worker: replace the bare `//`
separator with a real blank line at all three sites; nothing else changes. Nit 2: declined — the
pre-existing sibling comment two lines above `RemoveEmailAsync`'s D2 comment already says "the same
shape as `RevokeAsync` above," so changing only the new comment to avoid the positional reference
would make it diverge from its own neighbour to dodge a hypothetical. Consistency wins; recording the
decision so it isn't rediscovered later as an oversight. Re-run all four gates after the nit-1 edit.

**[worker]** Nit 1 applied. Replaced the bare `//` separator line with an actual blank line between
the pre-existing scoping comment and the new D2 comment at all three sites — `Account.razor`
`RevokeAsync` (`:316`), `Account.razor` `RemoveEmailAsync` (`:337`), and `Invitations.razor`
`RevokeAsync` (`:150`) — so each is now unambiguously two comment blocks either side of a blank line.
No comment text changed, no code line changed. Nit 2 left as-is per the Architect's ruling above — no
edit made.

**Gates re-run**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — 344/344 passed, whole suite,
no filter. `openspec validate request-cancellation --strict` — valid. `dotnet format
--verify-no-changes` — clean (confirms the blank line is not itself a formatting violation).
`git status --short -- src` unchanged in scope: `Account.razor`, `Invitations.razor` only.

→ @architect

**[supervisor]** Section review of §3 — scope `git diff a2c0db0..HEAD` (one commit, `61c482a`; three
call sites, +18/−6 in `src`). Verdict below; **no blockers**. Everything here is a note, and one of
them needs a decision before §4 opens.

**Mutation-residue check first, per CLAUDE.md.** `git diff a2c0db0..HEAD -- src` shows exactly the two
`.razor` files in scope and nothing else; `git status --short -- src` is empty, so there is no
untracked file for the diff to be blind to. Clean. No human-in-the-loop task in §3, so nothing ticked
ahead of a Product Owner confirmation.

### 1. Does the requirement hold end to end, or only at the call site? — it holds, and here is the path

I traced the whole route from POST to committed row for all three sites rather than checking the
argument. Taking `Invitations.razor:154` as the worked case, every step between the request arriving
and the row committing is either awaited inline on the request's own execution path with the token
threaded through unbroken — `BeginWriteLockedTransactionAsync(None)` → `OpenConnectionAsync(None)` →
`BEGIN IMMEDIATE` → `SingleOrDefaultAsync(None)` → `SaveChangesAsync(None)` → `CommitAsync(None)`
(`InvitationService.cs:139–167`, `:402–417`) — or framework machinery that does not forcibly abort
executing user code on a client disconnect. `RequestAborted` is cooperative; nothing in the pipeline
tears the handler down. `GitTokenService.RevokeAsync` (`:102–122`) and `GitEmailService.RemoveAsync`
(`:126–143`) are the same shape without the transaction.

I then looked for the things that could abandon the write *without being a `CancellationToken`
parameter*, which is what the question was actually asking:

- **No cache anywhere.** No `IMemoryCache`, no `IDistributedCache`, no singleton mutable state — the
  only singletons are `TimeProvider`, `IPasswordHasher`, `ISecretTokenGenerator` (`Program.cs:14–16`).
  This matters for the spec's wording specifically: the scenarios say the token "no longer
  authenticates" and the email "no longer resolves", not merely that a column changed.
  `GitTokenService.VerifyAsync` and `GitEmailService`'s resolve both read live rows, so a committed
  revocation *is* de-authorisation, with no second copy of the answer to invalidate.
- **No background or detached work.** No `IHostedService`, no `BackgroundService`, no `Task.Run`, no
  timer, no fire-and-forget. Nothing outlives the request scope, so the scoped `IdentityDbContext`
  cannot be disposed underneath an in-flight `SaveChangesAsync`.
- **No second token source.** The three services read no `IHttpContextAccessor` and there is no
  ambient cancellation in .NET, so `None` at the call site is `None` all the way down.
- **The one live adjacency** — see note N3 below — is that each handler follows its `None` write with
  a `RequestAborted` *read* two lines later (`Account.razor:322`, `:342`, `Invitations.razor:161`).
  On a dead client that read throws `OperationCanceledException`. It throws **after** the commit, so
  the requirement holds; but the ordering is what makes it hold, and nothing states or tests that.

**Answer: end to end, not merely at the call site.** The requirement holds — with the caveat in N2
about what "holds" means for this particular section.

### 2. §1 + §2 + §3 together — one rule, one spelling drift

All 15 sites now exist in final form and I read them as a set. The rule reads as one design: twelve
reads/creates carry `RequestAborted`, three de-authorisations carry `CancellationToken.None`, and each
of the three services opens its `<remarks>` with the identical sentence ("Callers must pass
`CancellationToken.None` here, not the request's own token (D1)") — §1's phrasing and §3's call-site
comments genuinely converge rather than paraphrase each other. No duplicated helper, no dead
scaffolding, no new type, no DI change, no `@rendermode`, no new route. The section adds no abstraction
at all, so there is nothing here to have grown twice. One drift only, at N4.

### 3. The 3.3 sweep — I ran a third instrument, and it is a different kind

The worker's three regex passes and the reviewer's CodeGraph traversal share a starting point: *find
the callers of known identity services*. The blind spot that shape has is a de-authorisation that
never touches one. So I started from the store instead and enumerated **every persistence write in
`src`**, then classified each: 8 `SaveChangesAsync` (`InvitationService:59,165,318`,
`BootstrapService:124`, `GitTokenService:33,118`, `GitEmailService:88,140`), 5 `Add`/1 `Remove`, zero
`ExecuteDeleteAsync`/`ExecuteUpdateAsync`, zero raw `SqliteCommand`/`ExecuteSql*`, zero
`File.Delete`/`Directory.Delete`, zero cookie deletion. Five are creates; three are de-authorisations —
exactly the three §3 handled. The only withdrawal-shaped call that writes nothing is
`Logout.razor:42`'s `SignOutAsync`, already a confirmed non-finding.

**The sweep is sound.** For it to have been wrong, one of these would have had to be true, and none is:
a de-authorisation that withdraws access *without writing an identity row* (evicting a cache, deleting
a file, rotating a secret, invalidating a server-side session) — there is no such state in the app; or
a write reaching the store through a channel none of the three instruments enumerates (raw ADO, a
migration, a hosted service) — there are none. That is the reassuring part. The load-bearing part is
that this rests on a *current property of the codebase*, not on the method — see N6.

### Notes — none blocking, N1 needs a decision before §4 opens

**N1 — ❓ @architect: task 4.3 as worded cannot be satisfied, and will trap its worker.** 4.3 reads
"Revocation completes under an already-cancelled token — the central assertion of this change, one per
de-authorisation path". Taken literally at the service level, that test is red by construction: the
three services **correctly honour** the token they are given, so
`RevokeAsync(accountId, tokenId, new CancellationToken(canceled: true))` throws at
`GitTokenService.cs:108` before it reaches `SaveChangesAsync`. D1's guarantee is a property of the
*caller*, not of the service — which is precisely why §3 is a call-site change. So 4.3 has the same
missing-seam problem 4.4 had, but the Product Owner's resolution for 4.4 (service level, empty store)
does **not** transfer: at the service level 4.3 asserts the opposite of the requirement. A §4 worker
who inherits "4.4 goes service-level" will reasonably extend it to 4.3, watch the test go red, and
have three bad options — weaken the assertion to something vacuous, reach for a page-level seam the §2
supervisor already established does not exist, or "fix" the services to ignore their token (which
contradicts design.md's Non-Goal on signatures and would make the parameter a lie). This is a
spec/task question, not an implementation one, and it is cheaper to settle now than mid-block.

**N2 — §3 has no behavioural delta, and that changes what §4 can prove.** Before `61c482a` the three
call sites omitted the argument, inheriting `CancellationToken cancellationToken = default` — which
*is* `CancellationToken.None`. Runtime behaviour is identical before and after this commit. That is
not a criticism: it is exactly what D2 says it is doing, converting an accident into a decision, and
the requirement was satisfied by accident before and is satisfied on purpose now. But the consequence
is sharp — **no behavioural test at any level can distinguish pre-§3 from post-§3 code**, so §4.5's
source-level sweep ("no page passes a request-scoped token to a de-authorisation call") is not a tidy
extra at the end of §4: it is the *only* mechanical evidence that §3's work exists, and the only thing
that will fail when a future consistency pass undoes it. It deserves to be briefed as the section's
primary test, not its last one. It is also the natural home for whatever 4.3 becomes once N1 is
settled.

**N3 — the highest-erosion-risk adjacency in the change is the one D2 does not annotate.** Each of the
three handlers is a 6–8 line method containing both `CancellationToken.None` and, two lines below,
`Context.RequestAborted` (`Account.razor:320/322` and `:340/342`, `Invitations.razor:158/161`). The D2
comment explains the write; nothing explains why its immediate neighbour differs. That is the precise
shape a "let's make these consistent" pass reaches for, and it is the pass D2 exists to defend against.
Separately, the *order* of those two lines is load-bearing for the requirement (the commit must precede
the read that throws on a dead client) and is stated nowhere. No change requested for §3 — the comments
are correct and the Architect's nit rulings stand — but both are reasons N2's 4.5 sweep matters more
than its position in the task list suggests.

**N4 — one spelling drift across the section boundary.** `RedeemInvitation.razor:116` and `:125` are
the only two of the 15 sites whose token is spelled `default` (`HttpContext?.RequestAborted ?? default`)
— and both carry a comment naming `CancellationToken.None`, which the code does not say. Semantically
identical; legibility is D2's entire argument, and `default` is the spelling D2 rejects. §2's lines,
approved under §2, and a one-word fix — recording it rather than requesting it, so a later tidy pass
has it written down instead of rediscovering it.

**N5 — the sweep's soundness expires with `git-backed-content-core`.** 3.3 holds because *every* grant
of access in this app is a SQLite row, every write to one goes through `Identity/`, and there is no
cache, no background work, and no non-EF persistence. The content capability breaks all three: files on
disk, git refs, a cross-process `flock`, and a Smart HTTP endpoint that will be the first caller
`GitTokenService.VerifyAsync` has ever had. D1 must be re-applied there rather than reinvented (design.md
already says so), and 3.3's sweep must be re-run with an instrument that starts from the *store*, not
from `Identity/`'s callers — the caller-side instruments will simply not see a git-side de-authorisation.

**Approve.**

**For `## NEXT`:**

- **Settle N1 before briefing §4** — task 4.3 is untestable as written at the service level, and the
  4.4 precedent does not transfer. Product Owner / Architect call: reword 4.3 as a source-level
  assertion (fold into 4.5), accept it as covered by comment + `<remarks>` only, or make D1 structural
  by dropping the token parameter from the three de-authorisation methods — the last contradicts
  design.md's Non-Goal and is proposal-level.
- **Brief §4.5 as this section's primary test, not its last** (N2) — it is the only evidence §3's work
  exists, since §3 has no behavioural delta. Both directions: no de-authorisation site carries a
  request-scoped token, no read/create site omits one. Expect 15 sites: 12 + 3.
- **N3's adjacency** — each de-auth handler holds a `None` write and a `RequestAborted` read two lines
  apart, and the commit-before-read ordering is load-bearing but unstated. No action for §3; a reason
  4.5 matters.
- **N4** — `RedeemInvitation.razor:116,125` spell the fallback `default` while their comments say
  `CancellationToken.None`; one-word alignment for a later tidy pass, not a fix block.
- **N5** — `git-backed-content-core` must re-apply D1 *and* re-run 3.3's sweep from the store side; the
  caller-side instruments used here will not see a git-side de-authorisation.

## 4. Tests

**[architect]** Base: `7a4d6e1` — the change's evidence: that cancellation is honoured where D1 says
it should be, and that the de-authorisation split is real rather than decorative.

**[architect]** Product Owner rulings opening §4, both taken before any code was written.

**Task 4.3 is re-cut** (committed `7a4d6e1`; `spec.md` untouched). The §3 supervisor found 4.3 red by
construction: at the service level, `RevokeAsync(accountId, tokenId, cancelled)` correctly throws at
`GitTokenService.cs:108` before `SaveChangesAsync`, so asserting "revocation completes under an
already-cancelled token" there asserts the *opposite* of the requirement. D1's guarantee is a property
of the **caller**. The requirement itself is unchanged and does hold end to end — the §3 supervisor
traced POST → committed row. What changed is how it is proven, into two halves:

- **4.3** — each service *throws* under a cancelled token, proving the parameter is live.
- **4.5** — every caller passes `CancellationToken.None`, proving the callers use it correctly.

Neither half means much alone. 4.3 without 4.5 proves a parameter nobody uses correctly; 4.5 without
4.3 proves callers pass `None` to a parameter that might be ignored. **That is why they are one
block.**

**§4 is two blocks** (PO): **A = 4.1, 4.2, 4.4** — cancellation *is* honoured on reads and creates,
the §2 side. **B = 4.3 + 4.5** — the de-authorisation split, the §3 side.

**[architect]** Brief — block A, tasks 4.1/4.2/4.4 → @worker.

**The tasks.**

- **4.1** A cancelled create leaves no record — **assert against the store, not the return value.**
  The return value of a cancelled call is not evidence; the absence of the row is.
- **4.2** A cancelled redemption leaves the invitation still redeemable — not merely "not redeemed",
  but *usable*: a subsequent redemption under a live token must succeed.
- **4.4** A cancelled bootstrap availability check *fails* rather than reporting the store empty.

**4.4 — the seam and the polarity, both already settled. Do not re-open either.**

*Seam (PO ruling):* assert at the **service level** in `BootstrapServiceTests.cs`, against an
**empty** store. No interface, no unsealing — `BootstrapService` is `sealed` (`BootstrapService.cs:13`)
and DI-registered by concrete type (`Program.cs:19`), the test project has no mocking or
component-render harness, and a cancelled HTTP request yields no response to assert against. A
page-level seam is a proposal-level change and is not §4's to invent.

*Polarity — read this twice, it is the one fail-open path in the change.* `IsAvailableAsync` is
`!await db.Accounts.AnyAsync(…)`. So **`true` = store empty = bootstrap OPEN** (the permissive value),
`false` = closed. **Failing open is returning `true`.** The empty-store setup is load-bearing: against
an empty store the honest answer *is* `true`, so an assertion that the cancelled call **throws**
distinguishes *throw* from *fail open*. Against a populated store it would only distinguish throw from
*fail closed* — passing while proving nothing. `design.md` stated this backwards until `0a38e46`;
task 4.4's own wording was always right.

**4.1 rests on ground the §2 supervisor already verified**, so trust it rather than re-deriving:
`BootstrapService.cs:104–128` rolls back safely because cancellation between `SaveChangesAsync(ct)`
and `CommitAsync(ct)` throws *pre-commit* into a **token-less** `await using` rollback. Relatedly,
`InvitationService.WriteLock.DisposeAsync()` (`InvitationService.cs:436–440`) takes no token and
**must not** — that rollback path is *why* 4.1 and 4.2 hold. It is not a §1.2 counter-example.

**Scope.** Tests only — `tests/ZeroWiki.Tests`. **No production code changes at all.** If a test cannot
be written without touching `src`, that is a finding: **stop and report it, do not add a seam.** Block
B (4.3 + 4.5) is not yours; do not write it or pre-empt it.

**Constraints.**

- **Do not spawn a `reviewer`, or any other agent.** Your handoff is a `→ @reviewer` line here; the
  Architect commissions the review. (§2's worker commissioned its own — an audit the audited party
  arranged.)
- **No mutation testing in this block.** CLAUDE.md caps it to security- and correctness-critical paths
  and it is easy to run past usefulness. 4.4 is the one assertion where it could be argued — and the
  answer is still no for now. If you believe a specific mutant is the only way to settle a specific
  doubt, **say so and stop**; do not run it.
- **Do not commit. Do not tick boxes in `tasks.md`.** The Architect does both.
- Post to this DEVLOG as you go, attributed `**[worker]**`, referencing `4.1/4.2/4.4`.

**Done-gates** — run all four and report actual output: `dotnet build` clean; `dotnet test` green,
**full suite, never a filter** (currently 344 — yours should raise it); `openspec validate
request-cancellation --strict`; `dotnet format --verify-no-changes`.

**[worker]** 4.1/4.2/4.4 done. Tests only — `git diff --stat -- src tests` touches exactly
`tests/ZeroWiki.Tests/Identity/BootstrapServiceTests.cs` (+40) and
`tests/ZeroWiki.Tests/Identity/InvitationRedemptionTests.cs` (+30); nothing under `src`. Did not
need to touch production code — no seam was missing for any of the three.

All three tests use an **already-cancelled token** (`new CancellationToken(canceled: true)`)
rather than an injected mid-flight cancel. Traced each method by hand first: in all three, the
first cancellable `await` (`AnyAsync`/`SingleOrDefaultAsync`) sits before any connection, write
lock, or row is touched, so a pre-cancelled token throws before anything begins — a stronger
"leaves nothing behind" than a mid-flight cancel would prove, not a weaker one. Each assertion
also checks `thrown.CancellationToken.IsCancellationRequested` to confirm the throw came from the
token being honoured, not merely from something else that happened to also throw. Confirmed the
whole suite went 344 → 347 green, not just the three new tests in isolation.

- **4.1 `BootstrapServiceTests.cs` — `A_cancelled_bootstrap_leaves_no_administrator_behind`.**
  Calls `CreateFirstAdministratorAsync` with a cancelled token, asserts it throws
  `OperationCanceledException`, then asserts `_db.Accounts.AsNoTracking().ToListAsync()` is
  **empty** — the store, not the discarded exception or a return value that doesn't exist for a
  throwing call. Rests on `BootstrapService.cs:104–128`'s already-verified rollback (§2
  supervisor) rather than re-deriving it; this test proves the store-level outcome the spec
  scenario actually asks for.
- **4.2 `InvitationRedemptionTests.cs` — `A_cancelled_redemption_leaves_the_invitation_still_redeemable`.**
  Issues an invitation, calls `RedeemAsync` with a cancelled token, asserts the throw, then
  asserts against the store: no account beyond the issuer, and the invitation's `RedeemedAt`
  **and** `RevokedAt` are both still null. Then goes one step further than "untouched" — it
  **redeems the same token again** with a live token and asserts `InvitationRedemption.Redeemed`,
  a fresh "bob" account existing, and `RedeemedAt` now set. That second call is what proves
  *usable*, not merely *not consumed*: a store-only assertion after the cancelled call couldn't
  rule out some invisible poisoning (e.g. a half-tracked entity) that only a real subsequent
  redemption would surface.
- **4.4 `BootstrapServiceTests.cs` — `A_cancelled_availability_check_throws_rather_than_reporting_the_store_empty`.**
  Runs `IsAvailableAsync` against the **empty** store the constructor gives every test (no
  `AddAccountAsync` call), with a cancelled token, and asserts it throws
  `OperationCanceledException`. The empty store is what makes this mean something: against an
  empty store the honest, uncancelled answer is `true` (`IsAvailableAsync` is `!AnyAsync`, and
  `true` = store empty = bootstrap **open** — the fail-open value per `design.md`'s corrected
  Risks item). A throw here therefore distinguishes *throw* from *fail open* (`true`). Had this
  been asserted against a populated store instead, the same assertion would only distinguish
  *throw* from *fail closed* (`false`) — passing while proving nothing about the one fail-open
  path in the change. Did not touch the polarity language or re-litigate it; used it as given.

**No mutation testing run**, per the brief — 4.4 was the one place it could have been argued for,
and the brief's own answer (no, for now) stood; no specific mutant seemed like the only way to
settle a doubt, so none was proposed.

**Gates**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **347/347 passed**, full suite,
no filter (was 344; +3). `openspec validate request-cancellation --strict` — valid. `dotnet format
--verify-no-changes` — clean. `git status --short -- src` empty; `git diff --stat -- src tests`
confirms exactly the two test files above.

→ @reviewer

**[reviewer]** Verdict on block A (4.1/4.2/4.4): **Request changes** — 4.1 and 4.2. 4.4 is
correct as written.

Read `git diff 7a4d6e1 -- tests` (confirmed `git diff 7a4d6e1 -- src` is empty — tests-only, as
claimed), `design.md` D1 and the Risks item, `specs/request-lifecycle/spec.md`'s bootstrap and
invitation scenarios, the §4 brief and worker's post above, `BootstrapService.cs` and
`InvitationService.cs` in full, and the test conventions in `BootstrapServiceTests.cs` /
`InvitationRedemptionTests.cs`.

**Central question, traced by hand rather than taken on the worker's word: does each test fail
when the property it exists to guard is broken?**

**4.4 — confirmed sound, no changes.** `IsAvailableAsync` (`BootstrapService.cs:30–31`) has
exactly one cancellable operation — `!await db.Accounts.AnyAsync(cancellationToken)` — so there is
no "before" distinct from "during" for this method; pre-cancelling is the only meaningful way to
test it, not a weaker substitute for a mid-flight cancel. The empty-store setup is load-bearing,
and I re-derived why independently rather than trusting the brief: against a populated store the
honest answer is already `false` (closed) whether or not cancellation is honoured, so the
*dangerous* value (`true` = open) is only reachable at all when the store is empty — only there
does "assert it throws" actually distinguish *throw* from *fail open* rather than merely from
*fail closed*. This is a real mutant-killing assertion: if `AnyAsync`'s cancellation token were
dropped (a realistic regression — e.g. someone "simplifies" the query and forgets to pass it
through), the call would run to completion against the empty store and return `true`; the test
would then see no throw and fail correctly. Confirmed by tracing the method, not by running a
mutant (per the block's brief and CLAUDE.md's cap, no mutation testing was run). **Approve, no
changes.**

**4.1 and 4.2 — the worker's comment has the polarity of its own claim backward, and the tests as
written cannot fail when the property they cite is broken.**

Traced both methods by hand:

- `BootstrapService.CreateFirstAdministratorAsync` (`BootstrapService.cs:66–128`): the first
  cancellable await is the advisory pre-filter `await db.Accounts.AnyAsync(cancellationToken)`
  (`:81`) — before `passwordHasher.Hash`, before `OpenConnectionAsync`, before the transaction is
  even opened. A pre-cancelled token throws there.
- `InvitationService.RedeemAsync` (`:219–…`): the first cancellable await is
  `RejectionAsync(tokenHash, now, cancellationToken)` (`:260`), an `AsNoTracking()` read — before
  `passwordHasher.Hash`, before `BeginWriteLockedTransactionAsync`. A pre-cancelled token throws
  there.

In both cases the transactional path — `SaveChangesAsync(ct)` → `CommitAsync(ct)` inside
`await using (transaction)`, whose disposal is what rolls back on an early exit
(`WriteLock.DisposeAsync`, `InvitationService.cs:436–440`, takes no token and must not) — is never
entered. `Assert.Empty(await _db.Accounts...)` in 4.1, and the untouched-invitation assertions in
4.2, are therefore true by construction: nothing ran that could have written a row, so nothing
needed to be rolled back for the assertion to pass. **I verified this is not a hypothetical
concern by testing the actual mechanism** (see below) — the transactional rollback these tests
cite as their evidence is provably outside the code path a pre-cancelled token exercises.

`BootstrapServiceTests.cs:116–121`'s comment claims the opposite: "this also exercises the
'leaves nothing behind' claim in its strongest form: nothing was ever begun." That has the
direction backward. A case where nothing was ever begun is the *weakest* form of the claim — it is
vacuously true and requires no mechanism to hold. The claim is at its *strongest*, and the only
place it is actually at risk, when something *was* begun and had to be undone — i.e. mid-flight,
between `SaveChangesAsync` and `CommitAsync`. `InvitationRedemptionTests.cs:82–84`'s comment makes
the same claim in gentler language ("nothing about redemption is begun") but doesn't assert the
"strongest form" framing, so it is descriptively accurate even though the test built on it has the
same evidentiary gap.

**Concretely: would 4.1 catch a broken rollback?** No. If the `await using (transaction)` block's
disposal path in `CreateFirstAdministratorAsync` silently committed instead of rolling back on a
cancelled `CommitAsync` — or if `CommitAsync` didn't propagate the token at all — this test would
still pass 3/3, because it never reaches that code. The task text (4.1: "assert against the store,
not the return value") and the spec scenario ("WHEN a client disconnects **while** a request is
creating an account … THEN the operation is abandoned") are both about a request that was in
flight, not one that never left the gate. The current test proves the pre-filter's cancellation is
honoured (a real, useful, but different property) and nothing about the rollback the comment
credits it with.

**Feasibility spike — a mid-flight test is achievable without touching `src`, and I built and ran
one to confirm rather than assert it in the abstract.** Both test classes already construct their
own `DbContextOptionsBuilder<IdentityDbContext>()` locally (`BootstrapServiceTests.cs:28–29`,
`InvitationRedemptionTests.cs:39–43`) — `IdentityDbContext` has no `OnConfiguring` override to
fight, so nothing stops a test from calling `.AddInterceptors(...)` on its own builder. I wrote a
throwaway console spike (outside this repo, `ProjectReference`d against `src/ZeroWiki`, never
touching any tracked file) implementing an `ISaveChangesInterceptor` whose `SavedChangesAsync`
override calls `cts.Cancel()` on the `CancellationTokenSource` supplying the token passed into
`CreateFirstAdministratorAsync`. `SavedChangesAsync` fires after `SaveChangesAsync` has written the
row into the (uncommitted) transaction but before the production code's subsequent
`await transaction.CommitAsync(cancellationToken)` runs. Result, empirically observed:

```
[interceptor] SavedChangesAsync (write committed to txn, not yet COMMITted) -- cancelling now
THREW: System.Threading.Tasks.TaskCanceledException: A task was canceled.
Account rows after cancellation attempt: 0
Result: MID-FLIGHT CANCEL SIMULATED SUCCESSFULLY, ROLLED BACK
```

`CommitAsync(cancellationToken)` sees the token already cancelled and throws before actually
committing (SQLite `DbTransaction.CommitAsync`'s default cancellation check, not overridden by
Microsoft.Data.Sqlite), so `await using` disposes into the same rollback path 4.1 currently never
reaches — and the row count confirms it rolled back. **This is a genuine yes, with a concrete
mechanism**, not "write a better test" hand-waved: a per-test-local `IdentityDbContext` +
`SaveChangesInterceptor`, armed only for that one test (each test class already builds a fresh
instance per xUnit test, so a shared always-on interceptor isn't needed — the new test can build
its own local connection/context/service the way the class constructor already does, scoped to
just that method). The same shape applies to 4.2: `InvitationService.RedeemAsync` has the
identical `SaveChangesAsync(ct)` → `writeLock.CommitAsync(ct)` shape (around `:316–317`), so the
same interceptor cancels between the same two calls.

Given that this is achievable, the honest fix is not merely to soften the comment — the comment
*and* the test are both weaker than the evidence standard this change has held itself to
elsewhere (§2's and §3's supervisors traced code paths and verified mechanisms rather than
asserting from the shape of the code). I'd rather see the mid-flight variant added.

**Findings.**

1. `BootstrapServiceTests.cs:114–132` (`A_cancelled_bootstrap_leaves_no_administrator_behind`) —
   add a second case (or replace this one) that cancels **between `SaveChangesAsync` and
   `CommitAsync`** via a `SaveChangesInterceptor` on a locally-built `IdentityDbContext`, asserting
   the same throw + empty-store outcome. This is the only case where the assertion can actually
   fail if the rollback breaks. If the pre-cancelled variant is kept alongside it (reasonable — it
   does cover the pre-filter's own cancellation-honouring, a real if smaller property), rewrite the
   comment: it is the *weakest*, not the *strongest*, form of the claim, and say so plainly rather
   than asserting the opposite.
2. `InvitationRedemptionTests.cs:81–107` (`A_cancelled_redemption_leaves_the_invitation_still_redeemable`)
   — same fix: add the mid-flight variant using the same interceptor mechanism against
   `RedeemAsync`'s `SaveChangesAsync`/`writeLock.CommitAsync` pair, and correct the comment's
   "nothing about redemption is begun" framing so it doesn't stand in as evidence for the
   transactional claim the task (4.2) and spec scenario are actually asking about.

**Not blocking, confirmed clean:**

- **Scope.** `git diff 7a4d6e1 -- src` is empty — tests only, exactly as claimed.
  `git status --short -- src` is also empty, so there's no untracked-file blind spot on the `src`
  side either.
- **Block B not pre-empted.** Only the three named tests were added; nothing touches
  `GitTokenService.RevokeAsync`, `GitEmailService.RemoveAsync`, or
  `InvitationService.RevokeAsync` (4.3/4.5's territory), and `RedeemInvitation`/de-authorisation
  page code is untouched (`git diff -- src` empty confirms this too).
- **No `reviewer`/other agent spawned by the worker** — the handoff is a plain `→ @reviewer` line,
  correctly.
- **No mutation testing run** — correct per the brief; my feasibility spike above is not a
  mutation run (no mutant was introduced into `src` or reverted, nothing was "broken to see if a
  test dies" — it's a throwaway console program outside the repo, `ProjectReference`d read-only
  against `src/ZeroWiki`, verifying an *instrument* for the worker to use, not exercising one
  against a mutated property). Flagging this distinction explicitly since CLAUDE.md caps mutation
  testing tightly: this spike answered "is the seam reachable at all," not "does a specific mutant
  survive."
- **Idiom/convention.** Test naming (`A_cancelled_..._...`) matches the file's existing
  sentence-style convention; `Assert.ThrowsAnyAsync<OperationCanceledException>` plus asserting
  `thrown.CancellationToken.IsCancellationRequested` is a reasonable, if slightly unusual for this
  file, way to confirm the throw came from the token rather than an unrelated failure — no
  objection. 4.2 correctly reuses the existing `IssueAsync()`/`AssertNoAccountBeyondTheIssuerAsync()`
  helpers rather than inventing new setup.
- **No design-decision violations** — D1's split (reads/creates cancel) is what's under test; D2
  (de-authorisation) is untouched, correctly, by this block.

**Verdict: Request changes** on 4.1 and 4.2 per the two findings above. 4.4 stands as-is.

→ @worker

**[worker]** Addressed both findings — 4.4 untouched, per the verdict.

**Agreed with the finding.** The pre-cancelled tests' comments claimed the "strongest form" of
"leaves nothing behind"; that was backwards. Re-traced both methods: `CreateFirstAdministratorAsync`'s
first cancellable await is the pre-filter `AnyAsync` (`BootstrapService.cs:81`), `RedeemAsync`'s is
`RejectionAsync`'s read (`:260`) — both before any connection or write lock, so a pre-cancelled
token never reaches the `SaveChangesAsync → CommitAsync` window and neither test could have failed
if that rollback were broken. Kept both pre-cancelled tests — they still establish something true
(cancellable at all; an early cancel leaves nothing behind) — but rewrote the comments to say
exactly that and no more, and to point at the new mid-flight test that reaches what they cannot.

**New shared test helper**: `tests/ZeroWiki.Tests/Identity/CancelAfterSaveInterceptor.cs` — a
`SaveChangesInterceptor` whose `SavedChangesAsync` override cancels a `CancellationTokenSource` the
instant `SaveChangesAsync` finishes writing into the still-open transaction, landing the
cancellation right before the production code's own `CommitAsync(cancellationToken)` call — the
same mechanism the reviewer's spike confirmed reachable. One file, matching this folder's existing
convention of a small standalone test-helper class per file (`SteppingTimeProvider.cs`,
`CountingPasswordHasher.cs`, `CapturingLoggerProvider.cs`) rather than duplicating it in both test
classes.

**Two new mid-flight tests, one per path**, each building its own `IdentityDbContext` on the
class's shared `_connection` with the interceptor registered on that context's own
`DbContextOptionsBuilder`, and its own service instance constructed against it — the class-level
`_db`/`_service` fixtures are untouched, so no other test in either file is affected:

- **`BootstrapServiceTests.A_cancellation_between_the_write_and_the_commit_still_rolls_back`** —
  cancels between `SaveChangesAsync` and `CommitAsync` (`BootstrapService.cs:124–125`). Asserts the
  call throws `OperationCanceledException` with `CancellationToken.IsCancellationRequested` true,
  then asserts `_db.Accounts.AsNoTracking().ToListAsync()` is empty — read through the *original*
  `_db`, sharing `_connection` with the intercepting context, not the intercepting context's own
  (disposed) tracker.
- **`InvitationRedemptionTests.A_cancellation_between_the_write_and_the_commit_still_rolls_back_and_stays_redeemable`** —
  same shape, cancels between `SaveChangesAsync` and `CommitAsync` (`InvitationService.cs:318–319`).
  Asserts the throw, asserts against the store that no account exists and the invitation carries
  neither `RedeemedAt` nor `RevokedAt`, then keeps 4.2's "usable, not merely untouched" property:
  redeems the same token again under a live call and asserts it succeeds.

**Why a broken rollback would now make these fail, reasoned rather than run** (no mutation
testing, per the constraint — the seam is already proven reachable, and nothing here is in enough
doubt to need a mutant to settle it): two distinct breakages, both caught.

1. If `CommitAsync` did not honour the token (committed unconditionally), the call would return
   `Created`/`Redeemed` instead of throwing, and `Assert.ThrowsAnyAsync<OperationCanceledException>`
   would fail outright — never reaching the store assertion.
2. If the token-less `await using` disposal on cancellation failed to roll back (e.g. discarded the
   transaction without issuing `ROLLBACK`), the write would still be visible to the store
   assertion: `_db` and the intercepting context share one physical SQLite connection
   (`_connection`), and SQLite allows only one transaction per connection, so an unrolled-back
   write is read-your-own-writes visible to the very next query on that connection — exactly the
   query `Assert.Empty`/`Assert.Null` runs. There is no way for an unrolled-back write to hide from
   these assertions on this fixture.

**Gates re-run**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **349/349 passed**, full
suite, no filter (was 347; +2). `openspec validate request-cancellation --strict` — valid.
`dotnet format --verify-no-changes` — clean. `git diff --stat -- src` empty;
`git status --short --untracked-files=all -- src tests` shows exactly the two modified test files
plus the one new untracked helper (`CancelAfterSaveInterceptor.cs`) — nothing under `src`.

No agent spawned. No boxes ticked. No commit made.

→ @reviewer

**[reviewer]** Second pass on block A (4.1/4.2/4.4). **Approve.**

Read `git diff 7a4d6e1 -- tests` (confirmed `git diff 7a4d6e1 -- src` still empty, `wc -l` = 0) and
`git status --short --untracked-files=all -- src tests`, which shows exactly the two modified test
files plus `CancelAfterSaveInterceptor.cs` as `??`. Per CLAUDE.md's own warning, read that file
directly rather than trusting the `??` — it is 29 lines, one `sealed class`, file-scoped namespace,
matches the file's own name, cancels a supplied `CancellationTokenSource` from `SavedChangesAsync`
and forwards to `base`. Nothing else in it.

**1. Does the mid-flight test fail if the rollback is broken? Yes — and I didn't take the
shared-connection premise on report, I built and ran a control experiment to isolate it from the
"does the interceptor even land the cancellation in the right place" question.**

Two separate things have to both be true for this test to have teeth: (a) the interceptor lands
the cancellation in the SaveChangesAsync→CommitAsync window, and (b) a query issued through a
*different* `DbContext` instance than the one that wrote can actually observe that context's
uncommitted state — otherwise the assertion queries a connection that never saw the write and
passes for a reason that has nothing to do with rollback. I tested these independently, outside
the repo (a throwaway console spike, `ProjectReference`d read-only against `src/ZeroWiki`,
never touching a tracked file — confirmed `git status` clean throughout, same discipline as my
first pass):

- **Reproduced the worker's exact shape** — one shared, already-open `SqliteConnection`; a
  `db`-equivalent context built once (matching `_db`); a second, locally-built context wrapping
  the *same* connection with the interceptor attached (matching `interceptingDb`) — and ran the
  real call. Result: threw `TaskCanceledException`, and `db` (the other context) read **0 rows**
  afterward.
- **The control that actually settles (b), separately from (a):** built two contexts on one shared
  connection by hand, had one open a write-locked transaction and `SaveChangesAsync` a row
  *without* committing or rolling back yet, then read through the *other* context **while the
  transaction was still open**. It saw **1 row** — the uncommitted write, live, through a
  different `DbContext` object. Then disposed the first context's transaction without committing
  (the rollback path) and re-read through the second context: **0 rows**. This is the load-bearing
  result: it proves that if the production code's rollback were broken and the row survived, the
  test's `Assert.Empty`/`Assert.Null` — which read through `_db`, not `interceptingDb` — **would
  see it**, because a second EF Core `DbContext` sharing one physical `SqliteConnection` is not an
  isolated view; it is the same session, same uncommitted transaction state, same
  read-your-own-writes guarantee a single context gets. The premise holds because it is a property
  of SQLite having one native transaction per connection handle, not of anything EF-specific that
  could silently change. **Confirmed, not assumed: the assertion is not vacuous.**
- I did not need to introduce an actual mutant into `src` to settle this — the control experiment
  isolates the visibility question from the rollback-correctness question without touching
  production code at all, which is the cheaper and sufficient way to answer it. No mutation testing
  run, per the brief.

**2. Does the interceptor leak? No — checked three ways.** `interceptingDb`'s options are built
fresh, locally, inside the test method (`new DbContextOptionsBuilder<IdentityDbContext>()...`);
nothing about `.AddInterceptors(...)` touches `_db`'s already-built options or any static/shared
state. The `CancellationTokenSource` is a local as well. Structurally, `BootstrapServiceTests` and
`InvitationRedemptionTests` are both plain `IDisposable` classes with no `IClassFixture`/
`ICollectionFixture` — xUnit gives every test method a fresh class instance (fresh constructor,
fresh `_connection`), so there is no path by which one test's interceptor could reach another
test's context regardless. Within the same method: I verified empirically that disposing
`interceptingDb` (the `await using` at the end of the mid-flight tests) leaves the shared
`SqliteConnection`'s `.State` as `Open` and `_db` fully queryable afterward — EF Core does not
take ownership of a connection instance handed to `UseSqlite(existing)`, so disposing the context
that borrowed it does not close it out from under the class's own `_db`/`Dispose()`. No leak.

**3. Are the corrected comments accurate, not overshot?** Read all four comment blocks against
what each test actually exercises. The pre-cancelled tests now say they prove "the method is
cancellable at all" and "an early cancel leaves nothing behind, and no more than that" — both true
and both real, useful properties (the pre-filter's own cancellation-honouring is not nothing), so
this isn't an understatement dressed as humility; it correctly scopes the claim to what the test
can actually see, distinct from the transactional claim it explicitly hands off to the mid-flight
test. The mid-flight tests' comments ("this test, unlike the pre-cancelled one, would see it and
fail") are exactly what I verified above. No overshoot either direction.

**4. 4.4 — confirmed untouched.** `A_cancelled_availability_check_throws_rather_than_reporting_the_store_empty`
is byte-identical to my first pass, merely relocated after the new mid-flight test in the diff.
Approved previously; nothing to re-litigate.

**5. Standard lens.** `git diff 7a4d6e1 -- src` is empty (confirmed independently, `wc -l` = 0) —
tests only, plus the one new test-only file. Block B untouched: no reference to `RevokeAsync`,
`RemoveAsync`, or any de-authorisation call site anywhere in the diff or the new file. C# idiom:
`CancelAfterSaveInterceptor` is `sealed`, file-scoped namespace, file name matches the type,
primary-constructor syntax matching `BootstrapService`/`InvitationService`'s own style, no
null-forgiving operators, no sync-over-async. New test method names
(`A_cancellation_between_the_write_and_the_commit_still_rolls_back[_and_stays_redeemable]`) match
the file's existing sentence-style convention. `_service`/`_db` used for the "live re-redemption"
follow-up and `AssertNoAccountBeyondTheIssuerAsync()`, correctly, not `interceptingDb`/
`interceptingService`.

**Gates — independently re-run, not taken on trust:** `dotnet build` — 0 warnings, 0 errors.
`dotnet test` — **349/349 passed**, full suite, no filter. `dotnet format --verify-no-changes` —
clean. `openspec validate request-cancellation --strict` — valid. All four match the Architect's
and worker's reported numbers exactly.

**No findings. No nits. Approve.**

→ @architect

**[architect]** Block A closed — reviewer `Approve` on the second pass, gates green under my own run
(build 0/0, 349/349 full suite, validate valid, format clean), 4.1/4.2/4.4 ticked, committed `7d4e20b`.
Tree verified clean of spike residue before commit: `git diff -- src` empty, and the one untracked file
(`CancelAfterSaveInterceptor.cs`) read directly rather than inferred from a clean diff, per CLAUDE.md's
new-file hazard.

**[architect]** Brief — block B, tasks 4.3 + 4.5 → @worker. **The last block in the change.**

**Why these two are one block.** They are the two halves of a single argument, and each is nearly
worthless alone:

- **4.3** — each de-authorisation service *throws* under an already-cancelled token. Proves the
  `CancellationToken` parameter is **live**.
- **4.5** — every caller passes `CancellationToken.None` to de-authorisation, and none omits a token
  from a read or create. Proves the callers **use it correctly**.

4.3 without 4.5 proves a parameter nobody uses correctly. 4.5 without 4.3 proves callers pass `None` to
a parameter that might be ignored — if the services silently dropped their token, the sweep would still
be green and the whole D1/D2 apparatus would be decorative. **Together** they give the requirement.
Review them as one argument.

**4.3 — assert the throw, and mind that this is deliberately the opposite of D1's guarantee.**

One test per de-authorisation path: `GitTokenService.RevokeAsync`, `InvitationService.RevokeAsync`,
`GitEmailService.RemoveAsync`. Each: call with an already-cancelled token, assert it **throws**.

This task was **re-cut by the Product Owner** (`7a4d6e1`) because its original wording — "revocation
completes under an already-cancelled token" — is **false at the service level by design**. The services
correctly honour their token, so `RevokeAsync(accountId, tokenId, cancelled)` throws at
`GitTokenService.cs:108` before `SaveChangesAsync`. D1's guarantee that revocation survives a
disconnect is a property of the **caller**, which passes `None`. Do not try to assert "revocation
completes" here — that would assert the opposite of the requirement and there is no seam for it. If you
find yourself wanting to make a service ignore its token, stop: `design.md` lists service changes as an
explicit Non-Goal.

Note the deliberate asymmetry, and do not "fix" it: these services **should** be cancellable, and 4.3
proves they are. The de-authorisation guarantee comes entirely from the call sites §3 made explicit.

**4.5 — the sweep, and per N2 the *primary* test of this whole change, not its afterthought.**

§3 changed **no runtime behaviour**. The omitted arguments it replaced already bound to
`CancellationToken.None` via the services' `= default`, so pre-§3 and post-§3 code are behaviourally
identical and **no behavioural test at any level can distinguish them**. 4.5 is the only mechanical
evidence §3's work exists. Both directions:

- No page passes a request-scoped token to a de-authorisation call (the 3 sites §3 made explicit).
- No page omits a token from a read or create (the 12 sites §2 filled in).

15 sites total. Decide and state in the DEVLOG what this test actually reads — the `.razor` sources, or
something else — and **why that instrument would catch a regression**. A sweep that cannot fail is
worse than no sweep, because it reads as coverage. Consider deliberately: would your test fail if
someone changed a `CancellationToken.None` back to `Context.RequestAborted`? If not, it is not a test.

**Check your instrument before believing it** — CLAUDE.md's standing rule, and this block is exactly
where it bites. Two agents once corroborated each other while both were wrong, because both anchor
regexes required `href="…"` while Blazor renders `href=""` bare. Test whatever pattern you measure with
against known-present *and* known-absent markup first, and say in the DEVLOG that you did.

**Known non-findings** — confirmed by three supervisors; do not report as gaps:
`BootstrapStartupExtensions.LogBootstrapStateAsync` (`Program.cs:74`, startup path, no token);
`Logout.razor:44` `context.SignOutAsync(...)` (withdrawal-shaped, no token, no store row).

**Also live, and yours to handle sensibly:** `RedeemInvitation.razor:116,125` spell the token `default`
while their own comments say `CancellationToken.None` (note N4). They are reads/creates from §2, so
they are *correct* — but a naive text sweep looking for the literal `CancellationToken.None` will
mis-handle them. Make the sweep robust to that rather than papering over it, and say which you did.

**Scope.** Tests only — `tests/ZeroWiki.Tests`. **No production code changes**, including no "tidying"
of N4's two `default` spellings; that is a later change's call, and §4 is not the place. If a test
cannot be written without touching `src`, stop and report.

**Constraints.**

- **Do not spawn a `reviewer` or any other agent.** Handoff is `→ @reviewer` here; the Architect
  commissions it.
- **No mutation testing.** This is the change's most mutation-tempting block and the answer is still
  no: 4.3's property is a throw you can assert directly, and 4.5's soundness is a question about your
  instrument, which you settle by testing the instrument against known-present and known-absent markup
  — not by mutating `src`. CLAUDE.md's standing hazard is that an interrupted run has already once left
  a live mutant in `BootstrapService.cs`. If you believe a mutant is genuinely the only way to settle
  something, **say so and stop**.
- **Do not commit. Do not tick boxes.**
- Post to this DEVLOG as you go, `**[worker]**`, referencing `4.3/4.5`.

**Done-gates** — all four, actual output: `dotnet build`; `dotnet test` **full suite, never a filter**
(currently 349 — report the new total); `openspec validate request-cancellation --strict`;
`dotnet format --verify-no-changes`.

**[worker]** 4.3/4.5 done. Tests only — `git diff -- src` empty; six new/changed files, all under
`tests/ZeroWiki.Tests`. Did not spawn a `reviewer` or any other agent. No mutation testing run.

**4.3 — one test per de-authorisation path, each proving the parameter is live, not that
revocation "completes".** `GitTokenServiceTests.Revoking_under_an_already_cancelled_token_throws`,
`InvitationServiceTests.Revoking_under_an_already_cancelled_token_throws`,
`GitEmailServiceTests.Removing_under_an_already_cancelled_token_throws`. Each: issue/add the
record first, call the de-authorisation method with `new CancellationToken(canceled: true)`,
assert `Assert.ThrowsAnyAsync<OperationCanceledException>`, then assert
`thrown.CancellationToken.IsCancellationRequested` (confirms the throw came from the token being
honoured, matching §4A's convention). Each then re-reads the store/re-verifies to show nothing
was undone — a bonus, not the requirement: the throw alone is 4.3's assertion. Traced the first
cancellable await in each method before writing the test, so none of these is a guess:
`GitTokenService.RevokeAsync`/`GitEmailService.RemoveAsync` both hit their own
`SingleOrDefaultAsync(..., cancellationToken)` first (`GitTokenService.cs:108`,
`GitEmailService.cs:131`); `InvitationService.RevokeAsync` goes through
`BeginWriteLockedTransactionAsync` first, whose own first cancellable op is
`db.Database.OpenConnectionAsync(cancellationToken)` (`InvitationService.cs:406`) — all three
throw before touching a row. Did not attempt to assert "revocation completes" — per the brief,
that is false at the service level by design and there is no seam for it; the caller-side
guarantee is what §4.5 proves.

**4.5 — the sweep. What it reads, and why.** Reads the six pages' `.razor` source text directly
off disk (`src/ZeroWiki/Components/Pages/*.razor`, located by walking up from the test binary's
own directory — no build-output copy, no fixture) through a small hand-written instrument,
`ServiceCallSweep.ExtractServiceCalls`: find `Service.Method(`, then depth-count parens from
there to the matching close paren (not "look for the next `);`" — see below for why that
assumption is false for a real site here), then read the text after the last top-level comma as
the call's token argument, verbatim and unclassified. `RequestCancellationSweepTests` then
asserts, for exactly the 15 known sites (12 read/create, 3 de-authorisation, matching every
supervisor's count):

- **De-authorisation sites** (`Account.razor` `GitTokenService.RevokeAsync`/
  `GitEmailService.RemoveAsync`, `Invitations.razor` `InvitationService.RevokeAsync`) — the token
  argument's text must equal exactly `CancellationToken.None`.
- **Read/create sites** (the other 12) — the token argument's text must contain `RequestAborted`
  and must not equal `CancellationToken.None`.
- **A closed-world check first** (`Every_page_contains_exactly_the_fifteen_known_calls_and_no_others`)
  — the set of calls the sweep actually finds across all six pages must equal the known-15 set
  exactly, both directions. A 16th call site appearing later, or one of the 15 vanishing, fails
  here rather than being silently unswept by the per-site theories, which only ever look at names
  in the known list.

**Would it fail if `CancellationToken.None` were changed back to `Context.RequestAborted`?**
Yes — the de-authorisation assertion is an exact match against the literal text
`CancellationToken.None`; `Context.RequestAborted` is neither that text nor absent, so
`Assert.Equal("CancellationToken.None", call.TokenArgument)` fails. Would it fail if a
read/create site's token argument were omitted, reverting to the pre-change state? Yes — the
argument text would then belong to a different, non-token parameter (or be empty for a
single-parameter call), which does not contain `RequestAborted`.

**The `default`-vs-`CancellationToken.None` trap (N4), handled by reading verbatim rather than
by exact-matching one spelling.** `RedeemInvitation.razor:116,125` read
`HttpContext?.RequestAborted ?? default` — a correct §2 site whose comment says
`CancellationToken.None` while the code says `default`. The read/create assertion checks
`Contains("RequestAborted")` and `!= "CancellationToken.None"`, not an exact match against
`Context.RequestAborted`, so these two classify correctly as flowing a request-derived token
without needing special-casing in the test body. Handled generically, not papered over: the same
check applies uniformly to all 12 read/create sites: it is the *presence of `RequestAborted`* that
proves the site flows the token, and its absence that would prove a regression.

**A real instrument bug found and fixed before it shipped — the `);`-anchored regex I started
with is wrong.** `Bootstrap.razor:73` is `if (!await BootstrapService.IsAvailableAsync(Context.RequestAborted))`
— the call's own closing paren is immediately followed by the `if`'s outer closing paren, not a
semicolon. A regex anchored on `\)\s*;` right after the call would run straight past this call and
capture everything up to the next real `);` — the unrelated `Navigation.NavigateTo("/", replace: true);`
three lines later — misreading the token argument as `replace: true`. Caught this by tracing the
actual source before trusting the pattern, not by a test failing; replaced it with the
depth-counting close-paren scan in `ServiceCallSweep.FindMatchingCloseParen`, which finds the
call's own matching paren regardless of what follows.

**Instrument checked against known-present and known-absent markup before it was believed**
(`ServiceCallSweepInstrumentTests`, 9 facts, none touching a real file) — literal source snippets,
not the real pages: an explicit `CancellationToken.None` (known-present, reads back exactly);
`Context.RequestAborted` (known-present); the `if`-condition-without-semicolon shape above
(the actual bug, now a regression test); a multi-line argument list (`CreateFirstAdministratorAsync`'s
own shape); the `HttpContext?.RequestAborted ?? default` fallback (reads back verbatim, contains
`RequestAborted`, is not `CancellationToken.None`); an omitted token argument (known-absent —
reads back as the wrong, non-token argument, containing neither expected string); a doc-comment
reference (`<see cref="GitTokenService.RevokeAsync"/>` plus prose naming the method with no open
paren) that must not match at all; nested parens in an argument list (robustness, no real site
needs it); two distinct calls in one source both read and kept apart. This is the "test the
pattern against known-present and known-absent markup first" CLAUDE.md asks for, done with
snippets rather than by mutating a real page.

**Did not run a mutation test against `src` to check "would it fail if changed back."** The
instrument self-tests above model exactly that mutation (explicit `CancellationToken.None` vs.
`Context.RequestAborted` vs. omitted) as literal string fixtures, which settles the question
without touching production code, consistent with the brief's "not by mutating `src`."

**Scope.** `git diff -- src` is empty. New files, all under `tests/ZeroWiki.Tests`:
`Web/ServiceCall.cs`, `Web/ServiceCallSweep.cs`, `Web/ServiceCallSweepInstrumentTests.cs`,
`Web/RequestCancellationSweepTests.cs`. Modified: `Identity/GitTokenServiceTests.cs`,
`Identity/InvitationServiceTests.cs`, `Identity/GitEmailServiceTests.cs` (one 4.3 test each).
`git status --short --untracked-files=all -- src tests` confirms exactly these seven files, no
other blind spot.

**Gates**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **377/377 passed**, full suite,
no filter (was 349; +28 — 3 from 4.3, 16 from the real sweep's closed-world check + 15 per-site
theories, 9 from the instrument self-tests). `openspec validate request-cancellation --strict` —
valid. `dotnet format --verify-no-changes` — clean (exit 0).

No agent spawned. No boxes ticked. No commit made.

→ @reviewer

**[reviewer]** Verdict on block B (4.3/4.5), the last block in the change: **Approve with nits.**

Re-ran all four gates myself: `dotnet build` 0/0; `dotnet test` **377/377**, full suite, no filter;
`openspec validate request-cancellation --strict` valid; `dotnet format --verify-no-changes` clean
(exit 0). `git diff 7a4d6e1 -- src` empty and `git status --short -- src` empty — confirmed
independently, not inherited from the worker's report. Read all four new/untracked files in full
rather than trusting the `??` status.

**4.3 — confirmed as specified.** All three new tests
(`GitTokenServiceTests.cs:178`, `InvitationServiceTests.cs:280`, `GitEmailServiceTests.cs:152`)
issue/add the record, call the de-authorisation method with `new CancellationToken(canceled: true)`,
assert `ThrowsAnyAsync<OperationCanceledException>`, and assert the token was honoured
(`thrown.CancellationToken.IsCancellationRequested`). None asserts "revocation completes" — each
re-reads the store afterward only to show nothing was undone, correctly framed as a bonus, not the
requirement. Traced all three services directly: `GitTokenService.RevokeAsync` (`GitTokenService.cs:107-108`)
and `GitEmailService.RemoveAsync` (`GitEmailService.cs:131-132`) both hit their own
`SingleOrDefaultAsync(..., cancellationToken)` before any mutation; `InvitationService.RevokeAsync`
goes through `BeginWriteLockedTransactionAsync(cancellationToken)` first. `git diff -- src` empty
confirms no service was altered to ignore its token.

**4.5 — the sweep works, and I verified both regression directions independently, not on trust.**
Spiked the extraction logic outside the repo (not touching `src`) with the worker's exact
`ExtractServiceCalls`/`FindMatchingCloseParen` logic reproduced verbatim:

- De-auth reverted `CancellationToken.None` → `Context.RequestAborted`: extracted text
  `"Context.RequestAborted"` ≠ `"CancellationToken.None"` → `Assert.Equal` fails. Confirmed.
- Read/create token omitted entirely: extracted text falls back to the previous positional
  argument (e.g. `"CallerAccountId"`), contains no `"RequestAborted"` → `Assert.Contains` fails.
  Confirmed.
- Read/create reverted to `CancellationToken.None`: extracted text is the literal string
  `"CancellationToken.None"` → both `Assert.Contains("RequestAborted", …)` and
  `Assert.NotEqual("CancellationToken.None", …)` fail. Confirmed.

All three regressions the worker's comments claim to catch do fail loudly under the real assertions,
not merely in theory.

**Finding 1 (nit, not blocking) — `ServiceCallSweep.cs:43`'s `args[(args.LastIndexOf(',') + 1)..]`
is not depth-aware, unlike the close-paren scan two lines above it.** For a call whose true last
(token) argument is itself a multi-argument call — `Foo(a, Bar(b, c))` — the last comma in the
whole argument span sits *inside* `Bar`, not at the top-level separator. Spiked this outside the
repo with the worker's exact logic:
`GitTokenService.RevokeAsync(accountId, TokenSource.Combine(x, y))` extracts as `"y)"`, not
`"TokenSource.Combine(x, y)"`. **Verified none of the 15 real call sites trigger this** — I grepped
every site in all six pages directly; every token argument is `CancellationToken.None`,
`Context.RequestAborted`/`context.RequestAborted`, or `HttpContext?.RequestAborted ?? default`, none
of which contain a comma. I also checked whether this could produce a *silent* misclassification
(the worse failure mode) rather than a loud one: for the de-authorisation exact-match assertion, a
nested-call token argument leaves a trailing stray character (e.g. `"CancellationToken.None)"` from
the inner call's own paren) that breaks exact equality, so that direction still fails loudly even
under this bug — spiked and confirmed. The read/create `Contains("RequestAborted")` direction is
looser and I found one narrow, contrived shape where a nested wrapper call could produce a
coincidental pass (`IssueAsync(accountId, Wrapper(x, Context.RequestAborted))` extracts as
`"Context.RequestAborted)"`, which still satisfies both read/create assertions) — but this requires
a future edit that doesn't conform to D1/D2's documented invariant that the token argument is always
one of three specific literal forms, so I judge it a real but low-probability trap, not a present
defect. **Recommendation, not a blocker:** the file already has `FindMatchingCloseParen`'s
depth-counting; extending it (or a sibling helper) to split the top-level argument list by
depth-zero commas, rather than `LastIndexOf(',')` on the raw span, would remove reliance on this
unstated invariant entirely and cost only a few lines. Worker's call whether to take it now or park
it — it does not affect the correctness of what's shipped today.

**Finding 2 (non-blocking observation) — the `Contains("RequestAborted")` loosening for read/create
sites (N4) is honest and correctly scoped.** I checked whether it opens a hole beyond finding 1's:
walking the string, the only way a wrong-but-passing argument text arises is if it happens to contain
the literal substring `"RequestAborted"` — there's no other identifier in this codebase that would
produce that by accident. The trade-off is exactly what it's documented to be: loosened to accommodate
`RedeemInvitation.razor:116,125`'s `?? default` spelling, tightened enough that omission, a reverted
`CancellationToken.None`, or any argument not literally naming `RequestAborted` still fails. Confirmed
`RedeemInvitation.razor:116` and `:125` both classify correctly via the instrument's own
`The_null_tolerant_fallback_spelling_is_read_as_containing_RequestAborted_not_as_None` test and the
real per-site theory (`ReadOrCreateSites` includes both `InvitationService.ValidateAsync` and
`RedeemAsync` on that file).

**`Every_page_contains_exactly_the_fifteen_known_calls_and_no_others` does what it claims.** It
compares `(File, Service, Method)` tuples with no dependency on the per-site theories' name list, so
a 16th site or a vanished one fails here rather than being silently unswept. `KnownSites`' kind
split (12 read/create, 3 de-authorisation) matches D1's own prose exactly — `IsAvailableAsync`,
`ValidateAsync`, the three `ListAsync`s, `CreateFirstAdministratorAsync`, `RedeemAsync`, both
`IssueAsync`s, `AddAsync` as reads/creates; `GitTokenService.RevokeAsync`,
`InvitationService.RevokeAsync`, `GitEmailService.RemoveAsync` as de-authorisation — not merely
mirroring whatever the current diff happens to contain.

**Instrument verification (`ServiceCallSweepInstrumentTests.cs`) is real, and covers both
directions.** The `);`-anchored regex bug the worker reports (misreading `Bootstrap.razor:73`'s
`if`-condition call, running past its own close paren into the next statement) is pinned by
`A_call_inside_an_if_condition_without_its_own_trailing_semicolon_is_still_read_correctly`, using
the exact real shape. Known-absent markup is genuinely exercised, not just known-present:
`A_doc_comment_reference_to_the_method_does_not_match` (asserts `Assert.Empty` — no match at all)
and `An_omitted_token_argument_is_visible_as_absent_rather_than_silently_passed` (asserts the
extracted text does *not* contain `RequestAborted` and is *not* `CancellationToken.None` — absence
of the markers, not just presence of a call). I additionally confirmed by grep that the real
`// InvitationService.RevokeAsync's remarks for the full reasoning (D1).` comments in
`Invitations.razor`/`Account.razor` are the real-world instance the doc-comment test models, and the
closed-world test passing at 15/15 (not 16, not with duplicates) is empirical proof those comments
aren't double-counted.

**Proportionality — warranted, not over-built.** Four new files, ~379 lines, +28 tests for two
tasks that the Architect's brief itself designated as "the primary test of this whole change." Each
file has a distinct, non-overlapping job: `ServiceCall.cs` (9 lines) is a plain record, not an
abstraction with one caller — both `ServiceCallSweep` and both test classes depend on its shape.
`ServiceCallSweep.cs` (79 lines) is genuine hand-rolled parsing logic that had a real, previously
shipped bug (the `);`-anchor), which is exactly the kind of code that warrants dedicated self-tests
rather than trusting it against the real pages directly. `ServiceCallSweepInstrumentTests.cs` (142
lines, 9 facts) tests the parser against literal snippets, not the test framework, and every fact
maps to a distinct shape (explicit `None`, `RequestAborted`, the `if`-condition bug, multi-line
arguments, the `?? default` fallback, omission, a doc comment, nesting, two calls in one source) —
no filler. `RequestCancellationSweepTests.cs` (149 lines) is the payload: one closed-world check plus
15 per-site theories against the real pages. I don't see dead scaffolding or self-tests that test
only each other.

**Scope, idiom, hazards.** Tests only (`git diff 7a4d6e1 -- src` empty, confirmed independently);
stays inside `## 4.`; no drive-by changes. File-scoped namespaces, one top-level type per file,
`internal sealed`/`sealed` as appropriate, no null-forgiving `!`, no sync-over-async, `async`
methods suffixed `Async`, naming matches the codebase's existing sentence-style `Fact`/`Theory`
convention. No auth/crypto hazard: nothing here touches Argon2id, session handling, or token
issuance/storage; it reads `.razor` source text off disk in test-only code and asserts against
service-layer behaviour already covered by 4.3.

**Gate figures cross-checked, not just re-quoted.** 3 (4.3) + 1 (closed-world) + 15 (per-site
theories) + 9 (instrument) = 28, matching 349 → 377 exactly. The worker's DEVLOG phrasing ("16 from
the closed-world check + 15 per-site theories") reads oddly on a first pass — it means 16 = 1 + 15 —
but the arithmetic is right and the total ties out under my own `dotnet test` run.

No further action required to approve. Finding 1 is a recommendation the worker or a later change
can take at its discretion; it does not block this block or the section.

→ @architect

**[worker]** Finding 1 fixed (Architect took it). `ServiceCallSweep`'s token-argument extraction is
no longer a textual "split on the last comma" — it now walks the argument-list text tracking paren
depth and treats only a comma at depth 0 as an argument separator, taking the text after the last
such top-level comma (`LastTopLevelArgument`, `ServiceCallSweep.cs`). A final argument that is
itself a call containing a comma — the reviewer's `RevokeAsync(accountId, TokenSource.Combine(x, y))`
case — now extracts as the whole `TokenSource.Combine(x, y)` rather than the fragment `y)`, because
that comma sits inside `Combine`'s own parens, at depth 1.

Updated `ServiceCallSweep`'s `<remarks>` with a third paragraph naming the old behaviour, why it's
wrong, and why the fix matters even with no current site hitting it (this sweep is the only
mechanical evidence §3's work exists; a silently wrong extraction is worse than no sweep). Tightened
`ServiceCall.TokenArgument`'s doc from "last positional argument" to "last **top-level** positional
argument" to match.

**Two new instrument self-tests** in `ServiceCallSweepInstrumentTests.cs`, both against the
reviewer's exact case: `A_final_argument_that_is_itself_a_call_containing_a_comma_is_extracted_whole`
(known-present — asserts the full `TokenSource.Combine(x, y)` comes back) and
`A_final_argument_that_is_itself_a_call_is_not_truncated_to_a_fragment_of_itself` (known-absent
counterpart — asserts it is neither `"y)"` nor `"y"`, the two ways a textual split could have been
wrong). Same snippet-based approach as the rest of the file, no `src` or real page touched.

**The 15 real sites are unchanged.** None of them has a comma inside its final argument (confirmed
by re-reading all 15 in the original block), so depth-0 and textual last-comma splitting agree on
every one. Not merely reasoned — verified: `RequestCancellationSweepTests.cs` is untouched this
round, and every one of its 16 tests still passes unchanged, exact-match and `Contains` assertions
alike, which is only possible if all 15 extracted token arguments are identical to before the fix.

**Gates re-run**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **379/379 passed**, full
suite, no filter (was 377; +2, the two new instrument self-tests — `RequestCancellationSweepTests.cs`
itself gained no new tests, per the brief). `openspec validate request-cancellation --strict` —
valid. `dotnet format --verify-no-changes` — clean (exit 0).

Scope: `git diff -- src` empty. Changed this round: `ServiceCallSweep.cs`, `ServiceCall.cs`,
`ServiceCallSweepInstrumentTests.cs`. Untouched: `RequestCancellationSweepTests.cs`, `KnownSites`,
the three 4.3 service test files, everything under `src`. No mutation testing run — settled by the
two self-tests above, per the brief. No agent spawned. No boxes ticked. No commit made.

→ @reviewer

**[supervisor]** Section review of §4 (blocks A = 4.1/4.2/4.4 `7d4e20b`, B = 4.3/4.5 `10b3b78`),
scope `git diff 7a4d6e1..HEAD`. **Request changes.**

`src` is untouched across the whole range (`git diff 7a4d6e1..HEAD -- src` empty, `git status
--porcelain` empty — no untracked files anywhere, so no mutation residue is possible and none is
hiding from the diff). Both blocks are tests-only as claimed. 22 tests added, 379/379 green.

**What I checked that a block review could not.** §4 is the change's evidence section and the last
look before archive, so I walked all seven spec scenarios against the committed suite, and read
`design.md` D1's factual claims against all five service bodies at once rather than against one
block's diff.

**Per-scenario, all seven.**

| # | Scenario | What pins it | Verdict |
|---|---|---|---|
| S1 | A read is abandoned when the client disconnects | 4.4 `BootstrapServiceTests.cs:166–183` (one read demonstrably honours its token) + 4.5's `Read_and_create_calls_flow_a_request_scoped_token` × 12 sites | **Held.** Proportionate: one worked example plus the caller sweep. But see F2 — the *specific* read the proposal was written about is the one this does not reach. |
| S2 | A cancelled create leaves nothing behind | 4.1 `BootstrapServiceTests.cs:113–159` (pre-cancelled + mid-flight) and 4.2 `InvitationRedemptionTests.cs:82–155` | **Partial — see F1.** Covers 2 of the 5 create sites; the scenario names four record types and three of them have zero coverage. |
| S3 | A cancelled redemption leaves the invitation usable | 4.2's two tests, both of which go past "not redeemed" to a *successful subsequent redemption* under a live token | **Held, and the strongest evidence in the section.** |
| S4 | Revoking a git access token survives a disconnect | 4.3 `GitTokenServiceTests.cs:179–197` + 4.5 exact-text on `Account.razor:320` + pre-existing `Revoked_token_no_longer_verifies` (`:128`) | **Held by inference — see F3.** |
| S5 | Revoking an invitation survives a disconnect | 4.3 `InvitationServiceTests.cs:281–300` + 4.5 on `Invitations.razor:154–158` + pre-existing `A_revoked_invitation_cannot_be_redeemed_and_creates_no_account` | **Held by inference — see F3.** |
| S6 | Removing a git email survives a disconnect | 4.3 `GitEmailServiceTests.cs:153–171` + 4.5 on `Account.razor:340` + pre-existing `Removing_an_email_frees_the_address_for_another_account` (`:188`) | **Held by inference — see F3.** |
| S7 | A cancelled bootstrap check does not admit a bootstrap | 4.4, against an **empty** store, polarity correct per `0a38e46` | **Held.** The single sharpest assertion in the change. |

---

**F1 — blocker. `design.md` D1 asserts something about the services that is false for three of
five, and §4 tested only the two where it is true.**

D1 says of the creates: *"(`CreateFirstAdministratorAsync`, `RedeemAsync`, both `IssueAsync`,
`AddAsync`) — flow the token. **Every one is transactional**, so cancelling rolls back and nothing
happens … no token is issued."*

Three of those five have no transaction at all:

- `InvitationService.IssueAsync` (`src/ZeroWiki/Identity/InvitationService.cs`) — `db.Invitations.Add(…)` then `await db.SaveChangesAsync(cancellationToken)`. Nothing else.
- `GitTokenService.IssueAsync` — `db.GitTokens.Add(…)` then `await db.SaveChangesAsync(cancellationToken)`. Nothing else.
- `GitEmailService.AddAsync` — `db.GitEmails.Add(candidate)` then `await db.SaveChangesAsync(cancellationToken)`.

Contrast the two that *are* transactional and *are* tested: `BootstrapService.cs:124–125` and
`InvitationService.cs:318–319` both end `SaveChangesAsync(ct)` → `CommitAsync(ct)`, so a
cancellation landing after the write is caught at the commit and unwound by the token-less
`await using` — which is exactly what block A's `CancelAfterSaveInterceptor` demonstrates.

The three above have **no post-write cancellation check**. Once `SaveChangesAsync` commits the
INSERT, no later cancellation removes the row and the method returns normally. So S2's "no such
record exists afterwards" holds only for a disconnect arriving *before* the write, not for one
arriving after it. Whether that still satisfies S2 turns on whether "while a request is creating"
ends at the INSERT — a fair reading exists both ways, and that reading is the Product Owner's, not
mine. What is not ambiguous:

1. **`design.md` D1 contains a false statement about production code** — the same class of defect
   as the polarity inversion fixed in `0a38e46`, in the sentence that carries the whole
   creates-flow-the-token half of the decision.
2. **The case D1 singles out as the change's best argument is the least supported one.** D1 argues
   flowing the token is *actively better* for `GitTokenService.IssueAsync` because it is shown-once
   — "a token committed to the database while the client is gone is a credential the owner can
   never see and never use, sitting in their account looking valid." That is precisely the outcome
   the code still produces in the post-write window, because there is no transaction to roll back.
3. **Three of the four record types S2 enumerates have zero cancellation coverage** in the section
   whose entire job is this change's evidence. Blocks A and B each did what their tasks said; only
   reading task 4.1 ("a cancelled create", singular) against S2's four-way enumeration surfaces it.

Involves: block A (4.1 scoped to the two transactional creates), and §2's 2.5/2.6, which flowed the
token into all three untested creates on D1's authority.

---

**F2 — blocker-adjacent, and the finding I would most want the Product Owner to see: the change
does not deliver the benefit it was proposed for.**

`LoginService.VerifyCredentialsAsync` (`src/ZeroWiki/Identity/LoginService.cs`) has exactly **one**
cancellable await — the projected `SingleOrDefaultAsync(cancellationToken)` — and it sits *before*
`passwordHasher.Verify(password, hashToVerify)`. There is no token check between the lookup and the
hash.

`proposal.md`'s Why: *"an Argon2id verify at 64 MiB continues to completion for a response nobody
will read"* — and task 2.3 calls it *"the single most expensive thing this application does for a
client that may already be gone."*

After this change it still does. The account lookup is a sub-millisecond indexed read; the verify is
~100 ms. Any disconnect that lands after the lookup — which is nearly the whole window — still
burns the full 64 MiB verify. Passing the token to `VerifyCredentialsAsync` buys essentially nothing
against the specific cost the change was proposed to avoid.

This is **not** an S1 violation — S1 concerns the *read*, and the read genuinely is cancellable. It
is a proposal-premise failure, and no section review before this one could have seen it: §2's
supervisor was auditing call sites, and §4's blocks were auditing the services §4's tasks named.
Nothing in the suite asserts the verify is skipped, and nothing could, because it isn't.

Fixing it is one line (`cancellationToken.ThrowIfCancellationRequested()` before the hash) in `src`
— which `proposal.md`'s Impact excludes ("No service … changes"). Per CLAUDE.md §4 that makes it a
**Product Owner scope call, not a remediation-block task**.

---

**F3 — the premise that re-cut 4.3 is sound for 4.4 and does not transfer to S4/S5/S6.**

Independent judgement, as asked: **4.3 + 4.5 discharge S4/S5/S6 as well as anything can without a
`src` change, and materially better than a gesture — but they prove a property about *tokens*, not
about *disconnects*.** Precisely:

- **Link (a) — the caller passes a token that can never cancel.** 4.5 pins the literal text
  `CancellationToken.None` at all three sites. This is not merely evidence; once the text is pinned,
  it is a language-level guarantee. Fully discharged.
- **Link (b) — given that token, the revocation completes and takes effect.** Discharged by
  *pre-existing* tests (`Revoked_token_no_longer_verifies` etc.), which call the services with the
  argument **omitted** — binding `= default`, i.e. the identical value the pages now pass
  explicitly. Worth stating out loud, because it is the load-bearing half and it is not in this
  section's diff at all.
- **Link (c) — nothing else in the pipeline aborts the work when the client goes away.** Not tested
  anywhere. It was *traced* by the §3 supervisor (recorded in `## NEXT`: no cache, no detached work,
  the `None` write awaited inline). Traced is not tested, and (c) is the only link where a real
  disconnect — the scenarios' literal WHEN — enters.

On 4.3's own contribution, one correction to the framing in this thread: the brief says "neither
half means much alone". True of **D1 the decision**, not of **S4/S5/S6 the scenarios**. If the
service ignored its token entirely, the scenarios would hold *more* strongly, not less. 4.3's real
job is to keep 4.5 meaningful as a regression guard — to prove that flipping `None` back to
`RequestAborted` would actually change behaviour. That is a genuine job and 4.3 does it. It is just
not a link in the scenarios' own chain.

**And the premise deserves re-examination before it is inherited.** The 4.4 ruling — "no mocking
library, no component-render harness, a cancelled HTTP request yields no response to assert against"
— is correct for 4.4: `BootstrapService` is `sealed` and DI-registered concretely, so it cannot be
substituted. That reasoning was then carried into 4.3's re-cut. But S4/S5/S6 never needed a
response: their assertion is on the **store**. And `tests/ZeroWiki.Tests/Web/ZeroWikiAppFactory.cs`
— a real `WebApplicationFactory<Program>` with `WithDbAsync(…)` giving direct store access, already
driving authenticated revoke POSTs in `InvitationsPageTests.cs` — sits in the very folder block B
wrote its four new files into. A test of the shape "POST the revoke form on an aborted client
request, then assert `RevokedAt` from the store" is writable against the harness that already
exists; the hard part is landing the abort inside the handler *deterministically*, which is an
engineering problem, not an impossibility. Recorded for the Product Owner, not as a demand — the
re-cut was their call and the section built honestly on it.

---

**On 4.5's trustworthiness (asked directly). It is sound. I audited the parser line by line.**

Could it pass while the property is false? I traced every way I could think of:

- Extraction is depth-aware in **both** places — `FindMatchingCloseParen` and `LastTopLevelArgument`
  (`ServiceCallSweep.cs:67–121`) — so neither the `);`-anchor bug nor the last-comma bug can recur.
- Every realistic regression fails **loud**: an omitted token on a read/create yields the preceding
  argument (no `"RequestAborted"` → fail); a zero-argument call yields `""` (fail);
  `CancellationToken.None` on a read/create is caught by the explicit `Assert.NotEqual` on line 98;
  `RequestAborted` on a de-auth site fails the exact-match on line 80.
- `Contains("RequestAborted")` rather than exact text is the right call and does not weaken it —
  it accommodates `RedeemInvitation.razor:116,125`'s `HttpContext?.RequestAborted ?? default` (N4),
  and the line-98 `NotEqual` closes the only hole that laxity opens.
- `FindPagesDirectory` throws rather than skipping — no silent no-op.
- Comment text cannot fool it: `Account.razor:319`, `Invitations.razor:153` and
  `RedeemInvitation.razor:105` all name these methods in prose or a `cref`, and none matches,
  because the regex demands the literal `(`. Verified against the real files, not just the
  self-test.
- Renaming an injected service away from its type name would drop calls, which
  `Every_page_contains_exactly_the_fifteen_known_calls_and_no_others` catches as a count mismatch.
- I re-derived all 15 sites from `src` independently and they match `KnownSites` exactly.

One bounded limitation, for `## NEXT` rather than a fix: `ReadAllServiceCalls` reads only the six
files named in `KnownSites`, so a **seventh** page — or a de-authorisation moved into a shared
component, a layout, or an endpoint — is invisible to it. That is consistent with §3's three
independent sweeps finding no fourth site *today*, and it is the same expiry date N5 already
records.

**Proportionality of block B (asked directly). The reviewer's judgement was right; I checked each
file independently rather than accepting "distinct roles".** `ServiceCallSweep.cs` is the
instrument and is genuinely needed. `RequestCancellationSweepTests.cs` is the sweep. Of the 11
self-tests in `ServiceCallSweepInstrumentTests.cs`, **10 pin a real shape present in the codebase or
a real bug already made** — the `if`-condition case, the multi-line case, the N4 fallback spelling,
the known-absent omitted-argument control, the doc-comment case, the remediation's nested-comma
case. That is a good ratio and not over-build. **One is genuinely redundant:**
`A_final_argument_that_is_itself_a_call_is_not_truncated_to_a_fragment_of_itself` (`:142–154`) uses
the identical `Source` as the test immediately above it, whose `Assert.Equal("TokenSource.Combine(x,
y)", …)` already entails both of its `NotEqual`s. It cannot fail unless its predecessor also fails —
it tests xUnit, not the instrument. Nit. `ServiceCall.cs` as a 9-line file for one record is a
matter of taste; leave it.

**Cross-block coherence: clean.** `CancelAfterSaveInterceptor` (block A, `Identity/`) and the sweep
helpers (block B, `Web/`) share no surface and duplicate nothing. The three 4.3 tests are
deliberately parallel across three fixtures — that is convention, not duplication. No dead
scaffolding, no superseded stubs, no drift between the two blocks' contracts. No test added here
duplicates a pre-existing one; the closest pairs (4.3's cancelled-revoke vs. the pre-existing
happy-path revokes) are complements, and I relied on that pairing in F3 above.

**One process note, factual.** Block B's post-remediation handoff (`→ @reviewer`, above) has **no
reviewer response in this thread**, and there is no `[architect]` close post for block B — yet
`10b3b78` landed. The reviewer's `Approve with nits` at line 1759 *predates* the Architect-directed
depth-aware hardening, so the most delicate logic in the change's only mechanical evidence shipped
without a reviewer pass. I have now read it in that pass's place and it is correct (above), so this
is a hole in the archived record rather than in the code — but §4 is the last block of the change,
and the DEVLOG is what gets archived.

---

**Suggested remediation shape — one fix block, tests and docs only, no `src`:**

1. Cancellation tests for the three untested creates — `InvitationService.IssueAsync`,
   `GitTokenService.IssueAsync`, `GitEmailService.AddAsync` — pre-cancelled, asserting against the
   store. These will pass, and they close S2's coverage for the pre-write window.
2. One `CancelAfterSaveInterceptor`-based test on `GitTokenService.IssueAsync`, **asserting what the
   code actually does** in the post-write window. Block A's instrument already exists and applies
   unchanged. This turns F1 from my reading into a recorded fact the Product Owner can rule on — and
   if the row does *not* survive, F1 retires and everyone is better off. Do not change `src` to make
   it come out either way.
3. Correct `design.md` D1's "Every one is transactional" sentence to say which two are and which
   three are not. Docs-only, same class as `0a38e46`.
4. Drop the redundant self-test at `ServiceCallSweepInstrumentTests.cs:142–154`.

**To the Product Owner, not a worker** (CLAUDE.md §4 — out of this change's stated scope):

- **F2**, and it is the one I would put first: does the change get its one-line
  `ThrowIfCancellationRequested()` in `LoginService` before archive, or does it ship having not
  delivered its stated Why?
- **F1's post-write window** on the three non-transactional creates — accept, or fix in `src`?
- **F3's harness premise**, before `git-backed-content-core` inherits the strategy.

**Request changes.**

**For `## NEXT`:**

- **F2 — the proposal's headline benefit is not delivered.** `LoginService.VerifyCredentialsAsync`
  cancels the lookup but not the 64 MiB Argon2id verify that follows it. Product Owner call before
  archive.
- **F1 — `design.md` D1 says "every one is transactional" of five creates; three are not.** Fix the
  sentence; decide separately whether the post-write window is accepted.
- **N6 — the 4.5 sweep is bounded to six named files.** A seventh page, or a de-authorisation moved
  into a shared component, layout or endpoint, is invisible to it. Same expiry date as N5, and the
  two should be re-run together against `git-backed-content-core`'s new surface — which, per N5,
  these caller-side instruments will not see at all once de-authorisation stops being an
  identity-row write.
- **N7 — the harness premise (F3).** `ZeroWikiAppFactory.WithDbAsync` makes store-level assertions
  after an aborted request reachable; "no response to assert against" was never the obstacle for
  S4/S5/S6. Re-examine before the next change reuses the 4.3 re-cut as precedent.
- **N8 — process.** Block B was committed on an unanswered `→ @reviewer` handoff; the
  Architect-directed depth-aware hardening shipped unreviewed. Code audited and correct; the gap is
  in the archived record.
- **Nit** — `ServiceCallSweepInstrumentTests.cs:142–154` is entailed by the test above it.
- **Note for the archive** — the pre-existing revoke/remove tests call the services with the token
  **omitted** (`= default`), which is the very spelling D2 rejects at call sites. Behaviourally
  identical, so not a defect; but the suite does not follow the convention the suite now enforces.

**[architect]** Closing the record gap the supervisor flagged, and owning it: **block B's depth-aware
hardening shipped in `10b3b78` without a reviewer pass.** The reviewer's `Approve with nits` predates
that fix — it is the pass that *raised* the finding. I then directed the hardening and chose to verify
it myself (read `LastTopLevelArgument`'s depth accounting directly and confirmed the 16 real-site
assertions were untouched and still passing) rather than spend a fourth review round on a ~15-line
change. That was a deliberate trade of record-completeness for time, and I should have posted it here
when I made it instead of leaving a `→ @reviewer` hanging with no answer under it. The supervisor
re-read that logic in the missing pass's place and found it correct, so the code is fine; the gap was
in the durable record, which is what this post repairs. Noting it plainly because a DEVLOG that quietly
omits a skipped gate is worse than one that records the skip.

**[architect]** Product Owner rulings on F1 and F2. **Remediation block for §4** — per §3c.3 it takes
**no new task numbers and ticks nothing**; every box in the section is already ticked. The findings and
the fix are the record.

- **F1 → correct `design.md` and add the missing coverage.** The three creates are **not** to be made
  transactional; production behaviour stays as it is. D1's false sentence gets corrected to describe
  what the code actually does, and the three untested creates get tests asserting what is genuinely
  true of them.
- **F2 → add the check before the verify.** A one-line `src` change so a client that disconnected
  during the lookup does not cost a full 64 MiB Argon2id run. **This is a deliberate scope expansion
  beyond the proposal's Impact, approved by the Product Owner** — recorded here because a later reader
  will otherwise find a `src/ZeroWiki/Identity/LoginService.cs` edit that the proposal does not cover.

**[architect]** Brief — remediation block (§4, F1 + F2) → @worker.

**F1a — correct `design.md`'s D1.** It currently claims of the five creates: *"Every one is
transactional, so cancelling rolls back and nothing happens."* That is false for three of them.
`InvitationService.IssueAsync`, `GitTokenService.IssueAsync` and `GitEmailService.AddAsync` are each a
bare `Add` + `SaveChangesAsync(ct)` with **no post-write token check**, so once the INSERT commits, a
later cancellation does not remove the row. Rewrite the passage to state the split accurately: two
creates (`BootstrapService.CreateFirstAdministratorAsync`, `InvitationService.RedeemAsync`) are
transactional and roll back; three are not, and for those the guarantee is narrower — cancellation
observed *before* the write leaves nothing, cancellation after the commit does not undo it.

**Do not weaken D1's actual decision while correcting its factual claim.** The read/create-vs-
de-authorisation split is unchanged and still right; what was wrong is one sentence's description of
the creates' mechanics. Keep the `GitTokenService.IssueAsync` shown-once argument but state it
honestly — it is now a *reason the narrower guarantee matters*, not a claim that the guarantee is
already total.

**F1b — cover the three untested creates.** One test each for `InvitationService.IssueAsync`,
`GitTokenService.IssueAsync`, `GitEmailService.AddAsync`. **Assert what is true, not what we wish were
true.** These services honour their token before the write, so an already-cancelled token throws and
leaves no row — assert that, against the store. **Do not write a test that implies the post-commit
window is covered when it is not.** If you judge that the honest thing is also to pin the *limit* —
a test showing a post-commit cancellation does **not** remove the row — propose it in the DEVLOG with
your reasoning rather than just adding it; that test documents a weakness deliberately and I want the
decision visible, not buried in a diff.

**F2 — `LoginService.VerifyCredentialsAsync`, one line, and a hazard attached to it.**

The only cancellable await is the `SingleOrDefaultAsync` at `:59`; `passwordHasher.Verify` at `:66` is
CPU-bound and synchronous, so it cannot be interrupted — only declined. Add a cancellation check
immediately before `:66` so a client that vanished during the lookup does not buy a full Argon2id run.

**The hazard — do not erode the timing-attack defence.** `:61–64` deliberately verifies against
`DummyPasswordHash` when no account matches, so response time does not reveal whether a username
exists. Your check must sit where it fires **identically for both branches** — before `:66`, outside
any `candidate is null` conditional. A check placed inside a branch, or after the branch-dependent
work, would make cancellation timing leak account existence, which trades a CPU saving for an
enumeration oracle. Existing test `The_dummy_hash_is_a_constant_and_is_not_derived_per_request` guards
part of this; it must stay green, and so must every other `LoginServiceTests` case.

Add a test that a cancellation observed after the lookup but before the verify throws rather than
hashing. State in the DEVLOG how your test distinguishes "threw before verifying" from "threw at some
point" — a test that cannot tell those apart does not pin F2's fix.

**Scope.** `design.md`, `tests/ZeroWiki.Tests`, and exactly one production file:
`src/ZeroWiki/Identity/LoginService.cs`. **No other `src` file.** Do not touch the three create
services — the PO ruled explicitly against making them transactional. Do not touch `spec.md`; no
requirement changed. Do not tidy N4's `default` spellings.

**Constraints.**

- **Do not spawn a `reviewer` or any other agent.** Handoff is `→ @reviewer` here.
- **No mutation testing.** Both fixes are directly assertable.
- **Do not commit. Do not tick boxes** — a remediation block ticks nothing.
- **`git diff -- src` will be non-empty this time** (one file, by design). Verify before handing off
  that it contains `LoginService.cs` **and nothing else**, and run `git status --short -- src`
  alongside it, since the diff is blind to untracked files.
- Post to this DEVLOG as `**[worker]**`, referencing `F1/F2`.

**Done-gates** — all four, actual output: `dotnet build`; `dotnet test` **full suite, no filter**
(currently 379 — report the new total); `openspec validate request-cancellation --strict`;
`dotnet format --verify-no-changes`.

**[worker]** F1/F2 remediation done. `git diff -- src` is exactly `LoginService.cs`, `+8/-0`, nothing
else; `git status --short -- src` confirms — no untracked file in `src`.

**F1a — `design.md` D1 corrected.** Replaced the false "every one is transactional" sentence with the
real split: `CreateFirstAdministratorAsync`/`RedeemAsync` are transactional and roll back through
commit; `InvitationService.IssueAsync`, `GitTokenService.IssueAsync`, `GitEmailService.AddAsync` are a
bare `Add` + `SaveChangesAsync(ct)` with no post-write check, so their guarantee is narrower —
pre-write cancellation leaves nothing, post-commit cancellation does not undo it. Kept the
shown-once argument for `GitTokenService.IssueAsync` but reframed it as the reason the narrower
guarantee matters (a token that commits while the client is gone is exactly the outcome the check is
trying to prevent, and remains possible in the post-write window for this method) rather than a claim
the guarantee is total. The read/create-vs-de-authorisation split itself is untouched — only the one
sentence's mechanics were wrong.

**F1b — three tests, one per untested create, in each service's existing test file, matching the
`Revoking_under_an_already_cancelled_token_throws` / `Removing_under_an_already_cancelled_token_throws`
naming and shape already in these files:**

- `InvitationServiceTests.Issuing_under_an_already_cancelled_token_throws_and_leaves_no_invitation` —
  pre-cancelled `IssueAsync` throws `OperationCanceledException`, asserts against the store
  (`_db.Invitations` empty), not the discarded return value.
- `GitTokenServiceTests.Issuing_under_an_already_cancelled_token_throws_and_leaves_no_token` — same
  shape against `_db.GitTokens`.
- `GitEmailServiceTests.Adding_under_an_already_cancelled_token_throws_and_leaves_no_email` — same
  shape, asserted via `_service.ListAsync` (no `GitEmails` `DbSet` direct-query precedent in that file;
  `ListAsync` is the store-facing read the file already uses elsewhere).

Each test's comment says plainly what it does and does not cover: the pre-write window only, nothing
about a cancellation observed after the write commits. Traced each method by hand first to confirm the
pre-cancelled token throws where claimed: `InvitationService.IssueAsync`'s only cancellable await is
`SaveChangesAsync` itself (no read precedes it, but EF checks the token before issuing SQL, so nothing
is written); `GitTokenService.IssueAsync` is the same shape; `GitEmailService.AddAsync`'s first
cancellable await is `FindByEmailAsync`'s uniqueness read, ahead of the insert.

**Proposing, not adding, a test that pins the post-commit limit.** `CancelAfterSaveInterceptor`
(`tests/ZeroWiki.Tests/Identity/CancelAfterSaveInterceptor.cs`, from block A) cancels its linked token
the instant `SaveChangesAsync` finishes writing. For a *transactional* create that lands the
cancellation before `CommitAsync` and proves the rollback. For `GitTokenService.IssueAsync` — no
`CommitAsync` follows `SaveChangesAsync` at all — registering the same interceptor would cancel the
token only *after* SQLite's implicit autocommit for that `SaveChangesAsync` call has already durably
committed the row. The call would therefore return normally (`IssuedGitToken`, no throw), and the row
would still be present in `_db.GitTokens` afterward. I'd propose this as
`GitTokenServiceTests.A_cancellation_observed_after_the_write_commits_does_not_remove_the_token` (the
service D1 singles out for the shown-once argument, so it's the sharpest place to make the limit
concrete), asserting no throw and the row present — the mirror image of the F1b test above, deliberately
documenting the weakness rather than a regression. Not added: the brief asked for a proposal with
reasoning here, not code, so this is that proposal — Architect/Product Owner call on whether to land it.

**F2 — one line in `LoginService.cs`, placed before `passwordHasher.CanVerify`'s branch, not after.**

```csharp
var storedHashIsUsable = candidate is not null && passwordHasher.CanVerify(candidate.PasswordHash);
var hashToVerify = storedHashIsUsable && candidate is not null
    ? candidate.PasswordHash
    : DummyPasswordHash;

cancellationToken.ThrowIfCancellationRequested();

var verified = passwordHasher.Verify(password, hashToVerify);
```

**Placement, and why it cannot leak account existence.** The check sits after `hashToVerify` is
computed — the point where the known-username and unknown-username paths have already converged onto
one value — and before `Verify`, with no `candidate is null` branch anywhere between the lookup and
this line. Both paths reach the exact same line of code doing the exact same thing before either one
does anything that takes a different amount of time. There is no branch for a check placed *inside* to
diverge by, because both branches already merged upstream of it. Placing it later (after `Verify`)
would have satisfied the letter of "before the hash" only if I'd misread the line numbers; placing it
inside the `storedHashIsUsable` ternary, or inside either of the two `if (candidate ...)` blocks further
down, would have been the actual hazard the brief warned about — this sits outside all of them.

**F2 test — `LoginServiceTests.A_cancellation_observed_after_the_lookup_throws_before_verifying`,**
plus a new test-only `IPasswordHasher` decorator,
`tests/ZeroWiki.Tests/Identity/CancelOnCanVerifyPasswordHasher.cs`. Its `CanVerify` cancels a linked
`CancellationTokenSource` and delegates; its `Verify` increments a call counter and delegates. The test
adds one account, swaps in this hasher, calls `VerifyCredentialsAsync` with the linked token, and
asserts both the throw **and** `hasher.VerifyCallCount == 0`.

**How this distinguishes "threw before verifying" from "threw at some point"**, since a bare
`Assert.ThrowsAnyAsync<OperationCanceledException>` cannot: `VerifyCredentialsAsync` always calls
`CanVerify` strictly after the account lookup completes and strictly before `Verify` runs (`CanVerify`
feeds `hashToVerify`, which `Verify` then consumes) — so cancelling inside `CanVerify` lands the
cancellation in exactly that window on every run, not merely "somewhere before the method returns". If
the new check were missing entirely, the call would *still* throw eventually — either from
`SingleOrDefaultAsync` re-observing the (by-then-cancelled) token on a subsequent enumeration, or not at
all, since nothing downstream re-checks it — so a throw-only assertion would not have caught F2's actual
bug. `VerifyCallCount == 0` is the part that can only be true if the throw happened *before* the Argon2id
call specifically, which is the property F2 exists to guarantee. Confirmed by tracing: with the fix
removed (checked by hand, not by mutating `src`, since the brief caps this block to no mutation testing),
`Verify` would run to completion and `VerifyCallCount` would be `1`, failing the assertion — so the test
has the discriminating power the brief asked for.

**The dummy-hash timing-attack test stays green, and every other `LoginServiceTests` case does too** —
confirmed by the full suite run below, not merely by inspection.

**Scope.** Read `design.md`, `tests/ZeroWiki.Tests`, and exactly one production file. Did not touch the
three create services (`InvitationService.cs`, `GitTokenService.cs`, `GitEmailService.cs`) — the
Product Owner ruled explicitly against making them transactional, and F1b's tests assert the store, not
`src`. Did not touch `spec.md`. Did not touch `RedeemInvitation.razor`'s N4 `default` spellings.

**Did not spawn a `reviewer` or any other agent.** No mutation testing run — both fixes are directly
assertable, per the brief. Did not commit. Did not tick any boxes — this is a remediation block, and
every box in §4 is already ticked.

**Gates**:

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — **383/383 passed**, full suite, no filter (was 379; +4 — the three F1b tests and one
  F2 test).
- `openspec validate request-cancellation --strict` — valid.
- `dotnet format --verify-no-changes` — clean (exit 0, no output).

`git diff -- src` is exactly `src/ZeroWiki/Identity/LoginService.cs`, `+8/-0`, nothing else.
`git status --short -- src` shows only that file as `M`. Also touched (in scope):
`openspec/changes/request-cancellation/design.md`,
`tests/ZeroWiki.Tests/Identity/{InvitationServiceTests.cs,GitTokenServiceTests.cs,GitEmailServiceTests.cs,LoginServiceTests.cs}`,
and one new untracked file, `tests/ZeroWiki.Tests/Identity/CancelOnCanVerifyPasswordHasher.cs`.

→ @reviewer

**[reviewer]** Verdict on the §4 remediation block (F1/F2): **Approve, with one nit worth recording
in `## NEXT`.**

Re-ran all four gates myself, not on trust: `dotnet build` 0/0; `dotnet test` **383/383**, full suite,
no filter; `openspec validate request-cancellation --strict` valid; `dotnet format --verify-no-changes`
clean (exit 0). `git diff --stat -- src` is exactly `LoginService.cs`, `+8/-0`; `git status --short --
src` shows only that file as `M`. `git diff -- openspec/changes/request-cancellation/specs` and `git
diff -- openspec/specs` are both empty — `spec.md` genuinely untouched. `git diff -- src/ZeroWiki/
Components/Pages/RedeemInvitation.razor` is empty — N4's two `default` spellings correctly left alone.
Read `CancelOnCanVerifyPasswordHasher.cs` directly (new, untracked, 39 lines) rather than trusting the
`??`, per CLAUDE.md's new-file hazard.

**1. The timing-oracle argument — verified, not merely accepted.**

Traced `LoginService.cs:49–74` in full. `hashToVerify` (`:62–64`) is the point where the known- and
unknown-username paths converge onto one value; `ThrowIfCancellationRequested()` (`:70`) sits after
that merge and before `Verify` (`:74`), outside every `candidate is null` conditional — confirmed by
reading the method top to bottom, not by trusting the worker's comment.

The asymmetry is real: `storedHashIsUsable` (`:61`) short-circuits, so `CanVerify` — a `TryParse` over
a `$`-delimited string, no crypto (`Argon2idPasswordHasher.cs:110,139–181`) — runs only when
`candidate is not null`. That is genuine, sub-microsecond, extra work on the known-username path
before the throw point.

**Why it is still not an oracle: the only caller threads `context.RequestAborted`
(`Login.razor:70`), not a generic timeout token.** `HttpContext.RequestAborted` fires on client
disconnect (or server shutdown), so the *only* way to land a request on this new throw path is for the
client to have already closed the connection — at which point there is no HTTP response for the
attacker to receive or time. A remote timing oracle needs an observable response; this path
categorically produces none. I checked the two side-channels worth checking and both are clean:

- **No differential log line.** `VerifyCredentialsAsync` has no `try`/`catch` around the new check or
  around `Verify`; `OperationCanceledException` propagates unhandled out of `Login.razor`'s
  `SubmitAsync`, with no logging call anywhere on that path. The existing `LogInformation`/`LogError`
  calls (`:78–98`) sit *after* the throw point and are never reached once cancelled, on either branch —
  so no log entry distinguishes "cancelled after a known username" from "cancelled after an unknown
  one."
- **No HTTP/2-multiplexing angle.** Aborting one request stream while keeping the TCP connection open
  and timing a *second* request on the same connection would only surface thread-pool/GC-level noise
  common to every async operation in the app, not a signal specific to this check — and that generic
  ambient noise already exists on every cancellable read/create §2 added, not something this block
  introduces.

**Conclusion: refutation attempted, argument holds.** Not an oracle, for the standard remote-timing
threat model, and specifically because `RequestAborted` (not a timeout token) gates the whole path.

**`DummyPasswordHash` defence on the non-cancelled path — confirmed intact.** Read
`Every_rejection_performs_exactly_one_verification` (`LoginServiceTests.cs:96–129`) and
`The_dummy_hash_is_a_constant_and_is_not_derived_per_request` (`:144–152`) — both are outside the
diff (`git diff` shows no touch to either), both still pass under the 383/383 run above, and both
still assert the same constant, same call count as before F2. F2 sits strictly upstream of `Verify`
and does not change what `Verify` is called with or how often on the non-cancelled path.

**2. Does the new test cover the fix, or only half of it? Confirmed: only the known-username half —
and the gap has real teeth, but closing it costs more than this block should spend.**

`CancelOnCanVerifyPasswordHasher.CanVerify` (`CancelOnCanVerifyPasswordHasher.cs:26–31`) cancels on
entry, but `CanVerify` is only invoked when `candidate is not null` (`LoginService.cs:61`). The one
new test (`LoginServiceTests.cs:229–248`) adds an account first, so it exercises the known-username
branch exclusively. The unknown-username branch — where `hashToVerify` resolves to `DummyPasswordHash`
without ever calling `CanVerify` — has no dedicated cancellation test.

I worked through whether this is a real gap or a harmless one, and it is real: **a regression that
moved the check inside `if (candidate is not null)` — exactly the hazard the brief warned against —
would slip past the current suite entirely.** The known-username test would still pass (the check
still fires on that branch); nothing else in `LoginServiceTests.cs` asserts cancellation behaviour on
the unknown-username branch at all. That is the one regression this test exists to guard against, and
half of it is unguarded.

I also checked whether a cheap second test could close it, and it can't, for the same reason `VerifyCallCount`
was needed in the first place: the only cancellable operation before the check is
`SingleOrDefaultAsync` (`:59`) itself. A plain pre-cancelled token on an unknown username throws
*there*, before ever reaching `:70` — indistinguishable from "no fix at all" and provably so (delete
`:70` and that hypothetical test would still pass). A discriminating test for this branch needs a seam
*after* the query returns and *before* the branch decision — an EF query/reader-level interceptor,
not a password-hasher decorator, since `CanVerify` is never invoked on this branch. That is a
materially heavier instrument than this remediation block's one-line fix warrants, and CLAUDE.md's
proportionality direction ("ZeroWiki is a wiki for a small trusted group, not a system that warrants
unbounded verification") points at not building it here.

**Recommendation: not a blocker for this block; record it in `## NEXT` as a known coverage gap.** The
production code is unconditional by direct inspection (one line, no enclosing branch — I traced it,
not merely trusted the comment), which is the property that actually prevents the oracle; the test
suite's job is to be a regression guard against someone later making it conditional, and today that
guard only covers one of the two branches it should. Worth a follow-up test using a query-level
interceptor if this file is touched again, not worth reopening this block for.

**3. F1a — `design.md`'s corrected D1: accurate, and the decision itself is preserved.**

Traced all five methods directly (not re-derived from the worker's report):

- `BootstrapService.CreateFirstAdministratorAsync` (`BootstrapService.cs:104–125`) —
  `SaveChangesAsync(ct)` then `await transaction.CommitAsync(ct)` inside `await using (transaction)`.
  Transactional, confirmed.
- `InvitationService.RedeemAsync` (`InvitationService.cs:316–318`) — same shape,
  `SaveChangesAsync(ct)` then `await writeLock.CommitAsync(ct)`. Transactional, confirmed.
- `InvitationService.IssueAsync` (`:38–61`) — `db.Invitations.Add(...)` then bare
  `SaveChangesAsync(ct)`, nothing else. No transaction, confirmed.
- `GitTokenService.IssueAsync` (`GitTokenService.cs:20–36`) — same bare-`Add`-plus-`SaveChangesAsync`
  shape. No transaction, confirmed.
- `GitEmailService.AddAsync` (`GitEmailService.cs:61–90`) — a uniqueness read
  (`FindByEmailAsync`) then `Add` then `SaveChangesAsync(ct)` inside a `try`/`catch DbUpdateException`
  (a race guard, not a rollback transaction). No transaction, confirmed.

The rewritten passage states this split correctly and does not overreach in either direction — it
still says the pre-write window is safe for all five, and correctly narrows the claim only for the
post-write window on the three non-transactional ones. **The read/create-vs-de-authorisation decision
itself is untouched**: D1's actual governing rule (flow the token into reads/creates, never into
de-authorisation) is not what was wrong, and the correction doesn't touch it — only the one factual
sentence about transactionality changed, exactly as the brief asked. The `GitTokenService.IssueAsync`
shown-once argument survives, correctly reframed from "this is already covered" to "this is why the
narrower guarantee matters" — a real downgrade of a claim, not a softening of the decision.

**4. F1b — three tests, all store-level, none overclaiming.**

Traced the first cancellable await in each of the three methods by hand before trusting the worker's
claim:

- `InvitationService.IssueAsync` — no read precedes the `Add`; `SaveChangesAsync(ct)` is the only
  cancellable operation, and EF Core checks the token before issuing SQL, so a pre-cancelled token
  throws before the INSERT. Confirmed against `InvitationServiceTests.cs:280–298`, which asserts
  `Assert.Empty(await _db.Invitations.AsNoTracking().ToListAsync())` — the store, not the discarded
  exception.
- `GitTokenService.IssueAsync` — identical shape. Confirmed against `GitTokenServiceTests.cs:178–196`,
  asserting `Assert.Empty(await _db.GitTokens.AsNoTracking().ToListAsync())`.
- `GitEmailService.AddAsync` — `FindByEmailAsync`'s uniqueness read precedes the `Add`, so the
  pre-cancelled token throws there, before the insert is even constructed. Confirmed against
  `GitEmailServiceTests.cs:152–170`, asserting `Assert.Empty(await _service.ListAsync(alice.Id))` — no
  `GitEmails` `DbSet` queried directly elsewhere in this file, so routing through the service's own
  read method matches the file's existing convention rather than inventing a new one.

All three comments state plainly that they cover the pre-write window only and say nothing about a
cancellation observed after the write commits — none implies coverage it doesn't have. **None of the
three touches `src`** beyond the one permitted file, confirmed by the `git diff --stat -- src` above.

**5. F2 placement and test — re-confirmed independently.** `LoginService.cs:70` sits exactly where
the brief demanded: after the branch merge, before `Verify`. `A_cancellation_observed_after_the_lookup_throws_before_verifying`
(`LoginServiceTests.cs:228–248`) asserts both the throw *and* `hasher.VerifyCallCount == 0`, and I
worked through why that pair — not the throw alone — is what proves "threw before verifying" rather
than "threw at some point": traced the call graph and confirmed `CanVerify` always runs strictly
between the account lookup and `Verify` (it feeds `hashToVerify`, which `Verify` then consumes), so
cancelling inside it lands the cancellation in exactly that window on every run. If `:70` were deleted,
`SingleOrDefaultAsync` (`:59`) has already completed by the time `CanVerify` cancels the token — the
next cancellation-observing point would be `Verify` itself, which does not check its token (it's
CPU-bound and synchronous), so the call would run to completion, `Verify` would execute once, and
`VerifyCallCount` would read `1`, failing the assertion. Confirmed by reading `Verify`
(`Argon2idPasswordHasher.cs:72–108`) directly — it takes no `CancellationToken` parameter at all, so
there is no other mechanism that could make a deleted `:70` still produce a `0` count. **Not vacuous.**

**6. Standard lens.** No sync-over-async, no `async void`, `CancellationToken`s threaded correctly
throughout the new lines, no `IDisposable` leaks (`using var cts` in the new test), nullable enabled
and no `!` anywhere in the diff. `CancelOnCanVerifyPasswordHasher` is `sealed`, file-scoped namespace,
file name matches the type, primary-constructor style matching the codebase's existing convention
(`RecordingPasswordHasher`, `CancelAfterSaveInterceptor`). Test naming matches each file's existing
sentence-style convention. No auth/crypto hazard beyond what's addressed above; no enumeration
regression on the non-cancelled path (confirmed §1 above); Argon2id parameters and the constant-dummy
mechanism are untouched by this diff.

**On the proposed post-commit-limit test (not added, proposed in-thread) — recommend taking it.**
`GitTokenServiceTests.A_cancellation_observed_after_the_write_commits_does_not_remove_the_token`, using
the existing `CancelAfterSaveInterceptor` from block A against `GitTokenService.IssueAsync`, asserting
no throw and the row present, is worth having rather than noise:

- It converts F1a's corrected prose into an executable, regression-catching fact, in the same spirit
  CLAUDE.md already endorses for a deliberately-kept surviving mutant ("record it deliberately with
  the reason"): this is a deliberately *accepted* narrower guarantee, and a test is the durable record
  of that acceptance, not merely the DEVLOG post.
- It's cheap: the interceptor already exists, already proven reachable and non-leaking by two review
  passes in block A, and `GitTokenService.IssueAsync`'s bare-`Add`-plus-`SaveChangesAsync` shape means
  no new mechanism is needed — SQLite's implicit per-statement autocommit outside an explicit
  transaction is deterministic, so the test would not be flaky.
- It's the sharpest of the three untested creates to pin, per the worker's own reasoning — it's the
  one D1 singles out for the shown-once argument, so a future refactor that accidentally added
  transactionality (silently strengthening the guarantee, which would be *welcome* but should be
  *noticed*) or one that broke the pre-write throw (weakening it further, which would not be welcome)
  would both show up here.
- Not noise: it doesn't duplicate F1b's three new tests (those pin the pre-write window; this pins the
  post-write window — the two together are what "narrower guarantee" actually means operationally),
  and it doesn't touch `src`.

I'd take it as a small follow-on to this remediation, or fold it into whatever next touches
`GitTokenService.cs`; either way, record the decision in `## NEXT` rather than let the proposal expire
silently in this thread.

**No blockers.** `git diff -- src` confirmed clean of anything beyond the one permitted line-range in
`LoginService.cs` — no mutation residue, no drive-by change. **Approve.**

→ @architect

**[worker]** Both additions done. No further `src` changes — `git diff -- src` is still exactly
`LoginService.cs`, `+8/-0`.

**1. Post-commit-limit test — added, `GitTokenServiceTests.cs`.**

```csharp
[Fact]
public async Task A_cancellation_observed_after_the_write_commits_does_not_remove_the_token()
{
    // This pins a KNOWN LIMIT, not a guarantee — do not "fix" IssueAsync to make this fail.
    // IssueAsync has no transaction (design.md D1, corrected): it is a bare Add followed by a
    // single SaveChangesAsync, which SQLite autocommits immediately once it completes. Landing
    // the cancellation after that write — via CancelAfterSaveInterceptor, the same instrument
    // block A used to prove the *transactional* creates roll back — lands it after the row is
    // already durably committed, with nothing left to roll back. The Product Owner ruled
    // explicitly against making these three creates transactional (§4 remediation), so this is
    // the accepted boundary of the guarantee, deliberately made executable rather than left as
    // prose: the call returns normally, and the token exists whether or not the client that
    // requested it is still there to receive it.
    ...
    var issued = await interceptingService.IssueAsync(account.Id, cancellationTokenSource.Token);

    Assert.True(cancellationTokenSource.IsCancellationRequested);
    Assert.NotNull(await _service.VerifyAsync("alice", issued.Token));
}
```

Same shape as block A's `CancelAfterSaveInterceptor` usage in `BootstrapServiceTests.cs` — a locally
built, interceptor-carrying `IdentityDbContext` sharing the class's `_connection`, its own
`GitTokenService` instance, class-level fixtures untouched. Deterministic per the Architect's read:
`GitTokenService.IssueAsync` has no transaction, so `SaveChangesAsync` autocommits on `Microsoft.Data.Sqlite`'s
default (implicit, no explicit `BeginTransaction`) — nothing races. Asserts *no throw* (the call
completes normally) and the token is live via `VerifyAsync`, the mirror image of the F1b test's *throw,
no row*. Comment leads with "KNOWN LIMIT, not a guarantee" and names the Product Owner ruling, so a
future reader can't mistake it for a desirable behaviour or "fix" the production code to make it fail.

**2. Unknown-username coverage — attempted, and it worked, but not via the mechanism suggested.**

The suggested `ReaderExecutedAsync` doesn't discriminate — checked empirically before writing anything
into the test project, not assumed. I built a throwaway console spike outside the repo (`ProjectReference`d
read-only against `src/ZeroWiki`, deleted after; never touched a tracked file) and ran the real
`SingleOrDefaultAsync` shape from `VerifyCredentialsAsync` against an empty store with a
`ReaderExecutedAsync`-cancelling interceptor:

```
[interceptor] ReaderExecutedAsync fired -- cancelling now
THREW from inside SingleOrDefaultAsync itself: TaskCanceledException. IsCancellationRequested: True
```

`SingleOrDefaultAsync` throws from its *own* internal read — Microsoft.Data.Sqlite's `ReadAsync`
checks `IsCancellationRequested` and short-circuits before reading, so the query throws before
`candidate` is ever assigned, regardless of where any downstream check sits. That is exactly the
non-discriminating failure mode flagged in the brief: it would pass whether or not the fix exists.

Tried `DataReaderDisposing` instead (still a `DbCommandInterceptor` override, same family as
`ReaderExecutedAsync`, no new machinery) — fires after the row (or its absence) is already resolved,
on the way out of the query rather than mid-read:

```
[interceptor] DataReaderDisposing fired -- cancelling now
NO THROW. candidate == null: True. IsCancellationRequested: True
```

Confirmed this actually discriminates before trusting it, the same spike, two scenarios: the real
`LoginService.VerifyCredentialsAsync` (fixed code) against an unknown username under this interceptor
threw with 0 `Verify` calls; a hand-written copy with the check moved inside
`if (candidate is not null)` — the exact regression the brief named — did **not** throw and made 1
`Verify` call, same interceptor, same token. Two runs, opposite outcomes, isolating exactly the one
line that moved. That's the discriminating power the brief asked me to state.

**Landed as `tests/ZeroWiki.Tests/Identity/CancelOnReaderDisposingInterceptor.cs`** (new, same
one-class-per-file convention as `CancelAfterSaveInterceptor.cs`, with the `ReaderExecutedAsync`
non-finding recorded in its `<remarks>` so nobody rediscovers it the hard way) and
`LoginServiceTests.A_cancellation_observed_after_the_lookup_on_the_unknown_username_path_throws_before_verifying`.
Builds its own intercepting `IdentityDbContext` on the shared `_connection` and a local
`RecordingPasswordHasher`, calls `VerifyCredentialsAsync("nobody", ...)` against the (default-empty)
store, asserts the throw and `Assert.Empty(recorder.Verifications)` — the same "zero calls, not just a
throw" discipline as the known-username test, and the property the spike's Scenario 2 shows fails
exactly when the regression the brief named is present.

**Gates re-run**: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **385/385 passed**, full
suite, no filter (was 383; +2 — the post-commit `GitTokenService` test and the unknown-username `F2`
test). `openspec validate request-cancellation --strict` — valid. `dotnet format --verify-no-changes`
— clean (exit 0). `git diff --stat -- src` unchanged: `LoginService.cs`, `+8/-0`, nothing else;
`git status --short -- src` confirms. New files this round:
`tests/ZeroWiki.Tests/Identity/CancelOnReaderDisposingInterceptor.cs`; modified:
`tests/ZeroWiki.Tests/Identity/{GitTokenServiceTests.cs,LoginServiceTests.cs}`. No agent spawned, no
mutation testing, no commit, nothing ticked.

→ @architect

**[supervisor]** Second and final section review of §4 — same scope, `git diff 7a4d6e1..HEAD`, now
four commits (`7d4e20b`, `10b3b78`, `7bed029`, `0393752`). **Approve.**

`git diff 7a4d6e1..HEAD -- src` is exactly `LoginService.cs`, `+8/-0`, a pure insertion — no line
moved, nothing reordered. `git status --short` is empty: no untracked file anywhere, so no mutation
residue is possible and none is hiding from the diff. `git diff 7a4d6e1..HEAD --
openspec/changes/request-cancellation/specs` and `-- openspec/specs` are both empty. `tests/
ZeroWiki.Tests/Web` is untouched by the remediation (0 files), so the 4.5 sweep and its 15 pinned
sites are exactly as block B left them. `RedeemInvitation.razor` is untouched, so N4's two `default`
spellings were left alone as instructed. `tasks.md` across the whole range is five boxes ticked and
nothing else — the remediation ticked nothing, correctly.

---

**F1 — discharged. Not in appearance: I re-traced all five method bodies before reading the prose.**

| Create | Shape in `src` | D1's corrected claim | Match |
|---|---|---|---|
| `BootstrapService.CreateFirstAdministratorAsync` | `BeginTransaction(deferred: false)` → `UseTransactionAsync` → `await using (transaction)` → `SaveChangesAsync(ct)` → `CommitAsync(ct)` | transactional, rolls back | ✅ |
| `InvitationService.RedeemAsync` | `await using var writeLock = await BeginWriteLockedTransactionAsync(ct)` (`:267`) → `SaveChangesAsync(ct)` → `writeLock.CommitAsync(ct)` | transactional, rolls back | ✅ |
| `InvitationService.IssueAsync` | `db.Invitations.Add(...)` → `SaveChangesAsync(ct)`, nothing else | bare `Add` + save, no post-write check | ✅ |
| `GitTokenService.IssueAsync` | `db.GitTokens.Add(token)` → `SaveChangesAsync(ct)`, nothing else | bare `Add` + save, no post-write check | ✅ |
| `GitEmailService.AddAsync` | `FindByEmailAsync(ct)` → `Add` → `SaveChangesAsync(ct)` in `try`/`catch DbUpdateException` (race re-read, not a rollback) | bare `Add` + save, no post-write check | ✅ **on the load-bearing claim** |

The last row is the only imprecision left: D1 calls `AddAsync` "a bare `Add` followed by
`SaveChangesAsync(ct)`", and it is not quite bare — a uniqueness read precedes it and a
`DbUpdateException` race-guard follows it. Neither is a transaction and neither is a post-write
cancellation check, so **every claim D1 actually rests on is true**, and the guarantee it states for
that method is the right one. Recording the wording for `## NEXT`, not blocking on it: the sentence
that was false is now true, which is what F1 asked for.

Also verified the correction did not quietly rewrite the decision: the read/create-vs-de-authorisation
split and the `GitTokenService.IssueAsync` shown-once argument both survive, the latter genuinely
downgraded from "this is already covered" to "this is why the narrower guarantee matters." That is the
honest reframing, not a softening.

**F1b coverage — and `AsNoTracking()` is load-bearing, which I checked rather than assumed.** All
three new tests assert the *store*: `_db.Invitations.AsNoTracking()`, `_db.GitTokens.AsNoTracking()`,
and `_service.ListAsync(alice.Id)`. For `InvitationService.IssueAsync` and `GitTokenService.IssueAsync`
the entity has already been `Add`ed into the change tracker by the time the cancellation throws, so a
tracked query would have been at risk of returning it from the identity map; `AsNoTracking()` forces
the round-trip that makes `Assert.Empty` mean "the database is empty." `GitEmailService.AddAsync`
throws at `FindByEmailAsync`, before the `Add`, so its tracker is clean either way. Each comment says
plainly it covers the pre-write window only. None overclaims.

**S2's status, asked directly: from `Partial — 2 of 5 create sites` to `Held`.**

Five of five create sites, four of four record types named in S2 (account, invitation, git access
token, git email), each with a cancellation test asserting against the store. The post-write window is
now *documented* rather than merely absent: transactional-and-rolls-back for the two, explicitly
narrower for the three, corrected in D1 and made executable on the sharpest of them. Held **under the
Product Owner's ruled reading** — that "while a request is creating" ends when the INSERT commits.
That reading is coherent and it was theirs to make; F1 asked for the ruling and got it. See the note
to the Product Owner below on the one thing the ruling does not reach.

---

**F2 — discharged, and I satisfied myself independently on the enumeration question. The reviewer's
argument is correct, but it is the second line of defence, not the first.**

I checked the reviewer's chain and it holds: `Login.razor:70` is the only production caller (`grep`
over `src` returns exactly one hit) and it threads `context.RequestAborted`; `Program.cs` registers no
`RequestTimeouts` middleware, so `RequestAborted` really is an abort token and not a timeout token; no
logging statement in `VerifyCredentialsAsync` sits before the throw point (`:78`, `:88`, `:96`, `:102`
are all downstream of `:74`); and `CanVerify` is `storedHash is not null && TryParse(storedHash, out
_)` — a `$`-split, three tagged-int parses, two base64 decodes, no crypto
(`Argon2idPasswordHasher.cs:110`, `:139–181`).

**But the argument I would actually stake the change on is simpler and does not depend on the caller
at all:**

1. **The fix is a no-op on every path that produces a response.** `ThrowIfCancellationRequested()` on
   an uncancelled token is one volatile read, identical on both branches. The diff is a pure insertion
   at `:66–73`; `:61`'s `CanVerify` short-circuit is pre-existing and unmoved. So the timing profile
   of every response-producing login is byte-for-byte what it was before this change. **A change that
   cannot alter the response path cannot introduce a response-timing oracle**, whatever token is
   passed.
2. **On the cancelled path, both branches throw the identical exception from the identical line.**
   Same type, same message, same token, same stack frame (`:70`). So there is no differential in logs,
   in `UseExceptionHandler`'s handling, or in any telemetry — not merely "no log line distinguishes
   them", but *nothing downstream can*, because the two branches are indistinguishable at the point
   they diverge from normal flow.
3. **Only then** does `RequestAborted` matter: it closes the residual case where an attacker might try
   to measure the cancelled path itself. There is no response to time, and the sub-microsecond
   `CanVerify` delta sits under a SQLite round-trip's own jitter.

**Verified, not refuted. It is not an enumeration oracle.** One qualification, because point 3 is the
part that could decay: the residual asymmetry is real (`CanVerify` runs only when `candidate is not
null`), and points 1–2 are what make it unobservable. If a future change ever passed a *timeout* token
here — `RequestTimeouts`, a `CancelAfter`, a linked deadline — point 3 fails and a 504's latency would
carry that asymmetry. That is cheap to retire permanently, and the fix is strictly better than the
current placement: **move the check up one statement, to immediately after `SingleOrDefaultAsync` at
`:59` and before `:61`.** It is still unconditional, still fires on both branches, still declines the
Argon2id run — and neither branch reaches `CanVerify` first, so the asymmetry ceases to exist rather
than being argued unobservable. Not a blocker; the current line is correct today. `## NEXT`.

**F2's tests discriminate — I checked both, and they discriminate for different reasons.**

- Known-username (`LoginServiceTests.cs:229–248`): `Assert.Equal(0, hasher.VerifyCallCount)`. Delete
  `:70` and `Verify` runs once — `VerifyCredentialsAsync` has no other cancellation-observing point
  after the query (`Argon2idPasswordHasher.Verify` takes no token). Fails. Discriminating.
- Unknown-username (`:251–286`): `Assert.ThrowsAnyAsync` **plus** `Assert.Empty(recorder.Verifications)`.
  Delete `:70`, or move it inside a `candidate is not null` branch, and this call does not throw at
  all — it returns `null` after one dummy verify. Fails on the throw assertion *and* the count. This
  is the branch the first test cannot reach, and it is the exact regression the brief warned against.

Between them the two tests pin unconditionality on both branches, which is the property that actually
prevents the oracle. Good instrument design, and the second test is the one that makes the pair worth
having.

---

**All seven scenarios, re-run after the remediation.**

| # | Scenario | What pins it now | Verdict |
|---|---|---|---|
| S1 | A read is abandoned when the client disconnects | 4.4 `BootstrapServiceTests.cs:166–183` + 4.5's `Read_and_create_calls_flow_a_request_scoped_token` × 12 sites — **and now** both new `LoginServiceTests` cases, which abandon the login lookup and prove the work behind it is declined | **Held, and strengthened.** The gap I named last round — the *specific* read the proposal was written about — is closed on both branches. |
| S2 | A cancelled create leaves nothing behind | 4.1 `BootstrapServiceTests.cs:113–159`, 4.2 `InvitationRedemptionTests.cs:82–155`, **+ F1b** `InvitationServiceTests.cs:280–298`, `GitTokenServiceTests.cs:178–196`, `GitEmailServiceTests.cs:152–170`, **+** the post-write limit pinned at `GitTokenServiceTests.cs:198–225` | **Held** (was *Partial — 2 of 5*). 5/5 create sites, 4/4 record types. Post-write window: rolls back for the two transactional, deliberately narrower for the three, per the Product Owner's ruling. |
| S3 | A cancelled redemption leaves the invitation usable | 4.2's two tests, both going past "not redeemed" to a *successful subsequent redemption* under a live token | **Held.** Unchanged, and still the strongest evidence in the section. |
| S4 | Revoking a git access token survives a disconnect | 4.3 `GitTokenServiceTests.cs:179–197` + 4.5 exact-text on `Account.razor:320` + pre-existing `Revoked_token_no_longer_verifies` | **Held by inference.** Unchanged by the remediation; links (a) and (b) discharged, (c) traced not tested. N7. |
| S5 | Revoking an invitation survives a disconnect | 4.3 `InvitationServiceTests.cs:281–300` + 4.5 on `Invitations.razor:154–158` + pre-existing `A_revoked_invitation_cannot_be_redeemed_and_creates_no_account` | **Held by inference.** As S4. |
| S6 | Removing a git email survives a disconnect | 4.3 `GitEmailServiceTests.cs:153–171` + 4.5 on `Account.razor:340` + pre-existing `Removing_an_email_frees_the_address_for_another_account` | **Held by inference.** As S4. |
| S7 | A cancelled bootstrap check does not admit a bootstrap | 4.4, against an **empty** store, polarity correct per `0a38e46` | **Held.** Still the single sharpest assertion in the change. |

Two changed rows, both in the direction the remediation was carved for; five unchanged. Beyond the
seven: the **proposal's stated Why** — "an Argon2id verify at 64 MiB continues to completion for a
response nobody will read" — is delivered for the first time by `LoginService.cs:70`. It is not a spec
scenario, and F2 was never an S1 violation; it was the change failing to buy the thing it was proposed
to buy. It now buys it.

---

**Your five direct questions, answered.**

**4. The limit-documenting test — honest and well-marked, and pinning it was right.** It was item 2 of
my own suggested remediation shape and I would take it again: it converts F1a's corrected prose into
something a regression can fail, and it is the mirror of the pre-write test — the two together are
what "narrower guarantee" means operationally. Determinism confirmed: `CancelAfterSaveInterceptor`
fires at `SavedChangesAsync`, after the write has committed, and `IssueAsync` then executes only
`return new IssuedGitToken(...)` — nothing left that observes the token, so the call cannot
intermittently throw. `_service.VerifyAsync` reads through the same `_connection`, so the assertion
sees the committed row.

**One wording flaw, for `## NEXT`.** The comment opens *"do not `fix` IssueAsync to make this fail."*
That is accurate as a statement of the current ruling and it names the ruling, which is the right
instinct. But it gives a future reader no exit: if a later change makes `IssueAsync` transactional —
a strict improvement, and exactly the outcome the reviewer wanted this test to make *visible* — the
test fails, and the comment as written reads as an instruction to revert the improvement. Add one
sentence: *"if this fails because `IssueAsync` became transactional, that is a strengthening — delete
this test, do not revert the code."* Docs-only, one line. It is the difference between a test that
documents a limit and a test that defends one.

**5. The helpers — `DataReaderDisposing` does discriminate; three helpers is *reducible to two*, and
the reason is build order.**

`DataReaderDisposing` fires from the reader's disposal, in the `finally` after enumeration has already
resolved the result; `SingleOrDefaultAsync` has captured its value by then and re-checks nothing on
the way out. That matches the worker's spike, and it is corroborated structurally rather than only by
the spike: the test passes today (385/385) and the *only* cancellation-observing point between the
query and `Verify` is `:70`, so a throw with zero verifications can have come from nowhere else. The
`ReaderExecutedAsync` non-finding is exactly right and recording it in the type's `<remarks>` was the
correct call — that is a genuine instrument-blindness trap of the kind CLAUDE.md already warns about,
and it is now written down where the next person will hit it.

On sprawl: the three are not duplicates — each lands a cancellation in a different window (post-write/
pre-commit; post-query/pre-caller-code; post-lookup/pre-verify) and all three are live. But
`CancelOnCanVerifyPasswordHasher` is **reducible**: `CancelOnReaderDisposingInterceptor` lands the
cancellation after the lookup on *both* branches, so the known-username test could be written with it
and assert `Assert.Empty(recorder.Verifications)` identically. The hasher decorator exists only
because it was built in round 1, before the more general instrument was built in round 2 to close the
gap round 1's reviewer found. That is the ordinary signature of an abstraction that grew twice across
rounds, and it is my remit to name it — but it is 39 lines behind a passing test, not dead
scaffolding, and it retires naturally alongside the `:59` placement change above (which would break it
anyway, since `CanVerify` would no longer be reached). Both belong in the same small follow-up.
`## NEXT`.

**6. Nothing already approved was disturbed.** Verified above: `src` outside `LoginService.cs`
untouched across the whole range, `spec.md` byte-identical, `tests/ZeroWiki.Tests/Web` untouched so the
15 pinned sites and the sweep instrument are as block B left them, `RedeemInvitation.razor`'s N4
spellings untouched. §1–§3 are outside this range and unaffected.

**7. Standard remit.** No drift between the remediation and blocks A/B: the three F1b tests match the
`Revoking_under_an_already_cancelled_token_throws` naming and shape already established in those
files, and the post-commit test reuses block A's interceptor unchanged and by the same locally-built-
`IdentityDbContext`-on-the-shared-`_connection` pattern as `BootstrapServiceTests`. No dead
scaffolding, no superseded stub, no eroded decision — the remediation touches no design decision
except to make D1's prose true. One cosmetic spelling inconsistency: the round-1 F2 test uses `using
var cts`, while block A and both round-2 tests use `var cancellationTokenSource = new
CancellationTokenSource()` without `using`. The section's established convention is the latter; the
`using` is the better spelling. Nit either way.

---

**The record repair, and the same gap recreated three posts below it.**

`7bed029`'s `[architect]` post states block B's gap accurately: the reviewer's `Approve with nits` at
line 1759 does predate the depth-aware hardening at line 1887, there is no reviewer post after it, and
owning that as a deliberate trade rather than an oversight is the right way to close it.

**But the same thing then happened inside the remediation block.** The reviewer's `Approve` (line
2375) was given on a **383-test** state. The `[worker]` post below it (line 2564) landed two further
tests **and a new file**, `CancelOnReaderDisposingInterceptor.cs`, taking the suite to **385**, and
there is no reviewer post beneath it. Both additions were reviewer-*recommended*, so the substance was
reviewed; the *implementation* was not — including a new type whose justification rests on a throwaway
spike run outside the repo and since deleted, i.e. the one artefact in this block that a second pair of
eyes was most warranted on. I have read all of it in that pass's place and it is correct (above), so
again the gap is in the record, not the code. Recording it because a post that repairs a record gap
while the same gap is being recreated below it is not yet a repair.

**Before archive, three record items are still missing**, and they are cheap to post:

1. An `[architect]` close for **block B** — the repair owns the skipped review but does not close the
   block.
2. An `[architect]` gates-and-commit post for the **remediation block** (`0393752`): build 0/0, `dotnet
   test` 385/385 full suite, `openspec validate --strict` valid, `dotnet format` clean, and `git diff
   -- src` = `LoginService.cs` `+8/-0`. Those figures currently exist only in the worker's post and in
   the brief to me; the DEVLOG has no Architect verification of them.
3. `## NEXT` still reads **"STOPPED … Do not archive"** with §4 marked `Request changes` in the
   table. Stale as of this post.

---

**To the Product Owner, before archiving — one thing, and it is the only one.**

`specs/request-lifecycle/spec.md` was correctly left untouched, but it is what gets promoted into
`openspec/specs/` at archive and becomes durable law. Its Requirement text reads: *"A cancelled
operation of this kind SHALL leave the store exactly as it was before the request arrived"*, and S2:
*"the operation is abandoned and no such record exists afterwards"* — both unqualified. `design.md`
D1 now correctly says that for three of the five creates the guarantee stops at the commit. **The
change ships with its committed spec stating something stronger than its design says the code does.**

Your F1 ruling settles the *behaviour* (it stays as it is) and it settles the *reading* (creating ends
at the INSERT). It does not settle whether the promoted spec should carry that qualification. Under
your reading the two documents are consistent; a reader who has only the spec will read it more
strongly. One clarifying clause in S2 at archive time — or a deliberate decision to leave it — is your
call, not mine, and it is worth thirty seconds now because after archive it is a new change.

Everything else below is `## NEXT` material and none of it blocks.

**Approve.** §4 is closed. All 16 tasks ticked, four sections with supervisor approval, and the change
now delivers the benefit its proposal was written for — which, before `0393752`, it did not.

**For `## NEXT`:**

- **N6 — the 4.5 sweep is bounded to six named files** (carried forward). A seventh page, or a
  de-authorisation moved into a shared component, layout or endpoint, is invisible to it. Same expiry
  date as N5; the two should be re-run together against `git-backed-content-core`'s new surface, which
  per N5 these caller-side instruments will not see at all once de-authorisation stops being an
  identity-row write.
- **N7 — the harness premise** (carried forward). `ZeroWikiAppFactory.WithDbAsync` makes store-level
  assertions after an aborted request reachable; "no response to assert against" was never the
  obstacle for S4/S5/S6. Re-examine before the next change reuses the 4.3 re-cut as precedent.
- **N9 — hoist `LoginService.cs:70` above `:61`.** One statement earlier, immediately after the
  `SingleOrDefaultAsync` await. Retires the `CanVerify` branch asymmetry outright instead of arguing it
  unobservable, and removes the dependency on the caller's token being an abort token rather than a
  timeout token. Do it together with N10.
- **N10 — `CancelOnCanVerifyPasswordHasher` is reducible to `CancelOnReaderDisposingInterceptor`.**
  The latter lands the cancellation after the lookup on both branches; the former exists only because
  it was built a round earlier. N9 breaks it anyway (`CanVerify` would no longer be reached), so
  collapse the two tests onto one instrument and delete the decorator when N9 lands.
- **N11 — the limit-documenting test needs an exit clause.**
  `GitTokenServiceTests.A_cancellation_observed_after_the_write_commits_does_not_remove_the_token`
  says *"do not `fix` IssueAsync to make this fail"*. Add: *"if this fails because `IssueAsync` became
  transactional, that is a strengthening — delete this test, do not revert the code."*
- **N12 — `spec.md` S2 and Requirement 1 are unqualified where D1 is now qualified.** Product Owner
  call at archive: add a clause, or accept the tension deliberately.
- **N13 — process, recurring.** Two blocks in this change committed work that landed *after* the
  reviewer's approving post: block B's depth-aware hardening (`10b3b78`) and the remediation's final
  two tests plus `CancelOnReaderDisposingInterceptor.cs` (`0393752`). Both times the code was correct;
  both times the archived record shows an approval that does not cover the shipped state. A reviewer
  re-pass is cheap when the addition is small — cheaper than the DEVLOG post explaining why it was
  skipped.
- **N14 — D1's `GitEmailService.AddAsync` wording.** Called "a bare `Add` followed by
  `SaveChangesAsync(ct)`"; it has a preceding uniqueness read and a following `DbUpdateException`
  race-guard. Every claim D1 rests on is true; the description is loose. Fix if D1 is ever edited
  again.
- **Nit** — `ServiceCallSweepInstrumentTests.cs:142–154` is entailed by the test above it (carried
  forward, unaddressed and correctly so).
- **Nit** — CTS spelling drift: `using var cts` in `LoginServiceTests.cs:239` against the section's
  established `var cancellationTokenSource = new CancellationTokenSource()` in the other four sites.
- **Note for the archive** (carried forward) — the pre-existing revoke/remove tests call the services
  with the token **omitted** (`= default`), the very spelling D2 rejects at call sites. Behaviourally
  identical, so not a defect; but the suite does not follow the convention the suite now enforces.

**[architect]** Closing §4 and the change's build record. Three posts owed, per N13 and the
supervisor's archive list — written now rather than left for the archive to inherit.

**Block B close (owed since `10b3b78`).** Reviewer `Approve with nits` → nit 1 (depth-aware argument
splitting) taken, nit on the redundant self-test left. Gates re-run by me: build 0/0, 379/379 full
suite, validate valid, format clean. 4.3/4.5 ticked; committed `10b3b78`. The hardening itself shipped
without a reviewer pass — owned separately above.

**Remediation close (`0393752`).** Reviewer `Approve` at the 383-test state; I then commissioned two
further additions (the post-write limit test, and the unknown-username coverage) taking it to 385, and
**did not send those back for a reviewer pass**. Gates re-run by me: build 0/0, **385/385** full suite,
validate valid, format clean. `git diff -- src` across the whole remediation is `LoginService.cs`
`+8/-0`. Nothing ticked — a remediation block ticks nothing.

**N13 is fair and I am recording it as a pattern, not an incident.** I repaired the block B record gap
in `7bed029` and then recreated the same gap three posts later: an `Approve` given on one state, more
code added after it, no re-review, and my own close post missing. Twice is a habit. The specific
lesson for the next change: **a reviewer's `Approve` certifies the state it was given, and any code
added after it — however small, however well-briefed — is uncertified until it is either re-reviewed or
the Architect posts explicitly that it was verified without one.** Both times the code was fine and
both times a supervisor had to find the hole. The DEVLOG is meant to be the durable record of *how*
this was built; a hole in it is a defect in the deliverable, not merely in process hygiene.

**[reviewer]** Audit of the N12 spec-text edit — `openspec/changes/request-cancellation/specs/request-lifecycle/spec.md`, uncommitted. Traced against code, not prose.

**1. The five-create split, verified line by line.**

- `BootstrapService.CreateFirstAdministratorAsync` (`BootstrapService.cs:98–128`) — `BEGIN IMMEDIATE`
  via `connection.BeginTransaction(deferred: false)`, enlisted, `SaveChangesAsync` then
  `transaction.CommitAsync` inside `await using (transaction)`. Cancellation before commit disposes
  the transaction without committing → rollback. **Matches "transactional, rolls back through
  commit."**
- `InvitationService.RedeemAsync` (`InvitationService.cs:267–319`) — same shape via
  `BeginWriteLockedTransactionAsync`/`WriteLock`, `SaveChangesAsync` then `writeLock.CommitAsync`
  inside `await using`. **Matches.**
- `InvitationService.IssueAsync` (`InvitationService.cs:58–59`) — `db.Invitations.Add(...)` then a
  bare `await db.SaveChangesAsync(cancellationToken)`, no transaction, no check after. **Matches
  "single statement, no post-write check."**
- `GitTokenService.IssueAsync` (`GitTokenService.cs:32–33`) — identical shape. **Matches.**
- `GitEmailService.AddAsync` (`GitEmailService.cs:84–101`) — the write itself is a bare `Add` +
  `SaveChangesAsync` with no transaction, so the cancellation-property claim holds. But the method
  also has a preceding `FindByEmailAsync` uniqueness read and a `catch (DbUpdateException)` race-guard
  around the write, so "write in a single statement" is loose in exactly the way N14 already flagged
  for `design.md`'s D1 wording. The `DbUpdateException` catch cannot swallow a cancellation (EF
  surfaces `OperationCanceledException` directly, never wrapped), so the property is still true — the
  looseness is descriptive, not factual. **Nit**, not a blocker, but this text is now durable law where
  D1 was narrative; worth tightening (e.g. "a bare insert with no cancellation check after the write")
  if the paragraph is touched again.

All five match the split the spec now asserts. No mischaracterisation found.

**2. New scenario vs. its test.** `GitTokenServiceTests.A_cancellation_observed_after_the_write_commits_does_not_remove_the_token`
(`GitTokenServiceTests.cs:199–226`) uses `CancelAfterSaveInterceptor`, which cancels the linked token
inside EF's `SavedChangesAsync` callback — after the single `INSERT` has executed and (SQLite,
no ambient transaction) autocommitted, before `IssueAsync` returns. The call completes normally
(`cancellationTokenSource.IsCancellationRequested` is asserted true) and `VerifyAsync` afterward proves
the token exists. That is exactly the scenario's WHEN ("disconnects after ... committed the write")
and THEN ("the record remains"). **Match confirmed.**

One gap worth naming: only `GitTokenService` has this post-commit test. `InvitationService.IssueAsync`
and `GitEmailService.AddAsync` have only the pre-write ("already-cancelled token throws, leaves no
row") tests. The scenario's WHEN names all three record types, but only one third of it is backed by
an executable proof — the other two rest on code-shape parity (identical bare-`Add`+`SaveChangesAsync`,
no transaction) rather than their own test. Consistent with this project's mutation-testing
proportionality (don't triple a redundant instrument), and N11 already flags the one test that exists
needs an exit clause — so I'm not asking for two more tests. Flagging only so nobody reads the DEVLOG
later as "the new scenario is proven three ways" when it's proven one way and inferred twice.

**3. Qualification did not overshoot.** All three pre-write tests
(`InvitationServiceTests.Issuing_under_an_already_cancelled_token_throws_and_leaves_no_invitation`,
`GitTokenServiceTests.Issuing_under_an_already_cancelled_token_throws_and_leaves_no_token`,
`GitEmailServiceTests.Adding_under_an_already_cancelled_token_throws_and_leaves_no_email`) assert
against the store (`Assert.Empty(...)` / `Assert.Empty(await ...ListAsync(...))`), not just the thrown
exception. A pre-commit cancellation genuinely leaves nothing, for all five creates — the qualifier
narrows the guarantee to what commits, it does not license a row surviving an abandoned pre-commit
write.

**4. Consistency with the untouched requirements.** Confirmed via `git diff` that only Requirement 1
and its scenarios changed — De-authorisation and the bootstrap-gate requirement are byte-identical.
The new scenario's WHEN is scoped to "creates an invitation, a git access token, or a git email
association" — it cannot be read as touching revocation/removal, which live under a separate
Requirement heading with their own distinct verbs ("Revoking ...", "Removing ..."). No contradiction,
no licence for de-authorisation to be abandoned.

Architectural note (not blocking): the new scenario omits account creation (bootstrap/redemption) from
its WHEN, even though those two are equally non-reversible once their transaction actually commits.
That's a defensible scoping choice — post-commit durability for a committed transaction is a trivial DB
property, not the narrower-guarantee finding N12 exists to document — but it's the kind of omission a
future editor could misread as "accounts are exempt from this limit." Worth a half-sentence if the
paragraph is revisited.

**5. OpenSpec conformance.** Heading levels and scenario formatting match the surrounding file exactly
(`#### Scenario:`, `- **WHEN**` / `- **THEN**` with the established continuation-line indent). Explanatory
prose after a SHALL statement is already this file's convention (see the de-authorisation requirement's
own paragraph) — not a new pattern being introduced. `openspec validate request-cancellation --strict`
→ **valid**.

**6. Stranger-reading.** Clear, not a hedge bolted on. The requirement states the guarantee and its
boundary in one sentence ("...where the cancellation is observed before the operation's write
commits"), then the very next paragraph names which two creates are transactional and which three
are not, in domain language (no method names) consistent with the rest of the file's voice. A reader
with no DEVLOG in front of them gets the rule and the reason in two paragraphs, and the new third
scenario makes the limit an assertable fact rather than something they'd have to infer from the
requirement text alone. The "where" clause is slightly informal for a normative SHALL sentence — "provided
that" or a second sentence would read marginally more like a legal requirement — but it is not
ambiguous and I would not hold up an approval for it.

**Verdict: Approve.** No blockers. Two nits (GitEmailService's "single statement" phrasing; the
1-of-3 test coverage of the new scenario, noted so it isn't over-read later) and one architectural
note (the new scenario's account-creation omission) — none change the truth of what's promoted, and
none need a re-audit. → @architect, clear to promote on archive per the Product Owner's N12 ruling.

## NEXT

**Resume point: COMPLETE AND CLEARED FOR ARCHIVE — all four sections closed with a supervisor
`Approve`, 16/16 tasks, 385/385 tests, and no outstanding Product Owner decision.** The last one,
**N12** (whether the promoted spec text carries the qualification D1 now states), was ruled *qualify
it now* and landed in `e5413d4`; the reviewer audited that edit and signed off — "clear to promote on
archive". Everything below is `## NEXT` material for later changes, not work outstanding on this one.

Gates re-run on the final tree before archiving: `dotnet build` clean, `dotnet test` 385/385,
`dotnet format --verify-no-changes` clean, `openspec validate request-cancellation --strict` valid,
working tree clean.

| Section | Block | Commit | Reviewer | Supervisor |
|---|---|---|---|---|
| §1 The rule | 1.1–1.2 | `ff14989` | Approve | Approve |
| §2 Reads and creates | 2.1–2.6 | `1eaa13f` | Approve | Approve |
| §3 De-authorisation | 3.1–3.3 | `61c482a` | Approve | Approve |
| §4 Tests | A 4.1/4.2/4.4 | `7d4e20b` | Approve | Request changes → **Approve** |
| §4 Tests | B 4.3/4.5 | `10b3b78` | Approve w/ nits | Request changes → **Approve** |
| §4 Tests | remediation F1+F2 | `0393752` | Approve | **Approve** |

Out-of-band commits: `f24c9ab` (§1 close), `0a38e46` (`design.md` polarity fix, §2's base), `2eead9c`
(`.claude/agents/worker.md`, process), `7f63128` (§3 close), `7a4d6e1` (4.3 re-cut), `7bed029` (§4
supervisor verdict + record repair), `e5413d4` (N12 — qualify the promoted spec text).

**Tests 344 → 385.** Single production change across the whole change: `LoginService.cs`, `+8/-0`.

### The seven spec scenarios, as finally delivered

| # | Scenario | Verdict |
|---|---|---|
| S1 | A read is abandoned on disconnect | Held, strengthened by F2 |
| S2 | A cancelled create leaves nothing behind | Held (was Partial — 2 of 5 sites) |
| S3 | A cancelled redemption leaves the invitation usable | Held — strongest evidence in the change |
| S4 | Git token revocation survives a disconnect | Held **by inference** (see N7) |
| S5 | Invitation revocation survives a disconnect | Held **by inference** |
| S6 | Git email removal survives a disconnect | Held **by inference** |
| S7 | The bootstrap gate cannot fail open | Held — sharpest assertion in the change |

**S4/S5/S6 are held by inference, and that is worth stating plainly** rather than letting the table
read as seven equal ticks. 4.5 proves the callers pass an uncancellable token; 4.3 proves the services
honour a token that *is* cancellable; the pre-existing revoke tests prove revocation takes effect. What
is traced but not tested is that nothing else in the pipeline abandons the work between them — that is
N7, and it is the only link where the scenarios' literal *WHEN a client disconnects* enters.

### The two supervisor blockers — both resolved

**F1** — `design.md`'s D1 falsely claimed all five creates were transactional; three are not. Product
Owner ruled: correct the doc, add the coverage, **do not** make the three transactional. Landed in
`0393752`. S2 moved Partial → Held, 5/5 create sites, under the PO's reading that "while a request is
creating" ends at the INSERT.

**F2** — the change did not deliver the benefit it was proposed for: `LoginService`'s only cancellable
await preceded the Argon2id verify. Product Owner approved a one-line `src` fix as a deliberate scope
expansion beyond the proposal's Impact. The proposal's stated Why is delivered for the first time by
`LoginService.cs:70`.

### A premise this change acted on turned out to be wrong

**A page-level seam *does* exist.** `tests/ZeroWiki.Tests/Web/ZeroWikiAppFactory.cs` is a real
`WebApplicationFactory<Program>` with `WithDbAsync` store access, already driving authenticated revoke
POSTs — sitting in the very folder block B added its four files to. The "no seam exists" reasoning that
the §2 supervisor established, that the PO's 4.4 ruling rested on, and that block B's brief repeated,
was **narrower than it was stated**: it is true of *cancelling a request mid-flight and asserting on the
response* (S4/S5/S6 never needed a response) and true of `BootstrapService` being `sealed`, but it was
carried forward as though no page-level testing were possible at all. Recorded because the conclusion
may still be right while the reasoning was not, and a later change should not inherit the overstatement.

**§3's substantive result, beyond the ticked boxes:** the §3 supervisor traced POST → committed row
rather than checking the argument, and the requirement *"de-authorisation completes regardless of the
client"* holds **end to end**, not merely at the call site. No cache (so "no longer authenticates" is
true of the live row, not just of a column), no background or detached work (so the scoped
`IdentityDbContext` cannot be disposed under an in-flight `SaveChangesAsync`), no second token source
inside the services. The `None` write is awaited inline on the request's own path throughout.

### Process notes that held for §1–§3 and should hold for §4

- **State in the brief that the worker must not spawn its own `reviewer`, or any other agent.** §2's
  block came back with a verdict already attached because its worker commissioned its own review — an
  audit the audited party arranged. `.claude/agents/worker.md` was amended in `2eead9c` to forbid it,
  but **do not rely on the agent definition alone**: whether a running session re-reads
  `.claude/agents/*.md` per spawn or caches them at startup was not established. §3's brief carried
  the constraint explicitly and it was honoured. The handoff is the `→ @reviewer` line in this DEVLOG;
  the Architect reads it and commissions the review.
- **The three sweep instruments used so far, so §4.5 does not repeat one and call it corroboration.**
  §3's worker ran three regex passes (known method names, then a verb-based pass independent of the
  method list); the §3 reviewer used CodeGraph's symbol graph; the §3 supervisor started from the
  *store* instead of from the services and enumerated every persistence write in `src` (8
  `SaveChangesAsync`, 5 `Add`/1 `Remove`, zero `ExecuteDelete/UpdateAsync`, zero raw SQL, zero file
  or cookie deletes) — five creates and three de-authorisations, exactly the three §3 handled. All
  three agree there is no fourth site.
- **§3.3's sweep — two known non-findings**, confirmed by three supervisors now. Do not report as gaps:
  - `BootstrapStartupExtensions.LogBootstrapStateAsync` (`Program.cs:74`) takes no token, but is a
    startup path, not de-authorisation. (§1 supervisor.)
  - `Logout.razor:44` `context.SignOutAsync(...)` is withdrawal-*shaped* but takes no token and
    touches no store row. (§2 supervisor.)

### Before §4 opens — items needing a decision

- **⛔ N1 — task 4.3 is red by construction at the service level. This is a Product Owner call and
  §4 is stopped pending it.** 4.3 asks for "revocation completes under an already-cancelled token,
  one per de-authorisation path". At the service level that assertion is *false by design*: the
  services correctly honour their token, so `RevokeAsync(accountId, tokenId, cancelled)` throws at
  `GitTokenService.cs:108` before it ever reaches `SaveChangesAsync`. **D1's guarantee is a property
  of the caller, not of the service** — the page passes `None`; the service is and should remain
  cancellable. 4.3 has 4.4's missing-seam problem, but **the PO's 4.4 resolution does not transfer**:
  taken service-level, 4.3 asserts the opposite of its requirement. A §4 worker inheriting "4.4 goes
  service-level" will extend it to 4.3, go red, and improvise one of three bad answers — a vacuous
  assertion, a page seam that does not exist, or making the services ignore their token, which
  contradicts `design.md`'s explicit Non-Goal ("Any change to service signatures"/behaviour). Raised
  to the Product Owner; do not let a worker resolve it.
- **N2 — §3 has no behavioural delta, and that changes what §4 can prove.** Before `61c482a` the three
  sites omitted the argument and inherited `= default`, which *is* `CancellationToken.None`. Runtime
  behaviour is byte-identical pre- and post-§3. That is exactly what D2 claims to do — make a silent
  default into a visible decision — but the consequence is that **no behavioural test at any level can
  distinguish pre-§3 from post-§3 code.** §4.5's source sweep is therefore the only mechanical evidence
  §3's work exists. Brief it as §4's *primary* test, not its last.
- **§4.4 is settled (Product Owner).** Assert at the **service level against an empty store** that
  `IsAvailableAsync(cancelled)` throws. No seam, no unsealing. The empty store is load-bearing.

- **Why §4.4 has no page-level seam, from the §2 supervisor** — the reasoning behind the ruling above.
  `BootstrapService` is
  `sealed` (`BootstrapService.cs:13`) and DI-registered by concrete type (`Program.cs:19`); the test
  project has no mocking library and no component-render harness; and a cancelled HTTP request yields
  no response to assert against. **Assert §4.4 at the service level in `BootstrapServiceTests.cs`,
  against an empty store.** The empty-store setup is load-bearing: it is what makes the assertion
  distinguish *throw* from *fail open* (`true`) rather than merely from *fail closed* (`false`).
  Asserting at the page level would need an interface or unsealing `BootstrapService` — a
  proposal-level call, not §4's to invent. If page-level coverage is wanted, that is a Product Owner
  question before §4 starts, not a worker's improvisation.
- **Mind the polarity when briefing §4.4.** `IsAvailableAsync` is `!AnyAsync(…)`, so `true` = store
  empty = bootstrap **open**, and failing open is returning `true`. `design.md`'s Risks section stated
  this backwards until `0a38e46`; task 4.4's own wording was always correct. An assertion written
  against the wrong value passes while proving nothing.
- **§4.1 rests on solid ground** — verified rather than assumed. §2 made
  `CreateFirstAdministratorAsync` genuinely cancellable for the first time, and `BootstrapService.cs:104–128`
  still rolls back safely: cancellation between `SaveChangesAsync(ct)` and `CommitAsync(ct)` throws
  pre-commit into a token-less `await using` rollback. (§2 supervisor; a composition no block review
  would have looked at.)
- `InvitationService.WriteLock.DisposeAsync()` (`InvitationService.cs:436–440`) takes no token and
  **must not** — it is the rollback path, which is *why* §4.1/§4.2's "a cancelled create leaves
  nothing behind" holds. Not a 1.2 counter-example.

### Architectural notes — no action, recorded so they are not rediscovered as surprises

- **N3 — the likeliest future erosion of D2 is the neighbour, not the annotated line.** In each of the
  three 6–8 line handlers, a `CancellationToken.None` write sits two lines from a `RequestAborted`
  read (`Account.razor:322`, `:342`, `Invitations.razor:161`). D2's comment annotates the write;
  nothing explains why the line below it differs. That adjacency *is* the "let's make these
  consistent" pass D2 exists to defend against, now sitting inside a single method body. Also note the
  read-after-write ordering is load-bearing: the trailing `RequestAborted` read throws on a dead
  client, but only *after* the commit — nothing states or tests that ordering.
- **N4 — two of the fifteen sites spell the token `default`, not `CancellationToken.None`.**
  `RedeemInvitation.razor:116,125` (§2's lines), whose comments name `CancellationToken.None` while
  the code says `default`. Cosmetic, a one-word fix, recorded rather than requested — but it is the
  only spelling drift across the fifteen.
- **N5 — the §3 sweep's soundness has an expiry date.** It rests on every withdrawal of access being
  an identity-row write. `git-backed-content-core` introduces files, git refs, `flock`, and
  `VerifyAsync`'s first-ever caller — after which "de-authorisation ⇒ writes an identity row" stops
  being true and the sweep would need re-running against the new surface, not merely re-read.
- **D1 is discoverable in `src` only from the de-authorisation side.** The Product Owner ruled the
  §1 remarks go on three methods, not five, and that ruling stands. What landed is three *instructions
  to callers*, not D1's *criterion* — the fail-safe-direction rule that generates them appears nowhere
  in `src`, so a seventh-service author must still recognise unaided that their method is
  withdrawal-shaped. Closing it would need one sentence of criterion, not two more remarks. A decision
  for when that service arrives.
- **Five unreachable throws now, up from three.** `Bootstrap.razor:64–65` and
  `BootstrapComplete.razor:30–31` joined the three pre-existing ones, and `RedeemInvitation`'s
  null-tolerance branch is unreachable for the same reason: no interactive render mode is registered
  anywhere (`Program.cs:12` is a bare `AddRazorComponents()`, `:121` a bare
  `MapRazorComponents<App>()`, and `Routes.razor` sets no `@rendermode`), so a Static SSR page always
  has an `HttpContext`. All correct as they stand — recorded so a later "tidy the unreachable
  branches" pass has to argue with it rather than silently remove it, which is D2's own reasoning one
  level up.
- **Null-`HttpContext` handling across the six pages is two behaviours, not three** — *throw* on five
  pages, *tolerate* on `RedeemInvitation` alone — with two spellings of the throw (a `Context` property
  where there is more than one call site, an inline `var context = … ?? throw` where there is exactly
  one). Both spellings pre-date this change. `Bootstrap` and `BootstrapComplete` diverging is that
  convention being followed, not drift.

### Not in this change

Cancellation in the git Smart HTTP remote and the content write path belong to
`git-backed-content-core`, which should adopt D1 rather than invent its own split. Timeouts, request
deadlines, and any server-side cancellation not originating from the client disconnecting are out of
scope entirely.
