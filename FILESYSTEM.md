# Filesystem of record — design

**Status**: proposed (owner direction). Nothing below is built yet.

Today Postgres is the system of record and the file store is an opaque pool of
SHA-256-named blobs. Lose the database and what survives is a heap of hash-named
files with no title, no filename, no tree, no categories, no sharing rules, and no
way to tell which of seven blobs is the current version of anything. The bytes
survive; the knowledge base does not.

This inverts that. **The directory tree is the system of record.** A node is a path.
The database becomes a derived index — a cache of what a scan of the directories
would tell you anyway — and everything a user would grieve losing lives on disk, in
formats they could read with `cat` if Gatherum vanished entirely.

It is the same unification the project already committed to in
"Pages are Markdown files", followed one level further down: pages and files stopped
being different kinds of *content*, and now nodes and files stop being different kinds
of *thing*.

## The acceptance test

> Point a fresh Gatherum at a directory of user home directories it has never seen,
> with no database and no Gatherum-specific files anywhere in them. It comes up, indexes
> everything, and every file is titled, searchable, viewable, and editable.

Every decision below is subordinate to that sentence. Where a feature cannot be
expressed by a plain directory of plain files, it degrades — it never blocks the scan
and never invents a requirement that a normal directory fails to meet.

Practically this means **progressive enhancement**: a bare directory gets
filename-as-title, media type by content sniffing, full-text and semantic search,
previews, and editing. What it does not get — categories, version history, sharing
beyond "private to the owner" — are precisely the things that only exist because
Gatherum wrote them down, and they live in a sidecar that travels with the directory.

## Layout

