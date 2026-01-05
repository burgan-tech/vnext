FROM node:20-slim

ENV VNEXT_CORE_RUNTIME_VERSION=latest \
    NPM_REGISTRY=https://registry.npmjs.org/ \
    NPM_CONFIG_CACHE=/app/.npm-cache \
    HOME=/app \
    # Server timeout configuration (milliseconds) - default 10 minutes
    SERVER_TIMEOUT_MS=600000 \
    SERVER_KEEP_ALIVE_TIMEOUT_MS=600000 \
    SERVER_HEADERS_TIMEOUT_MS=610000

WORKDIR /app

# Install required packages (Debian-based - better QEMU/multi-arch compatibility)
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl jq \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

# Set up directories and permissions
RUN mkdir -p /app/.npm-cache \
    && chown -R node:node /app \
    && chmod -R 777 /app

# API server copy
COPY --chown=node:node init/VNext.Init.Host/package-api-server.js /app/package-api-server.js
RUN chmod +x /app/package-api-server.js

USER node

# Health check - checks API server health endpoint
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:3000/health || exit 1

ENTRYPOINT ["node", "/app/package-api-server.js"]