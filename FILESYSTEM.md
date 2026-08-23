# Filesystem of record — design

**Status**: stages 1–4 built and green, rate limiting included; 5 and 6 partly. See Plan
at the end for what is and is not done.

**Session assumptions**: Gatherum is not deployed anywhere. There is no data to preserve,
no migration to stage, and no compatibility to keep — breaking changes are free. Files are
expected to be changed through the application; external edits are a case to survive
gracefully, not a workflow to optimize for.

Today Postgres is the system of record and the file store is an opaque pool of
SHA-256-named blobs. Losing the database leaves a heap of hash-named files with no title,
no filename, no tree, no categories, no sharing rules, and no way to tell which of seven
blobs is the current version of anything. The bytes survive; the knowledge base does not.

This inverts that. **The directory tree is the system of record.** A node is a path. The
database becomes a derived index — a cache of what a scan of the directories would say
anyway — and everything a user would grieve losing lives on disk, in formats they could
read with `cat` if Gatherum vanished entirely.

It is the same unification the project already committed to in "Pages are Markdown files",
followed one level further down: pages and files stopped being different kinds of
*content*, and now nodes and files stop being different kinds of *thing*.

## The acceptance test

> Point a fresh Gatherum at a directory of user home directories it has never seen, with
> no database and no Gatherum-specific files anywhere in them. It comes up, indexes
> everything, and every file is titled, searchable, viewable, and editable.

Every decision below is subordinate to that sentence. Where a feature cannot be expressed
by a plain directory of plain files, it degrades — it never blocks the scan and never
invents a requirement that a normal directory fails to meet.

Practically this means **progressive enhancement**: a bare directory gets
filename-as-title, media type by content sniffing, full-text and semantic search,
previews, and editing. What it does not get — categories, version history, sharing beyond
private-to-the-owner — are precisely the things that only exist because Gatherum wrote
them down, and they live in a sidecar that travels with the directory.

## Layout

```
{Gatherum__Storage__Root}/
  sand_head/                    ← the user's OIDC username, verbatim where it can be
    Homelab/
      Podman.md                 ← a node. filename is the title.
      rack-photo.jpg            ← also a node. no metadata needed to be useful.
      .gatherum/
        meta.json               ← titles, categories, descriptions, ids, access
    .gatherum/
      versions/ab/cd/<sha256>   ← the CAS, demoted to history storage
  bob/
    ...
```

The root directory is named after the user's `preferred_username` — Authelia sends the
login itself, so `sand_head` is `sand_head`. That is the point of using it: somebody
looking at these directories with no Gatherum running should recognise whose is whose, and
mangling the name into a slug spends exactly what makes it useful. Only what a directory
genuinely cannot hold is replaced, and the name is assigned once and never changed, because
renaming it would mean moving every file the user owns.

`.gatherum/meta.json` is per-directory, keyed by filename within that directory, and is
the fallback carrier for everything a path cannot say. It sits *beside* the files it
describes so that moving, copying, or rsyncing a directory carries its metadata along
without a central registry to keep in sync.

For Markdown, YAML frontmatter in the file itself takes precedence — a page should be
self-describing when it can be. `meta.json` is for everything with nowhere to put a
header: PDFs, photos, recordings, archives.

Both are read through one code path resolving each property in order:

1. frontmatter (text files only)
2. `.gatherum/meta.json` in the containing directory
3. the filesystem itself (name, mtime, sniffed media type)
4. the default

## Ownership is the directory. Access is not.

These are orthogonal axes and conflating them is the main thing this section exists to
prevent.

**Ownership comes from the path**, and only from the path. A file under `alice/` is
Alice's, full stop — there is no owner field to disagree with the directory it sits in,
and no way for metadata to claim otherwise. That is what makes ownership survive the
database: it is not recorded anywhere, it is *read off the layout*.

**Access is unconstrained by the path.** Any node may be private, shared with any set of
users, or public to the internet, regardless of whose directory it lives in. A node's
location says who is responsible for it; it says nothing about who may read it. This is
why the tree a user sees is a union of "mine" and "shared with me" rather than a listing
of one directory — see Union tree below.

The connection between the two axes is authority, and it runs one way:

> **Only the owner may set access, and access rules are honored only where the owner
> could have written them** — that is, in a `.gatherum/` beneath the owner's own root.

