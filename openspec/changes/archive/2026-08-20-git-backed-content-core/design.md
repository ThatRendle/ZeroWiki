## Context

ZeroWiki is a zero-config Markdown wiki deployed as a Docker container over a mounted data volume. Content is authored from two independent surfaces — a browser editor and an Obsidian vault on a laptop — and both write the same Markdown files. Left uncoordinated, that is a classic dual-writer data-loss problem. The design makes the content folder a **git repository that is the single source of truth** for both content and authorship, so conflict handling, edit history, authorship, and laptop sync all fall out of one mechanism.

Platform is ASP.NET Core 10 using a Blazor Web App with Static SSR as the default render mode and Interactive Server islands where live behavior is needed (see D7). `git` is available in the container and the app shells out to it. Scale is small and known (a handful of collaborators, few concurrent editors), which makes a simple serialized write model acceptable.

## Goals / Non-Goals

**Goals:**

- The `docs/` working tree of a non-bare git repo is what the app reads and renders; nothing authoritative lives outside the repo.
- Every browser save is one authored commit; authorship is read back from git history.
- No lost updates: concurrent edits produce an honest conflict, never a silent overwrite.
- A laptop can sync via `obsidian-git` against a Smart HTTP remote using wiki credentials — no bespoke sync engine, no SSH.
- The working tree is always clean, so pushes are always acceptable, and survives crashes.

**Non-Goals:**

- Browser Markdown editor UX, Fountain rendering, and realtime collaborative editing (later changes). Commit-on-save deliberately lays the durable-checkpoint groundwork the realtime layer will sit on top of.
- Full invite-only signup and login UX (later change). This change assumes only enough identity to verify git-remote credentials and stamp commit authors.
- Multi-vault / multi-tenant hosting and high-concurrency throughput.

## Decisions

### D1 — Non-bare repo with `updateInstead`, not a bare hub + checkout

The repo is non-bare; `docs/` is the checked-out working tree the app renders. Pushes target the checked-out branch via `receive.denyCurrentBranch = updateInstead`, which updates the working tree automatically when it is clean.

*Why:* collapses "sync hub" and "render tree" into one directory, and `updateInstead`'s clean-tree precondition is exactly satisfied by commit-on-save. *Alternative considered:* bare repo + `post-receive` `checkout -f` into a separate tree — cleaner separation and better for multi-vault, but adds a second directory to keep in sync for no benefit at this scale.

### D2 — Smart HTTP via `git http-backend`, behind wiki auth

Expose `info/refs`, `git-upload-pack`, `git-receive-pack` by mapping those routes to the `git http-backend` CGI as a subprocess (streaming stdin/stdout, setting `GIT_PROJECT_ROOT`, `PATH_INFO`, `REMOTE_USER`). The routes sit behind the app's authentication (HTTP Basic over TLS, or a per-user token).

*Why:* reuses battle-tested git plumbing instead of reimplementing the pack protocol; reuses one auth mechanism, one port, one reverse proxy. `obsidian-git` runs on `isomorphic-git`, which speaks Smart HTTP + Basic auth and does **not** do SSH well — so HTTP is what the client ecosystem wants. *Alternatives:* SSH + `git-shell` (separate user/key management); a pure-managed git server (immature, reinvents the protocol).

### D3 — Single cross-process write lock (`flock`)

All repository writes are serialized through one `flock` on a lockfile. It is taken by the app's own commit path directly, and — for pushes — by the app itself wrapping the whole `git http-backend` invocation (§7.5), not by the `pre-receive`/`post-receive` hooks that invocation runs as children of.

*Why filesystem-level, not in-process:* both acquisition points — the app's own commit path (§5.2) and its wrapper around `git http-backend` for pushes (§7.5) — run inside the same .NET process, so it is fair to ask why an in-process primitive (a C# `lock`, `SemaphoreSlim`) would not suffice. It would not, because the racer this lock exists to exclude is not two code paths inside one process — it is a **second, wholly separate OS process**: `## NEXT` obligation 16 names the concrete case, two ZeroWiki instances running concurrently against the same mounted volume during a rolling deploy's brief overlap. Each instance has its own CLR and its own in-process lock state, sharing nothing with the other's; an in-process lock can only ever exclude within the process that holds it, so it is structurally incapable of seeing, let alone blocking, a second instance's write. The shared primitive therefore has to live outside any one process's memory — on the filesystem, where any process that opens it (this instance, a concurrently-running sibling, or an operator's own `flock` invocation) is subject to the same exclusion. Without it, a push and a save — or two instances' own writes — can interleave and leave the tree dirty from two sources. At this scale, serialized writes are not a bottleneck.

*Why the hooks cannot be the lock point (§5 finding, Product Owner decision):* `receive.denyCurrentBranch=updateInstead`'s receive sequence is objects written → `pre-receive` runs and **exits** → refs update and the working tree is updated by git itself → `post-receive` runs and exits. A `flock` acquired and released inside `pre-receive` is gone before git touches the working tree — no hook process is alive during the write the lock exists to cover, so a hook-held lock cannot deliver this decision's guarantee regardless of which hook holds it. Locking around the whole `http-backend` invocation instead covers git's own ref/working-tree update because that update happens strictly inside the wrapped subprocess's lifetime. See D16 for the consequence this has for the hooks themselves: they must not attempt to take this lock at all, on pain of deadlock.

### D4 — Optimistic concurrency via base-revision CAS

Each save carries the revision it started from; the server rejects (409) if the file has advanced. This one check covers browser-vs-browser and browser-vs-incoming-push collisions identically. The `post-receive` broadcast lets an open editor learn its base went stale *mid-edit* rather than only at save time.

*Why:* prevents lost updates without pessimistic page locks; conflicts become visible, recoverable events. *Alternative:* lock a page while it is open in an editor — worse UX, and doesn't help against Obsidian pushes.

### D5 — Authorship derived from git; no hand-maintained author field

"Who edited this" is answered by `git log`/`blame`. Browser commits are stamped with the logged-in user (author enforced server-side); incoming push commits carry a self-asserted git identity mapped back to an account by email. No `authors:` frontmatter is maintained.

*Why:* a hand-maintained field always drifts from reality; git is already the truth. Self-asserted identity on the Obsidian side is acceptable for an invite-only trusted cast. *Alternative considered and rejected:* a separate creative-credit field — deferred; not needed for this change.

### D6 — Derived index, rebuildable from the repo

Tags, titles, and last-edit metadata live in a lightweight index built from the repo, rebuildable from scratch. `post-receive` and each browser commit trigger incremental re-index of changed files.

*Why:* preserves "drop the folder and go" — nothing authoritative outside git.

### D7 — Blazor Web App / Static SSR with interactive islands, not global Blazor Server

The app is a Blazor Web App whose **default render mode is Static SSR** (components render server-side per request, no SignalR circuit). Only the components that need live behavior opt into **`InteractiveServer`** — initially the "changed on disk" indicator, later presence/realtime. Read, browse, and login/invite surfaces stay Static SSR. The Markdown editor is a CodeMirror JS island regardless of render mode.

*Why:* ZeroWiki is read-mostly and self-hosted; global Blazor Server would make every reader hold a SignalR circuit for interactivity they aren't using, which is at odds with a lightweight wiki (and adds reconnection-overlay UX and server-memory UI state). Static SSR gives the same C# component model with no circuit, and the render mode can be flipped on per-component, so realtime slots in later without a rewrite. *Alternatives considered:* full **Blazor Server** — rejected (circuit-per-reader cost for no benefit here); plain **Razor Pages/MVC + hand-rolled JS** — legitimately leaner on read pages, but gives up the component model and makes the future realtime path hand-wired SignalR + JS instead of a render-mode flip.

### D8 — One mounted volume at `/data`: `identity.db` beside a `wiki/` repository

Production is a single Docker volume mounted at `/data`. The identity store is `/data/identity.db`; the content repository is `/data/wiki`, a non-bare git repo whose `docs/` directory is the working tree the app renders — so `.git` lives at `/data/wiki/.git` and pages at `/data/wiki/docs/*.md`; and the DataProtection key ring backing the authentication cookie is `/data/keys`. Everything below `wiki/` is git-backed; `identity.db` and `keys/` deliberately are not. All paths are configuration, defaulting to `/data`, so development points at a local gitignored folder without a second code path.

**`keys/` is a sibling of the repository and must stay one.** Inside `/data/wiki` the key ring would be picked up by commit-on-save and pushed to every Obsidian vault that clones the remote — the signing keys for every session, distributed to every laptop. The path is derived so that no configuration value can nest one inside the other.

*Why:* one mount is the entire deployment story — `docker run -v zerowiki:/data` and nothing else, which is what "zero-config" has to mean for an operator. Keeping SQLite outside the repository keeps D6 honest for *content* without committing a binary database into page history, where it would bloat every clone and conflict on every push.

### D9 — A dirty tree at startup is always committed, never discarded, with no policy switch

*(Resolves the "commit-as-recovered vs discard" Open Question.)* Startup reconciliation always commits an unclean working tree as a recovery commit. There is no configured policy.

*Why:* a dirty tree can contain **untracked** files — content git has never seen. The obvious way to populate a new ZeroWiki is to copy a folder of Markdown onto the volume, which "point it at a folder and it Just Works" actively invites, so this is the normal case rather than an edge one. Discarding destroys that irrecoverably; an unwanted recovery commit is a `git revert`. One failure mode is permanent and the other is not, which decides it. A switch whose only alternative setting deletes user content on startup is a footgun, and zero-config argues against a knob at all.

Recovery commits are authored **`System <system@zerowiki.org>`** — a fixed identity belonging to the software rather than to the deployment, so it reads identically in every install and can never be mistaken for a member. This is the one author line that does *not* use D10's deployment domain.

**Addendum — a nested git repository is refused, not gitlinked.** D9's asymmetry ("discarding destroys content irrecoverably; an unwanted recovery commit is a `git revert`") assumes reconciliation can actually capture what it commits. It cannot for a **nested** git repository: `git add -A` does not stage a directory containing its own `.git` (directory or gitfile, at any depth) — it stages a **gitlink** (mode `160000`), a bare reference to a commit in an object database this repository never touches or preserves. The commit "succeeds," `git status --porcelain` reports clean, and the working-tree-clean invariant assertion passes, while the actual file contents are recorded nowhere ZeroWiki controls; deleting the nested `.git` later makes them permanently unrecoverable, with every check having reported success. This is not an edge case for this product's own audience: the likely way to populate a new ZeroWiki is copying in an existing Obsidian vault, which (per `obsidian-git`) is usually itself git-backed. **Product Owner decision:** refuse to start when reconciliation would stage a gitlink, naming the offending path — consistent with the section's existing precedent of failing loudly on a bare repository at the root (2.1–2.2) rather than misbehaving quietly, and it never touches what the operator copied in. Out of scope: a gitlink already committed into history by an earlier run — no shipped build has created one, and retroactively detecting one would only brick a wiki that already has one.

The refusal fires only on a gitlink reconciliation would **introduce** — an `A` record, or an `M` record whose *old* mode was not already `160000` — not on one an adopted repository already has advancing its own nested `HEAD` in the ordinary way a submodule (or any pre-existing gitlink) does. An earlier implementation matched on new mode alone, which also fires on that ordinary advance; a legitimate adopted submodule would then start once and refuse forever the moment its pointer moved, which is the exact bricking this addendum rules out, arriving through a different door. Corrected in §2's remediation.

**Addendum — a born `HEAD` with no `docs/` is refused, not silently created into or reconciled over.** `EnsureInitialCommitAsync`'s early return on a born `HEAD` was written for "a previous start of this app already created it," and did not consider "history exists but there is no `docs/`" at all — whether that history belongs to a repository this app never initialized, or to one it did, whose `docs/` was later deleted by hand (`rm -rf docs`) between starts. Left unhandled, bootstrap completes with every check green — classify, config, hooks, no initial commit, nothing to reconcile, clean porcelain — and no working tree to serve from at all, silently failing the "serve from its `docs/` working tree" half of the *Repository present on startup* scenario. **Product Owner decision:** refuse to start in both cases, naming the missing `docs/` path, rather than creating it or committing into the repository. For an adopted repository, creating `docs/` would be establishing a working tree inside history ZeroWiki did not write, and committing would be writing to a repository ZeroWiki did not create. For a ZeroWiki-created repository whose `docs/` was deleted, the alternative is worse than it first looks: falling through to D9's reconciliation would `git add -A` the deletion of every page and commit it as a "recovered" change, permanently ratifying an operator's mistake into history instead of stopping at the door — a wholly missing working tree is a different and more severe class of problem than "some tracked files are dirty," so refusing here is not in tension with D9's always-commit posture, it is what that posture requires once the thing gone missing is the tree itself. This gives §2 **one consistent posture across all three foreign-repository faults** — a bare repository at the root, a gitlink reconciliation would introduce, and history with no `docs/`, regardless of who created that history.

**Addendum — every refusal precedes every write (ordering correction, Product Owner decision).** The two paragraphs above describe *when* ZeroWiki refuses; they do not by themselves describe *when it writes*, and for one release the code did not match the posture this design intended. `EnsureRepositoryAsync` applied `receive.denyCurrentBranch`/`http.receivepack` and (re)installed both Smart HTTP hooks — unconditionally overwriting whatever hooks an adopted repository already had — *before* running the missing-`docs/` and gitlink checks. An adopted repository with its own `pre-receive`/`post-receive` hooks and no `docs/` therefore had those hooks destroyed and was then refused anyway: damage to something ZeroWiki did not create, in service of a start it was never going to complete. Moving only the `docs/` probe earlier cannot fix this on its own — the gitlink probe is intrinsically a staging-and-diff operation (`git add -A` is how a gitlink is detected at all), so it cannot run any earlier than that. **Product Owner decision:** move configuration and hook installation to the *end* of `EnsureRepositoryAsync`, after the working-tree-clean assertion, rather than narrow the guarantee to fit the existing order. Nothing between classification and that assertion needs either: `receive.denyCurrentBranch`/`http.receivepack` govern pushes, and the hooks fire on push, and neither can occur before the app has finished starting. This ordering is now **load-bearing, not incidental** — a future change that moves configuration or hook installation earlier reopens exactly this defect, and should not do so without re-deriving why the current order is safe. With this fix, ZeroWiki genuinely never writes into a repository whose working tree it did not find intact: every refusal this method can raise runs before any write.

**What the posture is not, regardless of ordering.** "Never writes into a repository it did not find intact" is not the same claim as "never commits into a repository's history it did not create" — the latter is false by design. Once an adopted repository *has* been accepted (non-bare, resolvable, with a `docs/` working tree), D9's own reconciliation deliberately commits a `System`-authored recovery commit into that repository's history whenever its tree is dirty at startup — that is D9's entire point, not an exception to it. The accurate statement of ZeroWiki's posture is: **it does not write to a repository it did not create until it has accepted that repository, and thereafter D9 governs what it writes.** Acceptance is what the three refusals above gate; it is not a promise that history stays untouched forever after.

**Addendum — a pre-init filesystem scan closes the same gap on the *initialise* path (Product Owner decision).** The previous addendum's "every refusal precedes every write" holds for the *adopt* path — an existing repository ZeroWiki did not create. It did not yet hold for the *initialise* path — a plain folder copied onto the volume, with no `.git` of its own — where the gitlink check is intrinsically a staging-and-diff operation and so cannot run before `git init` creates a `.git` and an initial commit already exists. A nested repository copied in before that first start (an existing Obsidian vault, `.git` and all) would therefore be discovered only after `git init` and the initial commit had already run: `.git` created, one commit made, immediately followed by refusing to serve. **Product Owner decision:** walk `RepositoryRoot`'s directory tree in C# for a nested `.git` entry (directory or gitfile, at any depth) *before* `git init` ever runs on the initialise branch, and refuse if one is found, naming every offending path. This runs once in the life of a volume — the branch it guards stops existing the moment a repository is there — never on every startup and never on a populated vault. The scan does not follow symbolic links: a symlinked directory can loop back on itself (hanging the walk) or point outside the volume to something that is not nested in this folder at all, and either would defeat the point of the check. With this addition, every refusal on **both** paths — adopt and initialise — precedes every write.

This is a second instrument, not a duplicate of the index-based gitlink check, and the code says so at both sites. The scan answers *"is there a nested repository in this folder"*, walking the filesystem directly, and guards `EnsureRepositoryAsync` whenever it is about to create the initial commit, before any write. The index check answers *"would committing right now store a gitlink"*, from git's index, and guards every reconciliation once a commit already exists, including content that arrives long after bootstrap. Neither subsumes the other, and they deliberately disagree in one case: a nested repository the operator has `.gitignore`d is invisible to the index check (`git add -A` respects `.gitignore`, so no gitlink is ever staged) but is still found by the filesystem scan, which does not consult `.gitignore` at all. The Product Owner accepted this divergence knowingly — refusing is recoverable and legible, and a gitignored nested repository inside a wiki's content folder is a configuration worth stopping on rather than silently accepting.

