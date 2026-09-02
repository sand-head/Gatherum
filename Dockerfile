# Stage 0: the vendored emulator cores, each in a stage of its own. The toolchains they
# need — a WASI clang, an Emscripten SDK, a Rust cross-compiler — never reach the image
# that ships, editing C# never rebuilds an emulator, and editing one emulator's inputs
# never rebuilds the other two: each stage copies only what its core is built from, so
# its cache key is those files and nothing else. What comes out is what native/dist/
# holds; see native/README.md.
#
# Every stage ends by deleting what it fetched and compiled, in the same RUN that made
# it. A layer keeps whatever the step left behind, and these steps leave behind an SDK
# and a Rust target directory measured in gigabytes — which is what CI's layer cache
# would have to hold to be any use, and it holds ten. The few megabytes in dist/ are
# all the next stage takes.
FROM rust:1-bookworm AS core-base
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl git make python3 xz-utils ca-certificates clang \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /native
COPY native/build-core.sh ./

FROM core-base AS core-mgba
RUN rustup target add wasm32-wasip1
COPY native/core-shim ./core-shim
RUN ./build-core.sh mgba && rm -rf build core-shim/target /usr/local/cargo/registry

FROM core-base AS core-bsnes
RUN rustup target add wasm32-unknown-emscripten
COPY native/core-shim ./core-shim
COPY native/bsnes-support ./bsnes-support
RUN ./build-core.sh bsnes && rm -rf build core-shim/target /usr/local/cargo/registry

# Gecko's Rust and its wasm target rustup fetches from the pin in gecko-host; clang, in
# the base, is what compiles zstd for the browser so RVZ discs read.
FROM core-base AS core-gecko
COPY native/gecko-host ./gecko-host
RUN ./build-core.sh gecko && rm -rf build gecko-host/target /usr/local/cargo/registry

# Stage 1: compile and publish. The editor's Interactive Auto island relinks the
# WebAssembly runtime with SkiaSharp's native library — that needs the wasm-tools
# workload, and emscripten shells out to python.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && dotnet workload install wasm-tools
WORKDIR /src
COPY Directory.Build.props Gatherum.slnx nuget.config ./
COPY src/Gatherum.Core/Gatherum.Core.csproj src/Gatherum.Core/
COPY src/Gatherum.Client/Gatherum.Client.csproj src/Gatherum.Client/
COPY src/Gatherum.Infrastructure/Gatherum.Infrastructure.csproj src/Gatherum.Infrastructure/
COPY src/Gatherum.Web/Gatherum.Web.csproj src/Gatherum.Web/
RUN dotnet restore src/Gatherum.Web
# The packaged embedding model, in its own layer: editing source shouldn't re-download
# twenty-three megabytes of weights. The publish below finds them already fetched.
RUN dotnet msbuild src/Gatherum.Infrastructure/Gatherum.Infrastructure.csproj \
    -t:FetchEmbeddingModel
# Where the web project's build looks for them, and the only thing carried over from
# the stages above: not the source they were built from, and not the compilers that
# built them.
COPY --from=core-mgba /native/dist/ ./native/dist/
COPY --from=core-bsnes /native/dist/ ./native/dist/
COPY --from=core-gecko /native/dist/ ./native/dist/
COPY src ./src
# Published for one architecture on purpose. ONNX Runtime ships native libraries for
# every platform it supports, and a portable publish carries all of them — most of a
# gigabyte of Windows, Android and macOS binaries this Linux image can never load. A RID
# leaves only the one it will.
ARG TARGETARCH
RUN RID="linux-$(case "$TARGETARCH" in arm64) echo arm64;; *) echo x64;; esac)" \
    && dotnet publish src/Gatherum.Web -c Release -r "$RID" --self-contained false -o /app

# Stage 2: runtime, non-root.
# ffmpeg is what splits an uploaded video into the audio a model listens to and the
# frames it looks at. Without it images and audio still analyze; video records why
# it could not. The embedding model needs nothing installed: ONNX Runtime's native
# library is published alongside the app, and the weights beside it.
# chromium renders a bookmarked page before it is captured — scripts run, then the
# settled document is what gets kept. Debian's build rather than a Playwright download,
# so one apt layer serves both architectures; without it (or with the env var pointed
# at nothing) a bookmark degrades to capturing what the server serves.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg chromium \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/files && chown -R $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Gatherum__Storage__Root=/data/files \
    Gatherum__Bookmarks__BrowserPath=/usr/bin/chromium \
    XDG_CONFIG_HOME=/tmp/.chromium \
    XDG_CACHE_HOME=/tmp/.chromium-cache
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Gatherum.Web.dll"]
