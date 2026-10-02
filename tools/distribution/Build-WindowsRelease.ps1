#requires -Version 7.2
[CmdletBinding()]
param(
    [string] $SourceRoot = (Join-Path $PSScriptRoot '../..'),
    [ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version = '0.0.1'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($SourceRoot)
$project = Join-Path $sourcePath 'src/LinkBridge.App/LinkBridge.App.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'LinkBridge application project not found.' }
$releaseRoot = Join-Path $sourcePath 'artifacts/public-release'
[void][IO.Directory]::CreateDirectory($releaseRoot)
$stage = Join-Path $releaseRoot ('windows-stage-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $releaseRoot 'windows'
$previous = $null
foreach ($releasePath in @($stage, $destination)) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($releasePath)) -ne [IO.Path]::GetFullPath($releaseRoot)) { throw 'Release output escaped its named directory.' }
}
if ((Get-Item -LiteralPath $releaseRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release root is a reparse point.' }
$assemblyVersion = $Version + '.0'
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage `
    '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false' `
    "-p:Version=$Version" "-p:AssemblyVersion=$assemblyVersion" "-p:FileVersion=$assemblyVersion" `
    "-p:InformationalVersion=$Version" "-p:PathMap=$sourcePath=/_/LinkBridge"
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed; previous release was not replaced.' }
$applicationAssembly = Join-Path $stage 'LinkBridge.App.dll'
$actualVersion = [Reflection.AssemblyName]::GetAssemblyName($applicationAssembly).Version.ToString()
if ($actualVersion -ne $assemblyVersion) { throw 'Application assembly version does not match the requested release.' }
foreach ($name in @('LinkBridge.App.exe', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $name) -PathType Leaf)) { throw 'Publish is not a complete self-contained Windows app.' }
}
Copy-Item -LiteralPath (Join-Path $sourcePath 'src/LinkBridge.App/Assets/linkbridge.ico') -Destination (Join-Path $stage 'LinkBridge.ico')
foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $sourcePath $name) -Destination (Join-Path $stage $name)
}
Copy-Item -LiteralPath (Join-Path $sourcePath 'docs/BUILDING.md') -Destination (Join-Path $stage 'BUILDING.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Windows.ps1') -Destination (Join-Path $stage 'Install-Windows.ps1')
Copy-Item -LiteralPath (Join-Path $sourcePath 'tools/open-source/New-PairingProfile.ps1') -Destination (Join-Path $stage 'New-PairingProfile.ps1')

$runtimeConfiguration = Get-Content -LiteralPath (Join-Path $stage 'LinkBridge.App.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeFramework = @($runtimeConfiguration.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')
if ($runtimeFramework.Count -ne 1) { throw 'Cannot identify the bundled .NET runtime.' }
$runtimeVersion = [string]$runtimeFramework[0].version
$cacheOutput = & dotnet nuget locals global-packages --list --force-english-output
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate restored runtime-pack notices.' }
$cacheLine = @($cacheOutput | Where-Object { $_ -match '^global-packages:\s*(.+)$' })
if ($cacheLine.Count -ne 1) { throw 'Cannot locate the NuGet global package directory.' }
$nugetDirectory = ($cacheLine[0] -replace '^global-packages:\s*', '').Trim()
$runtimePackage = Join-Path $nugetDirectory ('microsoft.netcore.app.runtime.win-x64/' + $runtimeVersion)
foreach ($entry in @(
    @('LICENSE.TXT', 'LICENSE.NETRuntime.txt'),
    @('THIRD-PARTY-NOTICES.TXT', 'THIRD_PARTY_NOTICES.NETRuntime.txt')
)) {
    $noticeSource = Join-Path $runtimePackage $entry[0]
    if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) { throw 'Bundled runtime license/notice is missing; release stopped.' }
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $stage $entry[1])
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stage 'THIRD_PARTY_NOTICES.NETruntime.md'), @"
# Bundled .NET runtime

This self-contained Windows release includes Microsoft.NETCore.App.Runtime.win-x64 $runtimeVersion.
Its package declares MIT licensing; native/runtime components retain their accompanying notices.
The complete package license and notices are preserved as LICENSE.NETRuntime.txt and
THIRD_PARTY_NOTICES.NETRuntime.txt. Runtime license files already present in publish output remain intact.

Package provenance: https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/$runtimeVersion
Runtime source: https://github.com/dotnet/runtime

The source-level THIRD_PARTY_NOTICES.md table's older framework-dependent build description
is superseded for this binary bundle by this explicit self-contained runtime notice.
FFmpeg, optional audio drivers, and the Windows SDK are not included in this package.
"@, $utf8)
[IO.File]::WriteAllText((Join-Path $stage 'Install.cmd'), @'
@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Windows.ps1" %*
if errorlevel 1 (
  echo.
  echo Installation did not complete. Read the message above.
  pause
  exit /b 1
)
'@, [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), @"
LinkBridge $Version - Windows x64

Extract this entire ZIP, then double-click Install.cmd.
The app and its .NET runtime install for the current user at:
  %LOCALAPPDATA%\Programs\LinkBridge
No administrator install is requested. Existing %APPDATA%\LinkBridge settings are untouched.
An existing LinkBridge desktop shortcut pointing elsewhere causes Conflict; it is never overwritten.

Install.cmd uses ExecutionPolicy Bypass for that single PowerShell process only.
It does not change the user's or machine's saved execution policy, and cannot override enforced policy.
The install script can also be reviewed and run directly. SHA256SUMS.json checks package integrity;
it is not a publisher code signature. This build is not Authenticode signed.

The application starts without a paired computer on a new user account. Pairing currently needs
private settings and initial Mac Accessibility permission. See BUILDING.md and New-PairingProfile.ps1.
The profile generator requires PowerShell 7.2+, separately installed.
FFmpeg is not bundled. Install it separately/on PATH when using media functions.
Native audio drivers and application-visible camera extensions are separate components.

Keep LICENSE, THIRD_PARTY_NOTICES.md and the .NET runtime notices with the application.
"@, $utf8)
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Published release contains a reparse point.' }
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
    if ($relative -match '(?i)(^|/)(settings\.json|latest-release\.json|.*\.pdb|.*\.pem|.*\.p12|.*\.pfx|.*\.log)$') {
        throw 'Unexpected private configuration, signing material, log, or symbol in publish output.'
    }
    [ordered]@{ path = $relative; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    schemaVersion = 1; product = 'LinkBridge'; version = $Version; runtimeIdentifier = 'win-x64'
    selfContained = $true; applicationAssemblyVersion = $actualVersion; bundledRuntimeVersion = $runtimeVersion
    createdAtUtc = [DateTime]::UtcNow.ToString('o'); files = $files
}
[IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)
if (Test-Path -LiteralPath $destination) {
    if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release target is a reparse point.' }
    $previous = Join-Path $releaseRoot ('windows-previous-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($previous)) -ne [IO.Path]::GetFullPath($releaseRoot)) { throw 'Release backup destination mismatch.' }
    Move-Item -LiteralPath $destination -Destination $previous
}
try { Move-Item -LiteralPath $stage -Destination $destination }
catch {
    if ($null -ne $previous -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $destination
    }
    throw
}
Write-Host ('Public Windows release: ' + $destination)
Write-Host ('App assembly: ' + $actualVersion + '; bundled runtime: ' + $runtimeVersion)
