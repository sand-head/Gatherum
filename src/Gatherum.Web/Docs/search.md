# Search

There is one search box, at the top of every page. `Ctrl`/`⌘`+`K` anywhere puts the
caret in it, matches appear under it as you type — `↑`/`↓` and `Enter` to open one,
`Esc` to dismiss the list. Behind it are two searches that answer different questions,
and both work out of the box.

## The two halves

**Full text.** PostgreSQL's own text search (`tsvector` + GIN, `websearch_to_tsquery`)
over titles, category names, and text — including what a model read, heard or made of
your media. This is the half that finds the phrase you remember word for word.

**Semantic.** Pages, files and transcripts are cut into passages and embedded into
pgvector, so a search for "why the closet gets so hot" finds the page that only ever says
"thermals". The embedding model **ships with Gatherum** — twenty-three megabytes of
MiniLM, run in-process on the CPU — so there is no endpoint to stand up and nothing sent
anywhere. An instance can point at a better model instead; see
[Configuration](/docs/configuration).

The two rankings are **fused, never averaged**: each half ranks independently and the
lists are combined by rank, so a strong hit in either half surfaces.

## Modes

| Mode | Asks |
| --- | --- |
| `hybrid` *(default)* | Both halves, fused |
| `text` | The literal half alone |
| `semantic` | The meaning half alone |

Reach for `text` when the exact spelling is the point — an identifier, a filename, a
phrase you are quoting — because **only the full-text half honours query syntax**:

```
"exact phrase"        a phrase, in order
podman OR docker      either word
podman -kubernetes    the first, without the second
```

Reach for `semantic` when you remember what something was about but not what it said.

## What a result carries

A result is an id, a kind (`Page` or `File`), a title, and a snippet. The snippet comes
from wherever the match was: for a literal hit it is the text around the words you typed,
and for a semantic hit it is the passage that matched — which may be nowhere near the top
of the document.

Results can be filtered to pages or to files, and the limit runs from 1 to 100
(20 by default).

## What is searchable

- Titles, weighted above body text.
- The names of every category a node is filed in.
- A file's description.
- Extracted text: Markdown and code verbatim, a PDF's text layer, a `.docx`'s Markdown
  rendering, an image's metadata.
- If [analysis](/docs/pages-and-files) is configured: what a model read off an image,
  transcribed from audio or video, and summarized.

## Freshness

Full-text results are current the moment a save completes. Semantic results lag by
however long the embedding worker takes to notice — it sweeps every fifteen seconds by
default and works through what changed. A node is considered stale when a fingerprint of
everything it is embedded from stops matching the fingerprint its vectors were made
from, which is also why renaming a category re-embeds the hundred nodes filed under it
without anything having to remember to ask.

## When the model is unreachable

Never a failed search. If embeddings are switched off, or the endpoint an owner
configured is down, or embedding the query takes longer than its (short) budget, the
search answers from full text alone. A search never fails because a model is down.

## Privacy

Search only ever returns what the searcher may see, and that is enforced in the query
rather than filtered afterwards. Private subtrees belonging to someone else, and
[unlisted](/docs/sharing) nodes, do not appear — an unlisted node is reachable by its
link and by nothing else, search included.

Anonymous visitors can search too, if the instance publishes anything; their searches
are rate-limited more tightly than reads because the semantic half runs a model on the
request path.
