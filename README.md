# Gatherum

A self-hosted, web-first knowledge base for a person or a group, built on one idea: **pages and
files are the same kind of thing.** Every item is a *node* — it has a title, a place in
one tree, categories, links, backlinks, version history, and searchable text. A page is
simply a node whose file is Markdown; a fic chapter, a Podman quadlet, a PDF, and a
photo all live in one tree, one search, one login, one API. Built almost entirely in
C#/Blazor — the only JavaScript is a small interop file and the EPUB reader's
page-turner.

- **Editing**: pages — and uploaded `.docx` documents — open in
  [slopedit](https://git.sand.town/sand_head/slopedit)'s rich document editor — a
  Google-Docs-style C# editor on a SkiaSharp canvas (proportional text, serif section
  titles from a typeface the app ships, markdown auto-format as you type, tables with
  per-column alignment, images that carry their caption and display width, headings
  that fold their sections away), with a Source-mode toggle for pages;
  code and text files edit in its code editor with syntax highlighting. Autosave,
  mention insertion that links nodes together, and every save is a version — old
  versions downloadable, restorable, and viewable as HTML rather than as a canvas, so a
  preview is searchable, selectable and printable like the article it is.
- **Wiki words**: pages speak a small dialect the editor is taught per document —
  `[[Wiki links]]` that resolve by title (red, with an offer to write the page, when
  nothing answers to that name), `:::infobox` and `:::figure` asides the prose wraps
  around, and `> [!NOTE]` callouts. All of it is plain Markdown in the file, round-tripped
  losslessly, and an Insert menu writes the fences for you. Links go somewhere: a mention
  or a wiki link opens the node it names, an external link leaves the app.
- **Shared lists**: a `:::collection` fence turns a list into something a group
  works rather than reads — the page declares what exists to collect, each participant
  keeps their own tally as a page of their own, and the reading view draws the grid of
  who has what. Share the list and answering it is joining in: whoever can read the list
  sees every column on it, with no second gesture to publish your own answers — while the
  tally itself stays its owner's file, so a column is not a license to open the page
  behind it. A page per collectible is optional and worth it for the few worth writing
  about, ragged rosters are expressible (variants nest, per item), and a signed-out
  visitor to a public list reads it rather than being handed a checkbox that records
  nothing. Ordinary `- [ ]` checklists are untouched — they still mean *it is done*. And the same
  grid asks other questions: `:::availability` is who can make which night and `:::poll`
  is who picked which option, because a row per thing and a column per person is one
  shape whatever the noun.
- **Files**: drag-drop or picker upload anywhere in the tree, content-addressed
  storage (SHA-256) on disk, inline previews (images, PDF, video, audio — and EPUBs
  open in a paginated reader, chapter by chapter, links and all), descriptions,
  categories, and re-upload as a new version with old bytes retrievable. Text extraction
  (plain text/markdown/code verbatim, PDF via PdfPig, docx as its Markdown rendering,
  EPUB chapters in reading order, cartridge headers, image metadata) feeds search — and see **Multimedia** below for what a model adds to
  that on top.
- **Cartridges play**: upload a `.nes`, `.gb`, `.gbc`, `.sms`, `.gg`, `.gba`, `.sfc` or
  `.smc` — or a GameCube disc as `.iso`, `.gcm` or `.rvz` — and its page has a console
  on it — Nintendo Entertainment System, Game Boy and Game Boy Color, Master System,
  Game Gear, Game Boy Advance, Super Nintendo and GameCube — with keyboard or on-screen
  pad, sound, and battery saves kept by your own browser with a `.sav` download to take
  them anywhere. The first four emulators are C# in the app; the Game Boy Advance is
  mGBA, the Super Nintendo is bsnes and the GameCube is Gecko, all built to WebAssembly
  at image-build time from pinned sources (see `native/README.md`; the GameCube draws
  with WebGPU and wants its discs as `.rvz`). All of them run in WebAssembly in your
  browser, and nothing about the game leaves your machine. **Two
  people can play one cartridge** on the machines that had two ports for it: whoever else
  can see the page takes the second controller, and because both browsers run the same
  deterministic console, all that crosses the network is two bytes of buttons a frame —
  the server passes them along and never learns what any of them do.
