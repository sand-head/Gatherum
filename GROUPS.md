# Groups, roles, and guests — a proposal

**Status: proposed, not built.** This amends the access model designed in
`FILESYSTEM.md` and the standing rules in `AGENT.md`. Written for the owner to accept,
amend, or reject; if accepted, the rule it changes earns a `DECISIONS.md` entry rather
than being quietly reworded.

## Why now

Gatherum has been described everywhere as a knowledge base *for two people*. The owner's
correction: the intended scope is one person, a small group, or a medium-sized one. Two
consequences follow, and they are what this note is about.

1. Naming people one at a time stops scaling. A grant needs to be able to name a **group**,
   and the identity provider already knows who is in one.
2. "Anonymous, just for list checking" becomes a real requirement rather than a nice-to-have
   — and it runs into a rule that doesn't bend.

## Part 1 — a grant names a principal, not a user

Today the chain is `NodeGrant(NodeId, UserId, Role)` → `NodeAccessEntry(NodeId, UserId,
Role)`, and `DefaultNodeAuthorizer.VisibleTo` filters `n.AccessEntries.Any(e => e.UserId ==
id)`. Everything is keyed to one user id.

The change is to key it to a **principal**, which is either a user or a group. The closure
row becomes `NodeAccessEntry(NodeId, PrincipalId, Role)`, and the caller arrives with a
*set* of principals — their own id plus one per group in their token:

```csharp
nodes.Where(n => n.Reach == NodeReach.Listed && ceiling >= NodeReach.Listed
    || n.OwnerId == id
    || n.AccessEntries.Any(e => principals.Contains(e.PrincipalId)))
```

That keeps the property the closure exists for: still one indexed predicate, still no
ancestor walk. An `IN` over a handful of ids is the same shape of query as an equality,
given an index leading on `PrincipalId`.

### Membership belongs to the identity provider

Read the `groups` claim per request; **never store who is in a group.** Gatherum stores
*grants* — "this node is shared with `collectors`" — and asks the token who is in
`collectors` at the moment somebody knocks.

This is the same discipline the rest of the system already runs on. The filesystem is the
system of record for content and the database is an index over it; the IdP is the system of
record for membership and Gatherum holds no opinion about it. It is also why removal works:
take someone out of the group in Authelia and their next request has already lost the
access. Storing membership would make Gatherum a user-administration tool, which is the
same thing "no local accounts, ever" is there to prevent.

### A group id needs no table

Derive a group's `Guid` deterministically from its normalized name — a UUIDv5-style hash
over a fixed namespace — so:

- the sidecar stores the **name** (`"collectors"`), which is what a human reading
  `.gatherum/meta.json` needs to see,
- the closure stores the derived id, which is what the query needs,
- and a cold reindex recomputes one from the other with nothing to look up.

No `Groups` table means no table that a rebuild could fail to recover — the exact reasoning
`AccessService` already gives for writing grants to the sidecar before trusting them.

### The sidecar grows a second case

`MetadataGrant(string Root, AccessRole Role)` carries a *root name*, i.e. a username,
because that is what identifies a person on disk. A group has no root, so the record needs
two cases (`Root` xor `Group`), with the old shape still readable.

The authority rule is unchanged and worth restating because groups make it tempting to
forget: **a grant is honored only where the owner could have written it.** A group grant in
Alice's `.gatherum/` grants access inside Alice's tree and nowhere else.

### Granting to a group cannot be validated

`GrantAsync` currently rejects an unknown grantee with `db.Users.AnyAsync(...)`. There is no
equivalent for a group: Gatherum has never heard of `collectors` until somebody holding that
claim signs in, and a group with no members yet is perfectly legitimate.

So granting to a group is granting to a *name*, and a typo is silent — the worst failure
mode in an access system, because it fails closed and looks like it worked. Mitigation, and
it should ship with the feature rather than after it: offer group names seen in current
sessions as autocomplete, and mark a grant that has never matched anybody as **unconfirmed**
in the UI instead of pretending it was validated.

### The API key hole — the one genuinely hard question

An API key authenticates as a user, but it carries no token and therefore no fresh `groups`
claim. Since MCP agents are first-class users here, this is not an edge case.

- **(a) API keys get direct grants only.** Safe and simple; an agent cannot read what its
  own user can read in the browser, which will be reported as a bug roughly weekly.
- **(b) Cache the group set on the `User` at login.** Works, and is stale *in the unsafe
  direction*: removing someone from a group at the IdP leaves their key's access intact
  until they next sign in interactively — which for an agent-only account may be never.
- **(c) (b), bounded by refresh.** `RequestOfflineAccess` is already a configured option, so
  where a refresh token exists the group set can be re-resolved on a schedule and the
  staleness window becomes a number you choose rather than an accident.

I recommend **(c), degrading to (a)** when no refresh token is available — never (b) alone.
Whatever is chosen, it is a `DECISIONS.md` entry, because it is the one place in this design
that can silently grant more than the owner intended.

### "Role" means three different things — keep them apart

1. **What a grant confers** — `AccessRole.Reader | Editor`. Exists, unchanged.
2. **Who can be named by a grant** — users today, groups under this proposal.
3. **What you are instance-wide** — `User.IsAdmin` exists on the entity and in
   `GatherumClaims`, but nothing in the OIDC ticket handler ever sets it.

