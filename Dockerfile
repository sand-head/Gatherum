# Stage 1: compile and publish. The WebAssembly editor island relinks the .NET
# runtime with SkiaSharp's native library, which needs the wasm-tools workload.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Emscripten (the wasm relink toolchain) shells out to python.
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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
