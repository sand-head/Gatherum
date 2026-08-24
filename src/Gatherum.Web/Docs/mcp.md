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

`/mcp` inside Claude Code should then list the `gatherum` server with eleven tools.

## The tools

| Tool | Arguments | Returns |
| --- | --- | --- |
| `search` | `query`, `kind?` (`page`/`file`/`category`), `limit?`, `mode?` (`hybrid`/`text`/`semantic`) | Matches with kind and snippet |
| `get_node` | `id` | Metadata plus the Markdown body (pages) or extracted text and file metadata |
| `list_children` | `id?` (omit for roots) | Children in tree order |
| `create_page` | `title`, `markdown`, `parentId?` | The created node |
| `update_page` | `id`, `markdown`, `title?` | The updated node; a new version is recorded |
| `move_node` | `id`, `newParentId?`, `position?` | Confirmation |
| `add_category` | `id`, `name` (e.g. `Podman`) | The name it landed on |
| `remove_category` | `id`, `name` | Confirmation |
| `list_categories` | `matching?` | Every category, with member counts and its parents' ids |
| `browse_category` | `name`, `deep?` | The category, its parents, subcategories and nodes |
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
- **Categories are the subject index, and each one is a page.** File a new page under
  something; `list_categories` says what already exists, and an existing name is the one to
  match — a new name writes a new category page. Nesting is the same call pointed at a
  category: `add_category` on Podman's own node id, with `Homelab`, makes it a
  subcategory. `get_node` on a category reads what it says belongs in it, and
  `update_page` writes that.
- **Links earn backlinks.** A mention or wiki link is recorded in both directions the
  moment the page is saved; a bare title in prose is not.
- **Private is private.** A key sees exactly what its owner sees. Another user's private
  subtree does not appear in `search`, `get_node` or `list_children` — it behaves as if
  it does not exist.

[Working with agents](/docs/agents) is the longer version of this list.

## What is not there

There are no tools for uploading files, deleting nodes, changing sharing, or managing
keys. Those live in the [REST API](/docs/api), which accepts the same bearer token —
including `POST /api/nodes/resolve-titles`, the question a wiki link asks.
