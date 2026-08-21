// What a phone actually gets, asserted rather than eyeballed.
//
// Every check here is a regression that shipped: a content pane 148px wide at
// 390px because a 236px sidebar would not move, a row menu no finger could reveal,
// a 14px field that trapped iOS Safari at 1.4x with no way back. The assertions are
// deliberately about measurements and reachability rather than pixels — a
// screenshot diff that fails on a font hint helps nobody, so the shots are written
// for a human and compared by one.
import { chromium } from "playwright";
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

// Anything a user can hit or type into. Chips carry their own small × inside a
// pill that is itself big enough, so they are measured as the chip, not the ×.
const INTERACTIVE = "button, .button, a.tree-link, input, textarea, select";

async function routes(page) {
  const tree = await (await page.request.get("/api/nodes/tree")).json();
  const page1 = tree.find((n) => n.title.startsWith("Homelab"))?.id;
  const file = tree.find((n) => n.title.endsWith(".png"))?.id;
  return [
    ["home", "/"],
    ["pages", "/pages"],
    ["categories", "/categories"],
    ["category-deep", "/categories/Homelab/Networking/VLANs"],
    ["settings", "/settings"],
    ["not-found", "/not-found"],
    ...(page1 ? [["node-read", `/nodes/${page1}`], ["node-edit", `/nodes/${page1}?edit`]] : []),
    ...(file ? [["node-file", `/nodes/${file}`]] : []),
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
      if (box.height < TAP && !el.closest(".category"))
        small.push(`${name} @ ${Math.round(box.height)}px`);
    }
    const rowMenu = document.querySelector(".row-menu");
    return {
      small: [...new Set(small)],
      smallFont: [...new Set(smallFont)],
      overflow: document.documentElement.scrollWidth - window.innerWidth,
      headings: document.querySelectorAll("h1").length,
      rowMenuVisible: rowMenu ? getComputedStyle(rowMenu).visibility === "visible" : null,
      // The canvas must not be what a reader is handed.
      canvasInRead: document.querySelectorAll("canvas").length,
    };
  }, { TAP, FONT, INTERACTIVE });
}

async function main() {
  mkdirSync(shots, { recursive: true });
  const browser = await chromium.launch();
  const failures = [];
  const note = (where, what) => failures.push(`${where}: ${what}`);

  for (const vp of VIEWPORTS) {
    for (const scheme of ["light", "dark"]) {
      const ctx = await browser.newContext({
        baseURL: base,
        viewport: { width: vp.width, height: vp.height },
        deviceScaleFactor: vp.dpr,
        isMobile: vp.touch,
        hasTouch: vp.touch,
        colorScheme: scheme,
      });
      const page = await ctx.newPage();
      await page.goto("/auth/login");

      for (const [name, url] of await routes(page)) {
        const where = `${name} ${vp.width}/${scheme}`;
        await page.goto(url, { waitUntil: "networkidle" });
        const m = await measure(page);
        await page.screenshot({ path: `${shots}/${name}-${vp.width}-${scheme}.png`, fullPage: true });

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
        if (name === "node-read" && m.canvasInRead > 0)
          note(where, "a canvas is being rendered to read a page");
      }

      // The drawer: only below the breakpoint, and it must not survive a navigation.
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
