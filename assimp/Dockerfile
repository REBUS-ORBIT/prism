# prism-assimp -- sidecar pre-conversion service.
#
# Why a multi-stage build with assimp pinned from source?  The Debian
# packaged libassimp is conservative on format support (often missing
# .usdz, .blend internals, etc.).  Building from source lets us pin
# exactly which formats are turned on via cmake flags and stay current.
# When the requirements settle we can switch to the official
# `assimp/assimp` image, but for now hand-rolling is clearer.

# ---------- stage 1: build libassimp ----------
FROM debian:bookworm-slim AS assimp-build
ARG ASSIMP_VERSION=5.4.3
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates \
        zlib1g-dev libpng-dev libjpeg62-turbo-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
RUN git clone --depth 1 --branch v${ASSIMP_VERSION} https://github.com/assimp/assimp.git \
    && cd assimp \
    && cmake -S . -B build \
        -DASSIMP_BUILD_TESTS=OFF \
        -DASSIMP_BUILD_ASSIMP_TOOLS=OFF \
        -DASSIMP_INSTALL=ON \
        -DASSIMP_NO_EXPORT=OFF \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/opt/assimp \
    && cmake --build build --parallel $(nproc) \
    && cmake --install build

# ---------- stage 2: runtime ----------
FROM python:3.12-slim-bookworm AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
        libpng16-16 libjpeg62-turbo zlib1g \
        ca-certificates curl tini \
    && rm -rf /var/lib/apt/lists/*
COPY --from=assimp-build /opt/assimp /opt/assimp
ENV LD_LIBRARY_PATH=/opt/assimp/lib \
    PYASSIMP_LIB=/opt/assimp/lib/libassimp.so \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY src ./src

EXPOSE 8088
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -sf http://localhost:8088/health || exit 1
ENTRYPOINT ["/usr/bin/tini","--"]
CMD ["uvicorn","src.assimp_service.main:app","--host","0.0.0.0","--port","8088"]
