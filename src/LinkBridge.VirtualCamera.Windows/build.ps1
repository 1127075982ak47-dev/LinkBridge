$ErrorActionPreference = 'Stop'
$featureRoot = $PSScriptRoot
$wilPackage = Join-Path $featureRoot 'deps\wil.zip'
$wilDir = Join-Path $featureRoot 'deps\wil'
$wilHash = '7D67BC71DD0EDC342DB44EA0334AC9B6A1776514F81694B0B5B3C7AC9D608E42'
if (-not (Test-Path $wilPackage)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $wilPackage) | Out-Null
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.implementationlibrary/1.0.260126.7/microsoft.windows.implementationlibrary.1.0.260126.7.nupkg' -OutFile $wilPackage
}
if ((Get-FileHash -LiteralPath $wilPackage -Algorithm SHA256).Hash -ne $wilHash) { throw 'Pinned WIL package hash mismatch.' }
if (-not (Test-Path (Join-Path $wilDir 'include\wil\result.h'))) { Expand-Archive -LiteralPath $wilPackage -DestinationPath $wilDir -Force }
cmake -S $featureRoot -B (Join-Path $featureRoot 'build') -G 'Visual Studio 17 2022' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.26100.0'
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed.' }
cmake --build (Join-Path $featureRoot 'build') --config Release --parallel 3
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
Write-Output 'Native camera build complete. Registration/create commands were not executed.'
