[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$appDir = Join-Path $dist 'SteadyDim'
$zipPath = Join-Path $dist ("SteadyDim-{0}.zip" -f $Runtime)

if (Test-Path -LiteralPath $appDir) {
    Remove-Item -LiteralPath $appDir -Recurse -Force
}

New-Item -ItemType Directory -Path $appDir -Force | Out-Null

dotnet publish (Join-Path $root 'SteadyDim.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:DebugType=None `
    -o $appDir `
    -nodeReuse:false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
