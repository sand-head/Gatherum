# REST API

Everything the UI does, it does through `/api`. The endpoints are thin adapters over the
same application services the pages use, so the API cannot drift from the app.

## Authenticating

Send an API key as a bearer token:

```sh
curl -H "Authorization: Bearer gk_…" https://gatherum.example.org/api/nodes/roots
```

Keys are created in **Settings → API keys** and shown once. A browser session works too —
that is how inline previews fetch bytes — but scripts should use a key.

A handful of read endpoints also accept no credentials at all, and then see only what is
[public](/docs/sharing). They are marked *anonymous* below and are rate-limited per
client address.

## Errors

| Status | Means |
| --- | --- |
| `400` | The request did not make sense; the body is `{ "error": "…" }` |
| `401` | No credentials, on an endpoint that needs them |
| `403` | Authenticated, but not allowed to do that |
| `404` | No such node, or none you may see |
| `429` | Anonymous rate limit; `Retry-After` says how long |

## Reading

| Endpoint | Notes |
| --- | --- |
| `GET /api/nodes/roots` | Top-level nodes — *anonymous* |
| `GET /api/nodes/tree` | The whole visible tree, flat, with access and reach — *anonymous* |
| `GET /api/nodes/{id}` | One node with its body — *anonymous* |
| `GET /api/nodes/{id}/children` | Children in tree order — *anonymous* |
| `GET /api/nodes/{id}/backlinks` | Nodes whose bodies link to this one — *anonymous* |
| `GET /api/nodes/{id}/list?name=` | A [shared list](/docs/lists): rows, and every tally's answers — *anonymous* |
| `POST /api/nodes/{id}/list` | `{ key, answered, name? }` — records one answer on your own tally |
| `GET /api/nodes/{id}/similar?limit=` | Related nodes: shared categories, links, and semantic likeness — *anonymous* |
| `GET /api/nodes/{id}/versions` | Version history, newest first |
| `GET /api/search?query=&kind=&limit=&mode=` | See [Search](/docs/search) — *anonymous* |

A node comes back like this:

```json
{
  "id": "8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f",
  "kind": "Page",
  "title": "Podman on the NAS",
  "parentId": "…",
  "position": 3,
  "access": "Private",
  "categories": [{ "id": "3c1e…", "name": "Podman" }],
  "createdAt": "2026-01-04T10:12:00+00:00",
  "updatedAt": "2026-02-18T22:41:07+00:00",
  "markdown": "# Podman on the NAS\n…",
  "file": {
    "fileName": "podman-on-the-nas.md",
    "mediaType": "text/markdown",
    "sizeBytes": 4210,
    "version": 12,
    "sha256": "…",
    "description": "",
    "extractedText": "…",
    "transcript": "",
    "summary": "",
    "analysis": "None",
    "analysisError": null
  }
}
```

`markdown` is the body itself, and only for pages. For everything else the words live in
`file.extractedText`, with `file.transcript` and `file.summary` carrying what a model
read or heard when [analysis](/docs/pages-and-files) is configured.

## Writing pages

| Endpoint | Body |
| --- | --- |
| `POST /api/pages` | `{ "title": "…", "markdown": "…", "parentId": null }` |
| `PUT /api/pages/{id}` | `{ "markdown": "…", "title": "…" }` — title optional |
| `PUT /api/text/{id}` | `{ "text": "…" }` — any editable text node, pages included |
| `PUT /api/binary/{id}` | Raw bytes; for rich documents (`.docx`) |

Each write records a version. Saves by the same author within five minutes collapse into
the latest one.

## Files

| Endpoint | Notes |
| --- | --- |
| `POST /api/files` | Multipart `file`, optional `parentId` |
| `POST /api/files/{id}/versions` | Multipart `file` — a new version of an existing node |
| `GET /api/files/{id}/content?version=` | The bytes, inline, range requests supported — *anonymous* |
| `GET /api/files/{id}/download?version=` | The bytes, as an attachment — *anonymous* |
| `GET /api/files/{id}/epub?version=` | An EPUB's map: `{ title, chapters: [name…], position, version, renderer }` — *anonymous*, though only a signed-in reader gets a `position`; never cached |
| `GET /api/files/{id}/epub/{chapter}?version=` | One chapter as a self-contained, paginated page — *anonymous*. With `version` pinned the response is cacheable forever; bare it means "the latest" and is `no-store` |
| `PUT /api/files/{id}/epub/position` | `{ "chapter": 3, "progress": 0.5 }` — where this reader left off |
| `PUT /api/files/{id}/description` | `{ "description": "…" }` |

`/api/files/{id}/content` is also the URL a page embeds a file with; see
[Markdown](/docs/markdown).

An upload may be up to 2 GB — enough for a GameCube disc. A client that would rather
not hold a file that size in memory, which is what the app's own uploader is when it
runs in the browser, sends it in pieces instead:

