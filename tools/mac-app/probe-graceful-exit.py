import os, signal, subprocess, time
expected = os.path.expanduser('~/Desktop/LinkBridge.app/Contents/MacOS/LinkBridge.App')
def rows():
    return [line.strip().split(None, 2) for line in subprocess.check_output(['ps', '-axo', 'pid=,ppid=,comm='], text=True).splitlines()]
owners = {int(row[0]) for row in rows() if len(row) == 3 and row[2] == expected}
if len(owners) != 1:
    raise SystemExit('Expected exactly one installed LinkBridge process; no signal sent.')
deadline = time.monotonic() + 12
children = set()
while time.monotonic() < deadline:
    children = {int(row[0]) for row in rows() if len(row) == 3 and int(row[1]) in owners and row[2].endswith('/ffmpeg')}
    if children: break
    time.sleep(.2)
if not children:
    raise SystemExit('No active owned FFmpeg capture window observed; no signal sent.')
print('Observed app=' + str(sorted(owners)) + ' ownedChildren=' + str(sorted(children)), flush=True)
started = time.monotonic()
for pid in owners: os.kill(pid, signal.SIGTERM)
while time.monotonic() - started < 12:
    existing = {int(row[0]) for row in rows()}
    alive = existing & (owners | children)
    if not alive:
        print('PASS actual SIGTERM/window cleanup: app and owned FFmpeg exited in %.3fs' % (time.monotonic() - started), flush=True)
        break
    time.sleep(.1)
else:
    raise SystemExit('FAIL retained own processes: ' + str(sorted(alive)))
