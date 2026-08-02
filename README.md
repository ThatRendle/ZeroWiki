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
