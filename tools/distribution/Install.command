#!/bin/sh
# Install only the adjacent public app into the current user's Applications.
set -eu
directory="$(CDPATH= cd -- "$(dirname "$0")" && pwd -P)"
candidate="$directory/LinkBridge.app"
applications="$HOME/Applications"
target="$applications/LinkBridge.app"
[ "$(uname -s)" = "Darwin" ] || { printf '%s\n' 'Use this installer on macOS.' >&2; exit 1; }
[ "$(/usr/bin/sw_vers -productVersion | /usr/bin/cut -d. -f1)" -ge 15 ] || { printf '%s\n' 'The public .NET 10 preview requires macOS 15 or later.' >&2; exit 1; }
[ -d "$candidate" ] && [ ! -L "$candidate" ] || { printf '%s\n' 'Place Install.command beside the release LinkBridge.app.' >&2; exit 1; }
/usr/bin/codesign --verify --deep --strict "$candidate"
identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$candidate/Contents/Info.plist")"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$candidate/Contents/Info.plist")"
[ "$identifier" = "com.linkbridge.desktop" ] && [ "$version" = "0.0.1" ] || { printf '%s\n' 'Unexpected release identifier/version; installation refused.' >&2; exit 1; }
signature="$(/usr/bin/codesign -d --verbose=4 "$candidate" 2>&1)"
printf '%s\n' "$signature" | /usr/bin/grep -q '^Signature=adhoc$' || { printf '%s\n' 'This installer accepts the public ad-hoc preview only.' >&2; exit 1; }
if printf '%s\n' "$signature" | /usr/bin/grep -q '^Authority='; then
    printf '%s\n' 'A certificate-signed app is not this public preview; installation refused.' >&2; exit 1
fi
[ ! -L "$applications" ] && [ ! -L "$target" ] || { printf '%s\n' 'Applications or the target app is a symlink; installation refused.' >&2; exit 1; }
mkdir -p "$applications"
staging="$(mktemp -d "$applications/.LinkBridge-public-install.XXXXXX")"
if [ -e "$target" ]; then
    [ -d "$target" ] || { printf '%s\n' 'The target exists and is not an application bundle.' >&2; exit 1; }
    existing_id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$target/Contents/Info.plist")"
    [ "$existing_id" = "$identifier" ] || { printf '%s\n' 'Another app occupies the target path; installation refused.' >&2; exit 1; }
    /usr/bin/codesign --verify --deep --strict "$target"
    source_req="$(/usr/bin/codesign -d -r- "$candidate" 2>&1 | /usr/bin/sed -n '/^designated => /p')"
    target_req="$(/usr/bin/codesign -d -r- "$target" 2>&1 | /usr/bin/sed -n '/^designated => /p')"
    [ -n "$source_req" ] && [ -n "$target_req" ] || { printf '%s\n' 'Unable to compare existing signing identity; installation refused.' >&2; exit 1; }
    /usr/bin/csreq -r "$source_req" -b "$staging/source.req"
    /usr/bin/csreq -r "$target_req" -b "$staging/target.req"
    if ! /usr/bin/cmp -s "$staging/source.req" "$staging/target.req"; then
        printf '%s\n' 'The existing app has a different signing requirement. No app or authorization was replaced.' >&2
        printf '%s\n' 'Review/build the source with your own stable signing setup before choosing a manual migration.' >&2
        exit 1
    fi
fi
/usr/bin/ditto "$candidate" "$staging/LinkBridge.app"
/usr/bin/codesign --verify --deep --strict "$staging/LinkBridge.app"
backup=''
if [ -d "$target" ]; then
    backup="$applications/LinkBridge.before-$(date -u +%Y%m%d-%H%M%S)-$$.app"
    mv "$target" "$backup"
fi
if ! mv "$staging/LinkBridge.app" "$target"; then
    if [ -n "$backup" ] && [ -d "$backup" ]; then mv "$backup" "$target"; fi
    exit 1
fi
printf '%s\n' 'Installed ~/Applications/LinkBridge.app. Existing configuration was preserved.'
if [ -n "$backup" ]; then printf '%s\n' 'The previous application was retained beside it as a backup.'; fi
printf '%s\n' 'Open the app in Finder. Complete any macOS permission review yourself when prompted.'
printf '%s\n' 'This is an ad-hoc developer preview without Developer ID signing or notarization.'
