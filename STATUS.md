# Status

As of the pages-are-files / no-JavaScript revision. Everything listed as working has
been exercised end-to-end (unit/integration tests, API smoke tests, or scripted
browser sessions — including against the built container).

## Works

- **Unified node model, all the way down** — one `Node`; every body is a file version
  in content-addressed (SHA-256) disk storage. A page is a `text/markdown` node;
  `Kind` is derived, never stored. `FileVersion` is the single history mechanism:
  text autosaves collapse within a 5-minute same-author window, different authors
  always get separate versions, and restore is a row insert (content addressing means
  no byte copies).
- **Native editing** — Markdown and every text file (code, configs, notes) open in
  slopedit's `EditorView`: a from-scratch C# editor on a SkiaSharp canvas running as
  a WebAssembly island. Syntax highlighting by extension, autosave with indicator,
  server-rendered live Markdown preview, "Link node…" mention search, version
  history with view/restore. The only JavaScript in the app is a ~30-line interop
  file (Ctrl-K, drag-drop upload).
- **Awareness** — heartbeat presence ("Sam is editing", verified cross-user) and a
  newer-version warning in the editor. Concurrent saves are serialized per node;
  nobody's save is ever lost — it's a version.
- **Tree + search** — sidebar tree mixing all nodes (create, rename, delete, menu
  move up/down/move-to, drag-drop upload), Ctrl/⌘-K palette with kind badges and
  snippets, Postgres FTS with `websearch_to_tsquery`, title ranked above body.
- **Files** — upload via picker or drop, previews (image/PDF/video/audio/text),
  description, tags, referenced-by, per-version download; extraction: text verbatim,
  PDF (PdfPig), image metadata (MetadataExtractor); media types resolved sensibly
  when browsers upload code as octet-stream.
- **Links** — mentions (`[@Title](node://id)`) and embedded files parse straight from
  Markdown into link rows; backlinks everywhere; mentions render as in-app links,
  `> [!kind]` quotes render as callouts.
- **Auth** — OIDC-only via discovery, defensive `offline_access`, first user becomes
  admin, cookie sessions; API keys hashed at rest, revocable, shown once; dev
  auto-login only when no authority is configured.
- **REST API** (`/api`) and **MCP** (`/mcp`, all nine tools) — thin adapters over the
  same services; page bodies are Markdown verbatim in both directions.
- **Privacy** — per-subtree private flag enforced by one `INodeAuthorizer` in every
  read path.
- **Ops** — multi-stage Dockerfile (wasm-tools workload for the editor island,
  non-root runtime; built and exercised against a postgres container, editor
  verified in-browser against the containerized app), compose.yaml, Podman Quadlets,
  `/healthz`, JSON console logs outside Development, migrations on startup with
  opt-out.
- **Tests** — 39 passing: markdown links/rendering, tree ops, privacy, versions
  (collapse, restore, re-upload, cross-author), search, API keys, storage/extraction,
  and integration tests booting the app on Testcontainers Postgres (create page →
  search → MCP `get_node`).

## Stubbed / not shipped (tracked in PLAN.md)

- Drag-and-drop *reorder/reparent* in the tree (menu move ships).
- Public read-only share links.
- Export endpoints (bodies are already Markdown files on disk; export is a tree walk
  + zip stream).
- Rich preview for non-edited text files (plain `<pre>`; the editor itself highlights).

## Known gaps

- No live co-editing: presence + versions instead of CRDT merging (DECISIONS.md; the
  trade was chosen deliberately with the no-JS direction).
- The editor uses the raster canvas: SKGLView's dispose crashes the WASM renderer on
  navigation (DECISIONS.md has the upstream pointer).
- Editor is capped to text files ≤ 4 MB; larger text files fall back to FileView.
- File bytes are never garbage-collected when nodes are deleted.
- Saves serialize per node in-process; scaling beyond one app instance needs a
  database-level lock.

## Run it

```sh
# Local (Postgres on localhost, wasm-tools workload installed; see README):
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
