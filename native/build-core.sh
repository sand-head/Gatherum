#!/usr/bin/env bash
# Builds a vendored emulator core into one glue-free WebAssembly module.
#
# Nothing this fetches is committed: mGBA's source and the WASI toolchain land in
# native/build/, which is gitignored, and the one artefact that matters comes out at
# native/dist/mgba.wasm. Run it by hand or let CI run it; either way the inputs are
# pinned by hash and the output is reproducible from them.
#
# What comes out imports nothing but a handful of WASI calls and exports the flat
# surface in core-shim/src/lib.rs. It is not an Emscripten build: there is no JavaScript
# in it, because a vendored JavaScript library is the one thing Gatherum does not take.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build="$here/build"
dist="$here/dist"

# --- what we are pinned to -------------------------------------------------------

MGBA_REPO="https://github.com/mgba-emu/mgba.git"
MGBA_TAG="0.10.3"
MGBA_COMMIT="1c61b54208ca6266129d0f2394c04bd8c44f98c5"

WASI_SDK_VERSION="25"
WASI_SDK_URL="https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-${WASI_SDK_VERSION}/wasi-sdk-${WASI_SDK_VERSION}.0-x86_64-linux.tar.gz"
WASI_SDK_SHA256="52640dde13599bf127a95499e61d6d640256119456d1af8897ab6725bcf3d89c"

mkdir -p "$build" "$dist"

# --- the toolchain ---------------------------------------------------------------

sdk="$build/wasi-sdk-${WASI_SDK_VERSION}.0-x86_64-linux"
if [ ! -d "$sdk" ]; then
  echo "==> fetching the WASI toolchain"
  curl -sSL -o "$build/wasi-sdk.tar.gz" "$WASI_SDK_URL"
  echo "${WASI_SDK_SHA256}  $build/wasi-sdk.tar.gz" | sha256sum -c -
  tar xzf "$build/wasi-sdk.tar.gz" -C "$build"
  rm -f "$build/wasi-sdk.tar.gz"
fi
CC="$sdk/bin/clang"
SYSROOT="$sdk/share/wasi-sysroot"

# --- the core's source -----------------------------------------------------------

src="$build/mgba"
if [ ! -d "$src" ]; then
  echo "==> fetching mGBA $MGBA_TAG"
  git clone --quiet --depth 1 --branch "$MGBA_TAG" "$MGBA_REPO" "$src"
fi
# git already hashes what it fetched; this is the check that it is the *right* history.
got="$(git -C "$src" rev-parse HEAD)"
if [ "$got" != "$MGBA_COMMIT" ]; then
  echo "mGBA $MGBA_TAG is at $got, not the pinned $MGBA_COMMIT. Refusing to build." >&2
  exit 1
fi

# --- compiling -------------------------------------------------------------------

objects="$build/objects"
rm -rf "$objects" && mkdir -p "$objects"

# HAVE_LOCALE keeps mGBA from redefining a type wasi-libc already has; the rest is the
# ordinary "a core with no operating system under it" configuration.
defines=(
  -DM_CORE_GBA=1 -DM_CORE_GB=1 -DMINIMAL_CORE=2
  -DDISABLE_THREADING=1 -DHAVE_LOCALE=1 -DHAVE_STRDUP=1
)

# Left out on purpose, all of them things a browser has no use for: the test harness,
# the debugger, PNG screenshots, zip archives, a devkitPro device list, GameCube-over-
# the-network, Lua scripting, threads, and the two files that reach for a POSIX locale
# and a real filesystem.
sources=$(cd "$src" && find src/arm src/core src/gb src/gba src/sm83 src/util \
    src/third-party/blip_buf src/third-party/inih -name '*.c' \
  | grep -v "/test/" \
  | grep -v "debugger" \
  | grep -v -E "vfs-zip|vfs-devlist|vfs-file|dolphin|scripting|png-io|thread\.c|formatting\.c")

echo "==> compiling $(echo "$sources" | wc -l) core sources"
for file in $sources; do
  "$CC" --target=wasm32-wasi --sysroot="$SYSROOT" \
    -I"$src/include" -I"$src/src" "${defines[@]}" -O2 \
    -c "$src/$file" -o "$objects/$(echo "$file" | tr / _).o"
done

echo "==> compiling the libretro layer"
"$CC" --target=wasm32-wasi --sysroot="$SYSROOT" \
  -I"$src/include" -I"$src/src" -I"$src/src/platform/libretro" "${defines[@]}" -O2 \
  -c "$src/src/platform/libretro/libretro.c" -o "$objects/libretro.o"

echo "==> building the shim"
(cd "$here/core-shim" && cargo build --release --target wasm32-wasip1)
shim="$here/core-shim/target/wasm32-wasip1/release/libgatherum_core_shim.a"

# --- linking ---------------------------------------------------------------------

# A reactor, not a command: it is a library the browser calls into, with no main().
exports=(
  gatherum_boot gatherum_alloc gatherum_free gatherum_load gatherum_unload
  gatherum_reset gatherum_run gatherum_frame_ptr gatherum_frame_width
  gatherum_frame_height gatherum_audio_ptr gatherum_audio_len gatherum_set_buttons
  gatherum_fps gatherum_sample_rate gatherum_state_size gatherum_state_save
  gatherum_state_load gatherum_sram_ptr gatherum_sram_len
)
flags=()
for name in "${exports[@]}"; do flags+=("-Wl,--export=$name"); done

echo "==> linking"
"$CC" --target=wasm32-wasi --sysroot="$SYSROOT" -mexec-model=reactor -O2 \
  "${flags[@]}" -Wl,--allow-undefined \
  "$objects"/*.o "$shim" -o "$dist/mgba.wasm"

size=$(stat -c%s "$dist/mgba.wasm")
echo "==> native/dist/mgba.wasm  ($((size / 1024)) KB)"
sha256sum "$dist/mgba.wasm"
