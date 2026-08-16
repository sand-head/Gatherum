# Status

As of the MVP milestone. Everything listed as working has been exercised end-to-end
(unit/integration tests, API smoke tests, or scripted two-browser sessions).

## Works

- **Unified node model** — one `Node` for pages and files: tree position, tags, links,
  backlinks, revisions/versions, owner, timestamps, weighted-tsvector search text.
- **Tree + search** — sidebar tree mixing kinds at any depth (create, rename, delete,
  menu move up/down/move-to, drag-drop *upload*), Ctrl/⌘-K palette with kind badges
  and snippets, Postgres FTS with `websearch_to_tsquery`, title ranked above body.
- **Files** — upload via picker or drop, content-addressed SHA-256 filesystem storage,
  previews (image/PDF/video/audio/text), description, tags, referenced-by, version
  history with re-upload and per-version download; extraction: plain text/markdown/code
  verbatim, PDF (PdfPig), image metadata (MetadataExtractor); one-interface extensibility.
- **Editor** — TipTap block editor: slash commands, markdown shortcuts, headings,
  lists, checkboxes, tables, callouts, code blocks, image upload (creates a child File
  node), `@`-mentions with search; autosave (no save button) with saving/saved
  indicator; every save is a revision (5-minute collapse window); history panel with
  view + restore.
- **Live collaboration** — TipTap + Yjs over `/collab/{nodeId}` WebSockets served by
  YDotNet (no Node.js at runtime); remote cursors with names; CRDT state persisted per
  node; verified with two concurrent browsers (both directions, reload survival).
- **Auth** — OIDC-only via discovery (`Gatherum__Oidc__*`), defensive
  `offline_access`, first user becomes admin, cookie sessions; API keys hashed at
  rest, revocable, shown once; dev auto-login only when no authority is configured.
- **REST API** (`/api`) and **MCP** (`/mcp`, Streamable HTTP, all nine required tools) —
  both thin adapters over the same services; page bodies round-trip cleanly through
  Markdown (tested both directions, including mentions and callouts). `MCP.md` covers
  Claude Code setup.
- **Privacy** — per-subtree private flag enforced by one `INodeAuthorizer` in every
  read path (tree, search, API, MCP, collab handshake).
- **Ops** — multi-stage Dockerfile (non-root, verified built + running), compose.yaml,
  Podman Quadlet examples, `/healthz`, JSON console logs outside Development,
  migrations on startup with opt-out.
- **Tests** — 36 passing: markdown round-trip, tree ops, privacy, search, API keys,
  storage/extraction, and an integration test booting the app against Testcontainers
  Postgres (create page → search → MCP `get_node`).

## Stubbed / not shipped (tracked in PLAN.md)

- Drag-and-drop *reorder/reparent* in the tree (menu/keyboard move ships; DnD would
  call the same `NodeService.MoveAsync`).
- Public read-only share links.
- Export (Markdown/HTML/zip) — `PageMarkdown.ToMarkdown` already does the heavy lift.
- Syntax highlighting in code blocks and file previews (plain rendering ships).

## Known gaps

- External edits (REST/MCP/restore) during a *live* editing session are
  last-writer-wins between the CRDT autosave and the external write (DECISIONS.md).
- First-client seeding of a page's collab doc has a millisecond double-seed window
  (DECISIONS.md).
- Saves serialize per node in-process; scaling beyond one app instance needs a
  database-level lock and a shared Yjs backplane.
- File bytes are never garbage-collected when nodes are deleted (content-addressed
  store keeps them; acceptable for two users, cleanup job is future work).
- Tag *pages* browse exists; tag autocomplete uses native datalist, not a rich picker.

## Run it

```sh
# Local (Postgres on localhost, see README):
dotnet run --project src/Gatherum.Web        # http://localhost:5140

# Containers:
docker compose up --build                    # http://localhost:8080

# Rootless Podman (systemd):
podman build -t localhost/gatherum:latest .
cp deploy/quadlet/* ~/.config/containers/systemd/ && systemctl --user daemon-reload
systemctl --user start gatherum

# Tests (Docker needed for Testcontainers):
dotnet test
```
