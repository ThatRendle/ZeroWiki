# ZeroWiki

ZeroWiki is a zero-config, invite-only Markdown wiki: point it at a folder and it Just Works.
Content lives in a git repository that is the source of truth, edited from a browser and synced to
an Obsidian vault over a Smart HTTP git remote. Identity is invite-only with per-user git access
tokens, backed by SQLite.

## Running in Docker

```sh
docker build -t zerowiki .
docker run -d --name zerowiki -v zerowiki:/data -p 8080:8080 zerowiki
```

That's the entire deployment story — one image, one named volume, nothing else to configure.

### The data volume

Mount exactly one volume at `/data`. It holds:

- **`/data/identity.db`** — the SQLite identity store (accounts, invitations, git tokens). Never
  part of the content repository.
- **`/data/wiki`** — the content git repository root. `.git` lives at `/data/wiki/.git`; the
  rendered working tree is `/data/wiki/docs/*.md`.
- **`/data/keys`** — the DataProtection key ring that backs the authentication cookie. It lives
  beside the repository rather than inside it, and it must stay there: keys under `/data/wiki`
  would be committed as content and pushed to every vault that clones the remote. Losing this
  directory signs everyone out; it does not lose any content.

On first run against an empty **named** volume, Docker populates `/data` from the image, which is
created there already owned by the container's non-root user — no manual chown or init step is
needed. This is the `-v zerowiki:/data` form above, and it is the recommended one.

**A bind mount is different, and needs one step.** With `-v /some/host/path:/data`, Docker does
*not* seed the directory from the image — the host directory keeps its own ownership, and a freshly
created one is typically owned by root with mode `0755`. The container runs as uid **1654**, so
every write to the repository fails. Chown the host directory before the first run:

```sh
mkdir -p /some/host/path && sudo chown -R 1654:1654 /some/host/path
docker run -d --name zerowiki -v /some/host/path:/data -p 8080:8080 zerowiki
```

Git's "detected dubious ownership" refusal — which fires when a repository's owning uid differs
from the process uid — is already disarmed inside the image via `safe.directory`, so ownership only
has to be right for the filesystem's sake, not for git's.

### Configuration

Both volume paths are configuration, so they can be pointed elsewhere without a different image or
code path — this is how local development points at a gitignored `App_Data` folder instead of
`/data`:

| Setting | Environment variable | Default |
|---|---|---|
| Identity store connection string | `ConnectionStrings__IdentityDb` | `Data Source=/data/identity.db` |
| Content volume root | `ContentStorage__DataRoot` | `/data` |

The image sets both to their `/data` defaults already; only override them if you're relocating the
mount.

## Syncing with Obsidian

The content repository is a real git remote, reachable over Smart HTTP at:

```
https://<your-host>/git
```

(`/info/refs`, `/git-upload-pack`, `/git-receive-pack` — no repository-name segment; ZeroWiki
serves exactly one repository.)

### Vault layout

Open **`<clone>`** — the clone's root — as the vault, with the notes living in its `docs/`
subfolder. This layout has been tested end to end: both sync directions, a rejected push, and its
conflict resolution all work here. It also keeps `.obsidian/` (below) outside the `docs/` folder
that becomes wiki content.

Opening **`<clone>/docs`** directly as the vault also works. Only activation has been confirmed
here — the plugin's sidebar icon and a `main` branch indicator both appear — not the full round
trip. It also puts `.obsidian/` (below) *inside* `docs/`, mixed in with the wiki's own content,
rather than beside it.

### Getting a credential

Git does not accept your sign-in password. Sign in, go to **Account**, and generate a git access
token — it's shown once, so copy it immediately. Your git username is the same username you sign
in with. Revoking a token (also on the Account page) does not touch your sign-in password or
anything else about the account.

### Authenticating from the command line first

