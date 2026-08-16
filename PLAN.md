# Gatherum MVP — Plan

Gatherum is a self-hosted knowledge base where pages and files are the same kind of thing:
every item is a node in one tree with tags, links, revisions, and full-text search.

## Milestones (in order, one commit each)

1. **Skeleton.** Solution with `Gatherum.Core`, `Gatherum.Infrastructure`, `Gatherum.Web`
   (Blazor Web App, Interactive Server), `Gatherum.Tests`. Nullable + warnings-as-errors
   everywhere. Green build.
2. **Domain & schema.** `Node` plus page/file bodies, file versions, revisions, tags, links,
   users, API keys, Yjs state. EF Core + Npgsql, `tsvector` generated column with GIN index,
   migrations checked in, applied on startup (opt-out via `Gatherum__Database__Migrate=false`).
3. **Services & unit tests.** Application services in Core — the single home of business rules:
   node tree operations (create/rename/move/delete/reorder), tags, links, search-text
   maintenance, FTS search, file upload with versioning. Content-addressed (SHA-256) filesystem
   storage behind `IFileStorage`. Text extraction behind `ITextExtractor` (plain text/markdown/
   code verbatim, PDF via PdfPig, image metadata via MetadataExtractor). Markdown ⇄ TipTap-JSON
   converter with round-trip tests. `INodeAuthorizer` for the private-subtree flag.
   Unit tests: tree ops, markdown round-trip, search text, API-key hashing.
4. **Auth.** OIDC-only sign-in (`Gatherum__Oidc__*` env vars, discovery, defensive
   `offline_access`), cookie for the browser, first user becomes admin. API keys hashed at
   rest, revocable, usable as `Authorization: Bearer` on `/api` and `/mcp`.
5. **REST API.** Minimal APIs under `/api` — thin adapters over the Core services.
6. **MCP server.** Official `ModelContextProtocol` SDK, Streamable HTTP at `/mcp`, API-key
   auth. Tools: `search`, `get_node`, `list_children`, `create_page`, `update_page`,
   `move_node`, `add_tag`, `list_tags`, `get_backlinks`. `MCP.md` documents Claude Code setup.
7. **UI.** Sidebar tree mixing pages and files (menu/keyboard move; drag-drop is a should),
   Ctrl/⌘-K search palette with kind + snippet, file node pages (preview, description, tags,
   referenced-by, version history, re-upload), upload by drop or picker, tag browsing,
   settings page for API keys, dark mode via `prefers-color-scheme`.
8. **Editor & collab.** TipTap + Yjs via JS interop (ES modules bundled with esbuild through
   an MSBuild target). Slash commands, markdown shortcuts, headings/lists/checkboxes/tables/
   callouts/code blocks, image upload creates a File node, `@`-mention links nodes. Autosave
   → revisions with history/restore panel. Live collab through YDotNet-hosted Yjs sync over
   WebSockets with state persisted per node. If YDotNet proves unworkable, fall back to
   SignalR presence + optimistic concurrency and record it in `DECISIONS.md`.
9. **Ops, integration test, docs.** Multi-stage Dockerfile (non-root aspnet runtime),
   `compose.yaml`, Podman Quadlets, `/healthz`, structured logging. Testcontainers integration
   test: boot app against Postgres, create page → search → MCP `get_node`. `README.md`,
   `AGENT.md`, `STATUS.md`, `DECISIONS.md`.

## Shoulds — status

- Tag pages with autocomplete: **in scope** (milestone 7).
- Dark mode: **in scope** (milestone 7).
- Drag-and-drop reorder/reparent in the tree: **stubbed** — menu/keyboard move ships; DnD is a
  TODO (tree component exposes the same `MoveNode` service call DnD would use).
- Public share links: **stubbed** — TODO; schema leaves room (a future `ShareLink` table),
  no code ships.
- Export Markdown/HTML/zip: **stubbed** — TODO; `get_node` already produces Markdown, so
  export is a walk over the same converter.
