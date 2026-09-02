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
# Five cores, three toolchains, and the difference is each core's own doing rather than
# a preference of ours. mGBA is plain C that wants no operating system underneath it, so
# it compiles against WASI and comes out as one .wasm importing a handful of system calls
# and nothing else. bsnes runs its processor, sound and picture as coroutines and throws
# C++ exceptions, neither of which WASI's libc++ can do, so it compiles against Emscripten
# and comes out as a .wasm plus the loader Emscripten emits to drive it. Both link
# against the same core-shim, unchanged: the shim knows libretro, not who implements it.
# Gecko is Rust and not libretro at all, so it gets a host of its own (gecko-host/) built
# with Rust's own WebAssembly target and wasm-bindgen's loader beside it. Beetle VB is
# mGBA's shape again — C and C++ with exceptions switched off — and jgenesis is Gecko's,
# a Rust crate behind a host of its own (jgenesis-host/), minus the GPU.
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

# Gecko, a GameCube emulator in Rust. Its web build wants WebGPU and wasm-bindgen, and
# neither shim nor libretro comes into it.
GECKO_REPO="https://github.com/ioncodes/gecko.git"
GECKO_COMMIT="39e82205a0da154f23fd36b95e64a8029d468618"

# Gecko's sound processor runs a boot ROM, and Nintendo's cannot be shipped. Dolphin wrote
# a free one and keeps the assembled bytes in its tree; these are they, pinned to a commit
# and to their hashes, and built into the Gecko host.
DOLPHIN_COMMIT="a1e636d72c8469acf747ac6542f0b7ace7cea02f"
DOLPHIN_RAW="https://raw.githubusercontent.com/dolphin-emu/dolphin/${DOLPHIN_COMMIT}/Data/Sys/GC"
DSP_ROM_SHA256="4ea1fea6c649bcf9f627007bc9403d5437896c681d3e089b083263a7646cd3ae"
DSP_COEF_SHA256="d7741279c2e8ec5c5fb318f8fbdd6de6bf583520d288e836a5383233a4238179"

# Beetle VB, Mednafen's Virtual Boy module as libretro carries it. Plain C and C++
# compiled without exceptions, so it takes the same road as mGBA.
BEETLE_VB_REPO="https://github.com/libretro/beetle-vb-libretro.git"
BEETLE_VB_COMMIT="83ed42608601fb7b01d41e4f8fb2007a37b8c84e"

# jgenesis, a Mega Drive (and 32X) emulator in Rust, at its 0.14.1 release. Like Gecko
# it is not libretro, and unlike Gecko it draws into a buffer, so its host is Gecko's
# without the GPU.
JGENESIS_REPO="https://github.com/jsgroth/jgenesis.git"
JGENESIS_TAG="v0.14.1"
JGENESIS_COMMIT="cbe7f129e3f5c805a2a2e4318981834192116e90"

WASI_SDK_VERSION="25"
WASI_SDK_URL="https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-${WASI_SDK_VERSION}/wasi-sdk-${WASI_SDK_VERSION}.0-x86_64-linux.tar.gz"
WASI_SDK_SHA256="52640dde13599bf127a95499e61d6d640256119456d1af8897ab6725bcf3d89c"

EMSDK_REPO="https://github.com/emscripten-core/emsdk.git"
EMSDK_VERSION="3.1.74"

# The same version as the wasm-bindgen crate the Gecko host depends on: the tool and the
# crate check each other and refuse to work across a mismatch.
WASM_BINDGEN_VERSION="0.2.118"
WASM_BINDGEN_URL="https://github.com/wasm-bindgen/wasm-bindgen/releases/download/${WASM_BINDGEN_VERSION}/wasm-bindgen-${WASM_BINDGEN_VERSION}-x86_64-unknown-linux-musl.tar.gz"
WASM_BINDGEN_SHA256="00b519c9fc2d6e087265da1a00f29160bfcc6a823993482bc2e691910287427b"

# The flat surface core-shim exports. Both cores get all of it; a core that has no use
# for a call (mGBA reads its cartridge from memory and never asks for a path) simply
# answers in a way that says so.
EXPORTS=(
  gatherum_boot gatherum_set_option gatherum_alloc gatherum_free
  gatherum_needs_path gatherum_load gatherum_load_path gatherum_unload
  gatherum_reset gatherum_run gatherum_frame_ptr gatherum_frame_width
  gatherum_frame_height gatherum_audio_ptr gatherum_audio_len gatherum_set_buttons
  gatherum_set_sticks gatherum_fps gatherum_sample_rate gatherum_measure_state gatherum_state_size
  gatherum_state_save gatherum_state_load gatherum_state_ok
  gatherum_sram_ptr gatherum_sram_len
)

