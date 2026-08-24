# Gatherum MCP server

Gatherum exposes a Model Context Protocol server at `/mcp` (Streamable HTTP), so agents
like Claude Code can read and write your knowledge base with the same rules as the UI:
same services, same private-subtree visibility, same Markdown round-trip.

## Authentication

The endpoint requires an API key. Create one in **Settings → API keys** inside Gatherum;
the token (`gk_…`) is shown once. Send it as a bearer token:

```
Authorization: Bearer gk_…
```

## Claude Code setup

```sh
claude mcp add --transport http gatherum https://<your-host>/mcp \
  --header "Authorization: Bearer gk_…"
```

Verify with `/mcp` inside Claude Code — the `gatherum` server should list eleven tools.

For local development (`dotnet run`, default port 5140):

```sh
claude mcp add --transport http gatherum http://localhost:5140/mcp \
  --header "Authorization: Bearer gk_…"
```

## Tools

| Tool | Arguments | Returns |
| --- | --- | --- |
| `search` | `query`, `kind?` (`page`/`file`), `limit?`, `mode?` (`hybrid`/`text`/`semantic`) | Matches with kind and snippet |
| `get_node` | `id` | Metadata + Markdown body (pages) or extracted text + file metadata (files). Media analyzed by a model also carries `transcript` (words read off an image, speech heard in audio or video) and `summary`, with `analysis` saying whether that is `None`, `Pending`, `Complete`, or `Failed` |
| `list_children` | `id?` (omit for roots) | Children in tree order |
| `create_page` | `title`, `markdown`, `parentId?` | The created node (a Markdown file) |
| `update_page` | `id`, `markdown`, `title?` | The updated node (a new version is recorded) |
| `move_node` | `id`, `newParentId?`, `position?` | Confirmation |
| `add_category` | `id`, `path` (e.g. `Homelab/Podman`) | The path it landed on |
| `remove_category` | `id`, `path` | Confirmation |
| `list_categories` | `matching?` | The category tree in path order, with member counts |
| `browse_category` | `path`, `deep?` | The category, its ancestry, its subcategories and its nodes |
| `get_backlinks` | `id` | Nodes linking to the given node |

### Search

`search` runs two searches and fuses their rankings: a Postgres full-text match over
titles, category names and text, and a meaning-based match over the same text cut into
passages. The second half needs no setup — an embedding model ships with Gatherum and runs
in its process — so a query finds pages that never use its words, and the snippet of such
a hit is the passage that matched rather than the top of the document.

Only the full-text half honours websearch syntax (quoted phrases, `OR`, `-exclusions`), so
pass `mode=text` when the exact spelling is the point — an identifier, a filename, a
phrase you are quoting. `mode=semantic` asks for meaning alone. If embeddings are turned
off, or the owner has pointed Gatherum at an endpoint that is unreachable, every mode
still answers from full-text search: a search never fails because a model is down.

### Categories

Categories are what a node is *about*, and they nest: filing a page under
`Homelab/Podman` creates `Homelab` too, and the page counts as a member of both —
`browse_category` with `deep: true` lists everything underneath. A path is spelled
freely (`Homelab / podman` is the same category as `homelab/podman`); the capitalization
of a category is set by whoever creates it. Removing a node from a category leaves the
categories above it alone, and nothing is filed automatically: a node with no category
is fine, and search still finds it.

## Markdown conventions

The full reference is served by your own instance at `/docs/markdown` — and at
`/docs/markdown.md` as its own source, which is the link to hand an agent. What follows
is the summary.

Pages *are* Markdown files, so bodies round-trip verbatim. The Gatherum-specific forms:

- **Mentions**: `[@Some Node](node://<node-id>)` — renders as an @-mention in the editor
  and creates a link (and therefore a backlink on the target). The strongest form: it
  survives a rename.
- **Wiki links**: `[[Some Node]]` or `[[Some Node|the label]]` — names a page by title
  instead of by id. It resolves case-insensitively to a node the writing user can see,
  and makes the same link row (and backlink) a mention does. A title nothing answers to
  is not an error: it renders as a red link that offers to create the page.
- **Embedded files**: `![alt](/api/files/<node-id>/content)` — embeds a file node's
  content and links to it.
- **Asides**: a directive fence sets a run of blocks beside the prose, which flows past
  it. `:::infobox` is an encyclopedia's card (a heading and borderless label/value rows);
  `:::figure` is a captioned picture. Both take an optional side and width —
  `:::figure left 360` — and close with `:::`. Inside the fence the body is ordinary
  Markdown, so a reader that has never heard of any of this still renders everything
  between the two `:::` lines.
- **Callouts**: GitHub's alert spelling — a quote whose first line is `> [!NOTE]`,
  optionally followed by a title (`> [!WARNING] Restores are not backups`). The five
  kinds are note, tip, important, warning and caution; anything else stays a plain quote.

Everything else is GitHub-flavored Markdown: headings, lists, `- [ ]` task lists, pipe
tables, fenced code blocks with language, and inline bold/italic/strike/code/links.

A page written with any of this reads back byte-identical through `get_node`: the
editor and the API speak the same dialect.

## Notes

- Private subtrees belonging to other users are invisible to your key — searches,
  `get_node`, and `list_children` behave as if they don't exist.
- Every copy of Gatherum serves its whole manual at `/docs`, without a key —
  `/docs/all.md` is all of it in one fetch, `/docs/llms.txt` is the index.
- The REST API under `/api` accepts the same bearer token and covers the same
  operations plus file upload/download, including
  `POST /api/nodes/resolve-titles` (`{ "titles": ["Homelab"] }`) — which titles
  currently name a node, the question a `[[wiki link]]` asks — and
  `POST /api/nodes/reachable` (`{ "ids": ["…"] }`) — which of the nodes a page links
  you may open, the question a rendered page asks before it draws them.
