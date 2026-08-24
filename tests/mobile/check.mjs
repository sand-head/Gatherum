// What a phone actually gets, asserted rather than eyeballed.
//
// Every check here is a regression that shipped: a content pane 148px wide at
// 390px because a 236px sidebar would not move, a row menu no finger could reveal,
// a 14px field that trapped iOS Safari at 1.4x with no way back. The assertions are
// deliberately about measurements and reachability rather than pixels — a
// screenshot diff that fails on a font hint helps nobody, so the shots are written
// for a human and compared by one.
import { launch } from "./browser.mjs";
import { mkdirSync } from "node:fs";

const base = arg("--url") ?? "http://localhost:5140";
const shots = "shots";

// Below the shell breakpoint the sidebar is a drawer and the pointer is coarse.
const VIEWPORTS = [
  { name: "360", width: 360, height: 800, dpr: 3, touch: true },
  { name: "390", width: 390, height: 844, dpr: 3, touch: true },
  { name: "768", width: 768, height: 1024, dpr: 2, touch: false },
  { name: "1280", width: 1280, height: 900, dpr: 1, touch: false },
];

const TAP = 44;   // the floor a finger needs
const FONT = 16;  // below this, iOS Safari zooms in on focus and never back out

function arg(name) {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : undefined;
}

// Anything a user can hit or type into.
const INTERACTIVE = "button, .button, a.tree-link, input, textarea, select";

async function routes(page) {
  const tree = await (await page.request.get("/api/nodes/tree")).json();
  const page1 = tree.find((n) => n.title.startsWith("Homelab"))?.id;
  const file = tree.find((n) => n.title.endsWith(".png"))?.id;
  const code = tree.find((n) => n.title.endsWith(".cs"))?.id;
  return [
    ["home", "/"],
    ["pages", "/pages"],
    ["categories", "/categories"],
    ["category-deep", "/categories/Deeper%20still"],
    ["settings", "/settings"],
    ["not-found", "/not-found"],
    ...(page1 ? [["node-read", `/nodes/${page1}`], ["node-edit", `/nodes/${page1}?edit`]] : []),
    ...(file ? [["node-file", `/nodes/${file}`]] : []),
    ...(code ? [["node-code", `/nodes/${code}`], ["node-code-edit", `/nodes/${code}?edit`]] : []),
  ];
}

async function measure(page) {
  return page.evaluate(({ TAP, FONT, INTERACTIVE }) => {
    const small = [], smallFont = [];
    for (const el of document.querySelectorAll(INTERACTIVE)) {
      const cs = getComputedStyle(el);
      if (cs.display === "none" || cs.visibility === "hidden") continue;
      const box = el.getBoundingClientRect();
      if (box.width === 0 && box.height === 0) continue;
      const name = (typeof el.className === "string" && el.className) || el.tagName.toLowerCase();
      if (el.matches("input, textarea, select") && parseFloat(cs.fontSize) < FONT)
        smallFont.push(`${name} @ ${cs.fontSize}`);
      if (box.height < TAP)
        small.push(`${name} @ ${Math.round(box.height)}px`);
    }
    const rowMenu = document.querySelector(".row-menu");
    return {
      small: [...new Set(small)],
      smallFont: [...new Set(smallFont)],
      overflow: document.documentElement.scrollWidth - window.innerWidth,
      headings: document.querySelectorAll("h1").length,
      rowMenuVisible: rowMenu ? getComputedStyle(rowMenu).visibility === "visible" : null,
      // Emulation is per-context and has been observed to lapse across a long
      // session of navigations; a run that quietly stopped emulating a phone
      // would report every touch rule as broken. Fail loudly instead.
      coarse: matchMedia("(pointer: coarse)").matches,
      // The canvas must not be what a reader is handed.
      canvasInRead: document.querySelectorAll("canvas").length,
      // slopedit collapses a float when the page cannot spare MinBodyWidthPx
      // beside it, which is what keeps an infobox from crushing the prose to one
      // word per line on a phone. The host does not decide it and must not: this
      // asserts the outcome, so the mobile reading experience cannot regress
      // quietly under a package bump.
      floatedAsides: [...document.querySelectorAll(".reader-doc aside")]
        .filter((a) => getComputedStyle(a).float !== "none").length,
    };
  }, { TAP, FONT, INTERACTIVE });
}

