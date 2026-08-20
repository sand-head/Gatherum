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
COPY src ./src
RUN dotnet publish src/Gatherum.Web -c Release -o /app

# Stage 2: runtime, non-root.
# ffmpeg is what splits an uploaded video into the audio a model listens to and the
# frames it looks at. Without it images and audio still analyze; video records why
# it could not.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/files && chown -R $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Gatherum__Storage__Root=/data/files
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Gatherum.Web.dll"]
