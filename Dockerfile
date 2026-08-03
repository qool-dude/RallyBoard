# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY RallyBoard.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
# Avoid FileSystemWatcher/inotify exhaustion on small Linux hosts
ENV DOTNET_HOSTBUILDER_RELOADCONFIGONCHANGE=false
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "RallyBoard.dll"]
