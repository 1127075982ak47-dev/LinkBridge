#requires -Version 5.1
[CmdletBinding()]
param([string]$PackageRoot,[string]$InstallDirectory,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
if(-not $PackageRoot){$PackageRoot=Join-Path $PSScriptRoot '..'}
if(-not $InstallDirectory){$InstallDirectory=Join-Path $env:LOCALAPPDATA 'Programs\LinkBridge'}
if(-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64'){throw 'This beta is validated for Windows x64 only.'}
if([Environment]::OSVersion.Version.Build -lt 22000){throw 'This beta targets Windows 11 (build 22000+).'}
& (Join-Path $PSScriptRoot 'Verify-BetaPackage.ps1') -PackageRoot $PackageRoot
$installer=Join-Path $PackageRoot '01-Windows\LinkBridge-0.0.4-beta.1-Windows-x64-Setup.exe'
$destination=[IO.Path]::GetFullPath($InstallDirectory)
if($destination.Contains('"') -or $destination.Contains("`r") -or $destination.Contains("`n")){throw 'Invalid destination.'}
if($ValidateOnly){Write-Output 'PASS preflight. No installation, pairing or permission changes.';return}
$process=Start-Process -FilePath $installer -ArgumentList ('/S /D='+$destination) -WindowStyle Hidden -PassThru -Wait
if($process.ExitCode -ne 0){throw ('Installer failed with exit code '+$process.ExitCode+'. Run the visible installer for details; preserve the existing app and settings.')}
$manifest=Get-Content -LiteralPath (Join-Path $destination 'PACKAGE-MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.label -ne '0.0.4-beta.1'){throw 'Installed version does not match the requested beta.'}
foreach($entry in $manifest.files){
    $file=[IO.Path]::GetFullPath((Join-Path $destination $entry.path))
    if(-not $file.StartsWith($destination.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe installed manifest path.'}
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256){throw ('Installed file verification failed: '+$entry.path)}
}
[ordered]@{status='installed';version=$manifest.label;directory=$destination;next='Generate ONE private pairing set on Windows; import respective profiles after both apps exit; Mac owner completes permissions; configure screen layout.'}|ConvertTo-Json