So Alice cannot publish Bob's files by writing about them in her manifest, and a stray
`meta.json` in the wrong place grants nothing. Ownership determines *who decides*; it does
not narrow *what can be decided*.

Two consequences worth stating plainly:

- **Moving a file between user directories transfers ownership**, because ownership is the
  path. That is coherent but surprising, so a cross-root move in the UI should say so.
- **An editor of someone else's file cannot move it out of their root.** Bob editing
  Alice's page edits it in place; "moving it into his own tree" would be a transfer of
  ownership he is not entitled to make. The gesture available to him is a copy.

Nothing is indexed whose real path escapes the root it was found under — every path is
resolved before use. A symlink from `alice/` into `bob/private/` is not a sharing
mechanism, and it would otherwise be an ownership-laundering one.

## Titles

**The filename is the title. An override wins when present.**

The title of `Homelab/Podman.md` is `Podman` — extension stripped, no metadata consulted.
That is what makes the acceptance test pass on a directory nobody prepared.

An override exists because filesystems and titles disagree: `AC/DC` has a separator in it,
`CON` is reserved on Windows, ext4 stops at 255 bytes, and a quadlet named
`gatherum-postgres.container` deserves to be called "Postgres container" without being
renamed into something Podman no longer loads.

Renaming in-app therefore has two outcomes, tried in order:

1. **Move the file.** If the new title is a legal, available filename in that directory,
   the file is renamed and no metadata is written. The directory stays clean and
   navigable, which is the entire point of the exercise.
2. **Write an override.** If it is not — illegal characters, a collision, or a file whose
   name is load-bearing — the bytes stay put and the title goes in frontmatter or
   `meta.json`.

`Node.Title` survives as a column; it stops being authored directly and becomes derived at
index time. `ResolveTitlesAsync` is unchanged, including its collision rule (exact case
wins, then oldest) — which now also settles the case where one file is *named* `Podman.md`
and another carries the override title `Podman`.

## Sharing

**Everything starts private.** A node with no access metadata is visible to its owner and
nobody else. This is both the model the owner asked for and the only safe default for the
acceptance test: "no `.gatherum/` at all" and "private" are the same state, so a directory
Gatherum has never seen cannot accidentally publish anything. Given what `public` means
below, that equivalence is load-bearing rather than merely tidy.

| State | Who can see it |
|---|---|
| `private` (default) | owner only |
| `shared` | owner + named grantees |
| `unlisted` | **anyone holding the link** — reachable, and in no listing |
| `public` | **anyone on the internet, unauthenticated** — reachable and listed |

Grants name a user and a role — `reader` or `editor`. An editor was given the document,
not the filing cabinet: content changes and categories are theirs, while renaming, moving
and deleting stay with the owner, because ownership is the path and those move files
around inside somebody's directory. The owner always has full access and
cannot be locked out of their own directory. Inheritance is downward and additive: a
directory's access block applies to everything beneath it, and a node's own grants union
with what it inherits. Since the default is closed, the common gesture is opening
something up, and additive inheritance makes that the easy one — share `Homelab/` and
everything in it is shared. An access block may set `inherit: false` to start from nothing
where a subtree needs to be tighter than its parent.

### Unlisted separates reaching from enumerating

Everywhere else, "may you see this" and "may you find this" have the same answer, and
`INodeAuthorizer` can serve both from one predicate. Unlisted is where they come apart: the
id is the permission, so a direct link opens it and no tree, search, category page,
backlink or wiki-link resolution ever mentions it.

That splits the seam in two. `VisibleTo` answers enumeration and needs `Listed`; `CanSee`
answers a direct link and needs `WithLink`. The denormalized column stops being a boolean
and becomes an ordered `NodeReach` — `None`, `WithLink`, `Listed` — which also makes
inheritance a maximum rather than a boolean or. `Shared` has no place on that scale:
naming a person is not reach, and the grant closure already records it.

The owner and the grantees are unaffected — an unlisted node stays in *their* tree and
*their* search. Unlisted hides it from people who do not already have access some other
way, which is the only reading that makes it useful.

Unlisted pages send `noindex, nofollow`. Nothing links to them, but a crawler can arrive
by a leaked referrer, and a search engine is exactly the thing that turns "anyone with the
link" into "everyone".

### Public means public

