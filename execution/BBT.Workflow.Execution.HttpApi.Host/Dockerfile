# Ubuntu/glibc is required so NumPy, pandas, SciPy, and scikit-learn can use
# their published manylinux wheels on both amd64 and arm64.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    ASPNETCORE_URLS=http://+:5000 \
    PYTHONNET_PYDLL=libpython3.12.so.1.0 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_INDEX=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    OMP_NUM_THREADS=1 \
    OPENBLAS_NUM_THREADS=1 \
    MKL_NUM_THREADS=1 \
    Python__Enabled=true

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libpython3.12 \
        python3.12 \
        python3.12-venv \
        util-linux \
        wget \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/vnext-python
COPY execution/python/requirements.lock ./requirements.lock
RUN python3.12 -m venv /opt/vnext-python/venv \
    && env -u PIP_NO_INDEX /opt/vnext-python/venv/bin/python -m pip install \
        --no-cache-dir --only-binary=:all: --require-hashes -r requirements.lock \
    && /opt/vnext-python/venv/bin/python -c "import numpy, pandas, sklearn" \
    && /opt/vnext-python/venv/bin/python -m pip uninstall -y pip setuptools wheel

COPY execution/python/runner.py execution/python/vnext_runner.py ./

RUN groupadd --system --gid 10001 workflowuser \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent workflowuser \
    && chmod -R a=rX /opt/vnext-python

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

# Grpc.Tools ships glibc-linked protoc/grpc plugin binaries that cannot exec on musl —
# point its documented env overrides at Alpine's native protobuf toolchain instead.
RUN apk add --no-cache protobuf grpc-plugins
ENV PROTOBUF_PROTOC=/usr/bin/protoc \
    GRPC_PROTOC_PLUGIN=/usr/bin/grpc_csharp_plugin

# Grpc.Tools ships glibc-linked protoc/grpc plugin binaries that cannot exec on musl —
# point its documented env overrides at Alpine's native protobuf toolchain instead.
RUN apk add --no-cache protobuf grpc-plugins
ENV PROTOBUF_PROTOC=/usr/bin/protoc \
    GRPC_PROTOC_PLUGIN=/usr/bin/grpc_csharp_plugin

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
