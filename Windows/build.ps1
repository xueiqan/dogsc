[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) {
    throw '缺少项目内.NET10.0.401 SDK，请先按Windows/README.md准备工具链。'
}
$env:DOTNET_ROOT = Split-Path -Parent $taskSdk
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $taskSdk run --project 'DogSC.Core.SelfTest\DogSC.Core.SelfTest.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw '项目保存与导出自测失败。' }
    & $taskSdk run --project 'DogSC.Windows.SelfTest\DogSC.Windows.SelfTest.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows录制与故障恢复自测失败。' }
    & $taskSdk publish 'DogSC.Windows\DogSC.Windows.csproj' -c Release -r win-x64 --self-contained true -o 'artifacts\win-x64'
    if ($LASTEXITCODE -ne 0) { throw 'Windows构建失败。' }
    Write-Output ('已生成：' + (Join-Path $PSScriptRoot 'artifacts\win-x64\DogSC.Windows.exe'))
} finally {
    Pop-Location
}
