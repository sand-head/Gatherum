#!/usr/bin/env bash
# Builds the vendored emulator cores into WebAssembly.
#
#   ./build-core.sh            # every core
#   ./build-core.sh mgba       # just one
#
# Nothing this fetches is committed: the cores' sources and the toolchains land in
# native/build/, which is gitignored, and what matters comes out in native/dist/. Run it
# by hand or let CI run it; either way the inputs are pinned by hash or commit and the
# output is reproducible from them.
#
# Two cores, two toolchains, and the difference is the core's own doing rather than a
# preference of ours. mGBA is plain C that wants no operating system underneath it, so it
# compiles against WASI and comes out as one .wasm importing a handful of system calls
# and nothing else. bsnes runs its processor, sound and picture as coroutines and throws
# C++ exceptions, neither of which WASI's libc++ can do, so it compiles against Emscripten
# and comes out as a .wasm plus the loader Emscripten emits to drive it. Both link
# against the same core-shim, unchanged: the shim knows libretro, not who implements it.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build="$here/build"
dist="$here/dist"

# --- what we are pinned to -------------------------------------------------------

MGBA_REPO="https://github.com/mgba-emu/mgba.git"
MGBA_TAG="0.10.3"
MGBA_COMMIT="1c61b54208ca6266129d0f2394c04bd8c44f98c5"

# The libretro fork of bsnes, by way of EmulatorJS — who added libco's Emscripten fiber
# backend, which is the whole reason a coroutine-shaped core can run in a browser at all.
BSNES_REPO="https://github.com/EmulatorJS/bsnes-libretro.git"
BSNES_COMMIT="4b344745e3878e7c0675a60c624582935524b8f7"

WASI_SDK_VERSION="25"
WASI_SDK_URL="https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-${WASI_SDK_VERSION}/wasi-sdk-${WASI_SDK_VERSION}.0-x86_64-linux.tar.gz"
WASI_SDK_SHA256="52640dde13599bf127a95499e61d6d640256119456d1af8897ab6725bcf3d89c"

EMSDK_REPO="https://github.com/emscripten-core/emsdk.git"
EMSDK_VERSION="3.1.74"

# The flat surface core-shim exports. Both cores get all of it; a core that has no use
# for a call (mGBA reads its cartridge from memory and never asks for a path) simply
# answers in a way that says so.
EXPORTS=(
  gatherum_boot gatherum_set_option gatherum_alloc gatherum_free
  gatherum_needs_path gatherum_load gatherum_load_path gatherum_unload
  gatherum_reset gatherum_run gatherum_frame_ptr gatherum_frame_width
  gatherum_frame_height gatherum_audio_ptr gatherum_audio_len gatherum_set_buttons
  gatherum_fps gatherum_sample_rate gatherum_measure_state gatherum_state_size
  gatherum_state_save gatherum_state_load gatherum_state_ok
  gatherum_sram_ptr gatherum_sram_len
)

mkdir -p "$build" "$dist"

