#!/bin/sh
# Public, self-contained preview. Deliberately independent of local signing tools.
set -eu
root="$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd -P)"
cd "$root"
[ "$(uname -s)" = "Darwin" ] || { printf '%s\n' 'Build this package on macOS.' >&2; exit 1; }
[ "$(uname -m)" = "arm64" ] || { printf '%s\n' 'This release targets Apple Silicon (osx-arm64).' >&2; exit 1; }
for tool in dotnet swift swiftc iconutil codesign python3 ditto; do
    command -v "$tool" >/dev/null 2>&1 || { printf 'Missing build tool: %s\n' "$tool" >&2; exit 1; }
done
output="$root/artifacts/public-release"
mkdir -p "$root/artifacts" "$output"
[ ! -L "$output" ] || { printf '%s\n' 'Public output must not be a symlink.' >&2; exit 1; }
staging="$(mktemp -d "$root/artifacts/public-mac-stage.XXXXXX")"
publish="$staging/publish"
prepared="$staging/LinkBridge.app"
bundle="$output/LinkBridge.app"
dotnet publish src/LinkBridge.App/LinkBridge.App.csproj -c Release -r osx-arm64 --self-contained true \
    -p:UseAppHost=true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false \
    -p:PathMap="$root=/src/linkbridge" -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:Version=0.0.1 -p:AssemblyVersion=0.0.1.0 -p:FileVersion=0.0.1.0 -o "$publish"
mkdir -p "$prepared/Contents/MacOS" "$prepared/Contents/Resources"
cp -R "$publish/." "$prepared/Contents/MacOS/"
swift tools/mac-app/render-app-icon.swift src/LinkBridge.App/Assets/linkbridge.ico "$staging/LinkBridge.iconset"
iconutil -c icns "$staging/LinkBridge.iconset" -o "$prepared/Contents/Resources/LinkBridge.icns"
swiftc -O -target arm64-apple-macosx13.0 -debug-prefix-map "$root=/src/linkbridge" \
    -framework AppKit -framework CryptoKit -framework UniformTypeIdentifiers \
    tools/mac-app/FileDragHelper.swift -o "$prepared/Contents/MacOS/LinkBridge.FileDragHelper"
cp tools/mac-app/Info.plist "$prepared/Contents/Info.plist"
chmod +x "$prepared/Contents/MacOS/LinkBridge.App" "$prepared/Contents/MacOS/LinkBridge.FileDragHelper"
# Ad-hoc signing uses no certificate, keychain, personal identity or leaf pin.
# Inspect native binary magic rather than relying on filenames/extensions.
python3 - "$prepared" <<'PY'
import pathlib, plistlib, subprocess, sys
app = pathlib.Path(sys.argv[1])
info = plistlib.loads((app/'Contents/Info.plist').read_bytes())
if info.get('CFBundleIdentifier') != 'com.linkbridge.desktop' or info.get('CFBundleShortVersionString') != '0.0.1':
    raise SystemExit('Public bundle identifier/version mismatch.')
# Align the public runtime requirement with currently supported .NET 10 macOS.
# The private/local source Info.plist and installed application are not modified.
info['LSMinimumSystemVersion'] = '15.0'
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(info, fmt=plistlib.FMT_XML, sort_keys=False))
magics = {bytes.fromhex(x) for x in ('feedface','cefaedfe','feedfacf','cffaedfe','cafebabe','bebafeca','cafebabf','bfbafeca')}
binaries = []
for item in (app/'Contents/MacOS').rglob('*'):
    if item.is_symlink():
        if not item.resolve().is_relative_to(app.resolve()):
            raise SystemExit('External bundle symlink rejected.')
        continue
    if item.is_file():
        if item.suffix.lower() in ('.pdb','.pem','.key','.p12','.pfx'):
            raise SystemExit('Debug or private signing material found in public app.')
        with item.open('rb') as stream:
            if stream.read(4) in magics: binaries.append(item)
for item in sorted(binaries, key=lambda path:(path.name == 'LinkBridge.App', -len(path.parts), str(path))):
    subprocess.run(['/usr/bin/codesign','--force','--sign','-','--timestamp=none',str(item)], check=True)
subprocess.run(['/usr/bin/codesign','--force','--sign','-','--timestamp=none','--identifier','com.linkbridge.desktop',str(app)], check=True)
subprocess.run(['/usr/bin/codesign','--verify','--deep','--strict',str(app)], check=True)
metadata = subprocess.run(['/usr/bin/codesign','-d','--verbose=4',str(app)],capture_output=True,text=True,check=True)
details = metadata.stdout + metadata.stderr
if 'Signature=adhoc' not in details or 'Authority=' in details:
    raise SystemExit('Public bundle must be ad-hoc only; no personal signing identity is permitted.')
PY
if [ -e "$bundle" ]; then
    [ ! -L "$bundle" ] || { printf '%s\n' 'Refusing a symlink at public bundle output.' >&2; exit 1; }
    mv "$bundle" "$staging/previous-public-LinkBridge.app"
fi
if ! mv "$prepared" "$bundle"; then
    if [ -d "$staging/previous-public-LinkBridge.app" ]; then mv "$staging/previous-public-LinkBridge.app" "$bundle"; fi
    exit 1
fi
cp tools/distribution/Install.command "$output/Install.command"
cp tools/distribution/README.md "$output/README.md"
chmod +x "$output/Install.command"
python3 tools/distribution/finish-mac-release.py
printf '%s\n' 'Prepared artifacts/public-release/LinkBridge.app and Install.command.'
printf '%s\n' 'Self-contained Apple Silicon preview, ad-hoc signed; no Developer ID or notarization.'
printf '%s\n' 'No installed app, user configuration, keychain or system permission was changed.'