**Addendum — the scan is gated on "the repository has no commits yet", not on "`.git` is absent" (Product Owner decision, correcting the previous addendum's framing — the third attempt at making this sentence true).** The previous paragraph named two paths, adopt and initialise, and stopped there. But "`.git` is absent" and "ZeroWiki is about to write the initial commit" are two different conditions that merely coincide on a fresh volume; they diverge the moment `.git` exists with `HEAD` still unborn. A process that dies between `git init` and the initial commit that should have followed it leaves exactly that shape — `.git` present, no commit — and `git init` had already disabled the scan's only trigger for every later restart of that volume: the adopt branch is taken, the scan is never called, and `EnsureInitialCommitAsync` writes `docs/.gitkeep` and commits it before the index-based gitlink check ever gets a chance to fire. Enumerating "adopt and initialise" as the two covered paths is exactly what missed this third state; naming the rule instead — rather than the paths it happens to touch — is what makes the claim survive a path nobody listed. **Product Owner decision:** the scan runs whenever ZeroWiki is about to create the initial commit, which is true in exactly two circumstances, not two independently-derived conditions that happen to agree most of the time: `.git` does not exist at `RepositoryRoot` yet, or it does but `HEAD` is unborn. `EnsureRepositoryAsync` computes this once and uses the same value both to decide whether to run the scan and to decide whether `EnsureInitialCommitAsync` is about to write, so the two cannot drift apart again the way "no `.git` entry" and "`HEAD` unborn" did. A restructuring into separate acceptance/configuration phases was suggested as the more durable shape for this method; the Product Owner chose this targeted fix instead and the restructuring is carried to `## NEXT`.

**Addendum — an unreadable subdirectory refuses in its own right, distinct from the nested-repository refusal (Product Owner decision).** The scan above exists to *assert* "no nested repository anywhere in this tree" before any write; on a subdirectory this process cannot read, that property is **unverifiable**, not merely unverified, and treating "couldn't check" the same as "checked, clean" is exactly the silent-degradation pattern this section rejects everywhere else. Reproduced: a real nested git repository placed behind a `chmod 000` directory was invisible to the scan, and bootstrap proceeded regardless — `git init` ran and the initial commit was made with the hidden repository never seen. **Product Owner decision:** an unreadable directory refuses on its own, naming the path, with a message distinct from the nested-repository one — fixing permissions and removing a nested repository are different operator actions, and the refusal must say which one applies rather than leaving the operator to guess. The cost argument is the same one that justifies the scan itself: it runs once per volume, on content an operator has just copied in and is most likely still watching, so a refusal here is cheap and recoverable rather than a burden imposed on every startup. No realistic false-refusal case was found for it: the walk starts at `RepositoryRoot`, not `DataRoot`, which rules out the volume-root artifacts that would otherwise be the obvious worry — macOS Spotlight/`fseventsd` metadata directories, Linux `lost+found` — since none of them live inside `RepositoryRoot` itself. **What this does not reach:** `git add -A` has the same blind spot one layer further in — on an unreadable directory it warns to stderr but exits `0` and proceeds — and no filesystem scan in C# can close that, since it happens entirely inside git's own process. That gap is owed to §6 at the latest (`## NEXT`), not solved here; nothing above should be read as a guarantee that reconciliation itself is immune to the same class of problem.

### D10 — Commit authorship is a synthetic per-account identity, not a registered git email

Browser saves are authored `<Username> <username@<configured host domain>>`. The address is synthetic and need not be deliverable; a member is never required to disclose a real email address to edit the wiki. The domain is **configured for the deployment**, never read from the request's `Host` header — the header varies per request and is client-supplied, so deriving from it would both split one person's history between environments and let a spoofed `Host` write an attacker-chosen author line into history this design commits to never rewriting.

*Why:* the `GitEmails` table exists for the **inbound** direction — resolving a pushed commit's author back to an account. It is many-to-one by design, carries no primary flag, and has no `CreatedAt`, so "which address should we stamp?" has no answer the schema can give: alphabetical is the only available order, and it would silently rewrite a member's author line the day they add an address sorting earlier. A synthetic identity is deterministic, always exists (zero git emails is a legal account state), and is stable for the life of the account because no username-rename path exists. *Alternative considered:* a repo `.mailmap` maintained by the app, collapsing each member's registered addresses onto the canonical identity — genuinely tidier for `git shortlog`, but it makes the identity store a second writer into the repository, which is a coupling this change should not buy yet.

**Consequence binding §8.3:** the git-identity resolver MUST match the synthetic form **first**, ahead of the registered `GitEmails` rows. `/account` lets a member register any address, including another member's synthetic one; resolving synthetic-first means a squatted row can never capture someone else's attribution, and needs no change to the shipped `GitEmailService`.

**Consequence binding §6:** the author line §6 constructs MUST be a well-formed address **for every account**, including one whose username predates D11. D11 governs only where a username is *chosen*, and it is deliberately not retroactive — `LoginServiceTests` pins `ab`, `_legacy_` and `.old.name.` still authenticating — so accounts exist that were never subject to it and never will be. No rule applied at choice time can therefore make this precondition universal, which is precisely why §6 owes it and §11 cannot discharge it: §6 is where the address is actually constructed, and it is the only place a guarantee covering *every* account can live. §6 MUST NOT assume the username is already a legal localpart.

### D11 — Username form: first and last character alphanumeric, no consecutive dots, minimum three characters

`CredentialPolicy.UsernamePattern` becomes `^[A-Za-z0-9](([A-Za-z0-9_-]|\.[A-Za-z0-9_-]){0,125}([A-Za-z0-9]|\.[A-Za-z0-9]))?\z`, and a new `MinimumUsernameLength = 3` carries the length floor alongside the existing `MaximumUsernameLength = 64`. Every dot in the pattern is written as part of a unit that also consumes the character after it, and that character is never a dot — which is what forbids a doubled dot without a lookahead.

*Why:* D10 makes the username an email localpart, so the shape has to be fixed where usernames are chosen, not patched at the point of use. RFC 5322 `dot-atom-text` is `1*atext *("." 1*atext)`, and `.` is not `atext`: a dot may only ever appear *between* runs of `atext`. A **doubled** dot is therefore exactly as illegal as a leading or trailing one — `a..b` is no more a dot-atom than `.ab` is. D11 originally fixed the ends and said nothing about the middle, so `a..b` was accepted for the whole of §11; the consecutive-dot rule closes that.

The regex governs **shape only** and its bound is deliberately looser than the length cap, so an over-long username reports a length error rather than also being told its charset is wrong; the floor is a length rule for the same reason, mirroring how `MinimumPasswordLength` already works. Every quantifier bound stays finite and both ends stay anchored, so matching remains constant-time.

**The accepted set is a strict subset of the legal one, deliberately.** The property D10 needs is `accepted ⊆ legal` — every username we stamp yields a legal address — and *not* `legal ⊆ accepted`. Refusing something a dot-atom would allow is a legibility choice, not a defect: `_legacy_` and `-foo` are legal localparts and are refused, because a rule that has to be safe should be permitted to be narrower than the grammar it protects. Stating the direction is load-bearing — reading the claim as an equality is what made `a..b`'s acceptance look like the mirror image of `_legacy_`'s refusal, and let the gap survive three reviews.

This amends the **archived** `user-accounts` capability and is carried as a spec delta in this change. It is not retroactive: the pattern runs only at bootstrap and invitation redemption, never at login, so no existing account stops working.

### D12 — Pages live under `/wiki/`, with `_` for space and `__` for a literal underscore

A page's route is its working-tree path relative to `docs/`, with the `.md` extension dropped, under a `/wiki/` prefix: `docs/Project Notes/Kick Off.md` → `/wiki/Project_Notes/Kick_Off`. Directory separators stay separators. The prefix keeps content clear of the shipped root routes (`/login`, `/account`, `/invitations`, `/bootstrap`, `/bootstrap/complete`, `/logout`, `/invite/{Token}`, `/not-found`, `/Error`, `/`), so adding a page can never shadow an application surface.

The encoding is two layers, applied in this order, and the order is what makes them independent:

1. **Space → `_`, literal `_` → `__`.**
2. **Percent-encode what remains URL-significant** — `%`, `#`, `?` and control characters. This layer is not optional: a file named `a%20b.md` would otherwise emit a route that decodes straight into a collision with `a b.md`. Non-ASCII is left bare; accented filenames are ordinary in an Obsidian vault and survive the round trip.

Layer 2 cannot disturb layer 1 because `_` is RFC 3986 *unreserved*, so no encoder escapes it. Decoding runs in reverse: the framework percent-decodes the path, then `__` → `_` and `_` → space, scanned left to right.

*Why not `%20`:* readability of the common case. Spaces are what an Obsidian vault is actually full of, and `%20` in every wiki URL is the ugly default this deliberately avoids.

*Why not `%5F` for a literal underscore*, which would be unambiguous and keep the common case `%`-free: RFC 3986 §6.2.2.2 tells normalizers to decode percent-encoded **unreserved** octets, so a reverse proxy is entitled to rewrite `a_%5Fb` → `a__b` and hand the ambiguity back from outside the application's control. `__` is two ordinary unreserved characters and survives any normalizer. **A scheme that depends on a proxy declining to normalize is not a scheme.**

**The residual ambiguity is real, it is wider than it first looks, and it refuses rather than resolves.** The escape character is also the substitute character, so the rule is cleanest stated on the **route** side: **a route containing two or more consecutive `_` is ambiguous.** A run of *n* underscores has Fibonacci(*n*+1) preimages (the compositions of *n* into parts of 1 and 2), and a greedy left-to-right decode always yields the one that takes underscores first.

*Stating it filename-side is what kept getting it wrong.* Two earlier revisions of this sentence named a filename shape — first `a_ b.md` vs `a _b.md`, then "any run of two or more adjacent `{space, '_'}` characters" — and both were narrower than the mechanism. The second still excluded a **single literal `_`**, which encodes to `__` and therefore has two preimages by the very formula in the sentence before it, as the table below shows. The route side is where the collision actually exists, so that is where the rule belongs; a filename is ambiguous exactly when its encoding produces two consecutive underscores.

| filename | route | greedy decode | |
|---|---|---|---|
| `a b` | `a_b` | `a b` | round-trips |
| `a_b` | `a__b` | `a_b` | round-trips |
| `a  b` *(two spaces)* | `a__b` | `a_b` | **lost — collides with `a_b`** |
| `a_ b` | `a___b` | `a_ b` | round-trips |
| `a _b` | `a___b` | `a_ b` | **lost** |
| `a   b` | `a___b` | `a_ b` | **lost** |

So the realistic collision is **a double space against a single underscore** — `Chapter  1.md` against `Chapter_1.md` — a typo meeting an ordinary filename, not the pathological case this decision was originally framed around. *An earlier revision of this decision named only `a_ b.md` vs `a _b.md` and characterised the class as absurd; that estimate was wrong, and the Product Owner re-affirmed the scheme knowing the true frequency rather than inheriting the mistaken one.*

**The failure does not require two files.** A tree containing only `Chapter  1.md` produces exactly one claimant for `Chapter__1`, so detecting collisions by grouping files per route sees nothing wrong — yet that route decodes to `Chapter_1.md`. Since §6 inverts route→path to choose the file it *writes*, the save lands in a phantom `Chapter_1.md` while the member believes they edited `Chapter  1.md`; the two files then genuinely collide and both become unreachable. A silent wrong-file write is the failure class §2 refused four separate times, and grouping alone does not prevent it.

**Product Owner decision:** enumeration — which already walks the whole tree, so this is free — refuses a route unless **exactly one file claims it *and* decoding that route reproduces that file's own path**. Both conditions are one property: *the route identifies exactly this file*. A route failing either is reported with every path implicated and serves no body; every other route is unaffected, and the rest of the wiki is untouched. Grouping by claimant count is necessary but **not** sufficient, and an implementation that checks only the count satisfies the letter of this decision while leaving the defect it exists to prevent.

Refusing at the route rather than at startup is the point, and it is a deliberate departure from §2's posture rather than an inconsistency with it. §2's refusals guard the *volume's structure*, are seen once by an operator who is standing there, and are fixed before the app serves anything. A route collision is *content*, and content arrives at runtime by push from a laptop: a startup refusal would let any pushed filename brick the whole wiki for everyone until someone reached the server. The shared principle is that the system never silently picks one of two readings — where it refuses is set by what the fault can reach.

**Consequence binding §6:** a save can never target an ambiguous route, because an ambiguous route serves no page to edit. §6 inherits an inverse that is total on every route it can actually be reached from, and MUST NOT reintroduce a greedy "pick the first match" decode as a convenience.

### D13 — Raw HTML in a page is escaped, not rendered and not sanitized

The Markdown pipeline is **Markdig** with raw HTML disabled, so inline and block HTML in a page render as visible text rather than as markup. There is no HTML sanitizer.

*Why:* content reaches the working tree from two writers, and one of them is a `git push` authenticated by a per-user git token. A `<script>` or an `onerror=` attribute in a pushed page is stored XSS against every reader, including the administrator, and Blazor's `MarkupString` — the only way to emit rendered HTML from a component — sanitizes nothing whatsoever. Escaping needs no dependency; a sanitizer is a second dependency whose entire security value rests on it never having a bypass. *Accepted narrowing:* Obsidian users do sometimes embed raw HTML, and those pages will show their tags instead of their effect. That is legible and recoverable, which the alternative failure is not. Which further Markdig extensions to enable is deliberately left open and is not settled by this decision.

**Addendum — disabling raw HTML does not disable script execution, and an earlier revision of this decision claimed it did (Product Owner decision).** The sentence "escaping … has no bypass surface" was **false**. Markdig does not sanitize link and image *destinations*, so with raw HTML fully disabled the shipped pipeline still rendered `[x](javascript:alert(1))` as `<a href="javascript:alert(1)">`, `![x](javascript:…)` as an `<img src>`, and resolved the entity form `javascript&#58;` back to a working `javascript:` — live stored XSS reachable by any git-token holder, which is the exact threat this decision names and does not accept. The defect is instructive about *method*, not just about URLs: the worker, the reviewer and the Architect each audited D13 by asking "which values reach the browser un-encoded", all three answered correctly, and all three missed this because that question only ever inspects **tags**. The tests asserted on `<script>` alone. Three independent audits sharing one instrument corroborate each other while remaining equally blind — the same shape as the `href=""` anchor-regex blind spot recorded in §0.

**Product Owner decision: both a URI allow-list and a Content-Security-Policy header**, because they fail in opposite directions — an allow-list fails *closed* on a scheme nobody anticipated, while a CSP fails *open* if some future response omits the header, and neither covers the other's failure.

- **The allow-list is authoritative and fails closed.** A link or image destination is permitted only if it is relative (including a bare fragment) or carries one of an explicitly enumerated set of schemes — `http`, `https`, `mailto`. Anything else, *including any scheme not yet invented*, is refused rather than rendered as a live destination. It MUST be applied **after** Markdig has parsed and resolved entities, never against the raw Markdown source, because `javascript&#58;` is not `javascript:` until Markdig has decoded it. Normalize before matching — leading and trailing whitespace, embedded control characters, and case are all things a scheme can hide behind.
- **The CSP is defence in depth, and it is cheap here.** `script-src` without `'unsafe-inline'` blocks `javascript:` URI navigation outright, so the two mechanisms genuinely overlap on the primary threat. The application carries no inline `<script>` or `<style>` — `App.razor` references only external assets and Bootstrap is self-hosted — and D7 keeps ZeroWiki off WebAssembly, so no `wasm-unsafe-eval` is needed. The one obstacle is an inline `onclick` handler in `NavMenu.razor`, Blazor template scaffolding that should be removed on its own merits. Directives: `default-src 'self'`, `script-src 'self'`, `style-src 'self'`, `img-src 'self'`, `object-src 'none'`, `base-uri 'self'`, `frame-ancestors 'none'`, `form-action 'self'`.

*Known forward cost, recorded so it is not rediscovered as a surprise:* a future in-browser editor may inject styles at runtime. CodeMirror 6's `style-mod` writes `styleTag.textContent` when mounted into a document, which `style-src 'self'` blocks, but takes a constructable-stylesheet path — not an inline style, and so not governed by `style-src` — when mounted into a **shadow root**. The editor change owns that choice; it is a mounting decision, not a reason to weaken this policy or to pick a different editor.

### D14 — Frontmatter is parsed behind `IFrontmatterParser`, and failure is total

Markdig's YAML frontmatter extension only *delimits* the block — it hands over `yamlBlock.Lines.ToString()` and evaluates nothing — so the parser is a separate, freely substitutable choice. **SharpYaml** is that choice, reached only through an injectable `IFrontmatterParser` so it can be swapped without touching any calling code.

*Why:* the seam is a `string`, so no parser earns integration credit over any other, and the deciding factors are ordinary ones — SharpYaml is actively maintained (3.13.0, more recently released than Markdig itself) and shares an author and API idiom with Markdig, which the Product Owner already knows well. The interface exists because that reasoning is about familiarity rather than about a property only SharpYaml has.

**Failure must be total, never partial.** On any frontmatter that does not parse, the page renders its body with metadata **empty**. A parser that recovered and returned half a document would be worse than one that threw: partially-parsed metadata is indistinguishable from real metadata, so a page with broken frontmatter would silently acquire the wrong tags rather than none. `IFrontmatterParser` therefore returns empty on failure and never a partial result.

Two hardening requirements bind any implementation, because frontmatter is push-reachable:

- **Deserialize into a constrained shape** — a fixed POCO or `Dictionary<string, object>` — never through a type-resolving deserializer that honours `!!` tags. Both mainstream .NET YAML libraries can instantiate arbitrary types from a document, which turns page content into a deserialization gadget.
- **Bound the input before parsing it.** Cap the frontmatter block's size and nesting depth at the seam rather than trusting the library's defaults. Ordinary malformed YAML throws catchably and is handled; the two inputs that are *not* ordinary are an alias-expansion bomb (well-formed YAML that expands exponentially — strictness does not help) and nesting deep enough to overflow the stack. A `StackOverflowException` in .NET cannot be caught and kills the process, so it cannot be handled downstream at all: a single pushed page would crash the container on every read of it, permanently. The cap is what makes "failure is total" implementable rather than aspirational.

### D15 — The index lives in process memory, stamped with the commit it was built from

D6 said the index is "lightweight", "built from the repo" and "rebuildable from scratch", and left where
it lives and how it stays true unanswered. **Product Owner decision: an in-memory snapshot stamped with
the `HEAD` it was built from, and the page-serving path reads it.**

**Nothing is persisted.** D8 would have permitted a SQLite table beside `identity.db` — outside the
repository, so D6 stays honest — and it is rejected: it buys survival across restarts that a wiki this
size does not need, and buys a second artifact that can be stale, corrupt, or out of step with `HEAD`.
The hook could not write it in any case (`## NEXT` obligation 3: the runtime image has no HTTP client,
and no reason to gain a SQLite one), so a persisted index would *still* need the app to refresh it — the
freshness mechanism is required either way, at which point persistence adds a second source of truth for
nothing. "Rebuildable from the repository alone" then stops being a property to maintain and becomes the
only way the index can exist at all.

**The stamp is what makes the index correct without §6 and §8.** The snapshot records the commit it was
built from; serving a page verifies that stamp against the repository's current `HEAD` and refreshes when
they differ. This covers every writer identically — a browser save (§6), an `updateInstead` push (§8), and
an operator committing by hand on the volume — *including writers that never notify the app*, which is
the case D6's "`post-receive` … trigger[s] incremental re-index" quietly assumed away. §6 and §8 may later
push an update in as an optimisation; correctness never depends on their doing so, and an index that
depended on being told would be wrong for the third writer no matter how carefully the first two were
wired.

**This self-healing rests on an invariant it has never stated until now: every content-changing event
advances `HEAD`.** The stamp only detects staleness when `HEAD` has moved; a write that changes a file on
disk without ever completing a commit does not move it at all. `specs/content-editing/spec.md`'s *Failed
commit rolls back the write* scenario (§6) is exactly this: a browser save writes the file, the commit
fails, and `git checkout -- <file>` restores it under the write lock — `HEAD` never advances, whether the
rollback succeeds instantly or a refresh lands in the narrow window before it does. If a refresh reads that
file's frontmatter while the failed write is live, the index is left holding the aborted save's metadata,
and no future `HEAD` advance repairs it on its own: the next one takes the incremental branch, which only
re-reads the paths its diff names, and this path was never part of any commit for a diff to name. Inert
today — no writer in this change can fail a commit yet — and live the moment §6's save path exists. **§6
must satisfy this invariant directly as part of its own rollback** (e.g. discarding or re-reading the
affected path's index entry when `git checkout --` runs, not assuming the stamp mechanism already covers
it); it is not fixed here.

*The check is `git rev-parse HEAD` — one subprocess per page view.* Considered and rejected: reading
`.git/HEAD` and the ref file directly in C#, which spawns nothing — but a ref may be **packed**
(`git gc` moves loose refs into `.git/packed-refs`), so that fast path is two code paths, and silently
serves a stale wiki if the second is missed or subtly wrong. Serving stale content is the exact failure
the stamp exists to prevent, so it does not get a hand-rolled ref reader. What this replaces is strictly
more expensive: today every page view walks the **entire working tree** *and* spawns a `git log`
(obligation 21).

**A moved `HEAD` re-indexes what changed, not everything — usually.** `git diff --name-only
<stamped>..<current>` names the affected paths under `docs/`, and only those are re-read: a deletion
leaves the index, an addition or modification gets a fresh frontmatter/last-edit read, and each affected
route's *other* claimants are reconstructed from the previous snapshot rather than a re-walk, so an
emergent or resolved D12 collision is still caught. A full rebuild — the same whole-tree walk plus
whole-history walk startup pays, so not a cheap operation, but still unconditionally correct — is the
fallback in three cases: no previous stamp, a stamped commit git can no longer resolve (history rewritten,
a `reset --hard` behind the app's back), and a third this decision did not originally name — **the previous
snapshot has any `UnreadableDirectories`**, because a file under a directory this process could not read at
the last full build was never recorded anywhere in that snapshot, so the incremental path's claimant
reconstruction has no complete census to reconstruct from and cannot be trusted. The first two triggers are
one-off — the very next refresh returns to the incremental path once resolved. **The third is not**: it
fires on *every* `HEAD` advance for as long as any directory stays unreadable, so a full rebuild becomes the
request-path steady state for that entire duration, not an occasional fallback — an unfixed permissions
problem on the volume costs every page view a whole-tree-plus-whole-history rebuild until an operator
resolves it. This is what gives 4.3 a
real caller inside this change rather than an update method waiting for §6 to exist.

**Last-edit on a full rebuild is one bulk history walk, not one `git log` per page.** A single
`git log --name-status` pass takes the most recent commit touching each path. Per-page `git log` makes a
rebuild scale with the number of pages *times a process spawn*; one walk scales with history and spawns
once.

**Indexing reads a bounded prefix of each file, never the whole file.** Frontmatter is at the head of a
file by definition, so the indexer reads only enough *characters* — via a `StreamReader`, never a raw byte
slice, which could split a multi-byte UTF-8 codepoint — to contain a legal block (sized from D14's byte cap
directly: a character is never smaller than a byte, so reading that many characters always consumes at
least as many bytes as the cap allows, which is conservative rather than exact but never unsound) rather
than reading a page whole to extract two fields. This deliberately does **not** discharge obligation 21's
missing page-size cap on the *render* path, which still reads the file whole and still has no owner; §4
does not widen to fix that, and nothing here should be read as having done so.

**The index carries D12's refusals, not only its pages.** Ambiguous routes, sole claimants whose route
does not identify them, and unreadable directories ride in the same snapshot. Serving from the index must
not weaken what §3 established: an index holding only servable pages would silently demote a *refused*
route to an ordinary 404, losing the message naming every file implicated.

**The index is metadata only.** A page's body is still read from the working tree on the request that
renders it, so the index can never serve stale *content* — only stale metadata, which is what the stamp
governs.

### D16 — The write lock: primitive, location, and acquisition policy

D3 says one `flock`, taken by the app directly for its own commit path and by the app again — wrapping
the whole `git http-backend` invocation, not the `pre-receive`/`post-receive` hooks it runs — for
pushes. This settles the four things that decide whether that claim actually holds: which primitive,
where the lockfile lives, how each side acquires it, and what §5.1's lock does to the
snapshot-trusting read at `## NEXT` obligation 16.

**Primitive — a `flock(2)` advisory lock taken on a raw POSIX file descriptor via `P/Invoke`, not
`FileStream`/`FileShare` and not shelling out to `flock(1)` from the app.** Verified with a two-process
spike (scratchpad, not `src/`): a small console app P/Invoking `libc`'s `open()` and `flock()` directly,
run against the shell's own `flock(1)` inside `mcr.microsoft.com/dotnet/sdk:10.0` /
`mcr.microsoft.com/dotnet/aspnet:10.0` — the exact images this repository's `Dockerfile` builds and
ships, Ubuntu 24.04 with `flock` from `util-linux` present by default (an `Essential: yes` Debian/Ubuntu
package, needing no extra install). Development is macOS, which has **no `flock(1)` binary at all** — the
spike could not have been run meaningfully on the host and was run inside the container instead, which is
also the only environment whose result this decision can trust; nothing here rests on a macOS observation.

Both directions of mutual exclusion were demonstrated, not assumed:

- **.NET holds → shell blocks.** The spike opened a raw fd with `open(O_CREAT|O_RDWR)` and called
  `flock(fd, LOCK_EX)`, held it 3s, then released. Concurrently: `flock -n -x <path> -c '...'` (shell,
  non-blocking) exited `1` ("busy") while the lock was held, and a plain blocking `flock -x <path> -c
  '...'` returned only after ~2.5s (matching the remaining hold time from a 0.5s-delayed start) —
  `SHELL_GOT_LOCK_AFTER_WAIT`, `took 2511ms`.
- **Shell holds → .NET blocks.** `flock -x <path> -c 'sleep 3'` (shell) ran first; concurrently, the
  .NET side's non-blocking `flock(fd, LOCK_EX|LOCK_NB)` returned `EAGAIN` (`errno=11`) immediately, and
  its blocking `flock(fd, LOCK_EX)` returned only after the shell released — `acquired after 2447ms`.

Both other candidates were run and rejected on what was actually observed, not on their reputations:

- **`FileStream` with `FileShare.None`** *does* demonstrate mutual exclusion with `flock(1)` in both
  directions — but only because .NET's Unix I/O layer applies its own `flock()`-based emulation of
  Windows file-sharing semantics at *every* file open, independently of the `FileShare` value requested.
  This was caught by surprise: `File.OpenHandle(path, ..., FileShare.ReadWrite)` — requesting the least
  restrictive sharing, not `None` — still threw `IOException: …being used by another process` the moment
  the shell held an exclusive `flock(1)` on the same path. That is an internal, undocumented interaction
  between two lock layers the app does not control the second of, not a primitive chosen on its own
  terms; a `FileShare.None` blocking wait against `flock(1)` also only works as a manual poll-and-retry
  loop (`FileStream` open throws `IOException`, not a blocking wait), never a true kernel-level block.
  Rejected: the interoperability is real but incidental, riding on a code path (`SafeFileHandle`'s
  open-time locking) this design does not want to depend on for a security/integrity property.
- **A raw fd via `File.OpenHandle`, then P/Invoke `flock(2)` on it** — the natural middle ground — fails
  outright for the same reason: `File.OpenHandle` goes through the same open-time emulation as
  `FileStream`, so it throws before the app's own `flock()` call ever runs, whenever another process
  already holds the lock. The raw fd has to come from a **raw `open()`** P/Invoke, bypassing .NET's own
  file-open path entirely — which is what the chosen primitive does.

*Why this one:* real `flock(2)`, called directly, is byte-for-byte what `/bin/sh`'s `flock(1)` also
calls — mutual exclusion holds by construction, not by two independent implementations happening to
agree. It gives a true kernel-level blocking wait (no poll loop, immediate wake on release) and a true
non-blocking `LOCK_NB` trylock, and closing the fd (including on process exit/crash) releases the lock
automatically — no stale-lock cleanup path is needed on either side. The cost is `unsafe`/`P/Invoke` and
a few lines of interop rather than a `using var fs = new FileStream(...)`.

**What this claim's status actually is, now that §5.3 is struck.** When this was written, the shell's
`flock(1)` was the *only* other lock-holder in the design — the `pre-receive`/`post-receive` hooks — so
demonstrating `.NET flock(2) ↔ shell flock(1)` interop *was* the one claim the whole section rested on:
without it, nothing established that our primitive and the hooks' primitive excluded each other at all.
That is no longer the section's foundation. Both acquisition points are now the same C# `RepositoryWriteLock`
type — the app's own commit path (§5.2) and its wrapper around `git http-backend` (§7.5) — and the
property the section actually rests on is instance-vs-instance exclusion: two **separate OS processes**
each running our own `flock(2)` code excluding each other, which is precisely what a rolling deploy's
overlapping instances (`## NEXT` obligation 16) need and precisely what §5's "ours-vs-ours" tests prove
directly, by running a second real process against the production lock type. The `.NET-vs-flock(1)`
interop spike above still demonstrates something true and still worth having recorded — `flock(2)` is
the same kernel primitive regardless of which userspace code calls it, so this was never at risk of
being wrong — but it is no longer load-bearing for the guarantee this section makes. It is **insurance**,
not foundation: kept because an operator debugging a stuck lock will reach for `flock` on the command
line, and a future hook or maintenance script might too, so it is cheap confirmation that doing so
behaves as expected against our lockfile — not because anything in §5.1–§5.2/§7.5's own correctness
depends on it. The `flock(2)`-vs-`flock(1)` interop regression test (`RepositoryWriteLockTests.cs`,
Linux-only) is kept for that reason, reframed the same way in its own comments.

**A bounded wait has no syscall for it.** `flock(2)` is binary — block forever (`LOCK_EX`) or fail
instantly (`LOCK_EX|LOCK_NB`) — there is no third "block up to N seconds" mode, and a blocked `flock(2)`
call cannot be cancelled from .NET without leaking the native thread it runs on. §5.2's app-side lock
acquisition is therefore a **poll loop**: `LOCK_EX|LOCK_NB` on a fixed interval (starting at ~50ms,
matching what the spike's `FileStream` fallback already used) against a wall-clock deadline, driven by a
`CancellationToken` so it composes with the rest of the request pipeline rather than blocking a thread
pool thread for the whole wait. The push side has no such constraint: the app's own wrapper around the
whole `git http-backend` invocation (§7.5, not yet built) must instead **achieve** an unbounded wait,
taken directly by the app rather than by a hook (see D3's "why the hooks cannot be the lock point" and
the acquisition-policy note below for why no hook may attempt this acquisition itself). `RepositoryWriteLock`
exposes no blocking-`LOCK_EX` acquisition today, only `AcquireAsync`'s polling one — §7.5's cheapest
route is an effectively-infinite `timeout` passed to that same poll loop, which satisfies the
*guarantee* (the wait never gives up) without being a genuine kernel-level block; it would still be
polling, just with no practical ceiling. Whether §7.5 instead adds a true blocking `LOCK_EX` path to
`RepositoryWriteLock` is §7.5's decision, not one this design has made for it.

**Location — `ContentPaths.LockFilePath`, a sibling of `RepositoryRoot`, not a file under it.** The
brief's framing ("outside `ContentPaths.WorkingTree`") understates the constraint: within
`EnsureRepositoryAsync`'s call, `ReconcileWorkingTreeAsync` runs `git add -A` and
`AssertWorkingTreeIsCleanAsync` runs `git status --porcelain`, both with `repositoryRoot` (not `docs/`)
as the working directory and neither with a pathspec restricting it to `docs/` — the entire non-bare
repository, everything under
`RepositoryRoot` except `.git` itself, is git's tracked working tree and is what the clean-tree invariant
governs. A lockfile placed anywhere under `RepositoryRoot` — including directly inside it, alongside
`docs/` — would therefore be staged by reconciliation, committed by D9, and pushed to every Obsidian
vault on the next clone; `updateInstead` would then bounce the next push against a tree the lockfile
itself keeps dirty between acquisitions. It has to live outside `RepositoryRoot` entirely.

Per `## NEXT` obligation 2, the path is a property on `ContentPaths` — `LockFilePath`, computed as
`Path.Combine(DataRoot, "wiki.lock")` — following `KeysDirectory`'s exact precedent (a config-independent
sibling of `RepositoryRoot`, `<DataRoot>/keys`) rather than a parallel notion of where things live.
This resolves the tension the brief names: `GitHookInstaller` resolves the *hooks* directory dynamically
(`git rev-parse --git-path hooks`) because git itself decides where it will look for a hook — `core
.hooksPath`, a gitfile layout, or the default — and a hardcoded guess can silently miss the one git
actually consults. **Nothing in git dictates where a lockfile of our own invention lives**; there is no
protocol requirement pulling it toward `.git/`, so the reason the hooks path must be resolved dynamically
does not transfer to it. A fixed, `DataRoot`-relative constant is simpler, needs no `git` subprocess call
just to find the file to lock, and matches `KeysDirectory`'s already-established shape for "outside the
repository, on the volume, derived once." Because acquisition on both sides — the app's own commit path
(§5.2) and the push wrapper around `git http-backend` (§7.5) — happens in C# rather than in a generated
shell script, `ContentPaths.LockFilePath` never has to be baked as a literal path into hook text at
install time; neither hook needs to know it exists at all.

**Acquisition policy — bounded for saves, unbounded for pushes (Product Owner decision).**

- **Ceiling:** a configurable `ContentStorage:WriteLockTimeout`, defaulting to **10 seconds**, bound the
  same way as `ContentStorageOptions.DataRoot` (an `IOptions<ContentStorageOptions>` section, overridable
  by `appsettings.*.json` or an environment variable in the container). Ten seconds comfortably covers an
  ordinary commit-on-save (sub-second for a small text file) plus contention from a concurrent push of
  realistic size, while still failing well inside typical browser/reverse-proxy request timeouts (tens of
  seconds), so a stuck save surfaces as a clear error rather than a silently hanging tab. **This
  paragraph describes the save path's intended framing, not what ships today**: §6 (commit-on-save) is
  not yet built, so the one implemented consumer of this value is `ContentRepositoryService`'s **startup**
  accept-phase acquisition, whose expiry is a fatal refusal to start rather than a per-request failure —
  see `ContentStorageOptions.WriteLockTimeout`'s own doc comment for the operator-facing consequence of
  that gap. Whether §6.6's save acquisition shares this same configured value or gets its own is an open
  decision owed to §6.6 (`## NEXT`), not settled by this paragraph.
- **What the app does when it expires:** the save request fails outright — no file is ever written, since
  the lock is acquired *before* the write+commit begins — with a distinct error response (not D4's 409;
  this is lock contention, not a stale base revision) telling the caller the repository is busy and to
  retry. This is a per-request, recoverable failure, not one of D9's fatal startup refusals — the process
  keeps running and serving other reads.
- **What the operator sees:** a structured warning-level log entry recording how long the request waited
  before giving up, on the same request-handling path as other write failures — not a crash, not a
  silently swallowed error.
- **The asymmetry, justified on its own terms:** a browser save has a person synchronously waiting on an
  HTTP response; an indefinite hang is a hung tab with no recovery path but closing it, for a failure mode
  that costs nothing to recover from (nothing was written yet — the user just retries). A push is a
  background sync — `obsidian-git` runs it off an interval or an explicit user action with no one watching
  a terminal in real time — and more importantly, timing the push side's wait out mid-push has a *worse*
  failure shape than waiting: git's push protocol has no clean "busy, retry" signal to send back through
  `git http-backend`, so a bounded wait there would surface as a rejected or malformed push the client has
  to interpret, where an unbounded wait instead just... finishes, correctly, the moment the (normally
  brief, bounded) save that is holding the lock releases it. This reasoning does not depend on which
  process performs the acquisition — it held when a hook was the candidate lock-holder, and it holds
  now that the app's own `http-backend` wrapper (§7.5) is.
- **What an unbounded wait costs when the app itself is what is stuck — sharper now than when a hook
  would have been the one waiting.** If the app's own accept-and-write phase (§5.2) or a browser save's
  commit path holds the lock and hangs — a bug, not ordinary contention, since bounded acquisition and a
  crash both release the OS-level `flock` automatically — every subsequent push's wait now also runs
  *inside the app process itself* (the §7.5 wrapper around `git http-backend`, not a separate hook
  process), blocking forever with no timeout to break it and no diagnostic beyond "the push never
  returns." The operator's only lever is restarting the app process, which releases the lock as a side
  effect of the process exiting — the same lever as before, but now the thing an operator restarts to
  break the deadlock is the very process that is also refusing to serve the hung request, not a
  short-lived child of it. This is a real, named cost of the asymmetry, not a case the policy pretends
  away, and moving the mechanism from a hook to the app's own wrapper (§5's Product Owner decision) does
  not reduce it.
- **Why no hook may attempt this acquisition itself, even as a defensive no-op.** Once the app holds the
  lock around the whole `http-backend` invocation, that lock is held by the *parent* of every hook the
  receive runs — `pre-receive` and `post-receive` are children of the wrapped subprocess, which is itself
  a child of the app. A hook that calls `flock` on the same lockfile blocks on its own parent, which
  cannot release the lock while it is itself waiting on the hook to exit: `flock(2)` is per-open-file-
  description, so a separate process gets no re-entrancy, and this acquisition policy makes the wait
  unbounded on both sides of that deadlock. The result is not a slow push but one that never returns.
  `GitHookInstaller`'s generated hook bodies carry this warning explicitly, for whoever next edits them.

**Obligation 16 — the write lock is what makes `repositoryHasNoCommitsYet` a snapshot safe to trust, and
only once it wraps classification too.** `EnsureInitialCommitAsync` trusts a boolean
`EnsureRepositoryAsync` computed earlier in the same call, rather than re-verifying `HEAD` immediately
before committing — correct today only because startup is single-threaded and unlocked, so nothing else
can move `HEAD` between the two. **The racer this protects against is two app instances over one shared
volume during a container swap — not a push:** a `pre-receive`/`post-receive` hook cannot fire until some
app is already accepting connections and serving `git http-backend`, so no push can race a *first* start;
a second app instance starting concurrently against the same `/data` volume (a rolling deploy overlapping
the outgoing and incoming containers) can.

What *guarantees* that first half is the composition-root ordering, not an inference about how quickly a
push could arrive: `Program.cs:97` awaits `app.EnsureContentRepositoryAsync()` and `app.Run()` is not
reached until `Program.cs:180`, so Kestrel is not yet accepting connections anywhere in the accept
phase. A single instance therefore cannot self-race a push against its own startup as a matter of
program structure — which is also why this ordering is load-bearing rather than incidental, and why
moving the bootstrap call after `app.Run()` would silently reintroduce exactly the race the lock is
being added to close.

Once §5.1–5.2 land, the lock has to wrap the **entire accept phase** — from directory creation and
classification (`HasOwnGitEntry`, `LooksLikeBareGitDirectory`, `RepositoryHeadIsUnbornAsync`) through
`AssertWorkingTreeIsCleanAsync` — not only the writes downstream of it. Wrapping only the writes and
computing `repositoryHasNoCommitsYet` outside the lock would still leave a second instance able to compute
the predicate, block on the lock while the first instance's accept phase runs to completion (creating the
initial commit the second instance never saw), then enter its own write phase trusting a now-stale `true`.
**The predicate must therefore be (re-)derived after the lock is acquired, not merely trusted as a value
computed earlier in the call** — concretely, block B's `AcceptRepositoryAsync` acquires the write lock
first and performs classification inside it, so no other instance's write can land between "read the
repository's state" and "act on it." This does not extend to `ConfigureRepositoryAsync` (D3's own
"configure phase" — `receive.denyCurrentBranch`/`http.receivepack` and hook installation): neither writes
tracked content, hook installation touches only `.git/hooks` rather than the working tree the clean-tree
invariant governs, and pushes cannot arrive before configuration has already run once — so nothing in that
phase needs the mutual exclusion the lock exists to provide.

