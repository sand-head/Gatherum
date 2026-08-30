# Markdown in Gatherum

A page is a Markdown file. Not a database row that exports to Markdown — the file on
disk *is* the page, and everything below is written into it in plain text. Open the
storage directory with `cat` and you see exactly what the editor sees.

Most of what a page contains is ordinary GitHub-flavored Markdown. On top of that
Gatherum speaks five constructs of its own: **mentions**, **wiki links**, **embedded
files**, **asides** (`:::infobox`, `:::figure`) and **callouts** (`> [!NOTE]`). None of
them are invented syntax for their own sake — each is either an existing convention
(GitHub's alerts, MediaWiki's brackets, the directive fence) or a plain Markdown link
with a scheme of ours.

> A reader that has never heard of any of this still reads the file. A wiki link shows
> as `[[Homelab]]`, an aside shows as the two `:::` lines with ordinary Markdown between
> them, a callout shows as the block quote it is made of.

## The round trip

Gatherum's editor reads Markdown into a document model and writes it back out. That trip
is lossless on purpose: **a page opened and saved without being edited comes back
byte-identical.** Two exceptions, both small and both deliberate:

- A pipe table written without its `| --- |` delimiter row gets one, because Markdown
  wants one — and a delimiter cell spelled `:---` (explicit left, which is also the
  default) comes back as the plain `---`. From that point on the file is stable.
- Bold *inside* a callout's title is absorbed by the title's own bold.

A literal `[` in prose is written back escaped (`\[`), as any Markdown writer does.

Everything else — the side and width of an aside, the exact spelling of a callout's
kind, whether a wiki link carried a label — is the file's word and comes back unchanged.

## Ordinary Markdown

These are the constructs the editor models. Anything here can appear anywhere a page
can, including inside an aside or a callout.

