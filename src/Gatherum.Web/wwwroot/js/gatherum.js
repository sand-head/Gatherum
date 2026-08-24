// There is one search box, so there is one listener: registering replaces it, and
// registering nothing removes it. The shortcut focuses the field directly rather
// than calling back into .NET — on a server circuit the round trip is long enough
// to swallow the first characters typed after it.
let searchHotkey;

export function registerSearchShortcut(input) {
  if (searchHotkey) document.removeEventListener('keydown', searchHotkey);
  searchHotkey = null;
  if (!input) return;

  searchHotkey = (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
      e.preventDefault();
      input.focus();
      input.select();
    }
  };
  document.addEventListener('keydown', searchHotkey);
}

export function initDropZone(element, dotnet) {
  const stop = (e) => { e.preventDefault(); e.stopPropagation(); };
  ['dragenter', 'dragover'].forEach((name) =>
    element.addEventListener(name, (e) => { stop(e); element.classList.add('drop-active'); }));
  ['dragleave', 'drop'].forEach((name) =>
    element.addEventListener(name, (e) => { stop(e); element.classList.remove('drop-active'); }));

  element.addEventListener('drop', async (e) => {
    const files = [...e.dataTransfer.files];
    if (files.length === 0) return;
    for (const file of files) {
      const body = new FormData();
      body.append('file', file);
      await fetch('/api/files', { method: 'POST', body, credentials: 'same-origin' });
    }
    dotnet.invokeMethodAsync('OnFilesDropped');
  });
}

// The read view's Contents panel. DocumentHtmlView emits h1-h6 without ids — the
// document's own numbering is block indices, which mean nothing to the DOM — so a
// heading is addressed by its position among the emitted headings. Blazor has no
// native way to reach the nth descendant of an element and scroll it into view,
// which is the only reason this is here rather than in C#.
export function scrollToHeading(container, index) {
  const headings = container?.querySelectorAll('h1, h2, h3, h4, h5, h6');
  headings?.[index]?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

// Which mode is in effect: the explicit choice when there is one, the OS
// preference otherwise. The two are one question everywhere and nowhere else can
// ask it — data-theme lives on <html> and the preference lives in the browser.
const darkMedia = matchMedia('(prefers-color-scheme: dark)');
const darkNow = () =>
  (document.documentElement.dataset.theme ?? (darkMedia.matches ? 'dark' : 'light')) === 'dark';

// The slopedit editor paints to canvas, so CSS theming can't reach it; the
// editor island needs to be told which mode is in effect and when it changes.
// Neither MutationObserver (the toggle writes data-theme) nor the OS
// preference's change event is reachable from Blazor. Returns the current
// mode; pushes every later change into OnThemeChanged.
export function watchTheme(dotnet) {
  const notify = () => dotnet.invokeMethodAsync('OnThemeChanged', darkNow());
  new MutationObserver(notify).observe(document.documentElement, { attributeFilter: ['data-theme'] });
  darkMedia.addEventListener('change', notify);
  return darkNow();
}

// Tell the server which mode the reader is looking at, because the server renders
// part of what they are looking at and cannot otherwise know: slopedit's HTML view
// bakes a theme's colors into the stylesheet it emits rather than reaching for CSS
// variables, so a prerendered article is painted in whichever mode the server
// assumed, and a wrong assumption is a white page until the island goes interactive.
//
// A cookie rather than localStorage's key because what the server needs is the
// *color*, not the choice: "system" is not something a prerender can paint, and
// which of the two the reader picked is none of its business.
function publishMode() {
  document.cookie =
    `gatherum-mode=${darkNow() ? 'dark' : 'light'};path=/;max-age=31536000;samesite=lax`;
}

// The static header chrome can't be Blazor-native: the theme must come out of
// localStorage before the first frame, long before any circuit or runtime
// exists, and both handlers must survive enhanced navigation's DOM patching.
export function initChrome() {
  const root = document.documentElement;
  const saved = localStorage.getItem('gatherum-theme');
  if (saved === 'light' || saved === 'dark') root.dataset.theme = saved;
  // Only two things move the answer: the toggle below, and — while the reader is on
  // "system" — the OS preference.
  publishMode();
  darkMedia.addEventListener('change', publishMode);
  document.addEventListener('click', (e) => {
    if (e.target.closest('#theme-toggle')) {
      const next = { system: 'light', light: 'dark', dark: 'system' }[root.dataset.theme ?? 'system'];
      if (next === 'system') {
        delete root.dataset.theme;
        localStorage.removeItem('gatherum-theme');
      } else {
        root.dataset.theme = next;
        localStorage.setItem('gatherum-theme', next);
      }
      publishMode();
    }
    // Enhanced navigation patches the DOM without resetting popover state,
    // so close an open account menu when one of its items is followed.
    if (e.target.closest('#account-menu a, #account-menu button'))
      document.getElementById('account-menu')?.hidePopover();
    // The navigation drawer has the same problem and the same cure: a tapped
    // link routes without a page load, so the drawer would stay open over it.
    if (e.target.closest('#nav-drawer a'))
      document.getElementById('nav-drawer')?.hidePopover();
  });
}
