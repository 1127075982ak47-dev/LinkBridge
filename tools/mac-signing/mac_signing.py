"""Shared local signing helpers. No keychain/trust import, TCC, SIP or password handling."""
import hashlib
import json
import os
import pathlib
import plistlib
import re
import stat
import subprocess
import sys
import tempfile

IDENTIFIER = 'com.linkbridge.desktop'
DEFAULT_DIRECTORY = pathlib.Path.home() / 'Library/Application Support/LinkBridge/Signing/local-codesign-v1'
OPENSSL = next((path for path in ('/opt/homebrew/opt/openssl@3/bin/openssl','/usr/local/opt/openssl@3/bin/openssl')
                if pathlib.Path(path).is_file()), '/usr/bin/openssl')
CONFIG = '''[req]
prompt = no
distinguished_name = dn
x509_extensions = signing
[dn]
CN = LinkBridge Local Code Signing
O = LinkBridge Local Development
[signing]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid:always
'''
def run(command, input=None):
    # Native keychain approval can remain open while the user confirms access.
    # Give signing an interactive window; other diagnostic commands stay bounded.
    timeout = 300 if pathlib.Path(command[0]).name == 'codesign' and '--sign' in command else 60
    result = subprocess.run(command, input=input, capture_output=True, timeout=timeout)
    if result.returncode:
        # Only public diagnostics: none of our commands prints private key material.
        raise RuntimeError('%s %s failed (%s): %s' % (pathlib.Path(command[0]).name, command[1] if len(command)>1 else '', result.returncode,
                                                (result.stderr+result.stdout).decode('utf8', 'replace').strip()[:1500]))
    return result.stdout
def mac_only():
    if sys.platform != 'darwin': raise RuntimeError('Run on the target Mac; no Windows/OpenSSL substitution.')
    if not run([OPENSSL,'version']).decode().startswith('OpenSSL 3.'):
        raise RuntimeError('OpenSSL3 is required to verify the constrained self-signed CA:false leaf; do not broaden CA/KeyUsage to accommodate Apple LibreSSL.')
def private_file(path):
    meta = path.lstat()
    if not stat.S_ISREG(meta.st_mode) or meta.st_uid != os.getuid() or stat.S_IMODE(meta.st_mode) != 0o600:
        raise RuntimeError('File must be a regular owner-only 0600 file: '+str(path))
def private_directory(path):
    meta = path.lstat()
    if not stat.S_ISDIR(meta.st_mode) or meta.st_uid != os.getuid() or stat.S_IMODE(meta.st_mode) != 0o700:
        raise RuntimeError('Identity directory must be owner-only 0700, not a symlink: '+str(path))