| Construct | Written |
| --- | --- |
| Headings | `# H1` through `###### H6` |
| Emphasis | `**bold**`, `*italic*`, `~~strikethrough~~`, `` `code` `` |
| Scripts | `x^2^` superscript, `H~2~O` subscript — Pandoc's spelling, no unescaped spaces inside; one tilde, two are strikethrough |
| Footnote | `[^key]` in prose, `[^key]: The note` on a line of its own — see [Footnotes](#footnotes) |
| Citation | A footnote whose note cites a bookmark's capture — see [Citations](#citations) |
| Links | `[label](https://example.org)` |
| Images | `![alt text](url)` on a line of its own |
| Captioned image | `![The caption](url){}` — a trailing `{…}` makes the bracket text a caption set under the picture; `{width=300 align=center}` sizes and places it — see [Figure](#figure) |
| Bulleted list | `- item`, nested by indentation |
| Numbered list | `1. item` |
| Task list | `- [ ] to do`, `- [x] done` |
| Block quote | `> quoted` |
| Fenced code | Three backticks, with the language after the opening ones for highlighting |
| Table | `\| Cell \| Cell \|` with a `\| --- \| --- \|` delimiter row; `\| :---: \|` centers its column and `\| ---: \|` rights it, every row at once |
| Horizontal rule | `---` on its own line |

Raw HTML is not part of the dialect. It is not stripped from the file, but nothing
renders it.

## Linking

Gatherum has three ways to point at another node, and they differ in what they survive.

### Mentions — link by id

```markdown
See [@Podman notes](node://8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f) for the quadlet.
```

An ordinary Markdown link whose URL is `node://<node-id>`. This is the strongest form:
it names the node itself, so it keeps working when the target is renamed or moved. The
editor renders it as an @-mention; **Link node…** in the editor's toolbar searches for a
node and writes one.

A `node://<id>` URL in a *file's description* counts too — descriptions are scanned for
mentions the same way page bodies are.

`node://` is how a mention is written and stored; reading a page turns it into a real link
to `/nodes/<id>`, so clicking, middle-clicking, "open in new tab" and "copy link address"
all do what they do anywhere else. A mention pointing at something you may not open is
drawn padlocked instead — see [Sharing](/docs/sharing).

### Wiki links — link by title

```markdown
The rack lives in the [[Homelab]].
Rebuilt after [[Homelab|the rewire]].
```

`[[Title]]` names a page by its title instead of its id. Resolution is
case-insensitive, and matches only nodes the person reading can see — an
[unlisted](/docs/sharing) node is never found by title.

`[[Target|label]]` links `Target` and shows `label`. Inside a table cell the pipe has to
be escaped — `[[Homelab\|the rack]]` — and the escaped form is what gets written back.

A title nothing answers to is not an error. It renders as a **red link**, and clicking
it offers to create that page — the invitation every wiki runs on.

The parser is deliberately narrow, so that brackets in prose stay brackets:

- A wiki link never spans a line break.
- Brackets inside decline the match: `[[a[b]]` is text.
- Both halves must survive trimming non-empty: `[[ | x]]` is text.
- Code spans and fenced code are skipped entirely. `` `[[Homelab]]` `` is a code span
  that says `[[Homelab]]`, not a link.
- A backslash escapes the next character, so `\[\[Homelab]]` is prose.

### Embedded files — show another node's bytes

```markdown
![The rack, before the rewire](/api/files/0f8f6e1a-.../content)
```

A file node's content by URL. Images render inline; the embed also counts as a link, so
the file gets a backlink from the page that shows it.

### External links

Anything else — `https://…`, `mailto:` — is an ordinary link and leaves the app when
clicked.

### What links buy you

Every link of the three kinds above is recorded when the page is saved, in both
directions. The target node's **backlinks** list the page, and "Similar" counts the
kinship. That is the whole reason to prefer a link over a bare title in prose.

## Asides — `:::infobox` and `:::figure`

An aside is a run of blocks that leaves the vertical flow: it sits at one margin as a
card, and the prose wraps past it. Two kinds, both written as a directive fence.

### Infobox

The encyclopedia's card — a heading and a column of label/value rows, at the top right
of an article.

```markdown
:::infobox
# Podman
| **Kind** | Container engine |
| **Runs** | Rootless, on the NAS |
| **Since** | 2024 |
:::
```

Rendered, that is the card floated at the right of this paragraph:

:::infobox
# Podman
| **Kind** | Container engine |
| --- | --- |
| **Runs** | Rootless, on the NAS |
| **Since** | 2024 |
:::

Inside an infobox, headings are centered and get a tinted band, tables lose their grid
(an infobox is a table without one), and images are centered. Bold labels are your word,
not Gatherum's: write `| **Kind** | … |` if you want them bold.

### Figure

A picture with a caption.

```markdown
:::figure left 360
![The homelab, before the rewire](/api/files/0f8f6e1a-.../content){align=center}
:::
```

The trailing `{…}` — even empty — makes the bracket text the **caption**: styled text
set under the picture, wrapped to its width, one unit with the image for selection and
deletion, so it can never drift away. It takes two optional attributes, in Pandoc's
spelling:

| Attribute | Meaning |
| --- | --- |
| `width=300` / `width=50%` | Display width, in pixels or as a share of the column it sits in. Always clamped to that column. |
| `align=center` / `align=right` | Where the picture sits when it is narrower than its column. |

This is ordinary image syntax, not figure syntax — a captioned, sized image works
anywhere in a page. Without the `{…}` the bracket text is plain alt text, exactly as
it always was, and the older spelling — a paragraph after the image is the caption —
still reads fine:

```markdown
:::figure left 360
![The rack](/api/files/0f8f6e1a-.../content)
The homelab, before the rewire
:::
```

### The fence's arguments

Both kinds take an optional side and an optional width, in either order, on the opening
line:

| Argument | Meaning |
| --- | --- |
| `left` / `right` | Which margin it floats to. Default `right`. |
| A number | Width in pixels. Default `280` for an infobox, `320` for a figure. |

Widths below `120` are ignored and widths above `640` are clamped to it. An argument
that is neither a side nor a number is kept in the file and otherwise ignored.

### The rules

- The closing line must be exactly `:::` (leading and trailing whitespace is fine).
- **An unterminated fence is not an aside.** If nothing closes it, the `:::infobox` line
  is just a line of text — no half-open card, no swallowed rest of the page.
- **Asides do not nest.** The outer fence owns every block inside it; a `:::figure`
  written inside an `:::infobox` degrades to the Markdown it is made of.
- Two asides in a row stay two asides, even when they are the same kind — but two with
  nothing between them but a blank line will overlap on the page, because a float is
  placed at its own position in the flow rather than clearing the one above it. Put some
  prose between them, or send one to each margin.
- The body is parsed by the ordinary parser with everything else still active, so a
  `[[wiki link]]` inside an infobox cell is a wiki link.

## Callouts — `> [!NOTE]`

GitHub's alert spelling: a block quote whose first line names a kind.

```markdown
> [!WARNING] Restores are not backups
> Old bytes survive a restore. A deleted node's do not.
```

The five kinds, which are the only five:

| Kind | Written | Reads as |
| --- | --- | --- |
| Note | `> [!NOTE]` | Worth knowing |
| Tip | `> [!TIP]` | Worth doing |
| Important | `> [!IMPORTANT]` | Don't skip this |
| Warning | `> [!WARNING]` | This can bite |
| Caution | `> [!CAUTION]` | This can hurt |

Each renders as a tinted card in its kind's accent, with the title line inked to match.

- The kind is matched case-insensitively; `> [!warning]` works.
- The title is optional. With none, the callout is titled with its kind's own name and
  is written back as the bare `> [!NOTE]` marker it came from.
- A title is ordinary Markdown: a link or a code span in one survives.
- Body lines carry their own `>`.
- **A second marker starts a second callout**, even with no blank line between them —
  two `> [!NOTE]` blocks in a row are two cards, not one quoting the other.
- **An unknown kind stays a plain quote.** `> [!nope] …` is a block quote that happens
  to start with a bracket, which is exactly what it looks like.

## Collectible lists — `:::collection`

A shared list people tick against, each keeping their own answer. Everything about it
is in [collections](/docs/collections); the syntax is one fence with two spellings.

A fence whose argument is a **name** declares the list — the catalogue, what exists to
collect:

```markdown
:::collection Override sprites
- Sonic
  - Base
  - Gold
- [Klombo](node://8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f)
- Storm Scout
  - Base
:::
```

A fence whose argument **names another node** tracks that node's list, which makes this
page a tally — one person's record of what they have:

```markdown
:::collection [[Override sprites]]
- [x] Sonic — Gold, Sprite Day 2
- [ ] Storm Scout
:::
```

Inside the fence everything is vocabulary from further up this page: bulleted items,
nested one level for variants, task marks for ticks, mentions and wiki links.

- **An item is a line of text, and a page for it is optional.** A plain item is matched
  by its text; one that links a node is matched by that id, and so survives a rename.
  Ticks made against `Sonic` keep counting once Sonic becomes `[Sonic](node://…)`.
- **Variants are nested items**, one level, and optional per item: a sprite held back
  with only its base form lists only that.
- **Only a variant can be ticked** where an item has any. "Give me all three" is a
  different statement from the three ticks it would stand in for.
- **A trailing `—` makes the rest a note**: `- [x] Sonic — Gold, Sprite Day 2`. `--`
  works too, for a keyboard without an em dash.
- **A tally naming its catalogue with `[[Title]]` resolves by title**, which is a
  search — so it cannot reach an **unlisted** catalogue. A `node://` mention can,
  because an id is permission and a title is a search.
- Where a page declares more than one list, a tally says which after the link:
  `:::collection [[Season 4]] Sprites`.
- Ordinary checklists elsewhere are untouched. `- [ ]` outside a collection still means
  *it is done* — shared state, one answer for everybody — which is the commoner kind in
  a wiki and the reason this construct is opt-in.

Reading the page draws the fence as a grid: the catalogue's rows, a column per
participant, checkboxes in your own. Editing it shows the list itself, on a card — the
source is what you want while rearranging a roster.

## Footnotes

Pandoc's footnotes: a superscript marker in prose, and the note on a line of its own —
anywhere, though the end of the page is where they read best.

```markdown
The NAS reboots nightly.[^why]

[^why]: The controller wedges after a day; see [[Homelab]].
```

- The key (`why`) binds marker to note and never shows. Readers see a **number, derived
  from marker order** — reorder the prose and the numbers follow, whatever the keys say.
- A note is ordinary Markdown: links, wiki links and emphasis in one all work.
- Reading, the marker is a superscript link down to its note, and the note's number
  links back up to the first place it was cited.
- The halves are allowed to dangle: a marker whose note was deleted still shows its
  number, and a note nobody cites still renders, trailing the cited ones — an
  authoring mistake you can see is one you can fix.
- **Insert… → Footnote** drops a marker at the caret and lands the caret in a fresh
  note at the end of the page, as one undoable edit.

## Citations

A citation is not new syntax. It is a footnote whose note cites a node — written
entirely in the vocabulary above, so it is one plain Markdown line in the file:

```markdown
The closet runs hot in summer.[^1]

[^1]: [Server closet thermals](node://8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f), captured 27 August 2026 — [example.com](https://example.com/thermals).
```

What makes it *archive-backed* is what the note points at. When the cited node is a
[bookmark](/docs/pages-and-files#bookmarks), the mention opens the capture Gatherum
keeps — the page as it stood, not the URL as it now answers — the date names the
capture that backed the claim (the saved copy's capture history can page back to it),
and the source's own address trails the note the way "archived from the original"
trails a reference that expects the original to rot. Because the mention is an
ordinary node link, the bookmark's **backlinks** answer "what cites this?", and a
citation into a node the reader may not open draws padlocked like any other mention.

A node that is no bookmark — a page, an uploaded PDF — cites as the mention alone:
nothing about it needs a date to stay true.

**Insert… → Cite…** writes all of this for you: pick any node to cite it, or paste a
URL and Gatherum captures the page as a bookmark first — filed under the page doing
the citing — and cites the fresh capture. Agents write the same line over
[MCP](/docs/mcp) with `bookmark_page` and `update_page`: capture first, then cite the
node that came back.

## Writing all this by hand

You do not have to. In the editor, **Insert…** writes the fences for you — an infobox
skeleton, a figure around a file you pick, a footnote, a citation of a node or of a
URL captured on the spot, a callout of each kind, and a
wiki link with a search box behind it. **Link node…** inserts a mention. `Ctrl+.` and
`Ctrl+,` toggle superscript and subscript at the caret, beside the usual `Ctrl+B`/`I`/
`E` for bold, italic and code. The **Source** toggle swaps the
document editor for the raw Markdown, which is the same file either way.

One thing to know if you type one anyway: these constructs are read out of the source,
and an open document is past that point. A `[[wiki link]]`, a `[^footnote]` marker or a
`:::` fence typed straight into the document editor stays the text you typed until the page is read again
— so use the Insert menu, type it in Source mode, or simply reopen the page and it will
be the real thing.

## A worked page

````markdown
# Podman on the NAS

:::infobox
# Podman
| **Kind** | Container engine |
| **Host** | The NAS in the closet |
| **Docs** | [[Homelab]] |
:::

Everything in the [[Homelab]] runs rootless under Podman, driven by quadlets rather
than by `podman run` — see [@Quadlet reference](node://8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f).

> [!WARNING] Lingering has to be on
> Rootless units die at logout without `loginctl enable-linger`, and a reboot then
> comes back with nothing running.

## The rack

:::figure left 360
![Before the rewire](/api/files/0f8f6e1a-0000-0000-0000-000000000001/content){align=center}
:::

- [x] Move the switch to the top
- [ ] Label the runs

| Service | Port |
| --- | --- |
| Gatherum | 8080 |
| Authelia | 9091 |

```sh
systemctl --user start gatherum
```
````

## Cheat sheet

```markdown
[@Title](node://<node-id>)            mention — links by id, survives a rename
[[Title]]                             wiki link — links by title
[[Title|label]]                       wiki link with a label ( \| inside a table )
![alt](/api/files/<node-id>/content)  embed another node's bytes

:::infobox                            an encyclopedia card, floated right
# Heading
| **Label** | Value |
:::

:::figure left 360                    a captioned picture, floated left, 360px
![The caption](/api/files/<node-id>/content){align=center}
:::

![The caption](url){width=300}        the {…} makes bracket text a caption; width/align size and place it

> [!NOTE] Optional title              a callout: note, tip, important, warning, caution
> The body.

:::collection Override sprites        a collectible list: the catalogue
- Sonic
  - Gold                              a variant, nested one level
:::

:::collection [[Override sprites]]    a tally: one person's ticks against that catalogue
- [x] Sonic — a note after the dash
:::

x^2^  H~2~O                           superscript, subscript
Prose.[^key]                          a footnote marker — readers see a derived number
[^key]: The note                      its note, on a line of its own
[^key]: [Title](node://<node-id>), captured 27 August 2026 — [host](https://…).
                                      a citation: the mention opens the kept capture
```
