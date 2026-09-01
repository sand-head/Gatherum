# Vendored cores

A second way for a cartridge to play: an emulator somebody else wrote, compiled to
WebAssembly and driven from the same `IEmulatorCore` seam as the consoles written in C#.

The C# cores stay. They are the ones Gatherum can guarantee — no fetch, no toolchain, no
licence to honour but its own — and they cover the four machines simple enough to write
from scratch. This is for the ones that are not.

## The two halves

**`core-shim/` is Rust, and it lives here.** It is the only code in this directory you
will read or change often. libretro — the interface most emulator cores already speak —
is built out of function pointers: the host hands the core six callbacks and the core
calls back into them. JavaScript cannot manufacture a WebAssembly function pointer, so a
host written purely in JavaScript cannot get as far as `retro_init`. The shim is the
piece on the other side of that wall, compiled into the same module as the core, where a
callback is an ordinary function. It exports a flat surface of plain integers shaped like
`IEmulatorCore`: load, run, a packed framebuffer, interleaved sound, save states, and the
memory a battery would have kept.

It is `no_std`. The core brings a libc with it and a second copy from Rust's standard
library would be one too many, so the shim works in fixed buffers and borrows the core's
own allocator. Nothing in it knows which core it is linked against; any libretro core
will do, and the two here were built against the same shim without a line changed.

`bsnes-support/libco-extras.c` is the one exception, and it is small on purpose: two
coroutine calls and a clock that bsnes needs and its Emscripten backend does not
implement. It is a translation unit of its own rather than a patch, so the core's source
stays byte-for-byte what its licence points at.

**The cores themselves are not here, and never will be.** `build-core.sh` fetches each at
a pinned commit into `build/`, compiles it against a pinned toolchain, links the shim, and
leaves the result in `dist/`. Both directories are gitignored. That is the same bargain
`models/` already strikes for the embedding model: fetched by the build, verified against
a known hash or commit, never committed, and never downloaded at run time.

## Two toolchains, because the cores differ

| | mGBA | bsnes |
| --- | --- | --- |
| Toolchain | WASI SDK 25 | Emscripten 3.1.74 |
| Output | `dist/mgba.wasm` (1.6 MB) | `dist/bsnes.wasm` (2.3 MB) + `dist/bsnes.mjs` (84 KB) |
| Imports | ~14 WASI calls | Emscripten's own |
| Machines | Game Boy Advance | Super Nintendo |

mGBA is plain C, single-threaded, no exceptions, software-rendered. That profile compiles
straight against WASI and comes out as one module importing a handful of system calls and
containing no JavaScript at all. It is also a narrower profile than most cores have.

bsnes does not fit it. It runs its processor, its sound chip and its picture as
*coroutines* — each chip is a thread that runs until it catches up with the others — and
it throws C++ exceptions. WASI's libc++ ships without exception support and there is no
stack-switching primitive to build libco on, so no amount of configuration gets bsnes
through that toolchain. Emscripten has both: real exception support, and Asyncify, which
implements a coroutine swap by unwinding the WebAssembly stack out to JavaScript and
rewinding it back in.

The price is that Emscripten emits a loader in JavaScript beside the module, and it has to
be shipped. That is compiler output for a vendored core rather than a JavaScript library —
the same category as `dotnet.native.js`, which Blazor's own runtime has always been —
and `DECISIONS.md` records why the line was drawn there. It is not licence to reach for a
JavaScript dependency, and `wwwroot/js/gatherum.js` is still the only hand-written
JavaScript in the project.

One consequence of Asyncify leaks into the shim and is worth knowing before reading it:
**a value returned across a fiber swap does not survive the trip.** The function body runs
to completion, every side effect happens, and the caller is handed a zero. So anything
that might swap — measuring a save state, writing one — parks its answer in a static, and
a second call that cannot swap reads it back.

## Building

```sh
./native/build-core.sh          # both cores
./native/build-core.sh bsnes    # just one
```

Needs `curl`, `git`, `make`, and a Rust toolchain with both wasm targets:

