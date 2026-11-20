param(
    [ValidateSet('devices','pair','exec','list','read','write','delete','revoke','job')][string]$Action = 'devices',
    [string]$Server = 'http://127.0.0.1:4353',
    [string]$Code, [string]$Device, [string]$Command, [string]$Path,
    [string]$LocalFile, [string]$JobId, [string]$Cwd,
    [int]$TimeoutSeconds = 120, [switch]$Overwrite,
    [string]$TokenFile = "$PSScriptRoot/../dist/Server/data/operator-token.txt"
)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$Server
if ($uri.Scheme -ne 'https' -and !($uri.Scheme -eq 'http' -and $uri.IsLoopback)) { throw 'Use HTTPS outside localhost.' }
$token = if ($env:BRIDGE_TOKEN) { $env:BRIDGE_TOKEN } else { (Get-Content -LiteralPath $TokenFile -Raw).Trim() }
$headers = @{ Authorization = "Bearer $token" }
function Call($method, $route, $body) {
    $args = @{ Method=$method; Uri="$($Server.TrimEnd('/'))$route"; Headers=$headers; UseBasicParsing=$true }
    if ($null -ne $body) { $args.ContentType='application/json; charset=utf-8'; $args.Body=[Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 10 -Compress)) }
    Invoke-RestMethod @args
}
switch ($Action) {
    'devices' { Call GET '/api/devices' $null; return }
    'pair' { if (!$Code) { throw '-Code required' }; Call POST '/api/pair' @{code=$Code}; return }
    'job' { Call GET "/api/jobs/$JobId" $null; return }
    'revoke' { if (!$Device) { throw '-Device required' }; Call DELETE "/api/devices/$Device" $null; return }
}
if (!$Device) { throw '-Device required' }
$work = @{op=$Action;timeoutSeconds=$TimeoutSeconds;overwrite=[bool]$Overwrite}
if ($Command) { $work.command=$Command }
if ($Cwd) { $work.cwd=$Cwd }
if ($Path) { $work.path=$Path }
if ($Action -eq 'write') {
    if (!$LocalFile) { throw '-LocalFile required for write' }
    $work.contentBase64=[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $LocalFile)))
}
$job = Call POST "/api/devices/$Device/jobs" $work
$deadline = (Get-Date).AddSeconds($TimeoutSeconds + 30)
do {
    Start-Sleep -Milliseconds 250
    $result = Call GET "/api/jobs/$($job.jobId)" $null
    if ((Get-Date) -gt $deadline) { throw "Polling timed out. Job ID: $($job.jobId). Check with -Action job -JobId." }
} while ($result.status -eq 'running')
if ($Action -eq 'read' -and $LocalFile -and $result.result.ok) {
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($LocalFile), [Convert]::FromBase64String($result.result.contentBase64))
    $result.result.PSObject.Properties.Remove('contentBase64')
}
$result | ConvertTo-Json -Depth 15
