# Configuration

Everything configures through environment variables in `Gatherum__Section__Key` form.
This page is the reference for an operator; nothing here is needed to *use* Gatherum.

## Storage and database

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Database__ConnectionString` | `Host=localhost;Database=gatherum;Username=gatherum;Password=gatherum` | Npgsql connection string |
| `Gatherum__Database__Migrate` | `true` | Apply migrations at startup |
| `Gatherum__Storage__Root` | `data/files` (`/data/files` in the container) | The system of record |
| `Gatherum__Storage__ReindexOnStartup` | `true` | Reconcile the index against the directories at startup |

PostgreSQL 16 or newer with the [pgvector](https://github.com/pgvector/pgvector)
extension. The migration issues `CREATE EXTENSION vector`, which wants a superuser the
first time.

## Identity

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Oidc__Authority` | *(empty)* | Issuer URL; empty enables the development auto-login |
| `Gatherum__Oidc__ClientId` | *(empty)* | Client id |
| `Gatherum__Oidc__ClientSecret` | *(empty)* | Client secret |
| `Gatherum__Oidc__Scopes` | `openid profile email` | Requested scopes |
| `Gatherum__Oidc__RequestOfflineAccess` | `false` | Additionally request `offline_access` |

With no authority configured the app signs anyone in as a local development user. That
is fine on a laptop and an open door anywhere else, so **outside
`ASPNETCORE_ENVIRONMENT=Development` the app refuses to start** rather than be one.

## Bookmarks

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Bookmarks__BrowserPath` | *(empty)* | Chromium executable that renders a bookmarked page before capture. Empty looks in the usual Playwright locations; the container image sets it to its own browser |
| `Gatherum__Bookmarks__BlockAds` | `true` | Keep ads and trackers out of captures. Their hosts are refused before a rendered page's scripts run and stripped from the snapshot either way; off, a capture keeps the page as served |
| `Gatherum__Bookmarks__AdHostsUrl` | *(StevenBlack hosts)* | Where the blocklist comes from — any hosts-file, bare-domain, or `\|\|host^` list works, so it can point at Peter Lowe's, OISD, or the AdGuard DNS filter instead. Empty blocks with the small packaged list alone |

With a browser, a bookmark captures the page as it stands once its scripts have run and
settled. Without one — none installed and nothing configured — the capture is what the
server serves to a plain fetch, so a bare `dotnet run` still bookmarks, only without
rendering. A browser that fails to load a page degrades to the plain fetch too, with a
warning in the log.

Ad blocking works against a community-maintained blocklist, fetched just in time: the
first capture of the day pays for the download, every later one reuses it, and nothing
is ever fetched on a schedule — the list moves only when a capture somebody asked for
wants it. A small packaged list of the networks that matter is unioned in as the floor,
and is what a capture blocks with when the fetch fails or the instance is offline. A
page that itself lives on a listed host is exempt from its own entries, so bookmarking
such a site still captures it whole.

## Publishing

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Sharing__AllowPublic` | `true` | Serve nodes marked public at all |
| `Gatherum__Sharing__AnonymousReadsPerMinute` | `120` | Read budget per client address |
| `Gatherum__Sharing__AnonymousSearchesPerMinute` | `10` | Search budget per client address |

Signed-in callers are never metered. The budgets are per client address, so behind a
reverse proxy they depend on `X-Forwarded-For` reaching the app: the container image sets
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` and trusts it from any peer, and what keeps a
client from spoofing it is that only the proxy can reach the port. Publish the port more
widely and you need to revisit both.

This manual is served without credentials and under the read budget. It is identical in
every install and describes the software rather than what is in it.

## Multimedia analysis

Off unless an endpoint is configured. Point it at a model you run — llama.cpp's server,
or anything speaking the OpenAI API — and uploads that carry no text of their own get
some.

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Analysis__Endpoint` | *(empty)* | Base URL of an OpenAI-compatible API |
| `Gatherum__Analysis__Model` | *(empty)* | Reads images, writes summaries |
| `Gatherum__Analysis__AudioModel` | *(falls back to `Model`)* | Transcribes speech |
| `Gatherum__Analysis__ApiKey` | *(empty)* | Bearer token, if the runner wants one |
| `Gatherum__Analysis__BackfillExisting` | `true` | Queue media uploaded before analysis was configured |
| `Gatherum__Analysis__VideoFrames` | `4` | Frames sampled across a video |
| `Gatherum__Analysis__MaxBytes` | `268435456` | Largest file sent for analysis |
| `Gatherum__Analysis__TimeoutSeconds` | `900` | Ceiling on one analysis call |
| `Gatherum__Analysis__FfmpegPath` | `ffmpeg` | How to invoke ffmpeg |

