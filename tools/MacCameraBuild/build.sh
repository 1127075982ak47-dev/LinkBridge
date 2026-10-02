#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)
source="$repo/src/LinkBridge.VirtualCamera.Mac"
output="${LINKBRIDGE_MAC_CAMERA_OUT:-$repo/artifacts/mac-camera-unsigned}"
mkdir -p "$output"
sdk=$(xcrun --sdk macosx --show-sdk-path)
common="$source/Shared/LBCameraContract.m"
extension="$output/LinkBridgeCameraHost.app/Contents/Library/SystemExtensions/com.linkbridge.camera.extension.systemextension"
host="$output/LinkBridgeCameraHost.app"
mkdir -p "$host/Contents/MacOS" "$extension/Contents/MacOS"
# Compile only. No codesign, Apple account, trust, activation, or installation.
xcrun clang -fobjc-arc -fblocks -Wall -Wextra -Werror -isysroot "$sdk" -mmacosx-version-min=13.0 \
  "$common" "$source/Extension/LBProvider.m" "$source/Extension/main.m" \
  -framework Foundation -framework CoreMediaIO -framework CoreMedia -framework CoreVideo -framework IOSurface -framework Security \
  -o "$extension/Contents/MacOS/LinkBridgeCameraExtension"
xcrun clang -fobjc-arc -fblocks -Wall -Wextra -Werror -isysroot "$sdk" -mmacosx-version-min=13.0 \
  "$common" "$source/Host/LBSinkConnection.m" "$source/Host/LBUDPReceiver.m" "$source/Host/main.m" \
  -framework Foundation -framework AppKit -framework SystemExtensions -framework CoreMediaIO -framework CoreMedia -framework CoreVideo -framework IOSurface -framework Security \
  -o "$host/Contents/MacOS/LinkBridgeCameraHost"
xcrun clang -fobjc-arc -fblocks -Wall -Wextra -Werror -isysroot "$sdk" -mmacosx-version-min=13.0 \
  "$common" "$source/Tests/ContractProbe.m" \
  -framework Foundation -framework CoreMediaIO -framework CoreMedia -framework CoreVideo -framework IOSurface \
  -o "$output/LinkBridgeCameraContractProbe"
xcrun clang -fobjc-arc -fblocks -Wall -Wextra -Werror -isysroot "$sdk" -mmacosx-version-min=13.0 \
  "$common" "$source/Host/LBUDPReceiver.m" "$source/Tests/DecodeProbe.m" \
  -framework Foundation -framework CoreMediaIO -framework CoreMedia -framework CoreVideo -framework IOSurface \
  -o "$output/LinkBridgeCameraDecodeProbe"
cp "$source/Resources/Host-Info.plist" "$host/Contents/Info.plist"
group="${LINKBRIDGE_CAMERA_GROUP_ID:-UNSIGNED.com.linkbridge.camera}"
case "$group" in *[!A-Za-z0-9.-]*|'') echo 'Invalid group identifier' >&2; exit 2;; esac
sed "s/@GROUP_ID@/$group/g" "$source/Resources/Extension-Info.plist.in" > "$extension/Contents/Info.plist"
sed "s/@GROUP_ID@/$group/g" "$source/Resources/Host.entitlements.in" > "$output/Host.entitlements"
sed "s/@GROUP_ID@/$group/g" "$source/Resources/Extension.entitlements.in" > "$output/Extension.entitlements"
plutil -lint "$host/Contents/Info.plist" "$extension/Contents/Info.plist" "$output/Host.entitlements" "$output/Extension.entitlements"
printf 'BUILD_OK compile-only unsigned package: %s\n' "$host"
