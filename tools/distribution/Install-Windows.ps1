#requires -Version 5.1
[CmdletBinding()]
param([string]$PackageDirectory = '', [switch]$NoStart, [switch]$ValidateOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    # Windows PowerShell 5.1 may bind parameter defaults before PSScriptRoot is populated.
    # Resolve only after entering the script body; the invocation path is the fallback.
    $PackageDirectory = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
        $PackageDirectory = [IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path)
    }
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) { throw 'Installer package directory unavailable.' }

function Resolve-PackageFile([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or
        @($Relative.Replace('\', '/').Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) { throw 'Invalid package path.' }
    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Relative.Replace('/', '\')))
    if (-not $full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes its directory.' }
    $ancestor = [IO.FileInfo]::new($full).Directory
    while ($null -ne $ancestor -and $ancestor.FullName.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
        if ($ancestor.Exists -and (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Package contains a linked directory.' }
        $ancestor = $ancestor.Parent
    }
    return $full
}
function Assert-InstallAncestors([string]$Path) {
    $ancestor = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $ancestor) {
        if ($ancestor.Exists -and (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Install path traverses a reparse point.' }
        $ancestor = $ancestor.Parent
    }
}

$package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$manifestPath = Join-Path $package 'SHA256SUMS.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'LinkBridge' -or $manifest.runtimeIdentifier -ne 'win-x64' -or
    -not $manifest.selfContained -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or @($manifest.files).Count -eq 0) { throw 'Invalid release manifest.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if (-not $names.Add([string]$entry.path) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid or duplicate manifest file.' }
    $file = Resolve-PackageFile $package ([string]$entry.path)
    $item = Get-Item -LiteralPath $file
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Package integrity failed: ' + $entry.path) }
}
foreach ($required in @('LinkBridge.App.exe', 'LinkBridge.App.dll', 'LinkBridge.ico', 'coreclr.dll', 'System.Private.CoreLib.dll', 'LICENSE', 'LICENSE.NETRuntime.txt', 'THIRD_PARTY_NOTICES.NETRuntime.txt')) {
    if (-not $names.Contains($required)) { throw ('Incomplete package: ' + $required) }
}
$actualAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $package 'LinkBridge.App.dll')).Version.ToString()
if ($actualAssembly -ne [string]$manifest.applicationAssemblyVersion -or $actualAssembly -ne ([string]$manifest.version + '.0')) { throw 'Application assembly version mismatch.' }
if ($ValidateOnly) { Write-Host ('Validated LinkBridge ' + $manifest.version + ' self-contained win-x64 package.'); return }

$localData = [Environment]::GetFolderPath('LocalApplicationData')
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
if ([string]::IsNullOrWhiteSpace($localData) -or [string]::IsNullOrWhiteSpace($desktop)) { throw 'Current user installation locations unavailable.' }
$programs = [IO.Path]::GetFullPath((Join-Path $localData 'Programs'))
$install = [IO.Path]::GetFullPath((Join-Path $programs 'LinkBridge'))
if ([IO.Path]::GetDirectoryName($install) -ne $programs) { throw 'Install destination mismatch.' }
Assert-InstallAncestors $install
$application = Join-Path $install 'LinkBridge.App.exe'
$shortcutPath = Join-Path $desktop 'LinkBridge.lnk'
$shell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $shortcutPath) {
    if ((Get-Item -LiteralPath $shortcutPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Desktop shortcut is a reparse point; installation stopped.' }
    $existingShortcut = $shell.CreateShortcut($shortcutPath)
    $existingTarget = [Environment]::ExpandEnvironmentVariables([string]$existingShortcut.TargetPath)
    if ([string]::IsNullOrWhiteSpace($existingTarget) -or
        -not [string]::Equals([IO.Path]::GetFullPath($existingTarget), [IO.Path]::GetFullPath($application), [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host 'Conflict: the existing LinkBridge desktop shortcut opens a different installation. It was not changed.'
        Write-Host 'Move or rename that shortcut yourself before retrying this installer.'
        exit 2
    }
}

# Settings live under AppData\Roaming\LinkBridge; this installer never reads or changes that tree.
[void][IO.Directory]::CreateDirectory($programs)
$stage = Join-Path $programs ('.LinkBridge-stage-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $programs ('.LinkBridge-backup-' + [Guid]::NewGuid().ToString('N'))
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -ne $programs -or
    [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($backup)) -ne $programs) { throw 'Update staging path mismatch.' }
[void][IO.Directory]::CreateDirectory($stage)
foreach ($entry in $manifest.files) {
    $source = Resolve-PackageFile $package ([string]$entry.path)
    $target = Resolve-PackageFile $stage ([string]$entry.path)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($source, $target, $false)
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Copied application failed integrity validation.' }
}
[IO.File]::Copy($manifestPath, (Join-Path $stage 'SHA256SUMS.json'), $false)
foreach ($process in [Diagnostics.Process]::GetProcessesByName('LinkBridge.App')) {
    try {
        if ([string]::Equals($process.MainModule.FileName, $application, [StringComparison]::OrdinalIgnoreCase)) {
            if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(12000)) { throw 'Close this LinkBridge installation before updating it.' }
        }
    }
    finally { $process.Dispose() }
}
$hadPrevious = Test-Path -LiteralPath $install
if ($hadPrevious) { Move-Item -LiteralPath $install -Destination $backup }
try {
    Move-Item -LiteralPath $stage -Destination $install
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $application
    $shortcut.WorkingDirectory = $install
    $shortcut.IconLocation = (Join-Path $install 'LinkBridge.ico') + ',0'
    $shortcut.Description = 'LinkBridge'
    $shortcut.Save()
}
catch {
    # Preserve the rejected candidate, restore the whole previous application, never delete user data.
    if (Test-Path -LiteralPath $install) {
        $rejected = Join-Path $programs ('.LinkBridge-rejected-' + [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $install -Destination $rejected
    }
    if ($hadPrevious -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $install }
    throw
}
Write-Host ('Installed LinkBridge ' + $manifest.version + ' for the current user.')
if ($hadPrevious) { Write-Host ('Previous application backup: ' + $backup) }
Write-Host ('Desktop shortcut: ' + $shortcutPath)
if (-not $NoStart) { Start-Process -FilePath $application -WorkingDirectory $install -WindowStyle Normal }
