import { chromium } from "playwright";

// `npx playwright install chromium` is the normal way to get a browser, but plenty of
// environments already ship one (CI images, dev containers) and Playwright will only
// use the exact build its own version pins. GATHERUM_CHROMIUM points at whatever is
// already there.
export function launch() {
  const executablePath = process.env.GATHERUM_CHROMIUM;
  return chromium.launch(executablePath ? { executablePath } : {});
}
