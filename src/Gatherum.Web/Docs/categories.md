# Categories

A category is what a node is *about*. It is the second of Gatherum's two trees: the node
tree says where something *is*, categories say what it is *about*, and nothing else names
a subject. There are no tags.

Categories are arranged the way an encyclopedia arranges them — nested, not a cloud:

```
Homelab
Homelab/Networking
Homelab/Podman
Writing
Writing/Fic
```

## Paths

A category is identified by its **path**, with `/` between levels. Filing a page under
`Homelab/Podman` files it under `Homelab` too: the parent category lists it, a search for
either name finds it, and "Similar" counts the kinship.

Paths are spelled forgivingly. `Homelab / podman`, `homelab/podman` and `Homelab/Podman`
are the same category; the capitalization that sticks is whoever created it first. A
category comes into existence by being used — there is no "create category" step, and
adding `Homelab/Podman` to a node creates `Homelab` if nothing had needed it yet.

## Filing and unfiling

A node can be in any number of categories, or none. Nothing is filed automatically, and
an uncategorized node is not a problem: search still finds it, the tree still holds it.

- In the UI: the category bar at the foot of the node page, under the article and
  under what links here — chips to follow, and a field to file one more.
- Over REST: `POST /api/nodes/{id}/categories` with `{ "path": "Homelab/Podman" }`, and
  `DELETE /api/nodes/{id}/categories/{path}`.
- Over MCP: `add_category` and `remove_category`.

Removing a node from `Homelab/Podman` leaves it in whatever else it was in, including
`Homelab` if it was filed there in its own right. Removing the child does not remove the
parent.

## Browsing

`/categories` is the whole taxonomy in path order, with member counts. Each category has
a page of its own: its ancestry, its subcategories, and the nodes filed in it — with an
option to include everything filed under its subcategories too.

Two counts are reported for every category, and they answer different questions:

| Count | Means |
| --- | --- |
| `members` | Nodes filed in exactly this category |
| `subtreeMembers` | Nodes filed in it or anything nested under it |

## Maintaining them

Categories are maintained like anything else, and their subcategories follow along:

- **Rename** changes a category's name in place; everything filed under it stays filed.
- **Move** re-nests a category under a different parent — or to the top level — taking
  its subcategories with it.
- **Delete** removes a category. What was filed there is not deleted; it is simply no
  longer filed there.

Category names appear in the search index, so a node is findable by the name of a
category it is in even when its own text never says the word.

## Privacy

Category pages list only what the person browsing may see. Filing a private node under a
public category does not reveal it: the counts and listings another user gets are
computed against what they can reach.