**Verified against a running ZeroWiki on macOS only.** The Windows and Linux instructions below
are carried over from [`obsidian-git`'s own authentication
guide](https://github.com/Vinzent03/obsidian-git/blob/master/docs/Authentication.md) — they
haven't been exercised against this app, so treat them as a starting point, not a tested recipe.

**macOS.** The plugin's macOS docs describe storing the credential with the OS keychain helper,
primed by one authentication action from a terminal, before using the plugin:

```sh
git config --global credential.helper osxkeychain
```

or confirm one is already configured with `git config --show-origin --get-all credential.helper`
— use `--show-origin` and no scope flag, not `--global` alone: a helper installed by Homebrew's
git is commonly set at **system** scope and prints nothing under `--global` even though it's
active. Then run one clone, pull, or push from a terminal before opening Obsidian — the clone
below is enough — and enter the username and access token when it prompts.

**Windows (unverified here).** Confirm Git Credential Manager is enabled: `git config
credential.helper` should print `manager`; if not, `git config set credential.helper manager`.

**Linux (unverified here).** Store the credential with `libsecret`: `git config --global
credential.helper libsecret` (install `libsecret` first if it isn't already), then one
authentication action from a terminal before opening Obsidian.

### Cloning a fresh vault

```sh
git clone https://<username>@<your-host>/git zerowiki
```

This clone doubles as the terminal authentication above — git prompts for the password; paste the
access token there. On macOS, do this before opening Obsidian. For Windows and Linux, see
"Authenticating from the command line first" above.
(Embedding the token directly in the URL, `https://<username>:<token>@<your-host>/git`, also works
but leaves the token sitting in your shell history and in `.git/config` — the prompt is what lets
the credential helper store it instead.)

Open `zerowiki` as the vault in Obsidian (see "Vault layout" above).

### Pointing an existing vault at ZeroWiki

Clone the repository as in "Cloning a fresh vault" above, then copy your existing notes into the
clone's `docs/` folder before opening the clone's root as the vault.

### Configuring the `obsidian-git` plugin

Install the community plugin **obsidian-git**. In its settings:

- **Auto commit-and-sync interval (minutes)** — `0` (the default) disables it. Leave it at `0`
  unless you want edits synced on a timer rather than by your own action.
- **Auto pull interval (minutes)** — the same idea for pulling; `0` disables it.
- **Pull on commit-and-sync** — turn this **on**. Without it, "commit-and-sync" only commits and
  pushes, so a push this repository rejects (below) has no automatic way back.
- **Merge strategy** — how your local branch is updated when a pull brings in commits from the
  wiki; "Merge" is the safe default.
- **Merge strategy on conflicts** — set this to **"None (git default)"**. The other options
  resolve a genuine conflict automatically, in favour of one side or the other, without showing it
  to you. "None" is what makes a real conflict visible in Obsidian so you resolve it yourself,
  instead of one side's edit being silently discarded.

### Obsidian's own files end up in the repository

Obsidian writes a `.obsidian/` folder into whichever directory you open as the vault. A repository
ZeroWiki initializes is seeded with a `.gitignore` rule for `.obsidian/`, so a vault's editor
configuration — `workspace.json`, plugin settings, and the rest — never becomes wiki content, even
if a hand-written `.gitignore` was already on the volume: its rules survive, and the `.obsidian/`
line is only added once. Page rendering also skips any dot-prefixed entry, so `.obsidian/` is never
served as a page even where it is tracked.

A repository you hand ZeroWiki instead — one that already has commit history — is left exactly as
it is: if `.obsidian/` is already tracked, it stays tracked, and its `.gitignore`, if it has one, is
not touched at all.

### When a push is rejected

The server accepts a push only if it fast-forwards the branch it has checked out; if the vault's
history and the wiki's history have both moved on since the vault last pulled, the push is
**rejected** outright. The server does not attempt to merge or resolve anything — that is
deliberate, so it never guesses at intent. With **Pull on commit-and-sync** turned on (above), a
rejected commit-and-sync pulls automatically and merges, so you can commit-and-sync again; if
**Merge strategy on conflicts** is left on anything other than "None", that merge resolves a real
conflict for you without asking, rather than showing it to you. Resolve a genuine conflict the
same way you would with any other git remote: pull (Obsidian's "Pull" command, or `git pull`) to
merge the remote changes into the vault locally, fix any conflicting lines Obsidian or git
couldn't merge automatically, then push (or commit-and-sync) again.
