#!/bin/sh
set -eu
# Read-only OS diagnostic; no TCC database access, reset, or permission mutation.
/usr/bin/log show --last 8m --style compact --predicate 'subsystem == "com.apple.TCC" AND eventMessage CONTAINS "com.linkbridge.desktop"' 2>/dev/null | tail -n 24
