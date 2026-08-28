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

// One book on screen at a time, like the search box: registering replaces the
// listener, registering nothing removes it. A chapter's frame is sandboxed to an
// opaque origin, so a cross-chapter link in the book can only *ask* to be followed —
// a postMessage the frame's pager sends — and only the frame actually on screen is
// believed.
let epubListener;

let epubRelay = [];

export function registerEpubReader(frame, dotnet) {
  if (epubListener) removeEventListener('message', epubListener);
  epubListener = null;
  for (const [type, handler, options] of epubRelay)
    document.removeEventListener(type, handler, options);
  epubRelay = [];
  if (!frame) return;

  epubListener = (e) => {
    if (e.source !== frame.contentWindow) return;
    const chapter = e.data?.gatherumEpubChapter;
    if (Number.isInteger(chapter)) dotnet.invokeMethodAsync('OnChapterLinked', chapter);
    // The pager reports how far through the chapter the reader is; the component
    // turns that into the position the server remembers.
    const progress = e.data?.gatherumEpubProgress;
    if (typeof progress === 'number' && Number.isFinite(progress))
      dotnet.invokeMethodAsync('OnProgress', progress);
  };
  addEventListener('message', epubListener);

  // The swipe relay. A touch delivered into the chapter's document is handled
  // there and never bubbles out, so these listeners cannot fire for it — which
  // makes them a pure fallback: they only ever see a swipe the browser refused to
  // route into the frame (iOS has been observed doing exactly that to a sandboxed
  // frame it happily renders). A horizontal drag that starts over the reader and
  // lands here instead is claimed the same way the pager claims one — classified
  // on the first move, held with preventDefault — and streamed to the pager over
  // postMessage, the one channel the sandbox always leaves open.
  let touch = null;
  const post = (data) => frame.contentWindow.postMessage(data, '*');
  const relay = (type, options, handler) => {
    document.addEventListener(type, handler, options);
    epubRelay.push([type, handler, options]);
  };
  relay('touchstart', { capture: true, passive: true }, (e) => {
    touch = null;
    if (e.touches.length !== 1) return;
    const at = e.touches[0];
    const box = frame.getBoundingClientRect();
    if (at.clientX < box.left || at.clientX > box.right
      || at.clientY < box.top || at.clientY > box.bottom) return;
    touch = { x: at.clientX, y: at.clientY, t: Date.now(), on: false };
  });
  relay('touchmove', { capture: true, passive: false }, (e) => {
    if (!touch) return;
    if (e.touches.length !== 1) { touch = null; return; }
    const dx = e.touches[0].clientX - touch.x, dy = e.touches[0].clientY - touch.y;
    if (!touch.on) {
      if (Math.abs(dx) <= Math.abs(dy)) { touch = null; return; }
      touch.on = true;
    }
    if (e.cancelable) e.preventDefault();
    post({ gatherumEpubDrag: dx });
  });
  relay('touchend', { capture: true, passive: true }, (e) => {
    if (!touch) return;
    const { x, t, on } = touch;
    touch = null;
    if (!on) return;
    const dx = e.changedTouches[0].clientX - x;
    post({ gatherumEpubSettle: { dx, flick: Date.now() - t < 300 && Math.abs(dx) > 32 } });
  });
  relay('touchcancel', { capture: true, passive: true }, () => {
    if (!touch) return;
    touch = null;
    post({ gatherumEpubSettle: { cancel: true } });
  });
}

// The chapter rides into its frame as srcdoc rather than by navigation. A
// network-src sandboxed frame is a cross-origin document, and iOS has been seen
// withholding the raw touch stream from exactly those — taps still arrive, because
// click synthesis is a separate pipeline — while a srcdoc document stays with its
// parent and demonstrably receives touches on the affected hardware. The saved
// fraction and the debug flag ride behind it as messages, since a srcdoc document
// has no URL to carry them. Written here rather than bound in Blazor: a chapter
// with its images folded in runs to megabytes, which is no string to diff.
export function loadEpubChapter(frame, html, restore, debug) {
  frame.addEventListener('load', () => {
    if (debug) frame.contentWindow.postMessage({ gatherumEpubDebug: true }, '*');
    if (restore > 0) frame.contentWindow.postMessage({ gatherumEpubRestore: restore }, '*');
  }, { once: true });
  frame.srcdoc = html;
}

// An anonymous reader's ribbon. A signed-in reader's place lives on the server — any
// device, either user — but a visitor on a public book is deliberately never
// remembered there (no write is anonymous), so their place lives in the one place
// that is theirs alone: their own browser. Guarded, because storage can be blocked
// entirely — a browser that refuses to remember was asked not to.
export function readEpubPosition(nodeId) {
  try {
    const stored = JSON.parse(localStorage.getItem('gatherum-epub-' + nodeId));
    return Number.isInteger(stored?.chapter) && typeof stored?.progress === 'number'
      ? stored
      : null;
  } catch {
    return null;
  }
}

export function saveEpubPosition(nodeId, chapter, progress) {
  try {
    localStorage.setItem('gatherum-epub-' + nodeId, JSON.stringify({ chapter, progress }));
  } catch {
    // Nothing to do: the reader finds their own page, like a book with no ribbon.
  }
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
  const heading = container?.querySelectorAll('h1, h2, h3, h4, h5, h6')?.[index];
  if (!heading) return;
  // On a narrow page the read view folds each h2 section into a <details>, and an
  // element inside a closed one has no box to scroll to — so a jump unfolds the
  // sections on its way, which is also what the reader asked for.
  for (let fold = heading.closest('details'); fold; fold = fold.parentElement?.closest('details'))
    fold.open = true;
  heading.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