### D17 — The save path: what a base revision is, what stamps it, and what a git exit code may be trusted to mean

D4 says a save carries "the revision it started from" and is rejected if the file has advanced; D5 and
D10 say who authors the resulting commit; D16 says it runs under the write lock. This settles the six
things §6 has to decide before any of that can be built, and records the three Product Owner calls taken
at the section's open.

**Surface — a minimal Static SSR edit form, not an editor (Product Owner decision).** `proposal.md`
defers browser editor UX to a later change, so §6 ships the smallest browser-facing surface that makes
the write path real: a `textarea` carrying the page's Markdown, the base revision in a hidden field, and
a Save button, posting back to the address it was served from. **Explicit save *is* the save-point.**
This is what discharges 6.4, and it discharges it structurally rather than by mechanism: with a form
post, no keystroke ever reaches the server, so there is nothing to debounce and no path by which more
than one commit per save could be produced. A later editor change may add a client-side debounce on top;
it can never make this weaker, because the commit is driven by the post, not by the keystrokes.

**The address is `/wiki/{*Route}` with an `edit` flag on the query string — editing is a *mode of the
page*, not a second address for it (Product Owner decision, §6 block D1).** An earlier revision of this
decision said `/wiki/{*Route}/edit`, and that is **not a legal route template**: a catch-all parameter
may only occupy the last segment. Verified by execution rather than read off documentation —
`RoutePatternFactory.Parse("wiki/{*Route}/edit")` throws `RoutePatternException: A catch-all parameter
can only appear as the last segment of the route template`, while `wiki/edit/{*Route}`,
`edit/{*Route}` and today's `wiki/{*Route}` all parse. **The decision was unimplementable as written
and shipped through block A's four review rounds unnoticed**, which is worth more than the correction
itself: every round audited what the paragraph *argued*, and nobody parsed the string it *named*.

