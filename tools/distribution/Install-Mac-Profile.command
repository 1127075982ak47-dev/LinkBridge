#!/bin/sh
set -eu
directory="$(CDPATH= cd -- "$(dirname "$0")" && pwd -P)"
[ "$(uname -s)" = Darwin ] || exit 1
if pgrep -x LinkBridge.App >/dev/null; then printf '%s\n' '请先完全退出 LinkBridge，再导入配对。'; exit 1; fi
root="$HOME/Library/Application Support/LinkBridge"
[ ! -L "$root" ] && [ ! -L "$root/settings.json" ] || { printf '%s\n' '设置路径是链接，导入已停止。'; exit 1; }
umask 077
mkdir -p "$root"
/usr/bin/osascript -l JavaScript "$directory/Import-Mac-Profile.js" "$directory/mac.settings.json" "$root/settings.json"
chmod 600 "$root/settings.json"
printf '%s\n' '配对配置已导入，原设置已备份。请打开两端 LinkBridge，允许 Mac 辅助功能，再点一键连接。'
