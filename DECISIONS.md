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
construct afterward. The Source toggle has always paid the same price.

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
*Superseded in part by [A category is a page](#a-category-is-a-page): the argument against
tags stands, the path does not.*

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
  nothing that was filed becomes unfiled; nesting them afterward is an ordinary move.

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
page scrolling also hands slopedit's page-mode sanswery strip the document as its scroll
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
default is the behavior we wanted, and the version bump is the whole change.

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
`<pre>`'s `overflow-x`, `white-space` and the sanswery `.se-ln` gutter — the horizontal
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

## Categories moved to the foot of the page
The chips sat in `NodeHeader`, between the title and the first line of the article. No
wiki puts them there, and the reason is not habit: a category is a way *out* of a page,
and a row of exits above the prose is an invitation to leave before reading. Wikipedia's
catlinks bar has always been the last thing on an article, under the references and the
navboxes; Gatherum's is now the last thing under the history and "Referenced by", in a
box with a border on all four sides — the other foot sections open with a rule because
something follows them, and nothing follows this one.

The move made the component the header's, and it should not have been. `CategoryEditor`
is `NodeCategories` now: it renders the whole bar, label included, and owns the list it
shows. Filing a category was the only thing on the page that changed when you filed one,
and the header was re-reading the node on the article's behalf to keep a row of chips it
did not need. The page hands it what the prerender already knew, so the bar arrives with
the article rather than a beat after it, and every change after that is the bar's own.

Two things fell out of moving it that were wrong where it stood:

- **A reader is no longer offered the editor.** The old bar showed an anonymous visitor
  to a public page a × on every chip and a "+ category" field, both of which the API
  would refuse. The taxonomy still has no owner — any signed-in user files any node they
  can see — so `CanFile` is only ever "is there somebody there", which is the page's
  answer and not the bar's. With nothing to file and nothing filed, the bar renders
  nothing at all.
- **The header's metadata band can now be empty**, because categories were the one thing
  in it that everybody had. An unlisted page seen by the person holding the link has no
  share control and no badge, so the band is not rendered rather than rendered hollow.

What did not change is the direction: a page still says what it is about, and a category
still comes into existence by being used. That is Wikipedia's model too — `[[Category:X]]`
is written on the member, and the category page may not exist yet. What Wikipedia has
that this does not is the category *as a page*: a body to describe the subject in, and
its own membership in a parent category, which is how the hierarchy there is made. Here
the hierarchy is the path, and a category has nothing to say for itself. That is a
deliberate cost of `CategoryPath` — the prefix match is worth it — but it is the real
gap, and it is not a UI one.

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

## The browser is the archiver, and the plain fetch is what it degrades to
The owner wanted "Webpage, Complete": the page after its scripts have run, assets and
all. So `BrowserPageArchiver` fills the seam the HTTP archiver left stated: Playwright
drives the container's own Chromium (Debian's build, one apt layer for both
architectures, `Gatherum__Bookmarks__BrowserPath` to point anywhere else), waits for
network-idle within a bound, scrolls so lazy loading asks for its images, records every
response off the wire, and hands the settled DOM to the same `PageSnapshot` transform —
whose fetcher now answers from the recorded responses first, so the snapshot folds in
the very bytes the page rendered with. `PageSnapshot` also learned to inline what CSS
names — fonts and background images — and to drop `loading="lazy"`, since the scroll
already happened.

Deliberate departures from Chrome's version of the gesture. Scripts do not ride along:
by capture time their output *is* the DOM being saved, replaying them against it
rebuilds or blanks what they made, and the sandbox the snapshot is served under would
refuse them anyway — completeness here means what the scripts did, not what they were.
And the capture stays one file rather than a page-plus-folder, because a bookmark on
disk should read with `cat`.

Failure tilts toward keeping something: a URL serving a document, an instance with no
browser (a bare `dotnet run`), and a browser that cannot load or finish a page all
degrade to the plain HTTP capture with a logged reason; only the site's own answer — a
404, a 500 — fails a bookmark, because it would fail either way. Chromium runs with its
own sandbox off (`--no-sandbox`, `--disable-dev-shm-usage`): the rootless container is
the sandbox, and Chromium's wants privileges it does not have. The browser is told
about `HTTPS_PROXY`/`NO_PROXY` explicitly, since unlike `HttpClient` it does not honor
them on its own.

## A bookmark reads as Markdown, on the convention docx already set
"Convert these webpages to Markdown for LLM processing over MCP" needed no new surface:
docx had already established that a rich format's *extracted text* is its canonical
Markdown rendering, and `get_node` already hands extracted text to agents. So
`HtmlMarkdown` renders a captured page's whole body — headings, lists, links, tables,
quotes, fenced code — and `HtmlTextExtractor` stores that as the version's text, which
makes search index prose instead of tags and MCP serve Markdown without a tool being
added. The whole body, deliberately: guessing at "main content" is the reader's
judgment call, not an extractor's. Inlined images are the one loss — a snapshot carries
them as data: URIs megabytes long, so they reduce to their alt text. Nothing escapes
Markdown metacharacters in prose; the rendering is for reading and searching, not for
round-tripping. Unlike docx, the rendering does not feed link resolution: a captured
page's links are the web's, and resolving them as wiki titles would fabricate backlinks.

Capture history needed no new storage either — captures were already versions. What it
got is a face: the bookmark bar over the preview names the source, holds the Capture
again button, and pages the sandboxed frame back through older captures via the version
the content URL already accepted, an archive's calendar in one select. Restore and
download stay the History panel's; the bar only decides which past the frame shows.

## A category is a page
Categories irked, and the complaint was worth taking seriously: *on Wikipedia a category is
a thing that gets made, and pages are added to it; here pages dictate categories.* Half of
that is a misreading — Wikipedia's membership is declared on the member too, `[[Category:X]]`
is typed into the article, and a category page can be a redlink while already listing what
is in it. The direction of filing was never the difference.

The difference is that a Wikipedia category is **a page**. It has a body saying what belongs
in it, a history, a talk page, something for a link to resolve to — and, decisively, it
declares its own parents by being filed under them. Gatherum's category was a string. It
had none of that, and it had `Path` doing the work of identity, which is where the rest of
the awkwardness came from: a parallel set of verbs (rename, move, delete) that existed only
because a path is a thing its own service has to maintain, and a shape that could not say
"Podman is a homelab subject *and* a container subject" because a string has one prefix.

So a category is a page. `Node.IsCategory`, an ordinary Markdown file at
`Categories/<Name>.md` in the root of whoever first mentioned it, and `NodeCategory` demoted
to an edge between two nodes. That edge is the taxonomy's **only** relation: pointed at a
category it is a membership, pointed from one category at another it is a subcategory.
Nesting is not a second mechanism.

What that is worth, mostly measured in what stopped existing:

- **Three verbs became none.** Rename is renaming the node. Re-nest is filing it somewhere
  else — the same gesture as filing a page, on the same bar. Delete is deleting it.
  `CategoryService.RenameAsync`/`MoveAsync`/`DeleteAsync`, three REST endpoints, and
  `CategoryTools` — a whole component that existed to maintain paths — are gone.
- **Deleting a category no longer deletes its subcategories.** It could when they were
  rows under a prefix. They are pages now, so they lose a parent and become subjects of
  their own, and the pages filed in the deleted one are simply no longer filed there. That
  is the change most likely to surprise, and it is the one the model forces and improves:
  deleting `Homelab` should never have been able to delete somebody's writing about Podman.
- **The taxonomy is a graph.** `Podman` under `Homelab` and under `Containers` at once, and
  members belong to both. An index does this. `Homelab/Podman` could not.
- **Rename stops rewriting the world.** Identity is an id, so no path is repathed and no
  `RepathAsync` exists. It still costs the search text of everything beneath it — a
  category contributes its whole ancestry to its members' findable text, and that is the
  property the embedding staleness rule was written for — and it now also costs the
  *sidecars* of its direct members, because a name is what they write down.
- **`[[Homelab]]` resolves.** A category is a page, so it is a wiki-link target, a backlink
  target, and something `get_node` reads. That was the whole ask.

`CategoryPath` becomes `CategoryName`: one name, trimmed and whitespace-collapsed and
compared case-insensitively, unique among categories. Unique is load-bearing rather than
tidy — it is what lets `meta.json` say `"categories": ["Podman"]` and mean something with no
database to look an id up in, which is the same argument that already made a grant record a
root directory instead of a `Guid`. It is also what makes `/categories/Podman` addressable,
and a category name is the one address in this wiki a reader would type, so the readable
route is a second `@page` on `NodePage` rather than a second article view to keep in step.

Things worth stating because they are not free:

- **Ancestry was a string prefix and is now a walk.** `CategoryIndex` loads the whole
  taxonomy for the length of one operation and answers ancestors, descendants and closures
  from memory. That is not a new trade — `GetSimilarAsync` already decided it, in those
  words, for exactly this question — and the taxonomy is a table of dozens of rows in a
  wiki for two people. It is a snapshot: load one per operation and let it go.
- **The reindex needs two passes.** A page filed under "Podman" cannot be joined up until
  whichever root holds Podman's page has been walked, so filings are collected during the
  walk and wired afterward, in one pass, against one snapshot. A name nothing answers to —
  somebody typed it into a `meta.json` by hand — gets its page written. That is the one
  place the scan creates a file outside `.gatherum`, and it is deliberate: a taxonomy half
  of which exists only in the database would not survive the next cold start, which is the
  one thing this whole architecture is for.
- **A category page is private until its author says otherwise**, like any page. The
  taxonomy still has no owner in the sense that mattered — anyone who can edit a node can
  file it under anything, and listings are still computed against what the asker can see —
  but who may read a category's *prose* is now that page's own business. The subject is
  everyone's; the essay about it is its author's. Sharing it is the ordinary gesture.
- **An unqualified search no longer returns categories.** Every filed page has a same-named
  subject standing beside it, so the default would answer half in headings. They are
  indexed like the pages they are and `kind: category` asks for them.
- **Two spellings of one subject are two subjects**, and always were — the datalist under
  the category field is what keeps that from happening, and it matters more now that a
  second spelling writes a second file. An ordinary page called "Podman" is *not* quietly
  promoted into a subject when somebody files something under that word; only a category
  answers to a category name.

One thing was fixed on the way past because the design made it load-bearing:
`NodeService.RenameAsync` never wrote the sidecar, so a title override lived in the index
alone and the next reindex would quietly undo it. Harmless-looking until a category's name
is what its members write on disk.

## A mention was never a link
`[@Podman](node://id)` rendered, in the read view, as a `<span>`. It had the link's colour
and the link's underline and did nothing at all when clicked — no target, nothing to open
in a new tab, nothing to copy, nothing for the status bar to preview. The wiki link beside
it worked, which is what made it look like a styling problem rather than a missing anchor.

The cause is one line and worth writing down because it will happen again: the read view
hands `RichHtmlWriter` an allow-list of URL schemes, and anything outside it is written as
a styled span rather than an `<a>` — the sanitizer doing exactly its job. `wikilink:` is on
that list because slopedit put it there; `locked:` is on it because we did, deliberately,
so a padlocked link has an element to hang the padlock on. `node://` was on nobody's list.
It is Gatherum's own scheme and it never needed to be a *browser's* URL — the canvas
resolves it by hit-testing, and the canvas is where mentions were designed.

The fix is not to add `node` to the allow-list. `node://id` is not something a browser can
do anything with either: Ctrl-click would open a tab on a scheme it cannot resolve, and
"copy link address" would yield a string that means nothing outside this app. slopedit's
own note on why the read-only view emits real anchors says it plainly — a plain left click
comes back to the host, and *every other click is the browser's*. A link that only answers
one of those is half a link. So the read view addresses a mention as `/nodes/id`, which is
where it goes, and which every one of those gestures already understands.

Where that rewrite happens is the part that matters. It belongs inside the pass that
already asks the server which of a page's links this reader may follow, and nowhere
earlier — because a mention that has not been vouched for must stay inert. Do it up front,
before the answer comes back, and a page whose reachability call fails or gets rate-limited
ships live links into other people's private nodes; do it in the same pass, and the three
outcomes are one decision in one place: reachable becomes `/nodes/id`, unreachable becomes
`locked:id`, and unanswered stays `node://id` — which the sanitizer draws as no link at
all. `NodeLinks.Seal` became `NodeLinks.Address` because that is now what it does: it gives
every node link in the document the address it should have *for this reader*.

The stored bytes never change. A mention is written `node://id`, saved `node://id`, and
round-trips `node://id`; the addressing is the read view's, like the padlock, and for the
same reason — a document that can be saved has to write back the bytes it was read from.

A test existed that nearly caught this and didn't: it asserted the reader's HTML contained
the text `@Notebook`, which a span satisfies. The replacement asserts the anchor.

## The category bar is MediaWiki's catlinks, in this app's clothes
The bar at the foot of a page went through two wrong shapes before landing on the one
Wikipedia has used all along, and the middle one is worth recording because it *looked*
right in isolation and read wrong on the page.

