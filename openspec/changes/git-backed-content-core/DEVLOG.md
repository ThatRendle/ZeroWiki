# DEVLOG — `git-backed-content-core`

The shared working channel for this change. Organised by `## N.` section mirroring `tasks.md`, with
`## NEXT` pinned at the bottom. Append-only; only `## NEXT` is rewritten.

## 0. Pre-flight & design amendments

**[architect]** Change opened 2026-08-02. Branch `change/git-backed-content-core` cut from `main` at
`f2cae81`. Pre-flight clean: working tree clean, `openspec validate --strict` valid, `dotnet build`
0 warnings / 0 errors, `dotnet test` **385/385**.

**[architect]** Inherited state worth knowing before reading anything else: **§1.1 is already
delivered.** `src/ZeroWiki` was scaffolded by the archived `invite-only-authentication` change and is
already an ASP.NET Core 10 Blazor Web App whose default render mode is Static SSR — `Program.cs` calls
plain `AddRazorComponents()` / `MapRazorComponents<App>()`, there is no
`AddInteractiveServerComponents()`, and no `@rendermode` appears anywhere under `Components/`. That is
exactly D7's shape, achieved by omission. The Product Owner's ruling (2026-08-02): tick 1.1 and handle
the "Interactive Server enabled per-island" half **as and when it comes up** — the first island that
needs a circuit is §8.2's "changed on disk" broadcast, so the interactive render-mode wiring lands
next to its first consumer rather than sitting unused for seven sections.

**[architect]** The Product Owner elected to **front-load the three design gaps** rather than hit them
mid-section. All three are now closed and written into `design.md` as **D8–D11**; the Open Questions
section carries no live question. Summary, with the reasoning that is *not* in `design.md`:

- **D8 — one volume at `/data`.** The Product Owner's deployment shape: a single Docker volume, with
  `/data/identity.db` beside `/data/wiki`. `/data/wiki` is the repo root, so `.git` is at
  `/data/wiki/.git` and pages at `/data/wiki/docs/*.md` — "everything below `wiki` is git-backed", and
  the spec's mandated `docs/` working tree sits inside it. Both paths are configuration defaulting to
  `/data`.
- **D9 — always commit-as-recovered; no policy knob.** The argument that decided it: a dirty tree can
  hold **untracked** files, and copying a folder of Markdown onto the volume is the *normal* way to
  populate a new ZeroWiki, not an edge case. Discard is unrecoverable; an unwanted recovery commit is
  a `git revert`. The Product Owner also struck the "configured policy" wording from 2.4 — a switch
  whose only other setting deletes user content is a footgun. Recovery author:
  `System <system@zerowiki.org>`, deliberately the software's fixed identity, not the deployment's
  domain.
- **D10 — synthetic commit authorship.** The `invite-only-authentication` supervisor parked "which git
  email do we stamp, and what for an account with none?" for this change. The answer is *neither*:
  `GitEmails` is the **inbound** mapping table, many-to-one, with no primary flag and **no
  `CreatedAt`** — so "the first one they added" is not recoverable and alphabetical is the only order
  the schema can produce, which would silently rewrite a member's author line the day they add an
  address sorting earlier. Browser saves are therefore authored
  `<Username> <username@<configured host domain>>`. The Product Owner's rationale for synthetic
  addresses is worth preserving verbatim: *"those will not be functional emails, at least not at
  first, but some of the contributors might not want to supply an actual email address."* Confirmed
  safe by two checks: `Username` is written only at bootstrap and redemption and **has no rename
  path**, so the identity is stable for the life of the account; and the domain is **configured**, not
  read from the `Host` header, which is per-request and client-supplied.
  - **Binding on §8.3:** the resolver matches the **synthetic form first**, ahead of registered
    `GitEmails` rows. `/account` lets a member register any address, so without precedence someone
    could register another member's synthetic address and quietly collect their attribution. Resolving
    synthetic-first closes that with no change to the shipped `GitEmailService`.
