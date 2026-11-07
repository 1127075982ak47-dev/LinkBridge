#requires -Version 5.1
[CmdletBinding()]
param([switch]$GenerateOnly,[string]$WindowsAddress,[string]$MacAddress,[int]$MacWidth=2560,[int]$MacHeight=1440,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
function New-PrivatePair {
    param([string]$WinIP,[string]$MacIP,[int]$Width,[int]$Height,[string]$Output)
    foreach($value in @($WinIP,$MacIP)) {
        $ip=$null
        if(-not [Net.IPAddress]::TryParse($value,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw '请输入两台电脑各自的 IPv4 地址。'}
        $octets=$ip.GetAddressBytes()
        if($octets[0] -eq 0 -or $octets[0] -eq 127 -or $octets[0] -ge 224 -or $octets[3] -eq 255){throw '不能使用回环、广播或组播地址。'}
    }
    if($WinIP -eq $MacIP){throw '两台电脑的地址不能相同。'}
    if($Width -lt 320 -or $Width -gt 16384 -or $Height -lt 200 -or $Height -gt 16384){throw '屏幕宽高超出支持范围。'}
    if([string]::IsNullOrWhiteSpace($Output)){$Output=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('LinkBridge\PairingProfiles\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))}
    $Output=[IO.Path]::GetFullPath($Output)
    if(Test-Path -LiteralPath $Output){throw '输出目录已存在，请使用一个新的目录。'}
    $ancestor=[IO.DirectoryInfo]::new($Output)
    while($ancestor){
        if($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '输出路径不能经过链接目录。'}
        if(Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')){throw '配对文件含密钥，请保存在源码仓库外。'}
        $ancestor=$ancestor.Parent
    }
    [void][IO.Directory]::CreateDirectory($Output)
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))){
        $rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow'); $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Output -AclObject $acl
    $random=[Security.Cryptography.RandomNumberGenerator]::Create(); $bytes=New-Object byte[] 16
    try{$random.GetBytes($bytes);$secret=[BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()}finally{$random.Dispose();[Array]::Clear($bytes,0,$bytes.Length)}
    $utf8=New-Object Text.UTF8Encoding($false)
    foreach($side in @('windows','mac')){
        $profile=[ordered]@{displayName=$(if($side -eq 'windows'){'Windows'}else{'Mac'});autoDiscover=$true;enableInputServer=($side -eq 'mac');allowedControllerAddress=$(if($side -eq 'windows'){$MacIP}else{$WinIP});inputPairingSecret=$secret;remoteDisplayWidth=$Width;remoteDisplayHeight=$Height;shareClipboardAutomatically=$true;shareSystemAudioAutomatically=$false;shareLocalCamera=$false;shareLocalMicrophone=$false;windowRoamingEnabled=$false;launchAtStartup=$false}
        [IO.File]::WriteAllText((Join-Path $Output ($side+'.settings.json')),($profile|ConvertTo-Json),$utf8)
    }
    foreach($name in @('Import-Windows-Profile.ps1','Import-Mac-Profile.js','Install-Mac-Profile.command')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Output}
    [IO.File]::WriteAllText((Join-Path $Output 'Install-Windows-Profile.cmd'),"@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Import-Windows-Profile.ps1`"`r`npause`r`n",[Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $Output '先读我.txt'),"本目录含本次新配对密钥，勿公开上传。`r`n1. 退出两端 LinkBridge。`r`n2. Windows 双击 Install-Windows-Profile.cmd。`r`n3. 将本目录私下传到 Mac，在终端输入 sh 和一个空格，再拖入 Install-Mac-Profile.command，按回车。`r`n4. 启动两端 LinkBridge，完成 Mac 辅助功能授权，再点一键连接，打开屏幕布局识别、选择并保存接缝。`r`n导入会备份现有设置，只更新配对、角色与屏幕尺寸，保留已有共享偏好；不修改防火墙或系统授权。`r`n只生成一次，让两个配置保持同一个密钥。新客户应生成自己的一对。`r`n",$utf8)
    return $Output
}
if($GenerateOnly){New-PrivatePair $WindowsAddress $MacAddress $MacWidth $MacHeight $OutputDirectory;return}
Add-Type -AssemblyName PresentationFramework
[xml]$xaml=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Title="LinkBridge 首次配对助手" Width="620" Height="570" WindowStartupLocation="CenterScreen" ResizeMode="NoResize" FontFamily="Microsoft YaHei" Background="#F7F9FC">
<StackPanel Margin="30">
<TextBlock Text="连接你自己的两台电脑" FontSize="24" FontWeight="Bold" Margin="0,0,0,12"/>
<TextBlock Text="适用于 Windows 键鼠控制 Apple Silicon Mac。生成一组私有配对文件，导入并连接后在“屏幕布局”设置上下左右。" TextWrapping="Wrap" FontSize="14" Margin="0,0,0,22"/>
<TextBlock Text="Windows 的局域网 IPv4 地址"/><TextBox Name="WinIP" Height="32" FontSize="16" Margin="0,6,0,14"/>
<TextBlock Text="Mac 的局域网 IPv4 地址"/><TextBox Name="MacIP" Height="32" FontSize="16" Margin="0,6,0,14"/>
<TextBlock Text="初始 Mac 逻辑分辨率（配对后通过屏幕布局读取并确认）"/>
<StackPanel Orientation="Horizontal" Margin="0,6,0,16"><TextBox Name="WidthBox" Text="2560" Width="100" Height="32" FontSize="16"/><TextBlock Text=" × " VerticalAlignment="Center"/><TextBox Name="HeightBox" Text="1440" Width="100" Height="32" FontSize="16"/></StackPanel>
<TextBlock Text="地址查看：Windows 设置 → 网络和 Internet → 当前连接；Mac 系统设置 → 网络 → 当前连接 → 详细信息。两台电脑连接同一可信局域网。" TextWrapping="Wrap" FontSize="13" Foreground="#526174" Margin="0,0,0,20"/>
<Button Name="Generate" Content="生成配对文件并打开文件夹" Height="44" FontSize="16" Background="#DDEBFF"/>
<TextBlock Name="Status" TextWrapping="Wrap" Foreground="#9B341B" Margin="0,12,0,0"/>
</StackPanel></Window>
'@
$window=[Windows.Markup.XamlReader]::Load((New-Object Xml.XmlNodeReader($xaml)))
$localIP=@(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {$_.IPAddress -notmatch '^(127\.|169\.254\.|198\.18\.)' -and $_.InterfaceAlias -notmatch 'Loopback|Tailscale|WSL|vEthernet'} | Select-Object -ExpandProperty IPAddress)
if($localIP.Count -eq 1){$window.FindName('WinIP').Text=$localIP[0]}
$window.FindName('Generate').Add_Click({
    try{
        $folder=New-PrivatePair $window.FindName('WinIP').Text.Trim() $window.FindName('MacIP').Text.Trim() ([int]$window.FindName('WidthBox').Text) ([int]$window.FindName('HeightBox').Text) ''
        $window.FindName('Status').Text='已生成。请阅读文件夹内的先读我，退出软件后分别导入两端配置。'
        Start-Process explorer.exe -ArgumentList ('"'+$folder+'"')
    }catch{$window.FindName('Status').Text=$_.Exception.Message}
})
[void]$window.ShowDialog()
