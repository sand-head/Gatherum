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

// The ROM player's sound. Everything else about the player is C# — the processor, the
// picture chip, the sound chip that produces these samples — but there is no way to
// hand a waveform to a speaker from .NET: Web Audio has no binding, and a page cannot
// play something it has not been asked to play, so the context is created on the click
// that starts the game and nowhere else.
//
// Each frame's samples become one short buffer scheduled at the end of the last, which
// keeps the sound continuous without a worklet (a worklet is a second script file, and
// there is only one script here).
let emulatorAudio;

export function startEmulatorAudio(sampleRate) {
  stopEmulatorAudio();
  const Context = window.AudioContext ?? window.webkitAudioContext;
  if (!Context) return false;
  const context = new Context();
  context.resume();
  emulatorAudio = { context, rate: sampleRate, cursor: 0 };
  return true;
}

export function stopEmulatorAudio() {
  if (!emulatorAudio) return;
  emulatorAudio.context.close();
  emulatorAudio = undefined;
}

export function queueEmulatorAudio(bytes, valueCount, channels) {
  if (!emulatorAudio || valueCount <= 0) return;
  const { context, rate } = emulatorAudio;
  const ears = channels || 1;
  // A stereo core hands over its two ears interleaved, so the count of values is a
  // multiple of the channel count rather than the number of moments of sound.
  const frames = Math.floor(valueCount / ears);
  if (frames <= 0) return;
  const buffer = context.createBuffer(ears, frames, rate);
  for (let ear = 0; ear < ears; ear++) {
    const channel = buffer.getChannelData(ear);
    // Signed sixteen-bit, little-endian, read a byte at a time: a typed-array view
    // would need the interop buffer to be two-byte aligned, which is not promised.
    for (let i = 0; i < frames; i++) {
      const at = (i * ears + ear) * 2;
      const raw = bytes[at] | (bytes[at + 1] << 8);
      channel[i] = (raw >= 32768 ? raw - 65536 : raw) / 32768;
    }
  }
  const source = context.createBufferSource();
  source.buffer = buffer;
  source.connect(context.destination);
  // Stay a breath ahead of the clock. Falling behind means the cursor is in the past,
  // which would play everything at once; catching up by skipping is the only cure.
  const now = context.currentTime;
  if (emulatorAudio.cursor < now + 0.02) emulatorAudio.cursor = now + 0.08;
  source.start(emulatorAudio.cursor);
  emulatorAudio.cursor += frames / rate;
}

// A cartridge's battery-backed memory. It is the player's own, not the wiki's: the ROM
// is a file every reader shares and a save is one person's afternoon, so it lives in
// the browser that made it — the same place an anonymous reader's place in a book does.
// The player offers it as a file to download, which is the way it leaves this browser.
export function readEmulatorSave(nodeId) {
  try {
    return localStorage.getItem('gatherum-save-' + nodeId);
  } catch {
    return null;
  }
}

export function writeEmulatorSave(nodeId, base64) {
  try {
    localStorage.setItem('gatherum-save-' + nodeId, base64);
  } catch {
    // Storage refused or is full. The game keeps its save in memory for this sitting;
    // the download button is what gets it out.
  }
}

// ---- A vendored emulator core ----------------------------------------------------
//
// Some machines are too big to write from scratch, so their core is somebody else's,
// compiled to WebAssembly and fetched at build time (see native/README.md).
//
// There are two shapes of module and the difference is the core's own doing. A core
// that is plain C with no threads and no exceptions compiles against WASI and arrives
// as one bare module: everything it needs from a host is below, and everything it
// offers is a function taking integers. A core built out of coroutines cannot, so it
// compiles against Emscripten and arrives with the loader Emscripten emits beside it.
// Past `openCore` neither the rest of this file nor anything in C# can tell which it
// got: both answer `bytes()` with a view of the core's heap and `exports` with the same
// flat surface, because both link the same shim.
//
// Two heaps are in play — the core's and the .NET runtime's — and the frame has to
// cross between them every sixteen milliseconds. Blazor hands out the address of a
// pinned array and this copies into it directly, which is one memcpy rather than a
// marshalled round trip.

