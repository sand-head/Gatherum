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
    categories: ["Homelab", "Homelab/Podman"],
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

## A heading with a deliberately long title that will not fit a phone in one line

Text after it, so the outline has something to scroll to.
`,
  },
  {
    title: "Reverse proxy configuration",
    categories: ["Homelab/Networking"],
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

const DEEP_CATEGORY = [
  "Homelab/Networking/VLANs",
  "Homelab/Networking/VLANs/Tagged trunks and where they terminate",
  "Homelab/Networking/VLANs/Tagged trunks and where they terminate/Deeper still",
];

const CODE_FILE = {
  name: "caddyfile.conf",
  type: "text/plain",
  body: "example.test {\n\treverse_proxy 127.0.0.1:8080\n}\n",
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
    for (const path of spec.categories)
      await api.post(`/api/nodes/${id}/categories`, { data: { path } });
    made.push([spec.title, id]);
  }

  // Categories exist by being used, so the deep tree needs a node filed under it.
  const anchor = made[0][1];
  for (const path of DEEP_CATEGORY)
    await api.post(`/api/nodes/${anchor}/categories`, { data: { path } });

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
