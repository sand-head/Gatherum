# Status

As of collectible lists — a `:::collection` fence makes a shared list something a group
works rather than reads, with each person's ticks a page of their own. Before that:
search runs a full-text half and a meaning half and fuses their rankings, with the
embedding model that powers the second half shipping in the box, and the taxonomy (which
replaced tags) files a page under a subject rather than labelling it with one. Everything listed as working has
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
  losslessly through `GatherumMarkdown`. A Source toggle swaps to `EditorView` with
  the Markdown lexer; code/config/text files edit in `EditorView` with syntax
  highlighting. Uploaded `.docx` files open in the same `DocumentView` through
  `DocxConverter` (Full profile — underline, color, alignment survive), saving real
  docx bytes with the same autosave collapse; their search text is the canonical
  Markdown rendering, so docx mentions backlink like pages (all verified end-to-end,
  including from the WebAssembly home). Autosave with indicator, "Link node…" mention insertion as real link
  runs, version history with restore (an old Markdown version previews through
  `DocumentHtmlView` — the same document as HTML rather than as a canvas, so it is
  findable with Ctrl+F, selectable, printable, and its pictures are the browser's).
  Every interactive component — editor, tree, search box, node header, the category
  bar at the foot of a page, versions, file view, settings keys — is an Interactive
  Auto island in `Gatherum.Client` over one `IAppData` seam (services on the server
  circuit, HTTP under WebAssembly): the first visit renders on the server while the
  runtime downloads, and every visit after runs fully in WebAssembly with zero websockets
  (verified in-browser: editing, autosave, rename, categories, history, restore, search,
  keys, stale-version warning all exercised in the WASM home). The only JavaScript in
  the app is a ~30-line interop file.
- **The wiki's own syntaxes** — pages speak a dialect the editor is handed per call and
  never learns (`GatherumMarkdown`, the one door every read and write goes through):
  `[[Wiki links]]` and `[[Target|label]]`, resolved by title to a node the writer can
  see — they make link rows, so they backlink exactly like `node://` mentions, and a
  title nothing answers to inks red and offers to write the page on click; `:::infobox`
  and `:::figure` fences, floated at a margin with the prose wrapping past them, dressed
  with the app's own card (a tonal fill inside a rounded hairline, the title in the
  accent its chips wear) and Wikipedia's 0.88 em small print (a figure's picture
  carries its caption as the image block's own text — `![caption](url){width=300
  align=center}`, pandoc's attribute form, with the paragraph-under-the-picture spelling
  still read — so picture and caption select, delete and render as one unit, a real
  `<figure>` in HTML); `> [!NOTE]`-style callouts in GitHub's five kinds, tinted in each
  kind's accent; and tables that keep their delimiter row's `:---:`/`---:` column
  alignments, every row at once. Article section titles wear the app's serif in both
  renderers — Liberation Serif ships with the app, registered for the canvas and
  `@font-face`d for the browser from the same files — and every heading folds its
  section away behind a chevron, the same one in both surfaces since slopedit 2.6.0
  made the canvas disclosure the read view's own (down says "expand", the hairline
  runs under it). Folding is view state: no version, no serialization, no collab op;
  the caret entering a hidden region unfolds it, and a Contents jump reveals its
  target first. An aside's own heading is not a section — no chevron on an infobox,
  and a card no longer ends the article section it sits in (slopedit 2.6.2). An
  Insert menu writes the fences (the document editor can't type one into being), the
  node picker doubles as a wiki-link and figure chooser, and links now go somewhere at
  all: a mention or an embedded file opens its node, a wiki link resolves by title, an
  external scheme leaves the app — with any pending autosave flushed first. All of it
  is plain Markdown in the file: opening a page and saving it back changes nothing but
  the edits (verified in-browser against the running app, light and dark, and by
  round-trip tests).
- **Collectible lists** — a `:::collection` fence makes a list something a group works
  rather than reads. The page declares the catalogue; each participant's ticks live in a
  page of their own under their own root, written by nobody else. The catalogue's audience
  is the grid's audience — whoever may read the list sees every column on it, so ticking
  is joining in and there is no second gesture to publish your own answers — while a
  tally's own access still governs its *page*, so a column in the grid is not a licence to
  open the file behind it, find it in a tree, or search it up.
  Items are lines of text; linking a page for one is optional and makes it rename-proof,
  and promotion is lossless, so ticks made before it keep counting. Variants nest one
  level and are optional per item, because rosters are ragged — which also makes every
  count a count of collectibles rather than of lines. A rename orphans the ticks it
  stranded: they stay in the file and the grid tells their owner so. Read view only, through slopedit
  2.7.0's widget blocks — the canvas keeps the source, so a tick is never an edit — and
  signed out reads rather than ticking. Over REST and MCP too (`collection_status`,
  `mark_collected`). See COLLECTIONS.md.
- **Awareness** — heartbeat presence ("Sam is editing", verified cross-user) and a
  newer-version warning in the editor (verified: fires when another user saves the
  open document). Concurrent saves are serialized per node; nobody's save is ever
  lost — it's a version.
- **Categories, not tags — and each one is a page** — subjects rather than nodes, and
  `NodeCategory` is the taxonomy's only relation: an edge to a category files a page under
  it, an edge from one category to another *is* a subcategory. So a page filed under
  `Podman` is a member of `Homelab` too when Podman is filed there, the parent lists it
  (`?deep=true`), a search for either name finds it (the whole ancestry goes into the
  node's search text), and "Similar" scores a shared category above a shared corner of the
  taxonomy. A subject can sit under two parents at once. Categories are created by being
  used, spelled case- and whitespace-insensitively, and maintained — rename, re-nest,
  delete — as the pages they are, with their members' search text and sidecars following
  along. Deleting one frees its pages and promotes its subcategories rather than taking
  them with it, because they are pages too. A category whose only members are private to
  the other user isn't listed at all. `/categories` browses the taxonomy and
  `/categories/<Name>` is the category's own page; the bar at the foot of a node files and
  unfiles it. Tags are gone.
- **Tree + search** — sidebar tree mixing all nodes (create, rename, delete, menu
  move up/down/move-to, drag-drop upload), one header search box — Ctrl/⌘-K puts the
  caret in it and the matches float under it, with kind badges and
  snippets — Postgres FTS with `websearch_to_tsquery`, title ranked above body; a
  photo or a recording is findable by what a model read, heard, or made of it.
- **Semantic search** — on out of the box: a 23 MB int8 MiniLM ships with the app and
  runs in-process on the CPU (~6 ms a passage), so nothing has to be stood up for search
  to answer by meaning. `Gatherum__Embedding__Endpoint` replaces it with a better model
  you run; `Local=false` with no endpoint turns embedding off entirely. Every node's text is cut into passages,
  embedded with its title, and stored in pgvector behind an HNSW index; a search runs
  that KNN beside the tsvector query — under the same visibility filter, so a private
  subtree is filtered in the database — and fuses the two rankings by position rather
  than by score. A hit only the vector half found is snippeted from the passage that
  matched. Nodes are re-embedded by a sweep over a fingerprint the database computes, so
  an edit, a transcript landing hours later, and a category rename three levels up all
  re-embed without anything having to enqueue them; a passage's vector is keyed by the
  hash of what was embedded, so editing one paragraph of a long page re-embeds one
  paragraph. `Similar` scores the same likeness alongside categories and links. REST and
  MCP take `mode=hybrid|text|semantic`. The column's width comes from configuration at
  startup, so changing models is an env var and a re-embed, not a migration. A model that
  is unconfigured, unreachable, or slower than `QueryTimeoutMs` yields a full-text answer
  — never a failed search.
- **Files** — upload via picker or drop, previews (image/PDF/video/audio/text),
  description, categories, referenced-by, per-version download; extraction: text verbatim,
  PDF (PdfPig), image metadata (MetadataExtractor); media types resolved sensibly
  when browsers upload code as octet-stream.
- **Bookmarks** — a URL captured as a file node, on request and never on a schedule.
  `BrowserPageArchiver` (Playwright over the container's Chromium, honoring
  `HTTPS_PROXY`, `Gatherum__Bookmarks__BrowserPath` to point elsewhere) loads the page,
  lets scripts run and settle, scrolls lazy loading into existence, records every
  response off the wire, and keeps the post-JS DOM as one self-contained sanitized
  snapshot (stylesheets, images and the fonts/backgrounds their CSS names inlined from
  the recorded bytes under a byte purse; scripts, frames and handlers stripped;
  references absolutized; source URL and capture time stamped in a first-line comment) —
  verified end-to-end against a JS-only page through the API. Rendered sandboxed in the
  file view, findable by the page's words and by its address, "capture again" records a
  fresh capture as a new version. Non-HTML URLs (a PDF) are kept as the document they
  serve, and a missing or failing browser degrades to `HttpPageArchiver`'s plain fetch
  with a logged reason — never to a failure the browser alone caused. The source URL
  lives on `FileBody` and in `meta.json`, so it survives a reindex. The node wears a
  bookmark bar: source link, a Capture again button, and a capture picker that pages
  the sandboxed frame back through older captures (restore/download stay in the History
  panel). To search and to MCP a bookmark is its Markdown rendering — `HtmlMarkdown`
  converts the whole captured body (headings, lists, links, tables, quotes, fenced
  code; inlined images reduce to alt text) and `HtmlTextExtractor` stores it as the
  version's text, the convention docx set — so `get_node` hands agents structured prose.
  New: `POST /api/bookmarks`, `POST /api/bookmarks/{id}/capture`, MCP
  `bookmark_page`/`capture_bookmark`, and `text/html` served inline is CSP-sandboxed.
- **Citations backed by the archive** — a citation is a footnote whose note cites a
  node, in the vocabulary the dialect already had: a mention labeled with the bare
  title and, when the cited node is a bookmark, the day of its newest capture and the
  source's own address — `[^1]: [Title](node://id), captured 27 August 2026 —
  [example.com](https://…).` — so the claim points at the capture Gatherum keeps
  rather than at a URL that may rot. No new syntax: the note is one plain Markdown
  line, the mention backlinks (a bookmark's backlinks answer "what cites this?") and
  locks for a reader who may not open it, and the server's link pass sees it without
  knowing footnotes exist. *Insert… → Cite…* in the editor cites any node picked from
  search; a pasted URL is offered as *Capture and cite* — captured as a bookmark under
  the citing page, then cited, in one motion. Document mode only, like Footnote,
  because the model picks the key and places the marker. The manual's dialect page
  teaches the convention and `agents.md` tells models to `bookmark_page` first rather
  than paste a bare URL.
- **Multimedia analysis** — optional, off unless `Gatherum__Analysis__Endpoint` names an
  OpenAI-compatible model you run. Still images are read (OCR), audio and video are
  transcribed (video split by ffmpeg into its audio track and sampled frames), and each
  gets a short summary; transcript and summary both feed FTS and both come back over
  REST and MCP. Runs on a background worker after the upload returns, one file at a
  time, resumed by a startup sweep; reused when the same SHA-256 turns up again;
  failures land on the version with their reason and never touch the stored bytes.
- **Links** — mentions (`[@Title](node://id)`), `[[wiki links]]` and embedded files
  parse straight from Markdown into link rows; backlinks everywhere; all three are
  clickable in the editor (Ctrl/⌘+click while editing, a plain click while reading).
  A page may link a node its reader may not open — a public page pointing at its
  author's private file is ordinary — so the reader asks which of the ids it links are
  reachable (the direct-link question, so an unlisted node answers yes) and draws the
  rest locked: greyed, padlocked with Lucide's lock, and with no target at all, an
  embedded picture becoming its own caption rather than a broken image. The page still
  says what it says; the link stops pretending it goes somewhere.
- **Auth** — OIDC-only via discovery, defensive `offline_access`, first user becomes
  admin, cookie sessions; API keys hashed at rest, revocable, shown once; dev
  auto-login only when no authority is configured.
- **REST API** (`/api`) and **MCP** (`/mcp`, all fifteen tools) — thin adapters over the
  same services; page bodies are Markdown verbatim in both directions.
- **Privacy** — per-subtree private flag enforced by one `INodeAuthorizer` in every
  read path, and a published page renders for whoever may reach it: signed out, the node
  page opens in the same read view a signed-in reader gets, with the editor and every
  affordance that writes left off.
- **Ops** — multi-stage Dockerfile (wasm-tools for the Auto island, non-root runtime; built and
  exercised against a postgres container, editor verified in-browser against the
  containerized app), compose.yaml, Podman Quadlets, `/healthz`, JSON console logs
  outside Development, migrations on startup with opt-out.
- **Tests** — 342 passing: the Markdown dialect (infobox/figure/callout round trips,
  wiki-link spellings, extension composition, derived chrome, red-link inking, in-app
  URL shapes) and the same dialect as read-only HTML (the aside and its card, a
  callout's tint, a wiki link's URL, a mention that keeps its look and loses its
  target), the padlock a link the reader may not follow wears (which links are asked
  about, which are locked, the picture that becomes its caption, re-inking across a mode
  change, the allow-list the read view asks slopedit for, and an emitted anchor for the
  stylesheet to padlock with no target left in it),
  markdown links, docx extraction/editing/backlinks, tree ops, privacy,
  versions (collapse, restore, re-upload, cross-author), search, title resolution, API
  keys, storage/extraction, the taxonomy (nesting, counts, privacy, rename/move/delete,
  path spelling), collectible lists (the construct alone — declaration against tracking,
  ragged variants, notes, orphans, round trips; then the aggregate against real Postgres —
  a tally per person, a private tally that is still a column while its page stays shut to
  everyone else, a list nobody may read that has no grid to leak, a promotion whose ticks
  keep counting, a rename whose orphans are kept and reported only to their owner, and the
  parent row that refuses to be ticked), the widget seam the
  read view claims a collection through (the hole, and the whole-body writer that ignores
  it so a static export still holds the catalogue), and integration tests booting the app
  on Testcontainers Postgres
  (create page → search → MCP `get_node`; wiki link → backlink → `resolve-titles`;
  file a page in a nested category → find it from the category above, over REST and
  MCP; create a page → find it over REST by a question that shares none of its words;
  publish a page that mentions a private one → a stranger gets the page, the mention's
  text, and the answer that its target is not theirs to open; declare a collection over
  REST → tick it over MCP → the tally is a page with the mention and the tick in it, and
  a public list's first response carries the grid itself with no checkbox for a visitor
  who is not signed in).
  The packaged model is tested as it ships (shape, normalization, determinism,
  batch-invariance, that it tells two subjects apart, and that the shipped `MaxDistance`
  falls between kin and strangers). Everything *around* semantic search is tested against
  a `FakeEmbedder` that behaves like a very small model — hashed words for ordinary text, declared subjects for words that mean the same
  thing — because a real model's answers are approximate and an assertion about ranking
  made against one is a coin toss.

- **A manual in the box** — eleven pages (`src/Gatherum.Web/Docs`) embedded in the
  assembly and served at `/docs`: what a node is, the Markdown dialect in full,
  categories, collectible lists, search, sharing, the REST API, the MCP server, a
  briefing for agents, and configuration. Also served as their own source — `/docs/<page>.md`, `/docs/all.md`,
  `/docs/llms.txt` — because the dialect is syntax no model has seen and a link is how
  you teach it one. Unauthenticated (it describes the software, not the instance) and
  under the anonymous read budget. Each page's outline goes in the sidebar rail through
  a layout section, beside the rest of the manual, the way a wiki page's contents do.
  `DocsTests` checks every internal link lands and that the dialect page still names
  every callout kind and aside the code implements.

## Stubbed / not shipped (tracked in PLAN.md)

- Drag-and-drop *reorder/reparent* in the tree (menu move ships).
- Public read-only share links.
- Export endpoints (bodies are already Markdown files on disk; export is a tree walk
  + zip stream).

## Known gaps

- A `[[wiki link]]` naming a page the reader may not see inks red, not locked, and
  offers to write it. That is on purpose: a wiki link is a search by title, and
  answering "that one exists, it just isn't yours" would hand out the existence of a
  private page to anybody who guessed its name. A mention carries the id already, which
  is why it can wear the padlock instead.
- Only the read view locks. The editor draws a link to a node its author cannot see as
  an ordinary link — locking rewrites runs, and a document that saves has to write back
  the bytes it was read from. It costs a co-editor a click into a 404 on somebody else's
  private file, which is the small side of that trade.
- A signed-out visitor reaching a published *file* (an image, a PDF — anything with no
  document form) still lands on `FileView`, which offers a description box, an upload
  button and a History control that the API refuses. They are refusals, not leaks, but
  they should not be drawn for somebody who cannot use them.
- Typing `[[A Title]]` or a `:::infobox` fence *into* the document editor leaves it as
  the text you typed until the page is read again — the extensions read source, and an
  open document is past that. The Insert menu and the node picker create both directly,
  Source mode types them as source, and a reload turns hand-typed ones into the real
  thing.
- Inserting a construct clears the undo stack: the page is written back out around the
  snippet and read again (`GatherumMarkdown.Reload`), the same price the Source toggle
  has always paid.
- Asides don't nest: a construct inside one degrades to the vocabulary it is made of.
- Two asides with nothing but a blank line between them overlap: slopedit places a float
  at its own flow position and floats don't clear one another the way a browser's do.
  Prose between them (or one at each margin) is the fix — verified in-browser, and it is
  upstream's call, not the host's.
- Bold inside a callout's title is absorbed by the title's own bold; everything else in
  a title (links, code, emphasis) round-trips.
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
- Semantic hits are filtered for visibility *after* the HNSW index has picked its
  neighbours, so a search whose nearest passages all sit in the other user's private
  subtree can return fewer semantic results than it could have. The query over-fetches to
  make that unlikely at two people's scale; it is not a proof.
- The packaged model is small and quantized. It is a real embedding model, not a toy, but
  a large one you run yourself will beat it — which is what the endpoint setting is for.
- Passages are embedded one at a time rather than batched, because quantized activations
  are scaled per tensor and batching would make a vector depend on its neighbours. That
  costs about 1.5× the wall clock on indexing.
- `MaxDistance` is one number for the whole tree. A model whose distances run tighter or
  looser than the default needs it retuned by hand — there is no calibration pass.
- Results are fused, not re-ranked: nothing reads the passages back and reconsiders them.
  A cross-encoder would rank better and would cost a model call per result.
- Postgres must now carry pgvector. The shipped images do; a hand-rolled Postgres needs
  the extension installed and a superuser for the first migration.

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
