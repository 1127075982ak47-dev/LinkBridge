#!/bin/sh
set -eu
directory="$(CDPATH= cd -- "$(dirname "$0")" && pwd -P)"
root="$(CDPATH= cd -- "$directory/.." && pwd -P)"
cd "$root"
[ -f SHA256SUMS.txt ] || { printf '%s\n' 'Package manifest missing.' >&2; exit 1; }
/usr/bin/shasum -a 256 -c SHA256SUMS.txt
printf '%s\n' 'PASS package integrity. This is not a publisher signature.'
