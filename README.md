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
serves exactly one repository.) The pages themselves live in the `docs/` subdirectory of that
repository, so an Obsidian vault should be opened at `<clone>/docs`, not at the clone's root.

### Getting a credential

Git does not accept your sign-in password. Sign in, go to **Account**, and generate a git access
token — it's shown once, so copy it immediately. Your git username is the same username you sign
in with. Revoking a token (also on the Account page) does not touch your sign-in password or
anything else about the account.

### Cloning a fresh vault

```sh
git clone https://<username>@<your-host>/git zerowiki
```

Git will prompt for the password; paste the access token there. (Embedding the token directly in
the URL, `https://<username>:<token>@<your-host>/git`, also works but leaves the token sitting in
your shell history and in `.git/config` — prefer letting git prompt, or use a credential helper.)
Open `zerowiki/docs` as the vault in Obsidian.

### Pointing an existing vault at ZeroWiki

If you already keep a vault under git, add ZeroWiki as a remote and pull:

```sh
cd /path/to/your/vault
git remote add origin https://<username>@<your-host>/git
git pull origin HEAD --allow-unrelated-histories
```

The vault's own folder must be the repository's `docs/` directory — either move the vault's
contents there first, or clone fresh (above) and copy your notes in.

### Configuring the `obsidian-git` plugin

Install the community plugin **obsidian-git**, then in its settings:

- Leave **Vault backup interval (minutes)**, **Auto pull/push** etc. at whatever cadence you
  want — the plugin talks to the remote exactly like the `git` CLI above, over the same Smart
  HTTP endpoint with the same basic-auth credential.
- The first pull or push will ask for the git username and access token from the previous
  section. Most git credential helpers (macOS Keychain, Windows Credential Manager) will offer
  to remember it after the first prompt.
- No other setting is ZeroWiki-specific — `obsidian-git` speaks plain Smart HTTP, which is all
  this remote is.

### When a push is rejected

The server accepts a push only if it fast-forwards the branch it has checked out; if the vault's
history and the wiki's history have both moved on since the vault last pulled, the push is
**rejected** outright. The server does not attempt to merge or resolve anything — that is
deliberate, so it never guesses at intent. Resolve it the same way you would with any other git
remote: pull (Obsidian's "Pull" command, or `git pull`) to merge the remote changes into the
vault locally, resolve any conflicting lines if Obsidian or git can't merge them automatically,
then push again.
