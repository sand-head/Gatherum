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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/files /data/keys && chown -R $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Gatherum__Storage__Root=/data/files \
    Gatherum__Storage__KeyRing=/data/keys
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Gatherum.Web.dll"]