let core;
let coreUrl;

/// The clock the core is allowed to see. It counts frames, not time: two people playing
/// the same cartridge run the same frames from the same buttons, and a core that read a
/// real clock would drift apart from its twin with nothing to show for it.
let coreFrameClock = 0n;

const NANOSECONDS_PER_FRAME = 16666667n;

/// Every file the core asks for is not there. A browser has no filesystem to offer and
/// does not need one: the cartridge is handed in from memory and the save comes back the
/// same way. EBADF is the honest answer.
const NO_FILE = 8;

function coreHost(memoryOf) {
  const bytes = () => new Uint8Array(memoryOf().buffer);
  const words = () => new DataView(memoryOf().buffer);
  return {
    wasi_snapshot_preview1: {
      clock_time_get: (_id, _precision, out) => {
        words().setBigUint64(out, coreFrameClock, true);
        return 0;
      },
      fd_close: () => NO_FILE,
      fd_fdstat_get: () => NO_FILE,
      fd_filestat_get: () => NO_FILE,
      fd_filestat_set_size: () => NO_FILE,
      fd_prestat_dir_name: () => NO_FILE,
      fd_prestat_get: () => NO_FILE,
      fd_read: () => NO_FILE,
      fd_seek: () => NO_FILE,
      fd_sync: () => NO_FILE,
      fd_tell: () => NO_FILE,
      // Anything the core prints goes to the console, where a person debugging it can
      // find it; nothing else reads it.
      fd_write: (fd, iovs, count, out) => {
        let written = 0;
        let text = "";
        for (let i = 0; i < count; i++) {
          const at = words().getUint32(iovs + i * 8, true);
          const length = words().getUint32(iovs + i * 8 + 4, true);
          text += new TextDecoder().decode(bytes().subarray(at, at + length));
          written += length;
        }
        if (text.trim()) console.warn("emulator core:", text.trim());
        words().setUint32(out, written, true);
        return 0;
      },
      path_open: () => NO_FILE,
      proc_exit: () => { throw new Error("The emulator core gave up."); },
    },
    // Two number-formatting helpers from the one source file of the core's that wants a
    // POSIX locale. Nothing on the emulation path calls them.
    env: {
      ftostr_u: () => 0,
      strtof_u: () => 0,
    },
  };
}

/// The shim's whole surface. Naming it here rather than reaching for whatever a module
/// happens to export is what lets the two kinds be flattened into one: Emscripten spells
/// an exported C function with a leading underscore and a bare module does not.
const CORE_CALLS = [
  "gatherum_set_option", "gatherum_alloc", "gatherum_free",
  "gatherum_needs_path", "gatherum_load", "gatherum_load_path", "gatherum_unload",
  "gatherum_boot", "gatherum_reset", "gatherum_run",
  "gatherum_frame_ptr", "gatherum_frame_width", "gatherum_frame_height",
  "gatherum_audio_ptr", "gatherum_audio_len", "gatherum_set_buttons",
  "gatherum_fps", "gatherum_sample_rate",
  "gatherum_measure_state", "gatherum_state_size", "gatherum_state_save",
  "gatherum_state_load", "gatherum_state_ok",
  "gatherum_sram_ptr", "gatherum_sram_len",
];

function flattened(source, prefix) {
  const calls = {};
  for (const name of CORE_CALLS) calls[name] = source[prefix + name].bind(source);
  return calls;
}