Only (3) is actually missing, and it is small: map a configured group name to admin
(`Gatherum__Oidc__AdminGroup`), evaluated at each login beside `preferred_username`. Resist
turning that into a role system — one flag, one config key.

## Part 2 — anonymous list checking

The rule this meets is `AGENT.md`'s: *an API endpoint is authenticated unless it says
`.AllowAnonymous()`, and no write ever does.* Anonymous is not an identity; it reaches
public nodes read-only through `VisibleTo(nodes, null)`.

Underneath that rule sits a structural fact that is easy to miss: **ownership is the path,
and an anonymous visitor has no root.** There is nowhere on disk for their file to live
without inventing an owner. That, not the rule, is the real obstacle.

### Default: store nothing at all

A signed-out visitor ticks against a public catalogue and their ticks live in their own
browser, written always and read when the server had nothing. This is exactly what already
happens for a signed-out reader's place in a book, and it needs no new concept, no storage,
no rate limit, and no spam story.

For "I want to check off sprites on someone's public list", this is the whole feature. Ship
this first, alone, and a good fraction of the requirement is met at nearly no cost.

### Promotion: the visitor deliberately publishes

The part localStorage cannot do is show your column to everyone else. So make that a
separate, deliberate act — "share my list" — which mints a guest tally under the **catalogue
owner's** root:

```
alice/Collections/Override sprites.guests/<slug>.md
```

Alice owns those files because they are in her tree. She is hosting a guestbook: they sit in
her backup, count against her storage, and are hers to delete. Ownership-is-the-path stays
literally true, and nobody had to invent a rootless user.

Because promotion is deliberate and rate-limited, the ambient spam surface stays at zero for
the common case — a drive-by visitor creates nothing.

### The write is authorized; it just carries no identity

Mint a hashed capability token scoped to that one node, held by the visitor. That is
`ApiKeys` narrowed to a single node, not a new authentication concept — and `ApiKeys` is
already one of the three table-only exceptions, so the shape is precedented.

The rule then reads: **no write is ever unauthenticated**, where a capability that carries no
identity still counts as authentication. That is a real amendment to a rule that doesn't
bend, and it should be written down as one.

### Do not reuse the node id as the secret

`Unlisted` makes a node's id the secret, and it is tempting to lean on that here. It cannot
be reused for writes: a guest tally links to the catalogue, and the aggregate column works by
enumerating exactly those links — so the feature that displays everyone's progress would hand
out every guest's write key along with it. The token must be separate, hashed at rest, and
shown once.

### Controls that ship with it, not after

- **Off by default**, per catalogue, plus an instance switch beside `Sharing.AllowPublic`.
  Private by default has to survive this feature.
- **Caps**: guest tallies per catalogue, bytes per tally.
- **Rate limiting**: the anonymous limiter partitions on client address, which behind a proxy
  means `X-Forwarded-For`, trusted from any peer — the loopback bind is what stops header
  spoofing, so this feature is another reason not to publish the port wider.
- **Guest display names are untrusted text** from the open internet: length-capped, rendered
  as text, and the owner can rename or remove any of them.
- **Losing the token loses the list.** Say so at mint time, in the UI, before it matters.

## Part 3 — what group scale breaks that two people hid

None of this blocks the work above, but the owner should know which "fine at two people"
notes stop being fine, because several are written into the docs as accepted trade-offs:

1. **Semantic search starves.** Visibility is filtered *after* the HNSW index picks its
   neighbours, and `STATUS.md` is explicit that over-fetching makes starvation "unlikely at
   two people's scale; it is not a proof." With twenty people and twenty private subtrees it
   stops being unlikely. This is the one I'd fix first.
2. **Presence is in-process** and documents itself as enough "for a single-instance
   deployment."
3. **Save gates are in-process semaphores** — `STATUS.md` already names a database-level lock
   as the prerequisite for a second app instance.
4. **Signed-in callers are never rate-limited** (`AnonymousRateLimits`), which is a sound call
   for two authenticated people and a weaker one for fifty — and gives a leaked API key no
   ceiling at all.
5. **File bytes are never garbage-collected** when nodes are deleted (`STATUS.md`). Storage
   growth now scales with the number of people.

## What not to do

- No local accounts, and no group *membership* stored in Gatherum. The IdP owns that.
- No `Groups` table — the name derives the id, so a rebuild recovers it.
- No second visibility door for guests. They go through `VisibleTo` like everyone else.
- No id-as-write-secret.
- No new abstraction seam; nothing here has a second implementation.

## Open questions

1. **API keys and group claims** — (a), (b) or (c) above? This is the security-relevant one.
2. **Do guest tallies appear in the aggregate immediately, or after the owner approves each
   one?** Approval is the strongest anti-spam answer and also the most work; it may be worth
   it if a catalogue is ever linked somewhere busy.
3. **Do group grants need `Editor`, or is group-means-Reader enough for v1?** Reader-only
   halves the blast radius of a mistyped group name.
4. **Should `Sharing.AllowPublic` being off also disable guest tallies?** I think yes —
   one switch that means "this instance does not face the internet".
