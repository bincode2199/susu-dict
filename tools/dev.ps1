param([ValidateSet('info','restore','build','publish')][string]$Action = 'build')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
switch ($Action) {
  info { & $dotnet --info }
  restore { & $dotnet restore Susu.slnx --locked-mode --configfile NuGet.Config }
  build { & $dotnet build Susu.slnx -c Release --no-restore }
  publish { & $dotnet publish tools/Susu.Probes -c Release -r win-x64 -o artifacts/probes -p:RestoreConfigFile=NuGet.Config }
}
if ($LASTEXITCODE -ne 0) { throw "dotnet $Action failed: $LASTEXITCODE" }
