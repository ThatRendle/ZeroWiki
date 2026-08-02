# ZeroWiki container image.
#
# Data volume (D8, openspec/changes/git-backed-content-core/design.md): mount exactly one volume
# at /data. It holds:
#   /data/identity.db   — the SQLite identity store (accounts, invitations, git tokens).
#   /data/wiki           — the content git repository root; .git lives at /data/wiki/.git and the
#                           rendered working tree at /data/wiki/docs/*.md.
# Both paths are configuration (ConnectionStrings:IdentityDb and ContentStorage:DataRoot below),
# defaulting to /data so the container needs no other configuration to run:
#
#   docker run -v zerowiki:/data -p 8080:8080 zerowiki
#
# git is required in THIS (runtime) stage, not just the build stage: the app shells out to `git`
# and `git http-backend` at request time to serve the Smart HTTP remote (design D2) — it is not a
# build-time-only tool here.
#
# Runs as the pre-created non-root `app` user ($APP_UID, baked into the base image) rather than
# root. /data is chowned to that user at build time so that when Docker populates a fresh named
# volume from the image's /data directory on first mount, the volume comes up already owned by the
# uid the app runs as — no root step or entrypoint chown is needed. `safe.directory` is set
# globally for that user as defense in depth against git's "detected dubious ownership" refusal,
# which triggers whenever a repository's owning uid does not match the process euid (e.g. a volume
# populated by another means, or restored from a backup taken as a different user).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Only the app project is needed to run the container — restore/copy it in isolation (skipping
# the test project and solution file) so dependency layers cache independently of test changes.
COPY src/ZeroWiki/ZeroWiki.csproj src/ZeroWiki/
RUN dotnet restore src/ZeroWiki/ZeroWiki.csproj

COPY src/ZeroWiki/ src/ZeroWiki/
RUN dotnet publish src/ZeroWiki/ZeroWiki.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /data \
    && chown -R $APP_UID:$APP_UID /data

USER $APP_UID

RUN git config --global --add safe.directory '*'

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .

ENV ConnectionStrings__IdentityDb="Data Source=/data/identity.db" \
    ContentStorage__DataRoot="/data"

VOLUME ["/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "ZeroWiki.dll"]