```
{Gatherum__Storage__Root}/
  alice/                        ← one directory per user; the name is the mapping key
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

`.gatherum/meta.json` is per-directory, keyed by the filename within that directory,
and is the fallback carrier for everything a path cannot say. It sits *beside* the
files it describes so that moving, copying, or rsyncing a directory carries its
metadata along without a central registry to keep in sync.

For Markdown specifically, YAML frontmatter in the file itself takes precedence — a
page should be self-describing when it can be. `meta.json` is for everything with
nowhere to put a header: PDFs, photos, recordings, archives.

Both are read through one code path that resolves, per property, in this order:

1. frontmatter (text files only)
2. `.gatherum/meta.json` in the containing directory
3. the filesystem itself (name, mtime, sniffed media type)
4. the default

## Titles

**The filename is the title. An override wins when present.**

The title of `Homelab/Podman.md` is `Podman` — extension stripped, no metadata
consulted. That is what makes the acceptance test pass on a directory nobody prepared.

An override exists because filesystems and titles disagree: `AC/DC` has a separator in
it, `CON` is reserved on Windows, ext4 stops at 255 bytes, and a quadlet named
`gatherum-postgres.container` deserves to be called "Postgres container" without being
renamed into something Podman no longer loads.

Renaming in-app therefore has two outcomes, tried in order:

1. **Move the file.** If the new title is a legal, available filename in that directory,
   the file is renamed and no metadata is written. The directory stays clean and
   navigable, which is the entire point of the exercise.
2. **Write an override.** If it is not — illegal characters, a name collision, or a file
   whose name is load-bearing — the bytes stay where they are and the title is recorded
   in frontmatter or `meta.json`.

`Node.Title` survives as a column; it stops being authored directly and becomes derived
at index time. `ResolveTitlesAsync` is unchanged, including its collision rule (exact
case wins, then oldest) — which now also settles the new case where one file is named
`Podman.md` and another carries the override title `Podman`.

## Sharing

**Everything starts private.** A node with no access metadata is visible to its owner
and to nobody else. This is both the model the owner asked for and the only safe
default for the acceptance test: a directory Gatherum has never seen before cannot
accidentally publish anything, because "no `.gatherum/` at all" and "private" are the
same state.

Three states, per node:

| State | Who can see it |
|---|---|
| `private` (default) | owner only |
| `shared` | owner + named grantees |
| `public` | everyone (see anonymous access below) |

Grants name a user and a role — `reader` or `editor`. The owner always has full access
and cannot be locked out of their own directory.

**Inheritance is downward and additive.** A directory's access block applies to
everything beneath it, and a node's own grants union with what it inherits. Since the
default is closed, the common gesture is opening something up, and additive inheritance
makes that the easy one: share `Homelab/`, and everything in it is shared. An access
block may set `inherit: false` to start from nothing when a subtree genuinely needs to be
tighter than its parent.

Two invariants that keep this from becoming a hole:

- **Authority is local to the owner's directory.** A grant is honored only when it appears
  in a `.gatherum/` beneath the directory of the user who owns the file. Alice cannot
  publish Bob's files by writing about them in her own manifest, whatever her manifest says.
- **Nothing is indexed whose real path escapes its owner's root.** Every path is resolved
  before use and rejected if it leaves the root it was found under. A symlink from
  `alice/` into `bob/private/` is not a sharing mechanism.

Effective access stays denormalized onto `Node` at index time, exactly as
`PrivateToUserId` is today, so visibility remains an indexed predicate rather than an
ancestor walk. `INodeAuthorizer.VisibleTo` remains the single seam every query goes
through; its predicate changes, its shape does not.

**Anonymous access is a separate switch.** `public` means "every signed-in user of this
instance" unless `Gatherum__Sharing__AllowAnonymous=true`, in which case public nodes are
readable without signing in. Gatherum is OIDC-only today and unauthenticated reads are a
real change in exposure, so it is opt-in per instance rather than implied by the word
"public".

**Migration must preserve effective visibility, not re-derive it.** Every existing node
is visible to both users today. Flipping the default to private cannot silently hide a
two-person knowledge base, and re-deriving from the new default would do exactly that. The
migration writes explicit grants: nodes that are not `IsPrivate` become `shared` with the
other user as `editor`; `IsPrivate` nodes become `private`. Nobody's tree changes shape on
upgrade.

## What the database becomes

A cache. Everything in it is either a copy of something on disk or recomputed from it:

| Table | After |
|---|---|
| `Nodes` | derived from paths, names, frontmatter, `meta.json` |
| `FileVersions` | derived from `.gatherum/versions/` + manifest history |
| `Categories`, `NodeCategories` | derived from frontmatter / `meta.json` |
| `NodeLinks` | reparsed from body content |
| `NodeEmbeddings` | recomputed locally |
| `Users`, `ApiKeys` | **the exception — genuinely DB-only** |

So `gatherum reindex` (and a scan on startup) becomes the whole disaster-recovery story:
drop the database, restart, and everything returns except recomputed vectors and re-run
model analysis. API keys are the one thing worth a `pg_dump`, and they are cheap to
reissue.

The existing staleness rule pays for itself here without modification. Because a node is
stale for embedding exactly when `TextFingerprint` differs from `EmbeddedFingerprint`, and
because that comparison is the only thing that queues work, a rebuilt index automatically
re-embeds — and re-embeds *only* what actually changed, rather than the whole corpus. That
rule was written for category renames; it turns out to be the reindex design too.

## Versions

The working file is the current version. History is a CAS under
`.gatherum/versions/`, with the ordering recorded in the directory's manifest.

This is the deliberate asymmetry of the whole design: **current state is a plain file,
history is Gatherum's bookkeeping.** Delete `.gatherum/versions/` and you lose history
and keep every document. That is the right way round, and it is the opposite of today,
where the bookkeeping is the only thing that knows what the document *is*.

Content-addressing keeps earning its place here — dedup across versions, restore as a
copy, and the analysis-reuse that falls out of identical bytes — it just stops being the
namespace.

## Identity and links

Node GUIDs stay, because `[@Title](node://id)` mentions and backlinks depend on them and
because path-as-identity breaks every link the first time someone reorganizes a folder in
their file manager.

The GUID is recorded on disk — frontmatter for Markdown, `meta.json` otherwise — and
assigned on first index for files that have never been seen. A file that arrives with no
id gets one; a file that moves keeps the one it has.

When a path changes behind Gatherum's back, identity is recovered in this order: id in
frontmatter or manifest, then content hash matched against the last known index, then
treated as a new node. The content-hash step is what turns an external
`mv` from "delete plus create" — which would orphan every inbound link — into a move.
The CAS earning its keep in its new role.

## External changes

The point of the design is that people edit these directories outside Gatherum, so
Gatherum has to notice:

- A `FileSystemWatcher` per root, debounced, feeding the same indexing path as upload.
- A reconciliation scan at startup for everything that changed while the process was down.
- Rename and move detection per the identity rules above.
- In-app saves marked so the watcher does not reindex Gatherum's own writes.

This is the largest new subsystem and the one most likely to produce subtle bugs. It is
also the last stage of the plan, because everything before it is useful without it.

## What this costs

Honest ledger of what gets worse:

- **Dedup across nodes disappears.** Two users with the same 2 GB video pay twice, and
  pay for two transcriptions rather than reusing one by hash. Acceptable for a
  two-person instance; worth knowing it was a real property being spent.
- **Writes stop being idempotent.** `SaveAsync` currently makes a duplicate upload a
  no-op by construction. Path-addressed writes need real conflict handling.
- **Path traversal becomes a live concern.** `PathFor`'s 64-hex-character validation made
  escaping the root structurally impossible. User-controlled names and symlinks are a new
  attack surface that has to be closed deliberately (see the invariants under Sharing).
- **The tree stops being one directory.** A node shared from Alice to Bob appears in
  Bob's tree while living in Alice's directory, so the rendered tree is a union of "my
  root" and "shared with me" — which `Node.ParentId` and `GetTreeAsync` cannot express
  today.
- **Filesystem naming rules reach the user**, mitigated but not erased by title overrides.

## Standing rules this amends

`AGENT.md`'s "Rules that don't bend" describes the current architecture and will need
updating when this lands:

- *"every node's content is a file version in content-addressed storage"* → content is a
  file at a path; the CAS holds history.
- *"Two trees, and only two"* → still two, but the node tree is now the directory tree,
  and it is a union view per user rather than a single global tree.
- *"Don't add interfaces without a stated second implementation"* → `IFileStorage` is
  replaced rather than joined; the second implementation is the point.

## Plan

Each stage is shippable on its own and ordered so the payoff arrives before the risk.

1. **Path-shaped storage.** Replace `IFileStorage` with a path-addressed seam; user roots;
   the CAS moves to `.gatherum/versions/`. No user-visible change — the test is that the
   existing suite passes with bytes living at readable paths.
2. **Metadata sidecar + reindex.** Frontmatter and `meta.json`, filename-as-title with
   overrides, ids on disk, and `gatherum reindex` rebuilding the database from a cold scan.
   **The original motivation is retired here**: at the end of this stage, losing the
   database costs nothing but recomputation.
3. **Sharing.** Three states, grants with roles, additive inheritance, the two authority
   invariants, the anonymous switch, and the visibility-preserving migration.
4. **Union tree.** "Shared with me" as a first-class part of the tree, in the UI, the API,
   and MCP.
5. **External change watching.** Watcher, startup reconciliation, rename detection,
   self-write suppression.

Stages 1–2 are the ones that matter most and carry the least risk. Stage 5 is where the
bugs live.