*Why the query flag rather than a legal sibling route.* The two legal shapes each claim route space,
and one of them collides. Routes mirror directories (`PageRouteCodec.Encode` maps the separator
straight through), so a file at `docs/edit/foo.md` has the route `edit/foo` — and under
`/wiki/edit/{*Route}` a literal segment outranks a catch-all, so that page's own view address would
serve the *edit form for `foo`* and the page itself would be unreachable. Not theoretical: nothing
stops a member creating `edit/` in the vault, and §9's Obsidian round-trip means content arrives from
outside the app's control. `/edit/{*Route}` avoids the collision by claiming a second top-level path.
The query flag claims **nothing** — no new template, no reserved name, and no page route that can ever
be shadowed, now or by any content anyone adds later. That permanence is the reason, not the URL's
looks.

*The consequence, named rather than discovered:* view and edit share one component and one route, so
the component branches on the flag, and the flag's **presence** is what selects the mode. A valueless
`?edit` is a real binding hazard — the bound value is empty, not "true" — so the exact parameter form
and its binding is something the surface block must **run**, not assume; this change's standing rule
about tracing a premise applies to the correction as much as to what it corrected. (That block was D3
when this paragraph was written and is **D4** after §6's D3/D4 re-carve — the obligation followed the
surface, not the number.)

**The edit surface creates as well as edits (Product Owner decision, §6 block D1).** A route that
resolves to no existing page opens an empty form declaring `PageBaseRevision.AbsentAtHead`, and saving
it writes and commits a new file. The mechanism is already built and already spec'd — C2's CAS treats
"declared absent, still absent" as a match precisely so a brand-new page is a first-class save — so
this decision buys surface, not machinery. It is also what makes §9's round-trip demonstrable in both
directions from the browser rather than only in the pull direction.

*What this does not weaken:* the created path goes through `TryResolveWorkingTreePathFromRouteValue`
and the canonical-route refusal exactly as an edit does, so creation cannot reach a path an edit could
not. That is the whole of the guarantee, and it is narrower than it sounds — see immediately below.

**D12's ambiguity rule does *not* govern the save path today, and exposing creation is what makes that
reachable (a reviewer blocker on this block; the claim it replaces was mine and was false).** An earlier
revision of the paragraph above asserted that "a route that is ambiguous, non-canonical or undecodable
is refused for creation exactly as it is for viewing". Only two thirds of that is true. Verified by
reading the code rather than by re-reading the sentence: `PageSaveService` touches `IPageIndex` exactly
once in the whole class — `_index.Invalidate()` on the rollback path — and **never consults
`AmbiguousRoutes`**. Its only route defences are the resolve and the canonical-route check. The
ambiguity refusal lives solely on the read path, in `WikiPage.razor`.

The two surfaces therefore disagree, and the encoder supplies the witness: `Chapter_1.md` encodes to
`Chapter__1` (literal `_` doubles) and `Chapter  1.md` encodes to `Chapter__1` (two spaces, each to
`_`). D12 calls that route ambiguous and the read path answers 409 naming both claimants; a save through
the same route passes `IsCanonicalRouteValue` cleanly and writes to whichever one the decode picks,
silently, while the other file becomes unaddressable. **This predates creation** — C2's edit path has it
too — but it has been unreachable because nothing calls `SaveAsync` from a browser yet. D3 is what ends
that, and creation is what makes the consequence *new content on an ambiguous route* rather than only a
misdirected edit.

**Decision: the save itself must refuse a route that does not identify exactly one file, and the refusal
must live in the save path, not only at the surface.** A surface-only check is bypassable two ways, and
the second is not exotic: a form post can be submitted directly, and — more importantly — a route can
*become* ambiguous between the open and the save, because §9 means an Obsidian push can land
`Chapter  1.md` while a member is editing `Chapter_1.md`. That is the same read-then-write-is-not-atomic
argument the base-revision CAS already rests on, applied to identity instead of content: a check made
before the lock is a value that may be stale by the time it is acted on.

*The mechanism is deliberately left to D3, and the constraint that makes it non-obvious is named here so
D3 does not rediscover it the expensive way.* "Consult the index under the write lock" is **not** a free
answer: this decision already rejects making the save path block on `PageIndex`'s internal refresh gate,
for the lock-ordering reason recorded above, and it is safe today only because readers never take the
write lock. An answer derived from the working tree instead needs to handle collisions arising in a
*directory* segment — `a_b/` and `a  b/` (two spaces) both encode to `a__b/`, the same pairing D12's own
table records and the directory-level twin of the `Chapter_1.md`/`Chapter  1.md` witness above — and not
only in the filename, so a single-directory scan is not sufficient either. D3 owns choosing between
these and justifying the choice; D1 records that one of them must be chosen, because leaving the save
path as the one surface D12 does not govern is not an option once a browser can reach it.

**The mechanism, chosen in §6 block D3: a fresh working-tree enumeration under the write lock**, via
`PageEnumerationService.EnumeratePages()`, asking whether the save's canonical route appears in the
result's `AmbiguousRoutes`. It runs after the lock is acquired and before the CAS.

*Why not the index snapshot.* D1's framing above — that consulting the index is barred by the
lock-ordering objection — is **too strong, and the correction matters more than the conclusion**.
`IPageIndex.Current` is a plain read of the installed snapshot and never touches `PageIndex`'s
`_refreshGate`, so the lock-ordering argument does not reach it at all; that argument is about the save
blocking on the *refresh*, which `Current` does not do. The snapshot is rejected for an entirely
different and more decisive reason: **nothing refreshes it as a side effect of a save**, so it can be
arbitrarily stale with respect to a push the app has not yet noticed — and a push the app has not yet
noticed is *precisely* the in-flight case this refusal exists to catch. A mechanism whose blind spot is
exactly its own motivating scenario is not a cheaper option, it is a non-answer.

*Why not a git-side answer.* Re-deriving the collision rule against `HEAD`'s tree objects would
duplicate `PageEnumerationService`'s own logic in a second place, and two implementations of D12's
ambiguity rule is exactly the duplication that makes it possible for the two surfaces to disagree —
which is the defect this decision exists to close. Outside a lock-held save the working tree equals
`HEAD` by D9's invariant, so the git-side answer is not more authoritative either; it is the same answer
computed twice.

*Why a full walk rather than a scoped lookup.* A collision can arise in a **directory** segment, so
there is no bounded neighbourhood to scan — the constraint D1 recorded, discharged rather than worked
around.

*The cost, measured rather than assumed:* `EnumeratePages()` is a **path-only** walk — it derives routes
from filenames and reads no file contents and no frontmatter. So the per-save cost is directory
traversal, not content I/O, and it notably does **not** read working-tree bytes, so it does not touch
the hazard the rollback paragraph below is about. A full walk on the *normal* save path is nevertheless
a deliberate trade, not an incidental one: D15's full rebuild is chosen for the *exceptional* rollback
path, and this is a different bargain being struck for a different reason. It is accepted here because
ZeroWiki is a wiki for a small trusted group over a mounted volume, and correctness of D12's invariant
on the one surface that can silently violate it is worth a directory walk. If a deployment ever makes
that walk material, the answer is to make enumeration incremental, not to narrow this check.

**`LoadForEditAsync` probes `HEAD` *before* reading the file, and the order is load-bearing.** An editor
load is not atomic: it produces a `(content, base revision)` pair from two separate reads, and which one
happens first decides how a concurrent write can corrupt the pair.

- **Probe first, then read** — a write landing between them means the content read is *newer* than the
  declared base. The base falls behind the truth, the CAS sees a mismatch, and the save is refused as an
  honest `Conflict`. The member is told to reload; nothing is lost.
- **Read first, then probe** (rejected) — a write landing between them means the base is *newer* than
  the content the member is editing. The base has caught up past what was actually read, so the CAS
  compares clean and the save commits, **silently discarding a version the member never saw**. That is a
  lost update produced by the very mechanism meant to prevent one.

The asymmetry is the whole argument: one ordering can only ever manufacture a false conflict, the other
can manufacture a false agreement. Where those are the two failure modes available, the correct choice
is the one that fails toward refusing.

**Every failure outcome stays distinguishable at the browser surface, and none of them discards what the
member typed (Product Owner decision, §6 block D1).** There are **five**, not the four this section's
DEVLOG has been counting since C2: conflict, repository-busy, refused, failed-and-rolled-back, and
failed-with-the-rollback-also-failed. `Refused` was the one dropped from the tally, and it is not merely
an open-time answer — it is reachable at *write* time, from the symlink refusal and from a base-revision
probe git cannot answer with a blob-or-absent state. The surface must not collapse these into one "save
failed", because they call for different actions — retry now, retry in a moment, fix the address or the
file behind it, reload, and tell an operator respectively. On a **stale-base conflict** specifically the
form re-renders
carrying **exactly the text that was submitted**, with a message saying the page changed underneath the
save and that nothing was written. The member copies their work out and reloads; §6 offers no merge and
no automatic reload, because a minimal form that silently replaced a member's text with someone else's
would lose the edit the CAS exists to protect. `specs/content-editing/spec.md` gates this, so a section
review sees it rather than this paragraph.

**Base revision — the file's blob SHA at `HEAD`, not the repository's `HEAD` and not the file's
last-commit SHA.** A save declares the object name of the content it loaded, and the server compares it
against the one it reads back **after acquiring the write lock**.

*Why not repository `HEAD`:* it advances on every commit to any file, so an Obsidian push touching an
unrelated page would refuse a save that conflicts with nothing. The spec says "*the file* has advanced",
and a check that cannot tell those apart manufactures conflicts the member cannot act on.

*Why not the file's last-commit SHA* (`git log -1 -- <path>`): it answers "was this path touched", which
is not the same question as "did this content move". A commit that touches the path and restores byte-
identical content advances it, refusing a save that would lose nothing. It also costs a history walk per
editor load where the blob name costs a single object lookup.

*Why the blob name is the right identity:* it is exactly the content the member started from. If two
revisions name the same blob there is by definition no lost update to prevent, and if they differ there
certainly is. It is also stable under history rewriting in a way a commit SHA is not.

**How the blob name is read — and why not `git rev-parse HEAD:<path>`, which is the obvious choice and
is wrong for the reason this decision itself gives two sections below** (a reviewer blocker on this
block; the defect class D17 exists to close, caught inside D17). Reproduced on `git 2.55.0`:
`git rev-parse HEAD:<path>` exits **128** *both* when the path is absent at `HEAD`
(`fatal: path '…' does not exist in 'HEAD'`) *and* when `HEAD` is unborn or invalid
(`fatal: invalid object name 'HEAD'`). One exit code, two states that must be handled in opposite ways —
the first is this decision's ordinary absent-sentinel, the second is precisely the fault D9 and the
exit-code posture below require to refuse loudly. An implementer reading `128 → absent` off the first
case would rebuild `RepositoryHeadIsUnbornAsync`'s exact defect in the section that fixes it.

The instrument is therefore **`git ls-tree HEAD --format='%(objecttype) %(objectname)' -- <repo-relative
path>`**, which discriminates every state without consulting an exit code for any of them. Verified by
execution on the same version:

| state | exit | stdout |
|---|---|---|
| path present at `HEAD` | 0 | `blob <sha>` |
| path absent at `HEAD` | 0 | *(empty)* — the sentinel |
| path names a directory | 0 | `tree <sha>` — refuse; a route may only ever resolve to a file |
| `HEAD` unborn or invalid | 128 | *(stderr)* `fatal: Not a valid object name HEAD` — refuse |

Exit 0 with unexpected stdout, and any non-zero exit, are both faults and both refuse. That catch-all is
load-bearing rather than defensive: a **gitlink** (mode 160000) reports `commit <sha>` at exit 0, and it
is genuinely reachable — §2's gitlink refusal is scoped to reconciliation and the initial commit, so it
never runs against pushed content, and a gitlink can therefore arrive at `HEAD` by push. The catch-all is
the only thing that refuses it. `--format` also sidesteps `core.quotePath` — which **does** mangle
non-ASCII paths in `ls-tree`'s default output, and has bitten this change before — by emitting no path at
all, so nothing in the output needs unquoting. The `--format` option requires git ≥ 2.36; the shipped
image's `git` is 2.43 (verified inside `mcr.microsoft.com/dotnet/aspnet:10.0`, not inferred from the
distribution).

**This instrument's input contract: a repo-relative path that has already been through `PageRouteCodec`,
and it is a contract rather than a courtesy.** Given a path with a **trailing separator** `ls-tree`
recurses and prints one `blob <sha>` line *per child*, which passes a naive "starts with `blob`" check
and yields an arbitrary child's object name as the base revision — a wrong answer reported as a right
one, the same failure shape `rev-parse`'s doubled 128 had. §6's own call site cannot produce one
(`TryDecodeCore` refuses empty segments and always appends `.md`), so this is not a live defect; it is
written down because §7/§8 conflict detection is the likely next caller of a base-revision read and would
not inherit that guarantee. A caller without it must trim trailing separators or refuse them.

**"Present" here does not mean "an ordinary file."** Git models a symlink as a blob whose content is the
link target, so `%(objecttype)` cannot distinguish one, and enumeration's reparse-point skip does not
help: the save path accepts a route for a page that need not already be enumerated, so a symlink arriving
by push can sit at a save's resolved path and read as an ordinary present blob. The instrument is not
wrong by its own terms — the obligation lands on **the write step**, which must not follow a symlink out
of `docs/`.

The absent case is carried as an explicit sentinel rather than an empty string, so "I started from
nothing" and "I did not declare a base" are distinct inputs and the second is refused rather than
treated as the first.

**A save whose content is byte-identical to `HEAD` commits nothing and succeeds.** The CAS passes (the
base matches), the write is a no-op, `git add` stages nothing, and `git commit` would fail
"nothing to commit" — which would otherwise drive the *rollback* path and report a failure to a member
who did nothing wrong. So the save path checks whether anything was actually staged and, if not, returns
success without a commit. `--allow-empty` is rejected: an empty commit is history every Obsidian vault
then pulls, to record that nothing happened. This is a *clarification* of "exactly one commit per
save-point", not an exception to it — the requirement exists to forbid many commits per save, and where
there is no content change there is nothing to record. `specs/content-editing/spec.md` states it
explicitly so the section review is gated on it rather than on this paragraph.

**The comparison happens under the lock, and the lock is what makes it a CAS rather than a check.**
Read-then-write is not atomic across processes; acquiring D16's lock, *then* re-reading the blob name,
then writing, staging and committing, is. A base revision read before the lock is a value that may
already be stale by the time it is acted on — the same defect `## NEXT` obligation 16 recorded against
the startup path, in the one place where its consequence is a silently lost edit rather than a refused
boot.

**Authorship must be total over accounts, and D11 cannot make it so.** D10's *Consequence binding §6*
requires a well-formed address for **every** account, including one whose username predates D11 and
never will be subject to it (`LoginServiceTests.cs:271-289` pins `ab`, `_legacy_` and `.old.name.` still
authenticating). The address is therefore constructed in two cases, not one:

- A username that already satisfies RFC 5322 `dot-atom-text` is used directly as the localpart —
  `<Username> <username@<configured host domain>>`, exactly D10.
- One that does not gets a **deterministic fallback localpart drawn from a space no D11-legal username
  can ever occupy.** D11 admits only `[A-Za-z0-9_.-]` with alphanumeric ends; RFC 5322 `atext` is
  strictly wider, so a fallback built with an `atext` character D11 forbids — `+` — is legal as a
  dot-atom and **unreachable by any username anyone can choose**. The account's own primary key
  disambiguates, so two legacy usernames can never collapse onto one address.

**The domain defaults to `zerowiki.org` (Product Owner decision, §6 block C1).** D10 already fixes *how*
the domain is supplied — deployment configuration, never the request's `Host` header — but not what a
deployment gets when it configures nothing, and "point it at a folder and it Just Works" means that
default has to be a working one rather than a refusal.

`zerowiki.org` **is the Product Owner's own domain**, which is what makes this the right default rather
than merely a convenient one: it is the same namespace `GitAuthor.System`'s `system@zerowiki.org` already
stamps on D9 recovery commits, so a repository's history carries **one** authorship convention covering
both the software's own identity and its members', and no address ZeroWiki writes ever names a domain
belonging to a third party. An earlier draft of this decision proposed `zerowiki.invalid` (RFC 2606,
guaranteed non-resolvable) on the mistaken belief that `zerowiki.org` was someone else's — the reasoning
was sound and the premise was false, and it is recorded here because the two are separable and only the
premise was wrong.

