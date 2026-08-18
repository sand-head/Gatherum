# Status

As of the slopedit 2.0 revision (DocumentView pages, Interactive Auto editor island). Everything listed as working has
been exercised end-to-end (unit/integration tests, API smoke tests, or scripted
browser sessions — including against the built container).

## Works

- **Unified node model, all the way down** — one `Node`; every body is a file version
  in content-addressed (SHA-256) disk storage. A page is a `text/markdown` node;
  `Kind` is derived, never stored. `FileVersion` is the single history mechanism:
  text autosaves collapse within a 5-minute same-author window, different authors
  always get separate versions, and restore is a row insert (content addressing means
  no byte copies).
- **Native editing** — pages open in slopedit 2.0's `DocumentView`: a
  Google-Docs-style rich document editor (proportional layout, styled runs, markdown
  auto-format as you type, tables, images via the in-app content URLs), converting
  losslessly through `MarkdownSerializer`. A Source toggle swaps to `EditorView` with
  the Markdown lexer; code/config/text files edit in `EditorView` with syntax
  highlighting. Autosave with indicator, "Link node…" mention insertion as real link
  runs, version history with restore. The editor is an Interactive Auto island
  (Gatherum.Client) over a dual `IEditorData` seam — services on the server circuit,
  HTTP under WebAssembly. The only JavaScript in the app is a ~30-line interop file.
- **Awareness** — heartbeat presence ("Sam is editing", verified cross-user) and a
  newer-version warning in the editor (verified: fires when another user saves the
  open document). Concurrent saves are serialized per node; nobody's save is ever
  lost — it's a version.
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
- **Ops** — multi-stage Dockerfile (wasm-tools for the Auto island, non-root runtime; built and
  exercised against a postgres container, editor verified in-browser against the
  containerized app), compose.yaml, Podman Quadlets, `/healthz`, JSON console logs
  outside Development, migrations on startup with opt-out.
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
- The editor island is Interactive Auto, but Blazor's Auto mode matches the render
  mode already on the page — and the chrome is Server islands, so the editor runs in
  the Server home (streamed frames) in practice. True local rendering needs the whole
  chrome converted to WASM-capable components; open follow-up.
- docx viewing/editing waits on the SlopEdit.Docx package publish (DECISIONS.md).
- Editor is capped to text files ≤ 4 MB; larger text files fall back to FileView.
- File bytes are never garbage-collected when nodes are deleted.
- Saves serialize per node in-process; scaling beyond one app instance needs a
  database-level lock.

## Run it

```sh
# Local (Postgres on localhost; see README):
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
