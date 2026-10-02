#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)
output="${LINKBRIDGE_MAC_CAMERA_OUT:-$repo/artifacts/mac-camera-unsigned}"
"$output/LinkBridgeCameraContractProbe"
"$output/LinkBridgeCameraHost.app/Contents/MacOS/LinkBridgeCameraHost" --status
if [ "${1:-}" = "--decode" ]; then
  test -n "${2:-}" || { echo 'Provide absolute FFmpeg path' >&2; exit 2; }
  "$output/LinkBridgeCameraDecodeProbe" "$2"
  "$output/LinkBridgeCameraDecodeProbe" "$2" --reject-1080
fi
# Deliberately do not call --activate, --deactivate, --observe or --bridge.
