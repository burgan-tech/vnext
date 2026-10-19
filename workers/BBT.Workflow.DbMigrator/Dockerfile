# Base image using Alpine (ASP.NET runtime - Worker SDK project, no Kestrel)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23 AS base
WORKDIR /app

# Native libraries the base image leaves out:
# - krb5-libs: Npgsql probes GSSAPI on every connection (GssEncryptionMode=Prefer by default);
#   without libgssapi_krb5.so.2 the .NET native shim prints "Cannot load library libgssapi_krb5.so.2"
#   to stderr. The connection still succeeds, but the line reads as a startup failure.
# - icu-libs + icu-data-full: lets an operator set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 (needed by
#   Microsoft.Data.SqlClient in script plugins, which rejects invariant mode). Without ICU the runtime
#   FailFasts at startup. icu-data-en alone would silently format tr-TR as English. The image default
#   stays invariant (base image sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true).
RUN apk add --no-cache krb5-libs icu-libs icu-data-full

# Create non-root user for security
RUN adduser -D workflowuser && chown -R workflowuser:workflowuser /app

USER workflowuser

# Build image (pinned to 10.0.2xx band — SDK 10.0.300 produces assets format v4 incompatible with PostSharp)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.203-alpine3.23 AS build

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

WORKDIR /workers
COPY workers/ .
RUN dotnet restore "BBT.Workflow.DbMigrator/BBT.Workflow.DbMigrator.csproj"
WORKDIR "/workers/BBT.Workflow.DbMigrator"
ARG configuration=Release
RUN dotnet publish "BBT.Workflow.DbMigrator.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

# Final image
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false

# Run-once job: migrate schemas then exit (exit code 0 = success, 1 = failure)
ENTRYPOINT ["dotnet", "BBT.Workflow.DbMigrator.dll"]
