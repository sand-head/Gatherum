# AGENT.md — the standing brief

Read this before touching the repo. It stays true across milestones; update it when a
milestone changes something it describes. `CLAUDE.md` is a symlink to this file, so a
coding agent that looks for its own name finds the one brief rather than a second copy
to drift from.

## What Gatherum is

A self-hosted knowledge base for a person or a group where **pages and files are the same kind
of thing**: every item is a `Node` in one tree with categories, links, versions, and
searchable text — and a page is simply a node whose file is Markdown. One tree, one
search, one login, one API, plus an MCP server so agents are first-class users.
C#/Blazor end to end — static shell with Interactive Auto islands for everything
interactive: the first visit renders on a server circuit while the WASM runtime
downloads, every later visit runs fully in WebAssembly over `/api` (the only JS is
`wwwroot/js/gatherum.js`, plus the pager script `EpubChapterHtml` injects
into rendered EPUB chapters — see DECISIONS.md) — PostgreSQL, deployed as a single rootless
Podman container behind a TLS-terminating reverse proxy with Authelia for OIDC.

## Build, run, test

```sh
dotnet workload install wasm-tools     # once; the editor island relinks SkiaSharp
# Postgres (once):
docker run -d --name gatherum-pg -p 5432:5432 -e POSTGRES_DB=gatherum \
  -e POSTGRES_USER=gatherum -e POSTGRES_PASSWORD=gatherum pgvector/pgvector:pg16

dotnet build                             # the first build fetches the 23 MB embedding model
dotnet run --project src/Gatherum.Web    # http://localhost:5140, dev auto-login
dotnet test                              # needs Docker for Testcontainers
dotnet test --filter "FullyQualifiedName~FileVersionTests"    # a single class
dotnet test --filter "DisplayName~Restore_brings_back"        # a single test
```

Config via env vars `Gatherum__*` (see README table). No OIDC configured ⇒ dev
auto-login. Migrations: `dotnet ef migrations add <Name> -p src/Gatherum.Infrastructure
-s src/Gatherum.Infrastructure -o Data/Migrations` — they apply on app startup.

## Repo map