*Accepted, and named rather than discovered later:* an address under a domain that genuinely resolves
**looks** deliverable, so it can be harvested from any repository that reaches a public remote, and a
member may reasonably assume it is a real mailbox. D10's "synthetic, need not be deliverable" still
governs — nothing is obliged to receive mail there — but the Product Owner keeps the option open, which
`.invalid` would have foreclosed permanently for every deployment.

That last property is not decoration. D10's *Consequence binding §8.3* has the inbound resolver match
the synthetic form *first*, ahead of registered `GitEmails`, precisely so a squatted row cannot capture
another member's attribution; a fallback that a member could reproduce by choosing a username would
reopen that hole from the other side. The display name stays the raw username — git's name field
constrains only `<`, `>` and newline — so a legacy member's history still reads as themselves.

**A route's identity is a claim about a *name*; a filesystem's identity is a claim about a *file*, and a
case-insensitive volume makes those two things disagree (a supervisor blocker on §6, the section's most
serious defect).** D12 computes ambiguity over names: `Page` and `page` are different routes because
`Page.md` and `page.md` are different names. On a case-insensitive filesystem — macOS by default, and
Windows — they are **one file**. Enumeration walks the tree, sees `Page.md`, and publishes the route
`Page`, so `/wiki/page` reads as "no page here yet" and offers to create it, while a write to
`docs/page.md` lands on the existing `docs/Page.md`. Reproduced end to end:

```
core.ignoreCase = true            (git auto-detects it)
HEAD:   docs/Page.md = "Original body."
write:  docs/page.md = "NEW TEXT FROM MEMBER"   -> only Page.md exists; overwritten
git add docs/page.md              -> stages NOTHING
git status --porcelain            ->  M docs/Page.md
```

**Two guards, and they are not redundant — one is specific and one is general.**

*Specific:* a save refuses when the route's resolved path does not match the on-disk entry **exactly and
case-sensitively**, so route `page` can never write to `Page.md`. `LoadForEditAsync` applies the same
refusal, so the editor stops offering to create a page that already exists under another casing. The
check is gated on the filesystem's own answer rather than on comparing names, so a genuinely
case-sensitive host — where `Page.md` and `page.md` really are two pages — is not falsely refused.

*General:* **the save verifies the working-tree-clean invariant instead of inferring it.** The paragraph
above on identical content reasons "identical ⇒ nothing staged ⇒ report success without a commit". That
implication is true left to right and was being used right to left. **Nothing-staged has more than one
cause and only one of them is benign**; the case-collapse above is one, and the whole point of the
general guard is the ones nobody has thought of. So when nothing was staged, the save asks whether the
file's content actually matches what `HEAD` holds for that path, and if it does not, rolls back and
reports failure rather than success. This change's own standing rule — *when a guard's justification
names a case, check the guard's branch* — applied to a justification that named the only case its author
imagined.

**The instrument is `git hash-object`, compared against the blob sha the `ls-tree` probe already read —
and the first attempt at this guard used `git status --porcelain`, which was wrong in a way worth
keeping on the record (a reviewer blocker at the section review's second round).** `git status` reads
the **index**, and the index is precisely the bookkeeping the guard exists to distrust. A single
`git update-index --assume-unchanged` blinds `git add`, `git status` **and** D9's startup reconciliation
at once — verified by execution:

```
assume-unchanged, then write new bytes
  git add -A                        -> stages nothing
  git status --porcelain -- <path>  -> ''
  git status --porcelain (tree)     -> ''
  HEAD: Original.        disk: MEMBER TEXT
```

So the guard and the thing it guarded **shared an instrument**, and the resulting loss is worse than the
case-collapse that motivated the guard: there, D9 eventually committed the member's bytes as a system
recovery commit, so the edit landed unattributed; here **nothing ever notices at all**.

*Why `hash-object` is the right instrument and not merely a different one.* It is a pure function of
content plus attributes and **never consults the index**, so no index flag can blind it — while still
agreeing with the blob git would actually store if a `.gitattributes` filter is in play. An in-process
SHA over `blob <len>\0<bytes>` was rejected for that second reason: it touches no git at all, which
sounds stronger, but it would diverge from the real blob the moment a filter exists and would then
manufacture false faults on legitimate content.

**The general rule this earns, which outlives the specific bug:** *you cannot verify a subsystem's
bookkeeping by asking that subsystem.* This is the change's *audits sharing an instrument* rule (§0's
`href=""` regex, §3's link-destination XSS, §11's service-model differential, §6 D4's body-only
assertions) appearing for the first time in a **guard** rather than in an audit — and a guard is where it
costs the most, because an audit that shares a blind spot fails to find a defect while a guard that
shares one actively certifies its absence.

*Two further causes of an empty staging result the content comparison also covers*, named because each
was a distinct hole and none was reachable by the `status` instrument: a brand-new page
(`AbsentAtHead`) that stages nothing — always a fault, since a new file must stage — and content sitting
under a `.gitignore`, which stages nothing *and* is invisible to `status`.

**What §6 block D4 shipped that this decision must also name, because four code sites cite D17 for it**
(a supervisor blocker: `751cf95` changed `design.md` by six lines of block renumbering and no spec while
shipping all of the following):

- **The browser writes LF.** HTML form submission sends `textarea` content with CRLF; the save
  normalises CRLF and lone `\r` to LF before writing. A repository that is the source of truth, read by
  an Obsidian vault, should not accumulate line-ending noise from the one writer that emits it.
- **`core.autocrlf=false` is pinned on the content repository**, before any `git add` runs on any
  startup path. Without it an inherited host `core.autocrlf=input` makes `add -A` warn on stderr while
  exiting 0, which block B's posture correctly refuses — so an ordinary browser save made the app refuse
  to start, intermittently, only when git's index stat cache was cold. **The general lesson outlives the
  setting: this app's correctness must not depend on configuration inherited from the operator's
  `~/.gitconfig`.** `core.quotePath` was the same defect fixed as a one-off; the remaining inventory
  (`commit.gpgsign` first) belongs to §7 as a class.
- **The failure path is Post/Redirect/Get**, so a reload after a failed save never re-submits it. The
  member's text is carried across the redirect by `EditDraftStore` — an in-memory, short-TTL store keyed
  by an unguessable token, read only by the account that wrote it, capped per account so that one
  member's flood can never evict another's draft.
- **"Never discard what the member typed" outranks Post/Redirect/Get (Product Owner decision).** Where
  the draft store cannot accept a draft at all, the surface re-renders **inline** rather than
  redirecting: the member keeps every character and the resubmit prompt returns, in the one case that is
  hardest to reach. The spec's SHALL stays absolute and acquires no exception.

**Rollback must invalidate the index, because the rollback path is the one content-changing event that
does not advance `HEAD`.** D15's freshness rests on the invariant that every content change moves
`HEAD`; `specs/content-editing/spec.md:62-69` requires the path that breaks it.

*State the trigger as the invariant, not as one interleaving.* An earlier revision of this paragraph
named a single concrete race — a push landing just before a save takes the lock, so the next reader's
incremental refresh re-reads that same file mid-save — and the reviewer showed that the case *as
literally told* is largely blocked by the CAS re-read under the lock, while the reachable class is
strictly wider. Naming one interleaving would have sent block D hunting the wrong condition; this change
has a standing rule about exactly that (*when a guard's justification names a case, check the guard's
branch*), and a decision that only ever guards its own illustrative anecdote is the same defect
one level up.

The general statement: **the index reads file bytes from the working tree, and it does so on triggers it
does not control and that are unrelated to which file a save is holding open.** A full rebuild
(`BuildAsync`) walks the *whole* tree unconditionally — so any writer at all advancing `HEAD`, or a
startup, or a previous snapshot carrying an unreadable directory, can make the index read some
*other* in-flight save's uncommitted bytes; the incremental path can do the same whenever an unrelated
writer's `HEAD` advance names a path a save happens to be mid-write on. In every such case the commit
then fails, `git checkout --` restores the file, and `HEAD` sits wherever the *other* writer left it —
which is exactly what the index is already stamped with. The aborted bytes' title and tags are now in
the index, the stamp says it is fresh, and no later incremental refresh will revisit that path.

The save path therefore **invalidates the snapshot as part of the rollback**, installing
`PageIndexSnapshot.Empty` so the next read takes D15's no-previous-stamp branch and rebuilds in full. A
full rebuild is the expensive option and is chosen deliberately: rollback is an exceptional path, and an
exceptional path is the wrong place to be clever about which single entry to repair. A *successful* save
needs no such handling — it advances `HEAD`, which is exactly what the incremental path is for.

**Installing `Empty` is not by itself enough, and saying only that would leave the mechanism losable**
(a reviewer blocker on this block, round two — the same paragraph's *second* correction, one level up
from Note 1's). `PageIndex.Replace` is an unconditional `Volatile.Write`: a last-writer-wins swap, not a
compare-and-swap. So a reader already inside `RefreshAsync` — holding `_refreshGate`, having *already*
read the save's dirty bytes off disk — can call `Replace(refreshed)` **after** the rollback has called
`Replace(Empty)`, and the contaminated snapshot wins. Its `CommitSha` still equals live `HEAD`, because
the rollback never advanced it, so every later freshness check reports fresh and no incremental refresh
ever revisits the poisoned entry. The invalidation is silently undone by the very rebuild it was racing.

**The mechanism: generation and snapshot held as one immutable state, installed by compare-and-swap.**
`PageIndex` holds a single reference to an immutable `(Generation, Snapshot)` pair rather than a
snapshot field and a counter beside it.

- `Invalidate()` installs a new state with the generation incremented and the snapshot `Empty`.
- `GetCurrentAsync` **captures the whole state object** before the refresh begins its disk reads, and
  installs the result with `Interlocked.CompareExchange`, succeeding only if the current state is still
  the very object it captured. If it is not, the refreshed snapshot is *discarded* — it was computed over
  bytes an invalidation has since disowned.
- Exactly **one** unconditional installer is exposed for callers to use, it is the startup install, and
  it is named for that rather than left as a general-purpose setter (`Program.cs` ordering makes it
  unraceable, D16).

*`Invalidate()` is deliberately an unconditional write too, and does not need to be a CAS loop.* It is
not an exception to the rule above — the rule constrains what a *caller* may reach for, and the safety
property is that no snapshot **computed before an invalidation** can be installed after one. The CAS
enforces that by comparing object identity, so an unconditional bump always wins over an in-flight
refresh and always must: an invalidation is never stale, because it asserts that what is on disk has
changed rather than reporting something read from it. A CAS loop here would only make `Invalidate()`
retry until it won, which is what an unconditional write already achieves in one step.

The deeper reason, which is the one that will still hold if `Invalidate()` ever gains a second caller:
**`Empty` makes no claim about any page**, so installing it can never be *wrong*, only wasteful. A losing
refresh costs a rebuild; a losing invalidation would cost correctness. That asymmetry — not the disk
having changed — is what makes the unconditional write the safe side to be on.

*Why a CAS and not a capture-then-compare.* Reading the generation, comparing it, and then writing the
snapshot is a check-then-act: an invalidation landing between the compare and the write is still lost,
on a window that is narrow (no I/O in it) but real (a reviewer finding, round three — "closes it" was
unqualified and was not quite true). Making the compare and the install one atomic operation removes the
window rather than shrinking it, and costs nothing here, so there is no reason to accept a residual race
and document it.

*Why one unconditional installer and not a public `Replace`.* Leaving a general unconditional setter
public reintroduces exactly the weakness this decision rejects gating for (round three, and the reviewer
was right that the earlier wording oversold this): a future *refresher* calling it directly bypasses the
CAS as silently as a future *reader* skipping `_refreshGate` would bypass a gate. The generation removes
a dependence on invalidation-side and read-side discipline; it does not by itself remove a dependence on
install-side discipline, and narrowing the installer is what does that.

**On a discard, the request retries rather than serving what it built.** The refreshed snapshot is known
to be contaminated, so it must not be returned even for the one render that produced it — obligation 25
prevents serving an abandoned save's metadata, and doing it transiently to a live page is still doing it.
Returning the now-`Empty` state instead is also wrong: it renders an existing page as missing. So the
call **re-enters its own refresh**, bounded to a small number of attempts; the retry converges as soon as
no rollback is concurrent with it, which is the overwhelmingly common case, because rollback is an
exceptional path to begin with. If the bound is exhausted the call serves the empty state and logs it: at
that point invalidations are arriving faster than the index can rebuild, which means repeated commit
failures, and reporting the page as missing is the honest degradation where serving metadata known to be
contaminated is not. **The invariant to preserve when implementing this is "never serve a discarded
snapshot", not "always succeed".**

*Why a generation rather than routing the rollback's install through `_refreshGate`.* Gating it would in
fact work today, because every disk-read-into-a-snapshot currently happens inside that gate — but it
works only *because* of that, and nothing enforces it: the startup path already installs outside the
gate, and one future call site that reads outside it breaks the property silently. It would also make the
save path (holding the write lock) block on the index's internal gate, adding a lock-ordering fact that
is safe today only because readers never take the write lock. The CAS is local to `PageIndex` and depends
on neither of those. `## NEXT` obligation 26 is the reason to prefer that: once a save resolves through
the index, the index stops being a rebuildable convenience and becomes a data-integrity path, and a
data-integrity path should not rest on "nobody adds a call site."

**The rollback must restore the file *before* it invalidates, not after.** Invalidating first leaves a
window in which a refresh starts after the generation bump, reads the still-dirty bytes, and installs
with a generation that legitimately matches. Restore-then-invalidate removes that window: after the bump
there are no dirty bytes left to read. A refresh whose read *straddles* the restore is handled by the CAS
above, not by the ordering — it captured the pre-bump state, so its install fails and it retries.

**Write-lock timeouts split into two options (Product Owner decision).** `ContentStorage:WriteLockTimeout`
keeps its meaning and its 10s default: the startup accept phase, where expiry is a fatal refusal to
start. A second option gates the save's acquisition and defaults **shorter** — a browser request budget,
so a member meets a clear "repository busy, try again" rather than a hung tab.

*Why they split rather than share:* the two expiries have materially different costs and would otherwise
be tuned against each other. `AcquireAsync` measures with `Stopwatch`, and `Environment.TickCount64` and
`DateTime.UtcNow` count host suspension too — **there is no clock that does not**, so this is recordable
rather than fixable. After a host resumes, a save whose bound elapsed while the lid was shut fails busy
and the member retries; a *startup* whose bound elapsed refuses to boot, on a machine that did nothing
wrong but sleep. One number cannot be short enough to give fast save feedback and long enough to tolerate
a rolling deploy's overlap at the same time. A git push's wait remains unbounded and unconfigured (D16).

**One posture on what a git exit code may be trusted to mean (Product Owner decision).** Two sites
answered this question two different ways, and both were wrong in the same direction — trusting an exit
code past what git documents it to mean:

- `git add -A` **exits 0** while warning on stderr that it could not read a directory, so content behind
  it is silently unstaged while reconciliation reports success, `git status --porcelain` reports clean,
  and D9's "never discard" quietly does not hold for that subtree. §2's C# scan does not reach this: it
  guards the *initialise* path only, so on an **adopted** repository it never runs at all.
- `ContentRepositoryService.RepositoryHeadIsUnbornAsync` reads *any* failure as "unborn `HEAD`", but
  `git rev-parse --verify -q HEAD` exits **1** when `HEAD` does not resolve and **128** on "not a git
  repository". `PageIndexBuilder.ProbeCurrentHeadShaAsync` matches exit 1 specifically; this one is the
  wrong-way twin.

**The posture: an exit code is trusted only for what git documents that code to mean.** Where a command
can report a fault on stderr while still exiting 0, the app inspects stderr and **refuses** rather than
proceeding. Where an exit code discriminates a *state*, the specific documented code is matched — never
`!Succeeded`, which folds every unanticipated failure into whichever state the caller happened to expect.

**And a second clause, which the first does not imply and which cost this section a live data-loss
defect to learn** (a reviewer blocker on block B, re-derived by the Architect before being accepted):
**matching the documented code correctly still answers only the question that code is about — check that
it is the question the caller needs.**

Exit 1 from `rev-parse --verify -q HEAD` is not an over-read. It means precisely "`HEAD` does not
resolve to a commit", and that *is* what git calls an unborn `HEAD` — git conflates nothing, because a
new repository and a repository whose `HEAD` points at a deleted branch are **the same state** in its
model. Verified: in a repository with real history on `refs/heads/main`, `git symbolic-ref HEAD
refs/heads/ghost` makes `rev-parse --verify -q HEAD` exit **1 with empty stderr**, while
`rev-parse --is-bare-repository` — the earlier probe that makes 128 unreachable here — still exits 0.

So no refinement of exit-code handling can separate them, and an implementer who goes looking for one
will not find it. The defect is that `RepositoryHeadIsUnbornAsync`'s caller does not actually want to
know whether `HEAD` is unborn; it wants to know **whether this repository has any history**, and it has
been using the first as a proxy for the second. The two diverge exactly when refs exist but `HEAD` does
not resolve — at which point `EnsureInitialCommitAsync` creates an orphan root commit on the phantom
branch, the app starts, serves correct-looking pages, and every gate reports success while the wiki's
real history is reachable from nothing ZeroWiki reads. That is worse than the `add -A` case this
decision opened with: silently orphaning history rather than silently skipping a subtree.

**The question to ask instead is "are there any refs?"** — `git for-each-ref --count=1
--format='%(refname)'`, which exits 0 in both cases and answers by *output*, empty or not, consulting no
exit code at all (verified: empty on a fresh repository, `refs/heads/main` on the dangling-`HEAD` one).
An unresolvable `HEAD` **with** refs present is a repository fault, not a fresh repository, and refuses
under this decision's own "never silently write into a repository whose state we do not understand".

*This is §2's standing rule arriving one level up.* §2 recorded that mutation measures whether a
condition is faithful to its intent and is silent on whether the intent is the right question, and that
every defect it actually produced was of the second kind. Block B's condition was faithful, was
mutation-confirmed 3/3 by two independent agents, and was asking the wrong question — which is why both
of them, and the Architect, initially read it as correct.

*Why refuse rather than log and continue:* silently proceeding is precisely the failure D9 exists to
prevent, and it is worse here than a refusal because it looks like success at every subsequent check.
*Accepted cost, named by the Product Owner at decision time:* an unanticipated git warning can refuse a
startup. That is the trade this project has taken every previous time it arose — a fault that reaches
content refuses loudly rather than degrading quietly.

**`PageRouteCodec`'s two callerless resolvers are resolved here, not grown.** `TryResolveWorkingTreePath`
and `TryResolveWorkingTreePathFromRouteValue` were built for §6 and have no production caller. The save
path's route arrives from an HTTP request, already percent-decoded once by routing, so it uses
`TryResolveWorkingTreePathFromRouteValue`; **`TryResolveWorkingTreePath` is deleted.** §6 adds no third
resolver. The distinct-types redesign the block-3b reviewer asked for lands with it: the two decode
states become distinct types so a contract mix-up is a compile error rather than a documentation duty.
That was deferred in §3 on the grounds that the real shape of the caller was not yet known — it is now,
and this is the section where getting the pairing wrong stops being a wrong *read* and becomes a **wrong-
file write**, which the reviewer established cannot be detected at runtime because an encoded and a
decoded string containing no `%` are the same string.

**`Encode` returns the encoded-route type, and the cascade through `EnumeratedPage`, `PageIndexEntry`,
`AmbiguousPageRoute` and `PageIndexBuilder` is carried out rather than deferred.** This paragraph reached
the opposite conclusion twice before landing here, and both earlier versions are worth stating because
the second was wrong in a way that is easy to repeat.

The implementing block left `Encode` returning `string` and justified it by the change staying confined —
a **scope** argument, which this change had already ruled the wrong basis for a design call (`1253ff5`).
The Architect replaced it with a **property** argument: that the redesign's job was to turn an *invisible*
mix-up (two indistinguishable `string`s, undetectable at runtime, per the block-3b reviewer) into a
compile error, which it does, while wrapping a wrong string requires explicitly writing
`new EncodedRoute(…)` — a written assertion at a countable number of sites rather than a slip. That much
is true. The argument then claimed the cascade would reduce those sites "**but not to zero**, since a
route read back from any stored or enumerated form still has to be asserted into the type somewhere."

**That premise is false, and the review disproved it by tracing rather than by reading.** Every
construction of `EnumeratedPage`, `PageIndexEntry` and `AmbiguousPageRoute` — five sites — sources its
route from a fresh `PageRouteCodec.Encode` call, and there are exactly three `Encode` call sites in
production. Nothing reads a route back from anywhere: D15 keeps the index **in process memory only**,
rebuilt from the repository, never deserialised, and the only other route-shaped input is an HTTP request
value, which is a `RouteValue` and not this type at all. So `EncodedRoute` values originate at exactly
one place, and threading the type through `Encode` removes **every** manual assertion, not merely most of
them — a *stronger* property than the one the argument settled for, not a smaller number of the same
thing.

With the premise corrected the conclusion inverts: the cascade is worth doing, and **doing it now is what
makes it cheap**. §5's `## NEXT` obligation 17 recorded this exact shape — a refactor that is "nearly free
now; more expensive once [the caller] is threaded through the current shape" — and §6's C2 is about to add
the callers that would make it expensive. The type is therefore constructed in exactly one place, and the
compiler, rather than a reviewer's enumeration, is what guarantees it.

**One honest limit on that guarantee, stated because overclaiming it is the defect this change keeps
producing.** An `internal` constructor stops a caller *building* an `EncodedRoute`, but C# gives every
struct a public `default` — `default(EncodedRoute)` is available to anyone and carries a null `Value`.
So the property is "no caller can construct a route with content of their choosing", not "no caller can
obtain an instance". The escape is harmless here rather than by luck: the only value it can produce is the
empty one, and `TryDecodeCore` already refuses a null-or-empty route before anything else looks at it —
which is the same refusal that has guarded this path since §3, not something added to paper over this.
Worth stating plainly, because "constructible only inside the assembly" is the kind of sentence a later
reader trusts to be total.

*Recorded at this length deliberately.* Three passes produced a scope argument, then a property argument
resting on a false premise, then this. Each was more sophisticated than the last and the second was the
most dangerous, because it *sounded* like the kind of reasoning this change asks for. **A property
argument is not self-certifying — its premises are claims about the code and have to be traced like any
other.**

**Fourth pass (§12 supervisor review, remediation round one)** — the third pass above is now
materially false, and this records why and what the guarantee actually is.
`12.3`'s second pass (DEVLOG, `## 12.`) added a second in-assembly construction site for `EncodedRoute`:

- `ChangedOnDiskIndicator.razor`'s `OnInitialized`: `_route = new EncodedRoute(Route);`, where `Route` is
  a `[Parameter] string` fed across the Static SSR→circuit boundary from `WikiPage.razor`'s
  `<ChangedOnDiskIndicator Route="@notifiedPage.Route.Value" />`.

The third pass above rests on exactly the premise this reopens, and states it at length and in bold:
*"Nothing reads a route back from anywhere … the only other route-shaped input is an HTTP request
value."* The component-marker payload **is** a route read back from a serialized form — the DataProtection-
protected `descriptor` JSON a real Blazor circuit start deserializes, dumped and replayed by
`ChangedOnDiskIndicatorRouteRoundTripTests` and its own reflective instrument. The third pass quoted
the second pass's *"a route read back from any stored or enumerated form still has to be asserted into
the type somewhere,"* labelled it **"That premise is false,"** and disproved it by tracing every
construction site in the assembly. §12 made the disproved premise true again, at a site tracing could not
have found because it did not exist yet.

**What is still true, restated precisely rather than left to the third pass's stronger words.** External
callers still cannot construct a populated `EncodedRoute` — verified from *outside* the assembly by
`EncodedRouteTests.JsonDeserialization_CannotProduceAPopulatedInstance`, which the reverted
`System.Text.Json` converter approach left behind as a live guard against reintroducing exactly that
surface (the test project holds no `InternalsVisibleTo` grant, so this is a genuine external view, not an
in-assembly proxy for one). What is no longer true is the *count*: in-assembly construction sites are now
**two** — `PageRouteCodec.Encode` and `ChangedOnDiskIndicator.OnInitialized` — not one, and they are
asymmetric. `Encode` mints a route from a filesystem-derived path; `OnInitialized` **re-materialises** a
value `Encode` already produced elsewhere, across a boundary, without revalidating it beyond
`ArgumentException.ThrowIfNullOrEmpty` (added in the same §12 remediation this pass records, closing a
separate silent-empty-route gap the supervisor found adjacent to this one). *"The type is therefore
constructed in exactly one place, and the compiler, rather than a reviewer's enumeration, is what
guarantees it"* — the third pass's own words — no longer holds arithmetically: the guarantee has reverted
to a reviewer's enumeration of two named sites, not the compiler's enforcement of one.

**Why this is accepted rather than fixed, on the merits — a security judgement, separable from the
recording failure above.** The marker descriptor is DataProtection-protected: the string
`ChangedOnDiskIndicator.Route` receives has already been produced server-side and protected before it
ever reaches the client, so it is not client-forgeable, and an external caller has exactly the same trust
relationship with it as with any other component parameter crossing that boundary — no weaker. Threading
`EncodedRoute` itself across the boundary was tried first and reverted precisely because it required a
`System.Text.Json` converter, which *would* have widened who can construct the type (any external caller
with the JSON shape, not merely the DataProtection-protected marker payload) — the second construction
site is the narrower exposure of the two shapes considered, not an oversight. Both the block reviewer and
the §12 supervisor reached this same conclusion independently by reasoning from the type system and the
marker's provenance; neither tampered with a live marker to observe the result, so this remains one
argument made twice, not two independent checks.

**The corrected sentence.** Not *"constructed in exactly one place"* — constructed at exactly two named,
enumerable sites, one of which is a mint and one of which is a re-materialisation of a DataProtection-
protected value across a process boundary, neither reachable by an external, unprotected caller. Worth
restating what the third pass itself warned about its own sentence: *"'constructible only inside the
assembly' is the kind of sentence a later reader trusts to be total."* It was, once; §12 is the record of
it stopping being true, at a site the assembly did not yet have when the third pass was written — which is
also why no amount of re-tracing the third pass's own evidence would have caught it. See D19 §3's own
cross-reference to this pass for where a reader arriving at the broadcast design will land first.

### D18 — The Smart HTTP surface: routes, the CGI contract, the streaming host, and lock policy per verb

D3 named `git http-backend` as the mechanism; D16 built the lock it runs under; D17 built the browser
save it shares that lock with. Neither has been run. This settles the eight things §7 owes before B can
write a line of the host: the route templates and the exact `PATH_INFO` each produces, the full CGI
contract (environment in, headers out), why `GitProcessRunner` cannot host this and what the new host
must guarantee instead, the authentication scheme, the lock policy for a read verb the spec never
mentions, and three inherited obligations traced rather than carried forward unexamined. Every claim
below that is a fact about `git-http-backend` was produced by invoking the real binary — first on this
host (macOS, git 2.55.0) and then, wherever the fact could plausibly differ, inside
`mcr.microsoft.com/dotnet/aspnet:10.0` with `apt-get install git`, the exact runtime image and package
(`git 1:2.43.0-1ubuntu7.3`, Ubuntu 24.04.4) the `Dockerfile` ships. Every experiment ran against a
throwaway non-bare repository in the scratchpad, shaped like `ContentPaths` (`docs/` working tree,
`.git` at the root); nothing here touched `src/` or `tests/`.

**1 — Route shape and `PATH_INFO`: no repository-name segment, verified by a real clone and a real push
through both git versions.** `git help http-backend`'s own URL-translation section states the rule
plainly — `git-http-backend` concatenates `GIT_PROJECT_ROOT` and `PATH_INFO` and lets git's ordinary
non-bare repository discovery (the same walk any `git` command does from a working directory) find
`.git` beneath the result. There is nothing in that rule that requires a repository-name path segment;
it exists in every hosting example only because those examples serve *many* repositories under one
`GIT_PROJECT_ROOT` and need `PATH_INFO` to pick one. ZeroWiki serves exactly one, at a `GIT_PROJECT_ROOT`
that is already `ContentPaths.RepositoryRoot` and nothing else, so the segment carries no information
and is dropped. Verified, not inferred: `GIT_PROJECT_ROOT=<RepositoryRoot>` with `PATH_INFO=/info/refs`,
`/git-upload-pack` and `/git-receive-pack` — no other path component, and critically, **the CGI
subprocess's own working directory was never set to the repository either** (the bridge script below
invoked `git-http-backend` from its own directory, never `chdir`ing into the repo) — drove a real `git
clone`, a real edit-commit-push cycle exercising `updateInstead`, and a real non-fast-forward rejection,
byte-identically on both git versions:

