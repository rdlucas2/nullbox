# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until a project file changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY src/Nullbox.Core/Nullbox.Core.csproj src/Nullbox.Core/
COPY src/Nullbox.Gmail/Nullbox.Gmail.csproj src/Nullbox.Gmail/
COPY src/Nullbox.Jev/Nullbox.Jev.csproj src/Nullbox.Jev/
COPY src/Nullbox.Web/Nullbox.Web.csproj src/Nullbox.Web/
RUN dotnet restore src/Nullbox.Web/Nullbox.Web.csproj

COPY src/ src/
RUN dotnet publish src/Nullbox.Web/Nullbox.Web.csproj \
      --configuration Release --no-restore --output /app/publish \
      /p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# /data holds the SQLite database and Google token files; mount a volume here.
RUN mkdir -p /data/google \
 && chown -R "$APP_UID:$APP_UID" /data \
 && chmod 0700 /data/google

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    NULLBOX_DATA_DIR=/data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Non-root: the built-in "app" user of the official .NET images.
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]

# The runtime image has no curl/wget, so the app probes its own /healthz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD ["dotnet", "Nullbox.Web.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Nullbox.Web.dll"]
