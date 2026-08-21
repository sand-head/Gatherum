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

## Revision: pages are files, and no JavaScript (post-MVP)

Two directions landed after the MVP shipped, both at the owner's request:

10. **Pages become Markdown files.** The stored `Kind` column is gone — a page is a
    node whose file is `text/markdown`, and every text file is editable and viewable
    the way pages are. Bodies live as bytes in the content-addressed store;
    `FileVersion` is the single history mechanism for everything (text autosaves
    collapse within a window; restore is a row insert). `PageBody`, `Revision`, and
    the Yjs state table are gone.
11. **Native C#/Blazor everywhere possible.** TipTap, Yjs, YDotNet, npm, and esbuild
    are removed. The editor is [slopedit](https://git.sand.town/sand_head/slopedit)'s
    `EditorView` — a from-scratch C# editor on a SkiaSharp canvas — running as a
    WebAssembly island (static shell, per-component render modes; server islands for
    tree/search/pages). Live collaboration became presence + optimistic versioning:
    "X is editing", a newer-version notice, and history keeps everyone's saves. The
    only JavaScript is a ~30-line static interop file (Ctrl-K, drag-drop upload).

### Shoulds — status (updated)

- Tag pages with autocomplete: shipped.
- Dark mode: shipped.
- Drag-and-drop reorder/reparent: still TODO (menu move ships).
- Public share links: still TODO.
- Export Markdown/zip: closer than ever — bodies *are* Markdown files on disk; an
  export endpoint is a tree walk plus a zip stream. Still TODO.
- Syntax highlighting: shipped for the editor (slopedit lexers); file *previews* of
  non-edited text render plain.

### Post-revision: the editor joined the server circuit

slopedit 1.7.0 added Interactive Server support (server-side Skia, frames streamed
over the circuit), so the WebAssembly island, the `Gatherum.Client` project, and the
wasm-tools toolchain were all removed. The app is back to a single global Interactive
Server render mode, and the editor calls the application services directly.

### Post-revision: slopedit 2.0 — the document editor

Pages now edit in `DocumentView` (rich document, Markdown-lossless) with a Source
toggle; the editor returned to an island — Interactive Auto from `Gatherum.Client`
over a dual `IEditorData` seam — though Blazor's mode-matching keeps it in the Server
home while the chrome is Server islands. Blazor's 32 KB hub message cap, which killed
tall documents silently, is raised to 2 MB. docx editing is mapped and seamed but
waits on the SlopEdit.Docx package publish.

### Post-revision: the whole chrome went Interactive Auto

To let the Auto editor actually resolve to WebAssembly, every interactive island —
tree, search palette, node header, tags, version panel, file view, settings keys —
moved into `Gatherum.Client` as Interactive Auto components over a widened `IAppData`
seam (services on the server circuit, `/api` in WASM); the tags page became static
SSR. First visit renders on the server circuit while the runtime downloads; every
later visit runs fully local with zero websockets. `MarkdownRender` is gone (version
previews use a read-only `DocumentView`), and the upload endpoints' body cap now
matches the 512 MB the pickers promise.

### Post-revision: docx joins the document editor

SlopEdit.Docx published, so `.docx` uploads now open and edit in the same
`DocumentView` as pages (`ToRichDocument`/`FromRichDocument`, Full profile), saving
real docx bytes through a binary sibling of the text door with the same autosave
collapse. A `DocxTextExtractor` feeds search with the converter's canonical Markdown
rendering, which also makes docx mentions backlink like pages. All slopedit packages
moved to 2.0.1.

### Post-revision: slopedit 2.1 — the wiki's own words

slopedit 2.1 opened the Markdown container to the host: `MarkdownExtension` grafts
custom syntax onto the serializer per call, `FloatedRun`/`ExclusionZone` lift a run of
blocks out of the flow so prose wraps past it, `BlockDecoration` paints boxes behind
block runs, and `DocumentView.OnLinkActivated` hands link navigation to the host. On
that seam Gatherum grew the conventions an encyclopedia needs, none of which the editor
had to learn:

- `[[Wiki links]]` — slopedit's shipped `WikiLinkExtension` for the spelling, plus
  server-side resolution by title, so a wiki link backlinks like a mention and a title
  nobody has written yet renders as a red link that offers to create the page.
- `:::infobox` and `:::figure` — a host block extension reading a directive fence into a
  floated, decorated run of ordinary blocks; and `> [!NOTE]`-style callouts, which the
  docs had been promising since the MVP, now actually painted.
- Links go somewhere: mentions and embedded files route in-app, wiki links resolve by
  title, external schemes leave the app — and a pending autosave is flushed first.

The chrome is *derived* from block tags on every edit rather than pinned at parse, since
block indices move as a page is written. All slopedit packages moved to 2.1.0.

### Post-revision: media that carries no text of its own

Uploading a photo used to index its EXIF and nothing else; a video indexed nothing at
all. `IMediaAnalyzer` is a second seam beside `ITextExtractor` — slow, fallible, and off
the request path — with `OpenAiMediaAnalyzer` speaking `/chat/completions` against a
model the owner runs (llama.cpp, any-to-any). Still images get OCR and a description,
audio gets a transcript and a summary of it, and video is split by ffmpeg into the audio
a model listens to and frames it looks at. `FileVersion` grew `Transcript`, `Summary`,
`Analysis`, and `AnalysisError`; both new texts feed `SearchText` and the shared DTOs.
`MediaAnalysisQueue` hands uploads to `MediaAnalysisWorker`, which works one file at a
time, sweeps for unfinished and pre-existing media at startup, and records failures on
the version rather than retrying. Analysis is keyed by content hash, so identical bytes
and restored versions inherit an answer already paid for. With no endpoint configured
nothing is registered and every upload behaves exactly as it did before.

### Post-revision: nested categories, and tags are gone

Tags left the app at the owner's request — Wikipedia has none, Google Docs has none, and
finding an article is search's job. What replaced them is a taxonomy: `Category` rows
that nest, addressed by path (`homelab/podman`), with `CategoryService` owning the
spelling rules, the creation-by-use, the member counts, and the maintenance (rename,
re-nest, delete, each carrying its subcategories and its members' search text along). A
node's search text now carries a category's whole ancestry, so the parent finds the
child's pages; "Similar" scores a shared category above a shared corner of the taxonomy
and a body link above both. `/categories` browses the tree and every category page is
editable in place; the node header's chips file and unfile; REST gained
`/api/categories…` and MCP gained `add_category`, `remove_category`, `list_categories`
and `browse_category` in place of `add_tag`/`list_tags`. The migration turns every
existing tag into a root category of the same name.

### Post-revision: search grows a second half

Full-text search finds the words you remember; it cannot find the page you can only
describe. `IEmbedder` is a third seam beside extraction and analysis, with
`OpenAiEmbedder` speaking `/embeddings` against an embedding model the owner runs (a
second llama.cpp instance, since a chat model asked for embeddings gives poor ones). Each
node's text is cut into passages by `TextChunker`, embedded with the node's title, and
stored in a pgvector column whose width is settled at startup from configuration rather
than pinned in a migration. `Node.TextFingerprint` — a generated column — makes staleness
a property of the data, so `EmbeddingWorker` sweeps for it rather than being handed work,
and a category rename re-embeds everything filed under it without knowing embeddings
exist. `SearchService` now runs the tsvector query and a KNN query under one visibility
filter and fuses their rankings with `RankFusion` (reciprocal rank fusion); a hit only the
vector half found is snippeted from the passage that matched. `Similar` gained the same
sense, so a page can be kin to one it shares no category and no link with. REST and MCP
gained `mode=hybrid|text|semantic`. Off unless an endpoint is configured, and bounded on
the query path by `QueryTimeoutMs`, so a model that is missing, unreachable, or slow means
full-text search and never a failed search.

### Post-revision: the embedding model moves into the box

Semantic search shipped opt-in, needing an embedding endpoint stood up beside the app;
at the owner's request it became something Gatherum simply does. `LocalEmbedder` runs a
23 MB int8 MiniLM in-process on ONNX Runtime with `Microsoft.ML.Tokenizers` for WordPiece,
chosen over bge-small because a single global `MaxDistance` needs a gap between right and
wrong answers and only MiniLM leaves one. The weights are fetched by an MSBuild target
into a gitignored `models/`, hash-checked, and baked into the image in their own Docker
layer — never committed, never downloaded at run time. Passages are embedded one at a time
rather than batched, because quantized activations are scaled per tensor and batching
would put queries and documents in different regimes. `AddEmbedding` now picks exactly one
embedder — endpoint, else packaged model, else nothing — and the defaults were retuned to
the model that ships (384 dimensions, `MaxDistance` 0.8, 800-character passages). The
container publishes against a single runtime identifier, since ONNX's other platforms are
most of a gigabyte of dead weight.

### Shoulds — status (updated again)

- Tag pages with autocomplete: **superseded** — categories, with path autocomplete.
- Drag-and-drop reorder/reparent, public share links, export: still TODO.
