# Decisions

Deviations from the brief and judgment calls worth remembering. Newest last.

## Core references EF Core + Npgsql directly
The brief forbids speculative abstraction, and Postgres full-text search is an
architectural commitment (tsvector columns, `websearch_to_tsquery`), not a swappable
detail. So application services in `Gatherum.Core` use `GatherumDbContext` directly and
Core references the Npgsql provider. Infrastructure keeps what genuinely has alternative
implementations: storage, extraction, collab persistence — plus the migrations assembly.

## Development auto-login when OIDC is unconfigured
OIDC-only auth means a bare `dotnet run` would be unusable without an IdP. When
`Gatherum__Oidc__Authority` is unset, `/auth/login` signs in a local "Dev User" and the
app logs a warning at startup. Production deployments set the OIDC env vars, which
disables this path entirely. This keeps "usable end-to-end via dotnet run" true without
weakening configured deployments.

## Revisions collapse within a five-minute window
"Every save creates a revision" taken literally turns autosave (every ~1.2 s of typing)
into hundreds of junk snapshots. Saves by the same author within five minutes update the
latest revision in place; a pause or another author starts a new one. History stays
meaningful and restore still works at the granularity a human would want.

## TipTap v2, not v3
The editor pins TipTap 2.27.x: the v2 line is stable, its collaboration-cursor package
matches y-prosemirror 1.x, and v3 renamed/reshuffled the collaboration packages recently
enough that the risk wasn't worth it for the MVP. Upgrade path: bump all @tiptap/*
packages together and swap `extension-collaboration-cursor` for v3's
`extension-collaboration-caret`.

## Collaboration doc is seeded by the first client
When a page opens for live collab and the persisted Yjs state is empty, the first
browser to sync seeds the doc from the stored TipTap JSON. Two people racing to open a
never-collaborated page within the same instant could double-seed; accepted for a
two-person MVP because the window is milliseconds and every later open sees non-empty
state. Server-side seeding (building the XmlFragment via YDotNet in C#) is the upgrade
if it ever matters.

## External page edits reset collab state
`update_page` (REST/MCP) and revision restore write the body and delete the persisted
Yjs state, so the next editor session re-seeds from the stored content. Clients editing
live at that exact moment keep their in-memory doc and their next autosave wins. For two
users this is a understandable last-writer-wins corner; the richer fix (applying external
edits as CRDT transactions server-side) has a clear seam in `SavePageAsync`.

## Saves are serialized per node, in process
Concurrent autosaves from two live editors raced on revision numbers and link rows.
`SavePageAsync` takes a per-node semaphore. Process-wide is sufficient because Gatherum
deploys as a single container; scaling out would move this to a database lock.

## Pages are Markdown files (owner direction)
The unification got completed from the other side: instead of a page body table, a
page is a node whose file version is `text/markdown` bytes in the content-addressed
store. Kind is derived, `FileVersion` is the only history mechanism, and "revision"
and "file version" stopped being different things. Uploaded `.md` files and in-app
pages are indistinguishable — which is the thesis, applied to itself.

## No JavaScript: slopedit as the editor (owner direction)
TipTap+Yjs (and with them YDotNet, npm, esbuild) were replaced by
[slopedit](https://git.sand.town/sand_head/slopedit), the owner's from-scratch C#
editor on a SkiaSharp canvas. Because SkiaSharp.Views.Blazor renders only under
WebAssembly, the app moved from global Interactive Server to per-component render
modes: static shell, server islands for chrome and pages, one WASM island for the
editor. The editor talks to the server over /api with cookie auth. Remaining
JavaScript: one ~30-line static file for the Ctrl-K shortcut and drag-drop upload.

## Live collaboration downgraded to presence + versions (accepted trade)
Dropping Yjs means no shared cursors and no character-level merging. In their place:
heartbeat presence ("X is editing"), a newer-version warning in the editor, and
last-writer-wins where the loser's save survives as its own version (different
authors never collapse into one version). Chosen knowingly over keeping a JS editor
island; the seam for something richer later is SaveTextAsync.

## Raster canvas, not WebGL, for the editor
SKGLView (SkiaSharp 4.148) throws in Dispose when the editor island unmounts during
enhanced navigation, and an unhandled renderer exception kills the WASM runtime for
the rest of the session. The raster SKCanvasView tears down cleanly, so EditorPane
sets ForceRaster. Revisit when SkiaSharp or slopedit guards the WebGL dispose path.

## The editor moved into the server circuit (slopedit 1.7.0)
slopedit gained Interactive Server support: EditorView paints with native SkiaSharp on
the server and streams PNG frames to a plain canvas over the existing circuit, keeping
the client-side input surface. Gatherum dropped its WebAssembly island: Gatherum.Client
is gone, the app is back to one global Interactive Server render mode, EditorPane calls
the application services directly instead of going through /api, presence reads the
tracker in-process, and wasm-tools/python3 left the toolchain and Dockerfile. The
known costs, accepted: every keystroke and frame crosses the circuit (fine on a LAN or
decent link for two users), and offline editing is off the table. The two prior
entries about the WASM island and the raster-canvas workaround are superseded — Server
mode is always raster by design, and the SKGLView dispose bug can't reach us here.

## slopedit 2.0: DocumentView is the page editor
Pages now edit in slopedit's rich document editor — proportional layout, styled runs,
formatting as the model — with `MarkdownSerializer` moving content in and out of the
Markdown-profile document, which round-trips losslessly by construction. A Source
toggle swaps the same content into `EditorView` with the Markdown lexer; code and
other text files stay in `EditorView`. Mentions insert as real link runs (insert the
label, select it, `SetLink`). Images resolve through the editor-data seam so only
in-app `/api/files/…/content` URLs load — external images stay placeholders.

## Interactive Auto is wired, but resolves to Server on today's pages
The editor island runs `@rendermode="InteractiveAuto"` from Gatherum.Client, with one
`IEditorData` contract implemented twice: direct services on the server circuit, HTTP
in WebAssembly. The WASM runtime downloads and caches — but Blazor's Auto mode matches
the render mode already interactive on the page, and Gatherum's chrome (tree, search,
header, versions) are Interactive Server islands, so the editor renders in the Server
home on every visit. Local WebAssembly rendering would require converting the whole
chrome to WASM-capable components (HTTP data flow throughout); recorded as an open
follow-up rather than done silently. *(Since done — see "The whole chrome went
Interactive Auto" below.)*

## Blazor's 32 KB hub cap kills tall documents — raised to 2 MB
The circuit died silently (no server log, "connection closed with an error" in the
browser) whenever a document was tall enough — first seen with a wrapping heading.
Root cause: slopedit's editor interop exceeds SignalR's default 32 KB client→server
`MaximumReceiveMessageSize`. `AddHubOptions` raises it to 2 MB. Upstream note for
slopedit: hosts need this documented (or the payload chunked); the failure mode is
brutal to diagnose because nothing reaches the server logs.

## docx editing is blocked on the SlopEdit.Docx package
The `.docx` media type is mapped and the dispatch seam is ready, but SlopEdit.Docx
2.0.0 is not on the package feed yet (only Blazor/Core/TreeSitter are). When it
publishes: reference it, add a `DocxConverter.ToRichDocument/FromRichDocument` case
beside the Markdown one in NodeEditor, and a docx text extractor via
`DocxConverter.ToMarkdown` for search. *(Unblocked — see the next entry.)*

## docx is a document format, same editor as pages
SlopEdit.Docx 2.0.1 landed on the feed, so an uploaded `.docx` now opens in the same
`DocumentView` pages use — `DocxConverter.ToRichDocument` on the way in (Full
capability profile: underline, color, alignment survive), `FromRichDocument` on the
way out, running under WebAssembly like the rest of the editor. The body stays real
docx bytes in content-addressed storage; there is no hidden markdown shadow copy.
Three consequences follow from that: saves go through a new binary door
(`FileService.SaveBinaryAsync` beside `SaveTextAsync`, same autosave collapse, wired
as `PUT /api/binary/{id}`); search text comes from a `DocxTextExtractor` that emits
the converter's canonical Markdown rendering — what the editor can show is what
search can find; and because that extracted text *is* Markdown, mentions inserted in
a docx round-trip as real docx hyperlinks and register backlinks exactly like pages.
No Source toggle for docx — its source is a zip, not text. Conversion is lossy by
slopedit's design (exact fonts/spacing and embedded media don't survive; images
become visible placeholders), which fits a wiki: honest content, not print fidelity.

