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

// Theme choice can't be Blazor-native: it must read localStorage and set the
// attribute before the first frame, long before any circuit or runtime exists.
export function initTheme() {
  const root = document.documentElement;
  const saved = localStorage.getItem('gatherum-theme');
  if (saved === 'light' || saved === 'dark') root.dataset.theme = saved;
  document.addEventListener('click', (e) => {
    if (!e.target.closest('#theme-toggle')) return;
    const next = { system: 'light', light: 'dark', dark: 'system' }[root.dataset.theme ?? 'system'];
    if (next === 'system') {
      delete root.dataset.theme;
      localStorage.removeItem('gatherum-theme');
    } else {
      root.dataset.theme = next;
      localStorage.setItem('gatherum-theme', next);
    }
  });
}