/// Fetches and starts a module, whichever shape it is. What comes back is the same
/// either way: the shim's surface, a look at the core's heap, and — for the kind that
/// has one — the in-memory filesystem a core that opens its own cartridge will need.
async function openCore(url) {
  if (url.endsWith(".mjs")) {
    const module = await (await import(url)).default();
    // Emscripten replaces its heap rather than resizing it when it grows, so the view
    // has to be asked for again every time rather than kept.
    return { exports: flattened(module, "_"), bytes: () => module.HEAPU8, fs: module.FS };
  }
  const response = await fetch(url);
  if (!response.ok) return null;
  let memory;
  const source = await WebAssembly.instantiate(
    await response.arrayBuffer(), coreHost(() => memory));
  const exports = source.instance.exports;
  memory = exports.memory;
  exports._initialize?.();
  return { exports: flattened(exports, ""), bytes: () => new Uint8Array(memory.buffer) };
}

/// Writes a string into the core's heap and leaves it there. A core keeps the pointers
/// it is given for as long as it runs, so this is memory deliberately never freed.
function planted(text) {
  const encoded = new TextEncoder().encode(text + "\0");
  const address = core.exports.gatherum_alloc(encoded.length);
  if (address) core.bytes().set(encoded, address);
  return address;
}

/// Starts a core, first telling it anything it must know before it powers on.
///
/// `settings` is a flat list of names and values. Most of a core's options are taste;
/// the ones that come through here are not. bsnes fills memory with noise at power-on
/// unless told otherwise, which is faithful to the hardware and fatal to two people
/// whose consoles have to start life identical.
export async function loadEmulatorCore(url, settings) {
  if (core && coreUrl === url) return true;
  core = undefined;
  coreUrl = undefined;
  try {
    const opened = await openCore(url);
    if (!opened) return false;
    core = opened;
    for (let at = 0; at + 1 < (settings?.length ?? 0); at += 2) {
      core.exports.gatherum_set_option(planted(settings[at]), planted(settings[at + 1]));
    }
    core.exports.gatherum_boot();
    coreUrl = url;
    return true;
  } catch (error) {
    console.warn("The emulator core would not load:", error);
    core = undefined;
    return false;
  }
}

/// Hands the cartridge over.
///
/// Most cores read it out of memory, and keep the bytes rather than copying them, so
/// they are allocated out of the core's own heap and freed when the game is unloaded.
/// A few insist on opening the file themselves and ignore a buffer entirely — reporting
/// success either way, so a host that guesses wrong gets a console that loaded nothing
/// and said nothing. `gatherum_needs_path` is how it says which it is, and the answer
/// for one of those is a file in a filesystem that exists only in this tab.
export function loadEmulatorCartridge(rom, extension) {
  if (!core) return null;
  if (core.exports.gatherum_needs_path()) {
    if (!core.fs) return null;
    const path = "/cartridge" + extension;
    core.fs.writeFile(path, rom);
    if (!core.exports.gatherum_load_path(planted(path))) return null;
  } else {
    const address = core.exports.gatherum_alloc(rom.length);
    if (!address) return null;
    core.bytes().set(rom, address);
    if (!core.exports.gatherum_load(address, rom.length)) return null;
  }

  coreFrameClock = 0n;
  core.exports.gatherum_run();
  core.exports.gatherum_measure_state();
  return {
    width: core.exports.gatherum_frame_width(),
    height: core.exports.gatherum_frame_height(),
    fps: core.exports.gatherum_fps(),
    sampleRate: core.exports.gatherum_sample_rate(),
    stateSize: core.exports.gatherum_state_size(),
    saveSize: core.exports.gatherum_sram_len(),
  };
}

export function runEmulatorCore(first, second) {
  if (!core) return;
  core.exports.gatherum_set_buttons(0, first);
  core.exports.gatherum_set_buttons(1, second);
  coreFrameClock += NANOSECONDS_PER_FRAME;
  core.exports.gatherum_run();
}

export function resetEmulatorCore() {
  if (core) core.exports.gatherum_reset();
}

export function unloadEmulatorCartridge() {
  if (core) core.exports.gatherum_unload();
}

