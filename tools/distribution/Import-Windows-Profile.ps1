#requires -Version 5.1
param([string]$ProfilePath,[string]$ConfigurationDirectory)
$ErrorActionPreference='Stop'
if(-not $ProfilePath){$ProfilePath=Join-Path $PSScriptRoot 'windows.settings.json'}
$defaultRoot=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'LinkBridge'
$isDefault=[string]::IsNullOrWhiteSpace($ConfigurationDirectory) -or [IO.Path]::GetFullPath($ConfigurationDirectory).TrimEnd('\') -eq $defaultRoot.TrimEnd('\')
if($isDefault -and (Get-Process LinkBridge.App -ErrorAction SilentlyContinue)){throw '请先完全退出 LinkBridge，再导入配置。'}
$profile=Get-Content -LiteralPath $ProfilePath -Raw -Encoding UTF8 | ConvertFrom-Json
if($profile.inputPairingSecret -notmatch '^[0-9a-f]{32}$' -or $profile.enableInputServer -ne $false){throw '这不是有效的 Windows 配对文件。'}
$ip=$null
if(-not [Net.IPAddress]::TryParse($profile.allowedControllerAddress,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw '对端地址无效。'}
$root=if([string]::IsNullOrWhiteSpace($ConfigurationDirectory)){Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'LinkBridge'}else{[IO.Path]::GetFullPath($ConfigurationDirectory)}
$target=Join-Path $root 'settings.json'
foreach($p in @($root,$target)){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '设置路径是链接，导入已停止。'}}
[void][IO.Directory]::CreateDirectory($root)
$existing=@{}
if(Test-Path -LiteralPath $target){
    $old=Get-Content -LiteralPath $target -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($property in $old.PSObject.Properties){$existing[$property.Name]=$property.Value}
    Copy-Item -LiteralPath $target -Destination (Join-Path $root ('settings.before-pairing-'+[Guid]::NewGuid().ToString('N')+'.json'))
}
foreach($property in $profile.PSObject.Properties){
    if(-not $existing.ContainsKey($property.Name) -or $property.Name -in @('enableInputServer','allowedControllerAddress','inputPairingSecret','remoteDisplayWidth','remoteDisplayHeight')){$existing[$property.Name]=$property.Value}
}
$existing['lastConnectedPeerAddress']=$profile.allowedControllerAddress
$temp=Join-Path $root ('settings.pairing-'+[Guid]::NewGuid().ToString('N')+'.tmp')
[IO.File]::WriteAllText($temp,($existing|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $temp -Destination $target -Force
Write-Host '配对配置已导入，原有设置已备份。请完成 Mac 端导入和授权，再打开两端软件。'
