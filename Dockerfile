# syntax=docker/dockerfile:1
# Build context: repo root.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY src/TranscodeEngine.Api/TranscodeEngine.Api.csproj src/TranscodeEngine.Api/
RUN dotnet restore src/TranscodeEngine.Api/TranscodeEngine.Api.csproj

COPY src/ src/
RUN dotnet publish src/TranscodeEngine.Api/TranscodeEngine.Api.csproj -c Release -o /app/publish --no-restore

# dovi_tool rewrites Dolby Vision RPU metadata — the profile 7 → 8.1 conversion
# (docs/features/dolby-vision-conversion). A pinned static release, checked against the SHA-256 GitHub
# publishes for the asset, fetched here because the SDK image has curl and the runtime image need not.
# TARGETARCH is BuildKit's: each platform's build runs natively, so it names the platform being built.
ARG DOVI_TOOL_VERSION=2.3.3
ARG DOVI_TOOL_SHA256_AMD64=5dae82cb2becd3b9fd726127f936a8d32635e60746d16238fdfded12aa05988c
ARG DOVI_TOOL_SHA256_ARM64=daf538c275f4e702219ce8eb61db28382193ac9d0126e1ef4185a88303af4485
RUN set -eu; \
    case "${TARGETARCH}" in \
      amd64) triple=x86_64-unknown-linux-musl; sha="${DOVI_TOOL_SHA256_AMD64}" ;; \
      arm64) triple=aarch64-unknown-linux-musl; sha="${DOVI_TOOL_SHA256_ARM64}" ;; \
      *) echo "dovi_tool: no release asset for ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/dovi_tool.tar.gz \
      "https://github.com/quietvoid/dovi_tool/releases/download/${DOVI_TOOL_VERSION}/dovi_tool-${DOVI_TOOL_VERSION}-${triple}.tar.gz"; \
    echo "${sha}  /tmp/dovi_tool.tar.gz" | sha256sum -c -; \
    mkdir -p /tmp/dovi_tool /out; \
    tar -xzf /tmp/dovi_tool.tar.gz -C /tmp/dovi_tool; \
    install -m 0755 "$(find /tmp/dovi_tool -type f -name dovi_tool | head -n 1)" /out/dovi_tool; \
    /out/dovi_tool --version

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg + the VA-API userspace stack. Hardware encoding runs against a /dev/dri render node passed
# through by the Hosty manifest's `devices`. mesa-va-drivers covers both Intel (iHD/i965) and AMD
# (radeonsi); the host kernel driver behind the passed-through device does the actual work. With no
# device present the engine still runs and falls back to software encoding.
#
# mkvtoolnix (mkvextract, mkvmerge) carries the picture through a Dolby Vision profile 7 → 8.1 conversion
# together with dovi_tool below: mkvextract writes the dual-layer stream, mkvmerge assembles the output
# and writes the Matroska Dolby Vision mapping ffmpeg cannot. Without them the engine still runs and
# refuses that one job option; GET /hardware reports which under `tools`.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        vainfo \
        libva2 \
        libva-drm2 \
        mesa-va-drivers \
        libdrm2 \
        mkvtoolnix \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=build /out/dovi_tool /usr/local/bin/dovi_tool
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
