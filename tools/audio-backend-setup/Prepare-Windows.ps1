[CmdletBinding()]
param(
    [ValidateSet('Inspect','Stage')][string]$Mode='Inspect',
    [string]$PackageDirectory = (Join-Path $env:LOCALAPPDATA 'LinkBridge\BackendSetup\VBCABLE45'),
    [string]$BackupFile = (Join-Path $env:LOCALAPPDATA ('LinkBridge\BackendSetup\windows-defaults-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json'))
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This plan is for Windows x64.' }
$cache = Join-Path $env:LOCALAPPDATA 'LinkBridge\BackendSetup'
$helper = Join-Path $cache 'WindowsDefaults'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'WindowsDefaults\WindowsDefaults.csproj') -c Release --output $helper --artifacts-path (Join-Path $cache 'build') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Default snapshot helper build failed.' }
& dotnet (Join-Path $helper 'WindowsDefaults.dll') snapshot $BackupFile
if ($LASTEXITCODE -ne 0) { throw 'Default snapshot failed; no installation plan is allowed.' }
Write-Output "BACKUP=$BackupFile"
if ($Mode -eq 'Stage') {
    $zip = Join-Path $cache 'VBCABLE_Driver_Pack45.zip'
    Invoke-WebRequest -Uri 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip' -OutFile $zip
    # Review pin, not a vendor-published checksum. Changed vendor packages require a fresh review.
    $expected = 'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB'
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'ZIP review hash differs; do not extract/execute.' }
    if (Test-Path -LiteralPath $PackageDirectory) { throw 'Stage target already exists; select a fresh directory. Nothing overwritten.' }
    Expand-Archive -LiteralPath $zip -DestinationPath $PackageDirectory
}
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    Write-Output 'Package not staged. Use -Mode Stage to download/verify outside the repository. No install performed.'
    return
}
$installer = Join-Path $PackageDirectory 'VBCABLE_Setup_x64.exe'
$catalog = Join-Path $PackageDirectory 'vbaudio_cable64_win10.cat'
$driver = Join-Path $PackageDirectory 'vbaudio_cable64_win10.sys'
$inf = Join-Path $PackageDirectory 'vbMmeCable64_win10.inf'
$manifest = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'vbcable-pack45-sha256.json') -Raw | ConvertFrom-Json)
$actualFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -File)
if ($actualFiles.Count -ne $manifest.Count -or @(Get-ChildItem -LiteralPath $PackageDirectory -Directory).Count -ne 0) { throw 'Extracted package file set differs from reviewed Pack45.' }
foreach ($entry in $manifest) {
    $file = Join-Path $PackageDirectory $entry.Name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Package file review hash differs: $($entry.Name)" }
}
foreach ($file in @($installer,$catalog,$driver,$inf)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Complete extracted vendor package required: missing $file" }
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'BUREL VINCENT') { throw 'Installer signature/publisher invalid.' }
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne '734C35DFA6D98F48782A451633CEB471166EC70D60482FD89A1123D0EE3C4F41') { throw 'Installer review hash differs.' }
$catSignature = Get-AuthenticodeSignature -LiteralPath $catalog
if ($catSignature.Status -ne 'Valid' -or $catSignature.SignerCertificate.Subject -notmatch 'Microsoft Windows Hardware Compatibility Publisher') { throw 'Kernel driver catalog signature invalid.' }
$signTools = @(Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\signtool.exe$' } | Sort-Object FullName -Descending)
if ($signTools.Count -eq 0) { throw 'Windows SDK signtool is required for catalog membership verification; no install plan issued.' }
& $signTools[0].FullName verify /kp /c $catalog $driver
if ($LASTEXITCODE -ne 0) { throw 'Driver not verified by signed kernel catalog.' }
$snapshot = Get-Content -LiteralPath $BackupFile -Raw | ConvertFrom-Json
$cable = @($snapshot.Devices | Where-Object { $_.Name -match 'CABLE (Input|Output)' -and $_.State -eq 'Active' })
$report = [ordered]@{
    VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); PackageDirectory=$PackageDirectory; Backup=$BackupFile;
    InstallerStatus=[string]$signature.Status; InstallerPublisher=$signature.SignerCertificate.Subject;
    InstallerSha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;
    CatalogStatus=[string]$catSignature.Status; ExistingCableEndpoints=$cable;
    PerformedInstall=$false; ChangedDefaultDevices=$false; Escalated=$false; Rebooted=$false
}
$report | ConvertTo-Json -Depth 6
Write-Output 'PLAN ONLY: close audio apps after scheduling an interruption; right-click the verified installer and Run as administrator, then choose Install Driver (or Remove Driver for rollback). Reboot is required. No installer has been launched.'
Write-Output "VERIFIED_INSTALLER=$installer"
Write-Output "AFTER_INSTALL_DRY_RUN: dotnet '$helper\WindowsDefaults.dll' restore '$BackupFile'"
Write-Output 'Prefer manual restore via mmsys.cpl. Exact six-role automatic restore requires explicit --apply-private-policyconfig (undocumented COM ABI; not yet mutation-tested).'