async function main() {
  mkdirSync(shots, { recursive: true });
  const browser = await launch();
  const failures = [];
  const note = (where, what) => failures.push(`${where}: ${what}`);

  // A context per route rather than per viewport: touch emulation is a
  // context-level override and it does not reliably survive a long run of
  // navigations, which is a good way to spend an afternoon fixing an app that was
  // never broken.
  const open = async (vp, scheme) => {
    const ctx = await browser.newContext({
      baseURL: base,
      viewport: { width: vp.width, height: vp.height },
      deviceScaleFactor: vp.dpr,
      isMobile: vp.touch,
      hasTouch: vp.touch,
      colorScheme: scheme,
    });
    const page = await ctx.newPage();
    const errors = [];
    page.on("console", (m) => { if (m.type() === "error") errors.push(m.text().split("\n")[0].slice(0, 200)); });
    page.on("pageerror", (e) => errors.push(e.message.split("\n")[0].slice(0, 200)));
    await page.goto("/auth/login");
    return { ctx, page, errors };
  };

  // Blazor Auto renders on the server circuit while the WebAssembly payload
  // downloads and locally on every visit after, and those are different runtimes
  // running different code. Measuring only the first visit left the whole
  // WebAssembly path untested — which is how a read view that threw
  // TypeLoadException on every return visit passed a green suite. Go there
  // twice: the second navigation is the one a reader actually gets.
  const visit = async (page, url) => {
    await page.goto(url, { waitUntil: "networkidle" });
    // Let the runtime finish arriving before navigating again: leaving while
    // dotnet.native.wasm is still in flight cancels it, and a cancelled download
    // is a console error that means nothing.
    await page
      .waitForFunction(
        () => performance.getEntriesByType("resource")
          .some((r) => /dotnet\.native\..*\.wasm$/.test(r.name) && r.responseEnd > 0),
        null, { timeout: 60_000 })
      .catch(() => {});
    await page.waitForTimeout(500);
    await page.goto(url, { waitUntil: "networkidle" });
    await page.waitForTimeout(1200);
  };

  // Navigating away always leaves something half-fetched; those are not the
  // errors worth failing on. A component that threw is.
  const NOISE = /Failed to fetch|negotiation|instantiate_wasm_module|ERR_ABORTED|Failed to load resource|Fetch API cannot load|dotnet\.native|_framework\//i;

  for (const vp of VIEWPORTS) {
    for (const scheme of ["light", "dark"]) {
      const listing = await open(vp, scheme);
      const found = await routes(listing.page);
      await listing.ctx.close();

      for (const [name, url] of found) {
        const where = `${name} ${vp.width}/${scheme}`;
        const { ctx, page, errors } = await open(vp, scheme);
        await visit(page, url);
        const m = await measure(page);
        const real = errors.filter((e) => !NOISE.test(e));
        if (real.length) note(where, `console error: ${real[0]}`);
        await page.screenshot({ path: `${shots}/${name}-${vp.width}-${scheme}.png`, fullPage: true });

        if (vp.touch && !m.coarse)
          throw new Error(`${where}: touch emulation lapsed — this run is not measuring a phone`);

        if (m.overflow > 0) note(where, `${m.overflow}px of horizontal overflow`);
        if (m.headings === 0) note(where, "no <h1> — FocusOnNavigate has nothing to focus");
        if (vp.touch) {
          if (m.small.length) note(where, `under ${TAP}px: ${m.small.join(", ")}`);
          if (m.smallFont.length) note(where, `field under ${FONT}px: ${m.smallFont.join(", ")}`);
        }
        if (name === "pages") {
          if (m.rowMenuVisible === null) note(where, "no row menu in the tree at all");
          else if (vp.touch && !m.rowMenuVisible) note(where, "row menu still hover-gated on touch");
          else if (!vp.touch && m.rowMenuVisible) note(where, "row menu no longer hover-gated");
        }
        if ((name === "node-read" || name === "node-code") && m.canvasInRead > 0)
          note(where, "a canvas is being rendered to read a page");
        if (name === "node-read" && vp.touch && m.floatedAsides > 0)
          note(where, `${m.floatedAsides} aside(s) still floated on a phone`);
        await ctx.close();
      }

      // The drawer: only below the breakpoint, and it must not survive a navigation.
      const { ctx, page } = await open(vp, scheme);
      if (vp.touch) {
        await page.goto("/pages", { waitUntil: "networkidle" });
        await page.click(".nav-toggle");
        await page.waitForTimeout(300);
        const open = await page.evaluate(() =>
          document.querySelector(".sidebar")?.matches(":popover-open") ?? false);
        if (!open) note(`drawer ${vp.width}/${scheme}`, "did not open");
        await page.screenshot({ path: `${shots}/drawer-${vp.width}-${scheme}.png` });

        const link = page.locator(".sidebar a").first();
        if (await link.count()) {
          await link.click();
          await page.waitForTimeout(400);
          const stillOpen = await page.evaluate(() =>
            document.querySelector(".sidebar")?.matches(":popover-open") ?? false);
          if (stillOpen) note(`drawer ${vp.width}/${scheme}`, "stayed open across a navigation");
        }
      } else {
        const toggleShown = await page.evaluate(() => {
          const t = document.querySelector(".nav-toggle");
          return t ? getComputedStyle(t).display !== "none" : false;
        });
        if (toggleShown) note(`chrome ${vp.width}/${scheme}`, "drawer handle shown beside a permanent sidebar");
      }

      await ctx.close();
    }
  }

  await browser.close();
  if (failures.length) {
    console.error(`\n${failures.length} failure(s):\n` + failures.map((f) => `  ${f}`).join("\n"));
    process.exit(1);
  }
  console.log(`\nAll checks pass. Screenshots in ${shots}/.`);
}

main().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
