# AGENT.md — the standing brief

Read this before touching the repo. It stays true across milestones; update it when a
milestone changes something it describes.

## What Gatherum is

A self-hosted knowledge base for two people where **pages and files are the same kind
of thing**: every item is a `Node` in one tree with tags, links, versions, and
searchable text — and a page is simply a node whose file is Markdown. One tree, one
search, one login, one API, plus an MCP server so agents are first-class users.
C#/Blazor end to end — static shell with Interactive Auto islands for everything
interactive: the first visit renders on a server circuit while the WASM runtime
downloads, every later visit runs fully in WebAssembly over `/api` (the only JS is
`wwwroot/js/gatherum.js`, ~65 lines) — PostgreSQL, deployed as a single rootless
Podman container behind a TLS-terminating reverse proxy with Authelia for OIDC.

## Build, run, test

```sh
dotnet workload install wasm-tools     # once; the editor island relinks SkiaSharp
# Postgres (once):
docker run -d --name gatherum-pg -p 5432:5432 -e POSTGRES_DB=gatherum \
  -e POSTGRES_USER=gatherum -e POSTGRES_PASSWORD=gatherum postgres:16-alpine

dotnet build
dotnet run --project src/Gatherum.Web    # http://localhost:5140, dev auto-login
dotnet test                              # needs Docker for Testcontainers
dotnet test --filter "FullyQualifiedName~FileVersionTests"    # a single class
dotnet test --filter "DisplayName~Restore_brings_back"        # a single test
```

Config via env vars `Gatherum__*` (see README table). No OIDC configured ⇒ dev
auto-login. Migrations: `dotnet ef migrations add <Name> -p src/Gatherum.Infrastructure
-s src/Gatherum.Infrastructure -o Data/Migrations` — they apply on app startup.

## Repo map

- `src/Gatherum.Core` — domain entities, `GatherumDbContext`, **application services**
  (`Services/`) where every business rule lives (`NodeService` = tree/tags/links,
  `FileService` = bodies/versions/text editing), `Markdown/MarkdownContent` (link
  conventions), and the three seam interfaces in `Abstractions/`.
- `src/Gatherum.Infrastructure` — implementations with real dependencies: filesystem
  storage, text extractors, EF migrations.
- `src/Gatherum.Web` — the static pages and layout (`Components/`), REST API
  (`Api/`), MCP tools (`Mcp/`), auth (`Auth/`), presence + `ServerAppData`, the
  server implementation of the interactive components' data seam (`Services/`).
- `src/Gatherum.Client` — every interactive component, all Interactive Auto: the
  editor (`NodeEditor` hosting slopedit's `DocumentView` for pages and docx,
  `EditorView` for code/source), tree, sidebar panels (contents/similar/recent),
  search palette, node header, tags, version
  panel, file view, and settings keys. `IAppData` (`AppData.cs`) is their only view of the world —
  implemented by `ServerAppData` over the services on the server circuit and by
  `HttpAppData` over `/api` in WebAssembly.
- `tests/Gatherum.Tests` — unit tests plus `AppIntegrationTests` booting the real app.

Render modes: static SSR for pages and layout; every interactive component is an
Interactive Auto island from Gatherum.Client. Blazor's Auto mode matches the mode
already interactive on the page, so keeping the chrome free of Server-only islands is
what lets the whole screen resolve to WebAssembly — don't add an Interactive Server
component without realizing it pins every Auto island back to the circuit. UI
components, API endpoints, and MCP tools are all thin: they parse input, call a Core
service (through `IAppData` in Client components), map the result (`Api/ApiModels.cs`
DTOs are shared by REST and MCP). On the server, each `IAppData` operation runs in a
fresh DI scope via `Services/AppOperations`.

## Rules that don't bend

- One `Node` entity, one body model: every node's content is a file version in
  content-addressed storage; `Kind` is derived from the media type, never stored.
- MCP and REST stay thin adapters over the same application services. No logic in
  endpoints, tools, or components.
- Storage (`IFileStorage`), extraction (`ITextExtractor`), and authorization
  (`INodeAuthorizer`) are the only abstraction seams. Don't add interfaces without a
  stated second implementation.
- Auth is OIDC-only (plus API keys). No local accounts, ever.
- No JavaScript beyond `wwwroot/js/gatherum.js`, and nothing goes in there that
  Blazor can do natively.
- No comment where a better name would do. Comments explain invariants and whys, not whats.
- Warnings are errors. Never leave the tree red; build and test before every commit.

## Conventions

C# is modern and terse: primary constructors, records for values, expression-bodied
members where they read well. File placement follows the map above; one public type per
file, named for the type. Static pages and layout live under
`Components/{Layout,Pages}` in Web; interactive components live flat in Client and
touch the server only through `IAppData`.

**Make a new format editable/previewable**:
1. Teach `MediaTypes` (`src/Gatherum.Core/Domain/MediaTypes.cs`) the extension.
2. Editable text just works once `MediaTypes.IsText` says yes; for a richer preview,
   extend the media-type dispatch in `src/Gatherum.Client/FileView.razor`.
3. For syntax highlighting, add the lexer upstream in slopedit's `LexerRegistry`.
4. A binary rich-document format follows the docx pattern end to end: a converter
   case beside `DocxConverter`'s in `NodeEditor`, the format in NodePage's editable
   check and `FileService.SaveBinaryAsync`'s guard, and an extractor for search.

**Add a text extractor**:
1. Implement `ITextExtractor` in `src/Gatherum.Infrastructure/Extraction/` (cf.
   `PdfTextExtractor.cs`).
2. Register it in `GatherumServiceCollectionExtensions.AddGatherum`.
3. Add a claim/extract test beside `FileStorageTests.cs`.

**Add a storage backend**:
1. Implement `IFileStorage` (cf. `Storage/FileSystemStorage.cs`); save returns SHA-256.
2. Swap the registration in `AddGatherum` (make it configurable then).
3. Run `FileStorageTests` patterns against it.

**Add an MCP tool**:
1. Add the operation to a Core service if it doesn't exist.
2. Expose the REST endpoint in `Api/ApiEndpoints.cs`.
3. Add a method in `Mcp/GatherumMcpTools.cs` (attribute + `[Description]`s, call the
   service, map with the shared DTOs) and document it in `MCP.md`.

## Testing expectations

- Pure logic (markdown links/rendering, hashing, snippets, media types) gets plain
  unit tests.
- Service behavior (tree ops, privacy, tags, versions, search) gets tests against real
  Postgres via `PostgresFixture` + `ServiceHarness` — never mock the DbContext.
- Cross-surface flows belong in `AppIntegrationTests` (WebApplicationFactory + API key).
- Single test: `dotnet test --filter "DisplayName~<fragment>"`.

## Decision log

Deviations and judgment calls go in [DECISIONS.md](DECISIONS.md) when they happen —
commit messages alone don't count.

## What not to do

- No new projects without a real boundary that demands one.
- No third-party state/MVVM libraries; no JS libraries, vendored or fetched.
- No speculative abstractions, no repository layer over EF.
- No features beyond `PLAN.md` scope without asking.
