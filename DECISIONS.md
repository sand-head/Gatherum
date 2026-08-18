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
follow-up rather than done silently.

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
`DocxConverter.ToMarkdown` for search.
