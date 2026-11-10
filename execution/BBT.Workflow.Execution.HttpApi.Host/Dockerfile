FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    ASPNETCORE_URLS=http://+:5000

# libgssapi-krb5-2: Npgsql probes GSSAPI on every connection; without it the .NET native shim
# prints "Cannot load library libgssapi_krb5.so.2" to stderr (harmless, but reads as a failure).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libgssapi-krb5-2 \
        wget \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --system --gid 10001 workflowuser \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent workflowuser

WORKDIR /app
RUN chown workflowuser:workflowuser /app

# Build image (pinned to 10.0.2xx band for PostSharp compatibility).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.203-noble AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends unzip wget \
    && wget -q https://www.nuget.org/api/v2/package/NETStandard.Library.Ref/2.1.0 -O /tmp/netstandard.nupkg \
    && mkdir -p /root/.nuget/packages/netstandard.library.ref/2.1.0 \
    && mkdir -p /var/tmp/postsharp/NuGetFallback/NETStandard.Library.Ref/2.1.0 \
    && unzip -q /tmp/netstandard.nupkg -d /root/.nuget/packages/netstandard.library.ref/2.1.0 \
    && unzip -q /tmp/netstandard.nupkg -d /var/tmp/postsharp/NuGetFallback/NETStandard.Library.Ref/2.1.0 \
    && rm -rf /tmp/netstandard.nupkg /var/lib/apt/lists/*


COPY ["common.props", "/common.props"]
COPY ["Directory.Build.props", "/Directory.Build.props"]
COPY ["global.json", "/global.json"]
COPY ["nuget.config", "/nuget.config"]
COPY modules/ /modules
COPY src/ /src

WORKDIR /execution
COPY execution/ .
RUN dotnet restore "BBT.Workflow.Execution.HttpApi.Host/BBT.Workflow.Execution.HttpApi.Host.csproj"
WORKDIR /execution/BBT.Workflow.Execution.HttpApi.Host
ARG configuration=Release
RUN dotnet publish "BBT.Workflow.Execution.HttpApi.Host.csproj" \
    -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER workflowuser
EXPOSE 5000
ENTRYPOINT ["dotnet", "BBT.Workflow.Execution.HttpApi.Host.dll"]
