# Shared lists

A list people answer against — a set of sprites, the volumes of a series, the peaks in a
range, the nights a group can play — where everyone sees everyone else's answers, and
each answer stays its owner's to give.

Underneath there is one thing: **a row per question, a column per person, a mark where
that person says yes.** "Who has which sprite" and "who can make which night" differ in
the noun and in nothing else, so they are one construct with a small vocabulary — the
word the fence opens with says what an answer *means*, and decides nothing else.

| Written | Asks | An answer says | Answers each | Names who |
| --- | --- | --- | --- | --- |
| `:::collection` | who has each row | *has it* | as many as you like | yes |
| `:::availability` | who can make each row | *can make it* | as many as you like | yes |
| `:::poll` | who picked each row | *picked this* | one | no |

Everything below is written with `:::collection`; swap the word and it all still holds.

## Two documents, not one

Shared checklists are awkward everywhere because they conflate two things with completely
different tempos, authors, and privacy needs.

| | **The catalog** | **The tally** |
| --- | --- | --- |
| What it says | what exists to collect | what *I* have |
| Who writes it | one author, occasionally | each participant, constantly |
| Who should see it | everybody it is shared with | everybody the catalog is shared with |

One shared set of checkboxes cannot answer both: if you check "Sonic", has anyone else got
it? The checkbox has nowhere to put the answer.

So a shared list is two kinds of page. The **catalog** is a page with a
`:::collection` fence on it, naming the list. A **tally** is a page per person with a
fence tracking that catalog. Both are ordinary nodes, so both already have versions,
search, categories, backlinks and their own sharing — nothing here is a new kind of
thing.

## Writing a catalog

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

**Counts are of answerable things, not lines.** The list above is six things across four
lines, and every number in the interface says six.

## Answering

Anyone signed in who can see the catalog can answer against it. The first answer writes
their tally into being — a page called after the catalog, under a `Lists` folder
in their own root — and every later answer rewrites it.

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

- **A row with variants is a group and cannot be answered** — only its variants can.
  "Give me all three" is a different statement from the three answers it would stand in
  for, and the one thing this must not do is guess what you have.
- **Anything after an em dash is a note** — `— Gold, Sprite Day 2` — and it is yours,
  kept through every later answer.
- **You only ever write your own tally.** Nobody's answer touches anybody else's file.

## Who sees whose column

**The list's audience is the grid's audience.** Whoever may read the catalog sees every
column on it (a poll excepted — see below) — so answering is joining in, and there is nothing to share to make your
column count. Share the catalog with your group and their answers appear in it; publish
it and a public list is public, names and all.

That is deliberately not the same as publishing your tally. Its own sharing is untouched
and still governs the **page**: whether it opens at its own URL, whether it shows in
anybody's tree, whether search finds it. So a tally stays yours as a file — private
unless you say otherwise, like any other new node — while the answers on it count in the
list they were made against. What the grid shows of somebody else is exactly the rows
they answered and the name they answer under; the notes in their file, and any answers of
theirs the catalog has since orphaned, are their own business.

If you do not want your answers seen by the list's readers, do not answer — or delete your
tally. There is no half-in.

A signed-out visitor to a public list reads it and has no checkbox at all. In a grid
where every other column is a real person's real answers, a control that recorded nothing
but this browser would look exactly like the ones that count, and it would be lying.

## When the catalog changes

Catalogs are living documents: items get added, corrected and renamed while people are
answering against them.

- **An item that gains a page keeps its answers.** Matching falls back from id to text, so
  promoting `Sonic` to `[Sonic](node://…)` costs nobody anything.
- **A rename orphans the answers it stranded, and says so.** Whoever renamed the item
  cannot rewrite anybody else's tally — it is their file — so the answers simply stop
  matching. The grid reports them, and they stay in the file: put the old wording back
  and they count again.

## Several lists

A page can declare more than one; each is named by its fence and tracked separately. A
tally says which after the link:

```markdown
:::collection [[Season 4]] Sprites
```

The name is what identifies the list, so renaming the page it lives on orphans nothing.

A tally naming its catalog with `[[Title]]` resolves by title when you save the page,
and a title is a search — so that spelling cannot find an **unlisted** catalog, and a
tally written that way would track nothing. A `node://` mention can, because an id is
permission and a title is a search. Answering writes the mention spelling for that reason.

## Ordinary checklists are untouched

`- [ ]` outside a `:::collection` fence means what it has always meant: *this is done* —
one answer, shared by everybody, which is the commoner kind of checklist in a knowledge
base. A release checklist does not become twenty private opinions because somebody
elsewhere in the wiki is collecting sprites.

## Asking a different question

```markdown
:::availability Game nights
- Fri Oct 3
- Fri Oct 10
- Fri Oct 17 — after the con
:::
```

```markdown
:::poll Where for dinner?
- Thai
- Pizza
- Sushi
:::
```

Everything on this page applies unchanged: each person's answer is a page of their own,
rows can nest, an entry can link a page, and a renamed row orphans the answers it stranded
and says so.

Three things the word changes beyond the wording.

**A poll is one answer each.** Picking a row takes back the last one, so the control is a
radio rather than a checkbox and the file itself never says somebody picked two — that is
enforced where a tally is written, not where a grid is drawn, because the file is what
everybody else reads. You can still withdraw an answer and pick nothing.

**A poll reports how many, never who.** A roster and a schedule are asked *of* people —
"who can make Friday" has no useful answer without the who — but a poll is asked of a
group and answered by individuals, and being seen to change your mind in front of everyone
is a different act from voting. So a poll shows the totals and your own answer, and no
column for anybody else. That is decided where the answer is built, not where the grid is
drawn: a name withheld in the markup while the response still carries it is not withheld
at all. The totals are still of everybody who answered.

A poll is *not* a secret ballot in the strong sense, and should not be used as one. Every
answer is still a file its owner can share, an admin can read the disk, and the fence's
word can be edited to `:::collection`, which shows the columns that were there all along.
It hides who from the people reading the list, which is the ordinary courtesy a poll
wants, and nothing stronger.

**Availability and polls show a row's own total**, because "how many can make Friday" and
"how many picked Thai" are what those grids are read for. A collection list spends that width
on rows instead: how many people have Sonic is a curiosity beside how many you still need.

## For agents

Two MCP tools, and the same over REST:

| | |
| --- | --- |
| `get_list` | the list's rows, and every tally's answers against them |
| `answer_list` | record or take back one answer on your own tally |
| `GET /api/nodes/{id}/list` | the same, unauthenticated for a public list |
| `POST /api/nodes/{id}/list` | `{ key, answered, name? }` |

Ask either of the catalog or of any tally that tracks it — both answer with the same
grid. Every row carries the `key` an answer names it by, so read the list before writing to
it.
