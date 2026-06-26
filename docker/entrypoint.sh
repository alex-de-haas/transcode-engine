#!/bin/sh
# Log the hardware the container can actually see (best-effort), then launch the control API as PID 1.
#
# Hardware encoding needs a /dev/dri render node passed through from the host (the Hosty manifest's
# `devices`). This logs whether that worked so a misconfigured passthrough is obvious in the app logs;
# it never fails the start — with no device the engine still runs and uses software encoding.
set -eu

DEVICE="${VAAPI_DEVICE:-/dev/dri/renderD128}"

if [ -d /dev/dri ]; then
  echo "transcode-engine: /dev/dri present:"
  ls -l /dev/dri 2>/dev/null || true
  # Best-effort capability probe; prints the supported VA entrypoints when VAAPI is wired up correctly.
  vainfo --display drm --device "$DEVICE" 2>&1 | sed 's/^/vainfo: /' \
    || echo "transcode-engine: vainfo failed for $DEVICE (software encoding still works)."
else
  echo "transcode-engine: /dev/dri not present — no VAAPI device passed through; software encoding only."
fi

exec dotnet TranscodeEngine.Api.dll
