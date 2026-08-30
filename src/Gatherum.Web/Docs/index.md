# Gatherum

Gatherum is a self-hosted, web-first knowledge base built on one idea: **pages and files
are the same kind of thing.** Every item is a *node* — it has a title, one place in one
tree, categories, links, backlinks, version history, and searchable text. A page is
simply a node whose file is Markdown; a chapter draft, a Podman quadlet, a PDF and a
photo all live in one tree, one search, one login, one API.

This manual ships inside the app. Every copy of Gatherum serves it at `/docs`, so it
always describes the version you are actually running.

## Handing this to a model

The Markdown a page is written in has a few constructs no model has seen before —
`[[wiki links]]`, `:::infobox` and `:::figure` asides, `> [!NOTE]` callouts. Give an
assistant a link and it will know them:

| Link | What it is |
| --- | --- |
| `/docs/markdown.md` | The dialect on its own — the one page to paste if you only paste one |
| `/docs/all.md` | The entire manual as a single Markdown file |
| `/docs/llms.txt` | An index of every page, in the [llms.txt](https://llmstxt.org) convention |
| `/docs/<page>.md` | The Markdown source of any page here |

All four are readable without signing in: this manual is the same in every install and
says nothing about what is in yours. A model with an API key can also read the manual
over MCP or REST like any other document, but it does not need one to read this.

If the assistant is going to *write* pages rather than just read them, point it at
[Working with agents](/docs/agents) too — it is the short version of everything below,
in the order an agent needs it.

## The pages

- **[Markdown in Gatherum](/docs/markdown)** — every construct a page can contain, what
  it does, and the rules the parser follows.
- **[Pages and files](/docs/pages-and-files)** — nodes, the tree, editing, uploads,
  versions, and what Gatherum reads out of a file.
- **[Categories](/docs/categories)** — what a node is *about*. Each one is a page of its
  own, and nesting one is filing it under another.
- **[Collectible lists](/docs/collections)** — a shared list people tick against, each
  keeping their own answer, and everyone seeing everyone else's.
- **[Search](/docs/search)** — the two halves of the search box and when to reach for
  each.
- **[Sharing and privacy](/docs/sharing)** — private, shared, unlisted, public, and the
  difference between reaching a node and finding one.
- **[REST API](/docs/api)** — every endpoint under `/api`, with the shapes it takes and
  returns.
- **[MCP server](/docs/mcp)** — the fifteen tools at `/mcp`, and how to point an agent
  at them.
- **[Working with agents](/docs/agents)** — the briefing to give a model that is going
  to write here.
- **[Configuration](/docs/configuration)** — environment variables, deployment, storage
  layout, and backups.

## The shape of the thing

A few facts everything else rests on:

- **A node is a file.** Its content is a plain file on disk at a readable path, under a
  directory named for its owner. Gatherum's database is an index over those directories
  and can be rebuilt from them.
- **A page is a node whose file is Markdown.** Nothing distinguishes it in the database:
  `Kind` is derived from the media type, so renaming `notes.txt` to `notes.md` makes it
  a page.
- **One tree for placement, one graph for subject.** A node sits in exactly one place in
  the node tree (its parent), and belongs to any number of categories. A category is a
  page too, and filing one under another is what makes it a subcategory — so a subject
  can sit under two parents at once. There are no tags.
- **Every save is a version.** Old versions stay downloadable, restorable, and readable
  as HTML.
- **Private by default.** A node nobody has published is its owner's alone, and so is
  everything under it.
