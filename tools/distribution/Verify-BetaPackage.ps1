#requires -Version 5.1
[CmdletBinding()]
param([string]$PackageRoot)
$ErrorActionPreference='Stop'
if(-not $PackageRoot){$PackageRoot=Join-Path $PSScriptRoot '..'}
$root=[IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$manifest=Get-Content -LiteralPath (Join-Path $root 'PACKAGE-MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.product -ne 'LinkBridge' -or $manifest.label -ne '0.0.4-beta.1' -or @($manifest.files).Count -eq 0){throw 'Unexpected package manifest.'}
$seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $manifest.files){
    $relative=[string]$entry.path
    if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($relative -split '[\\/]') -contains '..' -or -not $seen.Add($relative)){throw 'Unsafe or duplicate package path.'}
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package path escaped its root.'}
    $ancestor=[IO.FileInfo]::new($path)
    while($ancestor -and $ancestor.FullName.Length -ge $root.Length){
        if($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked package path rejected.'}
        if($ancestor -is [IO.FileInfo]){$ancestor=$ancestor.Directory}else{$ancestor=$ancestor.Parent}
    }
    $file=Get-Item -LiteralPath $path
    if($file.PSIsContainer -or $file.Length -ne $entry.bytes -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256){throw ('Package integrity failed: '+$relative)}
}
Write-Output ('PASS '+$manifest.label+'; '+$seen.Count+' files verified. Hashes verify integrity, not publisher identity.')