```
$ git clone http://127.0.0.1:8791/ clone1        # macOS, git 2.55.0
Cloning into 'clone1'...
$ cat clone1/docs/page.md
hello
$ git -C clone1 log --oneline
1ec01fb init
```
```
=== [container, Ubuntu 24.04.4, git 2.43.0] Test C: real clone through python CGI bridge ===
CLONE OK
hello
=== Test D: push through bridge with Basic auth header, updateInstead ===
PUSH OK
hello
more
(clean tree)
```

The non-fast-forward case rejects identically to what `## NEXT` already recorded for `updateInstead`
directly (`! [rejected] HEAD -> main (fetch first)`), now reproduced through the actual HTTP/CGI path
rather than a bare `git push` to a local remote. **Decision: three routes, `/git/info/refs` (GET),
`/git/git-upload-pack` (POST), `/git/git-receive-pack` (POST), each stripping its own `/git` prefix to
produce `PATH_INFO`; `GIT_PROJECT_ROOT` is `ContentPaths.RepositoryRoot`, fixed, never derived from the
request.** The `/git` prefix is this decision's only free choice — it collides with nothing in
`Program.cs`'s existing top-level routes (`/`, `/account`, `/bootstrap`, `/bootstrap/complete`,
`/invitations`, `/login`, `/logout`, `/not-found`, `/invite/{Token}`, `/wiki/{*Route}`) and needs no
route-space reservation the way `/wiki/` does, since git clients address it directly rather than through
`PageRouteCodec`.

**2 — The CGI contract, each literal fed to the real binary.**

- *Export policy — `GIT_HTTP_EXPORT_ALL` set unconditionally, on every invocation, to a fixed non-empty
  string.* Verified on macOS/git 2.55.0, unchanged when independently reproduced there a second time
  during this round's remediation; not re-run in-container, on the judgment that CGI-level environment
  parsing has no version-dependent surface (see `git-http-backend.c`'s own age and stability) — stated
  explicitly here rather than left implicit. Presence, not value, is what `git-http-backend` checks:
  `GIT_HTTP_EXPORT_ALL=""` (set, empty) passes; the same call with the variable entirely unset (`env -u
  GIT_HTTP_EXPORT_ALL`) fails `Repository not exported`. The per-directory alternative — a
  `git-daemon-export-ok` marker file — was tried and rejected on a fact this design would otherwise have
  gotten wrong: for a **non-bare** repository the marker has to live *inside* `.git/`, not at the
  repository root next to `docs/`; placing it at the root (the natural first guess, and where a bare
  repository would want it) left the request 404ing with `Repository not exported` even though the file
  existed. `GIT_HTTP_EXPORT_ALL` needs no marker file anywhere and cannot be placed in the wrong
  directory, and it mirrors the precedent already set for `http.receivepack` (`ef2b75b`) — export policy
  is an unconditional environment fact set by the app on every call, not repository-resident state.
- *The full environment set*, all present on every invocation: `GIT_PROJECT_ROOT` (fixed, above),
  `GIT_HTTP_EXPORT_ALL` (fixed, above), `PATH_INFO` (from the matched route), `REQUEST_METHOD` (`GET` for
  `info/refs`, `POST` for the two service endpoints), `QUERY_STRING` (`service=git-upload-pack` on the
  `info/refs` GET; empty on the POSTs), `CONTENT_TYPE` and `CONTENT_LENGTH` (forwarded from the request
  when present — `info/refs` carries neither, and a chunked-transfer POST carries none either, below),
  `REMOTE_USER` (the authenticated account's `Username`; see §4), `HTTP_CONTENT_ENCODING` when the
  request carries `Content-Encoding` (below), and `HTTP_GIT_PROTOCOL` when the request carries
  `Git-Protocol` (a §7 remediation addition — see the new bullet below, after the CGI response header
  translation one). `PATH` is forwarded from the app's own process environment,
  unmodified — the one deliberate exception, because `git` itself is resolved through it. **`HOME` is
  never set, and no other variable is passed — enforced by clearing the subprocess's environment before
  this list (plus `PATH`) is added back, not merely by omitting `HOME` from an overlay onto the
  inherited one.** (macOS/git 2.55.0 only.)

  **Addendum — the first version of this sentence was false about what shipped, discovered by the
  supervisor's §7 section review, not by either block review (Product Owner decision, recorded here
  rather than silently corrected).** The implementation this obligation described built exactly the
  dictionary above and assigned it onto `ProcessStartInfo.Environment` key by key — but that property is
  pre-populated with a **copy of the current process's own environment** the first time it is read, so
  the assignment was an *overlay*, never a replacement. The subprocess therefore inherited the app's
  entire ambient environment, `HOME` included: measured at **108 variables** on the development machine,
  and confirmed to include `HOME=/home/app` inside the shipped `mcr.microsoft.com/dotnet/aspnet:10.0`
  image too (`docker run --user 1654 mcr.microsoft.com/dotnet/aspnet:10.0 env` — the base image itself
  sets it, not something a per-deployment config could have avoided). The doc comment restated the same
  false claim, and this design's own sentence restated it a third time — three artefacts agreeing because
  they shared one source, not because anything had measured the process. **Fix: `ProcessStartInfo
  .Environment.Clear()` before this obligation's list is applied, then `PATH` restored from the app's own
  process, unmodified.** `Clear()` alone is not safe on its own — `ProcessStartInfo("git")` resolves the
  `git` binary itself via `PATH`, so a cleared environment with nothing restored fails process startup
  outright — which is why `PATH` is the one exception. Whether anything else ambient (`LANG`, `TZ`, a
  locale or timezone variable) is genuinely required was checked by execution, not assumed: inside the
  shipped Ubuntu 24.04/git 2.43.0 image, `git http-backend` given only `PATH` plus this list, under a
  fully cleared environment (`env -i`), produced output identical in shape to the same call with the
  full ambient environment attached — for `info/refs`, and for a real push exercising the repository's
  installed no-op hooks. Nothing else is needed, and nothing else is passed.
- *`CONTENT_LENGTH` absent — the shape a chunked-transfer request produces once Kestrel dechunks it,
  since `HttpRequest.ContentLength` is `null` for one — measured rather than assumed to need special
  handling, and the measurement corrected the premise it set out to check.* A real 459-byte
  `git-receive-pack` request body (a genuine `git push`, captured off the wire) was replayed directly
  against `git-http-backend` with `CONTENT_LENGTH` unset. It completed in milliseconds — `unpack ok`, ref
  fast-forwarded — identically whether the subprocess's stdin was explicitly closed afterward or left
  open. **The premise that absence of `CONTENT_LENGTH` makes the backend block on a pipe-level EOF is
  false for a complete request**: git's own wire protocol is self-delimiting (the ref-update commands end
  in a flush-pkt; the packfile that follows carries its own object count and a trailing checksum), so
  `git-receive-pack` recognises "I have read a complete, valid request" from the protocol structure
  itself and stops reading — it does not need the pipe to signal end-of-stream. The real hazard is a
  **different** one: the same body truncated by 50 bytes (an incomplete pack, simulating a client
  connection dropped mid-upload), with stdin left open, hung — still running past an 8-second bound.
  Closing stdin at that point made no difference either way in the complete case (both closed and
  left-open variants finished sub-millisecond, see the run below), so closing it is not what makes the
  complete case fast; it is what turns the **incomplete** case from a permanent hang into a clean git-level
  failure, since only EOF tells a blocked read "no more is coming, stop waiting and fail." (macOS/git
  2.55.0.)
  ```
  [A-full-noclose] running after 3s (stdin still open, un-closed): False
  [A-full-noclose] exited 3.004s after write, returncode=0
  [C-truncated-noclose] running after 3s (stdin still open, un-closed): True
  [C-truncated-noclose] TIMED OUT waiting for exit (still hung)
  ```
  **Decision: the host closes the subprocess's stdin once it has finished copying the request body —
  successfully or not — in every invocation, regardless of whether `CONTENT_LENGTH` was set.** This is
  not what makes an ordinary request fast; it is what keeps a truncated one (a real client disconnect
  mid-push, not a hypothetical) from hanging the subprocess, and by extension the write lock it holds for
  the whole `git-receive-pack` invocation (§5), forever. It composes with §3's existing cancellation
  guarantee rather than replacing it: cancellation covers the host detecting the disconnect *during* its
  own copy (`Request.Body.CopyToAsync` observing `RequestAborted`); closing stdin unconditionally
  afterward covers the case where the copy itself returns normally but delivered less than a complete
  request.
