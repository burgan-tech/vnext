# Base image using Alpine
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app

# Create non-root user for security
RUN adduser -D workflowuser && chown -R workflowuser:workflowuser /app

USER workflowuser

# Build image
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

# Install NETStandard.Library.Ref targeting pack for PostSharp compatibility
RUN wget -q https://www.nuget.org/api/v2/package/NETStandard.Library.Ref/2.1.0 -O /tmp/netstandard.nupkg && \
    mkdir -p /root/.nuget/packages/netstandard.library.ref/2.1.0 && \
    mkdir -p /var/tmp/postsharp/NuGetFallback/NETStandard.Library.Ref/2.1.0 && \
    unzip -q /tmp/netstandard.nupkg -d /root/.nuget/packages/netstandard.library.ref/2.1.0 && \
    unzip -q /tmp/netstandard.nupkg -d /var/tmp/postsharp/NuGetFallback/NETStandard.Library.Ref/2.1.0 && \
    rm /tmp/netstandard.nupkg

COPY ["common.props", "/common.props"]
COPY ["Directory.Build.props", "/Directory.Build.props"]
COPY ["global.json", "/global.json"]
COPY ["nuget.config", "/nuget.config"]
COPY modules/ /modules
COPY src/ /src

WORKDIR /execution
COPY execution/ .
RUN dotnet restore "BBT.Workflow.Execution.HttpApi.Host/BBT.Workflow.Execution.HttpApi.Host.csproj"
WORKDIR "/execution/BBT.Workflow.Execution.HttpApi.Host"
ARG configuration=Release
RUN dotnet publish "BBT.Workflow.Execution.HttpApi.Host.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

# Final image
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5000
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false
ENV ASPNETCORE_URLS=http://+:5000

ENTRYPOINT ["dotnet", "BBT.Workflow.Execution.HttpApi.Host.dll"] 