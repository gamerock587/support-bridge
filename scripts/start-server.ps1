param([string]$Listen = 'http://127.0.0.1:4353')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'dist/Server/Bridge.Server.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/build.ps1 first.' }
$env:BRIDGE_LISTEN = $Listen
$env:BRIDGE_DATA = Join-Path $root 'dist/Server/data'
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$root/server.log" -RedirectStandardError "$root/server-error.log"
Start-Sleep -Seconds 2
if ($process.HasExited) { throw 'Server failed. Read server-error.log and server.log.' }
$process.Id | Set-Content "$root/dist/server.pid"
Write-Output "Server PID: $($process.Id)"
Write-Output "Listen: $Listen"
Write-Output "Token file: $env:BRIDGE_DATA/operator-token.txt"