- `src/Gatherum.Core` — domain entities, `GatherumDbContext`, **application services**
  (`Services/`) where every business rule lives (`NodeService` = tree/links/title
  resolution, `CategoryService` = the taxonomy and what is filed in it, with
  `CategoryIndex` the one-snapshot-per-operation view of its graph,
  `FileService` = bodies/versions/text editing, `BookmarkService` = a URL captured as
  a file node and captured again on demand, `SharedListService` = a shared list's
  catalog fused with every tally of it, and the one write, which is always the
  caller's own tally), `Markdown/MarkdownContent`,
  `Markdown/WikiLinkSyntax` and `Markdown/SharedListSyntax` (the conventions a body
  carries, read server-side without an editor), the seam
  interfaces in `Abstractions/`, `Services/MediaAnalysisQueue` — the hand-off from
  an upload to the background analyzer — and search's two halves: `SearchService` fuses
  them, `EmbeddingService` owns the vector one (passages, reuse, staleness, nearness),
  with `TextChunker`, `RankFusion` and `QueryEmbeddingCache` beside it.
- `src/Gatherum.Infrastructure` — implementations with real dependencies: filesystem
  storage, text extractors, media analysis (`Analysis/` — the OpenAI-compatible client,
  ffmpeg, and the background worker), bookmark capture (`Bookmarks/` — the headless
  Chromium archiver, the plain-HTTP one it falls back to, and the snapshot transform
  that makes a captured page inert and self-contained), embeddings (`Embedding/` — the packaged
  in-process model, the client for an endpoint of your own, and the sweep worker), EF
  migrations and `Data/EmbeddingSchema` (which sizes the vector
  column to the configured model at startup).
- `src/Gatherum.Web` — the static pages and layout (`Components/`), REST API
  (`Api/`, including `PlayEndpoints` — the one WebSocket, a relay for two people playing
  one cartridge), MCP tools (`Mcp/`), auth (`Auth/`), presence, `Services/PlaySessions`
  (who is in which game) + `ServerAppData`, the
  server implementation of the interactive components' data seam (`Services/`), and
  `Docs/` — the manual that ships with the app: Markdown files embedded in the
  assembly, read by `Services/DocsLibrary`, served as pages by
  `Components/Pages/DocsPage.razor` and as their own source by `Api/DocsEndpoints`.
- `src/Gatherum.Client` — every interactive component, all Interactive Auto: the
  editor (`NodeEditor` hosting slopedit's `DocumentView` for pages and docx,
  `EditorView` for code/source; a document that is read rather than edited goes to
  `DocumentHtmlView` instead — the version panel's preview is the one today), tree,
  sidebar panels (contents/similar/recent), search box, node header, the category bar at
  the foot of a page (`NodeCategories`), version panel, file view, settings keys, and
  the ROM player (`RomPlayer` over `Emulation/` — `IEmulatorCore` with a NES, a Game Boy
  and a Master System behind it, here because a console only ever runs in the reader's
  own browser, `VendoredCore` for the machines fetched at build time — a Game Boy
  Advance, a Super Nintendo, a Virtual Boy, a Mega Drive and 32X, a GameCube — plus
  `Emulation/Netplay/` where two of them keep in step) —
  plus Gatherum's Markdown dialect, which lives
  here because it is the editor's word: `GatherumMarkdown` (the extension set and the
  only read/write door), `AsideExtension`/`CalloutExtension`/`BlockTags`,
  `SharedListExtension` and the `SharedListWidget` grid it is read as,
  `DocumentChrome` (floats and decorations derived from tags), `ChromeInk`,
  `DocumentFonts` (the shipped serif, embedded for Skia and served for `@font-face`), `WikiLinks`,
  `NodeLinks` (the padlock a link the reader may not follow wears) and `NodeUrl`. `IAppData` (`AppData.cs`) is their only view of the world —
  implemented by `ServerAppData` over the services on the server circuit and by
  `HttpAppData` over `/api` in WebAssembly.
- `native/` — the second way a cartridge can play: an emulator somebody else wrote,
  compiled to WebAssembly. `core-shim/` is Rust and is most of what lives in the
  repo — a `no_std` staticlib giving libretro's function-pointer interface a flat surface
  JavaScript can call, because JavaScript cannot manufacture a wasm function pointer, and
  the same one links all three libretro cores unchanged (`bsnes-support/libco-extras.c`
  is the small exception, three functions bsnes's Emscripten backend leaves out).
  `gecko-host/` is the same flat surface built over a core that is not libretro at all:
  Gecko is a Rust crate, and the host owns its console, draws its picture through WebGPU
  and reads it back, and carries the two-file patch that lets a browser reach the memory
  card. `jgenesis-host/` is that shape again over jgenesis, the Mega Drive, minus the
  GPU, with its own one patch for the cartridge's battery memory. `build-core.sh`
  fetches each core at a pinned commit and builds it — mGBA and Beetle VB against WASI,
  bsnes against Emscripten, because a core built out of coroutines and exceptions cannot
  use the first, Gecko and jgenesis with Rust's own wasm target and wasm-bindgen; what
  it fetches and what it emits are both gitignored, the same bargain `models/` strikes.
  See `native/README.md`, which also carries the licence table.
- `tests/Gatherum.Tests` — unit tests plus `AppIntegrationTests` booting the real app.

Render modes: static SSR for pages and layout; every interactive component is an
Interactive Auto island from Gatherum.Client. Blazor's Auto mode matches the mode
already interactive on the page, so keeping the chrome free of Server-only islands is
what lets the whole screen resolve to WebAssembly — don't add an Interactive Server
component without realizing it pins every Auto island back to the circuit. UI
components, API endpoints, and MCP tools are all thin: they parse input, call a Core
service (through `IAppData` in Client components), map the result (`Api/ApiModels.cs`
DTOs are shared by REST and MCP). On the server, each `IAppData` operation runs in a
fresh DI scope via `Services/AppOperations`.

## Rules that don't bend

- One `Node` entity, one body model: a node's current content is a plain file at
  `{storage root}/{owner root}/{path}`, and superseded content is content-addressed under
  `{owner root}/.gatherum/versions`. `Kind` is derived from the media type, never stored.
- The filesystem is the system of record; the database is an index over it. Everything
  except `Users`, `ApiKeys`, and `ReadingPositions` (per-reader ephemera — see
  DECISIONS.md) is rebuildable by `Reindexer` from a cold scan, so nothing
  may live only in a table — see `FILESYSTEM.md`. The disk always wins a disagreement, and
  nothing outside `.gatherum` is written or deleted to resolve one.
- Ownership is the path: whoever owns the root directory owns what is under it, and no
  column may disagree. Access is orthogonal to location — only an owner sets it, and only
  where an owner could have written it.
- Private by default. A node with no declaration is its owner's alone, which is also what
  an unprepared directory means. `AccessMode.Public` is the internet, unauthenticated;
  `Unlisted` is reachable by link and in no listing.
- Seeing is not editing. Every write goes through `NodeService.EnsureEditable` (owner or
  `Editor`) and every structural change — rename, move, delete — through `EnsureOwner`.
  A read-path lookup never authorizes a write; `FileService` has a separate door for each.
- Reaching and enumerating are two questions. `INodeAuthorizer.CanSee` answers a direct
  link and needs `NodeReach.WithLink`; `VisibleTo` answers every listing and needs
  `Listed`. Never answer one with the other — unlisted is the case where they differ.
  A link written into a page is the first question (`NodeService.ReachableIdsAsync`), a
  `[[wiki link]]`'s title is the second (`ResolveTitlesAsync`): an id is permission, a
  title is a search.
- A page says what it says, whoever is reading. A link the reader may not follow is
  drawn locked (`NodeLinks`), never deleted and never left live — hiding it would
  misreport the page, and following it would only reach a 404. Locking is the read
  view's alone: it rewrites runs, and a document that can be saved has to write back the
  bytes it was read from.
- A shared list is two documents, and one mechanism with several words for it — the
  fence's word (`collection`, `availability`, `poll`) says what an answer *means*, the way a
  callout's kind does. It decides the wording, whether a row's own total is worth a
  column, whether a person has one answer or many (`SharedListSyntax.PicksOne`,
  enforced on the write because the file is what everybody else reads), and whether the
  grid names who answered what (`NamesAnswers` — a poll does not, withheld in the answer
  the server builds rather than in the markup, because a name the response still carries
  is not withheld). `SharedListSyntax.Kinds` parses them and
  `ListVocabulary` says what each calls things; adding a question is a row in each, and
  there is no third place. The catalog is a page with a `:::collection`
  fence; a tally is a page per person whose fence names that catalog, under its owner's
  root, and nobody writes anybody else's. **The catalog's audience is the grid's
  audience**: whoever may read the list sees every column on it, so the whole
  authorization is the one `GetWithBodyAsync` on the catalog already does — the
  aggregate re-asks nothing and spells nothing. A tally's own `AccessMode` still governs
  its *page* (its URL, the tree, search), which is why answering never publishes one. Answers
  are content, so they are a file: no `NodeAnswers` table, ever. Ordinary `- [ ]` outside a
  fence still means shared state, and reinterpreting it would break the commoner kind of
  checklist silently. See LISTS.md.
- One tree for placement, one graph for subject, and nothing else names a subject. A node
  has one place in the node tree. A category is a *page* — a node with `IsCategory` set —
  and `NodeCategory` is the taxonomy's only relation: an edge to a category is a
  membership, and an edge from a category to a category is a subcategory. So a subject can
  sit under two parents, categories are renamed/re-nested/deleted as the pages they are,
  and `CategoryName` is the only place that decides how one is spelled. No tags, and no
  second set of verbs.
- MCP and REST stay thin adapters over the same application services. No logic in
  endpoints, tools, or components.
- Storage (`IFileStorage`), extraction (`ITextExtractor`), analysis (`IMediaAnalyzer`),
  embedding (`IEmbedder`), authorization (`INodeAuthorizer`), and page capture
  (`IPageArchiver` — `BrowserPageArchiver` renders in headless Chromium where one is
  found, `HttpPageArchiver` is the plain fetch it degrades to) are the only abstraction
  seams. Don't add interfaces without a stated second implementation.
- A core is deterministic or it is broken. Same cartridge, same buttons, same frames ⇒
  byte-identical states — that is what lets two people play over a network by exchanging
  nothing but buttons. So: no wall clock (a cartridge's own real-time clock counts the
  console's cycles), no randomness, and nothing the player does *outside* the console in
  a save state. Draining audio is the trap: how often a browser asks for samples is its
  own business, so the sample queue is deliberately not serialized. `EmulatorStateTests`
  holds the line, including a muted console that must still match an unmuted one. A
  vendored core is held to the same rule from outside: mGBA asks the host for
  `clock_time_get`, and the host answers with a counter that advances a frame at a time,
  never the time — a core that reads a real clock desyncs quietly, minutes in.
- The buttons are the same on every console; the printing on them is not. Eight are
  shared, and the four above them — two shoulders, then the second pair of face buttons —
  arrived with the machines that had them. What a machine calls each is `ButtonLabels` on
  the core, and a `null` is a button it never had: the player leaves those off the pad
  rather than drawing one the hardware has no wire for. A button that lives above the
  eighth bit is why a netplay input message carries two bytes of buttons.
- The netplay server relays and understands nothing. It stamps which seat a message came
  from — a client says what it pressed, never who pressed it — and forwards it. How many
  seats a room has is the console's answer, not the server's. Don't teach it the game.
- A console runs in the reader's browser or not at all. `RomPlayer` is guarded on
  `OperatingSystem.IsBrowser()`: an Interactive Auto island renders on a circuit until
  the WebAssembly runtime lands, and sixty frames a second over a websocket is not a
  game. The cores tick the machine from inside every memory access rather than executing
  an instruction and catching up, because a mid-screen scroll write is what a status bar
  is made of — don't "optimize" that into an instruction-at-a-time loop.
- Extraction is exact, cheap, and runs inside the upload request; analysis asks a model,
  takes minutes, and runs on a background worker. Never put one on the other's path —
  an upload must return before any model is consulted. Embedding is a third tempo again:
  a background sweep for indexing, and one bounded call on the search path — which must
  time out into a full-text answer rather than make anyone wait. A search never fails
  because a model is unreachable. A bookmark capture is a fourth tempo: bounded and
  inside the request that asked, because the capture *is* the node's content — and never
  on any schedule. Nothing in Gatherum fetches the web unasked.
- An embedding is a function of its text and nothing else. The packaged model is
  quantized, so batching several passages into one tensor would make each one's vector
  depend on its neighbours — and a search box, always alone, systematically unlike the
  passages it is compared against. `LocalEmbedder` embeds one at a time on purpose; don't
  "optimize" that away.
- The packaged model is fetched by the build into a gitignored `models/`, verified by
  hash, never committed and never downloaded at run time.
- A node is stale for embedding when `TextFingerprint` (computed by the database) differs
  from `EmbeddedFingerprint`. That comparison is the only thing that queues work. Never
  add an enqueue call beside it: a second source of truth can only ever be the one that
  gets forgotten.
- A user's root directory is named after their OIDC `preferred_username`, assigned once
  and never renamed — moving it would mean moving every file they own.
- Data Protection keys live in the database (`GatherumDbContext : IDataProtectionKeyContext`),
  not on disk. They must outlive the container — the default location is the runtime
  user's home, which is inside the image and unwritable when it runs as an unmapped uid —
  and keeping them out of the storage root keeps them out of the backup people are told to
  take of it.
- The anonymous rate limiter partitions on the client address, which behind a proxy means
  `X-Forwarded-For` — enabled by `ASPNETCORE_FORWARDEDHEADERS_ENABLED` in the Dockerfile,
  and trusted from any peer, so the loopback bind is what stops header spoofing. Don't
  publish the port wider without revisiting both.
- Auth is OIDC-only (plus API keys). No local accounts, ever. Anonymous is not identity:
  it reaches public nodes read-only, through `VisibleTo(nodes, null)` and nothing else. An
  API endpoint is authenticated unless it says `.AllowAnonymous()`, and no write ever does.
- `INodeAuthorizer.VisibleTo` is the only door for visibility. Never spell the rule again
  in a query — widening the seam is what makes a change correct everywhere at once.
- No hand-written JavaScript beyond `wwwroot/js/gatherum.js` and the pager
  `EpubChapterHtml` injects into the chapters it renders (a sandboxed frame has no Blazor
  to lean on; the CSP admits that script by hash and nothing else). Nothing goes in either
  that Blazor can do natively, and no JavaScript library — vendored or fetched — goes
  anywhere near the wiki itself. **The rule is about the crucial features**: the tree,
  the editor, search, sharing, auth. Those are what a person keeps their life's notes in,
  and they are C# end to end so that the whole of what runs in a browser is code this
  project can account for. Playing a cartridge is not one of those, and it is scoped so it
  cannot become one: a console appears on a ROM's page and nowhere else, and a build with
  no core at all serves a download link while everything else works as before. So the ROM
  player may take whatever JavaScript a vendored core's toolchain emits — the owner's
  call, and what puts every libretro core within reach rather than only the few that
  happen to compile against WASI. It buys a console; it does not buy a jQuery.
- Every Markdown ⇄ document conversion goes through `GatherumMarkdown` — never
  `MarkdownSerializer` directly. A page read without the extension set writes the wiki's
  own syntax back out as prose.
- The manual in `src/Gatherum.Web/Docs` is part of the feature, not a follow-up. It is
  served unauthenticated on purpose — it describes the software, never the instance — so
  nothing about a particular deployment goes in it.
- No comment where a better name would do. Comments explain invariants and whys, not whats.
- Warnings are errors. Never leave the tree red; build and test before every commit.

## Conventions

Commit and PR titles are short, plain descriptions of the change in active voice
("Add bookmark capture history"), at the owner's request — no wordplay, no colons-and-
clauses, no prose styling. Save the voice for the body if the change needs one.

C# is modern and terse: primary constructors, records for values, expression-bodied
members where they read well. File placement follows the map above; one public type per
file, named for the type. Static pages and layout live under
`Components/{Layout,Pages}` in Web; interactive components live flat in Client and
touch the server only through `IAppData`.

Icons are [Lucide](https://lucide.dev) (ISC), inline `<svg>` with
`stroke="currentColor"` where there is markup to put one in — which is nearly
everywhere. An icon CSS has to draw instead, because the thing it decorates is text a
renderer emits, goes in `wwwroot/icons/` as a file from the pack and is masked, not
`background-image`d, so `currentColor` keeps working; see the README there. Don't add a
second icon set, and don't hand-draw a path when the pack has one.

**Make a new format editable/previewable**:
1. Teach `MediaTypes` (`src/Gatherum.Core/Domain/MediaTypes.cs`) the extension.
2. Editable text just works once `MediaTypes.IsText` says yes; for a richer preview,
   extend the media-type dispatch in `src/Gatherum.Client/FileView.razor`.
3. For syntax highlighting, add the lexer upstream in slopedit's `LexerRegistry`.
4. A binary rich-document format follows the docx pattern end to end: a converter
   case beside `DocxConverter`'s in `NodeEditor`, the format in NodePage's editable
   check and `FileService.SaveBinaryAsync`'s guard, and an extractor for search.

**Add a Markdown construct** (something the editor has no word for):
1. Write a `MarkdownInlineExtension` or `MarkdownBlockExtension` in `Gatherum.Client`
   and register it in `GatherumMarkdown.Extensions` — both sides, or the round trip
   silently destroys the syntax.
2. A block extension only *tags* the blocks it owns (`BlockTags`; the tag is the
   source's own argument line, which is how the writer finds the run again). Geometry
   and paint belong to `DocumentChrome.Apply`, which re-derives them after every edit —
   never declare a `FloatedRun` or `BlockDecoration` at parse time, because block
   indices move.
3. Colors come from `ChromeInk`, never hard-coded: a document outlives a theme switch.
4. If the construct links nodes, teach the server to see it too — `Markdown/` in Core,
   then `FileService.RefreshLinksAsync` — or it won't backlink.
5. If a reader is meant to *use* the construct rather than read it, claim its tag as a
   slopedit widget in `NodeReader` and render a component — the reading view's business
   alone. The canvas keeps painting the blocks, which is what stops a control from ever
   registering as an edit of an open document; `DocumentChrome` still declares the card
   it wears there. `SharedListWidget` is the one today.
6. Round-trip test in `GatherumMarkdownTests`: parse, write, parse, write, compare.
7. Write it into `src/Gatherum.Web/Docs/markdown.md`. The manual ships with the app and
   is what a model gets pointed at, so a construct it never mentions may as well not
   exist — `DocsTests` fails on a callout kind or an aside name the page has not heard
   of.

**Add a console** (a machine whose cartridges should play):
1. Implement `IEmulatorCore` under `src/Gatherum.Client/Emulation/` (cf. `Nes/NesConsole.cs`).
   One frame per `RunFrame`, an ARGB `Frame` the player pins once, and audio drained
   through `ReadAudio`; the player owns the clock.
2. Teach `Emulation/Emulator.Load` to recognise it — by the bytes first, the extension
   second — `MediaTypes` its extension, and `FileView.IsRom` both. That last one is a
   second copy on purpose: Gatherum.Client does not reference Core, so the extension
   list is spelled twice or the page renders a download link instead of a console.
3. Teach `Core/Roms/RomHeader` to read its header, so a cartridge is findable by what it
   says it is, and add the row to the extraction table in `Docs/pages-and-files.md`. A
   header is not always at the start of the file — Sega's is at the end of the first
   bank — and `RomTextExtractor.HeaderBytes` bounds how far the search goes.
4. Name its plastic in `ButtonLabels`. The eight bits are the same on every machine, but
   what they are printed as is not, and a `null` is a button the console never had.
5. Implement `SaveState`/`LoadState` over `StateWriter`/`StateReader` — positional and
   untagged, so both sides must walk the same fields in the same order; the four-byte tag
   at the head is what refuses somebody else's state. Leave the audio queue out.
6. Add tests beside `Nes6502Tests`/`GameBoyTests`/`Z80Tests`: hand-assembled programs in
   `RomFixtures`, never a real game — a checked-in ROM is somebody's copyrighted work.
   A console that reports more than one player also needs the determinism tests in
   `EmulatorStateTests` pointed at it, or netplay on it is a coin toss.

**Add a vendored console** (a machine too big to write from scratch):
1. Read `native/README.md` first — it carries the licence table, and a core whose licence
   does not fit AGPL-3.0 cannot be added whatever else is true of it.
2. Pin it in `native/build-core.sh` beside the five there. Plain C with no threads, no
   coroutines and no exceptions goes to WASI and comes out glue-free — C++ too, with
   exceptions switched off, which is what Beetle VB is; anything else goes to
   Emscripten. Link it against `core-shim` unchanged: the shim is not specific to any
   core, and on the WASI side anything the built module imports beyond WASI is a source
   file you meant to compile and did not.
3. Never let it learn the time. mGBA asks WASI for `clock_time_get` and the host answers
   with a frame counter; bsnes calls `clock()` and `libco-extras.c` answers zero. Find
   which it does before trusting anything it says about two machines agreeing.
4. Give it a row in `VendoredCore.Machines` — module URL, pad labels, player count, and
   any core option that changes what the machine *does* rather than how it looks. Teach
   `Emulator.Identify` its bytes; `MediaTypes` and `FileView.IsRom` as for any console.
   Never claim `.md` or `.bin` for a cartridge: one is a page and the other is
   everything.
5. Report `PlayerCount` 1 until you have **measured** two of it in step: same cartridge,
   scripted two-player input, `retro_serialize` compared every sixty frames over several
   hundred, plus a control run proving different buttons still diverge. Netplay is two
   machines that must agree frame for frame; a vendored core's determinism is somebody
   else's claim, and the answer to a claim is a measurement, not an assumption either way.
6. If it swaps coroutines under Asyncify, remember that a value returned across a swap is
   lost. Park the answer in a static and fetch it with a second call, the way the shim's
   state calls already do.

**Add a text extractor**:
1. Implement `ITextExtractor` in `src/Gatherum.Infrastructure/Extraction/` (cf.
   `PdfTextExtractor.cs`).
2. Register it in `GatherumServiceCollectionExtensions.AddGatherum`.
3. Add a claim/extract test beside `FileStorageTests.cs`.

**Add a media analyzer** (a new way to read, hear, or describe a medium):
1. Implement `IMediaAnalyzer` in `src/Gatherum.Infrastructure/Analysis/` (cf.
   `OpenAiMediaAnalyzer.cs`). Analysis is slow and fallible by nature: throw with a
   message a person can act on, and `MediaAnalysisWorker` records it on the version.
2. Register it in `GatherumServiceCollectionExtensions.AddAnalysis`, which only wires
   analysis up at all when an endpoint is configured — with none, nothing claims an
   image and every upload behaves as it did before analysis existed.
3. Add tests beside `MediaAnalysisTests.cs`, using `FakeMediaAnalyzer` for anything
   about queueing, reuse, or search text.

**Add an embedder** (a different way to turn text into a vector):
1. Implement `IEmbedder` in `src/Gatherum.Infrastructure/Embedding/` (cf. `LocalEmbedder.cs`
   for the packaged model, `OpenAiEmbedder.cs` for one you run). `Model` names it: vectors
   are stored beside that name and nothing compares two models' vectors.
2. Register it in `GatherumServiceCollectionExtensions.AddEmbedding`, which picks exactly
   one: an endpoint if configured, else the packaged model, else nothing at all. If you
   change what that method can pick, change `EmbeddingEnabled` beside it too — startup
   asks it whether to build the vector schema, and it must not load a model to answer.
3. If its vectors are a different width, `Gatherum__Embedding__Dimensions` is the only
   thing to change: startup resizes the column, drops the old vectors, and the worker
   earns them back. Never edit the migration for this.
4. Add tests beside `SemanticSearchTests.cs`, using `FakeEmbedder` — a real model's
   answers are approximate, and an assertion about ranking made against one is a coin
   toss dressed as a test.

**Add a storage backend**:
1. Implement `IFileStorage` (cf. `Storage/FileSystemStorage.cs`); save returns SHA-256.
2. Swap the registration in `AddGatherum` (make it configurable then).
3. Run `FileStorageTests` patterns against it.

**Add an MCP tool**:
1. Add the operation to a Core service if it doesn't exist.
2. Expose the REST endpoint in `Api/ApiEndpoints.cs`.
3. Add a method in `Mcp/GatherumMcpTools.cs` (attribute + `[Description]`s, call the
   service, map with the shared DTOs) and document it in `MCP.md`.

## Testing expectations

- Pure logic (markdown links/rendering, hashing, snippets, media types) gets plain
  unit tests.
- Service behavior (tree ops, privacy, categories, versions, search) gets tests against real
  Postgres via `PostgresFixture` + `ServiceHarness` — never mock the DbContext. The
  fixture's Postgres must carry pgvector.
- Cross-surface flows belong in `AppIntegrationTests` (WebApplicationFactory + API key).
- Single test: `dotnet test --filter "DisplayName~<fragment>"`.

## Decision log

Deviations and judgment calls go in [DECISIONS.md](DECISIONS.md) when they happen —
commit messages alone don't count.

[LISTS.md](LISTS.md) is the design behind shared lists — why a tally is a
file and not a table, why an item's page is optional, and why signing out means reading
rather than answering. Read it before changing anything under `:::collection`.

[FILESYSTEM.md](FILESYSTEM.md) is the architecture: the directory tree is the system of
record and the database is a derived index. Stages 1–4 are built, rate limiting
included; frontmatter, the union-tree UI and the filesystem watcher are not. Read it before changing storage, node
identity, or the access model.

## What not to do

- No new projects without a real boundary that demands one.
- No third-party state/MVVM libraries; no JS libraries, vendored or fetched — the ROM
  player's vendored cores are the one exception, and they are confined to a ROM's page.
- No speculative abstractions, no repository layer over EF.
- No features beyond `PLAN.md` scope without asking.