## The whole chrome went Interactive Auto — the app now renders in WebAssembly
Blazor's Auto mode matches whatever render mode is already interactive on the page,
so as long as any island was Interactive Server, the editor could never go local.
The fix was to leave no such island: the tree, search palette, node header, tag
editor, version panel, file view, and settings key panel all moved into
Gatherum.Client as Interactive Auto components, and the editor's `IEditorData` seam
widened into `IAppData` — one contract covering everything the interactive UI needs,
implemented twice (`ServerAppData` over the application services, `HttpAppData` over
`/api`). First visit still renders on the server circuit while the runtime downloads;
every visit after that runs fully in WebAssembly with **zero websockets** — presence,
autosave, and the stale-version warning all flow over HTTP. The tags page lost its
interactivity requirement entirely and became static SSR. Two consequences worth
naming: `MarkdownRender` (server-side Markdown→HTML) is gone — the version panel now
previews old Markdown in a read-only `DocumentView`, so previews look exactly like
the editor; and the file-upload endpoints raise the request body cap to the same
512 MB the pickers promise, because WASM-home uploads arrive as multipart HTTP
instead of streaming over a circuit.

## The redesign: scoped CSS per component, tokens via light-dark(), a JS-free-ish theme toggle
The UI went from one global `app.css` to Blazor CSS isolation: every component owns a
`*.razor.css` next to it, written in native modern CSS (nesting, `color-mix()`, pill
`100vmax` radii), and `app.css` shrank to the design system — color tokens, type, and
base element styles. The look is "what if Google made Wikipedia": Material-style chrome
(tonal ground, the content pane as a floating rounded sheet, pill search box and
buttons, chips, state-layer hovers, the four-hue node-graph mark) around
Wikipedia-style articles (serif titles over a thin rule, calm reading measure, blue
links). Theming is one mechanism, not two stylesheets: every color token is a
`light-dark()` pair resolved through `color-scheme`, so OS preference works with zero
extra rules and the sidebar toggle just sets `data-theme` on `<html>`. The toggle's
logic lives in `gatherum.js` (localStorage + a delegated click handler — genuinely not
Blazor-doable, since it must run before any circuit exists and paint-stable across
enhanced navigation), loaded from a two-line inline module in `App.razor`'s head.
Two isolation sharp edges worth remembering: elements rendered by child components
(NavLink's `<a>`, InputFile's `<input>`) never carry the parent's scope attribute, so
their state classes need top-level `::deep` selectors — nested `&.active` gets the
scope attribute appended and silently never matches; and shared primitives used across
components (buttons, inputs, `.category` chips, `.upload-label`, `.document-surface`) stay
global in `app.css` rather than being duplicated per scope.

