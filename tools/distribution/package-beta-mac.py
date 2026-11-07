from pathlib import Path
import shutil, json, hashlib, subprocess
root=Path(__file__).resolve().parents[2]
release=root/'artifacts/public-release'
stage=root.parent/'shipping/LinkBridge-macOS'
stage.mkdir(parents=True,exist_ok=True)
for name in ['LinkBridge.app','licenses']:
    shutil.copytree(release/name,stage/name,dirs_exist_ok=True,symlinks=True)
for name in ['Install.command','LICENSE','THIRD_PARTY_NOTICES.md','runtime-notice.md','release.json']:
    shutil.copy2(release/name,stage/name)
shutil.copy2(root/'docs/customer/LinkBridge用户说明书.html',stage/'使用说明.html')
(stage/'README.txt').write_text('LinkBridge 0.0.4-beta.1\n\nOpen 使用说明.html for detailed instructions. Run sh Install.command in Terminal.\nInstalls ~/Applications/LinkBridge.app for the current user; .NET runtime included.\nApple Silicon, macOS 15+. Ad-hoc signature, no Developer ID or notarization.\nThe owner completes macOS prompts. Different existing signing identities are refused.\nGenerate ONE private pairing set on Windows and import mac.settings.json on this Mac.\nKeyboard/mouse, one configurable seam, text/PNG clipboard and file copy/paste beta.\nAudio, camera, microphone, window roaming and multi-computer modes are excluded.\n',encoding='utf-8')
subprocess.run(['codesign','--verify','--deep','--strict',str(stage/'LinkBridge.app')],check=True)
files=[{'path':p.relative_to(stage).as_posix(),'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(stage.rglob('*')) if p.is_file() and p.name!='SHA256SUMS.json']
(stage/'SHA256SUMS.json').write_text(json.dumps({'label':'0.0.4-beta.1','files':files},ensure_ascii=False,indent=2),encoding='utf-8')
archive=root.parent/'LinkBridge-0.0.4-beta.1-macOS-arm64.zip'
subprocess.run(['/usr/bin/ditto','-c','-k','--norsrc','--keepParent',str(stage),str(archive)],check=True)
test=root.parent/'zip-check'
subprocess.run(['/usr/bin/ditto','-x','-k',str(archive),str(test)],check=True)
unpacked=test/'LinkBridge-macOS'
subprocess.run(['codesign','--verify','--deep','--strict',str(unpacked/'LinkBridge.app')],check=True)
for entry in files:
    assert hashlib.sha256((unpacked/entry['path']).read_bytes()).hexdigest()==entry['sha256']
print(json.dumps({'zip':str(archive),'bytes':archive.stat().st_size,'filesVerifiedAfterExtraction':len(files)}))
