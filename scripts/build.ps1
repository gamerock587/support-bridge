param([string]$Dotnet)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Dotnet) {
    $local = Join-Path $env:LOCALAPPDATA 'JedarBridge/dotnet/dotnet.exe'
    if (Test-Path $local) { $Dotnet = $local } else { $Dotnet = 'dotnet' }
}
foreach ($name in @('Server','Client')) {
    & $Dotnet publish "$root/src/Bridge.$name/Bridge.$name.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root/dist/$name"
    if ($LASTEXITCODE -ne 0) { throw "Failed publishing $name" }
}

Get-FileHash "$root/dist/Server/Bridge.Server.exe", "$root/dist/Client/Bridge.Client.exe" -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" } | Set-Content "$root/dist/SHA256SUMS.txt"
