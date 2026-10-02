#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
bundle="$PWD/artifacts/LinkBridge.app"
codesign --verify --deep --strict "$bundle"
# Prevent a routine build from silently replacing the identity to which macOS
# granted device access. This checks code identity only, never reads/modifies TCC.
python3 - <<'PY'
import os, subprocess
candidate = os.path.abspath('artifacts/LinkBridge.app')
installed = os.path.expanduser('~/Desktop/LinkBridge.app')
def requirement(path):
    result = subprocess.run(['codesign', '-d', '-r-', path], capture_output=True, text=True, check=True)
    for line in (result.stdout + '\n' + result.stderr).splitlines():
        line = line.lstrip('# ').strip()
        if line.startswith('designated =>'):
            return line
    raise SystemExit('Installation stopped: codesign did not report a designated requirement.')
new = requirement(candidate)
if os.path.isdir(installed):
    old = requirement(installed)
    if old != new and os.environ.get('LINKBRIDGE_ALLOW_IDENTITY_CHANGE') != '1':
        raise SystemExit('Installation stopped: the code identity changed. Prepare a persistent signing identity, or explicitly set LINKBRIDGE_ALLOW_IDENTITY_CHANGE=1 for a one-time migration that may need permissions again.')
PY
# Prepare and validate a complete replacement before closing the running app.
staging="$(mktemp -d "$HOME/Desktop/.linkbridge-install.XXXXXX")"
ditto "$bundle" "$staging/LinkBridge.app"
codesign --verify --deep --strict "$staging/LinkBridge.app"
# Stop only the currently installed LinkBridge process and its own FFmpeg children.
# Do not close another app or a camera/system service.
python3 - <<'PY'
import os, signal, subprocess, time
expected = os.path.expanduser('~/Desktop/LinkBridge.app/Contents/MacOS/LinkBridge.App')
rows = subprocess.check_output(['ps', '-axo', 'pid=,ppid=,comm='], text=True).splitlines()
processes = [row.strip().split(None, 2) for row in rows]
owners = {int(row[0]) for row in processes if len(row) == 3 and row[2] == expected}
children = {int(row[0]) for row in processes if len(row) == 3 and int(row[1]) in owners and row[2].endswith('/ffmpeg')}
for pid in owners | children:
    try: os.kill(pid, signal.SIGTERM)
    except ProcessLookupError: pass
deadline = time.monotonic() + 12
while time.monotonic() < deadline:
    alive = set()
    for pid in owners | children:
        try: os.kill(pid, 0); alive.add(pid)
        except ProcessLookupError: pass
    if not alive: break
    # FFmpeg can defer SIGTERM while macOS is awaiting privacy authorization.
    # Force only children captured from the exact installed app's process tree;
    # never enumerate and kill unrelated/reparented FFmpeg processes by name.
    if time.monotonic() > deadline - 8:
        current = {int(row.split(None, 2)[0]): row.strip().split(None, 2)
                   for row in subprocess.check_output(['ps', '-axo', 'pid=,ppid=,comm='], text=True).splitlines()}
        for pid in alive & children:
            row = current.get(pid)
            if row and len(row) == 3 and row[2].endswith('/ffmpeg') and int(row[1]) in owners | {1}:
                try: os.kill(pid, signal.SIGKILL)
                except ProcessLookupError: pass
    time.sleep(.1)
else: raise SystemExit('Owned LinkBridge processes did not stop; installation cancelled.')
PY
if [ -e "$HOME/Desktop/LinkBridge.app" ]; then
  mv "$HOME/Desktop/LinkBridge.app" "$staging/previous-LinkBridge.app"
fi
if ! mv "$staging/LinkBridge.app" "$HOME/Desktop/LinkBridge.app"; then
  if [ -e "$staging/previous-LinkBridge.app" ]; then
    mv "$staging/previous-LinkBridge.app" "$HOME/Desktop/LinkBridge.app"
  fi
  exit 1
fi
if ! codesign --verify --deep --strict "$HOME/Desktop/LinkBridge.app"; then
  mv "$HOME/Desktop/LinkBridge.app" "$staging/rejected-LinkBridge.app"
  if [ -e "$staging/previous-LinkBridge.app" ]; then
    mv "$staging/previous-LinkBridge.app" "$HOME/Desktop/LinkBridge.app"
    open "$HOME/Desktop/LinkBridge.app"
  fi
  exit 1
fi
open "$HOME/Desktop/LinkBridge.app"
printf 'Installed complete bundle. Previous bundle, if any: %s/previous-LinkBridge.app\n' "$staging"
