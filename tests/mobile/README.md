# Mobile checks

Nothing in the .NET test suite can see a layout: it has no browser, and CI only
proves the image builds. These are the net for the things that broke Gatherum on a
phone — a pane too narrow to hold a word, a control no finger can hit, a field that
traps iOS at 1.4x — and they run against a **running** instance rather than against
a stylesheet, so what they measure is what a reader gets.

They are deliberately not wired into `dotnet test` or CI: they need a browser, a
database and a server, and a screenshot diff that fails on a font hint helps nobody.
Run them when you touch layout.

```sh
# 1. a Gatherum on :5140, with Gatherum__Oidc__Authority unset so you are Dev User
docker run -d --name gatherum-pg -p 5432:5432 \
  -e POSTGRES_DB=gatherum -e POSTGRES_USER=gatherum -e POSTGRES_PASSWORD=gatherum \
  pgvector/pgvector:pg16
dotnet run --project ../../src/Gatherum.Web

# 2. once, and again whenever you want the fixtures back
npm install
npm run seed

# 3. the checks
npm run check                  # asserts, and writes shots/ for the eye
npm run check -- --url http://localhost:8080
```

`seed.mjs` writes the content the checks need and prints the node ids it made —
a long page with headings, a page with an infobox and a callout, a deep category
tree, a file with several versions, an image, and a code file. It is idempotent by
title: running it twice edits rather than duplicates.

`check.mjs` visits every route at 360 / 390 / 768 / 1280, in light and dark, with a
coarse pointer below 768, and fails on any of:

- horizontal overflow (`scrollWidth > innerWidth`) — the one that hides everything else
- an interactive control under 44px, or a field under 16px, under a coarse pointer
- the row menu still hover-gated on touch, or shown on a desktop pointer
- the drawer failing to open, or staying open across a navigation
- a page with no `h1` (`FocusOnNavigate` targets one)

Screenshots land in `shots/` as `<route>-<width>-<scheme>.png`. They are for the
pull request, not for a diff: nothing here compares them.
