# Status

As of the all-Auto revision (the whole interactive UI renders in WebAssembly after
the first visit). Everything listed as working has
been exercised end-to-end (unit/integration tests, API smoke tests, or scripted
browser sessions — including against the built container).

## Works

- **Unified node model, all the way down** — one `Node`; every body is a file version
  in content-addressed (SHA-256) disk storage. A page is a `text/markdown` node;
  `Kind` is derived, never stored. `FileVersion` is the single history mechanism:
  text autosaves collapse within a 5-minute same-author window, different authors
  always get separate versions, and restore is a row insert (content addressing means
  no byte copies).
- **Native editing** — pages open in slopedit's `DocumentView`: a
  Google-Docs-style rich document editor (proportional layout, styled runs, markdown
  auto-format as you type, tables, images via the in-app content URLs), converting
  losslessly through `MarkdownSerializer`. A Source toggle swaps to `EditorView` with
  the Markdown lexer; code/config/text files edit in `EditorView` with syntax
  highlighting. Uploaded `.docx` files open in the same `DocumentView` through
  `DocxConverter` (Full profile — underline, color, alignment survive), saving real
  docx bytes with the same autosave collapse; their search text is the canonical
  Markdown rendering, so docx mentions backlink like pages (all verified end-to-end,
  including from the WebAssembly home). Autosave with indicator, "Link node…" mention insertion as real link
  runs, version history with restore (old Markdown previews in a read-only
  `DocumentView`). Every interactive component — editor, tree, search palette, node
  header, tags, versions, file view, settings keys — is an Interactive Auto island in
  `Gatherum.Client` over one `IAppData` seam (services on the server circuit, HTTP
  under WebAssembly): the first visit renders on the server while the runtime
  downloads, and every visit after runs fully in WebAssembly with zero websockets
  (verified in-browser: editing, autosave, rename, tags, history, restore, search,
  keys, stale-version warning all exercised in the WASM home). The only JavaScript in
  the app is a ~30-line interop file.
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
- **Tests** — 42 passing: markdown links, docx extraction/editing/backlinks, tree ops, privacy, versions
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
- The first visit in a fresh browser renders on the server circuit while the WASM
  runtime downloads (~a minute on a slow link); local rendering starts from the next
  visit. That's Blazor Auto working as designed, not a gap to fix.
- docx conversion is lossy by slopedit's design: exact fonts/spacing and embedded
  media don't survive the trip (images become visible placeholders). Old docx
  versions have no inline preview in the history panel — download and restore only.
- Editor is capped to editable files ≤ 4 MB; larger ones fall back to FileView.
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
