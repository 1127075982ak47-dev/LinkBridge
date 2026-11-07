"""Allowlisted, current source for both public builds; never copy private artifacts."""
from pathlib import Path
import shutil, json, hashlib, tarfile, zipfile
ROOT=Path(__file__).resolve().parents[2]
DEST=ROOT/'artifacts/beta-20261107/source/LinkBridge'
DEST.mkdir(parents=True,exist_ok=True)
SKIP={'bin','obj','build','deps','.git','evidence','__pycache__','node_modules','artifacts'}
EXT={'.cs','.csproj','.slnx','.axaml','.manifest','.ico','.icns','.png','.jpg','.jpeg','.json','.swift','.m','.mm','.h','.hpp','.cpp','.c','.def','.idl','.rc','.in','.txt','.md','.ps1','.py','.sh','.command','.plist','.entitlements','.cmd','.nsi','.nsh','.js','.html','.css','.bat','.ttf','.otf','.resx','.svg'}
def copy_tree(base):
    for p in (ROOT/base).rglob('*'):
        relative=p.relative_to(ROOT/base)
        if any(part in SKIP for part in relative.parts) or p.is_symlink() or not p.is_file(): continue
        if p.suffix.lower() not in EXT and p.name not in {'LICENSE','LICENSE-Microsoft','LICENSE-WIL','CMakeLists.txt'}: continue
        if p.name in {'prepare-customer-source.py','prepare-beta-build.py','build-manuals.py','finalize-delivery.py','test-beta-windows.ps1','assemble-beta.py'}: continue
        target=DEST/base/relative
        target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(p,target)
for base in ['src','native','tools/distribution','tools/open-source','tools/DeviceProbe','tools/InputProbe',
             'tools/ScreenLayoutProbe','tools/FileTransferProbe','tools/ClipboardProbe','tools/PairedTlsProbe',
             'tools/DiscoveryDatagramsProbe','tools/ManualConnectionProbe','tools/AppIntegrationProbe']:
    copy_tree(Path(base))
for name in ['FileDragHelper.swift','render-app-icon.swift','Info.plist']:
    target=DEST/'tools/mac-app'/name
    target.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(ROOT/'tools/mac-app'/name,target)
for name in ['LICENSE','THIRD_PARTY_NOTICES.md','LinkBridge.slnx','tools/build-app-icon.py','docs/BUILDING.md']:
    (DEST/name).parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(ROOT/name,DEST/name)
if (ROOT/'docs/customer').exists(): copy_tree(Path('docs/customer'))
(DEST/'README.md').write_text('''# LinkBridge 0.0.4-beta.1 源码\n\n本快照对应 2026-11-07 双机公测。Windows 11 x64 键鼠控制 macOS 15+ Apple Silicon；一条可配置接缝；双向文字、PNG、文件复制粘贴。音视频、窗口跨屏和高级多机不在公测范围。\n\n客户端用户请使用公测交付包的安装器及说明书。构建：安装 .NET 10 SDK 后执行 `dotnet build LinkBridge.slnx -c Release`。Windows 自带运行时构建：`pwsh -File tools/distribution/Build-WindowsRelease.ps1`；Mac 原生构建：`sh tools/distribution/build-mac-release.sh`（Xcode Command Line Tools、Python 3、.NET 10 SDK）。Windows 安装器还需 NSIS 和 docs/customer/LinkBridge用户说明书.html。\n\n发行元数据见 tools/distribution/release.json。Mac 公开包只用 ad-hoc 签名，不得覆盖已有不同签名的授权应用。原生窗口捕获实验库不随基础源码构建；不影响公测键鼠和文件功能。\n\n协议测试见 tools 下的各 Probe。包含 MIT 和第三方声明，未包含个人配对配置、凭据、签名证书、日志或 FFmpeg。\n''',encoding='utf-8')
(DEST/'AGENTS.md').write_text('''# Public source workspace\n\nRead README.md and tools/distribution/release.json first. Preserve settings and pairing identities. Never import developer credentials or change existing Mac signing identity. Build only in this workspace, leave installed apps intact unless the owner requests an update. Do not use developer-machine absolute paths. Do not claim automated protocol tests prove physical gestures or first-install OS authorization.\n''',encoding='utf-8')
public_files=[p for p in sorted(DEST.rglob('*')) if p.is_file() and p.name!='SOURCE-MANIFEST.json' and not any(part in SKIP for part in p.relative_to(DEST).parts)]
files=[{'path':p.relative_to(DEST).as_posix(),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in public_files]
(DEST/'SOURCE-MANIFEST.json').write_text(json.dumps({'version':'0.0.4-beta.1','files':files},ensure_ascii=False,indent=2),encoding='utf-8')
archive=DEST.parent/'LinkBridge-source.tar.gz'
public_files.append(DEST/'SOURCE-MANIFEST.json')
with tarfile.open(archive,'w:gz') as tf:
    for p in public_files: tf.add(p,arcname='LinkBridge/'+p.relative_to(DEST).as_posix(),recursive=False)
with zipfile.ZipFile(DEST.parent/'LinkBridge-0.0.4-beta.1-source.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in public_files: z.write(p,'LinkBridge/'+p.relative_to(DEST).as_posix())
print(json.dumps({'files':len(files),'archive':str(archive)},ensure_ascii=False))