mkdir -p "$build" "$dist"

wanted=("$@")
if [ ${#wanted[@]} -eq 0 ]; then wanted=(mgba bsnes gecko beetlevb jgenesis); fi
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

wasm_bindgen() {
  bindgen="$build/wasm-bindgen-${WASM_BINDGEN_VERSION}-x86_64-unknown-linux-musl"
  if [ ! -x "$bindgen/wasm-bindgen" ]; then
    echo "==> fetching wasm-bindgen $WASM_BINDGEN_VERSION"
    curl -sSL -o "$build/wasm-bindgen.tar.gz" "$WASM_BINDGEN_URL"
    echo "${WASM_BINDGEN_SHA256}  $build/wasm-bindgen.tar.gz" | sha256sum -c -
    tar xzf "$build/wasm-bindgen.tar.gz" -C "$build"
    rm -f "$build/wasm-bindgen.tar.gz"
  fi
}

# A file fetched by URL and refused unless it hashes to what was pinned.
fetch_pinned() {
  local url="$1" target="$2" sha256="$3"
  if [ ! -f "$target" ]; then
    curl -sSL -o "$target" "$url"
  fi
  echo "${sha256}  $target" | sha256sum -c - > /dev/null || {
    echo "$target does not hash to the pinned $sha256. Refusing to build." >&2
    exit 1
  }
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

# --- Beetle VB -------------------------------------------------------------------

build_beetlevb() {
  wasi_sdk
  local src="$build/beetle-vb"
  clone_pinned "$BEETLE_VB_REPO" "$src" "$BEETLE_VB_COMMIT" "Beetle VB"

  local cc="$sdk/bin/clang" cxx="$sdk/bin/clang++" sysroot="$sdk/share/wasi-sysroot"
  local objects="$build/beetle-vb-objects"
  rm -rf "$objects" && mkdir -p "$objects"

  # The libretro Makefile's own flags for a plain unix build, minus the ones about
  # shared objects. WANT_32BPP is the pixel format the shim reads; the rest is what
  # Mednafen's code expects to find defined.
  local includes=(
    -I"$src" -I"$src/mednafen" -I"$src/mednafen/include" -I"$src/mednafen/hw_sound"
    -I"$src/mednafen/hw_cpu" -I"$src/mednafen/hw_misc" -I"$src/libretro-common/include"
  )
  local defines=(
    -DWANT_32BPP -D__LIBRETRO__ -DNDEBUG -DINLINE=inline
    -DMEDNAFEN_VERSION='"0.9.31"' -DMEDNAFEN_VERSION_NUMERIC=931
    -DSTDC_HEADERS -D__STDC_LIMIT_MACROS
  )

  # Makefile.common's list, spelled out: the V810, the four Virtual Boy chips and the
  # software float they share, Blip_Buffer for sound, Mednafen's state and settings
  # plumbing, the cheat engine the module calls into, and the libretro layer. strlcpy
  # is not in wasi-libc, so libretro-common's copy comes too.
  local sources=(
    mednafen/hw_cpu/v810/v810_cpu.cpp
    mednafen/vb/vsu.c mednafen/vb/input.c mednafen/vb/timer.c mednafen/vb/vip.c
    mednafen/hw_cpu/v810/fpu-new/softfloat.c
    mednafen/sound/Blip_Buffer.c
    mednafen/mempatcher.cpp mednafen/state.c mednafen/settings.c
    libretro-common/compat/compat_strl.c
    libretro.cpp
  )

  echo "==> compiling ${#sources[@]} Beetle VB sources"
  local file
  for file in "${sources[@]}"; do
    case "$file" in
      *.cpp)
        "$cxx" --target=wasm32-wasi --sysroot="$sysroot" -std=gnu++11 \
          -fno-exceptions -fno-rtti "${includes[@]}" "${defines[@]}" -O2 \
          -c "$src/$file" -o "$objects/$(echo "$file" | tr / _).o" ;;
      *)
        "$cc" --target=wasm32-wasi --sysroot="$sysroot" -std=gnu11 \
          "${includes[@]}" "${defines[@]}" -O2 \
          -c "$src/$file" -o "$objects/$(echo "$file" | tr / _).o" ;;
    esac
  done

  echo "==> building the shim for WASI"
  local shim
  shim="$(shim_for wasm32-wasip1)"

  local flags=()
  local name
  for name in "${EXPORTS[@]}"; do flags+=("-Wl,--export=$name"); done

  echo "==> linking beetle-vb.wasm"
  "$cxx" --target=wasm32-wasi --sysroot="$sysroot" -mexec-model=reactor -O2 \
    -fno-exceptions "${flags[@]}" -Wl,--allow-undefined \
    "$objects"/*.o "$shim" -o "$dist/beetle-vb.wasm"
  report beetle-vb.wasm
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

# --- Gecko -----------------------------------------------------------------------

build_gecko() {
  wasm_bindgen
  local src="$build/gecko"
  clone_pinned "$GECKO_REPO" "$src" "$GECKO_COMMIT" "Gecko"
  # Two of its submodules are compiled in: the IPL replacement it boots a disc with, and
  # the instruction specs its decoder is generated from. The third is test data.
  git -C "$src" submodule update --init --quiet submodules/solstice submodules/chipi-spec

  # The one place a fetched core is changed, and every change is a file in the repo
  # beside this script: Gecko keeps its memory card's contents to itself, and a browser
  # that is to keep a save has to be able to read them. Applied once; a tree that
  # already carries a patch is left alone rather than patched twice.
  local patch
  for patch in "$here"/gecko-host/patches/*.patch; do
    if git -C "$src" apply --check --reverse "$patch" > /dev/null 2>&1; then
      continue
    fi
    echo "==> applying $(basename "$patch")"
    git -C "$src" apply "$patch"
  done

  echo "==> fetching Dolphin's free DSP ROM"
  local dsp="$build/dsp"
  mkdir -p "$dsp"
  fetch_pinned "$DOLPHIN_RAW/dsp_rom.bin" "$dsp/dsp_rom.bin" "$DSP_ROM_SHA256"
  fetch_pinned "$DOLPHIN_RAW/dsp_coef.bin" "$dsp/dsp_coef.bin" "$DSP_COEF_SHA256"

  # The host crate pins its own toolchain in rust-toolchain.toml — the one Gecko pins
  # for itself — and rustup fetches it on the way past. RVZ discs are zstd, whose C
  # source cc compiles for this target with whatever clang it finds.
  echo "==> compiling the Gecko host (this takes a while)"
  (cd "$here/gecko-host" \
    && GATHERUM_DSP_ROM="$dsp/dsp_rom.bin" GATHERUM_DSP_COEF="$dsp/dsp_coef.bin" \
       cargo build --release --quiet --target wasm32-unknown-unknown)
  local module="$here/gecko-host/target/wasm32-unknown-unknown/release/gatherum_gecko_host.wasm"

  # wasm-bindgen rewrites the module for the browser and writes the loader beside it as
  # gecko.js, which is renamed so the web project's build stages it as the module kind it
  # is; the loader finds gecko_bg.wasm by its own URL, so the two ship as a pair.
  echo "==> binding gecko.mjs"
  local out="$build/gecko-out"
  rm -rf "$out" && mkdir -p "$out"
  "$bindgen/wasm-bindgen" --target web --out-dir "$out" --out-name gecko --no-typescript "$module"
  cp "$out/gecko_bg.wasm" "$dist/gecko_bg.wasm"
  cp "$out/gecko.js" "$dist/gecko.mjs"
  report gecko_bg.wasm
  report gecko.mjs
}

# --- jgenesis --------------------------------------------------------------------

build_jgenesis() {
  wasm_bindgen
  local src="$build/jgenesis"
  clone_pinned "$JGENESIS_REPO" "$src" "$JGENESIS_COMMIT" "jgenesis $JGENESIS_TAG"

  # The same bargain as Gecko's: a change to what is fetched is a patch file beside
  # this script, applied once. jgenesis keeps a cartridge's battery memory to itself,
  # and a browser that is to keep a save has to be able to read it and write it back.
  local patch
  for patch in "$here"/jgenesis-host/patches/*.patch; do
    if git -C "$src" apply --check --reverse "$patch" > /dev/null 2>&1; then
      continue
    fi
    echo "==> applying $(basename "$patch")"
    git -C "$src" apply "$patch"
  done

  echo "==> compiling the jgenesis host (this takes a while)"
  (cd "$here/jgenesis-host" && cargo build --release --quiet --target wasm32-unknown-unknown)
  local module="$here/jgenesis-host/target/wasm32-unknown-unknown/release/gatherum_jgenesis_host.wasm"

  echo "==> binding jgenesis.mjs"
  local out="$build/jgenesis-out"
  rm -rf "$out" && mkdir -p "$out"
  "$bindgen/wasm-bindgen" --target web --out-dir "$out" --out-name jgenesis --no-typescript "$module"
  cp "$out/jgenesis_bg.wasm" "$dist/jgenesis_bg.wasm"
  cp "$out/jgenesis.js" "$dist/jgenesis.mjs"
  report jgenesis_bg.wasm
  report jgenesis.mjs
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
if building gecko; then build_gecko; fi
if building beetlevb; then build_beetlevb; fi
if building jgenesis; then build_jgenesis; fi
