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

Uploads go anywhere in the tree, and the file keeps its name — the node's title starts
as the filename and can be renamed independently afterwards.

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
