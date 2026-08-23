export function registerSearchShortcut(dotnet) {
  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
      e.preventDefault();
      dotnet.invokeMethodAsync('Open');
    }
  });
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

// The slopedit editor paints to canvas, so CSS theming can't reach it; the
// editor island needs to be told which mode is in effect and when it changes.
// Neither MutationObserver (the toggle writes data-theme) nor the OS
// preference's change event is reachable from Blazor. Returns the current
// mode; pushes every later change into OnThemeChanged.
// The read view's Contents panel. DocumentHtmlView emits h1-h6 without ids — the
// document's own numbering is block indices, which mean nothing to the DOM — so a
// heading is addressed by its position among the emitted headings. Blazor has no
// native way to reach the nth descendant of an element and scroll it into view,
// which is the only reason this is here rather than in C#.
export function scrollToHeading(container, index) {
  const headings = container?.querySelectorAll('h1, h2, h3, h4, h5, h6');
  headings?.[index]?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

export function watchTheme(dotnet) {
  const root = document.documentElement;
  const media = matchMedia('(prefers-color-scheme: dark)');
  const dark = () => (root.dataset.theme ?? (media.matches ? 'dark' : 'light')) === 'dark';
  const notify = () => dotnet.invokeMethodAsync('OnThemeChanged', dark());
  new MutationObserver(notify).observe(root, { attributeFilter: ['data-theme'] });
  media.addEventListener('change', notify);
  return dark();
}

// The static header chrome can't be Blazor-native: the theme must come out of
// localStorage before the first frame, long before any circuit or runtime
// exists, and both handlers must survive enhanced navigation's DOM patching.
export function initChrome() {
  const root = document.documentElement;
  const saved = localStorage.getItem('gatherum-theme');
  if (saved === 'light' || saved === 'dark') root.dataset.theme = saved;
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
