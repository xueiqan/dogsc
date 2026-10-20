[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskApp = Join-Path $PSScriptRoot 'artifacts\win-x64\DogSC.Windows.exe'
if (-not (Test-Path -LiteralPath $taskApp)) {
    throw '尚未构建Windows版，请先运行本目录的build.ps1。'
}
Start-Process -FilePath $taskApp -WorkingDirectory (Split-Path -Parent $taskApp)
