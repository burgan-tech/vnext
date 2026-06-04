# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish CustomScriptHelpersDemo.csproj -c Release -o /app

# Runtime — note there is NO third-party package here; those arrive via a mounted volume.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# Operator mounts approved DLLs into this directory (see docker-compose.yml).
ENV SCRIPT_PLUGIN_DIR=/app/assemblies
ENTRYPOINT ["dotnet", "CustomScriptHelpersDemo.dll"]