- *The gzipped-request-body case, exercised end to end, not read off documentation.* `git-http-backend`'s
  own environment list (its man page's ENVIRONMENT section) does not mention compression at all; the
  binary's own strings do: `HTTP_CONTENT_ENCODING` alongside `inflateInit`/`inflate: %s`. Fed a
  gzip-compressed `git-upload-pack` request body with the header unset, the backend misreads the
  compressed bytes as a corrupt pkt-line stream: `fatal: protocol error: bad line length character:
  ?\x8b?` (`\x8b` is gzip's magic second byte). The identical compressed body **with**
  `HTTP_CONTENT_ENCODING=gzip` set produces a normal, valid packfile response. **Decision: the host
  forwards the request's `Content-Encoding` header verbatim as `HTTP_CONTENT_ENCODING` — never decodes it
  itself.** `git-http-backend` already does the inflation; a host that also decompressed would be
  double-decoding or racing which layer does it, for no benefit. (macOS/git 2.55.0 only — not
  independently re-run in-container; CGI environment parsing and zlib inflation are not version-dependent
  surfaces for this claim.)
- *CGI response header translation — `Status:` is optional and its absence means 200, not an omission to
  guard against.* Every invocation's stdout is a CRLF-terminated header block, a blank line, then the
  body, exactly as CGI specifies. A successful call carries **no `Status:` line at all** — only
  `Expires:`, `Pragma:`, `Cache-Control:`, and `Content-Type:` when there is a body — and every real git
  client in this section's tests treated that as 200 OK, which is the CGI default the host must
  replicate. A refusal carries an explicit line in the form `Status: 404 Not Found\r\n` (unexported
  repository) or `Status: 403 Forbidden\r\n` (receive-pack denied by git's own default policy — see §4);
  both were produced and captured verbatim. **Decision: parse the header block by splitting on `\r\n` up
  to the first blank line; a `Status:` line's leading token supplies the numeric code; its absence means
  200; every other line is forwarded as a response header unchanged; every byte after the blank line is
  the response body, copied without any decoding.** (macOS/git 2.55.0 and Ubuntu 24.04.4/git 2.43.0 —
  both quoted in obligation 1's transcript, which exercises this same header block on every request.)
- *`Git-Protocol` → `HTTP_GIT_PROTOCOL` — §7 remediation addition, found missing by the supervisor's
  §7 section review, absent from every version of this obligation until now.* This bullet's own list
  enumerated "the full environment set" and never named it — an omission, not a considered exclusion; the
  §7 thread that implemented this design never mentions protocol version at all. `git-http-backend` reads
  the CGI variable `HTTP_GIT_PROTOCOL` (the standard `HTTP_`-prefixed translation of the request header
  `Git-Protocol`, the same convention `HTTP_CONTENT_ENCODING` already uses in this same list) and
  re-exports it as `GIT_PROTOCOL` to whichever child it execs. Every git client since 2.26 sends
  `Git-Protocol: version=2` and falls back to v0 transparently when the server does not answer in kind —
  which is exactly why this omission was invisible to every test in this section: clone, fetch, and push
  all worked, just at protocol v0, which re-advertises every ref on every request where v2's `ls-refs`
  does not. Measured inside the shipped Ubuntu 24.04/git 2.43.0 image, under the corrected minimal
  environment above, same repository, same request, only `HTTP_GIT_PROTOCOL` added:
  ```
  without: 538 bytes -- full v0 ref advertisement (# service=git-upload-pack, every ref, full capability list)
  with:    319 bytes -- version 2 / ls-refs=unborn / fetch=shallow wait-for-done / object-format=sha1
  ```
  **Decision: the host forwards the request's `Git-Protocol` header verbatim as `HTTP_GIT_PROTOCOL` when
  present, and adds no variable at all when it is absent — never inventing a value for a client that did
  not ask.** `git-http-backend` already does the negotiation and the fallback; a host that invented a
  version would be answering on the client's behalf.

**3 — The streaming contract, and why `GitProcessRunner` is disqualified rather than extended.**
`GitProcessRunner.RunAsync` (`GitProcessRunner.cs:56-116`) sets `RedirectStandardOutput = true` and reads
it with `process.StandardOutput.ReadToEndAsync()` — a `TextReader`, decoding through the platform's
default encoding — and never sets `RedirectStandardInput` at all. Both are fatal here, and this section
proved rather than assumed why. A real `git clone`'s `POST /git-upload-pack` response, captured raw off
the CGI subprocess's stdout:

```
bytes: 784
UTF-8 decode FAILED as expected: 'utf-8' codec can't decode byte 0x9f in position 190: invalid start byte
contains NUL byte: True
```

(reproduced on the Ubuntu/2.43.0 image too — 435 bytes for a smaller repo, same failure mode, same NUL
byte). A packfile is not text; reading it through `ReadToEndAsync` would silently corrupt it before a
single byte reaches the client — not throw, since arbitrary bytes decode to *something* under most
encodings, just not the bytes that were sent. A push has no channel to receive its packfile at all,
since `RunAsync` never redirects standard input. **Decision, stated as a guarantee rather than an
implementation:** §7's streaming host is a distinct type, not a `GitProcessRunner` overload, and must
guarantee (a) no text decoding anywhere on either stream — the request body is copied to the
subprocess's raw stdin stream and the subprocess's raw stdout stream is copied to the response body,
both as bytes; (b) the CGI header block is peeled off that same raw byte stream (§2's blank-line rule),
never by reading a decoded string and re-encoding it; (c) cancellation kills the **whole process
tree**, which is not a new obligation but §6's already-paid one — `GitProcessRunner.cs:95-101`'s own
comment names `http-backend` explicitly as the reason a killed `git` invocation must take its children
with it, since `http-backend` forks `git-upload-pack`/`git-receive-pack`, which itself forks hooks; and
(d) the subprocess's stdin is **closed** once the request-body copy finishes, unconditionally — success
or failure — never merely left open once the last byte is written. (d) is a distinct guarantee from (c),
not a restatement of it: (c) covers the host detecting a client disconnect *during* its own copy; (d)
covers the copy returning normally having delivered an incomplete body, which §2's `CONTENT_LENGTH`
finding below measured as a genuine, unbounded hang — not a theoretical one — when stdin is left open.
The invocation carries no meaningful argument list either way it might be spawned (the resolved
`git-http-backend` binary path, as this section's experiments used, or `git` with a single
`"http-backend"` argument) — everything the process needs arrives through the environment and stdin, not
argv — which matters for obligation 9, traced below.

**4 — Authentication: HTTP Basic verified before any subprocess exists, never delegated to
`git-http-backend`'s own policy.** The credential is the per-user git token (D-series identity work),
presented as an HTTP `Basic` header's password field; `GitTokenService.VerifyAsync(username,
presentedToken)` (`GitTokenService.cs:59-76`) is the entire authorization decision — it hashes the
presented value and looks it up **only** among `GitTokens` rows matching that username, never touching
`Accounts.PasswordHash`. That is why a login password cannot authenticate here, traced rather than
asserted: there is no code path in `VerifyAsync` that reads the password hash at all, so a login password
presented as a git credential is looked up in a table it was never written into and fails identically to
any other wrong string — not by a comparison that rejects it, but by a lookup that has nowhere it could
match. A `null` result yields `401` with `WWW-Authenticate: Basic realm="ZeroWiki"` **before
`git-http-backend` is ever invoked** — no subprocess starts for a request that fails this check, for
either service. `AnonymousGate`'s own remarks already name this seam (`AnonymousGate.cs:23-24`): the
three git routes carry `[AllowAnonymous]` so the gate's cookie-authentication check doesn't swallow them.
This is deliberately **not** the same authentication `AnonymousGate` performs for the browser (a
signed-in cookie session) — a git client has no cookie to present, and D-series's own username+token
design exists because of that.

**What binds the authenticated surface to the handled surface, stated precisely because `[AllowAnonymous]`
removes both `AnonymousGate` and the `AddAuthorization` fallback policy for these three routes, leaving
nothing else in the general pipeline to answer for them.** `Program.cs:58-68`'s `FallbackPolicy` and
`AnonymousGate` both work by reading `[AllowAnonymous]` off the **matched endpoint** — there is one
exemption list, read twice, by design (`Program.cs:63-64`'s own comment: "so there is one exemption list
rather than two that can drift"). Opting the git routes out of that list removes both readings for them,
which is correct — the actual Basic-auth check has to run in their place — but it means the Basic-auth
check must not become a **second**, independently-matched list of its own, or D16's own drift concern
just moves rather than closing. **Decision: the Basic-auth check is not a separate middleware matching
requests by path string — it is an ASP.NET Core endpoint filter (`IEndpointFilter`, via
`.AddEndpointFilter()`) attached to the exact same `MapGroup("/git")` (or the individual `MapGet`/
`MapPost` calls) that defines `PATH_INFO`'s three routes.** An endpoint filter is metadata carried on the
specific `Endpoint` object routing matches — the same mechanism `[AllowAnonymous]` itself already uses,
one level up — so "did this request get Basic-auth-checked" and "did this request get handled by a git
route" are not two questions that could disagree; they are one question, answered once by routing, before
either the filter or the handler runs. Verified rather than assumed, in a throwaway minimal-API app
(`scratchpad/d18fix/routeprobe/`) with a filter and a handler both recording every path they see, attached
to the same `MapGroup("/git")`, against exactly the disagreement vectors named as the risk — trailing
slash, case, and an unrelated path under the same prefix:

```
/git/info/refs   -> 200, filter ran, handler ran
/git/info/refs/  -> 200, filter ran, handler ran   (trailing slash: ASP.NET's routing matches it — for BOTH)
/Git/Info/Refs   -> 200, filter ran, handler ran   (case-insensitive routing — for BOTH)
/git/info%2Frefs -> 404, filter did NOT run, handler did NOT run
/git/something-else -> 404, filter did NOT run, handler did NOT run
```

Every case landed on the same side for both filter and handler — never a request the filter skipped but
the handler served, or vice versa — because there is only one routing decision, not two independently
implemented ones. This is what "the same set by construction" means concretely: block B builds the Basic
-auth check as a filter on the git route registrations themselves, never as `if (path.StartsWith("/git"))`
in general middleware, and a fourth route added later inherits the same guarantee automatically by being
added to the same group rather than by remembering to update a second list.

`REMOTE_USER` is set to the authenticated account's `Username` purely so `git-receive-pack`'s reflog
carries an identifying `GIT_COMMITTER_NAME`/`GIT_COMMITTER_EMAIL` (documented behavior, `git help
http-backend`'s ENVIRONMENT section) — it plays **no role in this design's access control**, which is
worth stating because `git-http-backend` has its own, independent notion of authorization that this
design deliberately bypasses rather than relies on. That independent notion was also exercised and
confirmed live: `http.receivepack`'s documented default is "disabled for anonymous users, enabled for
users authenticated by the web server" — verified by toggling `REMOTE_USER` with `http.receivepack`
*unset*: absent, `info/refs?service=git-receive-pack` answers `Status: 403 Forbidden`; present
(`REMOTE_USER=alice`), the identical request succeeds. **This is exactly why `http.receivepack=true`
being already set unconditionally on every start (`ef2b75b`) matters**, per the brief's own framing: with
it set, `git-http-backend` offers `receive-pack` regardless of `REMOTE_USER`, so its absence would read
as an authentication bug (a 403 that looks like a rejected credential) when it is actually this
config default reasserting itself — confirmed by clearing `http.receivepack` and reproducing exactly that
403 with `REMOTE_USER` unset, then confirmed cleared again with the config restored. `git-http-backend`
therefore performs **zero** access control of its own in this design; all of it happens in the ASP.NET
pipeline, before the subprocess exists.

**5 — Lock policy per verb: `git-upload-pack` (clone/fetch) never takes the write lock; `git-receive-pack`
(push) takes it unbounded, wrapping the whole invocation (D16, already decided). This is the section's
one open question, and it is closed by evidence, not by git folklore about content-addressed
stores.** `specs/content-editing/spec.md:107-129`'s *Single per-repo write lock* requirement serializes
"all repository **writes** — browser commits and git push receipt"; a clone or fetch writes nothing on
the server side, so the requirement's own text already excludes it — the question this section owes is
whether an *unlocked* read is actually safe against a concurrent lock-held write, not whether the spec
demands locking it.

The claim under test: git's own object store (content-addressed, write-then-rename, never mutated in
place) and its ref-update protocol (each ref update is itself lockfile-protected and atomic) already give
a concurrent, completely unlocked reader a consistent, **complete** snapshot delivered to the client —
either the pre-commit or the post-commit state in full, never a torn or partial one — independently of
whether *our* `flock` is held.

*What the first pass actually measured, stated precisely because the earlier wording claimed more than
this.* A single serialized writer (matching production, where D16's lock guarantees exactly one writer is
ever active) committing 30 times in a loop, racing **eight** threads hammering `git-http-backend`'s
`info/refs` + `git-upload-pack` read path continuously and without ever touching the lock, each read
checked three ways: the CGI subprocess exited 0, the response contained the literal `PACK`, and the
advertised `HEAD` sha resolved with `git cat-file -e` **in the source repository**. That is real evidence
that the source repository was never corrupted by the race and that the reader never advertised a sha the
writer had not yet committed — but it does not inspect the bytes actually delivered, so it is not, on its
own, evidence that a client receiving that response gets a complete, valid pack rather than one truncated
mid-stream by an unlucky interleaving.

*The instrument that closes that gap, independently reproduced rather than taken on report.* Every
delivered pack piped into `git index-pack --stdin` against a fresh, isolated scratch object store,
demanding it index cleanly — a check that fails loudly on anything truncated, corrupt, or incomplete,
unlike a source-repository `cat-file -e` which says nothing about what actually left the process. Run
independently (not merely re-read from the reviewer's report) on the same host and binary, macOS/git
2.55.0:

```
verified (index-pack clean): 80 / 82 / 75   (three independent runs)
errors: 0
server-side git fsck exit: 0
```

3/3 clean, 75–82 fully-validated packs per run, zero index-pack failures. **This is the evidence that
actually supports "a consistent, complete snapshot delivered to a client"; the first pass's transcript
supports the narrower "the source repository stays intact under the race," which is necessary but not
sufficient for the sentence this section makes.**

*The second, separate gap: every writer above only ever appended.* `git commit` is append-only to the
object store, so nothing in the race above could rewrite or delete an object a reader was mid-stream on —
the operation that motivates locking reads on other git-hosting systems is **repack/prune**, which does
both, and `git commit` triggers `git gc --auto` in production (reachable, not hypothetical), even though
neither run above could have fired it — `gc --auto`'s loose-object threshold is far above 25–30 commits.
Raced again with a writer that forces the actual operation rather than waiting on a threshold that would
never trip in a short-lived test: the same eight-reader shape, now against a writer that runs `git repack
-A -d` (rewrite every pack, delete the now-redundant old ones) and `git prune --expire=now` (drop
unreachable loose objects immediately, the most aggressive prune git offers) every four commits, 40
commits total, 10 repack+prune cycles landing squarely inside the read race:

```
repack/prune events: 20   (10 repack + 10 prune, all exit 0)
verified (index-pack clean): 185 / 185 / 184   (three independent runs)
errors: 0
server-side git fsck exit: 0
```

3/3 clean, **confirmed on macOS/git 2.55.0 only — in-container confirmation on Ubuntu 24.04/git 2.43.0 is
outstanding, Docker unreachable both this round and the one before it.** That absence is not this
paragraph's finding to paper over with a borrowed transcript: obligation 1's cross-environment work
verified route resolution and clone/push cycles on both git versions, never a repack/prune race, so it
cannot vouch for this mechanism on 2.43.0 — the two are different experiments, and citing one for the
other is exactly this block's recurring defect, moved onto which run gets to stand for which claim. What
is offered instead is a *reason to expect* the result holds there, stated as an expectation and not
dressed as a measurement: an already-open file descriptor keeps its inode's content readable after the
directory entry is unlinked — the POSIX guarantee `## NEXT`'s carry-forward correction already invoked by
name for a different reason — and git's own repack/lookup code additionally re-scans the pack directory on
a failed lookup rather than trusting a cached list built once at process start, so a reader that has *not
yet* opened the file it needs would still find it in the newly-written pack rather than erroring against a
stale directory listing. Neither of those facts has been executed against git 2.43.0 in this block; both
are argument, not evidence, until it is. **Recorded as a §7 obligation owed to block C**, which already
stands up a real client against the shipped Ubuntu 24.04/git 2.43.0 image (7.3/7.4) and is where this
belongs rather than where a repack/prune race would have to be built solely to close it: C's brief should
include racing `git-upload-pack` reads against a repacking/pruning writer on that image, the same
`race_probe_gc.py` shape adapted rather than rebuilt, before this decision is treated as verified on the
environment that actually ships.

**Discharged by block C — same instrument, not a new one, on the environment that actually ships.**
Docker was reachable this round (`docker info` succeeded; the permission failure the two prior rounds hit
did not recur). `race_probe_gc.py` run unchanged from the file block A's remediation round left in the
scratchpad — checksummed before and after each invocation, `805b298d…` all three times, no edits — inside
`mcr.microsoft.com/dotnet/aspnet:10.0` with `git`/`python3` installed via `apt-get`, resolving to the exact
image, `git 2.43.0`, and Ubuntu release (`24.04.4`) the `Dockerfile` ships:

```
run 1: repack/prune events: 20   verified (index-pack clean): 119   errors: 0   server-side git fsck exit: 0
run 2: repack/prune events: 20   verified (index-pack clean): 122   errors: 0   server-side git fsck exit: 0
run 3: repack/prune events: 20   verified (index-pack clean):  98   errors: 0   server-side git fsck exit: 0
```

3/3 clean, 98–122 fully-validated packs per run, zero `index-pack` failures, `git fsck --full` exit 0 all
three times — the identical pass bar the macOS run above already reports, now met on 2.43.0 too. The
POSIX open-fd / pack-rescan reasoning is no longer standing in for a missing measurement on this
environment; it is corroborated by one. **In-container confirmation is no longer outstanding.**

**Decision, now resting on the stronger evidence, confirmed on both git versions this design ships or
develops against: `git-upload-pack` requests never acquire `RepositoryWriteLock`, safe against both a
committing writer and a repacking/pruning one.** What actually
makes this safe is git's own atomicity and open-fd guarantees for the object store and refs, not an
absence of contention — and getting this wrong in the "safe-looking" direction (locking reads too) has a
real, named cost per the brief: an unbounded lock held across a large clone would block every browser
save for the clone's entire duration, which is a strictly worse availability posture than the one this
design chooses. `git-receive-pack` continues to take the lock unbounded around the **entire**
`git-http-backend` invocation, exactly as D16 already settled — not reopened here, only confirmed as
still the right shape once a real subprocess exists to wrap.

**6 — Obligation 9 (the `GitProcessException` argument-leak concern): traced, and inert for §7.**
The obligation assumed a token-bearing value could reach a git subprocess's argument list or environment
and later surface in an exception message. Traced against this design rather than obeyed: the Basic
credential is verified entirely in C# by `GitTokenService.VerifyAsync`, a database lookup with no git
subprocess involved at all (§4); once verified, the only identity-shaped value that ever reaches the CGI
environment is `REMOTE_USER=<Username>` — a username, not a token, not a hash. `§3`'s streaming host
invokes `git-http-backend` with an empty (or single-literal, `"http-backend"`) argument list — nothing
request-derived is ever a command-line argument. There is no path by which a token, a token hash, or any
other secret becomes part of an argument list or an environment variable this design controls. **Obligation
9 does not apply to §7 as designed**; §7.5's brief (block B) needs no defensive precaution against a path
that does not exist, and no `CapturingLoggerProvider` test is needed for a leak this trace shows cannot
occur. If a future change adds a git subprocess invocation that does carry request-derived data into
`git`'s argv, that change re-opens the obligation for itself — it does not reattach to §7.

**7 — Obligation 10 (the checked-out branch, never assumed `main`): does not bite blocks A or B.**
`ContentRepositoryService.DefaultBranch = "main"` (`ContentRepositoryService.cs:26`) has exactly one call
site — `git init -b main` at first-ever repository creation (`ContentRepositoryService.cs:189`) —
confirmed by search, not inference. Nothing in this section's route templates, CGI contract, or lock
policy names a branch: `git-http-backend` resolves `info/refs`/`git-upload-pack`/`git-receive-pack`
against whatever `HEAD` the repository actually has, and `receive.denyCurrentBranch = updateInstead`
(already configured) is itself branch-name-agnostic — it means "the branch currently checked out",
resolved by git at push time, not a name this design supplies. An adopted repository on `master` is
served identically to one on `main`; nothing here would need to change. The obligation bites only in
block C, whose verification assertions must read the actual checked-out branch via `git symbolic-ref
HEAD` — an existing call site, `ContentRepositoryService.cs:661` — rather than assume `DefaultBranch`,
and that is C's brief to carry, not A's or B's.

**8 — §7 does not inherit D9's verification instrument as sound.** `## NEXT`'s carry-forward already
established, by execution, that `ContentRepositoryService`'s startup reconciliation (`git add -A` +
`git status --porcelain`, `ContentRepositoryService.cs:792-806`) is blind to a working tree an operator
has run `git update-index --assume-unchanged` against — a single stray invocation on the mounted volume,
however it got there, and D9's own recovery mechanism can no longer see the divergence it exists to
repair. That fix is escalated to its own change, `fix-reconciliation-index-blindness`, queued and out of
this change's scope (`## NEXT`), because it amends a requirement (`specs/content-store/spec.md`'s
*Working-tree-clean invariant*) that exists only on this branch and cannot land before this change
archives. What it means specifically for §7's push receipt, also already measured rather than assumed: a
`updateInstead` fast-forward push against a blinded-dirty tree is **refused**, not silently overwritten —
`error: Entry '<file>' not uptodate. Cannot merge.` / `! [remote rejected] main -> main` — with the
uncommitted local divergence surviving untouched. A blinded tree therefore produces a permanently-
bouncing remote and a health check that reports clean while every push fails, which is an availability
and diagnosis defect, not a data-loss one. §7 must not paper over this by, for instance, having its own
push path re-verify cleanliness with the same blind instrument — it inherits the existing
`AssertWorkingTreeIsCleanAsync` machinery as-is and carries the same blind spot forward, explicitly,
rather than silently.

**The `safe.directory` standing hazard named in obligation 4's environment list — discharged, but the
reasoning below was inverted until the §7 remediation block corrected it, and is rewritten here rather
than left standing (Product Owner decision).** `Dockerfile:54` runs `git config --system --add
safe.directory '*'` as root before the `USER` switch. The paragraph originally here reproduced the
container experiment with `HOME` unset and called that "the exact shape a CGI subprocess runs in" — but
at the time it was written, and for the whole of block B, that was **not** the shape the code produced:
§2's overlay defect meant the subprocess inherited the app's own `HOME`, so a per-user `--global` setting
would in fact have applied there, the opposite of what this paragraph concluded from. The experiment
itself was a legitimate test of the case that matters; it was just being cited for code that did not yet
match it.

**After the §7 remediation fix, this experiment describes the running code correctly, not
prospectively.** §2's environment is now genuinely cleared before `PATH` and its own list are added back,
so the CGI subprocess has no `HOME` for a `--global` setting to write into or read from — `Dockerfile:54`
running at *system* scope is what makes ownership verification work at all, rather than being a defence
that happened not to be exercised by what shipped. Reproduced inside the Ubuntu 24.04.4/git 2.43.0 image:
a repository owned by `appuser`, `git-http-backend` invoked as `root` with `HOME` unset (euid/owning-uid
mismatch, `HOME` absent) fails without the config line —

```
fatal: detected dubious ownership in repository at '/data/wiki/.git'
Status: 500 Internal Server Error
```

— and succeeds cleanly, still with `HOME` unset, once `git config --system --add safe.directory '*'`
is applied. **The Dockerfile decision itself was already correct and needs no change** — system scope was
always the safer choice regardless of what the CGI subprocess's environment happened to contain, and
`Dockerfile:23-29`'s own header comment already describes the environment this fix produces (a
`--global` setting silently not applying), so it stands unmodified rather than being corrected to match
something it already said correctly. Only this design document's reasoning was inverted, not the
Dockerfile's, and not the decision the Dockerfile records.

**Spec delta: none needed, checked requirement by requirement rather than assumed.**
`specs/git-sync/spec.md` already states the Smart HTTP surface, `updateInstead`, non-fast-forward
rejection, re-index-and-broadcast, Obsidian compatibility, and git-identity mapping as SHALL-level
requirements with scenarios; none of them name a route template, an environment variable, or a header —
those are implementation literals that belong in this design document, not the spec, and every fact
this section settled is consistent with what the spec already requires rather than in tension with it.
`specs/content-editing/spec.md:107-129`'s *Single per-repo write lock* requirement already scopes itself
to "repository writes", which is the textual basis for obligation 5's decision that `git-upload-pack`
needs no lock at all — the requirement was never claiming otherwise, and nothing here weakens or extends
what it demands of a push's unbounded wait. No requirement changes; none added.

### D19 — Push reactions: trigger point, changed-file determination, broadcast transport, and composition with §4

**Product Owner decision, DEVLOG §8:** a completed push reaches the app in-process. §7.5 already made the
app the parent of the whole `git http-backend` invocation (`GitSmartHttpEndpoints.HandleReceivePackAsync`),
holding `RepositoryWriteLock` around it — there is no `post-receive` signalling path, and none will be
built. `tasks.md` 8.1/8.2 named the hook; `specs/git-sync/spec.md`'s *Re-index and broadcast on received
push* does not — "after a push updates the working tree, re-index the changed files and broadcast" is
mechanism-neutral, and this section supplies the mechanism the spec actually asks for. Every literal below
was fed to the real `git-receive-pack` binary (macOS, git 2.55.0) against a throwaway non-bare repository
in the scratchpad, shaped like `ContentPaths` (`docs/` working tree, `.git` at the root, `receive.deny
CurrentBranch=updateInstead`) — the same methodology D18 used, at the same layer D18 already proved the
CGI path behaves identically to (obligation 1's clone/push transcripts).

**1 — Trigger point: split across the lock boundary, not uniformly inside or outside it.**
`HandleReceivePackAsync` already acquires `writeLock` before calling `InvokeGitHttpBackendAsync` and
disposes it in a `finally`. The **capture** of "what HEAD was" happens twice inside that boundary — once
immediately after the lock is acquired, before the backend runs, and once immediately after
`InvokeGitHttpBackendAsync` returns, still inside the `try` — each a single bounded `git rev-parse HEAD`
subprocess. The **reaction** — diffing the captured shas for changed paths, refreshing the index,
broadcasting — runs *after* `writeLock.Dispose()`, taking the captured `(before, after)` pair as fixed
values rather than re-reading "current `HEAD`" at reaction time.

*Why split rather than putting the whole thing on one side.* Fully inside: the reaction's slowest parts —
walking a diff, re-reading frontmatter for however many files changed, fanning a broadcast out to however
many connected circuits — would run while every subsequent writer sits in D16's already-unbounded queue
(`specs/content-editing/spec.md`'s *Push's wait for the lock has no ceiling*), stacking unbounded work
behind an already-unbounded wait for no benefit the spec asks for. Fully outside: capturing "before" `HEAD`
without the lock held risks racing a second writer between the read and the actual push landing, which
would attribute another writer's change to this push's diff. Splitting keeps the only work that must be
serialized (the two cheap `rev-parse` calls, which must bracket *this* push's invocation and no one else's)
inside the lock, and defers everything unbounded to after release. **Consequence for the spec's own
scenario:** a second push (or a browser save) can start, and finish, before push *N*'s own reaction
executes — accepted, and not a correctness hazard, because the reaction closes over two already-computed,
immutable shas rather than "current `HEAD`"; push *N*'s diff and broadcast describe exactly what push *N*
changed regardless of what lands after it. The only observable effect is ordering: push *N*'s "changed on
disk" signal can reach viewers slightly behind push *N*+1 having already landed — addressed in §4 below,
because a signal is a nudge to refresh, not a claim about which commit a viewer will see.

**Decision: capture inside the lock (bounded, cheap, must not race a concurrent writer); react outside it
(unbounded, must not lengthen another writer's already-unbounded wait).**

**2 — What "the changed files" means, established by execution rather than assumed, because the app never
receives a ref/old-sha/new-sha triple the way a `post-receive` hook would.** A wrapper standing in for the
trigger point above — `git rev-parse HEAD` before invoking `git-receive-pack`, the invocation, `git
rev-parse HEAD` after — was run against three real pushes:

```
=== successful fast-forward push ===
TRIGGER: BEFORE=a933401c68227b2e4f8a63373786bf9350a3ed58 AFTER=6a6edcae268358a0d15a7f3babf0ba7a0b51733a RC=0
TRIGGER: HEAD moved -- changed files:
docs/page.md

=== non-fast-forward push (rejected) ===
TRIGGER: BEFORE=4fe8f4096fb65ed6cef9a6bb95ea65cca710da5c AFTER=4fe8f4096fb65ed6cef9a6bb95ea65cca710da5c RC=0
TRIGGER: HEAD unchanged -- no reaction
 ! [rejected]        main -> main (fetch first)

=== push with nothing new to send ===
TRIGGER: BEFORE=4fe8f4096fb65ed6cef9a6bb95ea65cca710da5c AFTER=4fe8f4096fb65ed6cef9a6bb95ea65cca710da5c RC=0
TRIGGER: HEAD unchanged -- no reaction
Everything up-to-date
```

Two findings, neither assumed beforehand. First: **`git-receive-pack` exits `0` in the rejected case** —
the ref *update* was refused, not the subprocess — so an exit-code check would have wrongly reacted to a
push that changed nothing on disk; this is the same caution D17 already states about trusting a git exit
code, now confirmed for this subprocess too. `before == after` is what correctly reports "nothing to react
to," not the return code. Second: **a push with no new commits to send still invoked the wrapper** (an
empty ref-update command set, exit `0`, `HEAD` unchanged) under this test's transport — whether every real
Smart-HTTP client's `git push` always issues the `POST /git-receive-pack` in this case, or short-circuits
client-side after the `info/refs` negotiation and never sends it at all, was not independently reproduced
here, and it does not need to be: **either way is already safe under this design.** Never invoked means
`HandleReceivePackAsync`'s own trigger never runs at all; invoked-with-nothing-to-do reports `before ==
after` and reacts to nothing, by the same rule as the rejected case above.

**Decision: the app reconstructs the equivalent of a `post-receive` ref-pair for itself by bracketing its
own `git-receive-pack` invocation with `git rev-parse HEAD`, and treats `before == after` as "nothing
changed" — never the subprocess exit code, and never an assumption about whether a no-op push even reaches
the route.** `before != after`, the ordinary case, feeds `git diff --name-only <before> <after>` — the
same shape D15's own incremental refresh already uses — to name exactly the changed paths. A failed,
rejected, or no-op push triggers nothing, verified rather than assumed.

**3 — Broadcast transport: the circuit D7 already named, not a second SignalR connection this design
stands up alongside it.** D7 named the "changed on disk" indicator as the first component to opt into
`InteractiveServer` — that render-mode flip *is* the SignalR circuit (the framework's own `/_blazor` hub,
wired by `AddInteractiveServerComponents`/`.AddInteractiveServerRenderMode()`), not a bespoke `Hub`
subclass this section adds. **Decision:** the publish/subscribe layer riding on that circuit is an
in-process singleton keyed by page route — a component registers a callback under its own route when it
activates (`OnInitialized`/`OnAfterRenderAsync`) and disposes the registration when its circuit ends; §2's
reaction looks up only the routes named by its diff and invokes exactly those callbacks. A viewer on a
route the push never touched has no callback registered under any of the changed routes, so it is **never
invoked at all** — not told and then filtering client-side, simply never addressed — which is what answers
"must not receive": nothing crosses that viewer's circuit, because the reaction never reaches for it.

**The route this callback registers under crosses the same Static SSR→circuit boundary D7 named — see
D17's fourth pass for what that crossing costs.** `ChangedOnDiskIndicator` is the component D7 named as
the first `InteractiveServer` island; the route it subscribes under is not assigned inside the circuit,
it is re-materialised from a plain `string` parameter the SSR-rendered call site passed across exactly
this boundary. D17's fourth pass records why that is a second `EncodedRoute` construction site, what
guarantee is and is not lost by it, and why it is accepted rather than fixed.

**4 — Composition with §4/D15: 8.1 is a freshness win, not a correctness one, stated plainly rather than
assumed in its favour.** D15 already stamps the in-memory index with the `HEAD` it was built from and
refreshes it — incrementally, via the identical `git diff --name-only` shape §2 verified above — whenever
a page-serving request notices the stamp is stale, "covering every writer identically … *including writers
that never notify the app*" (D15's own words), which is exactly what a push is if §8.1 did nothing at all.
`specs/content-store/spec.md`'s *Content changed by an unannounced writer is still reflected* scenario
already demands this and, per D15, already delivers it — for a push exactly as for a manual commit on the
volume — with zero help from §8. **If 8.1's eager re-index did literally nothing: correctness is
unaffected.** The next page view of any affected page, by anyone, still triggers D15's stamp check,
notices `HEAD` moved, and refreshes before serving. What 8.1's re-index half actually buys is narrower and
purely a latency property: the first viewer to reload after a push does not pay the incremental-refresh
cost synchronously on their own request, because it already happened at push time. **What 8.1 is not
optional for** is different: 8.2 needs to know *which* routes changed in order to notify only their
viewers (§3's "must not receive"), and D15's lazy stamp check never computes that — it only ever asks "has
`HEAD` moved," never "which paths moved." §2's diff is that missing piece, and it is required
infrastructure for 8.2 regardless of whether the index-refresh half of 8.1 runs eagerly or is left to
D15's own lazy path. **Decision, stated as D18 §5 states its own evidence rather than its assumed value:**
correctness never depended on 8.1 running eagerly and does not start depending on it here; what 8.1 buys is
the changed-route set 8.2 needs, plus a warmed index for whoever reloads first.

**5 — 8.3's resolver ordering: settled here, implemented in block C.** `AccountGitAuthorFactory` (already
shipped, D10/§6) constructs the *outbound* synthetic address in one of two shapes per account: the raw
`Username` as the localpart when it is itself a legal RFC 5322 `dot-atom-text`, or the deterministic
fallback `account+<accountId:N>` when it is not (`AccountGitAuthorFactory.cs:34-60`). D10's *Consequence
binding §8.3* requires the *inbound* resolver to match the synthetic form first, ahead of registered
`GitEmails` rows, so a member squatting another member's address in `/account` can never capture their
attribution. Because the outbound side has two shapes, the inbound resolver must test both before ever
consulting `GitEmails`: match `account+<id>@<domain>` against a live account's `Id` directly; separately,
match `<localpart>@<domain>` against a live account's `Username` when that username is itself a legal
dot-atom (the same legality test `AccountGitAuthorFactory` already applies outbound, so the two directions
cannot drift apart). Either match resolves synthetically and short-circuits before `GitEmails` is
consulted at all. Only an address matching neither synthetic shape falls through to a `GitEmails` lookup,
and only failing that does it fall back to the raw pushed identity (spec: *Unknown git email is attributed
to the raw identity*). **Decision: synthetic-first (both shapes), `GitEmails` second, raw identity last —
`GitEmailService` and the `GitEmails` table need no schema or behaviour change to support it, as D10
already noted.** Block C implements the resolver and its tests; nothing here is new code.

**Spec delta: none.** `specs/git-sync/spec.md`'s *Re-index and broadcast on received push* requirement and
its *Viewers notified after a push* scenario are already mechanism-neutral and are satisfied by the
mechanism this section settles; no scenario changes.

## Risks / Trade-offs

- **Dirty tree blocks all pushes** → Transactional save (`git checkout -- <file>` on commit failure, under lock) plus startup reconciliation (commit-as-recovered or discard) guarantee the tree returns to clean.
- **Push lands between write and commit** → D3's shared `flock` makes browser save and push mutually exclusive.
- **Commit-per-keystroke history explosion** → Debounce/coalesce to save-points; exactly one commit per save-point. No `--amend` after a commit could have been fetched, because rewriting published history breaks sync.
- **Self-asserted push identity** → Accepted for a trusted invite-only group; email→account mapping attributes correctly, unknown emails fall back to the raw identity without failing the push.
- **CGI subprocess resource/latency** → Small user base; acceptable. Bound concurrency with the same lock and normal request limits.
- **Serialized writes are a ceiling** → Known and accepted for this scale; revisited only if multi-vault or high-concurrency ever arrives.

## Migration Plan

This is greenfield; there is no data to migrate. Deployment: ship a container image with `git` installed; on first start against an empty volume, `git init` a non-bare repo, create `docs/`, set `receive.denyCurrentBranch=updateInstead`, install the `pre-receive`/`post-receive` hooks, and make an initial commit. Rollback is to redeploy the previous image; the volume (git repo) is untouched and remains the source of truth.

## Open Questions

- ~~Credential form for the git remote~~ **Resolved**: per-user revocable git access tokens (Basic-auth password), decided in the `invite-only-authentication` change; the login password is rejected for git.
- ~~Startup reconciliation policy for a dirty tree: commit-as-recovered vs discard~~ **Resolved (D9)**: always commit-as-recovered, authored `System <system@zerowiki.org>`, with no policy switch.
- ~~Attachments/binary assets (pasted images)~~ **Resolved**: binary assets live in the repository alongside the Markdown and ride the same commit-on-save path and the same write lock. Page enumeration and the derived index stay Markdown-only; `git http-backend` serves every blob regardless of type. **No work in this change** — there is no editor UX here, so the upload path arrives with the editor change that needs it.
- ~~Which git email stamps a browser save, and what stamps one for an account with none~~ **Resolved (D10)**: neither — authorship is a synthetic per-account identity.