wanted=("$@")
if [ ${#wanted[@]} -eq 0 ]; then wanted=(mgba bsnes); fi
building() { for name in "${wanted[@]}"; do [ "$name" = "$1" ] && return 0; done; return 1; }

# --- fetching --------------------------------------------------------------------

# git already hashes what it fetched; this is the check that it is the *right* history.
clone_pinned() {
  local repo="$1" dir="$2" commit="$3" what="$4"
  if [ ! -d "$dir" ]; then
    echo "==> fetching $what"
    git clone --quiet "$repo" "$dir"
    git -C "$dir" checkout --quiet "$commit"
  fi
  local got
  got="$(git -C "$dir" rev-parse HEAD)"
  if [ "$got" != "$commit" ]; then
    echo "$what is at $got, not the pinned $commit. Refusing to build." >&2
    exit 1
  fi
}

wasi_sdk() {
  sdk="$build/wasi-sdk-${WASI_SDK_VERSION}.0-x86_64-linux"
  if [ ! -d "$sdk" ]; then
    echo "==> fetching the WASI toolchain"
    curl -sSL -o "$build/wasi-sdk.tar.gz" "$WASI_SDK_URL"
    echo "${WASI_SDK_SHA256}  $build/wasi-sdk.tar.gz" | sha256sum -c -
    tar xzf "$build/wasi-sdk.tar.gz" -C "$build"
    rm -f "$build/wasi-sdk.tar.gz"
  fi
}

emscripten() {
  emsdk="$build/emsdk"
  local sdk="$emsdk"
  if [ ! -d "$sdk" ]; then
    echo "==> fetching Emscripten $EMSDK_VERSION"
    git clone --quiet --depth 1 "$EMSDK_REPO" "$sdk"
  fi
  if [ ! -f "$sdk/upstream/emscripten/emcc" ]; then
    "$sdk/emsdk" install "$EMSDK_VERSION" > /dev/null
    "$sdk/emsdk" activate "$EMSDK_VERSION" > /dev/null
  fi
  # shellcheck disable=SC1091
  source "$sdk/emsdk_env.sh" > /dev/null 2>&1
  local got
  got="$(cat "$sdk/upstream/emscripten/emscripten-version.txt" | tr -d '"')"
  if [ "$got" != "$EMSDK_VERSION" ]; then
    echo "Emscripten is $got, not the pinned $EMSDK_VERSION. Refusing to build." >&2
    exit 1
  fi
}

# The shim is target-agnostic C ABI, so each core gets it built for the toolchain that
# core uses. Cargo only compiles here — a staticlib is never linked — so building for
# Emscripten's target does not need Emscripten's linker.
shim_for() {
  (cd "$here/core-shim" && cargo build --release --quiet --target "$1")
  echo "$here/core-shim/target/$1/release/libgatherum_core_shim.a"
}

# --- mGBA ------------------------------------------------------------------------

build_mgba() {
  wasi_sdk
  local src="$build/mgba"
  clone_pinned "$MGBA_REPO" "$src" "$MGBA_COMMIT" "mGBA $MGBA_TAG"

  local cc="$sdk/bin/clang" sysroot="$sdk/share/wasi-sysroot"
  local objects="$build/mgba-objects"
  rm -rf "$objects" && mkdir -p "$objects"

  # HAVE_LOCALE keeps mGBA from redefining a type wasi-libc already has; the rest is the
  # ordinary "a core with no operating system under it" configuration.
  local defines=(
    -DM_CORE_GBA=1 -DM_CORE_GB=1 -DMINIMAL_CORE=2
    -DDISABLE_THREADING=1 -DHAVE_LOCALE=1 -DHAVE_STRDUP=1
  )

  # Left out on purpose, all of them things a browser has no use for: the test harness,
  # the debugger, PNG screenshots, zip archives, a devkitPro device list, GameCube-over-
  # the-network, Lua scripting, threads, and the two files that reach for a POSIX locale
  # and a real filesystem.
  local sources
  sources=$(cd "$src" && find src/arm src/core src/gb src/gba src/sm83 src/util \
      src/third-party/blip_buf src/third-party/inih -name '*.c' \
    | grep -v "/test/" \
    | grep -v "debugger" \
    | grep -v -E "vfs-zip|vfs-devlist|vfs-file|dolphin|scripting|png-io|thread\.c|formatting\.c")

  echo "==> compiling $(echo "$sources" | wc -l) mGBA sources"
  local file
  for file in $sources; do
    "$cc" --target=wasm32-wasi --sysroot="$sysroot" \
      -I"$src/include" -I"$src/src" "${defines[@]}" -O2 \
      -c "$src/$file" -o "$objects/$(echo "$file" | tr / _).o"
  done

  echo "==> compiling mGBA's libretro layer"
  "$cc" --target=wasm32-wasi --sysroot="$sysroot" \
    -I"$src/include" -I"$src/src" -I"$src/src/platform/libretro" "${defines[@]}" -O2 \
    -c "$src/src/platform/libretro/libretro.c" -o "$objects/libretro.o"

  echo "==> building the shim for WASI"
  local shim
  shim="$(shim_for wasm32-wasip1)"

  # A reactor, not a command: it is a library the browser calls into, with no main().
  local flags=()
  local name
  for name in "${EXPORTS[@]}"; do flags+=("-Wl,--export=$name"); done

  echo "==> linking mgba.wasm"
  "$cc" --target=wasm32-wasi --sysroot="$sysroot" -mexec-model=reactor -O2 \
    "${flags[@]}" -Wl,--allow-undefined \
    "$objects"/*.o "$shim" -o "$dist/mgba.wasm"
  report mgba.wasm
}

# --- bsnes -----------------------------------------------------------------------

build_bsnes() {
  emscripten
  local src="$build/bsnes"
  clone_pinned "$BSNES_REPO" "$src" "$BSNES_COMMIT" "bsnes"

  # bsnes builds itself; asking it for the Emscripten platform gets one archive of the
  # whole core, named for a bitcode format it stopped being years ago. It has to be
  # *emmake*: the platform only picks the target name and static linking, and plain make
  # would quietly compile the whole core for the machine running the build — an archive
  # that links without complaint and has not one wasm symbol in it.
  echo "==> compiling bsnes (this takes a while)"
  emmake make -C "$src" platform=emscripten -j"$(nproc)" > /dev/null
  cp "$src/bsnes_libretro_emscripten.bc" "$build/libbsnes.a"

  # And proof that it is what it claims to be, because the failure mode above is silent:
  # the wrong archive links without complaint and fails at the far end with a page of
  # undefined libretro symbols. emsdk_env.sh does not put the LLVM tools on PATH, so this
  # reaches for the one it means by the path this script already knows — not by anything
  # sourcing a script may or may not have exported — and lets a missing tool be loud.
  # Not `grep -q`: it exits on the first match, llvm-nm dies of SIGPIPE half a million
  # symbols early, and pipefail reports the whole pipeline as a failure — which would make
  # this check reject exactly the archive it is looking for.
  if ! "$emsdk/upstream/bin/llvm-nm" "$build/libbsnes.a" | grep "T retro_run" > /dev/null; then
    echo "bsnes built without retro_run in it — that is a build for the wrong machine." >&2
    exit 1
  fi

  echo "==> compiling the libco extras"
  emcc -O2 -I"$src/libco" -c "$here/bsnes-support/libco-extras.c" -o "$build/libco-extras.o"

  echo "==> building the shim for Emscripten"
  local shim
  shim="$(shim_for wasm32-unknown-emscripten)"

  local flags=()
  local name
  for name in "${EXPORTS[@]}"; do flags+=("_$name"); done
  local exported
  exported="$(IFS=,; echo "${flags[*]}"),_malloc,_free"

  # ASYNCIFY is what lets a coroutine-shaped core run on a stack it does not own: a fiber
  # swap unwinds out to JavaScript and rewinds back in. It is also why the shim parks its
  # answers in statics — a value returned across a swap does not survive the trip.
  # FORCE_FILESYSTEM earns its keep because bsnes insists on opening the cartridge itself
  # rather than reading it out of memory; the host writes it to a file in memory first.
  echo "==> linking bsnes.mjs"
  em++ -O2 -fexceptions -sASYNCIFY=1 -sASYNCIFY_STACK_SIZE=32768 \
    -sMODULARIZE=1 -sEXPORT_ES6=1 -sENVIRONMENT=web,node \
    -sALLOW_MEMORY_GROWTH=1 -sINITIAL_MEMORY=64MB -sFORCE_FILESYSTEM=1 \
    -sEXPORTED_FUNCTIONS="$exported" \
    -sEXPORTED_RUNTIME_METHODS=HEAPU8,HEAPU32,FS,stringToNewUTF8 \
    "$build/libco-extras.o" "$build/libbsnes.a" "$shim" -o "$dist/bsnes.mjs"
  report bsnes.wasm
  report bsnes.mjs
}

report() {
  local size
  size=$(stat -c%s "$dist/$1")
  echo "==> native/dist/$1  ($((size / 1024)) KB)"
  sha256sum "$dist/$1"
}

# --- go --------------------------------------------------------------------------

if building mgba; then build_mgba; fi
if building bsnes; then build_bsnes; fi
