#!/usr/bin/env python3
"""Add adjacent public license/setup files and hashes without changing the signed app."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

def require_file(path, label):
    if not path.is_file() or path.is_symlink() or path.stat().st_size == 0:
        raise RuntimeError("Missing or unsafe required release input: " + label)

def run(command):
    result = subprocess.run(command, capture_output=True, text=True, check=False, timeout=60)
    if result.returncode:
        raise RuntimeError("Required release tool failed: " + pathlib.Path(command[0]).name)
    return result.stdout

def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    root = args.root.resolve()
    output = root/'artifacts/public-release'
    app = output/'LinkBridge.app'
    if not app.is_dir() or app.is_symlink() or output.is_symlink():
        raise RuntimeError('Prepared public LinkBridge.app is required; no build or installation is performed.')
    run(['/usr/bin/codesign','--verify','--deep','--strict',str(app)])
    signature = subprocess.run(['/usr/bin/codesign','-d','--verbose=4',str(app)],
                               capture_output=True, text=True, check=False, timeout=60)
    signature_text = signature.stdout + signature.stderr
    if signature.returncode or 'Signature=adhoc' not in signature_text or 'Authority=' in signature_text:
        raise RuntimeError('The adjacent public release must remain ad-hoc only.')

    config = root/'src/LinkBridge.App/bin/Release/net10.0/osx-arm64/LinkBridge.App.runtimeconfig.json'
    require_file(config, 'actual osx-arm64 runtimeconfig.json from this publish')
    options = json.loads(config.read_text(encoding='utf-8'))['runtimeOptions']
    if options.get('tfm') != 'net10.0':
        raise RuntimeError('Unexpected actual runtime target framework.')
    frameworks = options.get('includedFrameworks', [])
    versions = [item.get('version') for item in frameworks if item.get('name') == 'Microsoft.NETCore.App']
    if len(versions) != 1 or not isinstance(versions[0], str) or not re.fullmatch(r'10\.0\.\d+(?:[-+][A-Za-z0-9.-]+)?', versions[0]):
        raise RuntimeError('Actual self-contained .NET 10 runtime version could not be verified.')
    version = versions[0]
    package = 'microsoft.netcore.app.runtime.osx-arm64'
    cache = os.environ.get('NUGET_PACKAGES')
    if not cache:
        cache_listing = run(['dotnet','nuget','locals','global-packages','--list'])
        cache = next((line.partition(':')[2].strip() for line in cache_listing.splitlines()
                      if line.strip().startswith('global-packages:')), None)
    if not cache:
        raise RuntimeError('NuGet global-packages location is unavailable.')
    runtime_package = pathlib.Path(cache).expanduser()/package/version
    runtime_inputs = [(runtime_package/'LICENSE.TXT', 'runtime LICENSE.TXT'),
                      (runtime_package/'THIRD-PARTY-NOTICES.TXT', 'runtime THIRD-PARTY-NOTICES.TXT')]
    adjacent = [(root/'LICENSE', output/'LICENSE'),
                (root/'THIRD_PARTY_NOTICES.md', output/'THIRD_PARTY_NOTICES.md'),
                (root/'docs/BUILDING.md', output/'BUILDING.md'),
                (root/'tools/open-source/New-PairingProfile.ps1', output/'New-PairingProfile.ps1')]
    # Validate every required notice before changing the adjacent release files.
    for source, target in adjacent:
        require_file(source, source.relative_to(root).as_posix())
        if target.is_symlink(): raise RuntimeError('Adjacent release target must not be a symlink.')
    for source, label in runtime_inputs: require_file(source, label)
    notice_dir = output/'licenses'/('dotnet-runtime-' + version)
    if (output/'licenses').is_symlink() or notice_dir.is_symlink():
        raise RuntimeError('Runtime notice directory must not be a symlink.')
    for source, target in adjacent: shutil.copyfile(source, target)
    notice_dir.mkdir(parents=True, exist_ok=True)
    for source, _ in runtime_inputs:
        target = notice_dir/source.name
        if target.is_symlink(): raise RuntimeError('Runtime notice target must not be a symlink.')
        shutil.copyfile(source, target)
    runtime_notice = output/'runtime-notice.md'
    if runtime_notice.is_symlink(): raise RuntimeError('Runtime notice target must not be a symlink.')
    runtime_notice.write_text(
        '# Included .NET runtime\n\n'
        'This self-contained Apple Silicon package includes **Microsoft.NETCore.App ' + version + '**.\n\n'
        'The version was read from the actual `osx-arm64` publish runtime configuration, not inferred from the SDK or target framework.\n\n'
        'Runtime package: `Microsoft.NETCore.App.Runtime.osx-arm64/' + version + '`.\n\n'
        '- [Runtime license](licenses/dotnet-runtime-' + version + '/LICENSE.TXT)\n'
        '- [Runtime third-party notices](licenses/dotnet-runtime-' + version + '/THIRD-PARTY-NOTICES.TXT)\n'
        '- [Project and other dependency notices](THIRD_PARTY_NOTICES.md)\n\n'
        'The project LICENSE does not replace the licenses and notices of the included runtime or native dependencies. '
        'The original runtime notice files are copied without modification.\n\n'
        'No developer configuration, pairing secrets, private signing material, or compiler-machine path is part of this metadata.\n',
        encoding='utf-8')
    manifest = output/'SHA256SUMS.json'
    if manifest.is_symlink(): raise RuntimeError('Release manifest target must not be a symlink.')
    files = []
    for path in sorted(output.rglob('*')):
        if path == manifest: continue
        relative = path.relative_to(output).as_posix()
        if path.is_symlink():
            link = os.readlink(path)
            if os.path.isabs(link) or not path.resolve().is_relative_to(output.resolve()):
                raise RuntimeError('Public output contains an external or absolute symlink.')
            files.append({'path': relative, 'type': 'symlink', 'target': link,
                          'sha256': hashlib.sha256(link.encode('utf-8')).hexdigest()})
        elif path.is_dir(): continue
        elif path.is_file(): files.append({'path': relative, 'bytes': path.stat().st_size, 'sha256': digest(path)})
    manifest.write_text(json.dumps({'schema': 1, 'productVersion': '0.0.1', 'runtimeIdentifier': 'osx-arm64',
        'runtimeFramework': 'Microsoft.NETCore.App', 'runtimeVersion': version,
        'createdAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'files': files},
        ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    # Verification after hashing confirms that no signed bundle resource changed.
    run(['/usr/bin/codesign','--verify','--deep','--strict',str(app)])
    print(json.dumps({'prepared': True, 'runtimeVersion': version, 'files': len(files),
                      'manifest': 'artifacts/public-release/SHA256SUMS.json', 'signedAppModified': False}))

if __name__ == '__main__':
    try: main()
    except RuntimeError as error:
        print('Public release preparation failed: ' + str(error), file=sys.stderr); sys.exit(1)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print('Public release preparation failed: invalid metadata or filesystem/tool error.', file=sys.stderr); sys.exit(1)