| Endpoint | Notes |
| --- | --- |
| `POST /api/uploads` | `{ "fileName": "…", "contentType": "…" }` — begins a file; answers `{ "id": … }` |
| `PATCH /api/uploads/{id}?offset=` | Raw bytes, appended. `offset` is where the file ends so far, and a piece that says otherwise is refused with `409` |
| `POST /api/uploads/{id}/finish?parentId=` | The assembled file becomes a new node, exactly as `POST /api/files` would have made it |
| `POST /api/uploads/{id}/finish?nodeId=` | …or a new version of an existing one |
| `DELETE /api/uploads/{id}` | Abandons it. An upload nobody has touched for an hour is abandoned for them |

Only the key that began an upload may add to it or finish it, and an upload does not
survive the server restarting — the answer is a `404`, and the cure is to send it again.

## Bookmarks

| Endpoint | Body |
| --- | --- |
| `POST /api/bookmarks` | `{ "url": "https://…", "parentId": null }` — fetches the URL now and keeps the capture as a file node |
| `POST /api/bookmarks/{id}/capture` | Fetches the bookmark's URL again; the capture is a new version |

The fetch happens inside the request — expect it to take as long as the other server
does, up to a bound — and a refusal comes back as `400` with a sentence saying what the
server answered. A bookmark's node carries `sourceUrl` in its file metadata. See
[Bookmarks](/docs/pages-and-files#bookmarks).

## Structure

| Endpoint | Body |
| --- | --- |
| `POST /api/nodes/{id}/move` | `{ "newParentId": null, "position": 0 }` |
| `POST /api/nodes/{id}/rename` | `{ "title": "…" }` |
| `DELETE /api/nodes/{id}` | Deletes the node and its subtree |
| `POST /api/nodes/{id}/versions/{n}/restore` | Brings version `n` back as a new version |

## Categories

| Endpoint | Notes |
| --- | --- |
A category is a node — see [Categories](/docs/categories) — so it is read, renamed,
re-nested and deleted through the node endpoints above. What is left here is the taxonomy
itself.

| Endpoint | Notes |
| --- | --- |
| `GET /api/categories?matching=` | Every category, with member counts and its parents' ids |
| `GET /api/categories/{name}?deep=` | One category: its parents, subcategories, members |
| `POST /api/nodes/{id}/categories` | `{ "name": "Podman" }` — writes the category's page if none exists |
| `DELETE /api/nodes/{id}/categories/{name}` | Unfile from one category |

Nesting is the same call: `POST /api/nodes/{podmanCategoryId}/categories` with
`{ "name": "Homelab" }` makes Podman a subcategory of Homelab. A category can be nested
under more than one, so `parentIds` is a list and there is no path up to a single root.

The category's own page — its prose, its history, its access — is `GET /api/nodes/{id}`
like any other node, with `"kind": "Category"`.

## Sharing

| Endpoint | Body |
| --- | --- |
| `POST /api/nodes/{id}/access` | `{ "access": "Private\|Shared\|Unlisted\|Public", "inherit": true }` |
| `GET /api/nodes/{id}/grants` | Who this node is shared with |
| `POST /api/nodes/{id}/grants` | `{ "userId": "…", "role": "Reader\|Editor" }` |
| `DELETE /api/nodes/{id}/grants/{userId}` | Revoke one grant |
| `GET /api/users` | Who there is to share with — signed-in callers only |

## Links and titles

`POST /api/nodes/resolve-titles` answers the question a `[[wiki link]]` asks — which of
these titles currently name a node:

```sh
curl -X POST https://gatherum.example.org/api/nodes/resolve-titles \
  -H "Authorization: Bearer gk_…" -H "Content-Type: application/json" \
  -d '{ "titles": ["Homelab", "Nothing By This Name"] }'
```

```json
[{ "title": "Homelab", "id": "0f8f6e1a-…" }]
```

Titles that name nothing are simply absent from the answer — that is what makes a wiki
link render red.

Two more endpoints — `GET /api/nodes/{id}/presence` and
`POST /api/nodes/{id}/presence/leave` — exist for the editor to say who is typing. They
are an implementation detail of the editing UI rather than part of the API's surface.

## Keys

| Endpoint | Notes |
| --- | --- |
| `GET /api/keys` | Your keys: name, prefix, when last used |
| `POST /api/keys` | `{ "name": "…" }` — the response is the only time the token is shown |
| `DELETE /api/keys/{id}` | Revoke |

## The docs themselves

Outside `/api`, and readable without credentials:

| Endpoint | Returns |
| --- | --- |
| `GET /docs/llms.txt` | An index of this manual, for a model to follow |
| `GET /docs/all.md` | The whole manual as one Markdown file |
| `GET /docs/{page}.md` | One page's Markdown source |
