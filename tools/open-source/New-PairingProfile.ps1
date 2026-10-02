#requires -Version 7.2
<#
.SYNOPSIS
Create private, fresh LinkBridge profiles for a Windows-left / Mac-right pair.
.DESCRIPTION
Generates one cryptographically random 32-character hexadecimal pairing secret and writes
two settings files. It does not print the secret, read existing settings, install profiles,
change firewall rules, or overwrite output. Keep the generated files outside source control.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WindowsAddress,
    [Parameter(Mandatory)][string] $MacAddress,
    [string] $OutputDirectory,
    [ValidateRange(320, 16384)][int] $MacDisplayWidth = 2560,
    [ValidateRange(200, 16384)][int] $MacDisplayHeight = 1440
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-PeerIPv4([string] $Value) {
    $parsedAddress = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref] $parsedAddress) -or
        $parsedAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'Use a literal IPv4 address for each computer.'
    }
    $octets = $parsedAddress.GetAddressBytes()
    if ($octets[0] -eq 0 -or $octets[0] -eq 127 -or $octets[0] -ge 224 -or
        $parsedAddress.Equals([System.Net.IPAddress]::Broadcast)) {
        throw 'Use the actual peer interface address, not wildcard, loopback, multicast, or broadcast.'
    }
    return $parsedAddress.ToString()
}

function Assert-OrdinaryAncestors([string] $Path) {
    $ancestor = [System.IO.DirectoryInfo]::new($Path)
    while ($null -ne $ancestor) {
        if ($ancestor.Exists -and (($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Output must not traverse a symbolic link or reparse point.'
        }
        if ([System.IO.Directory]::Exists([System.IO.Path]::Combine($ancestor.FullName, '.git')) -or
            [System.IO.File]::Exists([System.IO.Path]::Combine($ancestor.FullName, '.git'))) {
            throw 'Pairing profiles contain a secret. Choose an output directory outside the Git checkout.'
        }
        $ancestor = $ancestor.Parent
    }
}

function Protect-ProfileDirectory([string] $Path) {
    if ($IsWindows) {
        $security = [System.Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $principals = @(
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        )
        foreach ($principal in $principals) {
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                $principal, [System.Security.AccessControl.FileSystemRights]::FullControl,
                [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)
            [void] $security.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $Path -AclObject $security
    }
    else {
        [System.IO.File]::SetUnixFileMode($Path, [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute')
    }
}

$windowsIPv4 = Resolve-PeerIPv4 $WindowsAddress
$macIPv4 = Resolve-PeerIPv4 $MacAddress
if ($windowsIPv4 -eq $macIPv4) { throw 'The two computers must have different interface addresses.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $privateBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($privateBase)) {
        $privateBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    }
    $folderName = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = [System.IO.Path]::Combine($privateBase, 'LinkBridge', 'PairingProfiles', $folderName)
}
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
Assert-OrdinaryAncestors $outputPath
if ([System.IO.Directory]::Exists($outputPath) -or [System.IO.File]::Exists($outputPath)) {
    throw 'Output already exists. Choose a new directory; existing profiles will not be replaced.'
}
[void] [System.IO.Directory]::CreateDirectory($outputPath)
Protect-ProfileDirectory $outputPath

$keyBytes = [byte[]]::new(16)
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($keyBytes)
    $pairingKey = [BitConverter]::ToString($keyBytes).Replace('-', '').ToLowerInvariant()
    $windowsProfile = [ordered]@{
        displayName = 'Windows'
        autoDiscover = $true
        enableInputServer = $false
        allowedControllerAddress = $macIPv4
        inputPairingSecret = $pairingKey
        remoteDisplayWidth = $MacDisplayWidth
        remoteDisplayHeight = $MacDisplayHeight
        shareClipboardAutomatically = $true
        shareSystemAudioAutomatically = $false
        shareLocalCamera = $false
        shareLocalMicrophone = $false
        launchAtStartup = $false
    }
    $macProfile = [ordered]@{
        displayName = 'Mac'
        autoDiscover = $true
        enableInputServer = $true
        allowedControllerAddress = $windowsIPv4
        inputPairingSecret = $pairingKey
        remoteDisplayWidth = $MacDisplayWidth
        remoteDisplayHeight = $MacDisplayHeight
        shareClipboardAutomatically = $true
        shareSystemAudioAutomatically = $false
        shareLocalCamera = $false
        shareLocalMicrophone = $false
        launchAtStartup = $false
    }
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $profiles = @{
        'windows.settings.json' = $windowsProfile
        'mac.settings.json' = $macProfile
    }
    foreach ($name in $profiles.Keys) {
        $destination = [System.IO.Path]::Combine($outputPath, $name)
        $serialized = $profiles[$name] | ConvertTo-Json -Depth 4
        $stream = [System.IO.FileStream]::new($destination, [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            if (-not $IsWindows) {
                [System.IO.File]::SetUnixFileMode($destination, [System.IO.UnixFileMode]'UserRead, UserWrite')
            }
            $bytes = $utf8.GetBytes($serialized + [Environment]::NewLine)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
    }
    Write-Host ('Windows profile: ' + [System.IO.Path]::Combine($outputPath, 'windows.settings.json'))
    Write-Host ('Mac profile: ' + [System.IO.Path]::Combine($outputPath, 'mac.settings.json'))
    Write-Host 'Created a new private pair. The key was not printed. Transfer only the Mac profile through a trusted private channel.'
    Write-Host 'Close both apps before installing profiles. This tool did not install or replace existing settings.'
}
finally {
    $random.Dispose()
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    $pairingKey = $null
    $windowsProfile = $null
    $macProfile = $null
    $serialized = $null
    $profiles = $null
    $bytes = $null
}