First it was a row of chips — filled pills, each with its own ×, in the shape the app uses
for a control you press. These are the names of subjects; the row is the end of a sentence
about what the page is about, and a chip apiece turns it into a toolbar you are meant to
operate.

Second it was a flex row: label on the left, a flexed list of names beside it, rules
between them. Closer, but flex is the wrong display model for a sentence — the names were
a column *next to* the label rather than a paragraph *containing* it, so a wrapped line
hung under the first name instead of returning to the box's edge, and keeping the label on
the first line took a `flex-basis: 0` and a negative-margin trick that existed only to
fight the layout model. When the construction needs tricks to look like text, the
construction is not text.

So, third: MediaWiki's own construction, literally, with the app's colors on it —

```
.catlinks       { border; faint background; padding }
.catlinks ul    { display: inline }
.catlinks li    { display: inline-block; border-left: 1px solid; padding: 0 .5em }
li:first-child  { border-left: 0 }
```

One inline flow. The label is part of the sentence — and it is a link to `/categories`,
because Wikipedia's "Categories" links to the category index and it should. The list
renders inline, so the names wrap the way prose wraps and a wrapped line starts at the
box's edge. The rule sits on the *left* of every name but the first, which means a wrapped
line opens with a rule — Wikipedia accepts that, and it is what makes the dividers read as
part of the text rather than as column borders. The faint ground under the box is
`--surface-ground`, which is within a hair of Wikipedia's own `#f8f9fa`.

Filing is the one thing Wikipedia's bar does not do — its categories are edited in the
wikitext, which is also the answer to *when* ours should appear: filing is editing, so a
reader sees the names alone and the × and the plus come out with ?edit. That rule briefly
carried an exception — a node with no editor showed the controls in its read view — and
the exception was the wrong fix for a real hole: files had no Edit surface at all, and
their view compensated by mixing writing into reading, offering "Upload new version" and a
description form to everybody, anonymous strangers on a public file included. So instead
of excepting the bar, every node got the tab: a page's Edit is the editor, a file's is its
own view with the management controls shown — upload, the description as a field rather
than a caption — and the bar's rule holds for every media type without a footnote. The ×
is 40% opacity until hovered or focused. The plus is a single glyph at the row's own size doing the whole
gesture: shut, it opens the field; open with something typed, it commits; open with
nothing, it shuts again. It replaced a dashed ghost pill reading "+ category" and a
separate "Add" button — two controls and a placeholder for one act. Enter and blur still
commit and Escape cancels; the plus stays put while the field is open because neither of
those is visible and a soft keyboard makes guessing expensive.

**Touch is a different shape, and the padlock rule proves it.** A 14px × is not something a
finger can hit, and `app.css` used to carry an exception — `.category button { min-height:
0 }` — so the chip's × could stay small inside a pill that was big enough. There is no pill
now, so the exception went, and with it the mobile check's matching
`!el.closest(".category")` escape. Under a coarse pointer the sentence becomes a list of
rows, each at least `--tap` tall, with the × at the right where a list keeps its actions —
which is what the check was there to insist on, and it now insists on unexempted.

A compiler quirk found on the way: Blazor's CSS isolation rewriter dropped the second
selector of a nested list (`& li, & li:first-child { … }`), so the first-child half never
matched and an earlier `padding-left` won on specificity. The symptom was a first row
indented by 4px on a phone and nowhere else. Nested selector lists get their own rules in
scoped stylesheets until that is known fixed.

