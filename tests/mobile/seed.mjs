// Fixtures for the mobile checks. A fresh Gatherum is empty, and an empty page
// proves nothing about a layout: the things that broke on a phone all needed
// content to break against — a heading tree for the Contents panel, an aside the
// prose wraps past, a taxonomy deep enough to run off the screen, a file with a
// history worth tabulating.
//
// Auth is the dev auto-signin: with Gatherum__Oidc__Authority unset, visiting
// /auth/login signs you in as Dev User, and the context's cookie jar carries that
// into every request below. No API key to mint, nothing to put in a file.
//
// Idempotent by title: a second run edits what the first one made.
import { launch } from "./browser.mjs";

const base = arg("--url") ?? "http://localhost:5140";

const PAGES = [
  {
    title: "Homelab: the closet rack",
    categories: ["Homelab", "Podman"],
    markdown: `The closet holds a four-unit rack, and everything in it runs as a Podman quadlet under a single unprivileged user.

:::infobox
## The rack
4U, closed door, passive return
:::

## Hardware

Thermals were the whole problem for the first summer: the door is solid, the return is a gap under it, and nothing moves air across the top of the rack.

### The closet and its thermals

> [!NOTE]
> The intake sits behind the door, which is why the numbers looked fine until July.

## Podman quadlets

Every service is a \`.container\` file in \`~/.config/containers/systemd\`. See [[Reverse proxy configuration]] and [[A page nobody has written]].

| Service | Port | Notes |
| --- | --- | --- |
| caddy | 443 | terminates TLS |
| gatherum | 8080 | behind caddy |

## A wide table

Past the point where every column is at its longest word there is nowhere left to squeeze, so this one scrolls in its own band rather than shrinking the article.

| Service | Image | Published port | Restart policy | Volume | Notes |
| --- | --- | --- | --- | --- | --- |
| caddy | docker.io/library/caddy:2 | 443 | on-failure | /srv/caddy | terminates TLS for everything |
| gatherum | ghcr.io/sand-head/gatherum | 8080 | always | /data/files | behind caddy on the same net |

## Two asides in a row

:::infobox
## First card
Nothing but a blank line separates these.
:::

:::infobox
## Second card
Which is the case that used to overlap.
:::

Prose after both, so the flow has somewhere to resume.

## A heading with a deliberately long title that will not fit a phone in one line

Text after it, so the outline has something to scroll to.
`,
  },
  {
    title: "Reverse proxy configuration",
    categories: ["Networking"],
    markdown: `Caddy terminates TLS and forwards to the container on 8080.

## Certificates

Renewal is automatic; the only manual step is the DNS challenge token.
`,
  },
  {
    title: "A very long chapter title that keeps going well past the fold",
    categories: ["Fiction"],
    markdown: "Short body, long title — the tree row is the thing under test.\n",
  },
];

// Nesting is filing a category's page under another category, so a deep taxonomy is a
// chain of those — child first, then the one above it. The long name is deliberate: the
// outline indents per level and a narrow screen has to survive both.
const NESTING = [
  ["Podman", "Homelab"],
  ["Networking", "Homelab"],
  ["VLANs", "Networking"],
  ["Tagged trunks and where they terminate", "VLANs"],
  ["Deeper still", "Tagged trunks and where they terminate"],
];

// A real extension, so the lexer has something to say and the read view has
// colours to prove it kept. The long line is deliberate: a code listing wraps
// the grid's way or scrolls, never the browser's.
const CODE_FILE = {
  name: "Thermals.cs",
  type: "text/plain",
  body: `using System;

namespace Homelab.Closet;

/// <summary>What the rack does to the air around it.</summary>
public sealed record Reading(DateTimeOffset At, double IntakeC, double ExhaustC)
{
    public double Delta => ExhaustC - IntakeC;

    public static Reading Parse(string line) => line.Split(',') is [var at, var i, var e]
        ? new Reading(DateTimeOffset.Parse(at), double.Parse(i), double.Parse(e))
        : throw new FormatException($"A reading is three fields, and this one was not: {line}");
}
`,
};

// A 1x1 PNG is enough to prove <img> resolves; the layout does not care what it is.
const PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
  "base64");

function arg(name) {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : undefined;
}

async function main() {
  const browser = await launch();
  const ctx = await browser.newContext({ baseURL: base });
  const page = await ctx.newPage();
  await page.goto("/auth/login");
  const api = ctx.request;

  const tree = await (await api.get("/api/nodes/tree")).json();
  const byTitle = new Map(tree.map((n) => [n.title, n.id]));
  const made = [];

  for (const spec of PAGES) {
    let id = byTitle.get(spec.title);
    if (id) {
      await api.put(`/api/pages/${id}`, { data: { markdown: spec.markdown } });
    } else {
      const created = await api.post("/api/pages", {
        data: { parentId: null, title: spec.title, markdown: spec.markdown },
      });
      id = (await created.json()).id;
    }
    for (const name of spec.categories)
      await api.post(`/api/nodes/${id}/categories`, { data: { name } });
    made.push([spec.title, id]);
  }

  // A category is a page, so nesting one is filing that page — which means the deep
  // tree is built by looking each category up by name and filing it under the next.
  const anchor = made[0][1];
  for (const [child] of NESTING)
    await api.post(`/api/nodes/${anchor}/categories`, { data: { name: child } });
  for (const [child, parent] of NESTING) {
    const view = await api.get(`/api/categories/${encodeURIComponent(child)}`);
    const { id } = (await view.json()).category;
    await api.post(`/api/nodes/${id}/categories`, { data: { name: parent } });
  }

  if (!byTitle.has(CODE_FILE.name)) {
    const created = await api.post("/api/files", {
      multipart: {
        file: { name: CODE_FILE.name, mimeType: CODE_FILE.type, buffer: Buffer.from(CODE_FILE.body) },
      },
    });
    made.push([CODE_FILE.name, (await created.json()).id]);
  }

  if (!byTitle.has("rack.png")) {
    const created = await api.post("/api/files", {
      multipart: { file: { name: "rack.png", mimeType: "image/png", buffer: PNG } },
    });
    const id = (await created.json()).id;
    // A second and third version, so the history table has rows to stack.
    for (let i = 0; i < 2; i++)
      await api.post(`/api/files/${id}/versions`, {
        multipart: { file: { name: "rack.png", mimeType: "image/png", buffer: PNG } },
      });
    made.push(["rack.png", id]);
  }

  await browser.close();
  for (const [title, id] of made) console.log(`${id}  ${title}`);
  console.log(`\nSeeded ${made.length} nodes at ${base}.`);
}

main().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
