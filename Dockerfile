# syntax=docker/dockerfile:1
# Build context: repo root.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TranscodeEngine.Api/TranscodeEngine.Api.csproj src/TranscodeEngine.Api/
RUN dotnet restore src/TranscodeEngine.Api/TranscodeEngine.Api.csproj

COPY src/ src/
RUN dotnet publish src/TranscodeEngine.Api/TranscodeEngine.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg + the VA-API userspace stack. Hardware encoding runs against a /dev/dri render node passed
# through by the Hosty manifest's `devices`. mesa-va-drivers covers both Intel (iHD/i965) and AMD
# (radeonsi); the host kernel driver behind the passed-through device does the actual work. With no
# device present the engine still runs and falls back to software encoding.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        vainfo \
        libva2 \
        libva-drm2 \
        mesa-va-drivers \
        libdrm2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