```sh
rustup target add wasm32-wasip1 wasm32-unknown-emscripten
```

The WASI SDK and the Emscripten SDK it fetches itself, once each, into `build/`.
Everything after the first run is incremental; delete `native/build/` to start over.
bsnes takes about twenty minutes from cold and dominates the build.

## What the host has to supply

For mGBA, around fourteen WASI functions. Almost all of them are filesystem calls that
can honestly answer "there is no filesystem" — a browser hands the cartridge in from
memory. One deserves care, and it applies to *every* core however it was built:

> **A core must never learn what time it is.** A core that reads a wall clock cannot stay
> in step with a copy of itself in somebody else's browser, and netplay here is two
> machines running the same frames from the same buttons. mGBA asks WASI for
> `clock_time_get`, and the host answers with a counter that advances by one frame per
> frame. bsnes calls `clock()` to seed its randomness, and `libco-extras.c` answers zero.
> This is the single easiest way to break determinism without noticing, because it desyncs
> quietly, minutes in.

bsnes brings its own host: Emscripten's loader supplies the environment, and Gatherum
gives it a cartridge through an in-memory filesystem, because bsnes is one of the cores
that insists on opening the file itself rather than reading it out of memory. The shim's
`gatherum_needs_path` is how the host finds out which kind it has.

It also has to be told not to fill memory with noise at power-on. bsnes does that by
default — it is faithful to the hardware, and fatal to two people whose consoles must
start life identical — so the host sets the core option `bsnes_entropy` to `None` before
booting. Without it two machines diverge inside a second.

## Licences

Gatherum is AGPL-3.0-or-later, so anything linked into it has to be compatible with that.

**mGBA is MPL-2.0**, and — this is the part that matters — its source files carry the
plain notice rather than the "Incompatible With Secondary Licenses" one from Exhibit B.
Under MPL 2.0 §3.3 that permits distributing the combined work under a Secondary License,
AGPL-3.0 among them.

**bsnes is GPL-3.0-or-later**, which AGPL-3.0 explicitly accommodates: §13 of each licence
permits linking works under the other. The copy built here is the libretro port by way of
EmulatorJS, who wrote libco's Emscripten fiber backend.

The obligation both bring back is the same: keep their notices intact and make their
source available. Pinning the exact upstream commit in `build-core.sh`, and never patching
what is fetched, is how that is met.

Not every core is so obliging. Before adding one, read its licence rather than its README:

| Core | Licence | Usable here |
| --- | --- | --- |
| mGBA | MPL-2.0 | Yes — built |
| bsnes | GPL-3.0-or-later | Yes — built |
| Beetle / Mednafen | GPL-2.0-or-later | Yes, upgraded to v3 |
| Mupen64Plus-Next | GPL-2.0-or-later | Yes, upgraded to v3 |
| SameBoy | MIT | Yes |
| Gambatte | GPL-2.0-**only** | No — incompatible with AGPL-3.0 |
| Snes9x | custom, non-commercial | No — not free software |
| Genesis Plus GX | custom, non-commercial | No — same |

## Adding another core

Most of the work is already done, because the shim is not specific to any core.

1. Read its licence and add it to the table above, having actually read it. A core whose
   licence does not fit cannot be added whatever else is true of it.
2. Pin its repository and commit at the top of `build-core.sh`, and give it a
   `build_<name>` function beside the two there.
3. Decide which toolchain it needs. Plain C with no threads, no coroutines and no
   exceptions goes to WASI and comes out glue-free; anything else goes to Emscripten.
   Check the module's imports either way: on the WASI side, anything beyond WASI is a
   source file you meant to compile and did not.
4. Answer its clock with a counter, never the time.
5. Teach `Emulator.Identify` its bytes and `VendoredCore` its descriptor. Report one
   player unless you have *measured* that two copies of it stay in step — same cartridge,
   same buttons, byte-identical states — because a vendored core's determinism is
   somebody else's claim rather than this project's promise.