`public` is not "any signed-in user". It is on the internet, for better or worse: no
session, no OIDC round trip, no API key. Marking a node public is a publishing gesture and
the UI should treat it as one — a distinct affordance from sharing with a person, with a
copyable link and unambiguous wording about what just happened.

Gatherum is `RequireAuthorization` end to end today, so this is a genuinely new surface,
and it should be a narrow one:

- **Anonymous is read-only, always.** No write path is ever reachable without
  authentication, whatever a node's access says. `editor` is meaningless for anonymous
  visitors and public never implies it.
- **Anonymous reach is exactly the public subtree**: node content, previews, downloads,
  backlinks and category listings filtered to public nodes, and search restricted the same
  way. A public wiki that cannot be searched is not much of one.
- **Rate limiting stops being optional**, and is implemented: reads and searches each get
  a per-minute budget per client address, searches much tighter because the semantic half
  runs a model on the request path. Signed-in callers are never metered — they
  authenticated to get here, and an IP-keyed bucket shared with the internet would meter
  them unpredictably.
- **MCP and `/api` writes stay authenticated.** Read endpoints gain an anonymous path;
  everything else keeps its current posture.

The seam makes this cheaper than it sounds. `INodeAuthorizer.VisibleTo(IQueryable<Node>,
Guid userId)` is the single funnel every visibility-sensitive query already goes through —
tree, search, categories, similar, backlinks, title resolution, ten call sites in all.
Widening it to `Guid?`, where `null` means anonymous and matches only public nodes, gets
correct anonymous behavior across every one of them at once, rather than one audited
endpoint at a time. That seam is the reason this design is safe to attempt; keep it the
only door.

An instance-level kill switch (`Gatherum__Sharing__AllowPublic`, default on) lets an
operator disable public sharing outright without touching per-node metadata.

## What the database becomes

A cache. Everything in it is either a copy of something on disk or recomputed from it:

| Table | After |
|---|---|
| `Nodes` | derived from paths, names, frontmatter, `meta.json` |
| `FileVersions` | derived from `.gatherum/versions/` + manifest history |
| `Categories`, `NodeCategories` | derived from frontmatter / `meta.json` |
| `NodeLinks` | reparsed from body content |
| `NodeEmbeddings` | recomputed locally |
| `Users`, `ApiKeys`, `DataProtectionKeys` | **the exceptions — genuinely DB-only** |

So `gatherum reindex`, and a scan on startup, become the whole disaster-recovery story:
drop the database, restart, and everything returns except recomputed vectors and re-run
model analysis. API keys are the one thing worth a `pg_dump`, and they are cheap to
reissue.

The existing staleness rule pays for itself here unmodified. Because a node is stale for
embedding exactly when `TextFingerprint` differs from `EmbeddedFingerprint`, and because
that comparison is the only thing that queues work, a rebuilt index re-embeds
automatically and re-embeds *only* what actually changed. That rule was written for
category renames; it turns out to be the reindex design too.

Since nothing is deployed, the four existing migrations should be squashed into a fresh
`Initial` rather than extended. The schema that comes out of this is different enough that
carrying its own history would be archaeology of a database nobody ever ran.

## Versions

The working file is the current version. History is a CAS under `.gatherum/versions/`,
with ordering recorded in the directory's manifest.

This is the deliberate asymmetry of the whole design: **current state is a plain file,
history is Gatherum's bookkeeping.** Delete `.gatherum/versions/` and you lose history and
keep every document. That is the right way round, and the opposite of today, where the
bookkeeping is the only thing that knows what a document *is*.

Content-addressing keeps earning its place here — dedup across versions, restore as a
copy, analysis reuse for identical bytes — it just stops being the namespace.

## Identity and links

Node GUIDs stay, because `[@Title](node://id)` mentions and backlinks depend on them, and
because path-as-identity breaks every link the first time someone reorganizes a folder.

The GUID is recorded on disk — frontmatter for Markdown, `meta.json` otherwise — and
assigned on first index for files never seen before. A file that arrives with no id gets
one; a file that moves keeps the one it has.

When a path changes behind Gatherum's back, identity is recovered in order: recorded id,
then content hash matched against the last known index, then treated as new. Because
external edits are the exception rather than the workflow, the recorded id carries almost
every case and the content-hash step is a safety net rather than a load-bearing mechanism.

## External changes