- **D11 — username form.** D10 makes the username an email localpart, and the shipped
  `UsernamePattern` permits a leading or trailing dot (`.emmz`, `emmz.`), which is not a legal RFC 5322
  dot-atom. Fixed at source rather than normalised at the point of use — the Product Owner's call.
  New pattern `^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?\z` plus `MinimumUsernameLength = 3`.

**[architect]** ⚠️ **Instrument failure worth recording, because it is the exact hazard CLAUDE.md
warns about and it nearly shipped a wrong answer to the Product Owner.** Evaluating a candidate
username pattern, the first Perl harness wrote the patterns inside `qr/…/`, where Perl **interpolated
`$\`** — the output record separator — out of the trailing `$\z`. The pattern under test silently
became one ending in a **literal `z` with no end anchor**, so `emmz.` and `emmz-` "passed" and `abc`,
`a.b` and a 64-character username "failed". Every one of those results is wrong, and the table was
internally consistent enough to look like a finding. Caught by the results being *implausible*, not by
the harness. The re-run uses non-interpolating `qr'…'` **and carries an explicit instrument check** —
`"abcz"` and `"abcq"` must behave identically under the pattern, which fails loudly if interpolation
is still happening. **Any regex harness in this change must carry an equivalent self-check.**

**[architect]** Corrected measurement, on which D11 rests — three variants over the same inputs:

| input | shipped | PO's first draft | D11 |
|---|---|---|---|
| `a`, `ab` | ok | REJECT | ok (regex) — refused by the length floor instead |
| `a.b` | ok | **REJECT** | ok |
| `.emmz`, `emmz.`, `-emmz`, `emmz-` | ok | REJECT | REJECT |
| `a`×64 | ok | ok | ok |
| `a`×65 | ok | **ok** | REJECT |
| `emmz\n` | REJECT | REJECT | REJECT |

The Product Owner's first draft
(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}[A-Za-z0-9][A-Za-z0-9._-]{0,63}[A-Za-z0-9]$\z`) achieved the goal but
carried two unintended effects: its **three** mandatory alphanumeric groups impose a minimum of three
*alphanumerics* — refusing `a.b` — and its two `{0,63}` runs put the ceiling at 129, above the real
`MaxLength(64)`. The middle group is a leftover from "at least one alphanumeric", which the new first
character already guarantees. `$\z` is harmless (`$` can only match where `\z` does) but the existing
comment argues specifically for `\z` alone. D11 keeps the regex to **shape only** and gives the floor
to a length rule, so one fault produces one message.

**[architect]** Process note from the Product Owner (2026-08-02), recorded because it governs how the
rest of this change is run: *"i think we're overthinking a lot of the process stuff for what is really
a pretty casual project right now."* Applied as: fewer clarification rounds, decisions taken and stated
rather than tabled as menus, and the gap-3 attachments question closed by the Architect on the design's
own logic rather than spent as a round. The block/review/supervisor loop stays — it has caught real
defects — but the deliberation in front of it comes down.

**[architect]** Scope amendment: D11 touches `CredentialPolicy`, which belongs to the **archived**
`user-accounts` capability. The Product Owner's call was to **fold it into this change** rather than
spin a separate one. Carried as `specs/user-accounts/spec.md` (`ADDED` → *Username form*), declared in
`proposal.md` under Modified Capabilities, and tasked as a new **`## 11.`** section. Not retroactive:
the pattern runs only at bootstrap and redemption, never at login, so no existing account — including
the Product Owner's `emmz` — stops working.

## NEXT

**Resume point: §1, block 1 (1.1–1.3).** Nothing committed on this branch yet beyond the design
amendments. §11 runs as its own block immediately after §1 — small, isolated, and foundational to
D10's synthetic identity.

Design questions outstanding: **none.** `design.md`'s Open Questions section is fully resolved
(D8–D11 plus the two questions inherited as already-resolved).
