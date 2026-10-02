"""Run only after explicit user approval; no passwords or private key bytes in argv/logs."""
import json, os, pathlib, subprocess, sys
from mac_signing import DEFAULT_DIRECTORY, load_identity, mac_only, keychain_ready

mac_only()
if sys.argv[1:] != ['--user-approved']:
    raise SystemExit('Explicit --user-approved migration required.')
identity = load_identity(DEFAULT_DIRECTORY)
keychain = pathlib.Path.home() / 'Library/Keychains/login.keychain-db'
if not keychain.is_file() or keychain.is_symlink():
    raise SystemExit('Expected current user login keychain not found; stopped.')

def command(arguments):
    result = subprocess.run(['/usr/bin/security', *arguments], capture_output=True, text=True)
    if result.returncode and 'already exists' not in (result.stderr + result.stdout).lower():
        raise RuntimeError(result.stderr.strip()[:700])
    return result

# Keep the pre-migration user trust snapshot local and private; never alter other policies.
backup = DEFAULT_DIRECTORY / 'user-trust-before.plist'
if not backup.exists():
    result = subprocess.run(['/usr/bin/security', 'trust-settings-export', str(backup)], capture_output=True, text=True)
    if backup.exists(): os.chmod(backup, 0o600)
    elif result.returncode and 'no trust settings' not in (result.stderr + result.stdout).lower():
        raise RuntimeError('Unable to preserve existing user trust settings; migration stopped.')

command(['import', str(DEFAULT_DIRECTORY / 'certificate.pem'), '-k', str(keychain), '-t', 'cert'])
print('Certificate import complete; importing only the prepared non-extractable signing key.', flush=True)
command(['import', str(DEFAULT_DIRECTORY / 'private-key.pem'), '-k', str(keychain), '-t', 'priv', '-T', '/usr/bin/codesign', '-x'])
print('Prepared signing key imported; applying current-user Code Signing policy only.', flush=True)
command(['add-trusted-cert', '-r', 'trustRoot', '-p', 'codeSign', '-k', str(keychain), str(DEFAULT_DIRECTORY / 'certificate.pem')])
keychain_ready(identity, DEFAULT_DIRECTORY)
print(json.dumps({'identityReady': True, 'leafSha1': identity['leafSha1'], 'trustDomain': 'current-user', 'policy': 'codeSign', 'privateKeyAllowedApplication': '/usr/bin/codesign'}))
