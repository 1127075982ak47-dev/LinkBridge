#requires -Version 7.2
[CmdletBinding()]
param([string]$SourceRoot=(Join-Path $PSScriptRoot '../..'),[Parameter(Mandatory)][string]$NsisPath,[string]$ManualDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$SourceRoot=[IO.Path]::GetFullPath($SourceRoot)
$release=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json
if(-not $ManualDirectory){$ManualDirectory=Join-Path $SourceRoot 'docs/customer'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $SourceRoot 'artifacts/customer-installer'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$publish=Join-Path $SourceRoot 'artifacts/public-release/windows'
if(-not (Test-Path (Join-Path $publish 'LinkBridge.App.exe'))){& (Join-Path $PSScriptRoot 'Build-WindowsRelease.ps1') -SourceRoot $SourceRoot}
$actual=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'LinkBridge.App.dll')).Version.ToString()
if($actual -ne ($release.version+'.0')){throw 'Rebuild the Windows public release: version mismatch.'}
$stage=Join-Path $SourceRoot ('artifacts/installer-stage-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage|Out-Null
Get-ChildItem -LiteralPath $publish -File|Where-Object Name -NotIn @('Install.cmd','Install-Windows.ps1','New-PairingProfile.ps1','README.txt','SHA256SUMS.json')|Copy-Item -Destination $stage
foreach($dir in @('Pairing','Help')){New-Item -ItemType Directory -Path (Join-Path $stage $dir)|Out-Null}
foreach($name in @('Pairing-Assistant.ps1','Import-Windows-Profile.ps1','Import-Mac-Profile.js','Install-Mac-Profile.command')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $stage 'Pairing')}
if(Test-Path -LiteralPath (Join-Path $ManualDirectory 'LinkBridge用户说明书.pdf')){Copy-Item -LiteralPath (Join-Path $ManualDirectory 'LinkBridge用户说明书.pdf') -Destination (Join-Path $stage 'Help')}
Copy-Item -LiteralPath (Join-Path $ManualDirectory 'LinkBridge用户说明书.html') -Destination (Join-Path $stage 'Help')
$utf8=New-Object Text.UTF8Encoding($false)
$files=@(Get-ChildItem $stage -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText((Join-Path $stage 'PACKAGE-MANIFEST.json'),(@{product='LinkBridge';version=$release.version;label=$release.label;delivery=$release.delivery;files=$files}|ConvertTo-Json -Depth 5),$utf8)
$deleteList=Join-Path $stage '..' ('uninstall-'+[Guid]::NewGuid().ToString('N')+'.nsh')
$lines=@(Get-ChildItem $stage -File -Recurse|ForEach-Object{'Delete "$INSTDIR\'+[IO.Path]::GetRelativePath($stage,$_.FullName)+'"'})
$lines+=@(Get-ChildItem $stage -Directory -Recurse|Sort-Object {$_.FullName.Length} -Descending|ForEach-Object{'RMDir "$INSTDIR\'+[IO.Path]::GetRelativePath($stage,$_.FullName)+'"'})
[IO.File]::WriteAllLines($deleteList,$lines,(New-Object Text.UTF8Encoding($true)))
$out=Join-Path $OutputDirectory ('LinkBridge-'+$release.label+'-Windows-x64-Setup.exe')
& $NsisPath /V2 "/DVERSION=$($release.version)" "/DLABEL=$($release.label)" "/DDELIVERY=$($release.delivery)" "/DPAYLOAD=$stage" "/DOUTPUT=$out" "/DOPERATIONS=$(Join-Path $PSScriptRoot 'Installer-Operations.ps1')" "/DDELETE_LIST=$deleteList" (Join-Path $PSScriptRoot 'customer-installer.nsi')
if($LASTEXITCODE -ne 0 -or -not (Test-Path $out)){throw 'NSIS compilation failed.'}
Get-Item $out|Select-Object Name,Length
Get-FileHash $out
