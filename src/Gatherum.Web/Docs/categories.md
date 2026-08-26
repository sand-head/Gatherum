# Categories

A category is what a node is *about*. It is the second of Gatherum's two trees: the node
tree says where something *is*, categories say what it is *about*, and nothing else names
a subject. There are no tags.

**A category is a page.** Not a label, not a path — an ordinary Markdown node with a body
saying what belongs in it, a version history, backlinks, and a `[[Homelab]]` that resolves
to it. It lives at `Categories/<Name>.md` in the root of whoever first mentioned it, and
it comes into existence by being used: there is no "create category" step, and filing a
page under `Podman` writes Podman's page if nobody has yet.

## One relation, twice

A node is filed under a category. A category filed under a category is a **subcategory** —
the same act, pointed at a subject instead of at a page about one.

```
Homelab          ← a category page
  Podman         ← a category page filed under Homelab
    Quadlets.md  ← a page filed under Podman
```

Filing a page under `Podman` makes it a member of `Homelab` too: the parent category lists
it, a search for either name finds it, and "Similar" counts the kinship.

Because nesting is filing, a subject can sit in **more than one place**. `Podman` can be
filed under `Homelab` and under `Containers` at once, and pages in it belong to both. An
encyclopedia's index does this; a slash-separated path could not.

## Names

A category is addressed by its name, and names are unique among categories. They are
spelled forgivingly: `Home lab`, `home  lab` and `HOME LAB` are one category, and the
capitalization that sticks is the one on its page.

The name is also what a node writes down on disk to say what it is about — see
[Configuration](/docs/configuration#how-your-files-are-stored) — which is why it has to be
unique: an id would be a database's opinion, and that file exists for the day there is no
database. Renaming a category is renaming its page; everything filed under it follows,
because nothing recorded a path.

## Filing and unfiling

A node can be in any number of categories, or none. Nothing is filed automatically, and an
uncategorized node is not a problem: search still finds it, the tree still holds it.

- In the UI: the category bar at the foot of the node page, under the article and under
  what links here. Reading shows the names alone; **Edit** the node — any node, a page or
  a file or a folder — and the bar grows an × per name and a **+** that opens a field for
  one more. A category's own bar is where it is nested under another.
- Over REST: `POST /api/nodes/{id}/categories` with `{ "name": "Podman" }`, and
  `DELETE /api/nodes/{id}/categories/{name}`.
- Over MCP: `add_category` and `remove_category`.

Removing a node from `Podman` leaves it in whatever else it was in, including `Homelab` if
it was filed there in its own right. Removing the child does not remove the parent.

## Browsing

`/categories` is the whole taxonomy, nested. A subject filed under two parents appears
under both — it genuinely is in both places.

Each category is at `/categories/<Name>`, which is its page: its own prose first, then what
is nested under it, then what is filed in it — with an option to include everything under
its subcategories too. Subcategories are listed as subcategories and never counted as
members.

Two counts are reported for every category, and they answer different questions:

| Count | Means |
| --- | --- |
| `members` | Nodes filed in exactly this category |
| `subtreeMembers` | Nodes filed in it or anything nested under it |

## Maintaining them

There is nothing to maintain that is not an ordinary page operation, which is the point of
a category being a page:

- **Rename** it by renaming its page. Everything filed under it stays filed.
- **Re-nest** it by filing its page under a different category, or unfiling it to make it
  a subject in its own right.
- **Delete** it by deleting its page. What was filed there is not deleted; it is simply no
  longer filed there. Its subcategories are pages too, so they are not deleted either —
  they lose a parent and become subjects of their own.

A category cannot be nested inside itself, however long the way round.

Category names appear in the search index, so a node is findable by the name of a category
it is in — and by every name that category is nested under — even when its own text never
says the word. Categories are left out of an unqualified search, because every filed page
has a same-named subject standing beside it; ask for `kind: category` to search them.

## Privacy

Category listings show only what the person browsing may see. Filing a private node under a
public category does not reveal it: the counts and listings another user gets are computed
against what they can reach, and a category whose every member is private to somebody else
is not listed at all.

A category's *page* is a page, so who may read its prose and who may edit it are that
page's own business — private to whoever wrote it until they share or publish it. The
subject is everyone's; the essay about it is its author's.
