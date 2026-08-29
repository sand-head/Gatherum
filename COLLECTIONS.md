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

## Item identity is the whole design problem

A tick has to name an item. What it names decides whether a season's collecting survives an
edit to the catalogue.

- **By line number** — a Sprite Day inserts a row and every tick below it shifts. Fails on
  the most common edit.
- **By item text** — a rename or a typo fix orphans everyone's tick. Given that sources
  already disagree on names, renames are certain.
- **By a stable key** — survives both, but the key has to live somewhere, and Gatherum has
  nowhere to put per-item structured data today: frontmatter is designed in `FILESYSTEM.md`
  and not built, and `.gatherum/meta.json` is keyed per *file*, not per line inside one.

Which points at the answer the architecture already has:

> **Each collectible is a node, and the catalogue is a category.**

A node id is stable across rename, move, and re-nesting — that is what node identity is
*for* (`FILESYSTEM.md`, "Identity and links"). Make each sprite a page and the tick names
an id, so correcting "Killswitch" to "Kill Switch" breaks nothing.

And it needs no new taxonomy: a category is already a page, `NodeCategory` is already the
only membership relation, categories already nest (so "Override sprites" files under
"Fortnite"), `browse_category` and `list_categories` already exist as MCP tools, and the
whole ancestry already goes into each member's search text. A collectible page can carry
its picture and what the sprite does, which is most of why anyone wants a collectible list
in the first place.

The cost is honest: a full season is ~17 sprites, and if each variant is its own collectible
that's ~51 nodes rather than 51 lines. I'd still take it for v1, because the alternative is
inventing a per-item metadata carrier that frontmatter is supposed to become, and because
"a Gold Sonic is a different collectible from a Sonic" is simply true.

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

The tally's body is a Markdown task list of mentions:

```markdown
- [x] [Sonic](node://0193…) — Gold, Sprite Day 2
- [x] [Tails](node://0194…)
- [ ] [Storm Scout](node://0195…)
```

Every word of that is vocabulary the dialect already has — task lists and `node://`
mentions are both documented in `markdown.md` — which buys the feature its properties for
free, exactly the way citations turned out to need no new construct at all (DECISIONS.md,
"A citation is a convention, not a construct"). The tally opens in the ordinary editor.
Its mentions make real `NodeLink` rows. A trailing note after the item is prose, so
"Gold, Sprite Day 2" costs nothing to support. And a tally read by a human with no Gatherum
running is still obviously a checklist.

The only genuinely new code is a reader that pulls `(node id → checked)` out of a task
list, which belongs in `Markdown/` in Core beside `WikiLinkSyntax`.

## How the catalogue shows everyone's columns

Because tally items are mentions, the tallies for a catalogue are **already** its backlinks.
The aggregate is:

1. take the catalogue's members (`NodeCategory`),
2. take the nodes linking to them that are tallies,
3. filter through `INodeAuthorizer.VisibleTo` — the only door for visibility, never a
   second spelling of the rule in a query,
4. parse each for ticks and fuse.

Note which door: an aggregate column is *enumeration*, so it is `VisibleTo`/`Listed`, not
`CanSee`/`WithLink`. Unlisted is precisely the case where answering one with the other
leaks — an unlisted tally must not appear in a column just because its id was reachable.

No new relation, no new visibility rule, no new sidecar.

## The anonymous half of the ask

Anonymous participation is designed in [GROUPS.md](GROUPS.md), because it changes the
access model rather than the list feature. The short version, and the part that matters
here:

**Ticking without signing in is free, and stores nothing.** A signed-out visitor to a
public catalogue ticks whatever they like, their ticks live in their own browser, and they
see their own column plus every shared column. This is exactly how a signed-out reader's
place in a book is already kept, so it needs no new concept and no spam story. For "let me
check off sprites on someone's public list", this alone is the entire feature, and it
should ship first and by itself.

**Showing your column to everyone else is a second, deliberate act.** The obstacle is not
really the "no anonymous writes" rule — it is that ownership is the path and a visitor has
no root, so their file has nowhere to live. The answer is that it lives under the
*catalogue owner's* root, in a `.guests` directory beside the catalogue: Alice is hosting a
guestbook, she owns those files, and they are hers to delete. The write is authorized by a
hashed capability token scoped to that one node — `ApiKeys` narrowed, not a new
authentication concept — and pointedly **not** by the node id, since the aggregate works by
enumerating exactly those links and would otherwise publish every guest's write key.

That last part is a real amendment to a rule that doesn't bend, with caps, rate limits and
an off-by-default switch attached. GROUPS.md spells out all of it.

**Group-shared catalogues.** With OIDC group grants (also GROUPS.md), a catalogue shared
with `collectors` shows the columns of everyone in `collectors` who published a tally,
without naming them one at a time. That is what makes this feature work for a group rather
than for a pair, and it needs no change to anything above: the aggregate is still backlinks
filtered through `VisibleTo`.

## What it would touch

- **Core** — a `CollectionService` (catalogue members + visible tallies, fused), and task-item
  reading in `Markdown/`. All the business rules here, per the brief.
- **Client** — one island rendering the grid, ticking through `IAppData` → `SaveTextAsync`
  on the reader's own tally; `localStorage` for the signed-out case.
- **Web** — a REST endpoint, an MCP tool or two (`collection_status`, `mark_collected`) so
  an agent can tick, and a page in `src/Gatherum.Web/Docs` — the manual is part of the
  feature, not a follow-up.
- **Tests** — service tests against real Postgres for aggregation and visibility (including
  the unlisted-tally case), and round-trip tests for tally parsing.

## What not to do

- No `NodeTicks` table. A tally is content and lives on disk.
- No second taxonomy verb. The catalogue is a category; membership is `NodeCategory`.
- No new abstraction seam — nothing here has a second implementation.
- No anonymous write endpoint, in any disguise.
- No new Markdown construct. Task lists and mentions already say all of it.

## Open questions for the owner

1. **Variants as nodes?** ~51 nodes a season, versus waiting for frontmatter to carry a
   variant list per item. I lean nodes, and I'd like a second opinion.
2. **Is the catalogue a category or a plain page with a list?** A category gets nesting,
   browse, and search-text inheritance; a page gets deliberate ordering and prose around
   the list. They can coexist, but v1 should pick one.
3. **Does a tick want structure** — date acquired, variant, a note — or is trailing prose
   after the item enough? Prose is free; structure is a format.
4. **Is there an import path?** With a roster that grows weekly, "paste a list, make the
   nodes" may matter more than the grid does.

## Sources for the worked example

- [All Fortnite Chapter 7 Season 4 Sprites at Launch – Complete List (Vice)](https://www.vice.com/en/article/fortnite-chapter-7-season-4-sprites-list/)
- [Fortnite Chapter 7 Season 4 'Override' Sprites (Sprite Checklist)](https://spritechecklist.net/season-4)
- [Fortnite Chapter 7 Season 4 Will Have 112 Sprites and 7 Variants, Leak Claims (Vice)](https://www.vice.com/en/article/fortnite-chapter-7-season-4-sprites-total-variants-leak/)
- [All Sprite Locations in Fortnite Override Chapter 7 Season 4 (Game Rant)](https://gamerant.com/fortnite-override-ch7-s4-all-sprites-effects-locations-how-to-get/)
