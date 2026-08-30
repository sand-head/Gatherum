# Collaborative collectible lists — a proposal

**Status: proposed, not built.** This is beyond `PLAN.md` scope, so it is written down for
the owner to accept, amend, or throw out rather than implemented. If it is accepted it
earns a `DECISIONS.md` entry and this file becomes the design behind it, the way
`FILESYSTEM.md` sits behind storage.

## First, a correction to the premise

Editing is **not** live-collaborative. TipTap and Yjs were removed along with npm, esbuild
and YDotNet when the editor became slopedit; live collaboration was knowingly downgraded
to presence plus optimistic versioning (DECISIONS.md, "Live collaboration downgraded to
presence + versions"). What exists today is:

- heartbeat presence — "Sam is editing" — expiring 15 seconds after the last beat,
- a newer-version warning when someone else saves the document you have open,
- saves serialized per node, last-writer-wins, with the loser's save surviving as its own
  version so two authors never collapse into one.

So "collaborative" in Gatherum means *two people can edit the same page without losing each
other's work*. It does not mean shared cursors or character-level merging, and a feature
that assumes a CRDT underneath would be building on something that isn't there.

That turns out not to matter, because a collection list does not want co-editing. It wants
something the current model is actually better at.

## The insight: a collection list is two documents, not one

The reason shared checklists are awkward everywhere is that they conflate two things with
completely different tempos, authors, and privacy needs:

| | **The catalogue** | **The tally** |
| --- | --- | --- |
| What it says | what exists to collect | what *I* have |
| Who writes it | one author, occasionally | each participant, constantly |
| Who should see it | everybody it's shared with | its owner, plus whoever they choose |
| Edited how often | weekly | daily |

One shared set of checkboxes cannot answer both. If Alice ticks "Sonic", has Bob got it?
The checkbox has nowhere to put the answer.

Split them and both halves get easy. The catalogue is **a page** — and therefore already
has versions, search, categories, backlinks, MCP tools, and a manual entry. The tally is
**a page per person** — and therefore already has its own `AccessMode`, set by its owner
and nobody else.

Nothing here needs a new kind of thing. That is the test this design is trying to pass.

## The worked example, and why it's instructive

Fortnite Chapter 7 Season 4 "Override" launched with 11 base sprites — Sonic, Tails,
Shadow, Jackrabbit, Klombo, 8-Bit, Crown, Adventure, Bush, Jonesy, Killswitch — with Storm
Scout held back for a later Sprite Day, each sprite carrying Base, Gold and Cheat Master
variants, and five community "Design-a-Sprite" winners (Bullet, Dumpster Dive, Honey, Pond,
X-Ray) arriving across the season.

Two things about that roster matter more than the roster:

1. **Sources disagree right now.** One says 11 base sprites and that Gold isn't in the game
   yet; another says 12 base and describes Gold's effect. Counts quoted range over 22, 33,
   36, 51 and a leaked 112.
2. **It grows on a schedule.** New sprites on Thursdays, new variant access on Mondays,
   community winners staggered through the season.

So the catalogue is a *living document* that will be corrected and extended dozens of times
while people are ticking against it. That makes the interesting problem not the checkbox.
It is:

## Item identity is the interesting problem

A tick has to name an item, and what it names decides whether a season's collecting
survives an edit to the catalogue.

- **By line number** — a Sprite Day inserts a row and every tick below it shifts. Fails on
  the most common edit there is.
- **By item text** — survives insertion and reordering, breaks on a rename.
- **By node id** — survives everything, because outliving a rename is what node identity
  is *for* (`FILESYSTEM.md`, "Identity and links").

The first draft of this design concluded that only the third is sound, and therefore that
every collectible must be its own page. That was wrong in the way that matters: it made the
expensive option mandatory. Wanting to tick off forty sprites is not wanting to write forty
pages about sprites, and a design that demands the second to deliver the first will not get
used.

So: **an item is a line of text, and it may optionally link a node.**

```markdown
- Sonic
- Tails
- [Klombo](node://0193…)      ← this one has a page: a picture, what it does, backlinks
- Storm Scout
```

Identity follows from what the line has. A linked item is keyed by its node id and is
immune to renaming. A plain item is keyed by its normalized text and survives everything
except a rename. You pay for stability only where you wanted a page anyway.

**Promotion has to be lossless**, or nobody will ever do it: when an item gains a link,
matching falls back from id to text, so ticks made against `Sonic` keep counting once
`Sonic` becomes `[Sonic](node://…)`. One rule, and it makes the optional half safe to opt
into later.

**A rename orphans the plain items, and that is surfaced rather than hidden.** Alice cannot
rewrite Bob's tally to follow her rename — it is his file, under his root — so the ticks
simply stop matching. The grid says so: *"3 ticks no longer match an item"*, with the
orphans listed and a click to re-point them. Silence is what would be unacceptable here,
not the mismatch.

That also settles what the catalogue *is*: **a page with a list on it**, not a category of
pages. It gets deliberate ordering (a category has none), prose around the list, and one
node to share instead of fifty. Pages for individual collectibles remain exactly what they
should be — an upgrade for the handful you care about, filed in whatever category you like.

## Where a tally lives

**Ownership is the path**, so a tally lives under its owner's root:

```
alice/Collections/Override sprites.md      ← Alice's tally, her node, her access
bob/Collections/Override sprites.md        ← Bob's, entirely separately
```

It is a node like any other, and it carries its own `AccessMode` — which is the call made
when this was proposed. Alice sets hers `Public` and her column shows to everyone; Bob
leaves his `Private` and collects alone. Nobody's sharing gesture publishes anybody else's
tally, which keeps "only an owner sets access" intact. Setting the *catalogue* public
publishes the catalogue and nothing else.

**A tally is content, not ephemera.** It is tempting to add a `NodeTicks` table and be done
in an afternoon, and that would be wrong: `ReadingPositions` earns its database-only
exception because losing one costs exactly a page number, and a season of collecting is not
that. A tally must be a file under the storage root — rebuildable by `Reindexer`, carried
by the backup people are told to take, readable when Gatherum isn't running.

### The format: no new format

The tally's body is a task list, mirroring the catalogue's lines:

```markdown
Tracking [[Override sprites]].

- [x] Sonic — Gold, Sprite Day 2
- [ ] Tails
- [x] [Klombo](node://0193…)
```

Every word of that is vocabulary the dialect already has — task lists, wiki links and
`node://` mentions are all documented in `markdown.md` — which buys the feature its
properties for free, exactly the way citations turned out to need no new construct at all
(DECISIONS.md, "A citation is a convention, not a construct"). The tally opens in the
ordinary editor. Its first line makes the `NodeLink` row that the aggregate finds it by. A
trailing note after an item is prose, so "Gold, Sprite Day 2" costs nothing to support. And
a tally read by a human with no Gatherum running is still obviously a checklist.

The only genuinely new code is a reader that pulls `(item → checked)` out of a task list,
which belongs in `Markdown/` in Core beside `WikiLinkSyntax`.

## How the catalogue shows everyone's columns

A tally names its catalogue on its first line, so **every tally is a backlink of the
catalogue**. The whole read path is two calls that already exist, plus a parse:

```
NodeService.GetBacklinksAsync(userId, catalogueId)   → the candidate tallies
FileService … read the catalogue body                → the rows, in the author's order
```

`GetBacklinksAsync` already filters through `INodeAuthorizer.VisibleTo`, so the visibility
rule is spelled exactly once, where it always was. Parse each candidate for `- [x]` items,
match them to rows by id or text, and that is the grid.

Note which door it is: a column in an aggregate is *enumeration*, so `VisibleTo`/`Listed`,
never `CanSee`/`WithLink`. Unlisted is precisely the case where answering one with the other
leaks — an unlisted tally must not appear in a column just because its id was reachable.

One wrinkle worth writing down: `[[Wiki link]]` resolves by *title*, which is the
enumeration question, so a tally cannot name an unlisted catalogue that way. Naming it with
a `node://` mention instead works, because an id is permission. Unlisted catalogues are
therefore fine; they just want the other spelling.

### What makes a page a tally

Nothing marks one. A tally is recognized *structurally*: a page that links the catalogue and
carries task items matching its rows. The alternative was a flag on `Node` beside
`IsCategory`, and a new domain concept is too much to pay to prevent the only failure this
has — a page that discusses the list using example checkboxes gets counted as somebody's
column.

Two things make that acceptable. A column appears only because its owner deliberately made
that page visible, which is never accidental. And the catalogue owner can see exactly whose
columns show.

No new relation, no new table, no new visibility rule, no new sidecar.

## The anonymous half of the ask

The rule this meets is `AGENT.md`'s: *an API endpoint is authenticated unless it says
`.AllowAnonymous()`, and no write ever does.* Underneath it sits a structural fact that is
easier to miss and matters more: **ownership is the path, and a signed-out visitor has no
root.** Their file has nowhere on disk to live without inventing an owner for it.

So this comes in two tiers, and the first one is most of the value.

### Ticking stores nothing

A signed-out visitor to a public catalogue ticks whatever they like, their ticks live in
their own browser, and they see their own column plus every shared column. This is exactly
how a signed-out reader's place in a book is already kept — written as the fallback the
server never is, read only when the server had nothing — so it needs no new concept, no
storage, no rate limit, and no spam story.

For "let me check off sprites on someone's public list", this is the whole feature. It
should ship first and by itself.

### Publishing your column is a second, deliberate act

The part localStorage cannot do is show your progress to everybody else. Make that an
explicit "share my list", which mints a guest tally under the **catalogue owner's** root:

```
alice/Collections/Override sprites.guests/<slug>.md
```

Alice owns those files because they sit in her tree — she is hosting a guestbook. They are
in her backup, count against her storage, and are hers to delete. Ownership-is-the-path
stays literally true and nobody had to invent a rootless user.

Because promotion is deliberate and rate-limited, a drive-by visitor creates nothing: the
ambient spam surface for the common case is zero.

**The write is authorized; it just carries no identity.** Mint a hashed capability token
scoped to that one node — `ApiKeys` narrowed to a single node rather than a new
authentication concept, and `ApiKeys` is already a table-only exception, so the shape is
precedented. The rule then reads *no write is ever unauthenticated*, where a capability
carrying no identity still counts. That is a real amendment to a rule that doesn't bend and
belongs in `DECISIONS.md` as one.

**Do not reuse the node id as the secret.** `Unlisted` makes a node's id the secret for
*reads*, and it cannot be reused here: a guest tally links to the catalogue, and the
aggregate column works by enumerating exactly those links — so the feature that displays
everyone's progress would hand out every guest's write key with it. Separate token, hashed
at rest, shown once.

**Controls that ship with it, not after:** off by default per catalogue plus an instance
switch beside `Sharing.AllowPublic`; caps on guest tallies per catalogue and bytes per
tally; the anonymous limiter, which partitions on client address and behind a proxy means
`X-Forwarded-For` trusted from any peer — the loopback bind is what stops spoofing, so this
is another reason not to publish the port wider; guest display names treated as untrusted
text from the open internet, length-capped and removable by the owner; and a plain warning
at mint time that losing the token loses the list.

## Sharing a catalogue with everyone who can sign in

Signing in is gated on an Authelia group, so "everyone with an account here" is already the
set the owner means when they share a list with their people. But the access modes go from
`Shared` — which names people one at a time — straight to `Unlisted` and `Public`, which
mean the open internet. There is no mode for *anyone who got past the front door*.

With more than a handful of participants that gap is the thing that will actually hurt: a
twenty-person catalogue means twenty grants, or publishing it to the internet.

The fix is a reach between the two, and it is worth flagging that it does **not** slot
neatly into the existing ordered scale. `NodeReach` is ordered because inheritance is a
maximum and the two questions are comparisons — but "any signed-in user" and "anyone holding
the link" are not comparable: the second includes strangers, the first excludes them. So it
is either a second axis or a deliberate placement, and either way it is a change to the one
type every visibility query filters on. Not free, not huge, and probably the next thing to
decide after this feature's shape.

## What group scale breaks that two people hid

None of this blocks the work above, but several accepted trade-offs are written into the
docs with "two people" as their justification, and that premise has changed:

1. **Semantic search starves.** Visibility is filtered *after* the HNSW index picks its
   neighbours, and `STATUS.md` is explicit that over-fetching makes starvation "unlikely at
   two people's scale; it is not a proof." Twenty people with twenty private subtrees is
   where that stops being unlikely. The one I would fix first.
2. **Presence is in-process**, and documents itself as enough "for a single-instance
   deployment."
3. **Save gates are in-process semaphores** — `STATUS.md` already names a database-level
   lock as the prerequisite for a second app instance.
4. **Signed-in callers are never rate-limited** (`AnonymousRateLimits`), which is a sound
   call for two authenticated people and gives a leaked API key no ceiling at all.
5. **File bytes are never garbage-collected** when nodes are deleted (`STATUS.md`). Storage
   growth now scales with the number of people.

## What it would touch

- **Core** — a `CollectionService` (catalogue rows + visible tallies, matched and fused),
  and task-item reading in `Markdown/`. All the business rules here, per the brief.
- **Client** — one island rendering the grid, ticking through `IAppData` → `SaveTextAsync`
  on the reader's own tally; `localStorage` for the signed-out case.
- **Web** — a REST endpoint, an MCP tool or two (`collection_status`, `mark_collected`) so
  an agent can tick, and a page in `src/Gatherum.Web/Docs` — the manual is part of the
  feature, not a follow-up.
- **Tests** — service tests against real Postgres for aggregation and visibility (including
  the unlisted-tally case), and round-trip tests for tally parsing.

## What not to do

- No `NodeTicks` table. A tally is content and lives on disk.
- No second taxonomy verb, and no taxonomy at all in the list machinery. A catalogue is a
  page with a list on it; categories file it exactly as they file anything else.
- No page required per item. Pages are the optional half, for the items worth writing about.
- No new abstraction seam — nothing here has a second implementation.
- No anonymous write endpoint, in any disguise.
- No new Markdown construct. Task lists and mentions already say all of it.

## Open questions for the owner

1. **Does a catalogue need an "everyone who can sign in" reach?** This is the one that
   changes the order of work rather than the design. `Shared` means naming twenty people;
   `Public` means the open internet; there is nothing in between, and once the front door
   is an OIDC group that gap is what a group-sized instance feels first. It is also the
   only part of this that touches `NodeReach`, the type every visibility query filters on.
2. **Does a tick want structure** — date acquired, variant, a note — or is trailing prose
   after the item enough? Prose is free; structure is a format.
3. **Do guest tallies appear in the aggregate immediately, or after the owner approves
   each?** Approval is the strongest anti-spam answer and also the most work; it matters
   only if a catalogue is ever linked somewhere busy.

Settled since the first draft: items are text with optional node links rather than mandatory
pages; the catalogue is a page rather than a category; a tally is recognized structurally
rather than flagged.

## Sources for the worked example

- [All Fortnite Chapter 7 Season 4 Sprites at Launch – Complete List (Vice)](https://www.vice.com/en/article/fortnite-chapter-7-season-4-sprites-list/)
- [Fortnite Chapter 7 Season 4 'Override' Sprites (Sprite Checklist)](https://spritechecklist.net/season-4)
- [Fortnite Chapter 7 Season 4 Will Have 112 Sprites and 7 Variants, Leak Claims (Vice)](https://www.vice.com/en/article/fortnite-chapter-7-season-4-sprites-total-variants-leak/)
- [All Sprite Locations in Fortnite Override Chapter 7 Season 4 (Game Rant)](https://gamerant.com/fortnite-override-ch7-s4-all-sprites-effects-locations-how-to-get/)
