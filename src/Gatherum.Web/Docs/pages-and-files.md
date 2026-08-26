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
- **Everything else** gets a preview: images, PDF, video and audio play inline; anything
  else offers a download.

Editing autosaves. Presence shows who else has the document open, and if someone saved
while you were typing the editor says so — your next save makes a new version and theirs
stays in history either way.

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
| Images | Embedded metadata |

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
