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