Nothing is ever sent anywhere without an endpoint you configured.

## Embedding

Semantic search works with no configuration: a 23 MB MiniLM ships with the app and runs
in-process on the CPU. These override it.

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Embedding__Endpoint` | *(empty)* | An OpenAI-compatible embeddings API; replaces the packaged model |
| `Gatherum__Embedding__Model` | *(empty)* | The model at that endpoint |
| `Gatherum__Embedding__Local` | `true` | Use the packaged model when no endpoint is set |
| `Gatherum__Embedding__ModelPath` | `models/all-MiniLM-L6-v2` | Where the packaged model lives |
| `Gatherum__Embedding__Dimensions` | `384` | Vector width; startup resizes the column to match |
| `Gatherum__Embedding__ApiKey` | *(empty)* | Bearer token, if the runner wants one |
| `Gatherum__Embedding__MaxDistance` | `0.8` | How far apart two texts can be and still count as an answer |
| `Gatherum__Embedding__MaxChunkChars` | `800` | Longest passage handed to the model |
| `Gatherum__Embedding__MaxChunksPerNode` | `200` | Ceiling on passages per node |
| `Gatherum__Embedding__BatchSize` | `16` | Passages per request |
| `Gatherum__Embedding__SweepSeconds` | `15` | How often the worker looks for changed text |
| `Gatherum__Embedding__QueryTimeoutMs` | `2000` | Ceiling on embedding a query before answering from full text alone |
| `Gatherum__Embedding__TimeoutSeconds` | `120` | Ceiling on one background batch |

`Local=false` with no endpoint leaves search full-text only. Changing `Dimensions` is the
only thing needed to switch to a model of a different width: startup resizes the column,
drops the old vectors, and the worker earns them back.

## How your files are stored

The directory behind `Gatherum__Storage__Root` **is** the knowledge base:

```
{storage root}/
  {username}/                  one directory per user, named for their OIDC username
    Categories/
      Podman.md                a category — a page saying what belongs in it
    Homelab/
      podman-on-the-nas.md     a page, as a plain file
      rack.jpg
    .gatherum/
      meta.json                titles, categories, sharing, history
      versions/                superseded content, addressed by SHA-256
```

`meta.json` names a node's categories by name — `"categories": ["Podman"]` — for the same
reason it records who a node is shared with by directory rather than by user id: an id is a
database's opinion, and this file exists for the day there is no database. A category page
marks itself with `"category": true`, and the categories *it* lists are the ones it is
nested under, which is the only place the taxonomy's shape is written down.

Ownership is the path: whoever owns the root directory owns what is under it. Every page
and file is readable, greppable and rsyncable with Gatherum switched off.

## Backups

Back up the storage root. That is the whole instruction.

The database is an index over those directories. Lose it and the startup scan rebuilds
it — tree, categories, links, version history, and who each node is shared with. Only
users, API keys and the keys protecting sign-in cookies live in the database alone, and
all three cost the same thing to lose: signing in again.

`pg_dump gatherum > gatherum.sql` is still worth having, because it saves re-embedding
and re-running media analysis. It is a convenience; the directory is the backup.

## Running it

```sh
cp .env.example .env          # the database password and your OIDC client
mkdir -p data && sudo chown -R 1654:1654 data   # the image's user is uid 1654
docker compose up --build
```

On TrueNAS SCALE, or anywhere else that runs containers as a fixed user, don't chown
anything: point `GATHERUM_DATA` at your dataset and set `GATHERUM_UID`/`GATHERUM_GID` to
the user that owns it (`568` for TrueNAS's `apps`). A mismatch here is a container that
cannot write its own knowledge base.

Then point a TLS-terminating reverse proxy that forwards WebSockets at `127.0.0.1:8080`.
The interactive UI runs over Blazor's `/_blazor` connection, so a proxy that drops
upgrades leaves the app looking loaded but inert.