Files are expected to change through the application. External edits should be survived,
not courted, which makes this much smaller than it would otherwise be:

- **Reconciliation on startup is the primary mechanism.** Scan the roots, compare against
  the index, apply what changed. This is the same code path as `reindex` and it is needed
  for cold-start recovery regardless, so it costs nothing extra.
- **A debounced `FileSystemWatcher` per root is a best-effort convenience** on top, not a
  correctness requirement. If it misses an event, the next startup scan catches it.
- **In-app writes are marked** so Gatherum does not reindex its own saves.
- **Nothing external is ever destroyed to resolve a discrepancy.** Where the index and the
  disk disagree, the disk wins and the index yields; where that would lose in-app state
  (a version chain whose head no longer matches the file), the file is taken as a new
  version rather than a correction, and it is logged.

## What this costs

- **Dedup across nodes disappears.** Two users with the same 2 GB video pay twice, and pay
  for two transcriptions rather than reusing one by hash. Acceptable for a two-person
  instance; worth knowing a real property is being spent.
- **Writes stop being idempotent.** `SaveAsync` currently makes a duplicate upload a no-op
  by construction. Path-addressed writes need real conflict handling.
- **Path traversal becomes a live concern.** `PathFor`'s 64-hex validation made escaping
  the root structurally impossible. User-controlled names and symlinks are new attack
  surface, closed deliberately by the realpath rule above.
- **Anonymous traffic is new.** Public nodes mean unauthenticated readers, which means
  rate limiting, abuse handling, and a much lower tolerance for a visibility bug.
- **The tree stops being one directory**, as ownership and access come apart.
- **Filesystem naming rules reach the user**, mitigated but not erased by overrides.

## Standing rules this amends

`AGENT.md`'s "Rules that don't bend" describes the current architecture and needs updating
as stages land:

- *"every node's content is a file version in content-addressed storage"* → content is a
  file at a path; the CAS holds history.
- *"Two trees, and only two"* → still two, but the node tree is the directory tree, viewed
  per user as a union of owned and shared-in.
- *"Auth is OIDC-only"* → still true for identity; anonymous read of public nodes is not
  identity and grants nothing.
- *"Don't add interfaces without a stated second implementation"* → `IFileStorage` is
  replaced rather than joined; the replacement is the point.

## Plan

Each stage is shippable alone, ordered so the payoff arrives before the risk. No migration
work anywhere: nothing is deployed, so each stage may rebuild the schema and re-scan from
disk.

1. **Path-shaped storage** — *done*. `IFileStorage` is path-addressed, user roots exist,
   the CAS is `.gatherum/versions/`, migrations squashed to a fresh `Initial`.
2. **Metadata sidecar + reindex** — *done, minus frontmatter*. `meta.json` carries titles,
   descriptions, categories, access, grants and history; `Reindexer` rebuilds from a cold
   scan and runs at startup. **The original motivation is retired here.** Markdown does
   not yet carry its own frontmatter — `meta.json` is the only carrier — so a page is not
   yet self-describing on its own.
3. **Sharing model** — *done*. Three states, grants with roles, additive inheritance,
   `inherit: false`, the authority rule, `VisibleTo(…, Guid?)`.
4. **Public on the internet** — *done*. Anonymous reads reach public nodes through the
   same seam; every write refuses anonymous; reads and searches are rate limited per
   client address with signed-in callers exempt; `Gatherum__Sharing__AllowPublic=false`
   hides every public node at once, immediately and without editing what an owner
   recorded on disk.
5. **Union tree** — *partly*. The data is right: `GetTreeAsync` returns owned plus
   shared-in, and `TreeNode.Owned` distinguishes them. The UI shows badges and offers the
   three reach states on nodes you own, but there is still no share-with-a-person control
   and no grouping of shared-in items.
7. **Reading in a browser without signing in** — *done*. `/` is the published index for a
   stranger and the usual home for everybody else; a node page renders for whoever may
   reach it, read-only and with no editor island; the chrome offers Sign in and nothing
   that writes.
6. **External change reconciliation** — *partly*. The startup scan is the mechanism and it
   works, including identity-by-recorded-id and new-version-on-external-edit. There is no
   `FileSystemWatcher`, no content-hash rename detection, and no self-write suppression,
   so an external change is picked up at next startup rather than immediately.
