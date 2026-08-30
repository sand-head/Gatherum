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
gap that will be felt first — see COLLECTIONS.md, which ran into it.

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
`COLLECTIONS.md` is the design; this is what got built and what it cost.

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
view through `CollectionView.Kind` — the *catalog's* word, not the tally's, so a grid
read from either page says the same thing. What it decides is: what the first column is
called, how the total and the score are phrased, what to say to somebody who has not
answered, and what a screen reader hears at a mark. What it decides beyond that is
nothing, which is the property that makes adding one safe.

The set lives in two places by necessity — `CollectionSyntax.Kinds` parses without an
editor, `ListVocabulary` says what each word calls things and cannot be in Core because it
is chrome — and a test pins them equal, the way the manual is pinned to the constructs it
documents. An unknown word still renders a grid in the commonest vocabulary rather than
failing, because a file written by a newer build should degrade to a readable list rather
than to nothing.

A poll came next and tested the shape, which is what it was for. Two of the three things
it needed were vocabulary — "Option" for the first column, "picked this" at a mark — and
the third was not: a poll is **one answer each**, which is a rule about what a file may
say rather than about how a grid looks. So it went beside the parser
(`CollectionSyntax.PicksOne`) and is enforced on the write path, where the tally is
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

The type names did not follow. `CollectionService`, `CollectionSyntax`, `CollectionWidget`
still say collection, which is now the name of the flagship question rather than of the
mechanism. Renaming them across Core, Web and Client is churn against no behavior, and
"a collection of everyone's answers" is a fair reading of what the service returns.
