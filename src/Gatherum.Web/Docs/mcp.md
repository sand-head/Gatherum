# MCP server

Gatherum exposes a [Model Context Protocol](https://modelcontextprotocol.io) server at
`/mcp` over Streamable HTTP, so an agent reads and writes your knowledge base under the
same rules as the UI: same services, same visibility, same Markdown round trip.

## Authentication

The endpoint requires an API key — a browser session is not accepted here. Create one in
**Settings → API keys**; the token (`gk_…`) is shown once.

```
Authorization: Bearer gk_…
```

## Pointing Claude Code at it

```sh
claude mcp add --transport http gatherum https://<your-host>/mcp \
  --header "Authorization: Bearer gk_…"
```

Locally (`dotnet run`, default port 5140):

```sh
claude mcp add --transport http gatherum http://localhost:5140/mcp \
  --header "Authorization: Bearer gk_…"
```

`/mcp` inside Claude Code should then list the `gatherum` server with thirteen tools.

## The tools

| Tool | Arguments | Returns |
| --- | --- | --- |
| `search` | `query`, `kind?` (`page`/`file`), `limit?`, `mode?` (`hybrid`/`text`/`semantic`) | Matches with kind and snippet |
| `get_node` | `id` | Metadata plus the Markdown body (pages) or extracted text and file metadata |
| `list_children` | `id?` (omit for roots) | Children in tree order |
| `create_page` | `title`, `markdown`, `parentId?` | The created node |
| `update_page` | `id`, `markdown`, `title?` | The updated node; a new version is recorded |
| `bookmark_page` | `url`, `parentId?` | The captured page as a new file node |
| `capture_bookmark` | `id` | The bookmark, with a fresh capture as its newest version |
| `move_node` | `id`, `newParentId?`, `position?` | Confirmation |
| `add_category` | `id`, `path` (e.g. `Homelab/Podman`) | The path it landed on |
| `remove_category` | `id`, `path` | Confirmation |
| `list_categories` | `matching?` | The category tree in path order, with member counts |
| `browse_category` | `path`, `deep?` | The category, its ancestry, subcategories and nodes |
| `get_backlinks` | `id` | Nodes linking to the given node |

Media that a model has analyzed comes back from `get_node` with `transcript` and
`summary` beside its text, and `analysis` saying whether that is `None`, `Pending`,
`Complete` or `Failed`.

## What an agent should know

- **Pages are Markdown files, and they round-trip verbatim.** What `create_page` is given
  is what the file holds and what `get_node` gives back. Write the
  [dialect](/docs/markdown) — wiki links, mentions, asides, callouts — and the editor
  renders it; write plain Markdown and nothing is lost either.
- **Search before creating.** Titles are how `[[wiki links]]` resolve, so a near-duplicate
  page is worse here than a missing one.
- **Categories are the subject index.** File a new page under something; `list_categories`
  says what already exists, and the capitalization of an existing path is the one to
  match.
- **Links earn backlinks.** A mention or wiki link is recorded in both directions the
  moment the page is saved; a bare title in prose is not.
- **A bookmark is a capture, not a link.** `bookmark_page` renders the URL in a
  headless browser once, now — scripts run and settle first — and keeps a
  self-contained snapshot as a file node, searchable by what the page said and by its
  address. Nothing is fetched again unless `capture_bookmark` asks, and each capture is
  a version. See [Bookmarks](/docs/pages-and-files#bookmarks).
- **A bookmark reads as Markdown.** `get_node` returns the captured page rendered as
  Markdown in `extractedText` — headings, lists, links, tables and code, no markup —
  the same convention docx files follow. Read the bookmark, not the HTML.
- **Private is private.** A key sees exactly what its owner sees. Another user's private
  subtree does not appear in `search`, `get_node` or `list_children` — it behaves as if
  it does not exist.

[Working with agents](/docs/agents) is the longer version of this list.

## What is not there

There are no tools for uploading files, deleting nodes, changing sharing, or managing
keys. Those live in the [REST API](/docs/api), which accepts the same bearer token —
including `POST /api/nodes/resolve-titles`, the question a wiki link asks.
