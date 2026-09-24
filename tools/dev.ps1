param(
  [ValidateSet('info','restore','build','test','publish')][string]$Action = 'build',
  [string]$OutputDirectory = 'artifacts/probes',
  [string]$Project = 'tools/Susu.Probes'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
switch ($Action) {
  info { & $dotnet --info }
  restore { & $dotnet restore Susu.slnx --locked-mode --configfile NuGet.Config }
  build { & $dotnet build Susu.slnx -c Release --no-restore }
  test { & $dotnet test --solution Susu.slnx -c Release --no-restore }
  publish { & $dotnet publish $Project -c Release -r win-x64 -o $OutputDirectory -p:RestoreConfigFile=NuGet.Config }
}
if ($LASTEXITCODE -ne 0) { throw "dotnet $Action failed: $LASTEXITCODE" }
