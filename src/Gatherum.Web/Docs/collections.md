# Collectible lists

A shared list people tick against — a set of sprites, the volumes of a series, the peaks
in a range — where everyone sees what everyone else has, and each answer stays its
owner's to give.

## Two documents, not one

Shared checklists are awkward everywhere because they conflate two things with completely
different tempos, authors, and privacy needs.

| | **The catalogue** | **The tally** |
| --- | --- | --- |
| What it says | what exists to collect | what *I* have |
| Who writes it | one author, occasionally | each participant, constantly |
| Who should see it | everybody it is shared with | its owner, plus whoever they choose |

One shared set of checkboxes cannot answer both: if you tick "Sonic", has anyone else got
it? The checkbox has nowhere to put the answer.

So a collection is two kinds of page. The **catalogue** is a page with a
`:::collection` fence on it, naming the list. A **tally** is a page per person with a
fence tracking that catalogue. Both are ordinary nodes, so both already have versions,
search, categories, backlinks and their own sharing — nothing here is a new kind of
thing.

## Writing a catalogue

```markdown
Sprites arrive on Thursdays.

:::collection Override sprites
- Sonic
  - Base
  - Gold
  - Cheat Master
- [Klombo](node://8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f)
- Storm Scout
  - Base
:::
```

Reading that page draws the grid. Editing it shows the list itself on a card, which is
what you want while rearranging a roster rather than a live table of other people's data
laid over the thing you are editing.

**An item is a line of text.** Giving it a page is optional, and worth it for the few
items you actually want to write about — a picture, what it does, its own backlinks.
`[Klombo](node://…)` above is one of those; the rest are just words.

**Variants are nested items**, one level deep, and optional per item. Rosters are ragged:
a sprite held back for a later event has only its base form, and a fence-wide declaration
of "every item has three variants" would be a lie about the list from the first week. So
Storm Scout lists the one variant it has, and Klombo lists none.

**Counts are of collectibles, not lines.** The list above is six things across four
lines, and every number in the interface says six.

## Ticking

Anyone signed in who can see the catalogue can tick against it. The first tick writes
their tally into being — a page called after the catalogue, under a `Collections` folder
in their own root — and every later tick rewrites it.

That page is a file like any other. You can open it, read it, edit it by hand, and find
it in a backup:

```markdown
:::collection [Override sprites](node://0193f0c2-1b7a-7d31-9a44-2f5d1e6c8a90)
- [x] Sonic — Gold, Sprite Day 2
  - [x] Base
  - [x] Gold
  - [ ] Cheat Master
- [ ] Storm Scout
  - [ ] Base
:::
```

- **A row with variants is a group and cannot be ticked** — only its variants can.
  "Give me all three" is a different statement from the three ticks it would stand in
  for, and the one thing this must not do is guess what you have.
- **Anything after an em dash is a note** — `— Gold, Sprite Day 2` — and it is yours,
  kept through every later tick.
- **You only ever write your own tally.** Nobody's tick touches anybody else's file.

## Who sees whose column

A tally carries its own sharing, set by its owner and nobody else — so a new tally is
**private**, and its column shows to nobody until its owner says otherwise. Sharing the
catalogue publishes the catalogue and nothing else.

The columns in a grid are the tallies you may *enumerate*. That is the listing question,
not the reaching one, so an **unlisted** tally never appears in a column, whoever holds
its link. See [sharing](/docs/sharing) for what the modes mean.

A signed-out visitor to a public list reads it and has no checkbox at all. In a grid
where every other column is a real person's real ticks, a control that recorded nothing
but this browser would look exactly like the ones that count, and it would be lying.

## When the catalogue changes

Catalogues are living documents: items get added, corrected and renamed while people are
ticking against them.

- **An item that gains a page keeps its ticks.** Matching falls back from id to text, so
  promoting `Sonic` to `[Sonic](node://…)` costs nobody anything.
- **A rename orphans the ticks it stranded, and says so.** Whoever renamed the item
  cannot rewrite anybody else's tally — it is their file — so the ticks simply stop
  matching. The grid reports them, and they stay in the file: put the old wording back
  and they count again.

## Several lists

A page can declare more than one; each is named by its fence and tracked separately. A
tally says which after the link:

```markdown
:::collection [[Season 4]] Sprites
```

The name is what identifies the list, so renaming the page it lives on orphans nothing.

A tally naming its catalogue with `[[Title]]` resolves by title, which is a search — so
that spelling cannot reach an **unlisted** catalogue. A `node://` mention can, because an
id is permission and a title is a search. Ticking writes the mention spelling for that
reason.

## Ordinary checklists are untouched

`- [ ]` outside a `:::collection` fence means what it has always meant: *this is done* —
one answer, shared by everybody, which is the commoner kind of checklist in a knowledge
base. A release checklist does not become twenty private opinions because somebody
elsewhere in the wiki is collecting sprites.

## For agents

Two MCP tools, and the same over REST:

| | |
| --- | --- |
| `collection_status` | the list's rows, and every visible tally's ticks |
| `mark_collected` | record or take back one collectible on your own tally |
| `GET /api/nodes/{id}/collection` | the same, unauthenticated for a public list |
| `POST /api/nodes/{id}/collection` | `{ key, collected, list? }` |

Ask either of the catalogue or of any tally that tracks it — both answer with the same
grid. Every row carries the `key` a tick names it by, so read the list before writing to
it.
