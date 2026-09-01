# Pages and files

Everything in Gatherum is a **node**. A node has a title, one parent, a position among
its siblings, categories, links, an access mode, a version history, and a file. A page
is a node whose file is Markdown; a PDF is a node whose file is a PDF. There is no
second kind of thing.

`Kind` is derived, never stored: a node is a `Page` when its media type is
`text/markdown` and a `File` otherwise. Rename `notes.txt` to `notes.md` and it becomes
a page.

## The tree

Every node has exactly one place in the tree, and the tree is the directory layout: a
node's parent is the directory it sits in, under a root directory named for its owner.
Nothing about placement is an opinion stored in a table — move a node in the UI and the
file moves on disk.

- **All pages** (`/pages`) is the whole tree, and where you create, move, rename and
  upload.
- The sidebar is reading context rather than a file manager: the current article's
  contents, similar articles, and where you have been.
- A node can hold children whatever its own content is. A page with pages under it is a
  perfectly ordinary section.

## Making things

| To make | Do |
| --- | --- |
| A page | **New page** in the tree, or `POST /api/pages`, or the `create_page` MCP tool |
| A page from a red link | Click a `[[wiki link]]` that resolves to nothing and accept the offer |
| A file | Drag it onto the tree, use the picker, or `POST /api/files` |
| A new version of a file | Re-upload onto the existing node |
| A bookmark | **Bookmark a page** in the New menu, or `POST /api/bookmarks`, or the `bookmark_page` MCP tool |

Uploads go anywhere in the tree, and the file keeps its name — the node's title starts
as the filename and can be renamed independently afterwards.

## Bookmarks

A bookmark is a web page kept the way an archive keeps one, not the way a browser does:
paste a URL and Gatherum captures the page **once, now**, and saves it as a file node.
A headless browser loads the page and lets its scripts run and settle — what gets kept
is the document as it stands afterwards, the page you actually saw, not the stub the
server sent to build it from. The capture is one self-contained HTML file — the
stylesheets, images and fonts the page rendered with folded in, every remaining link
made absolute — whose first line records where and when it was taken. It is searchable
by what the page said and by its address, and it still reads after the original
changes, moves, or disappears.

- The node's title is the page's own; the file lands wherever in the tree you asked.
- The page renders inline on the node — sandboxed, so nothing in it runs — under a bar
  naming where it was saved from, one click from the live page.
- **Capture again** (the button on that bar, the `capture_bookmark` MCP tool, or
  `POST /api/bookmarks/{id}/capture`) fetches the URL again and keeps the result as a
  new version — and the bar's capture picker pages back through the older ones, each
  rendered as the page stood then, like an archive's calendar of crawls. The History
  panel below restores or downloads any of them.
- A URL that serves a document rather than a page — a PDF, an image — is kept as that
  document, source address and all.
- Ads and trackers do not ride along: known ad, analytics and consent hosts are refused
  before the page's scripts run — so ad units, tracking pixels and cookie overlays are
  never in the document being kept — and anything still pointing at one is stripped
  from the file, which would otherwise phone home on every reading. Off switch in
  [Configuration](/docs/configuration) (`Gatherum__Bookmarks__BlockAds`).
- To search — and to an agent reading it over MCP — a bookmark is its **Markdown
  rendering**: headings, lists, links and tables as structured prose, the same
  convention docx files follow, so a model processes the page without wading through
  its markup.

Nothing is fetched on a schedule and nothing is re-fetched behind your back: a capture
happens when you ask, and that is the whole of it. The page's scripts run once, at
capture time, and do not ride along in the file — their output is what the snapshot
*is*, and stored markup that could execute is a page that could act as its reader. On
an instance with no browser installed (a bare `dotnet run`, say — see
[Configuration](/docs/configuration)), the capture degrades to what the server serves
a plain fetch.

## Editing

Pages, uploaded `.docx` documents, and any text file open in the editor.

- **Pages and documents** open in a rich document editor — proportional text, tables,
  images, Markdown auto-formatting as you type — with a **Source** toggle that shows
  the raw Markdown. Both surfaces read and write the same file; see
  [Markdown in Gatherum](/docs/markdown) for the dialect.
