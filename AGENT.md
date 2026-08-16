# AGENT.md — the standing brief

Read this before touching the repo. It stays true across milestones; update it when a
milestone changes something it describes.

## What Gatherum is

A self-hosted knowledge base for two people where **pages and files are the same kind
of thing**: every item is a `Node` in one tree with tags, links, revisions, and
searchable text — only the body kind (rich-text page or uploaded file) differs. One
tree, one search, one login, one API, plus an MCP server so agents are first-class
users. Blazor Interactive Server + PostgreSQL, deployed as a single rootless Podman
container behind a TLS-terminating reverse proxy with Authelia for OIDC.

## Build, run, test

```sh
# Postgres (once):
docker run -d --name gatherum-pg -p 5432:5432 -e POSTGRES_DB=gatherum \
  -e POSTGRES_USER=gatherum -e POSTGRES_PASSWORD=gatherum postgres:16-alpine

dotnet build                       # bundles editor JS via esbuild when npm exists
dotnet run --project src/Gatherum.Web    # http://localhost:5140, dev auto-login
dotnet test                        # needs Docker for Testcontainers
dotnet test --filter "FullyQualifiedName~PageMarkdownTests"   # a single class
dotnet test --filter "DisplayName~Mentions_round_trip"        # a single test
```

Config via env vars `Gatherum__*` (see README table). No OIDC configured ⇒ dev
auto-login. Migrations: `dotnet ef migrations add <Name> -p src/Gatherum.Infrastructure
-s src/Gatherum.Infrastructure -o Data/Migrations` — they apply on app startup.

## Repo map

- `src/Gatherum.Core` — domain entities, `GatherumDbContext`, **application services**
  (`Services/`) where every business rule lives, `Markdown/PageMarkdown` (TipTap JSON ⇄
  Markdown), and the three seam interfaces in `Abstractions/`.
- `src/Gatherum.Infrastructure` — implementations with real dependencies: filesystem
  storage, text extractors, Yjs persistence (`Collaboration/`), EF migrations.
- `src/Gatherum.Web` — Blazor components (`Components/`), REST API (`Api/`), MCP tools
  (`Mcp/`), auth (`Auth/`), Yjs websocket host (Program.cs), editor JS (`Scripts/`,
  bundled by esbuild to `wwwroot/js/dist/` at build time — never committed).
- `tests/Gatherum.Tests` — unit tests plus `AppIntegrationTests` booting the real app.

UI components, API endpoints, and MCP tools are all thin: they parse input, call a Core
service, map the result (`Api/ApiModels.cs` DTOs are shared by REST and MCP). Blazor
components run each operation in a fresh DI scope via `Services/AppOperations`.

## Rules that don't bend

- One `Node` entity for every kind of item. New body kinds extend it; they don't fork it.
- MCP and REST stay thin adapters over the same application services. No logic in
  endpoints, tools, or components.
- Storage (`IFileStorage`), extraction (`ITextExtractor`), and authorization
  (`INodeAuthorizer`) are the only abstraction seams. Don't add interfaces without a
  stated second implementation.
- Auth is OIDC-only (plus API keys). No local accounts, ever.
- No comment where a better name would do. Comments explain invariants and whys, not whats.
- Warnings are errors. Never leave the tree red; build and test before every commit.

## Conventions

C# is modern and terse: primary constructors, records for values, expression-bodied
members where they read well. File placement follows the map above; one public type per
file, named for the type. Razor components live under `Components/{Layout,Pages,Shared}`.

**Add a body kind** (e.g. `Canvas`):
1. Extend `NodeKind` and add a body entity (`src/Gatherum.Core/Domain/`, cf. `PageBody.cs`).
2. Configure it in `GatherumDbContext`, add a migration.
3. Teach `NodeService.RefreshSearchText` how to index it.
4. Add a view component beside `Components/Pages/PageView.razor` and dispatch on kind
   in `NodePage.razor`.
5. Extend `NodeDto.From` so REST and MCP describe it.

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

- Pure logic (markdown conversion, hashing, snippets) gets plain unit tests.
- Service behavior (tree ops, privacy, tags, revisions, search) gets tests against real
  Postgres via the `PostgresFixture` collection — never mock the DbContext.
- Cross-surface flows belong in `AppIntegrationTests` (WebApplicationFactory + API key).
- Single test: `dotnet test --filter "DisplayName~<fragment>"`.

## Decision log

Deviations and judgment calls go in [DECISIONS.md](DECISIONS.md) when they happen —
commit messages alone don't count.

## What not to do

- No new projects without a real boundary that demands one.
- No third-party state/MVVM libraries; no hand-vendored JS (npm + esbuild only).
- No speculative abstractions, no repository layer over EF.
- No features beyond `PLAN.md` scope without asking.
- Don't commit `wwwroot/js/dist/` or `node_modules/`.
