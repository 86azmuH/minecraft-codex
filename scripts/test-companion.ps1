param(
    [string]$Prompt = 'Do not use tools. Reply with exactly: COMPANION_BRIDGE_OK'
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'MinecraftCodex\dotnet'
$serverExecutable = (Resolve-Path (Join-Path $PSScriptRoot '..\companion\src\MinecraftCodex.Companion\bin\Release\net8.0\MinecraftCodex.Companion.exe')).Path
$clientExecutable = (Resolve-Path (Join-Path $PSScriptRoot '..\companion\tools\MinecraftCodex.Companion.Client\bin\Release\net8.0\MinecraftCodex.Companion.Client.exe')).Path
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$Prompt | & $clientExecutable --server $serverExecutable --working-directory $workspace
exit $LASTEXITCODE