## Bookmark captures block ads with a curated embedded list, not EasyList
An ad blocker for captures wants a filter list, and the real ones (EasyList, uBlock's)
update daily — but "nothing in Gatherum fetches the web unasked" would make a
self-updating blocklist the first exception, and a hash-pinned build-time fetch (the
embedding model's trick) breaks on a file that changes under it. So the list is a
hand-curated set of ad, tracking and consent *hosts* embedded in the assembly
(`Bookmarks/AdHosts.txt`), matched by domain suffix — no cosmetic rules, no regex
engine. Blocking happens twice because the two halves catch different things: the
browser archiver aborts listed hosts at the network, so ad scripts never run, never
draw placeholders or consent overlays into the DOM, and stop costing the settle budget;
the snapshot transform then refuses to fetch listed assets and strips elements pointing
at them, which is what covers the plain-fetch fallback and keeps tracking pixels from
reporting every later reading of the archive. A page whose own host is on the list is
exempt from its own entry — bookmarking the ad company's blog still captures its logo —
and the main-frame navigation is never blocked at all. `Gatherum__Bookmarks__BlockAds`
turns the whole thing off.

## The ad blocklist is community-sourced after all, fetched just in time
The entry above chose a hand-curated embedded list to keep "nothing fetches the web
unasked" airtight; the owner would rather have the community's coverage, and the tempo
rule survives the change: the list is fetched lazily *inside* a capture somebody asked
for — never on a schedule, never at startup — cached for a day in memory, with a
fifteen-minute silence after a failure so a dead list host doesn't toll every bookmark.
`AdBlocklistProvider` owns that lifecycle; `AdBlocklist` stays the immutable matcher.
The default source is StevenBlack's hosts file, but the parser reads all the shapes
these lists come in (hosts format, bare domains, `*.` wildcards, `||host^` rules —
cosmetic and exception rules are skipped whole, and a mid-token `#` is Adblock syntax,
not a comment, lest `example.com##.ad` read as a block on example.com), so
`Gatherum__Bookmarks__AdHostsUrl` can point at OISD, Peter Lowe's, or the AdGuard DNS
filter. The packaged curated list stayed, demoted to seed and safety net: it is unioned
under every fetched list so an update can widen blocking but never narrow it — and
because its entries are registrable domains, `Match` answering with the *most general*
entry keeps the first-party exemption whole when a community list names one outfit's
subdomains host by host. A fetch failure degrades to blocking less, never to failing
the capture that wanted the list.

## The EPUB pager is a second script, served rather than shipped
"No JavaScript beyond `wwwroot/js/gatherum.js`" met a page that has no Blazor to lean
on: an EPUB chapter renders in a frame sandboxed to an opaque origin (the same
inertness a bookmark snapshot gets), and turning pages inside that frame is something
only a script in the document can do. So `EpubChapterHtml` injects one pager script
into every chapter it renders, and the response's `Content-Security-Policy` admits
exactly that script by SHA-256 hash — the book's own scripts are stripped, and one that
slipped past stripping would still not run. The interop file grew a `message` listener
because a sandboxed frame's only voice toward the page hosting it is `postMessage`
(cross-chapter links, reading progress), which Blazor cannot hear natively.

## Reading positions live only in the database
"Nothing may live only in a table" is about content: everything the directories can
say must be rebuildable from them. A reading position is the opposite kind of thing —
per-reader, per-node ephemera that no file could carry (the storage root is browsed,
rsynced, and shared; a sidecar churning on every page turn would be backup noise, and
one file per reader pair would put one user's state under the other's root). So
`ReadingPositions` joins `Users`, `ApiKeys`, and the Data Protection keys in the
database-only column: not derived from the directories, cascade-deleted with the node
and the user, and losing one costs exactly a page number. Saving requires only seeing
the node — a ribbon is the reader's own, so no editing right is asked — and anonymous
readers are never remembered server-side: the position write is an authenticated
endpoint like every other write. A visitor's place is kept by their own browser
instead (`localStorage`, written on every save as the fallback the server never is,
read only when the server had nothing) — so a stranger still resumes their reading,
and nothing about them ever lands in the instance.

## A citation is a convention, not a construct
Archive-backed citations — a claim's reference pointing at the capture Gatherum keeps
rather than at a URL that will someday 404 — landed as no new syntax at all. A citation
is a footnote whose note cites a node: `[^1]: [Title](node://id), captured 27 August
2026 — [example.com](https://…).` Every word of that is vocabulary the dialect already
had (Pandoc's footnote, a mention, prose, an external link), which buys the whole
feature its properties for free: the note round-trips because footnotes round-trip, the
mention backlinks because `RefreshLinksAsync` reads links wherever they sit — Markdig
parses the definition line as a paragraph and finds the link without a footnotes
extension, pinned by test — and a citation into a private bookmark padlocks because
`NodeLinks` walks every block and a note is blocks. `Citation` (Client) is the one
place the sentence is spelled.

Judgment calls in the spelling. The mention is labeled with the bare title, not the
`@Title` a mention dialog inserts: a reference list names its source rather than
addressing it, and the label was never what made a mention a mention. The external
link is labeled with the source's host — a reference names its publisher — and kept
last, the way "archived from the original" trails a reference that expects the
original to rot. The date is the capture's UTC day, spelled out invariantly: it is
prose both readers must agree about, whatever locale either browser is in. And the
citation deliberately does *not* pin a capture version. The note's link is the plain
`node://id` every other mention is; the date names the capture that backed the claim,
and the bookmark bar's capture picker can page back to it. A version-pinned URL shape
would have rippled through `NodeUrl`, `MarkdownContent`, `LinkRouter` and the node
page for a distinction the date already records — revisit if captures ever diverge
faster than a dated note can disambiguate.

The editor's *Insert… → Cite…* is the node picker again, with one addition: a query
that parses as an absolute http(s) URL is offered as *Capture and cite* — the URL is
bookmarked first (inside the dialog, with the same waiting-and-refusal manners as the
Bookmark dialog, since it is somebody else's server) and the fresh capture cited like
any picked node. The captured bookmark files under the page doing the citing, so a
reference lives with the article that leans on it. Insertion rides the model's own
`InsertFootnote()` — the model owns key and numbering — and then dresses the fresh
note's runs directly, the run-rewriting `WikiLinks` and `NodeLinks` already practice;
the write-out-and-reread the fence constructs pay (and the undo stack it costs) is
not needed, because a footnote is not read out of source. Document mode only, like
Footnote, and for the same reason. A node with no source URL cites as the mention
alone: only a capture has a date worth quoting, and citing pages and PDFs was too
useful to refuse for want of one.

## The chapter rides into its frame as srcdoc
The reader's frame is never navigated to the chapter URL; the component fetches the
rendered chapter and hands it over as `srcdoc`. The reason is an iOS behavior no
emulator shows: a *network-src* sandboxed frame is a cross-origin document, and iOS
Safari was observed withholding the raw touch stream from exactly that shape — taps
still arrived (click synthesis is a separate pipeline; the reader's old arrows
worked), swipes never did, and the identical document received touches when loaded
top-level. A six-variant embedding matrix run on the affected phone showed every
srcdoc shape — parser-created, script-inserted, sandboxed, content-swapped —
receiving touches, so srcdoc is the transport the hardware itself validated. The
response header keeps `sandbox` plus the hash-pinned `script-src` for direct opens;
the srcdoc copy, which no header can accompany, carries the pinning half as a meta
policy (meta may not carry `sandbox` — the frame's sandbox attribute supplies that)
and the saved fraction and debug flag follow as postMessages, since a srcdoc
document has no URL to put them on.

## slopedit 2.5.11: the serif lands, captions stop pretending, sections fold under the caret
The five features between 2.5.0 and 2.5.11 were, again, mostly built for this wiki, and
the adoption is one bump plus the wiki's half of each.

**Typefaces.** `EditorTheme` speaks font families now, resolved through a process-wide
`FontRegistry` (fonts a host ships as bytes) before installed fonts — the only channel
that reaches Skia in WASM, where no fonts are installed at all. Gatherum ships the four
Liberation Serif faces (OFL, license beside them) once, in `Gatherum.Client/wwwroot/fonts`:
embedded in the assembly for `DocumentFonts.EnsureRegistered()` — called from `Dress`, so
no document lays out before its faces exist, on the server circuit, in WASM, and in the
read view's static pass alike — and served as static web assets for app.css's
`@font-face`, the same files both ways, which is upstream's parity rule for fonts. Both
themes set `HeadingFontFamily` to that family list, and `--font-serif` now leads with the
shipped face: the article's own section titles finally wear the serif the design
("what if Google made Wikipedia") had so far only put on the page title, in the canvas
and the browser from the same file. Body and code stay slopedit's embedded defaults —
already guaranteed everywhere, so shipping replacements would buy nothing.

**Small print.** `Block.FontScale` reaches layout as well as paint, so
`AsideExtension.Style` sets every aside block at Wikipedia's 0.88 em — infobox rows,
figure captions, the heading's own multiplier riding on top — and `DocumentChrome`
restamps it each edit so a row typed into a card wears it too. Presentation only: no
serializer stores a scale.

**Captions are the image's own.** The serializer's dialect grew pandoc's attribute form,
`![caption](url){width=300 align=center}` — a trailing `{…}`, even empty, makes the
bracket text a caption: the image block's own styled runs, wrapped to the picture's
width, one unit with it for selection and deletion, a real `<figure>`/`<figcaption>` in
the HTML view. *Insert… → Figure* writes that form now (with `align=center` spelled out,
because it is the file's word once the dialect has a spelling for it); the older
paragraph-under-the-picture figure still reads as it always did, and merging it into the
image on parse was considered and declined — the un-merge is not byte-faithful in every
case, and the round trip outranks the upgrade. One defensive move came with this:
`WriteImage` serializes alignment, so the centering `Style` stamps on a *bare* picture in
an aside would have written `{align=center}` into files that never said it — caught by
the round-trip tests on the day of the bump — and `AsideExtension.Untagged` now sheds
exactly that stamp (bare spelling, centered) while leaving any alignment the file did
say alone.

**Tables keep their delimiter row's word.** `|:---:|` and `|---:|` parse onto every row
(`Block.ColumnAlignments`), lay out, and write back; the context menu grew the *Align
column* verbs on its own. Nothing was Gatherum's to build — the tests pin that an
aligned table round-trips (explicit left normalizes to the plain dash, the delimiter
row's one liberty) — and per-cell docx shading now survives the rich door in both
directions for the same price. colspan upstream declined on purpose; nothing here
mourns it.

**Sections fold in the editor.** `Dress` sets `FoldableHeadings`: every heading wears a
drawn chevron and folds its section to the next equal-or-shallower heading — the
editor-side answer to the read view's `CollapseSectionsBelow`, and the reversal of "the
editor never hides the text the caret lives in" *because upstream answered the
objection*: fold state is view state (no Version, no serialization, no collab op), fold
indices ride splices like the caret, and the caret entering a hidden region unfolds it,
so content is never unreachable. A Contents jump calls `RevealBlock` first, the way
upstream's own `ScrollToBlock` does. The HTML renderers ignore the flag, so read-only
documents dress identically for free.

And the fix that cost nothing to adopt: declared decorations and floats now ride every
splice the way the caret does, so an edit above an infobox no longer slides its card
between derivations. `DocumentChrome`'s per-keystroke pass stays — the tags are still
the truth about what a construct *is* (membership at the seams, small print on new
blocks, callout title ink) — but its comment now says what it is for, not what upstream
could not do.

## Folding folds back to the read view: one affordance, and it is Minerva's
Same-day reversal, on the owner's word. Turning `FoldableHeadings` on put a second
folding affordance next to the one the app already had, and the two disagree about
everything a reader can see: the mobile read fold is Minerva's — a 14px chevron in a
36px gutter that indents the heading, pointing down to say "expand" and up when open —
while the canvas draws its own 10px chevron hanging in the heading's margin, down when
open and *right* when folded. Both are upstream's designs; the HTML one is host-stylable
CSS (`se-sec`/`se-shead` are API the way the padlock's anchor is), but the canvas one is
`DrawDisclosure`, private, on `const` metrics, with no theme knob — so the styles cannot
be reconciled from this side of the package boundary, and the preference between them is
the mobile one.

So the editor's folding is off again — `Dress` says why in place, and a test pins it so
a future bump doesn't quietly reintroduce the second style — and the fold-gutter
arrangement that existed only to house the canvas chevrons (ContentPadding 24 leaning
into the pane on a −24px margin) went with it. Everything else from 2.5.11 stays. The
way back is upstream: a canvas disclosure drawn to Minerva's glyph and direction (or a
host-stylable one), at which point the flag is one line and the gutter is the comment
already sitting next to it.

## slopedit 2.6.0: the chevron comes back, in the read view's own hand
Upstream took the fold affordance the reversal above asked for, and the editor's
folding is on again. `DrawDisclosure` now draws Minerva's chevron — the same
`M5 9l7 7 7-7` glyph in the same 14px box the HTML fold's `ChevronSvg` uses, with the
same direction convention (down says "expand", up says "open") — and the ruled
heading's hairline runs underneath it, spanning the fold gutter the way the mobile
summary's `border-bottom` does. One affordance, two renderers, so `Dress` sets
`FoldableHeadings` again and a Contents jump calls `RevealBlock` before it lands.

The gutter is the host's to provide, and it is the thing that will look broken if it
is missing: the chevron hangs `MarkerGap + DisclosureSizePx` — 23px — left of a
heading's text origin, so a canvas at `ContentPadding=0` clips the affordance away
entirely and the fold silently has no handle. The editor passes 24 and the surface
leans 24px back into the pane's padding (`margin-inline`, `--fold-lean`), which buys
the gutter without moving the text column: the canvas grows by 48, the padding gives
48 back, and the two surfaces still wrap at identical points — measured in the
running app at 888px canvas against the reader's 840px column.

Below the shell breakpoint the lean is the pane's own 16px instead, because a phone
cannot spare eight more pixels of column and the read view's margin is not the
editor's to widen. The cost is that the editor's column on a phone is 16px narrower
than the reader's, so a page wraps slightly differently between the two there. That is
the affordable half of the trade; a clipped chevron, or a canvas painted out over the
sheet's rounded edge, is not.

Two upstream changes rode along that nobody here asked for. The canvas **paints in
CSS's order** now — in-flow chrome, then each float entire, then the flow's content
over the top (CSS 2.1 §E.2) — which fixes a thing this app had been looking at
without naming: a ruled heading's hairline drew straight across a floated infobox,
because the float's card went down before the body's chrome. The read view never had
the bug; the canvas agrees with it now. And `BlockDecoration` grew a real box model
(per-side padding and borders via `BoxEdges`, plus `CornerRadiusPx`), which comes with
a half-a-border-width shift for cards positioned against the old behavior —
`DocumentChrome` draws at `BorderWidth: 1f`, so the shift is half a pixel and nothing
here moved. The room is there if an aside ever wants a leading rule down one edge.

## slopedit 2.6.2: an aside's heading is not a section
The infobox wore a fold chevron, which it should not: a declared float is its own flow
and its heading names the card — Gatherum has always said as much on its own side,
where the outline builder skips a tagged heading because "an aside's own headings title
the card, not the page." There was no way to say it to slopedit, so it went upstream,
and 2.6.2 carries both halves.

The second half was a bug found by chasing the first, and worth more than it. A
heading's section ran to the next heading of equal or shallower level *including one
inside a float* — and an encyclopedia titles its infobox at h1 inside an h2 article. So
on a real page here, the page-title's section ended at the infobox two blocks in, and
its chevron folded one empty paragraph. Now the scan walks past an aside's headings and
a section closes only at a heading of the flow: folding the title folds the article.

Upstream declined the `FoldFloatedHeadings` knob offered with the patch, and the reason
is the better one: `RichHtmlWriter` never emitted a `<details>` for a section a float
touches, so the HTML view has never folded an aside's heading, and a knob would have had
no setting the read view could honour — the same canvas-vs-HTML disagreement the
previous release closed. They also kept the float clamp this end had called
belt-and-braces, having found the ordering that reaches it (`RevealBlock` walks the
folded set without forcing a layout first), and added the worse case of the same
ordering: a float declared over a section that was *already folded* now drops the fold
as stale rather than leaving content hidden behind a chevron that no longer exists.
`DocumentChrome` re-declares floats on every edit, so that ordering is reachable here;
the direction it now fails in is the safe one.

## The infobox joins the app: a tonal card, a rounded hairline, an accent title
`BlockDecoration` grew a real box model in 2.6.0 — per-side padding and borders through
`BoxEdges`, plus `CornerRadiusPx` — and the infobox had been the one thing on a Gatherum
page still drawn as a hard rectangle. Everything else the app insets is a tonal fill
behind a rounded hairline: the content sheet at `--radius-l`, a code band at
`--radius-s`. An aside is one of those, so it is drawn like one, at `--radius` between
them, with roomier flanks than crown (`BoxEdges.Symmetric(10, 12)`) because a 280px
column of small print is read down its middle.

The palette moved with it, in both cases toward what the tokens already said. `CardFill`
was documented as `--surface-dim` and had drifted to a near-white that only read as a
card because of its outline; it is `--surface-dim` now, and the outline stepped back to
`--outline-dim`, the weight the content sheet is drawn with. A tonal card wants an edge
you can find, not one you read.

**The title band is gone, and that is the design rather than a subtraction.** The first
attempt kept it as one of the app's chips — a rounded tint inset from the card's flanks.
Rendered, it failed twice over: a band wants to reach the card's edges and a rounded card
has no edges to reach, so an inset one reads as a chip that missed; and the hairline the
h1 already rules landed *inside* it, two dividers doing one job. The heading's rule is
level-driven in both renderers with no per-block opt-out, and the level is the file's
word (`# Podman`) — not something to rewrite for a paint decision. So the band went and
the rule stayed: the title takes `--on-chip`, the accent its own category chips wear, and
the rule under it is the divider. Fewer parts, and both renderers already agreed about
every one of them.

Callouts took the same card in their own accent. Two constructs sitting in one page
should not disagree about what a card is, and a rounded infobox beside a square callout
would have been worse than leaving both square.

Verified in the running app in all four combinations — canvas and HTML, light and dark —
because a decoration is the one thing both renderers draw from the same numbers, and the
point of the box model is that they land in the same place.

### The card needed a page margin to pad against
The rule under an infobox's title sat visibly off-centre, and the cause was geometry
rather than paint. A decoration may not outset past the page's edge — slopedit clamps
it, because a box drawn there is drawn nowhere — and a right-floated infobox's column
*is* that edge. So the card padded 12px on the left and 0 on the right, and the rule,
which spans the text column, sat 12px from one border and 1px from the other. Worse,
the two surfaces disagreed about it: the editor already spent 24px of `ContentPadding`
on the fold gutter and so had room, while the read view spent 0 and did not.

`ContentPadding` is the page margin in slopedit's sense — the room a document has
outside its text column — and it turns out two things need it: the gutter a fold
chevron hangs in, and the room a card at the margin outsets into. Both surfaces spend
`DocumentChrome.PagePaddingPx` now and lean it back into the pane, so the text column
is where it always was (both measure 840 against the same pane, checked in the running
app) and the card pads evenly. One constant, referenced by both, with the invariant it
has to satisfy — the margin covers the card's outset — asserted beside the emitted
padding in `An_asides_card_pads_evenly_on_both_sides`. That test was run against the
old geometry first and fails there with a right padding of `0px`, so it pins the bug
rather than agreeing with the fix.

## An OIDC group gates sign-in; Gatherum still has no groups
Sharing with a group is the obvious next thought once an instance holds more than a
couple of people, and it was designed once — a grant naming a principal rather than a
user, group ids derived from names, the closure keyed on both — before the owner's
correction: what is wanted is the normal thing, which is the identity provider deciding
who may sign in at all. So there is no group in the domain. `Gatherum__Oidc__RequiredGroup`
is read from the token as each person arrives and remembered nowhere, which is what makes
removal take effect at the provider rather than needing to be mirrored here, and sharing
still names people one at a time.

Two calls inside that. The gate **fails closed**: a required group with no claim to answer
it turns everybody away, because the alternative is an instance that silently admits every
account the provider has the day a scope stops being sent — and the warning it logs names
the claim and the scope, since "I configured the group and nobody can log in" has exactly
one cause worth printing. And the refusal happens **before** `GetOrCreateAsync`, so
somebody the provider authenticated but this instance does not admit leaves behind no user
row and no root directory.

`Gatherum__Oidc__AdminGroup` works the same way and is authoritative when set: it grants
admin and takes it away on every sign-in, because a claim read per request is only
meaningful if the answer is allowed to change. Left empty, admin stays where it was — with
the first account ever seen.

What this deliberately leaves undone: the access modes still go from `Shared`, which names
people, straight to `Unlisted` and `Public`, which mean the open internet. There is no mode
for "anyone who got past the front door", and once the front door is a group that is the
gap that will be felt first — see LISTS.md, which ran into it.

## Authenticated is a second axis, not a fourth rung
"Everyone who can sign in" is the mode a Gatherum shared by a group actually wants, and
until now the scale went from `Shared`, which names people one at a time, straight to
`Unlisted` and `Public`, which mean the open internet.

The obvious implementation is a new value on `NodeReach`, and it is wrong. That enum is
ordered so that inheritance can be a maximum and the two visibility questions can be
comparisons — but "everyone signed in" and "anyone holding the link" are not comparable:
the second admits anonymous strangers and hides the node from every listing, the first
admits nobody anonymous and lists it to everybody else. Forced onto one scale, the maximum
has to discard one of them, and an unlisted page inside an authenticated directory would
silently vanish from the listings of the very people the directory was shared with —
a narrowing, in the direction nobody would think to check.

So `NodeReach` keeps its meaning, which was always *what the anonymous internet gets*, and
`Node.ListedToSignedIn` carries the other axis. Both are derived by `AccessService` from
the same pre-order walk and both are additive downward, one as a maximum and one as an or.
`VisibleTo` gains a term rather than a join, so visibility stays the indexed predicate it
has to be. `AuthenticatedAccessTests` pins the case that forced this.

Two smaller calls inside it. `AccessMode.Authenticated` is declared **last** rather than in
its conceptual place between `Shared` and `Unlisted`, because the database stores that enum
by ordinal and inserting a value would silently renumber every access already recorded —
the sidecar writes it by name and does not care, but the rows do. And `Public` does not set
`ListedToSignedIn`: a public node already reaches everyone through `NodeReach.Listed`, and
routing it through the new axis as well would let it outlive `Sharing.AllowPublic`, whose
documented meaning is that a node claiming to be public is treated as private by every
query. Keeping it out means this change moves nothing for an instance that has the internet
switched off.

## A shared list is one construct, and both halves of it are files
`LISTS.md` is the design; this is what got built and what it cost.

A collaborative collectible list conflates two documents with different tempos: what
exists to collect, written once by one author, and what *I* have, written constantly by
each participant. One set of checkboxes cannot answer both — if you answer Sonic, has anyone
else got it, and where would the checkbox put the answer? So the catalog is a page and a
tally is a page per person. No new relation, no new table, no new visibility rule.

**The catalog's audience is the grid's audience**, and this is the correction that
mattered most. The first cut made each tally's own `AccessMode` decide whether its column
appeared, which is locally impeccable — only an owner sets access — and globally absurd:
it made joining a shared list a two-gesture act, answer and then publish a second page, or
your column counts for nobody. Nobody who shares a roster with their group means "and each
of you must separately publish your answers first". So authorization happens once, at the
door the service already knocks on — `NodeService.GetWithBodyAsync` on the catalog,
which is `INodeAuthorizer`'s answer — and a reader who got past it gets the whole grid.
The aggregate then asks no visibility question of its own, which is not a second door left
unlocked but the same door knocked on once; `INodeAuthorizer` stays the only one, and the
rule survives because nothing here re-spells it.

What that deliberately does *not* do is publish the tally page. Its `AccessMode` is
untouched and still governs the node — its own URL, the tree, search — so a tally stays
private as a file while the answers on it count in the list they were made against. The
disclosure is exactly the rows somebody answered and the name they answer under; notes and
orphans stay their owner's, and orphans are reported only to the person who can act on
one. Two consequences worth saying out loud: a public catalog's grid is public, display
names included, so publishing one is a decision about other people as well as about the
page; and there is no half-in — the way out of a grid is not to answer, or to delete the
tally, because a mode meaning "counted but hidden" would be a checkbox lying in the other
direction.

It also moved the wiki-link caveat somewhere better. A tally naming its catalog
`[[by title]]` is matched by comparing that title against the catalog's own rather than
by resolving it, so whether the match happens no longer depends on who is *reading*: an
unlisted catalog still cannot be named by title, but that is now a fact about the author
writing the link, which is where it belongs.

**A tally is content, not ephemera.** `NodeAnswers` would have been an afternoon's work and
wrong: `ReadingPositions` earns its database-only exception because losing one costs a page
number, and a season of collecting is not that. A tally is a file under its owner's root —
rebuildable by `Reindexer`, carried by the backup, readable when Gatherum is not running.

**One construct, declared by a fence.** Making every Markdown checklist per-person was
considered and rejected: `- [ ]` already means *it is done* — shared state, one answer for
everybody, and the commoner kind in a knowledge base — so reinterpreting it would break the
first kind silently. A per-page setting is the wrong unit (two lists on one page cannot
both be it) and has nowhere to live. So the list declares itself, and the same fence does
both jobs: an argument that is a name declares a catalog, an argument that names another
node tracks it. Recognition is exact rather than inferred — an earlier draft recognized a
tally structurally, by it linking a catalog and carrying matching task items, which would
have counted any page discussing the list with example checkboxes as somebody's column.

**Item identity is the interesting problem, and the answer is that pages are optional.**
Keying by line number fails on the commonest edit there is; by text, on a rename; by node
id, never. The first draft therefore made a page per collectible mandatory, which is the
expensive option made compulsory — wanting to answer off forty sprites is not wanting to write
forty pages. So an item is a line of text that *may* link a node, and matching is: two
linked items are their ids, anything else is its text. That asymmetry is what makes
promotion lossless, so answers made against `Sonic` keep counting once Sonic becomes a page.
A rename orphans the plain answers it stranded — Alice cannot rewrite Bob's file to follow
her wording — so the orphans are kept in his file and reported in the grid. Silence is the
one unacceptable answer.

**Variants are nested items because rosters are ragged.** Declaring the variant set once on
the fence would be a lie from the first week: things are held back, arrive late, and do not
all carry the same forms. Nesting also forces the counting rule — every number is of
collectibles, never of lines — and makes a parent row a group rather than a control:
"give me all three" is a different statement from the three answers it would stand in for,
and the one thing this must not do is guess what somebody has.

**Signed out is read-only.** A third answer was on the table — answer freely into this
browser's `localStorage`, the mechanism that already keeps an anonymous reader's place in a
book — and it is wrong here. A reading position can be quietly local because nobody else was
ever going to see it; in a grid where every other column is a real person's real answers, a
checkbox that writes to nobody looks exactly like the ones that count. So there is no
checkbox at all, and an invitation to sign in instead. Guest tallies (a hashed capability
token, its file under the catalog owner's root, off by default) remain designed and
unbuilt: going from read-only to counted is purely additive, and the other direction is not.

What this cost outside Core: one Markdown extension, one component, two endpoints and two
MCP tools. The construct is the dialect's first *interactive* one, and it needed a hole in
the reading view to put a component in — which slopedit 2.7.0 shipped as widget blocks,
keyed by the `Block.Tag` an extension already stamps. The blocks stay blocks, so items
reach search and a `[[wiki link]]` in one is still a real link; the canvas keeps painting
the source, so an answer can never register as an edit of an open document; and `WriteBody`
ignores the claim, so a static export holds the catalog rather than a gap. Two behaviors
to design around rather than discover: a tagged run inside a float renders as blocks, so a
collection inside an `:::infobox` is a plain list, and a collapsible section holding a
widget keeps its chevron rather than folding a component out of the page.

## One grid, several questions
The collectible list shipped narrow: `:::collection`, a "catalog" of "collectibles",
copy about what you still have to find. Then the obvious question — what if a group wants
to see which nights everyone can play D&D — and the answer turned out to be that the
feature was already general and only the words were not.

Nothing under the fence ever knew what a row meant. The mechanism is: a row per thing, a
column per person, a mark where that person says yes, each column a page its owner writes.
"Who has which sprite" and "who can make which night" are the same question asked of
different nouns.

Three ways to say so were on the table. Rename the construct to something neutral and let
every list read blandly — which throws away the specificity that makes a collection page
read well. Let each list configure its own labels, which is a settings-in-syntax smell and
a schema nobody asked for. Or make the fence's *word* the vocabulary: one implementation,
a small named set, each word buying a handful of phrases. The third is what callouts
already do here — five spellings, one extension, a dictionary of kinds — so it is a shape
this codebase has already agreed to, and a new question costs a row rather than a
component.

The word rides in `Block.Tag` where the construct's argument already rode, survives the
round trip because the writer gives back what the source said, and reaches the reading
view through `SharedListView.Kind` — the *catalog's* word, not the tally's, so a grid
read from either page says the same thing. What it decides is: what the first column is
called, how the total and the score are phrased, what to say to somebody who has not
answered, and what a screen reader hears at a mark. What it decides beyond that is
nothing, which is the property that makes adding one safe.

The set lives in two places by necessity — `SharedListSyntax.Kinds` parses without an
editor, `ListVocabulary` says what each word calls things and cannot be in Core because it
is chrome — and a test pins them equal, the way the manual is pinned to the constructs it
documents. An unknown word still renders a grid in the commonest vocabulary rather than
failing, because a file written by a newer build should degrade to a readable list rather
than to nothing.

A poll came next and tested the shape, which is what it was for. Two of the three things
it needed were vocabulary — "Option" for the first column, "picked this" at a mark — and
the third was not: a poll is **one answer each**, which is a rule about what a file may
say rather than about how a grid looks. So it went beside the parser
(`SharedListSyntax.PicksOne`) and is enforced on the write path, where the tally is
actually produced; the reading view only reads it to draw a radio instead of a box, which
is a promise the write then keeps. Withdrawing is still allowed — one answer *at most*,
not one compulsorily.

A poll also does not name who answered what, which is the third thing its word decides
and the one worth being careful about. A roster and a schedule are asked *of* people —
"who can make Friday" has no useful answer without the who — while a poll is asked of a
group and answered by individuals, and being seen to change your mind in front of
everybody is a different act from voting. So the columns are withheld, the totals are
not, and the reader keeps their own column because they have to see their answer to
change it.

Withheld **in the answer the service builds**, not in the markup. Hiding a column in the
component while the response still carried the names would be the same lie as a checkbox
that records nothing — anybody could read the JSON. That also forced a row's total onto
the server, where it belongs anyway: it is the one number a reader cannot derive from
what they were sent, because on a poll there are no columns to derive it from.

What this is not is a secret ballot, and the manual says so. Every answer is still a file
its owner may share, an admin reads the disk, and editing the fence's word to
`:::collection` shows the columns that were there all along. Making it stronger would mean
answers that are not files, which is the one thing this design will not do. It hides who
from the people reading the list — the ordinary courtesy a poll wants — and claims nothing
more.

It also earned the vocabulary its first genuinely visual flag. A poll is read down its
rows ("how many picked Thai"), and so is an availability list ("how many can make
Friday"), while a collection is read across them ("how many do I still need"). So a row's
own total is a column the vocabulary asks for, rather than one every grid carries and two
thirds of them waste width on. The total is counted before any column is
withheld, so a poll reports honestly rather than reporting whatever this reader was
allowed to see.

The type names did not follow. `SharedListService`, `SharedListSyntax`, `SharedListWidget`
still say collection, which is now the name of the flagship question rather than of the
mechanism. Renaming them across Core, Web and Client is churn against no behavior, and
"a collection of everyone's answers" is a fair reading of what the service returns.

## The console is C#, and it plays in the reader's own browser

Playing an uploaded ROM meant a choice: wrap an existing emulator, or write one. Every
in-browser emulator worth having is JavaScript or WebAssembly compiled from C, and both
answers are the same answer — a vendored library, which is the one thing the standing
brief says the app does not do. Not out of purity: the whole point of `gatherum.js` being
a hundred and fifty lines is that a person can read all the JavaScript in the app in one
sitting, and half a megabyte of somebody's minified emulator ends that permanently.

So the consoles are C#, in `Gatherum.Client/Emulation`, and they run in WebAssembly — the
same home the editor already runs in. `IEmulatorCore` is the seam and it has two
implementations, which is why it is allowed to exist at all: a Nintendo Entertainment
System (`Nes/`, a 6502 with the dot-accurate picture chip games actually depend on, the
sound chip's five channels, and the eight cartridge boards most of the library shipped
on) and a Game Boy that is a Game Boy Color when the cartridge asks to be one
(`GameBoy/`, an SM83, the picture chip's mode timing, four sound channels, MBC1 through
MBC5). Both consoles have the same eight buttons, which is why `GamepadButtons` is one
enum and not two.

Both cores tick the rest of the machine from inside every memory access rather than
running an instruction and catching up afterwards. That is more code in the addressing
modes and it is the difference between a status bar that stays still and one that
shimmers: a game splits the screen by writing a scroll register partway down it, and an
instruction executed atomically puts the seam wherever the instruction happened to end.
The cycle counts then fall out of the accesses instead of being looked up, which is also
how the timing tests are written — they assert what the console charged, not what a table
claims.

**Where a save lives** was the one design question that is really about Gatherum rather
than about hardware. A battery-backed save is content, and content in Gatherum is a file
— which argues for writing it into the tree as a node, the way a shared list writes each
person's tally. But a ROM is a file *everybody who can see the page shares*, and a save is
one person's afternoon: filing it in the tree would either publish it or need a
per-reader mechanism the filesystem-is-the-record rule has no room for. It cannot go in a
table either — nothing outside `Users`, `ApiKeys` and `ReadingPositions` may live only in
the database. So it lives in the reader's own browser, exactly like an anonymous reader's
place in a book, with a download and an upload beside it so the choice is never a trap:
the save leaves as a `.sav` file, which is the format everything else in the world reads.

**The player refuses to run on the server circuit.** An Interactive Auto island renders on
a circuit until the WebAssembly runtime has downloaded, and sixty frames a second over a
websocket is not a game — it is a denial of service with a sprite on it. So the component
checks `OperatingSystem.IsBrowser()` and says what is happening instead of trying.

The picture reaches the screen through `SKCanvasView`, which was already in the graph
under the editor. The frame buffer is pinned once with a `GCHandle` and the bitmap is
installed over it, so a frame costs no copy at all — the core writes where Skia reads.
The browser's animation callback asks for frames and the wall clock decides how many are
due, because the console's frame rate (60.0988 and 59.7275) is not the display's on any
hardware anybody owns.

Sound is the one thing that needed JavaScript, and it got about forty lines: Web Audio has
no .NET binding, a page may not make noise before it is asked to, and each frame's samples
become a short buffer scheduled at the end of the last one. A worklet would be a second
script file, and there is only one.

**What is not here.** There are no save states — a save state is a snapshot of every
field in the emulator, which is a serialization format to keep compatible forever, and
the cartridges that save already save. There is no gamepad support: the Gamepad API is
poll-only and would be more JavaScript than the sound is. Neither is a decision against;
both are simply not in this change.

## Playing together: the wire carries buttons, and nothing else

Two people playing the same cartridge in two browsers is not a streaming problem. Both
machines are deterministic — the same cartridge and the same buttons on the same frames
reach byte-identical states — so what crosses the network is a byte of buttons per player
per frame and nothing else. No video, no audio, no state, once a game is under way.

That property is not free, and it is the reason the seam grew a save state before the
netplay did. Determinism has to be *stated* and *tested* or it rots: `IEmulatorCore` now
says a core may not read a wall clock, may not be random, and may not let anything the
player does outside the console leak into the machine. The last one is the interesting
case. Draining sound is a browser's business — how often it asks depends on whether the
tab is muted, whether it is in the background, how the audio graph feels — so the queue
of finished samples is deliberately *not* part of a save state. If it were, two people
playing the same game would fingerprint differently the moment one of them turned the
sound off, and the desync check would fire on nothing. There is a test that mutes one of
two consoles and demands their states still match byte for byte.

**Input-delay lockstep, not rollback.** Each player commits their buttons three frames
ahead; a frame runs only when everybody's buttons for it are in hand. Three frames is
about fifty milliseconds — a round trip a relay on the same continent makes comfortably,
and short enough to be hard to feel. Rollback (predict, then rewind and replay when you
guessed wrong) is better and needs the machine snapshotted several times a second; the
snapshot now exists, so that door is open, but the cost is a save-state format that has
to stay compatible with itself forever and a great deal more code to get subtly wrong.
Lockstep is the version that is obviously correct. When it falls behind it stalls and
says whose connection it is waiting for, which is worse than rollback and better than
quietly desyncing.

**The server relays and understands nothing.** It stamps which seat a message came from
— a client says what it pressed, never who pressed it — and forwards it. It has never
known which console a file is for and does not start now: how many seats a room has is
the console's answer, sent by whoever arrives first. A server that understood the game
would be a server that could disagree with it.

**A room is a node.** That is the whole authorization: whoever may see the ROM's page may
join its game, through the same `INodeAuthorizer.CanSee` that answers for the page itself,
and the endpoint sits inside the `/api` group so a node you may not see refuses a player
exactly as it refuses a reader. There is no anonymous door — sending your buttons into
somebody else's game is not reading. And the server checks that everybody holds the same
cartridge by the SHA-256 it already stores, because two machines running different bytes
would drift apart on the first frame and the drift would look like a bug rather than a
mistake.

**A WebSocket through the server, not a peer connection.** WebRTC would cut the latency
roughly in half, and would cost a signalling channel, STUN and TURN for the players
behind NAT, and a pile of JavaScript — Web Audio has no .NET binding and neither does
`RTCPeerConnection`. A relay through the instance both players are already signed in to
needs none of that, and `ClientWebSocket` works in WebAssembly over the browser's own
socket, carrying the session cookie because it is the same origin. For a wiki a group
self-hosts, the server is already in the middle of everything else they do together.

**Handing over the machine.** When the second player arrives, the first serializes its
console and sends it; the second loads it and both start from that frame. Joining a game
already going was the whole point of building the save state rather than starting
everybody at the title screen. Both ends then pre-commit the frames inside the delay
window as "nothing pressed", because those frames are already in the past by the time
anybody could have an opinion about them, and both ends have to agree about that.

**Desync is detected, not prevented.** Every sixty frames each machine fingerprints
itself (FNV-1a over the save state) and the fingerprints are compared a second later.
A mismatch stops the session and says so. It is not a cryptographic hash and does not
need to be: this is two machines checking they still agree, not one defending against
another that lies. Somebody who wants to cheat has an emulator of their own.

**The Game Boy does not get this.** Its `PlayerCount` is one and the player never offers
the button. Two people on a Game Boy meant two Game Boys and a link cable, which is a
second console to emulate and a serial protocol to synchronise — a different feature
wearing the same words.

## A third console, and what a second one had already settled

The Master System is the first console added since `IEmulatorCore` existed, which made it
a test of the seam as much as of the hardware. Most of it needed nothing: the same eight
buttons, the same one-frame-per-`RunFrame` contract, the same positional save state, and
netplay that worked on the first run because two ports on the front of a console is
exactly what the protocol was written against. Two things did not fit, and both were the
seam being too narrow rather than the console being strange.

**Sound is not always one channel.** A Game Gear has a register saying which of the four
channels reach which ear — it is the whole reason the register exists — and the audio path
handed the browser a single mono buffer. The choice was to implement the register and
throw its answer away, or widen the seam by one property. Discarding it would have left
code that does nothing, which is worse than either alternative, so `AudioChannels` joins
the interface and `queueEmulatorAudio` deinterleaves. A mono core answers 1 and behaves
exactly as before.

**The plastic is part of the machine.** The player drew a pad labelled A, B, Start and
Select because that is what both Nintendos are printed with. Sega numbered the face
buttons, put Pause on the console rather than the pad, and wired Reset to a line the game
reads and answers for itself — and a Game Gear has none of the four, only a Start button
beside the screen. `ButtonLabels` is one property carrying four strings, `null` meaning a
button the machine never had, and the player leaves those off the pad and out of the
sentence about the keyboard. The bits stay the same everywhere; only the printing moves.

Three details of the hardware were worth the code they cost. The paging registers live
*inside* work memory at the top of the address space, so a write to `$FFFD` both stores a
byte and moves a bank — and the first kilobyte never pages, because the interrupt vectors
are there and a program that could page them away could not return from anything. Nothing
in a cartridge file says which of the two boards is fitted, so Codemasters' own checksum
is what gives their board away; paging one as the other hangs on the title screen. And the
picture chip draws a line at the moment the beam finishes it rather than pixel by pixel,
because the registers that decide a line have stopped moving by then — what a game changes
in a line interrupt lands on the line after, which is precisely what a split screen is.

The Game Gear is not a fourth core. It is the same silicon behind a smaller window: it
draws the identical 256×192 picture and shows the 160×144 in the middle, so the crop lives
in the picture chip rather than in the player, and a cartridge that runs on one runs on the
other. Which console a file wants is a nibble in Sega's header, and where that header sits
is the one thing that made the search-side extraction more expensive — it is at the end of
the first bank rather than the start of the file, so identifying a cartridge now reads the
first 32 KB instead of the first 336 bytes. Still bounded, still constant, still nothing
like reading the file.

**What was not done.** The picture chip's four older modes, inherited from the TMS9918 the
Master System's was built out of, are not implemented: mode 4 is what the library was
written for, and a handful of early cartridges that use mode 2 will draw wrongly rather
than refuse. The FM sound board sold in Japan is not emulated either — a game that has an
FM soundtrack plays its ordinary one.

## A core from elsewhere, built at image time and wearing the same seam

The consoles in `Gatherum.Client/Emulation` were written here because the alternative was
vendoring somebody's minified JavaScript emulator, and the whole point of `gatherum.js`
being short is that a person can read all the JavaScript in the app in one sitting. That
argument holds for a NES. It does not hold for a Game Boy Advance: an accurate ARM7TDMI
with the picture and sound hardware around it is not a weekend, and writing a worse one
than mGBA to avoid a dependency would be pride rather than engineering.

So the Game Boy Advance is mGBA — and the reason it can be is that **the thing vendored
is not JavaScript**. mGBA compiles to a single WebAssembly module with no glue file: the
rule that bends is only that `gatherum.js` grew by a WASI host, which is Gatherum's own
code and readable as such. An Emscripten build would have shipped a JavaScript library
and was never on the table.

**The shim is Rust, and that was a preference rather than a finding.** Both were built
and measured: identical frame times, the same sixteen imports, 2.6% more binary for Rust.
The deciding argument was the owner's — code you like and can read in your own repository
beats code that is marginally simpler to build — and it is a better criterion than binary
size. What makes it work is `no_std`: the core brings wasi-libc with it, and Rust's own
copy would be a second one. The shim exists at all because libretro is built out of
function pointers and JavaScript cannot manufacture one, so a host written purely in JS
cannot reach the end of `retro_init`.

**Nothing is committed and nothing is fetched at run time.** `build-core.sh` pins mGBA by
commit and the toolchain by hash, and the Dockerfile runs it in a stage of its own so the
compiler never reaches the shipped image. That is the bargain `models/` already strikes.
A working copy that has never run the script simply has no Game Boy Advance core, and
those cartridges offer a download rather than a broken console — which is also what a
deployment that would rather not carry one gets.

Three things fell out of the work that were not obvious going in.

**A wall clock is how a vendored core breaks netplay.** mGBA asks the host for
`clock_time_get`. Answer honestly and two people playing the same cartridge drift apart
with nothing on screen to say why. The host feeds it a counter that advances one frame per
frame. It is written down in three places because it is the kind of thing that is
forgotten exactly once.

**A Game Boy Advance has ten buttons and the seam had eight.** `GamepadButtons` grew two
shoulders and the netplay wire grew a byte to carry them. Leaving them out was tempting
and would have made half the library unplayable.

**A cartridge's header does not always know what the cartridge does.** Every other format
here declares whether it saves; a GBA does not — the save hardware is found by scanning
the program for a marker. `RomHeader.Battery` became a `bool?` so the answer can be "the
format does not say" rather than a guess printed as a fact, and the console works it out
at run time regardless.

**What this is not.** `PlayerCount` is 1, and not because a Game Boy Advance had one
controller port — it is because playing together rests on two machines being guaranteed
to reach identical states, and that guarantee is the one thing a core from elsewhere
cannot give. The C# consoles are held to it by `EmulatorStateTests`; mGBA is held to
nothing here. Offering "play together" on a core nobody in this project can hold to that
line would be selling a promise that belongs to somebody else.

## bsnes, an Emscripten core, and a measurement rather than a promise

The previous section closed by saying `PlayerCount` was 1 because a vendored core's
determinism is somebody else's claim. That was the right answer to the wrong question.
"A guarantee is what a core from elsewhere cannot give" is true and beside the point: the
seam does not need a guarantee, it needs a fact. Two copies of the module, the same
cartridge, the same buttons on the same frames, `retro_serialize` compared every sixty
frames — either they agree or they do not, and that is measurable from outside.

bsnes was measured. Two machines agreed at all ten checkpoints over six hundred frames of
scripted two-player input, and a control run proves different buttons still reach
different states, so the agreement is not a core ignoring its pads. `PlayerCount` is 2 for
the Super Nintendo, and it is 2 for that reason and no other. mGBA has not been measured
and stays at 1; if somebody measures it, it changes.

Getting there took three findings.

**The core must not be allowed to know anything but the cartridge.** bsnes fills memory
with noise at power-on — faithful to the hardware, and fatal to two consoles that have to
start life identical. That is the core option `bsnes_entropy=None`, which meant teaching
the shim to answer `RETRO_ENVIRONMENT_GET_VARIABLE`. With entropy off the two machines
still differed by eight bytes, which turned out to be `emulator/random.hpp` seeding itself
from `clock()`. The wall-clock rule the last section wrote down three times had a fourth
place to be written: `libco-extras.c` answers zero. A seed that is never used still lands
in a save state, and a desync check comparing states would have cried wolf every frame.

**A value returned across an Asyncify fiber swap does not survive the trip.** bsnes runs
its processor, sound and picture as coroutines, and on Emscripten a coroutine swap unwinds
the WebAssembly stack out to JavaScript and rewinds it back in. `retro_serialize_size`
completed, every side effect happened, and the caller was handed a zero — a save state of
nought bytes with nothing to say why. Anything in the shim that can swap now parks its
answer in a static and a second call that cannot swap reads it. This is a property of the
mechanism rather than of bsnes, so the next coroutine-shaped core inherits the fix.

**A `bool` from WebAssembly is a number.** The shim's `gatherum_state_ok` comes back to
JavaScript as `1`, which the .NET runtime refuses to deserialize as a `System.Boolean` —
and the symptom was netplay stuck on "Handing the game over…" rather than anything naming
a type. Coerced at the boundary now.

### The rule that actually bent

"No JavaScript beyond `gatherum.js`" was the reason the last section gave for mGBA being
a WASI build and for an Emscripten one being "never on the table". bsnes is where that
stopped being tenable: WASI's libc++ ships without exception support and offers nothing to
build a coroutine swap on, so bsnes cannot be built that way at all. Emscripten emits an
84 KB loader beside the module, and it has to ship.

Faced with that, the argument written here first was that compiler output for a pinned,
hash-verified core is not a *library* — that the line was never "no `.js` files", and that
Gatherum has shipped 352 KB of exactly this kind of file since day one because Blazor's
own `dotnet.native.js` is an Emscripten build too. All of that is true, and it was still a
rationalization: it worked backwards from a build that had already succeeded to a reading
of the rule that permitted it.

The owner's answer is the real one, and it is simpler. **The rule is about the crucial
features.** The tree, the editor, search, sharing, auth — the things a person keeps their
life's notes in — are C# end to end so that everything running in a browser is code this
project can account for. Playing a cartridge is not one of those, and the ROM player is
scoped so it cannot become one: a console appears on a ROM's page and nowhere else, and a
build with no core at all serves a download link while the rest of the app is untouched.
Within that scope, a vendored core may bring whatever its toolchain emits.

That is a wider licence than the rationalization was, and worth stating plainly rather
than being pleased about: it puts the whole libretro catalogue within reach instead of the
few cores that happen to compile against WASI, and it means the next such core does not
need an argument, only a licence check and a determinism measurement. The bound is scope,
not file format — the wiki proper still takes no JavaScript library, hand-written or
otherwise.

What is given up is real and worth naming: the WASI build's import list is fourteen
functions long and can be read in a minute, and `bsnes.mjs` cannot. The mitigation is that
it is generated from pinned inputs by a script in the repository, so it is reproducible
rather than trusted. mGBA stays a WASI build for the same reason — nothing forces it to
change, and glue-free is better where it is free.

### Why bsnes rather than a smaller core

Asked for a Super Nintendo, the honest first answer was Supafaust — it needs no coroutines
and would have been a plainer build. The owner asked for bsnes, twice, and bsnes is the
more accurate emulator; where accuracy is the whole point of an emulator, deferring to it
over build convenience is the right call anyway. The cost is 2.3 MB of module against
Supafaust's 2.3 MB and a harder build, which is not much of a cost.

**Both cores link the same shim, unchanged.** That was the claim `core-shim` was written
on and this is the evidence for it: a C core built against WASI and a C++ core built
against Emscripten, sharing one Rust translation unit that knows libretro and nothing
about either.

## A controller is read as state, and mapped by position

Real gamepads reach a browser only through the Gamepad API, which Blazor has no binding
for — so the poll lives in `gatherum.js` beside the player's sound, the one place
hand-written JavaScript is allowed and only for what Blazor cannot do natively. It is a
poll rather than an event stream because that is what the API is: a snapshot per call.
The player takes one synchronous in-process call per painted frame, and the JavaScript
answers with the W3C standard layout packed one button per bit; deciding what those
positions *mean* stays in C#, next to the keyboard map that already made the same kind
of call.

The mapping is positional, not lettered. A modern pad prints A on its bottom face button
and every console here printed B there, so matching letters would cross the two buttons
a game most cares about; the bottom-and-right pair land on B and A, left and top on Y
and X, both shoulder rows on the one shoulder pair these machines had. The left stick is
folded into the d-pad in the JavaScript — partly because a retro console has nothing
else to give a stick to, and partly because the common USB "retro" pads report a
nonstandard mapping with their d-pad on those same axes, so the fold is what makes
exactly the pads people buy for these games work.

Three consequences were chosen deliberately. A controller feeds the same bits the
keyboard does, merged before the console or the netplay wire ever sees them, so playing
together is untouched — the wire still carries buttons and never learns what held them.
A controller needs no focus: blur releases the keys, whose key-ups would otherwise be
lost, but not the pad, which cannot stick because it is re-read every frame. And every
connected pad is OR-ed into player one — the second local port is netplay's job, and a
machine for choosing seats among local pads is a feature nobody asked for yet.

## Gecko, a core that is not libretro, and a disc that is not a cartridge

The owner asked for Gecko — the GameCube/Wii emulator with a web build — as a core, with
any patches kept local. Three things about it were not like the two cores before it, and
each one bent something.

**It is not libretro, so the shim has nothing to say to it.** Gecko is a Rust crate: a
constructor that takes a disc, a `run_until_vsync`, a render sink that receives GX
actions and an audio sink that receives stereo samples. `core-shim` is a libretro host and
would have been a second layer of translation over a first. So Gecko has a host crate of
its own, `native/gecko-host`, whose whole job is to be the shim's shape over Gecko's:
the same `gatherum_*` exports, the same integers, so that `gatherum.js` gained a third
`openCore` branch and nothing else in JavaScript or C# can tell which kind of core it
holds. That was the test of whether the flat surface was the right seam, and it passed:
the surface was designed around libretro, and a core that shares nothing with libretro
fits it in six hundred lines.

**It draws on the GPU, and the seam wants pixels.** Gecko has no software rasterizer;
its renderer is wgpu, and in a browser that means WebGPU. The player's contract is an
ARGB array. The host reads the composited output texture back into a staging buffer
every frame and maps it, and on WebGPU a map completes on a later JavaScript task —
so the picture handed over is always the previous frame's. One frame of lag is invisible;
a synchronous wait would have stalled every frame. The alternative — Gecko owning a
canvas on the page and the player drawing nothing — would have made a GameCube the one
console whose picture the player could not see, scale, or paint the play button over,
and it was not taken. The cost is that the GameCube is the one console that needs
WebGPU, and a browser without it is told so and offered the download.

**Its image is a disc, and a disc does not fit.** A GameCube disc is 1.46 GB. Every
cartridge before it came through `IAppData.ReadBytesAsync` into the .NET heap, was
hashed for netplay, and was copied into the core's memory. The .NET heap in a browser
cannot hold that, and copying it twice would be twice as bad. So a machine can now be
marked `LoadsByUrl`: the player reads the first kilobyte of the file to identify it —
enough, because both disc magic words and RVZ's header live there — and then hands the
core the file's *address*, and `gatherum.js` streams the response body straight into an
allocation in the core's own memory. That needed one new seam method, `ReadHeadAsync`, a
ranged read, and the host's memory ceiling raised from Gecko's 512 MiB to the 4 GiB a
32-bit WebAssembly memory can be. RVZ — zstd-compressed, decompressed as the game reads
— is what the manual tells people to keep discs as, because it is what makes the whole
thing reasonable rather than merely possible.

Three smaller findings:

- **Nintendo's silicon.** A GameCube boots from an IPL and its sound processor boots from
  a ROM, and neither can be shipped. Gecko replaces the IPL with a small free one from
  its `solstice` submodule and needs no BIOS. The DSP ROM it cannot replace — the sound
  processor is emulated instruction by instruction and the ROM is the code it runs —
  and Dolphin's team wrote a free one in 2013 and has kept it since; the build fetches
  the assembled bytes from Dolphin's tree at a pinned commit, checks their hashes, and
  bakes them into the host. GPL-2.0-or-later, upgraded to v3 like Mednafen would be.
  Gecko's own README lists Dolphin's coefficient table's hash as the one it expects,
  which was the confirmation the pairing would work.
- **The one patch.** "If patches are needed, keep them local" was the instruction, and
  one was: Gecko's memory card keeps its flash private, and a browser that is to keep a
  save has to read and write it. Twenty-two lines — two accessors and a downcast — as a
  `.patch` file in `gecko-host/patches/`, applied by `build-core.sh` after the clone and
  skipped if already there. The README's "never patching what is fetched" was about the
  licence obligation to make the source available, and a patch file in the repository
  meets it as well as an unpatched clone does; the sentence now says what it meant.
- **Reset without a reset line.** Gecko cannot reset a console; it can only build one.
  The disc is inside the old console and copying it is the one thing that must not
  happen, so the host takes the disc out of the dead machine and builds a fresh one
  around it, carrying the memory card across. That is what the Reset button does.

Left out, and said so in the manual: Wii discs, which the head reader recognises and the
player refuses by name — a Wii needs a NAND, an IOS and a Starlet the browser build does
not carry — and netplay, because Gecko has no save state to hand a second player and
nobody has measured two of it agreeing. `SaveState` on a core whose state measures at
zero now returns false rather than succeeding at saving nothing.

**The licence gap.** Gecko is GPL-3.0 and fits. The IPL replacement it compiles in comes
from a submodule whose repository declares no licence at all. Gecko ships it, credits its
author, and has since it began; that is not a grant. It is recorded in `native/README.md`
as a gap rather than resolved, because it is not this project's to resolve, and the owner
should know before an image with this core goes to anyone who reads licence tables.

## One Docker stage per core, and layers that keep only what they made

Asked whether moving the emulator code into a library of its own would let the build
cache more, the answer was no on both counts that a library could touch: the cores were
already a stage of their own, keyed on `native/` and untouched by any C# change, and
`dotnet publish` inside an image compiles every project from clean, so one project or
five costs the same. The caching that was missing was *inside* `native/`: one `COPY
native` and one `RUN` meant a line changed in `gecko-host` rebuilt bsnes, forty minutes
of it. So the stage is now three, each copying only its own core's inputs.

The second finding was worse than the first and had been there since bsnes. A layer keeps
everything the step left behind, and the core step left behind the Emscripten SDK, three
cores' sources and a Rust target directory that alone is close to a gigabyte — inside a
layer that `cache-to: type=gha,mode=max` then exported to a cache with a ten-gigabyte
ceiling. A cache that big is a cache that gets evicted, and an evicted core layer is
every core rebuilt on every run, which is the opposite of what the stage was for. Each
stage now deletes its build tree and the cargo registry in the same `RUN`, so what CI
keeps is what the next stage copies: a few megabytes in `dist/`.

Not done, and on purpose: a BuildKit cache mount for `obj/` and NuGet, which is the lever
for the *C#* publish step. It would help every project equally and is unrelated to where
the emulators live, and it is a different change with its own failure modes.

## Sticks stay analog, in one packing spoken end to end

The controller work above folded a pad's left stick into the d-pad, which is right for
every console whose games think in eight directions and wrong for the one that walks or
runs by how far the stick is pushed. So a stick now also travels as itself: four signed
bytes in one integer — left X, left Y, right X, right Y from the low byte up, positive
right and up — packed in `readEmulatorGamepadSticks`, spoken unchanged through
`StickState`, `runEmulatorCore`, and a `gatherum_set_sticks` export on both core shapes,
and unpacked last by the core that cares. One convention, tested where it is defined,
because a byte-order disagreement here would read as a stick leaning somewhere nobody
pushed. `IEmulatorCore.SetSticks` defaults to doing nothing — most of these machines
have nowhere to plug a stick in, and the C# consoles say so by not mentioning it.

The consumers differ by what their hardware was. The libretro shim answers
`RETRO_DEVICE_ANALOG` queries with the values scaled to libretro's range (and Y flipped,
because libretro says down is positive), which no current libretro core here reads — it
is the shim staying core-agnostic rather than a feature for bsnes. The Gecko host maps
them onto the main stick and the C-stick; the d-pad-as-stick fallback survives for
keyboards, yielding whenever the real stick is off centre. Triggers stay digital — a
shoulder press pulls the trigger all the way — because that is the existing behaviour
and the games that read a half-pulled trigger are rare enough to wait.

Two boundaries were drawn on purpose. Sticks never enter a shared game: netplay
exchanges buttons and nothing else, so the player applies analog only when playing
alone — the one stick console seats one player anyway, and a stick console that is ever
to play together must first put its sticks on the wire. And `gatherum_set_sticks` is
the one call `gatherum.js` treats as optional in a core module, so a `dist/` built
before sticks existed still opens and plays; it just cannot be told where the stick is
until it is rebuilt.

## Sound through a worklet, and a disc that arrives in pieces

The ROM player's sound was one `AudioBufferSourceNode` per frame, scheduled end to end,
and it crackled on every console. Two reasons, both at the seams: the browser resampled
each sixteen-millisecond buffer from the console's rate to the device's on its own, with
no knowledge of its neighbours, and a start time that is not a whole sample rounds to
one, so every seam was a gap or an overlap a sample wide. A third fault sat in the paint
loop: it read the sound once after running up to two frames, and the libretro shim keeps
one frame of sound and drops it when the next runs — so every catch-up frame on the
SNES threw away sixteen milliseconds of audio outright, and a stereo console's two
frames overran the player's buffer besides.

Now the samples go into an `AudioWorklet` holding a queue, and the worklet's own clock
draws them out, interpolating across every seam; the context is asked to run at the
console's rate so the browser's resampler does the quality work and the interpolation
only matters where it declined. It primes forty milliseconds before the first sample
plays, fades out rather than cuts when starved, and trims a queue that has grown past a
quarter of a second — the only direction a paint loop can drift. A worklet wants a
second script file, which this project does not have: the processor is a string in
`gatherum.js`, loaded from a blob, so the one-file rule holds in letter and in spirit.
The player reads audio after every frame it runs, and drains a muted console too, so
nothing stale bursts out when the sound comes back.

A GameCube disc would not upload for a plainer reason: the ceiling was 512 MB and a
disc is 1.4 GB. Raising the number was the small half. The WebAssembly home sends an
upload through the browser's HTTP client, which buffers a request body whole before
sending it — a disc image is bigger than the heap it would have to sit in, and the
runtime has no streaming request to offer that every browser honours. So `HttpAppData`
now sends a file in eight-megabyte pieces to `/api/uploads`, a staging file in the
server's temp directory that `finish` hands to `FileService` exactly as a multipart
upload would have. `IAppData` did not change: the components still hand over a stream,
and the server circuit still streams it straight to the service. The staging map is in
memory on purpose — an upload cannot outlive the process it began in, and one that tries
gets a 404 to retry from, which is what an interrupted upload is. The multipart endpoints
stay as they were for the drop zone and for API clients, at the same 2 GB ceiling.

## A GameCube that showed nothing, and the five reasons it did not

Super Monkey Ball 2 played as a black screen in Firefox, with WebGPU on. The report named
the browser, so the first question was whether Firefox's WebGPU was the difference, and
the answer came from a harness that drives `gecko.mjs` exactly as `gatherum.js` does with
no Blazor in the way: Chromium was black in the same way, at the same instruction. Neither
the readback nor the page was at fault — the picture was crossing every frame, and it was
black because the console had nothing to show. Gecko was silent about why, because the
host had no `tracing` subscriber; it has one now, warnings and up into the browser
console, which is the same place the WASI shim's cores print and where a person debugging
a cartridge looks.

What it was, in the order it was found, each one uncovering the next:

- **The boot block's clock speeds were zero.** The IPL writes the bus and CPU clocks at
  `0x800000F8` and `0x800000FC`; Gecko's IPL-less boot writes the RAM and ARAM sizes and
  not those. The SDK derives every tick conversion from the bus clock, so
  `OSMillisecondsToTicks` was zero and every timed wait built on it was over before it
  started — the DVD library's post-reset settle among them, which put every thread to
  sleep and left the CPU in the scheduler's idle loop. Patched into the boot, where the
  other boot-block words are written.
- **The IPL mask ROM, SRAM and clock were off the bus.** Gecko attaches that EXI device
  when it boots from a real IPL and not otherwise, so a game reading the console's
  settings got a stub. The host attaches it with a blank ROM — the real one cannot be
  shipped, and a font read from it comes out empty — and the SRAM and clock behind it
  answer as a fresh console would. The clock is the determinism rule's business: Gecko
  read `SystemTime::now()`, which panics on `wasm32-unknown-unknown`, and a patch makes
  the browser build read a counter the host sets from the frame count instead.
- **The write-gather pipe dropped everything when the command processor was unlinked.**
  Gecko landed a burst in memory and advanced the PI write pointer only under the CPU–GP
  link. A game running two FIFOs — the CPU filling one while the GP reads the other, the
  two swapped every frame, the way Amusement Vision's engine does it — fills the idle one
  through that pointer alone, so nothing it drew ever reached memory, the GP never
  finished, and the game printed "GP WAIT Timeout" to a port nobody was reading. Twilight
  Princess stays linked and never noticed. The patch drains the pipe regardless and lets
  the link govern only the CP's own pointer and distance, which is what the hardware does.
- **The DSP's DMA-busy bit was writable and was the audio stream's.** Gecko copied bit 9
  of the DSP control register from every CPU write, and set it for as long as the audio
  DMA played. The SDK writes that register read-modify-write, and a sound driver that
  polls `ARGetDMAStatus` before every ARAM transfer — which is that bit — therefore waited
  on a bit that was never going to clear. Patched to be the ARAM DMA status it is.
- **The renderer panicked on a batch left over from the previous frame.** Draws are
  batched until a non-draw action arrives, and the host, following Gecko's own web build,
  swapped the vertex scratch out at every frame boundary — so a batch still pending at
  the boundary indexed into a buffer that was now empty, and Twilight Princess died on
  frame nine with a slice out of range. The host now flushes pending draws before the
  swap. Gecko's web page only ever loads a DOL and had not met this.

Four of the five are patch files beside the memory-card one, each a hardware fact rather
than a taste, each small enough to read, and each a candidate to send upstream. The
question "is this one disc failing to initialise?" was the right one and the answer was
no: the same disc stalled natively under Gecko's own headless benchmark, at the same
place, and so would any game that reset its drive and waited, ran an unlinked FIFO, or
polled the ARAM DMA — all of which are ordinary. Gecko's `dev` branch was checked and is
seven commits of rendering and input work over the pin; none of it touches these paths.

What was measured, in real Firefox 154 on Linux through the harness: Twilight Princess
draws its health-and-safety screen with sound; Super Monkey Ball 2 boots through its
publisher screens to its title screen with sound. Both run slowly — Gecko in a browser is
its interpreters, and a heavy scene is eight frames a second — which the manual already
says and this does not change.

## Gecko becomes a fork and a submodule

Five patch files applied with `git apply` at fetch time were a fork with worse tooling:
no history, no path upstream, and every new fix written against a throwaway clone under
`native/build/`. So Gecko is now `sand-head/gecko`, whose `gatherum` branch sits on
upstream's `dev` — the owner's call, since an emulator this young moves fastest there and
the seven commits it had over `master` were all rendering and input work that built and
played unchanged — and carries each fix as one commit in upstream's own voice, terse and
lowercase, with no co-author trailer, so that any of them can be offered back as it is.
The `patches/` directory is gone.

Gatherum holds it as a submodule at `native/gecko` rather than fetching the fork at a
pinned commit, because the whole point is that it is ours to change: a fix is edited,
built and tested in place, committed in the submodule, pushed, and the pointer bumped.
The README's "the cores are not here" keeps its spirit — a submodule is a pointer, not a
copy, and Gatherum's history carries none of Gecko's — and loses a little of its letter:
a clone now wants `--recurse-submodules`, CI's checkout fetches the submodule and then
only the two of Gecko's own that are compiled in (the third is test data), and the Docker
context carries the checkout because the core stage has no `.git` to fetch with.
`build-core.sh` refuses, with the command to run, when either is missing. mGBA and bsnes
are unchanged: fetched, pinned by commit, never patched.

## Where a GameCube frame's time went, and two idle loops

Asked whether the frame rate was the integrated GPU's doing, the answer from the kernel's
per-client DRM accounting was no on both counts: Firefox had put WebGPU on the discrete
card, and that card was one percent busy. The Firefox Profiler on the thread running the
console said where the time was instead, and it was not where the "interpreter is slow"
story put it. Twilight Princess spent 57% of every frame in the DSP — the sound processor,
emulated instruction by instruction — and a count of instructions per frame made the
shape of it plain: five million DSP instructions a frame, on a chip that can execute 1.35
million in that time, and eight million CPU instructions a frame on a game sitting at its
health-and-safety screen doing nothing. Both were idle loops run in full. Gecko's JIT
skips both kinds; its interpreters, which are what a browser gets, skipped neither.

Two commits on the fork. The interpreter now classifies a backward branch the moment it
closes a loop, with the JIT's own classifier moved to `gekko::idle` so both modes share
one definition of "reads only what an interrupt or a DMA could change", and jumps the
clock to the next scheduler deadline — the decrementer and time base are derived from
that clock, so an alarm or a timed wait still lands where it would have. And the DSP's
wait table learns the Zelda microcode's three idle loops beside the AX ones it knew: a
flag in its own DRAM that its mail handler sets, a command ring whose read and write
words have drawn level, and the `LR @CMBH` spelling of the mailbox poll; Dolphin's
analyzer lists the same three. The parked condition reads the words the loop reads, and
requires no interrupt pending, because only the handler can change them.

Measured in Firefox on the same discs: Twilight Princess from 128 ms a frame to 8 —
full speed with time to spare — and Super Monkey Ball 2 from 67 to 50, where a profile
now shows no hot spot at all, only the work of a real 3D scene spread across the
interpreter, the GX FIFO and the vertex uploads. That remainder is the price of no JIT,
and no patch buys it back. Two things tried and found worthless are worth recording:
`wasm-opt -O3` shrank the module by a quarter and changed the frame time by nothing, and
the chip the WebGPU device lands on does not matter, because the picture is never waited
for.

## A cached interpreter, and what it was worth

Asked for a JIT and told why a browser cannot have Gecko's — Cranelift emits machine
code, and a page can only run WebAssembly — the owner asked for the cached interpreter
instead. It is in the fork now: a block is scanned once with the JIT's own scanner
(`gekko::block`, moved out from under the JIT so both can use it), every instruction in
it resolved once to the number of its handler, and the block run to its end or to the
first branch that leaves it, with interrupts taken at block boundaries as the JIT takes
them and the JIT's idle classes skipping to the next deadline after one pass. The
resolver is generated at build time from the text of the dispatch tables chipi writes,
because chipi does not know about it: every `_dN` decoder gets an `_rN` twin returning a
number, and `execute` is one `match` over those numbers. Blocks are keyed by pc through
a direct-mapped table ahead of a map, register the RAM lines they span, and are dropped
when a store or a DMA touches one — the same pending-line mechanism the JIT invalidates
from, which is no longer the JIT's alone. The hooks the debugger builds with run at the
same places they ran before.

Measured in Firefox on the same two discs, against the idle-skipping build before it:
Twilight Princess 7 to 5 ms a frame; Super Monkey Ball 2 49 to 43. Two findings shaped
the second number. Resolving the handler once saved almost nothing — Firefox walks the
table tree cheaply — and what saved the twelve percent was calling the handler through a
`match` rather than through a function pointer, which in WebAssembly is a checked
indirect call. And a count showed the cache doing exactly its job (400k blocks a frame,
seven instructions each, no misses to speak of) while the frame stayed where it was,
because a title screen's cost is now a third interpreter handlers, a third the block
loop and a third GX: the FIFO decode and the vertex uploads through WebGPU's
`writeBuffer`, which is Firefox's memcpy, not ours. From 67 ms a frame at the start of
the day to 43 is the whole of what a browser gets from this core without a compiler.
The block scanner and the numbered handlers are the front half of a WebAssembly-emitting
JIT, should anyone build the back half.

## The back half: a JIT that emits WebAssembly

The owner asked for the back half, and it is built. A block the cached interpreter has run
sixteen times is handed to `gekko::wasmjit`, which emits a WebAssembly module of one
function — importing the host's memory and function table, so it reads the console where
the interpreter does — and the host (`TableCompiler`, over js-sys, so no JavaScript text
ships) instantiates it and puts its function in a slot of the host's own table. To Rust
compiled for wasm32 a table slot *is* a function pointer, so the cache calls a compiled
block the way it calls anything else, and the linker flag that makes the table growable
is the only build change. What the emitter translates it translates inline: integer
arithmetic, rotates, compares and the condition register, branches, loads and stores
against RAM by the bus's own fast path (segment 8 or C, in range, and not a line the
cache holds code on), and the floating-point and paired-single arithmetic a game spends
its time in. Everything else calls the interpreter's handler by its own table slot,
which the generated tables know by the `OP_*` constant it is specialised on — not by
address, because a native test build has several copies of a generic function.

**The interpreter is the oracle, and three tools hold the emitter to it.** A validation
switch runs every compiled block twice, compiled and then interpreted from the same
registers and — the compiled run's RAM stores having been noted and undone — the same
memory, and reports the first blocks that disagree; a block that writes a device register
and reads it back disagrees with itself, which is the one kind of noise it makes. A
translation mask turns each class of translation off from the harness, for bisecting. And
a native test runs emitted blocks in `wasmi` over an image of the console beside the
handlers on the console itself, for every arithmetic instruction the emitter knows, from
tables of awkward operands; it found the last bug in minutes after the browser had shown
it only as a few floats off by a bit in vertex memory. Four things bit on the way and are
worth writing down: a halfword store swaps bytes with a formula that is only right when
the high half is already zero; the cycles a block owes must be settled before any call
the bus might answer with the clock in hand, because the interpreter charges an
instruction before it runs, and a device read that sees a clock five cycles behind is a
timer that drifts; a `flush` inside a conditional branch loses the cycles on the other
path; and a block that ends early where the interpreter would not have creates an
interrupt point the interpreter never had. Both discs now match the interpreter frame
for frame, registers, clock and RAM, over the whole run measured.

**What it is worth, so far: less than the mechanism promised.** With every register the
interpreter's, Super Monkey Ball 2's title screen went from 43 ms a frame to 39. Keeping
the registers a block touches in WebAssembly locals — read from the console once, written
back before a handler, the bus in the interpreter's hands, or the way out, forgotten
after a handler may have changed them, and the two arms of a quantized access merged
through the console — took a same-session A/B to 50 against 35 on that screen, a quarter
to a third off, and Twilight Princess was never CPU-bound. A dump of a typical block
shows why the rest is still there: thirty-six WebAssembly operations per GameCube
instruction, most of them the memory fast path's segment and range checks and byte swaps,
and the condition-register updates that read and write the console for every `Rc` and
compare. Those, the Rust loop that dispatches blocks (a tenth of the frame), and the GX
decode and vertex uploads are each their own piece of work, and each is measured against
the same oracle before it lands.

## Three things the first deployment of the GameCube JIT found

The owner deployed it and played: Super Monkey Ball 2 was slow and crackled, Twilight
Princess was black. Reproducing the second in the real app, not the harness, took the
tools from the previous entry — the app's console fingerprinted every couple of seconds
against the harness's per-frame table — and they said the emulation was identical
frame for frame while the canvas stayed black. The picture was in the core's frame
buffer with alpha zero on every pixel: that game never writes alpha, Super Monkey Ball 2
happens to, and a bitmap declared opaque still composites those pixels as nothing. The
seam promises an opaque frame, so the host's readback now forces alpha on every pixel
it copies out.

Behind it were two more, found by running longer. A browser gives every WebAssembly
module its own page of executable memory, and Twilight Princess compiles tens of
thousands of blocks, so one block per module ran Firefox out of it in a minute ("out of
memory" from `WebAssembly.Module`, then a panic in the WebGPU backend when its
allocations failed too). Hot blocks are now compiled thirty-two to a module, or fewer
when the first has waited four thousand block runs, and a released slot is pointed at a
function that only traps, because the table is what keeps a module alive. And the
browser's memory grew by tens of megabytes a second with the JIT on or off: the renderer
queues every EFB copy to RAM as a writeback with its own staging buffer, the native
front end drains those by waiting on the device, and the WebGPU host — which cannot
wait — never drained them, so they piled up, and the games never got those copies in
RAM either. The drain is split on the fork into a hand-over and a finish; the host maps
each copy as it is handed over and finishes it on the frame it arrives, the way the
picture already arrives a frame late. That lag is a compromise the native core does not
make, and it is the reason the GameCube stays a one-player machine here: two consoles
whose EFB copies land on different frames are not the same machine.

Cache busting was asked about and is not a factor: every core file is served with
`Cache-Control: no-cache` and an ETag, so a browser revalidates each time and never
pairs a stale loader with a new binary. The crackle is the frame rate: on the owner's
machine the title screen runs at about forty percent of speed, the player runs up to
two frames a paint to catch up, and the audio worklet starves between paints. That is
the work of the previous entry's last paragraph, not a bug in the sound.

## Firmware belongs to the instance, and only where something reads it

The owner asked for BIOS uploads "on a per-instance level", and the phrase settles the
design. A console's boot ROM is not one person's document: two readers of the same
Gatherum play the same console, so a file kept in somebody's root would be a console
that behaves differently depending on whose page you opened. It goes in the storage
root's own `.gatherum/firmware/`, beside the users' directories rather than inside any
of them — the one place a file belongs to the instance. The scan skips it as it skips
every other sidecar, so it is not a node, has no owner, appears in no listing, and is
still a plain file that travels with a backup of the storage root. `IFileStorage` grew
the four calls that reach it, so the seam stays the only door to the filesystem.

**Anybody signed in may add one or take one away.** Gatherum has no operator role, and
inventing one for this would be inventing an access model for a single feature; the
honest alternatives were "everybody" or "nobody", and nobody makes the feature
pointless. The bytes are only ever served back to a signed-in browser, which is the
player fetching them for its core — an anonymous reader of a public ROM page gets a
console without firmware, which is a console that works.

**The catalogue is closed and a file is taken only at its exact size.** A machine takes
exactly the files listed for it and nothing else, so the storage cannot become a
dumping ground, and a file of another length is a different file whatever it is called.
The list has one entry: the GameCube's IPL, which the console reads its font out of.
The Game Boy Advance's BIOS belongs there too and is deliberately absent, because
nothing could read it yet — mGBA opens it as a file, our libretro shim answers every
WASI file call with "no such file", and an upload that lands somewhere no core looks is
worse than no upload at all. Listing a machine is the last step of supporting it, not
the first.

**Nothing here needs firmware.** Every console plays without it — the GameCube on
Gecko's free IPL replacement, which keeps doing the booting even when a real IPL is
present. That is what makes every way of not having a file the same quiet no: none
uploaded, a reader who is not signed in, a core built before the call existed. The core
gained `gatherum_set_firmware`, which takes the bytes before the console powers on and
ignores a ROM of any other length, and the loader hands them over between setting the
core's options and booting it.

## A frame's largest cost was a buffer nobody had filled

The owner reported the GameCube slow across the board, and named the case: Twilight
Princess is fine on the screen it starts on and not fine once the game behind it starts.
Measuring that meant reaching it — a scratch harness driving `gecko.mjs` past the health
screen with the A button, into the attract demo, which is real gameplay rendered by the
real engine. There it cost **56 ms a frame**, three and a half times the budget.

**A frame was timed by its phases rather than guessed at.** The Gecko profiler was tried
first and gave one wasm code address holding 99.5% of the samples, which says nothing, so
the host was instrumented instead: a clock lent to the fork through one extern function,
accumulators around each phase of `gatherum_run`, then around the DSP, the GX FIFO decode,
each kind of renderer action, and the two halves of a draw flush. All of it temporary, and
none of it in the tree. What it said, per frame: the console 23 ms (the Gekko 12, the DSP
7, the FIFO decode 4), the renderer 20, the EFB writebacks under 1.

**The renderer's twenty milliseconds were nearly all one line.** `wgpu`'s WebGPU backend
implements `write_buffer_with` by allocating and zeroing a staging buffer of the size
asked for and copying the whole of it across on drop, and the size asked for was the draw
buffer's whole *capacity* — sized to the heaviest frame ever seen, written in full four
times a frame however few draws were in it. Each section is now written with the bytes it
actually holds, the vertices packed through a scratch vector kept between flushes rather
than a fresh zeroed one each time. Twelve milliseconds a frame became one and a third, and
the picture came back pixel for pixel identical, which is the test that matters for a
change that only moves bytes.

**The second was an allocation per action.** The queue between the console and the renderer
carried each action's new vertices as their own `Vec` — five thousand nine hundred
allocations a frame. They share one vector now and an action carries a range into it, and
the two vectors go back to the queue emptied so the next frame writes into memory already
there. Measured together, in a same-session A/B against the build without them: Twilight
Princess in the attract demo **56.3 ms a frame to 34.3**, and Super Monkey Ball 2's title
screen **52.5 to 39.1**. A third of a frame, for forty lines.

**Where the rest of it is, measured, so nobody has to guess again.** Of what is left, the
Gekko is 12 ms, the renderer 9, the DSP 7 and the GX FIFO decode 4. The Gekko's number is
the interesting one, because the JIT is not the problem: 285,699 compiled block runs a
frame against 33 interpreted, 1.68 million instructions a frame — and only **5.9
instructions per block**, at 41 ns a block. A block that short spends most of itself
arriving and leaving: the cache lookup, the type-checked indirect call, the prologue that
loads its registers into locals and the epilogue that writes them back. That is the next
piece of work, and WebAssembly has the instruction for it — the build already enables tail
calls, so a compiled block can `return_call_indirect` straight into the next one instead
of returning to the Rust loop, which is block chaining under another name. The DSP is a
plain interpreter here (its Cranelift JIT is native-only) and wants the same treatment the
Gekko got. The renderer's nine are 5,233 draw actions a frame averaging 4.9 vertices each,
of which 97.7% are back to back with identical state — the backend already merges them
down to 62 real draws, so what is left is the work done *before* the merge decision, and
the fix is to reach that decision sooner.

**One thing tried and not kept.** The draw record each of those 5,233 draws asks for is a
kilobyte and a half, allocated and zeroed fresh every time; pooling them was worth nothing
measurable, and two runs with the pool drew a picture that differed from two runs without
it by a little everywhere. Unexplained, and not chased, because there was no speed in it
either way — but recorded, so the next person to think of it knows to look there first.

## The Gekko's twelve milliseconds, and three ways at them

The owner asked for the Gekko number next, in one branch. It is about twelve of a
thirty-four millisecond frame, and the first thing was to stop guessing at it: each block
the emitter compiles now carried a tally of what it made of each instruction, added up
every time the block ran. A frame of Twilight Princess in its attract demo, executed:
**1.67 million instructions across 285,180 blocks — 5.87 instructions a block** — of which
623,639 are plain integer work, 484,718 are loads and stores, 284,243 are branches,
267,035 are floating point, 133,501 write the condition register and 12,914 fall back to
an interpreter handler. Blocks of six instructions was the shape of the problem, and three
things were tried against it.

**Block chaining, and why a browser does not want it.** A block that ends where another
begins should go straight there. It was built: the cache keeps a table of which address
has a compiled block in which table slot, in memory the blocks read for themselves, and a
block's way out checks the four things the loop above it checks — that the console has
cycles left before the scheduler's deadline, that no interrupt is waiting, that nothing has
been written over code, and that there is a block at the address — and then tail-calls it.
It worked exactly as intended: Rust dispatches a frame fell from 285,699 to 76,006. It was
**three milliseconds a frame slower**. Firefox's `return_call_indirect` costs more than the
dispatch it saves; an ordinary `call_indirect` with a depth counter to bound the stack was
still slower. The lesson is worth more than the code: the dispatch loop is not what the
frame is spending, and a mechanism that removes three quarters of it can still lose. It is
not in the tree.

**Shaving the emitted code did not move it either.** Two attempts, both sound on paper.
The guard around every load and store asked two questions — that the address is one of the
two segments RAM answers on, and that its offset is inside RAM — where one fold answers
both: flipping the top bit and dropping the next lands segments 8 and C on the same offset
and leaves everything else far outside RAM. Five bytes off every one of 484,718 accesses a
frame. And the condition register, which 133,501 instructions write and every conditional
branch reads, moved into a local the way the general registers already do. Neither could be
told from the noise, and the second made a short block *bigger*: at six instructions a
block, loading the register once and writing it back costs more than the two memory
accesses it saves. The fold is kept, because it is strictly less code and a test proves it
accepts exactly what the two questions did, at every segment and every boundary. The local
is not kept, because nothing measured says it earns its place.

**What did move it: blocks that carry on.** The scanner ended a block at every conditional
branch. One that goes *forward* need not end it — the emitter already knows how to make a
branch a way out, so it emits the taken path as an exit and keeps translating the code
after it, which the interpreter has always handled (a step whose `nia` is not where the
next one starts leaves the block). A branch that goes *backward* still ends it: that is a
loop closing, it is what the idle classifier reads, and it is where the next look at the
interrupts belongs. Blocks went from **5.87 instructions to 8.89**, and the console phase
from 22.65 ms a frame to 21.97.

**Two things this costs, and one it does not.** Interrupts are taken at block boundaries,
so longer blocks take them a little later — six instructions of granularity became nine.
That changes what a run computes, and the frame a run draws is not the frame the previous
build drew; it is still the same frame every time for a given build, which is what
determinism means here. And it is not a translation bug: the emitter is held against the
interpreter for every compiled block, in the browser on the real disc, and the only
disagreement in the whole run is the one the mechanism has always reported — a block that
reads a video register, writes it, and reads it back cannot agree with a second run of
itself. Two tests in the fork hold the new shape: one runs a block with a forward branch in
the middle both ways, taken and not, against the interpreter, and one proves the folded
guard.

**Where this leaves the frame.** Three well-founded attacks on the Gekko produced one
three-percent win between them, and the two that failed say something the successful one
does not: the dispatch is not the cost and the guards are not the cost, which leaves the
work itself and the memory it touches. There is no lever of the size the last round found.
The DSP is the opposite case and the obvious next one — seven milliseconds a frame of a
plain interpreter, with no compiler at all, where the Gekko already has one.

## The DSP had no compiler and did not need one yet

The Gekko's twelve milliseconds had no lever left in them; the DSP's seven turned out to
be mostly waste. Measured first, as ever: a frame of Twilight Princess runs **317,773 DSP
instructions in 6.83 ms — 21.5 nanoseconds each**, against the Gekko's 7.1 with a compiler
behind it. A histogram of where the DSP's program counter goes said the work is real: the
busiest address is 2.8% of the steps and the top twenty are forty percent between them,
which is a handful of tight mixer loops, not a spin nobody had noticed.

**What a step was paying for.** Reaching one instruction meant two reads of instruction
memory, a word built out of their bytes, one walk of the decode tree to find how long the
instruction is and a second walk to find its handler — ending in a call through a table,
which in WebAssembly is a checked indirect call. Every bit of that is the same each time
the instruction runs, and instruction memory only changes when a microcode is uploaded.

So it is worked out once and kept, in a cache with an entry per instruction address —
0x1000 words of IRAM and 0x1000 of IROM — holding the instruction, how long it is, and the
number of its handler. Running it is a load and a jump: the number comes from a `resolve`
generated beside the tables chipi writes, exactly as the Gekko's block cache already does,
and `execute` runs it through one `match`. The whole cache is emptied in
`rebuild_wait_table`, which is already the one place both writers of instruction memory
call. **6.83 ms a frame to 4.00**, on the same 317,773 steps and the same audio out.

**Two ways it is held to the old path.** The handler an instruction reaches must be the
handler `dispatch` would have walked its tables to reach, and nothing else checks that the
generated table stayed in step with the one chipi wrote — so a test runs every one of the
65,536 opcodes both ways over the same console and compares the registers, DRAM and IFX
after each, with an opcode that panics required to panic both ways. And the harness now
counts the audio samples a run produces: 881,040 over 550 frames, the same to the sample
before and after.

**Where it lands.** The console phase — the Gekko, the DSP and the GX FIFO decode
together — went from 21.77 ms a frame to 19.17 in a same-session A/B. The frame median
moved about 1.2 of that; the rest the renderer takes up, which is worth knowing on its own:
past a point this frame is gated by how fast the GPU process accepts work, not by how fast
the console produces it.

**What is left of the DSP.** Twelve and a half nanoseconds an instruction, against maybe
five or six for an interpreter with nothing left to give and three or four with a compiler.
The remaining candidates are all fractions of a millisecond — the accumulator snapshot
taken before every instruction that has an extension field, which only three of the
extension handlers read, is nine percent of steps wasted and about a third of a millisecond
— so the next real multiple here is the same one the Gekko got: blocks, and a JIT that
emits WebAssembly. The machinery for that already exists next door.
