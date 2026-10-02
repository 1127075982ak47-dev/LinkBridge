#!/usr/bin/env python3
"""Offline certificate/DR tests in a private temp directory; never imports keys or changes trust."""
import argparse
import json
import pathlib
import tempfile
import sys
from mac_signing import mac_only,prepare,load_identity,compile_requirement,inspect_signature,private_file

def assert_ok(pass_value,description):
    if not pass_value:raise RuntimeError(description)
    print('PASS '+description)
def main():
    mac_only();parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reject-adhoc',type=pathlib.Path);args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='LinkBridgeSigningSelfTest-') as temp:
        directory=pathlib.Path(temp)/'identity'
        identity,created=prepare(directory);again,recreated=prepare(directory)
        assert_ok(created and not recreated and identity==again,'repeated preparation reuses exact certificate/key fingerprint')
        for path in directory.iterdir():private_file(path)
        assert_ok(True,'all prepared identity files are owner-only regular 0600 files')
        exact=compile_requirement(identity['requirement'])
        weak=compile_requirement('designated => identifier "com.linkbridge.desktop"')
        foreign=compile_requirement('designated => identifier "other.app" and certificate leaf = H"'+identity['leafSha1']+'"')
        assert_ok(exact!=weak and exact!=foreign,'compiled exact leaf+identifier DR differs from relaxed or foreign-identifier rules')
        saved=json.loads((directory/'identity.json').read_text());saved['leafSha256']='0'*64
        (directory/'identity.json').write_text(json.dumps(saved))
        refused=False
        try:load_identity(directory)
        except RuntimeError as error:refused='changed' in str(error)
        assert_ok(refused,'tampered fingerprint manifest is rejected instead of replacing identity')
        (directory/'identity.json').write_text(json.dumps(identity))
        if args.reject_adhoc:
            refused=False
            try:inspect_signature(args.reject_adhoc.resolve(),identity)
            except RuntimeError as error:refused='DR does not exactly match' in str(error)
            assert_ok(refused,'actual existing ad-hoc app cannot satisfy stable certificate DR gate')
    print('No persistent identity regenerated, keychain/trust writes, app signatures, TCC access or private-key/password output.')
if __name__=='__main__':
    try:main()
    except Exception as error:print('FAILED: '+str(error),file=sys.stderr);sys.exit(1)
