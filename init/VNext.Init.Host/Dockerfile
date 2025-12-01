FROM node:20-alpine

ENV VNEXT_CORE_RUNTIME_VERSION=latest \
    NPM_REGISTRY=https://registry.npmjs.org/ \
    NPM_CONFIG_CACHE=/app/.npm-cache \
    HOME=/app

WORKDIR /app

# Tools + permissions
RUN apk update && apk upgrade && apk add --no-cache bash curl jq \
    && mkdir -p /app/.npm-cache /app/custom-components \
    && chown -R node:node /app && chmod -R 777 /app

# API server copy
COPY VNext.Init.Host/package-api-server.js /app/package-api-server.js
RUN chmod +x /app/package-api-server.js

USER node

# Health check - checks API server health endpoint
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:3000/health || exit 1

ENTRYPOINT ["node", "/app/package-api-server.js"]

