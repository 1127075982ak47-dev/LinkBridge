#!/usr/bin/env python3
"""Read-only strict A/B signature, exact DR/leaf, existing Code Signing trust and changed-code gate."""
import argparse
import json
import pathlib
import sys
from mac_signing import DEFAULT_DIRECTORY,mac_only,load_identity,keychain_ready,inspect_signature
def main():
    mac_only();parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('a',type=pathlib.Path);parser.add_argument('b',type=pathlib.Path)
    parser.add_argument('--directory',type=pathlib.Path,default=DEFAULT_DIRECTORY)
    args=parser.parse_args();identity=load_identity(args.directory)
    keychain_ready(identity,args.directory)
    a=inspect_signature(args.a.resolve(),identity);b=inspect_signature(args.b.resolve(),identity)
    if a['cdhash']==b['cdhash']:raise RuntimeError('A/B CodeDirectory hashes are identical; no actual changed-build evidence.')
    print(json.dumps(dict(a=a,b=b,strictSignature=True,stableExactRequirement=True,changedCodeDirectory=True,
                          existingCodeSigningTrustVerified=True,tccRetentionVerified=False),indent=2))
if __name__=='__main__':
    try:main()
    except Exception as error:print('FAILED: '+str(error),file=sys.stderr);sys.exit(1)
