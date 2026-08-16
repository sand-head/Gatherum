# Decisions

Deviations from the brief and judgment calls worth remembering. Newest last.

## Core references EF Core + Npgsql directly
The brief forbids speculative abstraction, and Postgres full-text search is an
architectural commitment (tsvector columns, `websearch_to_tsquery`), not a swappable
detail. So application services in `Gatherum.Core` use `GatherumDbContext` directly and
Core references the Npgsql provider. Infrastructure keeps what genuinely has alternative
implementations: storage, extraction, collab persistence — plus the migrations assembly.

## Development auto-login when OIDC is unconfigured
OIDC-only auth means a bare `dotnet run` would be unusable without an IdP. When
`Gatherum__Oidc__Authority` is unset, `/auth/login` signs in a local "Dev User" and the
app logs a warning at startup. Production deployments set the OIDC env vars, which
disables this path entirely. This keeps "usable end-to-end via dotnet run" true without
weakening configured deployments.

## Revisions collapse within a five-minute window
"Every save creates a revision" taken literally turns autosave (every ~1.2 s of typing)
into hundreds of junk snapshots. Saves by the same author within five minutes update the
latest revision in place; a pause or another author starts a new one. History stays
meaningful and restore still works at the granularity a human would want.

## TipTap v2, not v3
The editor pins TipTap 2.27.x: the v2 line is stable, its collaboration-cursor package
matches y-prosemirror 1.x, and v3 renamed/reshuffled the collaboration packages recently
enough that the risk wasn't worth it for the MVP. Upgrade path: bump all @tiptap/*
packages together and swap `extension-collaboration-cursor` for v3's
`extension-collaboration-caret`.

## Collaboration doc is seeded by the first client
When a page opens for live collab and the persisted Yjs state is empty, the first
browser to sync seeds the doc from the stored TipTap JSON. Two people racing to open a
never-collaborated page within the same instant could double-seed; accepted for a
two-person MVP because the window is milliseconds and every later open sees non-empty
state. Server-side seeding (building the XmlFragment via YDotNet in C#) is the upgrade
if it ever matters.

## External page edits reset collab state
`update_page` (REST/MCP) and revision restore write the body and delete the persisted
Yjs state, so the next editor session re-seeds from the stored content. Clients editing
live at that exact moment keep their in-memory doc and their next autosave wins. For two
users this is a understandable last-writer-wins corner; the richer fix (applying external
edits as CRDT transactions server-side) has a clear seam in `SavePageAsync`.

## Saves are serialized per node, in process
Concurrent autosaves from two live editors raced on revision numbers and link rows.
`SavePageAsync` takes a per-node semaphore. Process-wide is sufficient because Gatherum
deploys as a single container; scaling out would move this to a database lock.
