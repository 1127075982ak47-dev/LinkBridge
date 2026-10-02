#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mkdir -p "$PWD/artifacts"
staging="$(mktemp -d "$PWD/artifacts/mac-stage.XXXXXX")"
output="$staging/publish"
bundle="$PWD/artifacts/LinkBridge.app"
prepared="$staging/LinkBridge.app"
identity="${LINKBRIDGE_SIGN_IDENTITY:-$(python3 tools/mac-signing/ready-identity.py)}"
dotnet publish src/LinkBridge.App/LinkBridge.App.csproj -c Release -r osx-arm64 --self-contained false -p:UseAppHost=true -p:PublishSingleFile=true -o "$output"
mkdir -p "$prepared/Contents/MacOS" "$prepared/Contents/Resources"
iconset="$PWD/artifacts/mac-icon/LinkBridge.iconset"
swift tools/mac-app/render-app-icon.swift src/LinkBridge.App/Assets/linkbridge.ico "$iconset"
iconutil -c icns "$iconset" -o "$prepared/Contents/Resources/LinkBridge.icns"
cp -R "$output/." "$prepared/Contents/MacOS/"
swiftc -O -target arm64-apple-macosx13.0 -framework AppKit -framework CryptoKit -framework UniformTypeIdentifiers tools/mac-app/FileDragHelper.swift -o "$prepared/Contents/MacOS/LinkBridge.FileDragHelper"
cp tools/mac-app/Info.plist "$prepared/Contents/Info.plist"
chmod +x "$prepared/Contents/MacOS/LinkBridge.App"
# Sign nested native binaries before the outer bundle. Keep the same legitimate
# identity across releases; ad-hoc builds are preview artifacts only by default.
python3 - "$prepared/Contents/MacOS" "-" <<'PY'
import pathlib, subprocess, sys
mach_o = {bytes.fromhex(value) for value in (
    'feedface', 'cefaedfe', 'feedfacf', 'cffaedfe',
    'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca')}
binaries = []
for binary in pathlib.Path(sys.argv[1]).rglob('*'):
    if binary.is_file() and not binary.is_symlink():
        with binary.open('rb') as source:
            if source.read(4) in mach_o:
                binaries.append(binary)
# Inspect the binary format instead of relying on extensions. Managed DLLs are
# bundled into apphost; native libraries remain separate and are signed first.
for binary in sorted(binaries, key=lambda item: (item.name == 'LinkBridge.App', str(item))):
    subprocess.run(['codesign', '--force', '--sign', sys.argv[2], str(binary)], check=True)
PY
codesign --force --sign - "$prepared"
if [ "$identity" != "-" ]; then
  # Sign the complete candidate with the pinned leaf and explicit stable DR.
  # The signing helper checks current user Code Signing trust before copying.
  fixed="$(python3 tools/mac-signing/ready-identity.py)"
  [ "$identity" = "$fixed" ] || { printf '%s\n' 'Signing identity differs from the fixed desktop identity.' >&2; exit 1; }
  signed="$staging/signed/LinkBridge.app"
  mkdir -p "$staging/signed"
  python3 tools/mac-signing/sign-copy.py "$prepared" "$signed" --apply
  prepared="$signed"
fi
codesign --verify --deep --strict "$prepared"
codesign -d -r- "$prepared" > "$staging/designated-requirement.txt" 2>&1
if [ -e "$bundle" ]; then
  mv "$bundle" "$staging/previous-LinkBridge.app"
fi
if ! mv "$prepared" "$bundle"; then
  if [ -e "$staging/previous-LinkBridge.app" ]; then
    mv "$staging/previous-LinkBridge.app" "$bundle"
  fi
  exit 1
fi
cp "$staging/designated-requirement.txt" "$PWD/artifacts/mac-designated-requirement.txt"
if [ "$identity" = "-" ]; then
  printf '%s\n' 'Prepared ad-hoc preview: a new build can require device permissions again.' >&2
fi
if [ "${LINKBRIDGE_INSTALL:-0}" = "1" ]; then
  sh tools/mac-app/install-validated-app.sh
fi
printf 'Built %s\n' "$bundle"
