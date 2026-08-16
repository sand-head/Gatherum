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

Verify with `/mcp` inside Claude Code — the `gatherum` server should list nine tools.

For local development (`dotnet run`, default port 5140):

```sh
claude mcp add --transport http gatherum http://localhost:5140/mcp \
  --header "Authorization: Bearer gk_…"
```

## Tools

| Tool | Arguments | Returns |
| --- | --- | --- |
| `search` | `query`, `kind?` (`page`/`file`), `limit?` | Matches with kind and snippet |
| `get_node` | `id` | Metadata + Markdown body (pages) or extracted text + file metadata (files) |
| `list_children` | `id?` (omit for roots) | Children in tree order |
| `create_page` | `title`, `markdown`, `parentId?` | The created node |
| `update_page` | `id`, `markdown`, `title?` | The updated node (a new revision is recorded) |
| `move_node` | `id`, `newParentId?`, `position?` | Confirmation |
| `add_tag` | `id`, `tag` | Confirmation |
| `list_tags` | — | Tags with node counts |
| `get_backlinks` | `id` | Nodes linking to the given node |

## Markdown conventions

Page bodies round-trip between the editor and Markdown. Two Gatherum-specific forms:

- **Mentions**: `[@Some Node](node://<node-id>)` — renders as an @-mention in the editor
  and creates a link (and therefore a backlink on the target).
- **Embedded files**: `![alt](/api/files/<node-id>/content)` — embeds a file node's
  content and links to it.

Everything else is GitHub-flavored Markdown: headings, lists, `- [ ]` task lists, pipe
tables, fenced code blocks with language, `> [!info]`-style callouts (info, note, tip,
warning, danger), and inline bold/italic/strike/code/links.

## Notes

- Private subtrees belonging to other users are invisible to your key — searches,
  `get_node`, and `list_children` behave as if they don't exist.
- The REST API under `/api` accepts the same bearer token and covers the same
  operations plus file upload/download.