def write_private(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w') as file: file.write(value)
def cert_details(directory):
    cert, key = directory/'certificate.pem', directory/'private-key.pem'
    private_directory(directory)
    for path in (cert,key,directory/'openssl.cnf'): private_file(path)
    if (directory/'openssl.cnf').read_text() != CONFIG: raise RuntimeError('Unexpected certificate policy configuration.')
    run([OPENSSL,'rsa','-in',str(key),'-check','-noout'])
    public_key = run([OPENSSL,'pkey','-in',str(key),'-pubout'])
    if public_key != run([OPENSSL,'x509','-in',str(cert),'-pubkey','-noout']): raise RuntimeError('Certificate/private key mismatch.')
    run([OPENSSL,'verify','-check_ss_sig','-CAfile',str(cert),str(cert)])
    run([OPENSSL,'x509','-in',str(cert),'-checkend','2592000','-noout'])
    details = run([OPENSSL,'x509','-in',str(cert),'-text','-noout']).decode()
    required = {'Basic Constraints':'CA:FALSE','Key Usage':'Digital Signature','Extended Key Usage':'Code Signing'}
    for extension, value in required.items():
        match = re.search(r'X509v3 '+extension+r': critical\s*\n\s*([^\n]+)', details)
        if not match or match.group(1).strip() != value: raise RuntimeError('Certificate restriction mismatch: '+extension)
    if 'sha256WithRSAEncryption' not in details or '3072 bit' not in details: raise RuntimeError('Expected RSA3072 / SHA256 certificate.')
    der = run([OPENSSL,'x509','-in',str(cert),'-outform','DER'])
    sha1, sha256 = hashlib.sha1(der).hexdigest(), hashlib.sha256(der).hexdigest()
    return dict(schema=1,identifier=IDENTIFIER,leafSha1=sha1,leafSha256=sha256,
                publicKeySha256=hashlib.sha256(public_key).hexdigest(),subject='LinkBridge Local Code Signing',
                requirement=requirement(IDENTIFIER,sha1),privateKeyEncrypted=False)
def requirement(identifier, sha1):
    if not re.fullmatch('[a-zA-Z0-9._-]+', identifier): raise RuntimeError('Unexpected signing identifier.')
    if not re.fullmatch('[0-9a-f]{40}', sha1): raise RuntimeError('Invalid leaf SHA1 pin.')
    return 'designated => identifier '+json.dumps(identifier)+' and certificate leaf = H"'+sha1+'"'
def prepare(directory):
    os.umask(0o077)
    if directory.exists() or directory.is_symlink():
        return load_identity(directory), False # Never replace even an expired/incomplete identity.
    directory.parent.mkdir(parents=True, exist_ok=True)
    directory.mkdir(mode=0o700)
    private_directory(directory)
    write_private(directory/'openssl.cnf',CONFIG)
    run([OPENSSL,'genpkey','-algorithm','RSA','-pkeyopt','rsa_keygen_bits:3072','-out',str(directory/'private-key.pem')])
    os.chmod(directory/'private-key.pem',0o600)
    serial = '0x'+os.urandom(16).hex()
    run([OPENSSL,'req','-new','-x509','-sha256','-days','3650','-set_serial',serial,'-key',str(directory/'private-key.pem'),
         '-out',str(directory/'certificate.pem'),'-config',str(directory/'openssl.cnf')])
    os.chmod(directory/'certificate.pem',0o600)
    identity=cert_details(directory)
    write_private(directory/'identity.json',json.dumps(identity,indent=2)+'\n')
    write_private(directory/'designated.req',identity['requirement']+'\n')
    return load_identity(directory), True
def load_identity(directory):
    for name in ('identity.json','designated.req'): private_file(directory/name)
    saved=json.loads((directory/'identity.json').read_text())
    actual=cert_details(directory)
    if actual != saved or (directory/'designated.req').read_text().strip() != actual['requirement']:
        raise RuntimeError('Identity fingerprint/identifier/requirement changed; no automatic regeneration.')
    return actual
def compile_requirement(requirement_text):
    # Compare canonical binary requirement sets rather than whitespace/quote formatting.
    with tempfile.TemporaryDirectory(prefix='LinkBridgeRequirement-') as temp:
        path=pathlib.Path(temp)/'requirements.bin'
        if not requirement_text.startswith('designated =>'): raise RuntimeError('Expected designated requirement.')
        expression = requirement_text.split('=>',1)[1].strip()
        run(['/usr/bin/csreq','-r','='+expression,'-b',str(path)])
        return path.read_bytes()
def keychain_ready(identity,directory):
    listed=run(['/usr/bin/security','find-identity','-v','-p','codesigning']).decode()
    if not re.search(r'\b'+identity['leafSha1']+r'\b',listed,re.I):
        raise RuntimeError('Exact prepared certificate is not a valid code-signing identity in the user keychain search list. User import/trust/private-key approval remains pending.')
    # This evaluates existing Code Signing trust. No explicit root or SSL policy is added.
    run(['/usr/bin/security','verify-cert','-L','-c',str(directory/'certificate.pem'),'-p','codeSign'])
def app_metadata(path):
    if path.suffix != '.app' or not path.is_dir(): raise RuntimeError('Existing .app required.')
    info=plistlib.loads((path/'Contents/Info.plist').read_bytes())
    if info.get('CFBundleIdentifier') != IDENTIFIER: raise RuntimeError('Bundle identifier mismatch.')
    main=path/'Contents/MacOS'/info.get('CFBundleExecutable','')
    if not main.is_file(): raise RuntimeError('Missing app executable.')
    return main
def inspect_signature(path,identity):
    app_metadata(path)
    run(['/usr/bin/codesign','--verify','--deep','--strict',str(path)])
    output=subprocess.run(['/usr/bin/codesign','-d','--verbose=4','-r-',str(path)],capture_output=True,timeout=60)
    if output.returncode: raise RuntimeError('Signature metadata query failed.')
    lines=(output.stdout+output.stderr).decode().splitlines()
    dr=next((line.lstrip('# ').strip() for line in lines if line.lstrip('# ').startswith('designated =>')),None)
    if not dr or compile_requirement(dr) != compile_requirement(identity['requirement']):
        raise RuntimeError('DR does not exactly match prepared identifier AND leaf pin; reject ad-hoc/relaxed requirements.')
    details=dict(line.split('=',1) for line in lines if '=' in line and not line.lstrip('# ').startswith('designated =>'))
    cdhash=details.get('CDHash')
    if not cdhash: raise RuntimeError('Missing actual CodeDirectory hash.')
    with tempfile.TemporaryDirectory(prefix='LinkBridgeSignedCertificates-') as temp:
        prefix=pathlib.Path(temp)/'certificate'
        run(['/usr/bin/codesign','-d','--extract-certificates='+str(prefix),str(path)])
        actual=(pathlib.Path(str(prefix)+'0')).read_bytes()
        if hashlib.sha256(actual).hexdigest() != identity['leafSha256']: raise RuntimeError('Extracted signing leaf differs from prepared certificate.')
        pem=pathlib.Path(temp)/'leaf.pem'
        pem.write_bytes(run([OPENSSL,'x509','-inform','DER','-in',str(prefix)+'0','-outform','PEM']))
        run([OPENSSL,'verify','-check_ss_sig','-CAfile',str(pem),str(pem)])
    return dict(path=str(path),cdhash=cdhash,requirement=dr,identifier=details.get('Identifier'),leafSha256=identity['leafSha256'])
