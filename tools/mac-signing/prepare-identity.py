#!/usr/bin/env python3
"""Default: ephemeral dry-run, or verify a previously prepared identity. --prepare persists exactly once."""
import argparse
import json
import pathlib
import tempfile
import sys
from mac_signing import DEFAULT_DIRECTORY,mac_only,prepare,load_identity,compile_requirement

def main():
    mac_only()
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory',type=pathlib.Path,default=DEFAULT_DIRECTORY)
    parser.add_argument('--prepare',action='store_true')
    args=parser.parse_args()
    if args.prepare:
        identity,created=prepare(args.directory)
        print(json.dumps(dict(identity,mode='persistent-preparation',created=created,directory=str(args.directory),keychainImported=False,trustChanged=False),indent=2))
    elif args.directory.exists() or args.directory.is_symlink():
        identity=load_identity(args.directory)
        print(json.dumps(dict(identity,mode='verify-existing',created=False,keychainImported=False,trustChanged=False),indent=2))
    else:
        with tempfile.TemporaryDirectory(prefix='LinkBridgeSigningDryRun-') as temp:
            identity,_=prepare(pathlib.Path(temp)/'ephemeral-identity')
            compile_requirement(identity['requirement'])
            print(json.dumps(dict(mode='ephemeral-dry-run',opensslConstraints='verified',requirementCompilation='passed',persisted=False,
                                  keychainImported=False,trustChanged=False),indent=2))
    if args.prepare or args.directory.exists(): compile_requirement(identity['requirement'])
if __name__=='__main__':
    try: main()
    except Exception as error: print('FAILED: '+str(error),file=sys.stderr);sys.exit(1)
