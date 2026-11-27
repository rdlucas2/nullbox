# Fake Gmail/token/Jev endpoints for the compose smoke test (deploy/smoke). Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY tools/Nullbox.FakeUpstream/ tools/Nullbox.FakeUpstream/
RUN dotnet publish tools/Nullbox.FakeUpstream/Nullbox.FakeUpstream.csproj --configuration Release --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Nullbox.FakeUpstream.dll"]
