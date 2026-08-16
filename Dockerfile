# Stage 1: bundle the editor JavaScript (the runtime image needs no Node).
FROM node:22-alpine AS client
WORKDIR /client
COPY src/Gatherum.Web/package.json src/Gatherum.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Gatherum.Web/Scripts ./Scripts
RUN npx esbuild Scripts/editor.js --bundle --format=esm --minify --outfile=dist/editor.js

# Stage 2: compile and publish the app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Gatherum.slnx ./
COPY src/Gatherum.Core/Gatherum.Core.csproj src/Gatherum.Core/
COPY src/Gatherum.Infrastructure/Gatherum.Infrastructure.csproj src/Gatherum.Infrastructure/
COPY src/Gatherum.Web/Gatherum.Web.csproj src/Gatherum.Web/
RUN dotnet restore src/Gatherum.Web
COPY src ./src
COPY --from=client /client/dist src/Gatherum.Web/wwwroot/js/dist
RUN dotnet publish src/Gatherum.Web -c Release -o /app -p:SkipClientBundle=true

# Stage 3: runtime, non-root.
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
