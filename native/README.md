# Vendored cores

A second way for a cartridge to play: an emulator somebody else wrote, compiled to
WebAssembly and driven from the same `IEmulatorCore` seam as the consoles written in C#.

The C# cores stay. They are the ones Gatherum can guarantee — no fetch, no toolchain, no
licence to honour but its own — and they cover the four machines simple enough to write
from scratch. This is for the ones that are not.

## The two halves

**`core-shim/` is Rust, and it lives here.** It is the only code in this directory you
will ever read or change. libretro — the interface most emulator cores already speak — is
built out of function pointers: the host hands the core six callbacks and the core calls
back into them. JavaScript cannot manufacture a WebAssembly function pointer, so a host
written purely in JavaScript cannot get as far as `retro_init`. The shim is the piece on
the other side of that wall, compiled into the same module as the core, where a callback
is an ordinary function. It exports a flat surface of plain integers shaped like
`IEmulatorCore`: load, run, a packed framebuffer, interleaved sound, save states, and the
memory a battery would have kept.

It is `no_std`. The core brings wasi-libc with it and a second copy from Rust's standard
library would be one too many, so the shim works in fixed buffers and borrows the core's
own allocator. Nothing in it knows it is mGBA; any libretro core that links will do.

**The core itself is not here, and never will be.** `build-core.sh` fetches it at a
pinned commit into `build/`, compiles it against a pinned WASI toolchain, links the shim,
and leaves one file at `dist/mgba.wasm`. Both directories are gitignored. That is the
same bargain `models/` already strikes for the embedding model: fetched by the build,
verified against a known hash, never committed, and never downloaded at run time.

## Building

```sh
./native/build-core.sh
```

Needs `curl`, `git`, and a Rust toolchain with the `wasm32-wasip1` target
(`rustup target add wasm32-wasip1`). The WASI toolchain it fetches itself, once.
Everything after the first run is incremental; delete `native/build/` to start over.

What comes out is about 1.6 MB, imports a handful of WASI calls and nothing else, and
contains no JavaScript at all. It is deliberately **not** an Emscripten build: those ship
a JavaScript glue file, and a vendored JavaScript library is the one thing Gatherum does
not take.

## What the host has to supply

The module imports around fourteen WASI functions. Almost all of them are filesystem
calls that can honestly answer "there is no filesystem" — a browser hands the cartridge
in from memory. One deserves care:

> **`clock_time_get` must not return the time.** A core that reads a wall clock cannot
> stay in step with a copy of itself in somebody else's browser, and netplay here is two
> machines running the same frames from the same buttons. Feed it a counter that advances
> by one frame per frame. This is the single easiest way to break determinism without
> noticing, because it desyncs quietly, minutes in.

## Licences

Gatherum is AGPL-3.0-or-later, so anything linked into it has to be compatible with that.

**mGBA is MPL-2.0**, and — this is the part that matters — its source files carry the
plain notice rather than the "Incompatible With Secondary Licenses" one from Exhibit B.
Under MPL 2.0 §3.3 that permits distributing the combined work under a Secondary License,
AGPL-3.0 among them. The obligation that comes back is to keep mGBA's notices intact and
to make its source available; pinning the exact upstream commit in `build-core.sh`, and
never patching it, is how that is met.

Not every core is so obliging. Before adding one, read its licence rather than its README:

| Core | Licence | Usable here |
| --- | --- | --- |
| mGBA | MPL-2.0 | Yes |
| bsnes | GPL-3.0 | Yes |
| Beetle / Mednafen | GPL-2.0-or-later | Yes, upgraded to v3 |
| SameBoy | MIT | Yes |
| Gambatte | GPL-2.0-**only** | No — incompatible with AGPL-3.0 |
| Snes9x | custom, non-commercial | No — not free software |
| Genesis Plus GX | custom, non-commercial | No — same |

## Adding another core

Most of the work is already done, because the shim is not mGBA-specific.

1. Pin its repository, tag and commit at the top of `build-core.sh`.
2. Work out which of its sources want an operating system underneath them and leave those
   out. The exclusions already there — tests, debugger, PNG, zip, threads, scripting — are
   the usual suspects, and a core is mostly portable once they are gone.
3. Link it against `core-shim` unchanged, and check the module's imports: anything beyond
   WASI is a source file you meant to compile and did not.
4. Add its licence to the table above, having actually read it.