- **Bookmarks**: paste a URL and Gatherum keeps the page the way an archive would —
  fetched once, on request, never on a schedule. A headless Chromium (in the container
  image) loads the page, its scripts run and settle, lazy-loaded images are scrolled
  into existence, and the document as it stands is captured as one self-contained HTML
  file — stylesheets, images and fonts folded in from the very responses the page
  rendered with, the source address and capture time stamped on the first line. It
  renders inline (sandboxed), searches by what the page said and by where it came
  from, and "capture again" records a fresh capture as a new version with every old
  one kept, like an archive's older crawls. A URL that serves a document — a PDF, an
  image — is kept as that document; with no browser to be found, the capture degrades
  to what the server serves a plain fetch. And a page can **cite** the archive:
  *Insert… → Cite…* writes a footnote whose mention opens the kept capture — dated,
  with the source's own address trailing it — so a claim's reference outlives the page
  it quotes; paste a URL instead of picking a node and it is captured and cited in one
  motion, the way a citation should have always worked.
- **Multimedia**: point `Gatherum__Analysis__Endpoint` at a model you run — llama.cpp's
  server, or anything else speaking the OpenAI API — and uploads that carry no text of
  their own get some. Still images are read (the writing on a photographed whiteboard),
  audio and video are transcribed, and everything gets a short summary, so a recording
  answers to what it was *about* and not just to its filename. Analysis runs on a
  background worker after the upload returns, survives restarts, and is reused when the
  same bytes turn up again. Off by default; nothing is ever sent anywhere without an
  endpoint you configured.
- **Categories**: what a node is *about*, arranged the way an encyclopedia arranges it —
  nested, not a tag cloud. And each one is a *page*: a body saying what belongs in it, a
  history, backlinks, a `[[Podman]]` that resolves to it. Filing a page under `Podman`
  is one edge; filing Podman's own page under `Homelab` is the same edge, and that is what
  a subcategory is — so the page is about the homelab too, the parent lists it, a search
  for either name finds it, and "Similar" counts the kinship. A subject can sit under two
  parents at once. There is nothing to maintain that is not an ordinary page operation:
  rename it by renaming its page, re-nest it by filing it elsewhere, delete it by deleting
  it.
- **Search**: two halves that answer different questions, and both work out of the box.
  PostgreSQL full-text (`tsvector` + GIN, `websearch_to_tsquery`) over titles, category
  names, and text — including what a model read, heard, or made of your media — finds the
  phrase you remember word for word. Beside it, semantic search: pages, files and
  transcripts are cut into passages and embedded into pgvector, so a search for "why the
  closet gets so hot" finds the page that only ever says "thermals". The two rankings are
  fused, never averaged. The embedding model *ships with Gatherum* — twenty-three
  megabytes of MiniLM, run in-process on the CPU, no endpoint to stand up and nothing
  sent anywhere — and `Gatherum__Embedding__Endpoint` overrides it with a better model if
  you run one. `Ctrl`/`⌘`+`K` anywhere.
- **Awareness**: presence shows who else is editing a document, and the editor warns
  when someone saved a newer version (their save stays in history either way).
- **Access**: OIDC sign-in only (built for Authelia; any discovery-capable IdP works),
  API keys for scripts, a REST API under `/api`, and an MCP server at `/mcp` so agents
  like Claude Code can read and write the same knowledge base (see [MCP.md](MCP.md)).
- **Privacy**: any node can be marked private, hiding its whole subtree from the other
  user — in the tree, in search, over the API, and over MCP.

## Documentation

Every copy of Gatherum serves its own manual at **`/docs`** — what a node is, the
Markdown dialect pages are written in, categories, shared lists, search, sharing,
the REST API, the MCP server, and configuration. It is built from
[`src/Gatherum.Web/Docs`](src/Gatherum.Web/Docs) and embedded in the assembly, so it
always describes the version that is running.

It is also written to be handed to a model, which is the point: the wiki words below are
syntax no assistant has seen before. Those URLs need no sign-in.