- **Code and text files** open in a code editor with syntax highlighting.
- **Everything else** gets a preview: images, PDF, video and audio play inline, and an
  `.epub` opens in a paginated reader — the chapter flows into book-like pages turned by
  the arrow keys, the page edges, or a scroll, with the book's own chapter list in a bar
  above and its internal links still going where they point. The reader keeps your
  place: reopen the book on any device you're signed in on and it opens where you left
  off, and each reader keeps their own place. A visitor on a public book is never
  remembered by the server — their place is kept by their own browser instead, and
  goes no further. A **game cartridge** — a `.nes`, `.gb`, `.gbc`, `.sms`, `.gg` or
  `.gba` file — plays: see
  [Playing a cartridge](#playing-a-cartridge) below. Anything else offers a download.

Editing autosaves. Presence shows who else has the document open, and if someone saved
while you were typing the editor says so — your next save makes a new version and theirs
stays in history either way.

## Playing a cartridge

Upload a `.nes`, `.gb`, `.gbc`, `.sms`, `.gg` or `.gba` file and its page has a console
on it. Press **Play** and the game runs.

The console is not a plug-in and not a download. For most of these machines the
processor, the picture chip and the sound chip are all part of Gatherum itself; the Game
Boy Advance is big enough that its emulator is a well-known one from elsewhere, built
into Gatherum when Gatherum was built. Either way it runs in your browser, the cartridge
is fetched once, and nothing about the game leaves your machine afterwards.

- **The controls** are the arrow keys, <kbd>Z</kbd> and <kbd>X</kbd> for the two face
  buttons, <kbd>Enter</kbd> and <kbd>Shift</kbd> for the other two. What those last two
  are called depends on the machine — Start and Select on a Nintendo, Pause and Reset on
  a Master System — and the player prints the names the console's own plastic used. A
  game controller plugged in or paired works as well, with nothing to set up: while the
  game runs the player reads it directly, and its buttons land by where they sit on the
  pad — the bottom and right face buttons are the console's B and A whatever your pad
  prints on them. On a touchscreen a pad appears under the screen instead. Click the
  picture to give it the keyboard again after clicking anything else — the controller
  never needs that click.
- **Sound** starts with the game — a browser will not make noise until someone has asked
  it to — and there is a button to turn it off.
- **Saving.** A cartridge with a battery in it saves the way it always did, and the save
  is kept by your own browser rather than by Gatherum: the ROM is a file everyone who can
  see the page shares, and a save is one person's afternoon. **Download save** takes it
  out as a `.sav` file, and **Load save** puts one back — that is how a save moves
  between browsers, or in from somewhere else.
- **The first visit** to any page after signing in renders on the server while the
  in-browser runtime downloads, and the player says so rather than pretending: reload
  once and it plays.

### Playing together

A Nintendo Entertainment System has two controller ports, and so does a Master System —
and so does the player. Press **Play together** and anybody else who can see the page and
opens it takes the second one — they appear in a strip under the screen, and you are
playing the same game.

- **Nothing but buttons crosses the network.** Both browsers run the same console from
  the same cartridge, and identical machines given identical buttons stay identical, so
  there is no video to send. Gatherum's server passes the buttons along and never learns
  what any of them do.
- **Joining a game already going works.** Whoever started hands their whole machine over,
  so the second player arrives wherever the first one has got to rather than at the title
  screen.
- **A stall is honest.** If somebody's connection falls behind, the game waits and says
  whose — rather than guessing what they pressed and being wrong about it a second later.
- **Same page, same cartridge.** A room is the ROM's page: whoever may open the page may
  join, and nobody else. The server checks that everybody is holding the same file, byte
  for byte, before it seats them.
- Playing together needs an account, like every other thing in Gatherum that is not
  simply reading.

> [!NOTE]
> The Game Boy, the Game Gear and the Game Boy Advance are one-player machines here. Two
> people on either meant two consoles and a cable between them, which is a second machine
> to emulate rather than a second port to read — it is not built. Playing together also
> asks that both machines agree frame for frame; for a console Gatherum wrote that is a
> promise it keeps, and for one it did not write it is a claim that has to be measured
> before anybody relies on it. The Super Nintendo's has been, which is why it plays with
> two, and the Game Boy Advance's has not.

### The consoles

| Console | Files | Players |
| --- | --- | --- |
| Nintendo Entertainment System | `.nes` | Two |
| Game Boy and Game Boy Color | `.gb`, `.gbc` | One |
| Master System | `.sms` | Two |
| Game Gear | `.gg` | One |
| Game Boy Advance | `.gba` | One |
| Super Nintendo | `.sfc`, `.smc` | Two |

Gatherum knows the Nintendo Entertainment System's common cartridge boards (NROM, MMC1,
UxROM, CNROM, MMC3, AxROM and a few others), the Game Boy's (none, MBC1, MBC2, MBC3 with
its clock, and MBC5) and the Master System's (Sega's own paging hardware, and the cheaper
board Codemasters built for their own games). A cartridge on a board it has not met says
so by name instead of failing quietly.