## The sidebar became reading context; the tree moved to /pages
The sidebar no longer mirrors the whole tree — a hierarchy crammed into 290 pixels
got thinner and less useful with every node added. It now shows three
Wikipedia-flavored sections: **Contents** (the open article's headings),
**Similar** (related articles), and **Recent** (last visits) — with the full tree,
drop-zone, row menus and move modal unchanged, promoted to its own `/pages` page
behind an "All pages" footer link. Decisions worth recording:
- **Contents comes from the editor island, not from parsed HTML** — slopedit renders
  to canvas, so there are no heading anchors to scroll to. The editor publishes its
  heading blocks into a scoped `OutlineState` (the same cross-island pattern as
  `TreeState`), and a click travels back as a jump request the editor answers with a
  caret move plus `RevealCaret()`. No JavaScript involved. `OutlineState` tracks its
  publisher because, when navigating between two articles, the outgoing editor
  disposes *after* the incoming one has published and must not wipe the new outline.
- **Similar is scored in `NodeService`**: one point per shared tag, two for a body
  link in either direction — a deliberate mention beats a shared label — ties to the
  most recently updated. Visibility is resolved before scoring so a private node's
  tags never leak into the other user's ranking.
- **Recent lives in localStorage** (`gatherum-recents`), written by the sidebar
  island through plain `localStorage.getItem`/`setItem` interop — no addition to
  `gatherum.js`, because none of it needs to run before Blazor exists. Titles are
  refreshed on every visit, so renames self-heal, and entries whose node has been
  deleted (or made private by the other user) drop out on their next failed load.

## The editor canvas follows the theme
slopedit paints with SkiaSharp, so the `light-dark()` tokens could never reach the
document surface — it shipped hard-coded to a VS-dark palette and sat as a black slab
inside the light theme's white sheet. Now `EditorThemes` (Gatherum.Client) restates
the app.css tokens as `SKColor` palettes — surface, ink, selection, caret, dim
markers, link blue — applied three ways: `EditorTheme` on the views, ink colors on
each `RichDocument`, and a VS-light `SyntaxTheme` for source mode (dark keeps
slopedit's default; each kind's default flags are copied over so behaviors like the
link underline survive). Which mode is in effect comes from a new `watchTheme` export
in gatherum.js — a MutationObserver on `data-theme` plus the OS preference's change
event, neither reachable from Blazor — feeding a scoped `ThemeState` that the editor
and version-preview islands watch; a toggle repaints the open canvas live, no reload.
The palette is deliberately duplicated (CSS tokens and SKColors) rather than read
from CSS at runtime: reading computed styles would need more interop for a set of
values that changes about never — change a token in app.css, change it in
EditorThemes.

## The wiki's syntaxes live in Gatherum, not in slopedit
slopedit 2.1 added a per-call extension seam to its Markdown container rather than
learning `[[links]]` and infoboxes itself — it edits code as often as prose. Gatherum
takes it up in `GatherumMarkdown`, which is the *only* place in the app that turns a
page into a document or back: one extension list (`WikiLinkExtension`, `AsideExtension`,
`CalloutExtension`) passed to both `FromMarkdown` and `ToMarkdown`, so the round trip is
lossless by construction. Every surface that reads a page — the editor, the Source
toggle, the version preview — goes through it, because a document parsed without the
extensions would write the syntax back out as prose and quietly destroy it.

## Chrome is derived from block tags, not pinned at parse
slopedit's `BlockDecoration` and `FloatedRun` are declared against block indices, and the
sample host declares them while parsing. That is fine for a document nobody edits; it is
wrong for an editor, because typing a paragraph above an infobox moves every block under
it and the card stays where it was. So the extensions only *tag* the blocks they own
(the tag is the fence's own argument line, which is also how the writer finds the run
again), and `DocumentChrome.Apply` recomputes floats and decorations from those tags
after every change and every theme switch. It re-declares only when the derived set
actually differs, so a keystroke that moves nothing costs a comparison. The same pass
inks a callout's title, which is why the extensions can stay colorblind: what a
construct *is* belongs to the source, what it looks like belongs to the mode. The tag
carries a `#n` instance marker for the same reason a run needs a boundary: two warnings
in a row, or two infoboxes, are the same words twice, and "the same construct" is
"blocks whose tag is equal" — without the marker the second one's opening line would be
eaten on the way out.

## A wiki link resolves by title, through its writer's eyes
`[[Homelab]]` names a page instead of pointing at one, so `NodeService.ResolveTitlesAsync`
matches case-insensitively and — titles not being unique — breaks a tie on the exact-case
match and then the oldest node, so the same name always lands on the same page. It
resolves against what the *writer* can see, which is what keeps a private subtree from
being discoverable by typing its title: an unresolvable link is simply red. Mentions
(`node://id`) stay the stronger form — they survive a rename — and both make link rows,
so backlinks don't care which spelling was used.

## Inserting a construct re-reads the page
There is no way to type a `:::infobox` fence into being in the document editor: the
extensions read *source*, and an open document is past that. The Insert menu therefore
writes the page back out around the snippet and reads it again through
`GatherumMarkdown.Reload` — into the same `RichDocument` instance, because the view, the
caret and the event subscriptions are bound to it. The cost is the undo stack, which is
why these are menu items rather than keystrokes, and why the caret is put back at the
construct afterwards. The Source toggle has always paid the same price.

## Callouts are GitHub's five, not the five the docs invented
MCP.md had been promising `> [!info]`-style callouts (info, note, tip, warning, danger)
since the MVP; nothing rendered them. The implementation went with GitHub's actual
alert vocabulary — note, tip, important, warning, caution — because that is what people
paste in from elsewhere and what other renderers understand, and the docs were corrected
to match. A quote whose first line names anything else stays an ordinary quote.

## Analysis is a second seam, not a fourth text extractor
`ITextExtractor` looked like the obvious home for OCR and transcription — it already
turns bytes into search text, and first-claimer-wins already routes by media type. It
was the wrong home for one reason: extraction runs *inside* the upload request, awaited
before `SaveChangesAsync`. PdfPig answers in milliseconds; transcribing an hour of video
does not, and no upload should hold a connection open while a model thinks. So
`IMediaAnalyzer` is its own seam with its own contract — it returns a transcript *and* a
summary rather than one string, it is expected to fail, and it runs on
`MediaAnalysisWorker` after the bytes are already committed. The rule that seams need a
stated second implementation is met by the shape of the thing: a local whisper.cpp
sidecar and a hosted API are both drop-ins behind it, and swapping engines must not mean
touching `FileService`.

## The engine is one OpenAI-compatible endpoint you run yourself (owner direction)
Gatherum's premise is that the knowledge base is *yours*, and a per-node privacy flag
means little if uploads are shipped to a vendor to be described. The owner runs
llama.cpp with an any-to-any model, so `OpenAiMediaAnalyzer` speaks exactly one wire
format — `/chat/completions` with multimodal content parts — and covers reading an
image, hearing a recording, and writing both summaries through it. Media is inlined as
base64 rather than referenced by URL: a local runner has no route back to us, and
inlining is what keeps the bytes on the machine they were uploaded to. With no endpoint
configured no analyzer is registered at all, so an unconfigured Gatherum behaves exactly
as it did before any of this existed.

## Transcript and summary are indexed side by side
They answer different questions and neither substitutes for the other. The transcript is
verbatim, so it answers the exact phrase someone remembers seeing on a whiteboard or
hearing forty minutes into a call. The summary is a description, so it answers the
subject nobody ever said out loud — the photo of a server rack that contains no word
resembling "server rack". Both land in `Node.SearchText` and both are asked for
separately, in two calls with two prompts: a model asked for the words *and* the gist at
once returns the words paraphrased into the gist, and the whole point of a transcript is
that it is exact.

## Derived text keys off the hash, so it is paid for once
A model's answer belongs to the bytes, not to the version row, so re-uploading a file
that is already in storage, or restoring an old version, copies the transcript instead of
re-earning it — `PlanAnalysisAsync` looks for a completed version with the same SHA-256
before ever queueing. That falls straight out of content-addressing, and it is what makes
a restore a row insert here too.

## Switching analysis on reaches backwards
An endpoint configured today would otherwise only ever describe tomorrow's uploads, which
makes the feature feel broken for the photos already in the tree. The worker's startup
sweep therefore marks the *current* version of every claimed node Pending
(`BackfillExisting`, on by default) and works through the backlog one file at a time.
Only current versions: history is for reading back, not for spending an afternoon of a
model on. It is a config flag because the first run against a large library is measured
in hours.

## Failed analysis is recorded on the version, not retried
A model that is down, a video with no ffmpeg to split it, a file past the size ceiling —
all of them land as `Failed` plus the message, which the file view shows and MCP returns.
No retry loop and no backoff schedule: the failures that matter here are configuration
mistakes that a retry would repeat forever, and the fix is to correct the config and let
the restart sweep pick the work back up. The upload itself is never at risk either way —
the bytes are stored, versioned, and searchable by title and category before any model is
asked anything.

## Nested categories replace tags
Tags were in the brief from the start, and they were the wrong thing for this app.
Wikipedia has no tags; Google Docs has none either. A tag is a flat label, so a wiki
grown past a few dozen pages ends up with `podman`, `quadlet`, `quadlets` and `homelab`
side by side, none of them saying that the first three are ways of talking about the
last. Finding a page was never the tags' job anyway — full-text search over titles,
bodies and file text is what answers "where is the thing about rootless units". What
was missing is the other half: *what this wiki is about*, arranged, so it can be browsed
downward from a subject rather than recalled by keyword.

So categories, and they nest. A category is identified by its path — `homelab/podman` —
because that is what a writer types and what a URL carries; `CategoryPath` is the single
place that decides that "Homelab / Podman", "homelab/podman" and " HOMELAB/podman " are
one category, and `Category.Name` keeps the capitalization of whoever created it. The
path is denormalized onto every row, which is what makes "everything under Homelab" a
prefix match instead of a recursive walk, and it is rewritten for a whole subtree when a
category is renamed or moved — along with the search text of every node underneath,
since a node's findable text is its categories' *whole ancestry* ("homelab podman"), so
searching the parent finds the child's pages.

Consequences worth stating:

- **A node is filed, not labelled.** It has one place in the node tree and as many
  categories as its subject demands. Filing under `Homelab/Podman` creates `Homelab`
  too and makes the node a member of both; a category page lists its own members and,
  on request (`?deep=true`), its subcategories'.
- **Similar counts kinship, not coincidence.** A body link either way scores four, a
  category both nodes are in scores two, a category one shares with the other's ancestry
  scores one — so two Podman pages beat a Podman page and a Backups page, which still
  beat two unrelated pages under the same root. (It replaces the old one-point-per-tag
  scoring.) Which categories touch a node's ancestry is decided over the whole taxonomy
  in memory: it is a table of dozens of rows for a two-person wiki, and that keeps the
  node query a single `Contains`.
- **The taxonomy has no owner.** Either user can file any node they can see and rename,
  re-nest or delete any category — there is no per-category permission in a wiki for
  two people. Privacy is kept where it always was, on the nodes: counts are of what the
  asking user can see, and a category whose every member is private to the other user is
  not listed at all, because the name of a category describes the pages in it.
- **Empty categories stay.** A parent with only subcategories is normal, and a category
  emptied by unfiling its last page is not garbage — it is a heading someone meant. The
  cure for a mistaken one is deleting it, which is why rename/move/delete are a first-
  class part of the surface (`CategoryTools` on every category page, plus REST and MCP)
  rather than something only a DBA can do.
- **The migration carries every tag over** as a root category of the same name, so
  nothing that was filed becomes unfiled; nesting them afterwards is an ordinary move.

## Semantic search is a second half, not a replacement
The obvious reading of "I want semantic search" is that vectors replace the tsvector
index. They must not. Full-text search is what answers a quoted phrase, an identifier, a
filename, and a `-exclusion`; an embedding model is bad at all four, because it is built
to ignore exactly the spelling those depend on. Vectors answer the other question — the
page about the thing you can describe but not name. So `SearchService` runs both halves
and fuses them, `websearch_to_tsquery` keeps its syntax, and the modes exist to ask for
one half deliberately rather than to choose a default.

## Fused by rank, never by score
`ts_rank` and cosine distance share no scale, no range, and no meaning. Normalizing them
into one number is a guess that silently decides how much a lexical hit is worth against a
semantic one, and the guess would have to be re-tuned for every embedding model. Reciprocal
rank fusion reads only positions, so it needs no such constant: a result near the top of
either list places well, and one near the top of both wins. It also degrades to exactly
the old ordering when one list is empty, which is what makes an unconfigured Gatherum, or
one whose model is asleep, behave as it always did.

## pgvector, and the Postgres image that carries it
Vectors could have been stored as `real[]` and scored in C#, which would have kept the
stock `postgres:16-alpine` image. It was the wrong trade for the same reason the tsvector
column was: this app has already decided that Postgres is where search lives, and half a
search engine in the database with the other half in a `foreach` is worse than either.
The cost is honest and one line — `pgvector/pgvector:pg16` in `compose.yaml`, the quadlet,
and the test fixture — and the migration's `CREATE EXTENSION vector` wants a superuser
the first time, which the official images' `POSTGRES_USER` already is.

## The vector column's width is a runtime setting, not a migration
pgvector puts a column's dimension in its type, and every model has its own. Pinning one
in the migration would have made "try a different embedding model" a schema change, which
is exactly the experiment the owner should be able to run with an env var. So the
migration leaves the column dimensionless, and `EmbeddingSchema` sizes it at startup from
`Gatherum__Embedding__Dimensions`, building the HNSW index that a sized column makes
possible. Changing that number drops every stored vector and clears every node's embedded
fingerprint — vectors from two models are not comparable, and blending them mis-ranks
silently rather than failing — and the worker earns them back over the next few minutes.

## A database-computed fingerprint replaces the queue
Media analysis is handed work by the upload that created it, because analysis belongs to a
version's bytes and only an upload makes those. Embedding does not work that way: a node's
vectors go stale when somebody edits it, when a transcript lands on it hours later, when
it is filed under a new category, and when a category three levels up is renamed and a
hundred nodes' search text is rewritten by one `UPDATE`. Every one of those would have
needed its own enqueue call, and the last one has no upload path to hang it on. So
`Node.TextFingerprint` is a stored generated column — `md5(title || search text)` — and a
node is stale exactly when it differs from `EmbeddedFingerprint`. The sweep reads one
index. Nothing has to remember to enqueue, because there is nothing to remember.

## Passages, not one vector per node
A single vector per node is the cheaper design and the wrong one here, where a node may be
an hour of transcribed video or a fic chapter. Averaging that into one point puts it
vaguely near everything and close to nothing, and loses the ability to say *which* part
answered. Nodes are cut into passages on their own paragraph boundaries, each passage
carrying the tail of the one before so a sentence split across a seam survives somewhere,
and each embedded with the node's title so a paragraph that never repeats the subject
still belongs to it. A passage's vector is keyed by the hash of exactly what was embedded,
so editing one paragraph of a long page re-embeds one passage, and the same text uploaded
twice is paid for once — the same content-addressing that already governs bytes and
transcripts.

## A distance ceiling, or every search finds something
KNN always returns its k nearest neighbours, however far away they are. Left alone, that
turns "no results" into a palette full of the least-unrelated pages in the tree, which is
worse than an empty list because it looks like an answer. `Gatherum__Embedding__MaxDistance`
is the cutoff past which a passage is not an answer at all. It is a property of the model
rather than of Gatherum, so it is configurable and documented as the knob to turn when
search feels too literal or starts to wander.

## The search box gets a deadline the indexer does not
Embedding a batch of passages in the background may take a minute; nobody is watching. The
same call on the search path has about two seconds before a person believes the app is
broken. `QueryTimeoutMs` bounds it, and expiry is not an error — it returns the full-text
half and logs. This is the same instinct as the analysis rule that an upload must return
before any model is consulted: a model may make a feature better, and may never be allowed
to make the app worse.

## The embedding model ships in the box (owner direction)
Semantic search that needs a second inference server stood up before it does anything is
a feature most installs will never turn on, and Gatherum is meant to be one container and
a database. So a small embedding model — MiniLM, quantized to eight bits, twenty-three
megabytes — ships with the app and runs in its own process on the CPU at about six
milliseconds a passage. Semantic search is therefore what Gatherum *does*, not what it can
be configured to do, and `Gatherum__Embedding__Endpoint` becomes an override for people
who run something better rather than the price of entry. Nothing about the privacy story
changes: an in-process model is the strongest possible version of "nothing is ever sent
anywhere". The cost is honest — an existing tree spends a few CPU-minutes embedding itself
the first time it starts after this — and `Local=false` turns it off.

## MiniLM rather than bge-small, because of the threshold
On a smoke test of four questions asked in words their answers never use, both models
ranked the right passage first. They differ in something our design cares about more.
MiniLM answered at cosine distances of 0.75 and below while putting wrong answers at 0.87
and above; bge-small answered between 0.43 and 0.56 and put *wrong* answers as near as
0.45. bge's similarities bunch into a narrow band, which is a known trait and harmless if
all you do is rank — but `MaxDistance` is a single global cutoff, and no cutoff separates
overlapping bands. MiniLM leaves a gap to put one in. It is also smaller (23 MB against
34) and faster. Four probes is a smoke test and not a benchmark; the shape of the
difference is the part worth trusting.

## The model is fetched by the build, not committed
Twenty-three megabytes of weights in git history is a permanent tax on every clone, for a
file that never merges, never diffs, and would be joined by another the first time the
model changes. So an MSBuild target fetches it once into a gitignored `models/`, checks it
against a known SHA-256, and every later build finds it already there; the Dockerfile does
it in its own layer so editing source doesn't re-download it. The trade is that a cold
build needs the network — mitigated by the two files being placeable by hand and by
`-p:FetchEmbeddingModel=false`. The *running* app never downloads anything, which is the
property that actually matters.

## One passage per inference, though the caller batches
Batching passages into one tensor is the obvious way to make indexing faster, and it was
wrong here. This model's activations are quantized with a scale computed across the whole
input tensor, so a passage embedded beside a long neighbour comes out about 0.97 cosine
from the same passage embedded beside a short one — the test written to check padding
found it. Random drift would have been tolerable; this is not random. A search box is
always a batch of one, and passages would have arrived sixteen at a time, so every query
would have sat in a different quantization regime from every document it was compared
against — a systematic error, invisible except as slightly worse results forever. Embedding
one at a time costs about 1.5× the wall clock and restores the property everything else
assumes: a vector is a function of its text and of nothing else.

## Published for one architecture
ONNX Runtime ships native libraries for Windows, macOS, Android, iOS and Linux, and a
portable publish carries every one of them — 770 MB of output, most of a gigabyte of it
binaries this Linux image can never load. The Dockerfile therefore publishes with a
runtime identifier picked from Docker's `TARGETARCH`, which prunes the rest and brings the
publish to 143 MB. Local `dotnet run` and `dotnet test` are untouched.


## Versions come from the history, not from a file
The container workflow needs a number, and the two usual sources are both bad: a
hand-edited `<Version>` drifts from what shipped, and a date stamp says nothing about
what changed. GitVersion in mainline mode reads the number off `main` itself — count
what has landed since the last release tag — so a version is a fact about the commits
an image contains, and merging is the only thing anyone has to remember to do. In
GitVersion 6 that is a *strategy* rather than the old `mode: Mainline`, which no longer
parses: `strategies: [ConfiguredNextVersion, Mainline]` on the GitHubFlow preset, with
`main` set to `ContinuousDeployment` so a merge produces a clean `0.0.8` instead of a
`0.0.8-3` waiting to be blessed. Untagged, the count starts at the root commit and main
sits in 0.0.x; tagging a commit `v1.0.0` moves the floor and the counting continues.

## The published image is amd64 only
The Dockerfile already reads `TARGETARCH` and would build arm64 correctly, but the
build links a WebAssembly runtime with emscripten and installs the `wasm-tools`
workload, and doing that under QEMU on a hosted runner turns a long build into an
untenable one. The workflow therefore publishes `linux/amd64` and nothing else. Anyone
who needs arm64 can `docker build` on an arm64 machine and get a correct image with no
changes; adding `platforms: linux/amd64,linux/arm64` to the workflow is a one-line
change the day a native arm64 runner is available.

## Reading a version is HTML; editing is still a canvas (slopedit 2.2)
The canvas exists for the things a browser will not give us: a caret, a selection,
proportional hit-testing, an IME. A version preview has none of them, and paying for one
there cost the reader everything the browser does better — Ctrl+F, screen readers, native
selection and copy, `<img>` with its own cache, and a page that prints as a page rather
than as a screenshot. So the history panel's preview is `DocumentHtmlView` and the editor
is unchanged. The two renderers are the same document by construction (same theme, same
measurer, same layout, same metrics, the same font files served to the browser), so this
is a re-render, not a second reading of the page.

Two things were deliberately not done with it. Links in the preview stay the browser's:
`wikilink:` keeps its URL and a mention's `node://` — outside the emitter's allow-list —
renders as a link with nowhere to go, exactly as the canvas painted it in a view that
routed no clicks. Wiring the editor's link routing into the panel would have copied
`FollowLinkAsync` into a second component to make *history* navigate away from itself.
And a page still opens in the editor rather than in a Read tab with an Edit button: the
component upstream suggests for one is now here, but which surface a page opens in is a
product decision, not a consequence of a package bump.

## Mobile: two measured breakpoints, a popover drawer, and reading before editing
The app had one width media query in the whole tree, and all it did was narrow the
sidebar. Measured in Chromium against the real stylesheets, that left a 390px phone
with a 104px article column — about one word per line — and `/pages` ellipsising tree
titles down to single letters. This is the pass that fixed it, and four of its calls
are worth writing down.

**The breakpoints are measured, not conventional.** 700px is where a 236px sidebar
stops leaving room for a comfortable reading measure (a 45ch line needs about 650px of
viewport with the sidebar in flow, 50ch about 690px), and 480px is where a five-column
table stops being a table. They are pixel literals in the `@media` prelude because a
custom property cannot be read from a media query; the sizes those queries *vary* —
`--gutter`, `--pane-pad`, `--sidebar-w`, `--tap` — are tokens in `app.css`, so a
breakpoint redefines a value instead of restating the rule that used it. Every width
above 700px is unchanged to the pixel, which was the constraint the whole pass was
written under.

**The drawer is the native `popover` API, not a component.** It gives the top layer,
light-dismiss, Escape and a backdrop for nothing, the way the account menu already
does, and it needs no state — which matters, because a stateful shell component would
have to be an Interactive Auto island in `Gatherum.Client` and any slip to Interactive
Server would pin the whole app back to the circuit. The cost is that `[popover]` brings
UA defaults with it and they apply whether or not it is open: without an explicit
`position: static` reset on the wide layout the sidebar is `position: fixed` at *every*
width. That reset lives with the wide-layout rule rather than in the drawer query, and
the drawer closes on navigation through the same delegated click listener in
`gatherum.js` that already closes the account menu.

**Below 700px the page scrolls, above it the pane does.** An inner scroll container
means the mobile URL bar can never collapse and the reader pays for it on every screen;
page scrolling also hands slopedit's page-mode sticky strip the document as its scroll
container, which is the ancestor it wants. Two scroll models, one breakpoint, and the
header comment in `MainLayout.razor.css` says which is which — the file's original claim
that `.content` is always the scroller is now only true above the line.

**Touch is `(hover: none)` and `(pointer: coarse)`, never a width.** A tablet is a coarse
pointer with a wide viewport. The row menu was `visibility: hidden` until `:hover`, which
put every per-node action — new page inside, upload inside, move, privacy, delete —
behind a gesture a phone does not have; it is unconditional under `(hover: none)`. Tap
targets grow their boxes, not their icons, under `(pointer: coarse)` only, so desktop
density survives. Fields go to 16px there because iOS Safari zooms into anything smaller
on focus and never zooms back out.

The one genuinely new JavaScript is `scrollToHeading`, and it is here under protest:
`DocumentHtmlView` emits `h1`–`h6` without ids while the document numbers blocks, so the
read view's Contents panel addresses a heading by its position among the emitted
headings — and Blazor has no native way to reach the nth descendant of an element and
scroll it into view.

## A page reads; editing is a URL (the Read tab, finally)
The previous entry left this parked: "which surface a page opens in is a product
decision, not a consequence of a package bump." The decision is that a page reads.

`/nodes/{id}` renders `DocumentHtmlView` and `/nodes/{id}?edit` renders the canvas. A
URL rather than a toggle for three reasons, in ascending order of how much they matter:
`NodePage` is static SSR and cannot hold interactive state anyway; the edit surface
becomes bookmarkable and the back button leaves it, which is what a wiki's Edit tab has
always been; and a static pass through the read view emits the article itself, so the
first response already carries the prose — a reader on a phone gets the page without
waiting for WebAssembly and never downloads a canvas to read one. `?edit` is a presence
check on a `string?`, not a `bool`, because Blazor binds a bool query parameter through
`bool.TryParse` and that rejects `?edit=1`.

Reading also fixes something the canvas could not: following a link while editing needs
Ctrl+click, since a plain click must place the caret, and a finger cannot Ctrl+click. The
routing itself moved to `LinkRouter`, shared by both surfaces — the four cases are the
same either way and only what follows differs, the editor having a save to flush first.
The offer to write a red link's missing page now lands where you actually discover it.

`OutlineState` needed no change: it was already written to take a publisher and to clear
only its own entries, so the reader is simply a second publisher. Images needed nothing
either — Gatherum writes `/api/files/…` into a document, so the read view's plain `<img>`
fetches them same-origin and gets the browser's own lazy-loading and cache.
`DocumentHtmlView` has no `ImageSource` parameter and does not need one.

## Layout gets a test net, and it is not in CI
`dotnet test` has no browser and CI only proves the image builds, so nothing in the tree
could have caught any of the above — every one of them was found by measuring a real
page in a real browser. `tests/mobile` is that, kept: a small Playwright harness that
seeds fixtures through the REST API (dev auto-signin, no key to mint) and then asserts,
at four widths in both schemes, that nothing overflows horizontally, that no control is
under 44px and no field under 16px on a coarse pointer, that the row menu is hover-gated
on a mouse and not on a finger, that the drawer opens and does not survive a navigation,
that every route has the `h1` `FocusOnNavigate` needs, and that reading a page renders no
canvas.

Playwright is a real new dependency and it is deliberately **not** wired into `dotnet
test` or the workflow: it wants a browser, a database and a running server, and a
screenshot diff that fails on a font hint helps nobody. It writes its screenshots for a
human to look at and compares none of them. Run it when you touch layout.

## The host does not restyle what slopedit renders
The read view briefly shipped three CSS overrides against `DocumentHtmlView`'s output —
`img` sizing, `display: block; overflow-x: auto` on `.se-table`, and an `!important`
float collapse on the aside so an infobox would stop squeezing prose on a phone. All
three are now gone, and the rule is that none of their kind come back.

The two renderers are one document rendered twice, and that is enforced rather than
hoped for: the HTML is derived from the same theme, the same measurer's line height and
baseline, the same `DocumentMetrics`, and — where a browser could not be asked to reach
the same answer — the layout's own numbers, with a reflection-driven parity suite
upstream that fails when any member of the model makes no difference to the emitted
HTML. A host reaching in to restyle the output turns a re-render into a
reinterpretation: the page would read differently from how it edits, which is the one
thing the split exists to prevent. The table override was the clearest case — `display:
block` on a `<table>` discards the `<col>` widths the emitter hands over under
`table-layout: fixed`, which *are* the canvas's slack-first column squeeze — and the
`img` rule was merely redundant, since `.se-img` already carries `max-width: 100%`.

So the mobile float problem is upstream's, and it is a real one: an aside is 280px wide
against a ~340px column at 390px, in the canvas and in the HTML alike. The fix belongs
in the layout, where one rule serves both — a float whose remaining measure falls below
something readable should not float, and the parity suite should cover it. Until then it
is a known gap (STATUS.md), not a host workaround.

`.reader-doc` is therefore a box for the view to sit in and nothing else, and the
comment in `NodeReader.razor.css` says so, because the next person to see a squeezed
heading on a phone will reach for exactly the rule that was just removed.

## Two ways a host breaks that parity without touching a slopedit rule
Not restyling the output turns out to be necessary and not sufficient. Both renderers
were still wrapping in different places, and neither cause was a rule aimed at
slopedit.

**The first was a parameter set on one surface and not the other.** The read view was
built by copying the version panel's `DocumentHtmlView` call, `ContentPadding="0"`
included — which is right for that panel, because `.history-preview` supplies its own
padding — while the editor's `DocumentView` took slopedit's default of 24px. Content
padding is not decoration: it decides the column the layout measures. 24px a side is 48
off the measure, which is the difference between "Hard / ware" and "H / a / rdware"
beside a 280px float. `ContentPadding` is slopedit's own parameter and the host is
entitled to set it — the value here is 0 on both, because the content pane already
supplies the article's margin and a phone has no width to spare — but it has to be the
*same* number on both surfaces or they are not rendering the same document.

**The second was app.css doing what app.css is for.** `DocumentHtmlView` emits bare
`<code>`, `<pre>`, `<a>` and headings, and this file styles bare elements — so
Gatherum's inline-code chip landed on slopedit's code with 5px of padding a side that
the canvas knows nothing about. Two code spans in a paragraph is 20px, enough to move a
line break. Anything slopedit sets itself wins on specificity and was never at risk;
what leaked was precisely what slopedit leaves alone. The reset in app.css keyed on
`.slopedit-html-view` is therefore not styling the document either — it is refusing to,
and handing each element back to the document's own rules.

The general shape: a host controls the box the view sits in and the parameters it is
given, and nothing inside. Give both surfaces the same parameters, keep global element
styles out, and the two renderers agree line for line — verified at 390px across a page
carrying an infobox, a callout, a table, inline code and wiki links.

## slopedit 2.2.2: the phone fixes landed where they belonged
The float that would not collapse was written up here as upstream's, and it is fixed
upstream. 2.2.2 collapses a floated run back into the flow at the page's full measure
when the page cannot spare `MinBodyWidthPx` (280 by default) beside it and its gutter,
so the infobox that crushed "Hardware" to "Hard / ware" at 390px now stacks above the
prose and both surfaces show the same thing. Nothing in Gatherum configures it; the
default is the behaviour we wanted, and the version bump is the whole change.

The way it is implemented is the reason the CSS override had to come out rather than
merely be tidied. The decision lives in the layout — `IsFloatCollapsed(i)`, a function
of the current measure — and both renderers read it: the canvas stacks on it and
`RichHtmlWriter` emits `float:none;width:100%` for it. A media query in this repo's
stylesheet would have been a second opinion about where the breakpoint is, and the
Read/Edit switch would have stopped being a re-render. It also gets something a media
query could not: turn the phone to landscape and the aside comes back, because the
measure changed rather than the viewport class.

Two more things arrive with it. A table too wide for any squeeze now keeps its natural
width and scrolls inside its own band — the canvas clips and translates, the HTML wraps
the `<table>` in an `overflow-x:auto` container — which is what the removed
`.se-table { display: block }` override was reaching for and getting wrong, since that
rule discarded the very `<col>` widths the two renderers share. And the canvas learned
that a hyphen is a break opportunity, which is a wrap-parity fix that shows up most on
narrow pages, where every line end is a fresh chance to disagree.

The mobile checks gained an assertion that no aside is still floated on a phone. Gatherum
does not decide that and must not, but it is the outcome the reading experience depends
on, so a future package bump cannot quietly take it away.

## slopedit 2.3.0: code reads without a canvas too
A code file opened in the read view was a plain `<pre>` with the text in it — honest,
and the one place the Read/Edit split was still a downgrade, because the editor
highlights and the reader did not. `CodeHtmlView` is `EditorView`'s reading half and it
is now what `NodeReader` renders for anything that is not Markdown or docx.

It is a stronger guarantee than the document pair's. `RichHtmlWriter` walks blocks and
`CodeHtmlWriter` walks *cells* — which is all `SkiaTextRenderer` walks either — so both
consume the same `ICellGrid`: the same lexer output, the same soft-wrapped rows, the
same line-number labels, the same collapsed folds. Parity there is structural rather
than maintained. Feeding it the same `EditorDocument` the editor would get, with the
same `LexerRegistry.ForPath` and the same `EditorThemes.Syntax`, is the whole
integration — which is why `NodeReader` needed its `FileName` parameter back.

Two things this repo does not do, deliberately. It passes no padding: `EditorView` has
no `ContentPadding` parameter at all, so `CodeHtmlView` takes its default and the pair
agrees the way it was built to. And it adds no reset for the code view, because
`CodeHtmlWriter` already emits `code { background: none; padding: 0 }` and owns the
`<pre>`'s `overflow-x`, `white-space` and the sticky `.se-ln` gutter — the horizontal
scroll of a long line is slopedit's, not app.css's. The document view is the one that
still needs the reset in app.css, because `RichHtmlWriter`'s `code` rule sets only the
face and leaves background and padding to whatever the host has lying around. That
asymmetry is worth fixing upstream; if it is, the reset here can go.

A lesson worth writing down, because it cost a red suite: **Razor does not compile-check
component parameter names.** `ContentPadding` on `EditorView` built clean with zero
warnings and threw `InvalidOperationException` at render time, on a route nothing had
visited yet. The mobile checks caught it because they now read a code file in both
modes; the build never could.

## The Edit tab moved to where a wiki keeps it
Reading a page put an Edit button at the top of the article body, floating over the
prose it acts on. Wikipedia has never done that: the title sits at the left of a band,
the article tools sit at the right of the same band, and a thin rule closes it before
the prose starts. `NodeHeader` already *was* that band — serif title, categories, the
classic rule — so Edit is a tab in it now, and `NodeReader` renders nothing but the
article.

Which surface is showing is the page's business, not the header's, so the header takes
an `EditHref` and renders the tab only when it is given one: absent while editing,
because it would offer you where you already are, and absent on a node with no editable
body. Mobile is a different shape, and Wikipedia's own skin says so: Minerva does not put a
labelled tab beside the title, it puts a compact icon toolbar — language, watchlist,
pencil — in its own band under the title's metadata, right-aligned, just above the rule
that opens the article. Gatherum does the same, with the one action it has. The header is
a grid for that reason: one DOM in reading order (title, metadata, actions), placed
beside the title where there are words to fit and in its own row where there are not.
The label goes with it, because an `<input>` cannot ellipsize — it just clips — and a
title sharing its line with a button loses its last word.

Not done, and worth naming: Wikipedia's band is *Read | Edit | View history*, and
Gatherum's history is still a toggle at the foot of the page. Moving it up would mean
`VersionPanel` giving up its own open/closed state to the header, which is a bigger
change than this one and not obviously right.

## The checks only ever saw half the app
Blazor Auto renders on the server circuit while the WebAssembly payload downloads, and
locally on every visit after. Those are different runtimes running different code, and
the mobile checks measured only the first visit of a fresh context — so every route was
tested on the circuit and the WebAssembly path was never exercised at all.

It hid a real one. `CodeHtmlView` was missing from the WebAssembly asset set, so the
read view threw `TypeLoadException` on every return visit and the infobox stayed floated
on a phone — while the suite was green, because the suite never returned. The cause was
a stale `_framework` output: `dotnet build` had happily left the previous package's
`SlopEdit.Blazor.wasm` in place across a version bump, so the reference resolved to
2.3.0 and the browser was handed 2.2.x. Deleting `bin` and `obj` and rebuilding fixed
it, and the fingerprint changing is how you can tell it worked.

Each route is now visited twice, waiting for `dotnet.native.wasm` to finish arriving
before the second navigation so nothing is cancelled mid-flight, and console errors fail
the run — with a filter for the download-cancellation noise a navigation always leaves
behind, narrow enough that the `TypeLoadException` above still fails it.

## The manual ships inside the app
Gatherum's pages are written in a dialect nothing else speaks — `[[wiki links]]`,
`:::infobox` and `:::figure` asides, `> [!NOTE]` callouts — and the people most likely to
write a page here now are models, which have never seen any of it. A README on a git host
does not solve that: it describes whatever `main` says today, not the version somebody is
actually running, and it is not a link you can hand to a fetch tool that also lands on the
instance the page is going into.

So the manual is embedded in the assembly and served at `/docs`: as pages for a person, as
`/docs/<page>.md` for whatever is reading, and as `/docs/all.md` and `/docs/llms.txt` for
something that would rather fetch once than crawl. Embedded rather than a directory on
disk, because a deployment cannot forget to copy what it does not have to copy — the
Dockerfile only ever copies `src`.

Unauthenticated, deliberately. The manual is identical in every install and says nothing
about the knowledge base it sits next to, and a model handed a link arrives holding no
session. It answers under the same per-address read budget as the rest of the anonymous
surface.

Rendered with Markdig rather than through `GatherumMarkdown` and slopedit's HTML writer,
which would have been the dogfooding answer. The writer is built for a document the editor
could open — it is the read view's other half — and a manual is a static page with tables
and anchored headings and no canvas anywhere near it. Markdig's alert blocks and custom
containers get callouts and asides close enough to demonstrate the constructs the page is
about, and the CSS restates app.css's tokens rather than `ChromeInk`'s canvas colors. The
one visible seam: an aside's side and width arguments are the editor's, and the docs
renderer ignores them.

What keeps it honest is `DocsTests`: every page has a title, every `/docs/…` link inside
the manual lands on something served, and the dialect page has to name every callout kind
in `CalloutExtension.Kinds` and both aside names in `BlockTags`. Adding a construct
without documenting it fails the suite.

## The manual's outline goes in the rail, like every other article's
The first cut of the docs page carried its own furniture: a nav column on the left, a
table of contents on the right, the article squeezed between them — while the app's own
sidebar, three feet to the left, sat empty. A page of documentation is an article, and
this app already has a place where an article's contents go.

So the sidebar takes it. `MainLayout` grew a `SectionOutlet`, and a page that has reading
context of its own fills it — the manual does, through `DocsPanels`: contents, the rest
of the manual, and the raw-Markdown links, in the same panels `SidebarPanels` renders for
a wiki page. A section rather than the layout learning which routes have panels, and it
sits outside the `AuthorizeView` so a signed-out reader gets it too. The article gets the
column to itself.

Two things this cost, both found by clicking rather than by reading:

The rail is now one scroll container holding two panel groups, because two — each
`flex: 1` inside the sidebar's column — split the height between them and clipped the
longer one. `.panels` moved to app.css at the same time: two components in two projects
wear the same rail, and CSS isolation cannot be shared, so the alternative was two copies
that drift.

And a contents entry has to name its whole path, not a bare `#id`. `App.razor` sets
`<base href="/">`, a fragment-only href resolves against the *document base URL* rather
than the current URL, and every entry quietly navigated to the site root. It is the kind
of thing that looks right in the markup, renders right, and is wrong the moment anybody
clicks it — so the integration test asserts the anchor carries its path.

## The reader tells the server which mode to prerender in
The read view is real HTML in the first response, and slopedit's HTML writer bakes a
theme's colors into the stylesheet it emits — `background:#ffffff` or `background:#1e1f20`,
not a CSS variable — because the canvas half of the same document has no variables to
read. So the prerender is painted in whichever mode the server assumed, and the server
assumed light: `ThemeState.IsDark` starts false and only becomes true once `watchTheme`
answers, which is after the island goes interactive. A reader in dark mode got a white
article for as long as that took, and then it snapped.

Nothing in a request said which mode was in effect. The explicit choice is a `data-theme`
attribute set by `initChrome` from localStorage, and the fallback is an OS preference
that never leaves the browser — so the fix is to send the answer. `gatherum.js` now folds
the two into a color and writes it to a `gatherum-mode` cookie, on load, on every toggle,
and whenever the OS preference moves; `BrowserTheme` reads it back and seeds `ThemeState`
for the render that cannot ask.

The cookie carries the *color*, not the choice: "system" is not something a prerender can
paint, and which of the two the reader picked is none of the server's business. It is a
cookie rather than localStorage's existing key for the obvious reason — localStorage does
not travel with a request — and the two coexist because they answer different questions.

That leaves the visit with no cookie yet, which is the one the complaint was actually
about. `Accept-CH` asks for `Sec-CH-Prefers-Color-Scheme` and `Critical-CH` makes the
answer arrive on *that* navigation rather than the next one: a browser that has not been
asked before retries the request once with the hint attached. Only Chromium answers;
everywhere else the cookie covers every load after the first, and a first load with
neither falls back to light exactly as before. A cookie beats the hint when both arrive —
the hint is the OS preference, the cookie is what the reader chose, and a choice outranks
a preference.

What was tempting and wrong: rendering `data-theme` on `<html>` from the same cookie, so
the whole first frame agreed. The cookie cannot tell "system, currently dark" from
"explicitly dark", so a reader on system would have had the mode pinned — visible
immediately as the wrong toggle icon, and needing a reconciliation pass in JS to unpin.
It also buys nothing: `light-dark()` already resolves against the OS preference at first
paint, and `initChrome` wins the race against the render-blocking stylesheets for the
case where it does not. The article was the only thing that could not resolve itself.

## One search box, and the results hang under it
Clicking the header's search pill used to open a command palette: a modal over a dimmed
page with a *second* input in it, which the first input then had to hand its caret to.
Two boxes for one search, and the page you were reading covered up while you decided
whether the match was the one you wanted.

`SearchPalette` is now `SearchBox`, and the pill is the input. Matches float under it in
a dropdown anchored to the field, so the page stays visible behind them and the query
stays visible above them. `Ctrl`/`⌘`-K no longer *opens* anything — it focuses the field
and selects what is in it, which is also why the shortcut now focuses the element from
JavaScript instead of calling back into .NET: on a first visit the island is still on the
server circuit, and a round trip there swallows whatever you type in the meantime.

Three things fall out of the field being real rather than a trigger:

The field is a `<label>`. Below the shell breakpoint it keeps only the magnifier it
already led with — five labelled controls do not fit 360px — and a tap on that icon
focuses the input inside it with no JavaScript and no second element. The caret is what
expands it, `:focus-within` is what draws the expansion, and `MainLayout` lets it take
the whole bar while it holds one, over the brand and the icons.

Losing focus is what closes the list, so a click on a match would close it before the
click landed. The hits refuse `mousedown`'s default instead: focus never leaves the
field, and `focusout` keeps its plain meaning of "the user is done here".

The list shows eight, where the palette showed fifteen. A dropdown under a header is for
recognizing the page you meant, not for reading a result set — and eight is what fits
without the keyboard walking the selection out of view, which is the only reason the
palette's scroller was ever needed.

## slopedit 2.5: the encyclopedia's dress, and sections that fold on a phone
Two upstream features, adopted the day they landed because both were built for this
wiki. `Dress` now gives every document `HeadingRuleLevels = 2` and
`HeadingSpacing = 1.5` — Wikipedia's hairline under h1 and h2 and its breath around
section titles, spent by the layout so the canvas and the HTML place every gap alike.
Presentation, not model: nothing about a saved page changes.

The read view also passes `CollapseSectionsBelow` to `DocumentHtmlView`: under the
floor each h2 section folds behind its heading band, Minerva-style — `<details open>`,
no script, find-in-page still reaches folded text. The floor is 480, app.css's own
"tables stop being tables" number, and it is a floor on the *article's measure* rather
than the viewport (slopedit's float-collapse doctrine): a phone folds, and so does the
pane beside the sidebar in a squeezed window, because a 440px column is cramped for the
same reason wherever it comes from. Read-tab chrome only — the editor never hides the
text the caret lives in — and `scrollToHeading` opens the `<details>` on its way so a
Contents jump cannot land on a heading with no box. Both packages (and Infrastructure's
`SlopEdit.Docx`, which had drifted to 2.2.0) now pin 2.5.0.

A third feature landed quietly, in the API rather than the README: `FloatedRun` grew
`TopMarginPx`/`BottomMarginPx`, part of the derived zone the body wraps around.
`DocumentChrome` gives asides 8px of each — Wikipedia's `margin: 0.5em 0 0.5em 1em`,
whose 1em was already the 20px gutter — so an infobox no longer touches the heading
above it or the prose that clears it.

## Footnotes and scripts came with the container, so the wiki's job was the chrome
slopedit 2.5 also made Pandoc's footnotes (`[^key]` / `[^key]: note`) and scripts
(`^sup^`, `~sub~`) native to the Markdown container — not extensions, so Gatherum's
parse/serialize/render path picked them up the moment the package bumped, wiki
extensions and all (pinned by round-trip and read-view tests). What was Gatherum's to
add: a **Footnote** item in the editor's Insert menu — a real document op
(`InsertFootnote()`), one undoable edit with the caret landing in the new note, unlike
the fence constructs' write-out-and-reread, and document-mode only because picking an
unused key is the model's job — plus the manual's word on all three, enforced by
`DocsTests` beside the callout kinds.

Looked at and left: `RichHtmlOptions.HeadingAnchors` (Wikipedia-style heading ids for
`#Section` deep links) has no `DocumentHtmlView` parameter yet, so the reader cannot
turn it on without going around the component — revisit when slopedit exposes it.
`GetOutline()` duplicates what `PublishOutline` already walks, but Gatherum's walk
skips asides' own headings (`Tag: null`), which the model's outline has no word for.

## Bookmarks capture what the server serves, through a seam a browser could fill
"Index the rendered page à la the Internet Archive" became a sixth abstraction seam:
`IPageArchiver`, with `HttpPageArchiver` fetching what one polite HTTP request gets and
`PageSnapshot` folding it into a single inert HTML file — scripts, frames and handlers
stripped, stylesheets and images inlined under a byte purse, every remaining reference
absolutized, the source URL and capture time stamped in a first-line comment. That is
not what a browser paints for a script-rendered page, and the seam is the honest
admission: a browser-driving archiver is the stated second implementation, slotting in
without touching `BookmarkService`. The snapshot is one file so the acceptance test
keeps its meaning — a bookmark on disk reads with `cat`, Gatherum or no Gatherum.

Three judgment calls beside it. The fetch runs inside the request rather than on a
worker: unlike analysis, the capture *is* the node's content, so there is nothing
sensible to show until it exists — the dialog waits with a progress note, bounded at
thirty seconds. The source URL lives on `FileBody` and in `meta.json` (and redundantly
in the snapshot itself), so "capture again" survives losing the database like everything
else. And nothing blocks fetching private addresses: the server sits on a homelab where
bookmarking one's own dashboards is a first-class use, and every caller is one of the
two authenticated users.

## Stored HTML renders, and is therefore served sandboxed
A bookmark's snapshot should read as the page it captured, so `text/html` nodes render
in the file view instead of opening as source in the code editor — which also demoted
HTML from "editable text" on the node page, a fair trade since editing a capture is not
a thing. Serving stored markup inline on the app's origin is stored XSS by construction
(upload was already such a door, before bookmarks widened it), so the content endpoint
now sends `Content-Security-Policy: sandbox` for HTML and SVG, the preview iframe adds
its own `sandbox`, and the capture had its scripts stripped at save — three fences,
any one of which is sufficient, none of which is trusted alone.
