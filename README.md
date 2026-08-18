# Gatherum

A self-hosted, web-first knowledge base for two people, built on one idea: **pages and
files are the same kind of thing.** Every item is a *node* — it has a title, a place in
one tree, tags, links, backlinks, version history, and searchable text. A page is
simply a node whose file is Markdown; a fic chapter, a Podman quadlet, a PDF, and a
photo all live in one tree, one search, one login, one API. Built almost entirely in
C#/Blazor — the only JavaScript is a ~65-line interop file.

- **Editing**: pages — and uploaded `.docx` documents — open in
  [slopedit](https://git.sand.town/sand_head/slopedit)'s rich document editor — a
  Google-Docs-style C# editor on a SkiaSharp canvas (proportional text, markdown
  auto-format as you type, tables, images), with a Source-mode toggle for pages;
  code and text files edit in its code editor with syntax highlighting. Autosave,
  mention insertion that links nodes together, and every save is a version — old
  versions viewable, downloadable, restorable.
- **Files**: drag-drop or picker upload anywhere in the tree, content-addressed
  storage (SHA-256) on disk, inline previews (images, PDF, video, audio), descriptions,
  tags, and re-upload as a new version with old bytes retrievable. Text extraction
  (plain text/markdown/code verbatim, PDF via PdfPig, docx as its Markdown rendering,
  image metadata) feeds search.
- **Search**: PostgreSQL full-text (`tsvector` + GIN, `websearch_to_tsquery`) over
  titles, tags, and text. `Ctrl`/`⌘`+`K` anywhere.
- **Awareness**: presence shows who else is editing a document, and the editor warns
  when someone saved a newer version (their save stays in history either way).
- **Access**: OIDC sign-in only (built for Authelia; any discovery-capable IdP works),
  API keys for scripts, a REST API under `/api`, and an MCP server at `/mcp` so agents
  like Claude Code can read and write the same knowledge base (see [MCP.md](MCP.md)).
- **Privacy**: any node can be marked private, hiding its whole subtree from the other
  user — in the tree, in search, over the API, and over MCP.

## Run it locally

Requires the .NET 10 SDK with the `wasm-tools` workload (`dotnet workload install
wasm-tools`; emscripten also wants `python3` on PATH) and a PostgreSQL 16+:

```sh
docker run -d --name gatherum-pg -p 5432:5432 \
  -e POSTGRES_DB=gatherum -e POSTGRES_USER=gatherum -e POSTGRES_PASSWORD=gatherum \
  postgres:16-alpine

dotnet run --project src/Gatherum.Web
```

Open http://localhost:5140. With no OIDC configured the app signs you in as a local
"Dev User" (and warns in its logs) so everything works out of the box.

Or run the whole stack in containers:

```sh
docker compose up --build     # or: podman compose up --build
```

## Configuration

Everything configures through environment variables (`Gatherum__Section__Key` form):

| Variable | Default | Purpose |
| --- | --- | --- |
| `Gatherum__Database__ConnectionString` | `Host=localhost;Database=gatherum;Username=gatherum;Password=gatherum` | Npgsql connection string |
| `Gatherum__Database__Migrate` | `true` | Apply EF migrations on startup; set `false` to opt out |
| `Gatherum__Storage__Root` | `data/files` (`/data/files` in the container) | Root of content-addressed file storage |
| `Gatherum__Oidc__Authority` | *(empty)* | OIDC issuer URL; empty enables the dev auto-login |
| `Gatherum__Oidc__ClientId` | *(empty)* | OIDC client id |
| `Gatherum__Oidc__ClientSecret` | *(empty)* | OIDC client secret |
| `Gatherum__Oidc__Scopes` | `openid profile email` | Requested scopes |
| `Gatherum__Oidc__RequestOfflineAccess` | `false` | Additionally request `offline_access` (only if your IdP allows it) |

The first user ever to sign in becomes admin. API keys are created in **Settings**,
stored hashed, revocable, and sent as `Authorization: Bearer gk_…` to `/api` and `/mcp`.

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
podman build -t localhost/gatherum:latest .
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

## Backups

Two things hold all state:

1. **The database** — `pg_dump gatherum > gatherum.sql` (or snapshot the
   `gatherum-db` volume). Metadata, tags, links, versions, search text.
2. **The file store** — the directory behind `Gatherum__Storage__Root` (the
   `gatherum-files` volume). Every body — pages included — is a content-addressed
   blob here, so this rsyncs cheaply.

Restore = restore both, start the container.

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
