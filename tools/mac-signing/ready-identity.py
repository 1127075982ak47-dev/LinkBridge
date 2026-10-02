"""Read the fixed signing identity for a build; never rotate or silently replace it."""
import sys
from mac_signing import DEFAULT_DIRECTORY, load_identity, keychain_ready, mac_only
mac_only()
if not DEFAULT_DIRECTORY.exists():
    print('-')
else:
    try:
        identity = load_identity(DEFAULT_DIRECTORY)
        keychain_ready(identity, DEFAULT_DIRECTORY)
        print(identity['leafSha1'])
    except Exception as error:
        print('Fixed signing identity is not ready: '+str(error), file=sys.stderr)
        sys.exit(1)