| Link | What it is |
| --- | --- |
| `/docs/markdown.md` | The dialect on its own — the one page to paste if you only paste one |
| `/docs/all.md` | The whole manual as a single Markdown file |
| `/docs/llms.txt` | An index of every page, in the [llms.txt](https://llmstxt.org) convention |
| `/docs/<page>.md` | Any page's Markdown source |

## Run it locally

Requires the .NET 10 SDK with the `wasm-tools` workload (`dotnet workload install
wasm-tools`; emscripten also wants `python3` on PATH) and a PostgreSQL 16+ carrying the
[pgvector](https://github.com/pgvector/pgvector) extension — the image below has it, and
any Postgres does once `CREATE EXTENSION vector` can run (the migration issues it, which
wants a superuser the first time):

```sh
docker run -d --name gatherum-pg -p 5432:5432 \
  -e POSTGRES_DB=gatherum -e POSTGRES_USER=gatherum -e POSTGRES_PASSWORD=gatherum \
  pgvector/pgvector:pg16

dotnet run --project src/Gatherum.Web
```

Open http://localhost:5140. With no OIDC configured the app signs you in as a local
"Dev User" (and warns in its logs) so everything works out of the box.

The first build downloads the packaged embedding model — 23 MB of MiniLM weights and its
vocabulary — into a gitignored `models/` folder, checks them against a known SHA-256, and
never fetches again. To build with no network, put those two files there yourself or pass
`-p:FetchEmbeddingModel=false` and point `Gatherum__Embedding__ModelPath` at a copy. The
running app never downloads anything.

### Deploying it

`compose.yaml` is a deployment rather than a demo, so it asks for the two things a
deployment needs and refuses to start without them:

```sh
cp .env.example .env          # fill in the password and your OIDC client
mkdir -p data && sudo chown -R 1654:1654 data   # the image's user is uid 1654
docker compose up --build     # or: podman compose up --build
```

**On TrueNAS SCALE**, or anywhere else that runs containers as a fixed user, don't chown
anything: point `GATHERUM_DATA` at your dataset and set `GATHERUM_UID`/`GATHERUM_GID` to
the user that owns it — `568` for TrueNAS's `apps`. `stat -c %u:%g /path/to/dataset` if
you are unsure. A mismatch here is a container that cannot write its own knowledge base.

That user needs to own `${GATHERUM_DATA}` and everything under it. Nothing else on the
host needs to be writable: the keys protecting sign-in cookies live in the database, so a
restart does not sign anybody out and there is no second directory to get the permissions
right on.

Then point your reverse proxy at `127.0.0.1:8080`. The port is bound to loopback on
purpose — see the note under Configuration about forwarded headers.

Two things it does not do, deliberately:

- **It will not run without an identity provider.** Auth is OIDC-only, and the
  development fallback signs in whoever asks without authenticating them. Outside
  `ASPNETCORE_ENVIRONMENT=Development` the app refuses to start rather than be an open
  door, so a half-filled `.env` fails loudly instead of quietly.
- **It does not put your knowledge base in a named volume.** `${GATHERUM_DATA}` is a bind
  mount because that directory *is* the knowledge base: one directory per user, every page
  and file readable, greppable and rsyncable with Gatherum switched off. Point your backup
  at it.

### Running it locally without an identity provider

For a look at it on a machine only you can reach, set the environment explicitly:

```sh
GATHERUM_OIDC_AUTHORITY=none GATHERUM_OIDC_CLIENT_SECRET=none POSTGRES_PASSWORD=local \
  docker compose run --rm -e ASPNETCORE_ENVIRONMENT=Development -p 8080:8080 gatherum
```

which turns the auto-login back on. Never do this anywhere reachable by anyone else.

## Configuration

Everything configures through environment variables (`Gatherum__Section__Key` form):

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Database__ConnectionString` | `Host=localhost;Database=gatherum;Username=gatherum;Password=gatherum` | Npgsql connection string |
| `Gatherum__Database__Migrate` | `true` | Apply EF migrations on startup; set `false` to opt out |
| `Gatherum__Storage__Root` | `data/files` (`/data/files` in the container) | The system of record: one directory per user, named after their OIDC username |
| `Gatherum__Storage__ReindexOnStartup` | `true` | Reconcile the index against the directories at startup — also how a lost database recovers |
| `Gatherum__Sharing__AllowPublic` | `true` | Serve nodes marked public. Off hides them all at once, without editing what an owner recorded |
| `Gatherum__Sharing__AnonymousReadsPerMinute` | `120` | Read budget per client address, for callers with no session |
| `Gatherum__Sharing__AnonymousSearchesPerMinute` | `10` | Search budget per client address. Tighter, because the semantic half runs a model on the request path |

**Behind a reverse proxy**, the anonymous budgets are per client address, so they depend on
`X-Forwarded-For` reaching the app. The container image sets
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, which honours it from *any* peer — so the thing
keeping a client from spoofing the header and minting itself unlimited budgets is that
nothing but the proxy can reach the port. The quadlet binds `127.0.0.1:8080` for exactly
that reason. If you publish the port more widely than that, set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=false` or put known proxies in front of it.
| `Gatherum__Oidc__Authority` | *(empty)* | OIDC issuer URL; empty enables the dev auto-login |
| `Gatherum__Oidc__ClientId` | *(empty)* | OIDC client id |
| `Gatherum__Oidc__ClientSecret` | *(empty)* | OIDC client secret |
| `Gatherum__Oidc__Scopes` | `openid profile email` | Requested scopes |
| `Gatherum__Oidc__RequestOfflineAccess` | `false` | Additionally request `offline_access` (only if your IdP allows it) |
| `Gatherum__Oidc__GroupsClaim` | `groups` | Claim the provider puts group names in |
| `Gatherum__Oidc__RequiredGroup` | *(empty)* | Sign-in requires this group; empty admits every account the provider authenticates |
| `Gatherum__Oidc__AdminGroup` | *(empty)* | Membership grants admin on every sign-in; empty leaves it with the first account seen |
| `Gatherum__Analysis__Endpoint` | *(empty)* | Base URL of an OpenAI-compatible API (e.g. `http://localhost:8080/v1`); empty leaves multimedia analysis off |
| `Gatherum__Analysis__Model` | *(empty)* | Model that reads images and writes summaries |
| `Gatherum__Analysis__AudioModel` | *(falls back to `Model`)* | Model that transcribes speech, if a different one has the ears |
| `Gatherum__Analysis__ApiKey` | *(empty)* | Bearer token, when your runner wants one |
| `Gatherum__Analysis__BackfillExisting` | `true` | On first start, queue media uploaded before analysis was configured |
| `Gatherum__Analysis__VideoFrames` | `4` | Frames sampled across a video for its summary |
| `Gatherum__Analysis__MaxBytes` | `268435456` | Largest file sent for analysis; bigger ones upload and store as before |
| `Gatherum__Analysis__TimeoutSeconds` | `900` | Ceiling on one analysis call |
| `Gatherum__Analysis__FfmpegPath` | `ffmpeg` | How to invoke ffmpeg, which splits video into audio and frames |
| `Gatherum__Bookmarks__BrowserPath` | *(empty; `/usr/bin/chromium` in the container)* | Chromium that renders a bookmarked page before capture. Empty looks in the usual Playwright places and, finding nothing, captures what the server serves instead |
| `Gatherum__Bookmarks__BlockAds` | `true` | Keep ads and trackers out of captures: their hosts are refused before a rendered page's scripts run, and stripped from the snapshot either way |
| `Gatherum__Bookmarks__AdHostsUrl` | *(StevenBlack hosts)* | Community blocklist fetched just in time for a capture and kept for a day; hosts-file, bare-domain and `\|\|host^` formats all work. Empty blocks with the small packaged list alone, which is also the fallback when the fetch fails |
| `Gatherum__Embedding__Endpoint` | *(empty)* | Base URL of an OpenAI-compatible embeddings API (e.g. `http://localhost:8090/v1`); set, it replaces the packaged model |
| `Gatherum__Embedding__Model` | *(empty)* | The embedding model at that endpoint |
| `Gatherum__Embedding__Local` | `true` | Use the packaged MiniLM when no endpoint is set; `false` with no endpoint leaves search full-text only |
| `Gatherum__Embedding__ModelPath` | `models/all-MiniLM-L6-v2` | Where the packaged model lives; relative to the app directory |
| `Gatherum__Embedding__Dimensions` | `384` | Width of the model's vectors (the packaged one's); startup resizes the column to match and re-embeds if it changed |
| `Gatherum__Embedding__ApiKey` | *(empty)* | Bearer token, when your runner wants one |
| `Gatherum__Embedding__MaxDistance` | `0.8` | How far apart two texts can be and still count as an answer; measured for the packaged model, and a property of whichever model you use — raise if search feels too literal, lower if it wanders |
| `Gatherum__Embedding__MaxChunkChars` | `800` | Longest passage handed to the model; keeps inside the packaged model's 256-token window |
| `Gatherum__Embedding__MaxChunksPerNode` | `200` | Ceiling on passages per node; past it the tail stays full-text-only and the log says so |
| `Gatherum__Embedding__BatchSize` | `16` | Passages per request |
| `Gatherum__Embedding__SweepSeconds` | `15` | How often the worker looks for nodes whose text has changed |
| `Gatherum__Embedding__QueryTimeoutMs` | `2000` | Ceiling on embedding a search box before answering from full-text alone |
| `Gatherum__Embedding__TimeoutSeconds` | `120` | Ceiling on one background batch |

The first user ever to sign in becomes admin. API keys are created in **Settings**,
stored hashed, revocable, and sent as `Authorization: Bearer gk_…` to `/api` and `/mcp`.

## Container images

Every merge to `main` publishes an image to this repository's registry, versioned by
[GitVersion](https://gitversion.net) in mainline mode — the number counts what has
landed on `main` since the last release tag, so nothing in the tree needs bumping:

```sh
docker pull ghcr.io/sand-head/gatherum:latest   # or a version: :0.0.8, :0.0
```

`latest` and the `major.minor` tag move; the full version and the `sha-…` tag do not,
so pin one of those in anything you actually deploy. To start a 1.x line, tag a commit
on `main` `v1.0.0` and the versions continue from there.

## Deploying behind a reverse proxy

Gatherum expects a proxy that terminates TLS and **forwards WebSockets** (the app's
interactive UI runs over Blazor's `/_blazor` connection). The container sets
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so pass `X-Forwarded-For` and
`X-Forwarded-Proto`. Caddy example:

```
gatherum.example.org {
    reverse_proxy 127.0.0.1:8080
}
```

(Caddy forwards WebSockets and sets the forwarded headers by default; for nginx you
need the usual `Upgrade`/`Connection` header stanza.)

### Podman Quadlets

Copy `deploy/quadlet/*` to `~/.config/containers/systemd/`, adjust the passwords,
OIDC settings, and image name, then:

```sh
podman pull ghcr.io/sand-head/gatherum:latest   # or build it: podman build -t localhost/gatherum:latest .
systemctl --user daemon-reload
systemctl --user start gatherum
```

### Authelia client

```yaml
identity_providers:
  oidc:
    clients:
      - client_id: gatherum
        client_name: Gatherum
        client_secret: '$pbkdf2-sha512$…'   # authelia crypto hash generate pbkdf2
        public: false
        authorization_policy: two_factor
        redirect_uris:
          - https://gatherum.example.org/signin-oidc
        scopes: [openid, profile, email]
        userinfo_signed_response_alg: none
```

Then set `Gatherum__Oidc__Authority=https://auth.example.org`, the client id, and the
**plaintext** client secret on the Gatherum container.

To admit only one Authelia group, add `groups` to the client's `scopes` above **and** to
`Gatherum__Oidc__Scopes`, then set `Gatherum__Oidc__RequiredGroup=gatherum`. The gate fails
closed: if the claim never arrives — the usual cause being the scope missing from one of
those two places — nobody is admitted, and the log line says so by name. Add
`Gatherum__Oidc__AdminGroup` to have a second group decide who is an admin, checked afresh
at every sign-in.

## Backups

**One thing holds your knowledge base**: the directory behind
`Gatherum__Storage__Root` — one subdirectory per user, every page and file a plain
file at a readable path, with titles, categories, sharing and history in a
`.gatherum/meta.json` beside them. Back that up and you have everything.

The database is an index over it. Lose it, corrupt it, botch a migration: start the
container and the startup scan rebuilds it from the directories — tree, categories,
links, version history, and who each node is shared with. Only users, API keys and the
keys protecting sign-in cookies live in the database alone, and all three cost the same
thing to lose: signing in again.

So `pg_dump gatherum > gatherum.sql` is worth having to save re-embedding and
re-running media analysis, but it is a convenience. The directory is the backup.

## Extending

**A new text extractor** (make some file type searchable): implement
`ITextExtractor` (`src/Gatherum.Core/Abstractions/ITextExtractor.cs`) next to the
existing ones in `src/Gatherum.Infrastructure/Extraction/`, then register it in
`GatherumServiceCollectionExtensions.AddGatherum`. First extractor claiming a file wins.

**A new storage backend** (e.g. S3): implement `IFileStorage`
(`src/Gatherum.Core/Abstractions/IFileStorage.cs`) — save returns the SHA-256, reads
resolve it — and swap the registration in `AddGatherum`. No caller changes.

**Richer permissions**: replace `DefaultNodeAuthorizer`
(`src/Gatherum.Core/Services/DefaultNodeAuthorizer.cs`), the single gate every read
path goes through.

## Development

```sh
dotnet build                 # needs the wasm-tools workload (the Auto islands)
dotnet test                  # unit + integration tests; Postgres via Testcontainers
```

See [AGENT.md](AGENT.md) for the repo map and conventions, [PLAN.md](PLAN.md) for the
build plan, [DECISIONS.md](DECISIONS.md) for recorded trade-offs, and
[STATUS.md](STATUS.md) for what ships and what's stubbed.

Icons are [Lucide](https://lucide.dev), ISC — mostly inline in the markup, and as files
under `src/Gatherum.Web/wwwroot/icons/` (with the pack's license) where a stylesheet has
to draw one.
