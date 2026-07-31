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

## NEXT

- §1 in flight (block 1.1–1.2, briefed).
- §2's worker must add `[CascadingParameter] HttpContext` to `Bootstrap.razor` and
  `BootstrapComplete.razor` before 2.1–2.2 can pass anything — see the finding above.
- Open for §2: whether to split it into 2.1–2.2 (the two bootstrap pages, which need the cascading
  parameter added) and 2.3–2.6 (the four pages that already hold it), or run it as one block.
  Architect to put this to the Product Owner when §2 opens.
- §4.4 asserts the bootstrap gate cannot fail open under cancellation; it depends on §2's bootstrap
  plumbing landing first.
