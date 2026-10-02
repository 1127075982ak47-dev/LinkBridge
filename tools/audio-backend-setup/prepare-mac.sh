#!/bin/sh
# Stage/inspect only. No sudo, installer invocation, defaults mutation, audio restart or reboot.
set -eu
mode=${1:-inspect}
case "$mode" in inspect|stage) ;; *) echo 'usage: sh prepare-mac.sh [inspect|stage] [new-backup.json]' >&2; exit 2;; esac
base=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cache="$HOME/Library/Caches/LinkBridge/BackendSetup"
mkdir -p "$cache"
backup=${2:-"$cache/mac-defaults-$(date -u +%Y%m%dT%H%M%SZ)-$$.json"}
python3 "$base/mac-defaults.py" snapshot "$backup"
echo "BACKUP=$backup"
pkg="$cache/BlackHole16ch-0.7.1.pkg"
uninstaller="$cache/BlackHole16ch-Uninstaller.pkg"
if [ "$mode" = stage ]; then
    curl --fail --location --connect-timeout 15 --max-time 120 --output "$pkg" 'https://existential.audio/downloads/BlackHole16ch-0.7.1.pkg'
    curl --fail --location --connect-timeout 15 --max-time 120 --output "$uninstaller" 'https://existential.audio/downloads/BlackHole16ch-Uninstaller.pkg'
fi
if [ ! -f "$pkg" ]; then
    echo 'Package not staged. Use stage to download and verify; no installation performed.'
    exit 0
fi
actual=$(shasum -a 256 "$pkg" | awk '{print $1}')
expected=57254e2f76cd40db7f3f715238b1a2cb2bd08819d38abf4087f2944f71a3641a
[ "$actual" = "$expected" ] || { echo 'Package checksum differs; stop and review the new package.' >&2; exit 1; }
sig=$(pkgutil --check-signature "$pkg")
printf '%s\n' "$sig"
printf '%s\n' "$sig" | grep -F 'Developer ID Installer: Existential Audio Inc. (Q5C99V536K)' >/dev/null
printf '%s\n' "$sig" | grep -F 'Status: signed by a developer certificate issued by Apple for distribution' >/dev/null
spctl --assess --verbose=2 --type install "$pkg"
if [ -f "$uninstaller" ]; then
    [ "$(shasum -a 256 "$uninstaller" | awk '{print $1}')" = b978879d3820f83e25639f8009c0ad573a7558e412b29d153dcbb74ce2259d07 ] || { echo 'Uninstaller review checksum differs.' >&2; exit 1; }
    uninstall_sig=$(pkgutil --check-signature "$uninstaller")
    printf '%s\n' "$uninstall_sig"
    printf '%s\n' "$uninstall_sig" | grep -F 'Developer ID Installer: Existential Audio Inc. (Q5C99V536K)' >/dev/null
    printf '%s\n' "$uninstall_sig" | grep -F 'Status: signed by a developer certificate issued by Apple for distribution' >/dev/null
    spctl --assess --verbose=2 --type install "$uninstaller"
    echo "VERIFIED_UNINSTALLER=$uninstaller"
    echo 'UNINSTALL WARNING: the official 16ch uninstaller also restarts coreaudiod, interrupting all Mac audio. It is not executed by this script.'
fi
pkgutil --pkg-info audio.existential.BlackHole2ch || true
pkgutil --pkg-info audio.existential.BlackHole16ch || true
echo "VERIFIED_PACKAGE=$pkg"
echo 'PLAN ONLY: after saving work and scheduling a restart, open this verified package in Installer and install ONLY BlackHole16ch. Existing BlackHole2ch is retained, but the vendor postinstall chmod/chown matches all BlackHole*ch.driver bundles. Reboot required; nothing installed here.'
printf 'AFTER_INSTALL_DRY_RUN: python3 "%s/mac-defaults.py" restore "%s"\n' "$base" "$backup"
echo 'Only explicit restore --apply writes the original default UIDs; do not make BlackHole16ch the system microphone or speaker default.'
