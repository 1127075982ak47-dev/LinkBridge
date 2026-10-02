# Publication audit · 0.0.1

Audit date: 2026-10-02 (Asia/Shanghai). Repository: `1127075982ak47-dev/LinkBridge`.

## Scope

- Original published application-source baseline: `12e3f9a276c4b0bace10e4a3eeb52b04a18c9986`, including `main`, `v0.0.1` and every reachable historical blob.
- GitHub branch/tag APIs and `git ls-remote` independently confirmed the advertised refs. At the initial scan the repository contained one commit, 316 reachable objects and 243 complete blobs, totaling 8,402,564 bytes.
- Downloaded the existing public source ZIP from GitHub and independently compared its SHA-256 with the published asset digest and checksum file.
- Examined the new distribution source files and complete public Windows/macOS archives before uploading them. Installer validation and signing checks are separate from the secrets checks.

## Methods

1. Read each reachable Git blob in full, including binary contents, and checked every historical tree filename.
2. Loaded known private comparison values in memory from application settings, authentication material, local SSH private keys and local proxy/node profiles. The extended comparison covered 41 private comparison sources and 32 unique values. Values were not printed, saved in reports or passed as process arguments.
3. Compared raw/UTF-8 and UTF-16 representations, private-key blocks, token-shaped literals, private configuration filenames and personal build metadata. Context review distinguished a common code-identifier collision from an exposed credential; that collision does not appear here as a secret value.
4. Ran Gitleaks 8.30.1 against the complete Git history, public worktree and Windows release directory. The scanner download was checked against its upstream SHA-256 checksum. These scans reported no leaks.
5. Read all ZIP members, including application/native binaries, and verified archive CRC and each bundled per-file manifest. The two new packages contain no application settings, pairing profile, SSH key, private signing key, personal signing certificate, local deployment manifest, debug symbols or application log.

## Public assets checked

| Asset | Files scanned | Size | SHA-256 |
| --- | ---: | ---: | --- |
| `LinkBridge-0.0.1-source.zip` | 252 | 7,878,353 bytes | `a83fb118b0804851341ae1e586693f4402936ea1dffb28a5848c428a14ae6e0d` |
| `LinkBridge-0.0.1-Windows-x64.zip` | 245 | 54,454,074 bytes | `8150f8a701eb87db62b8260f023d1ca8044e09088a7fe151fa87894749a45074` |
| `LinkBridge-0.0.1-macOS-arm64.zip` | 18 | 48,200,203 bytes | `37fcf5b9c5b73171b5b54a86c2524def7b53f5d46e4954f39833cf9604838460` |

The new Windows/macOS packages were scanned over **234,794,545 uncompressed bytes**. Archive filenames and contents were checked, not only compressed-byte text.

## Results

**No confirmed local credential, pairing-key, node-key or private-signing-material exposure was found in the covered public history or release assets.** No credential-bearing public file was identified for deletion or history rewriting.

The public binaries are rebuilt from audited application source. Windows contains its own .NET runtime and is not Authenticode signed. The Mac public app is ad-hoc signed: `TeamIdentifier` is not set and no personal certificate `Authority` is present. The maintainer's certificate-signed daily Mac app is a separate installation and was not used as the public download.

Repository secret scanning and secret-scanning push protection were verified enabled. Source releases and installed binaries retain applicable third-party and bundled-runtime license notices.

## Limits

This records the inspected advertised history and release assets at publication time. It does not inspect deleted/unreachable server-side objects, third-party clones, private local working files, GitHub caches or every possible future credential format. Pattern and exact-value scanning are not a mathematical proof that no unknown secret exists.

Fresh recipients must generate their own private pairing configuration. The packages do not carry a maintainer's already-working pairing state. Installer manifests prove file integrity; they are not publisher certificates, independent security certification or full fresh-machine application acceptance.
