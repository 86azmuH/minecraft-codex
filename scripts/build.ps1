$ErrorActionPreference = 'Stop'

$projectSdk = Join-Path $env:LOCALAPPDATA 'MinecraftCodex\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $projectSdk) { $projectSdk } else { 'dotnet' }

$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'minecraft-codex-dotnet-home'
$env:APPDATA = Join-Path $env:TEMP 'minecraft-codex-appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

& $dotnet build (Join-Path $PSScriptRoot '..\MinecraftCodex.sln') --configuration Release --configfile (Join-Path $PSScriptRoot '..\NuGet.Config')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet run --no-build --project (Join-Path $PSScriptRoot '..\companion\tests\MinecraftCodex.Companion.Tests') --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $PSScriptRoot '..\companion\tools\MinecraftCodex.Companion.Client') --configuration Release --configfile (Join-Path $PSScriptRoot '..\NuGet.Config')
exit $LASTEXITCODE
