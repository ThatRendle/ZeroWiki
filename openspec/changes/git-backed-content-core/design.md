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
and its binding is something block D3 must **run**, not assume; this change's standing rule about
tracing a premise applies to the correction as much as to what it corrected.

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
