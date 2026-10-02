#!/usr/bin/env python3
"""Prepare a signing plan; --apply signs a NEW bundle copy only, after explicit keychain migration."""
import argparse
import json
import pathlib
import shutil
import subprocess
import sys
from mac_signing import DEFAULT_DIRECTORY,mac_only,load_identity,keychain_ready,app_metadata,run,requirement,inspect_signature

MAGICS={bytes.fromhex(x) for x in ('feedface','cefaedfe','feedfacf','cffaedfe','cafebabe','bebafeca','cafebabf','bfbafeca')}
def main():
    mac_only();parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source',type=pathlib.Path);parser.add_argument('destination',type=pathlib.Path)
    parser.add_argument('--directory',type=pathlib.Path,default=DEFAULT_DIRECTORY);parser.add_argument('--apply',action='store_true')
    args=parser.parse_args();identity=load_identity(args.directory)
    source=args.source.resolve();destination=args.destination.absolute()
    app_metadata(source)
    if destination.exists() or destination.is_symlink() or destination.suffix!='.app': raise RuntimeError('Destination must be a NEW .app path, not an installed app.')
    if destination==source or source in destination.parents: raise RuntimeError('Copy cannot be inside source.')
    if not args.apply:
        print(json.dumps(dict(mode='dry-run',source=str(source),destination=str(destination),identityLeafSha1=identity['leafSha1'],
                              requirement=identity['requirement'],copied=False,signed=False,keychainImported=False,trustChanged=False),indent=2));return
    keychain_ready(identity,args.directory)
    shutil.copytree(source,destination,symlinks=True)
    for item in destination.rglob('*'):
        if item.is_symlink() and not item.resolve().is_relative_to(destination): raise RuntimeError('External bundle symlink rejected; unsigned candidate retained for review.')
    binaries=[]
    for item in destination.rglob('*'):
        if item.is_file() and not item.is_symlink():
            with item.open('rb') as handle:
                if handle.read(4) in MAGICS:binaries.append(item)
    for item in sorted(binaries,key=lambda path:(path==app_metadata(destination),-len(path.parts),str(path))):
        query=subprocess.run(['/usr/bin/codesign','-d','--verbose=2',str(item)],capture_output=True)
        lines=(query.stdout+query.stderr).decode().splitlines()
        identifier=next((line.split('=',1)[1] for line in lines if line.startswith('Identifier=')),item.name)
        run(['/usr/bin/codesign','--force','--sign',identity['leafSha1'],'--timestamp=none','--preserve-metadata=entitlements,flags,runtime',
             '--identifier',identifier,'--requirements','='+requirement(identifier,identity['leafSha1']),str(item)])
    run(['/usr/bin/codesign','--force','--sign',identity['leafSha1'],'--timestamp=none','--preserve-metadata=entitlements,flags,runtime',
         '--identifier',identity['identifier'],'--requirements','='+identity['requirement'],str(destination)])
    print(json.dumps(inspect_signature(destination,identity),indent=2))
if __name__=='__main__':
    try:main()
    except Exception as error:print('FAILED: '+str(error),file=sys.stderr);sys.exit(1)