The Game Boy Advance and the Super Nintendo are the two machines here whose emulators
Gatherum did not write, so which games run well on them is those emulators' business
rather than Gatherum's. They are also the two that are fetched and built rather than
shipped, so a Gatherum built without them offers a download where the others offer a
console, and says so.

A Game Boy Advance cartridge's header says nothing about whether it saves, so the file's
details do not either — the console works it out when the game starts. A Super Nintendo
cartridge hides its header at the end of a bank rather than the start of the file, and
which bank depends on how the board was wired; Gatherum looks in both places and trusts
the one whose checksum adds up. A `.smc` file with the extra 512 bytes an old copier
wrote in front of it reads the same as a `.sfc` without them. The Super Nintendo's pad
has four face buttons rather than two, and they are drawn in the diamond the console
printed them in: <kbd>Z</kbd> and <kbd>X</kbd> are B and A as everywhere else, and
<kbd>A</kbd> and <kbd>S</kbd>, above them, are Y and X.

A Master System cartridge carries no title, only the catalogue number Sega sold it under,
so that is what search has to go on for one. A Game Gear plays in colour on a screen a
little smaller than the picture the hardware draws, and shows the middle of it, which is
what the console did.

> [!NOTE]
> Gatherum plays cartridges; it does not supply them. What you upload is your business,
> and the same rules apply to a ROM as to anything else in the tree — private by default,
> and yours to share or not.

## Reading

A page's own URL is the read view: real HTML, so find-in-page, native selection,
printing and screen readers all work, and every link is a real link. Top-level and
second-level headings wear a hairline rule, the way an encyclopedia's do. On a narrow
screen — a phone, or a squeezed window — each `##` section folds behind its heading:
tap the heading band to close and open it, the way Wikipedia's mobile skin folds an
article. Jumping to a heading from the Contents panel unfolds whatever is in the way.

## Versions

Every save is a version. Saves by the same author within five minutes collapse into the
latest one, so autosave does not turn a paragraph into forty snapshots; a pause, or a
different author, starts a new version.

For any node you can list its versions, download the old bytes, read an old version
rendered as HTML — searchable, selectable and printable, rather than a canvas — and
restore one. A restore is a new version carrying the old content, so nothing is lost by
going back.

> [!WARNING]
> A restore brings back old bytes. It does not bring back a deleted node — deleting is
> the one thing history does not undo.

## What Gatherum reads out of a file

Two different tempos, and they never share a path.

**Extraction** is exact, cheap, and happens inside the upload request:

| File | Text it contributes to search |
| --- | --- |
| Text, Markdown, code | The content, verbatim |
| PDF | The text layer, via PdfPig |
| `.docx` | Its Markdown rendering — the same text the editor shows |
| `.epub` | Its chapters in reading order, each as its Markdown rendering |
| Images | Embedded metadata |
| `.nes`, `.gb`, `.gbc` | What the cartridge header says: the console, the title printed in it, the board, and whether it saves |
| `.sms`, `.gg` | What Sega's header says: the console, the catalogue number the game was sold under, and the region |
| `.gba` | The title and game code printed in the header, and the region its last letter names |
| `.sfc`, `.smc` | The title printed in the header, the board and any second processor on it, the save memory, and the region |

**Analysis** asks a model, takes minutes, and runs on a background worker after the
upload has already returned. It is off unless the instance's owner has configured
`Gatherum__Analysis__Endpoint` (see [Configuration](/docs/configuration)), and nothing
is ever sent anywhere without one. With it on, uploads that carry no text of their own
get some: still images are read (the writing on a photographed whiteboard), audio and
video are transcribed, and everything gets a short summary. The work survives restarts
and is reused when the same bytes turn up again.

Both feed [search](/docs/search), and both come back over the API: `transcript` and
`summary` beside the extracted text, with `analysis` saying whether that is `None`,
`Pending`, `Complete` or `Failed`.

## Descriptions

A file node can carry a description — a sentence about what the thing is, which is
searchable and shows in the file view. Descriptions are scanned for `node://` mentions,
so a description can link other nodes and earn them backlinks.

## Deleting

Deleting a node deletes its subtree. The bytes of superseded versions live in a
content-addressed store and are not what a delete reclaims; the node, its children, and
their place in the tree are gone. There is no trash.
