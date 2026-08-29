# Sharing and privacy

Gatherum is private by default. A node nobody has said anything about is its owner's
alone, and so is everything under it — which is also what an unprepared directory means
when Gatherum first scans one.

## Who can sign in

Gatherum authenticates against your identity provider and can be told to admit only one of
its groups — the usual arrangement for an instance shared by a team. Membership stays the
provider's to know: it is read from the sign-in and remembered nowhere, so removing somebody
there removes them here at their next attempt. A second group can carry admin the same way.

Groups decide who gets *in*. They are not a sharing primitive: inside Gatherum, sharing
names people.

## Ownership is the path

Whoever owns the root directory a node was found under owns the node. Ownership is read
off the layout rather than stored as an opinion about it, which is why it survives losing
the database. A user's root directory is named after their identity provider username,
assigned once and never renamed.

Only an owner can change who reaches their nodes, and only where an owner could have
written it.

## The four modes

| Mode | Who reaches it | Listed? |
| --- | --- | --- |
| **Private** | The owner | — |
| **Shared** | The owner, plus the people named in its grants | To those people |
| **Unlisted** | Anyone holding the link, without signing in | No |
| **Public** | Anyone on the internet, without signing in | Yes |

**Unlisted** is the interesting one. Everywhere else, "may you reach this?" and "may you
find this?" have the same answer. An unlisted node breaks them apart: its id is the
secret, so the link works for anyone, and nothing enumerates it — not the tree, not
search, not category pages, not backlinks, not `[[wiki link]]` resolution.

## Grants

**Shared** names people. Each grant carries a role:

- **Reader** — may read the node and its subtree.
- **Editor** — may also edit the content.

Structural changes — rename, move, delete — stay with the owner regardless of role.
Seeing is not editing, and editing is not owning.

## Inheritance

Access flows downward: sharing a directory shares what is in it. A node inherits its
ancestors' access unless it says otherwise, and a node that opts out of inheritance is
the escape hatch for a subtree that has to be tighter than the one containing it.

Inheritance is a **maximum**, not an override: a node reaches as far as its own
declaration or its ancestors', whichever goes further. So publishing one page inside a
private tree publishes exactly that page, and a subtree that has to be tighter than its
parent is the case that turns inheritance off.

## What a signed-out visitor gets

Anonymous is not an identity. It reaches public nodes read-only, and unlisted nodes when
it holds the id. It never writes: no API endpoint that changes anything accepts an
unauthenticated caller, ever.

Anonymous traffic is metered per client address — a read budget and a tighter search
budget, both configurable, both off entirely for anyone signed in. An owner who wants
none of this can switch publishing off instance-wide with
`Gatherum__Sharing__AllowPublic=false`, which hides every public node at once without
editing what anyone recorded.

## Signing in

Authentication is OIDC-only — built for Authelia, and any discovery-capable provider
works. There are no local accounts. The first user ever to sign in becomes admin.

## API keys

Scripts and agents authenticate with an API key instead of a session. Create one in
**Settings → API keys**; the token (`gk_…`) is shown once and stored hashed, and can be
revoked at any time.

```
Authorization: Bearer gk_…
```

A key carries its owner's identity and nothing more: it sees exactly what that person
sees, and can do exactly what that person can do. `/api` accepts a key or a browser
session; `/mcp` accepts only a key.

This manual is the one thing served without any of that — `/docs` is readable by anyone
who can reach the app, because it describes the software rather than what is in it.
