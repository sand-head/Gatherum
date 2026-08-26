# Working with agents

This page is the briefing to hand a model that is going to read or write here. Give it
this URL, or `/docs/all.md` for the whole manual in one fetch.

## The one-paragraph version

Gatherum is a wiki where every item — page, PDF, photo, quadlet — is a node in one tree
with categories, links, versions and full-text plus semantic search. A page is a
Markdown file and nothing else; what you write is what the file holds. Reach it over
[MCP](/docs/mcp) at `/mcp` or the [REST API](/docs/api) at `/api`, both with
`Authorization: Bearer gk_…`.

## The dialect, in twelve lines

Everything below is plain Markdown in the file. [The full page](/docs/markdown) has the
rules; this is the shape.

```markdown
[@Title](node://<node-id>)            mention — links by id, survives a rename
[[Title]]                             wiki link — links by title, case-insensitive
[[Title|label]]                       …with a label ( escape the pipe inside a table )
![alt](/api/files/<node-id>/content)  embed another node's bytes

:::infobox                            an encyclopedia card, floats right
# Heading
| **Label** | Value |
:::

:::figure left 360                    a captioned picture, floats left, 360px wide
![alt](/api/files/<node-id>/content)
The caption
:::

> [!NOTE] Optional title              note, tip, important, warning, caution
> The body of the callout.
```

The traps, all of which are quiet rather than loud:

- A `:::` fence that never closes is **not** an aside — it is two lines of text. Always
  close it.
- Asides do not nest, and two with nothing but a blank line between them overlap on the
  page — put a paragraph between them, or one at each margin.
- Only those five callout kinds exist. `> [!HINT]` is an ordinary block quote.
- A wiki link never spans a line break, and is ignored inside code spans and fences.
- `[[Title]]` resolves against titles that exist *right now*. A title nothing answers to
  renders as a red link — which is a fine thing to leave deliberately, and a bad thing
  to leave by accident.

## Writing a page well

1. **Search first.** `search` with a couple of phrasings. Two pages about one subject is
   the failure mode this tool has.
2. **Title it as it would be linked.** The title is the wiki-link key, so prefer the
   name someone would type in prose: `Podman` over `Notes on podman (2026)`.
3. **Place it.** `parentId` puts it under the right part of the tree; omitting it makes
   a top-level page, which is rarely what you want.
4. **File it.** `list_categories` first, then `add_category` with a name that already
   exists if one fits — a new name writes a new category page, and two spellings of one
   subject are two subjects. Match the existing capitalization.
5. **Link outward.** Mention or wiki-link the pages this one talks about. Backlinks are
   recorded on save and are most of what makes the wiki navigable.
6. **Lead with the answer.** An infobox for the facts, a first paragraph that says what
   the thing is, then the detail.

## Editing an existing page

`update_page` **replaces the whole body**. Fetch it with `get_node`, change what needs
changing, and send the rest back unaltered — the round trip is lossless, so an unchanged
region should come back byte-identical.

Every save records a version, and saves by the same author inside five minutes collapse
into one, so a couple of quick corrections do not litter the history. Nothing is
overwritten irrecoverably: an earlier version can always be restored.

## What you cannot do

- **Upload or delete** — no MCP tool does either. The REST API deletes; the REST API
  uploads.
- **Change sharing** — REST only, and only as the owner.
- **See someone else's private subtree** — it is not hidden from you, it is absent.
  Do not report it as missing data.

## When something is not there

A page that does not exist yet is worth a red link rather than a guess: write
`[[The Thing]]` in the prose that needed it and leave the page uncreated. Somebody — or
you, next time — gets an offer to write it, and in the meantime the intent is recorded
where it matters instead of in a to-do list nobody reads.
