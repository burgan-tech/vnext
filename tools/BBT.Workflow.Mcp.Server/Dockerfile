# Base image using Alpine
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23 AS base
WORKDIR /app

# Create non-root user for security
RUN adduser -D workflowuser && chown -R workflowuser:workflowuser /app

USER workflowuser

# Build image. Unlike the workflow hosts, the MCP server has NO Aether/PostSharp references,
# so the NETStandard.Library.Ref targeting-pack workaround is not needed here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.203-alpine3.23 AS build

COPY ["common.props", "/common.props"]
COPY ["Directory.Build.props", "/Directory.Build.props"]
COPY ["global.json", "/global.json"]
COPY ["nuget.config", "/nuget.config"]

WORKDIR /tools
# The MCP server is self-contained (no ProjectReference to src/), so only its own project is needed.
COPY tools/BBT.Workflow.Mcp.Server/ ./BBT.Workflow.Mcp.Server
RUN dotnet restore "BBT.Workflow.Mcp.Server/BBT.Workflow.Mcp.Server.csproj"
WORKDIR "/tools/BBT.Workflow.Mcp.Server"
ARG configuration=Release
RUN dotnet publish "BBT.Workflow.Mcp.Server.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

# Final image
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5000
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false
ENV ASPNETCORE_URLS=http://+:5000

# Default to Streamable HTTP in containers; override CMD for other transports.
ENTRYPOINT ["dotnet", "BBT.Workflow.Mcp.Server.dll"]
CMD ["--transport", "http"]
