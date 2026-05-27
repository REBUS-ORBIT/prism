# PRISM server image.
#
# Multi-stage build:
#   1. web-builder — builds both Vue SPAs into web/dist
#   2. server-builder — TypeScript compile to server/dist (depends on
#      shared/ for the agent-protocol contract types and JSON schemas)
#   3. runtime — slim node:22 with prod deps, the compiled server, the
#      built SPAs, and the Drizzle migrations
#
# Image: ghcr.io/rebus-orbit/prism-server:<tag>

# -------- web-builder --------
FROM node:22-alpine AS web-builder
WORKDIR /build/web
COPY web/package.json web/package-lock.json* ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# -------- server-builder --------
FROM node:22-alpine AS server-builder
WORKDIR /build
COPY shared ./shared
COPY server/package.json server/package-lock.json* server/tsconfig.json ./server/
WORKDIR /build/server
RUN npm ci --no-audit --no-fund
COPY server/src ./src
COPY server/scripts ./scripts
RUN npm run build

# -------- runtime --------
FROM node:22-alpine AS runtime
WORKDIR /prism

RUN apk add --no-cache wget tini

COPY server/package.json server/package-lock.json* ./
RUN npm ci --omit=dev --no-audit --no-fund && npm cache clean --force

COPY --from=server-builder /build/server/dist     ./dist
COPY --from=server-builder /build/server/src/db/migrations ./migrations
COPY --from=server-builder /build/shared          /shared
COPY --from=web-builder    /build/web/dist        ./web-dist

# Agent install scripts shipped to admins via the Workstations download UI.
# Bundled into the image at build time so the route always serves the
# version that matches the checked-in agent/install/*.ps1 sources.
COPY agent/install/install.ps1     ./agent-install/install.ps1
COPY agent/install/uninstall.ps1   ./agent-install/uninstall.ps1
COPY agent/src/PRISM.Agent/agent-config.example.json ./agent-install/agent-config.example.json

# Phase K: bundle the narrative docs into the image so
# /docs/portal-integration can serve them. Read-only at runtime; ships
# whatever was committed at the tag this image was built from.
COPY docs/                         ./docs/

ENV NODE_ENV=production
ENV PORT=8765
ENV WEB_DIST_DIR=/prism/web-dist
ENV UPLOAD_DIR=/var/lib/prism/uploads
ENV MIGRATIONS_DIR=/prism/migrations
ENV AGENT_INSTALL_DIR=/prism/agent-install
ENV DOCS_DIR=/prism/docs

EXPOSE 8765

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD wget -qO- http://localhost:${PORT}/health || exit 1

ENTRYPOINT ["/sbin/tini", "--"]
# TypeScript preserved the source tree under dist/ because the server imports
# from ../shared/ — the real entry is dist/server/src/main.js.
CMD ["node", "dist/server/src/main.js"]
