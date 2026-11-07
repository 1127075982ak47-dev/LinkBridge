#requires -Version 5.1
param([ValidateSet('Prepare','Integrate','Remove')][string]$Action,[Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference='Stop'
$install=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$exe=Join-Path $install 'LinkBridge.App.exe'
$marker=Join-Path $install 'linkbridge-install.json'
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LinkBridge.Open'
$desktop=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'LinkBridge.lnk'
$menu=Join-Path ([Environment]::GetFolderPath('Programs')) 'LinkBridge'
$shell=New-Object -ComObject WScript.Shell
function Assert-Path {
    if($install.Length -lt 12 -or [IO.Path]::GetPathRoot($install).TrimEnd('\') -eq $install){throw '安装目录无效。'}
    foreach($special in @('UserProfile','DesktopDirectory','ApplicationData','LocalApplicationData','Programs','ProgramFiles','Windows')){
        $p=[Environment]::GetFolderPath($special)
        if($p -and [string]::Equals($p.TrimEnd('\'),$install,[StringComparison]::OrdinalIgnoreCase)){throw '请选择一个专用子目录。'}
    }
    $a=[IO.DirectoryInfo]::new($install)
    while($a){if($a.Exists -and ($a.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '安装路径不能经过链接目录。'};$a=$a.Parent}
}
Assert-Path
if($Action -eq 'Prepare'){
    if((Test-Path -LiteralPath $install) -and @(Get-ChildItem -LiteralPath $install -Force).Count -gt 0){
        if(-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath (Join-Path $install 'LinkBridge.App.dll'))){throw '该目录包含其他文件，请选择空目录或现有 LinkBridge 目录。'}
        $assembly=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $install 'LinkBridge.App.dll'))
        if($assembly.Name -ne 'LinkBridge.App'){throw '现有应用标识不符。'}
        foreach($p in @(Get-Process LinkBridge.App -ErrorAction SilentlyContinue)){
            if($p.Path -eq $exe){if(-not $p.CloseMainWindow() -or -not $p.WaitForExit(12000)){throw '请退出这个安装目录中的 LinkBridge 再重试。'}}
        }
        $backupBase=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LinkBridge\InstallBackups'
        $backup=Join-Path $backupBase ([DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
        if(-not ([IO.Path]::GetFullPath($backup)).StartsWith([IO.Path]::GetFullPath($backupBase)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '备份路径校验失败。'}
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Get-ChildItem -LiteralPath $install -Force | Copy-Item -Destination $backup -Recurse
    }
    Write-Output '安装目录已检查；已有版本已备份。'
}elseif($Action -eq 'Integrate'){
    $release=Get-Content -LiteralPath (Join-Path $install 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    New-Item -ItemType Directory -Force -Path $menu | Out-Null
    foreach($entry in @(@('LinkBridge.lnk',$exe,''),@('首次配对助手.lnk',"$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe",('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$install+'\Pairing\Pairing-Assistant.ps1"')), @('用户说明书.lnk',(Join-Path $install 'Help\LinkBridge用户说明书.html'),''))){
        $lnk=$shell.CreateShortcut((Join-Path $menu $entry[0]));$lnk.TargetPath=$entry[1];$lnk.Arguments=$entry[2];$lnk.WorkingDirectory=$install;$lnk.IconLocation=(Join-Path $install 'LinkBridge.ico')+',0';$lnk.Save()
    }
    $desktopOwned=$false
    if(-not (Test-Path -LiteralPath $desktop)){
        $lnk=$shell.CreateShortcut($desktop);$lnk.TargetPath=$exe;$lnk.WorkingDirectory=$install;$lnk.IconLocation=(Join-Path $install 'LinkBridge.ico')+',0';$lnk.Save();$desktopOwned=$true
    }elseif($shell.CreateShortcut($desktop).TargetPath -eq $exe){$desktopOwned=$true}
    @{product='LinkBridge';delivery=$release.delivery;desktopOwned=$desktopOwned;directory=$install}|ConvertTo-Json|Set-Content -LiteralPath $marker -Encoding UTF8
    New-Item -Path $registry -Force | Out-Null
    $values=@{DisplayName='LinkBridge 联桥';DisplayVersion=$release.label;Publisher='LinkBridge contributors';InstallLocation=$install;DisplayIcon=(Join-Path $install 'LinkBridge.ico');UninstallString=('"'+$install+'\Uninstall.exe"');QuietUninstallString=('"'+$install+'\Uninstall.exe" /S');URLInfoAbout='https://github.com/1127075982ak47-dev/LinkBridge'}
    foreach($k in $values.Keys){Set-ItemProperty -LiteralPath $registry -Name $k -Value $values[$k]}
    foreach($k in @('NoModify','NoRepair')){New-ItemProperty -LiteralPath $registry -Name $k -Value 1 -PropertyType DWord -Force|Out-Null}
    Write-Output '快捷方式与卸载入口已建立，已有其他桌面入口已保留。'
}else{
    foreach($p in @(Get-Process LinkBridge.App -ErrorAction SilentlyContinue)){
        if($p.Path -eq $exe){if(-not $p.CloseMainWindow() -or -not $p.WaitForExit(12000)){throw '请退出软件后再卸载。'}}
    }
    if(Test-Path -LiteralPath $marker){
        $info=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
        if($info.product -ne 'LinkBridge' -or $info.directory -ne $install){throw '安装记录不匹配，卸载已停止。'}
        if($info.desktopOwned -and (Test-Path -LiteralPath $desktop) -and $shell.CreateShortcut($desktop).TargetPath -eq $exe){Remove-Item -LiteralPath $desktop}
    }
    $menuMain=Join-Path $menu 'LinkBridge.lnk'
    if((Test-Path -LiteralPath $menuMain) -and $shell.CreateShortcut($menuMain).TargetPath -eq $exe){
        foreach($n in @('LinkBridge.lnk','首次配对助手.lnk','用户说明书.lnk')){$p=Join-Path $menu $n;if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p}}
        if(@(Get-ChildItem -LiteralPath $menu -Force).Count -eq 0){Remove-Item -LiteralPath $menu}
    }
    if((Test-Path -LiteralPath $registry) -and (Get-ItemProperty -LiteralPath $registry).InstallLocation -eq $install){Remove-Item -LiteralPath $registry}
    Write-Output '卸载入口已清理；用户配置、配对身份和备份均保留。'
}