/// The .NET heap, fetched fresh each time: it moves when the runtime grows it, and a
/// view kept from last frame would be pointing at a buffer nobody owns any more.
function dotnetHeap() {
  const runtime = globalThis.getDotnetRuntime && globalThis.getDotnetRuntime(0);
  return runtime && runtime.localHeapViewU8 && runtime.localHeapViewU8();
}

function copyOut(source, length, address) {
  const heap = dotnetHeap();
  if (!heap) return 0;
  heap.set(core.bytes().subarray(source, source + length), address);
  return length;
}

export function readEmulatorCoreFrame(address, bytes) {
  if (!core) return 0;
  return copyOut(core.exports.gatherum_frame_ptr(), bytes, address);
}

export function readEmulatorCoreAudio(address, capacity) {
  if (!core) return 0;
  const values = Math.min(core.exports.gatherum_audio_len(), capacity);
  if (values <= 0) return 0;
  copyOut(core.exports.gatherum_audio_ptr(), values * 2, address);
  return values;
}

// A save state is worked out in two calls rather than one, and so is the verdict on
// whether it came off. A core whose chips are coroutines may switch fibers in the
// middle of serializing, and where those fibers are the browser's, switching unwinds
// the stack out to JavaScript and rewinds it back — the work all happens, but the
// return value does not survive the trip. So the shim parks its answer and a second
// call that cannot switch fetches it.
export function saveEmulatorCoreState(address, bytes) {
  if (!core) return false;
  const scratch = core.exports.gatherum_alloc(bytes);
  if (!scratch) return false;
  core.exports.gatherum_state_save(scratch, bytes);
  // A WebAssembly export has no booleans in it, so what comes back is 0 or 1 and the
  // coercion is what makes it a boolean the runtime on the other side will accept.
  const saved = !!core.exports.gatherum_state_ok();
  if (saved) copyOut(scratch, bytes, address);
  core.exports.gatherum_free(scratch);
  return saved;
}

export function loadEmulatorCoreState(address, bytes) {
  if (!core) return false;
  const scratch = core.exports.gatherum_alloc(bytes);
  if (!scratch) return false;
  const heap = dotnetHeap();
  if (!heap) { core.exports.gatherum_free(scratch); return false; }
  core.bytes().set(heap.subarray(address, address + bytes), scratch);
  core.exports.gatherum_state_load(scratch, bytes);
  const loaded = !!core.exports.gatherum_state_ok();
  core.exports.gatherum_free(scratch);
  return loaded;
}

export function readEmulatorCoreSave(address) {
  if (!core) return 0;
  const length = core.exports.gatherum_sram_len();
  const source = core.exports.gatherum_sram_ptr();
  if (!length || !source) return 0;
  return copyOut(source, length, address);
}

export function writeEmulatorCoreSave(address, bytes) {
  if (!core) return false;
  const length = core.exports.gatherum_sram_len();
  const target = core.exports.gatherum_sram_ptr();
  const heap = dotnetHeap();
  if (!length || !target || !heap) return false;
  const taken = Math.min(length, bytes);
  core.bytes().set(heap.subarray(address, address + taken), target);
  return true;
}

/// A cheap fingerprint of the cartridge's battery memory, computed where the memory
/// already lives. The player watches this to decide whether a save is worth writing, and
/// copying 128 KB into .NET every second only to hash it would be the expensive way to
/// answer a question that is usually "nothing changed".
export function fingerprintEmulatorCoreSave() {
  if (!core) return 0;
  const length = core.exports.gatherum_sram_len();
  const source = core.exports.gatherum_sram_ptr();
  if (!length || !source) return 0;
  const memory = core.bytes();
  let hash = 2166136261;
  // Every byte of a megabyte-and-a-bit is more than this needs; a stride catches the
  // writes a game actually makes without walking the whole array sixty times a minute.
  for (let at = 0; at < length; at += 17) {
    hash = Math.imul(hash ^ memory[source + at], 16777619);
  }
  return hash >>> 0;
}
