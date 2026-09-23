# Dot-source to put the repository-local toolchain first on PATH for this process only.
# Machine and user environment settings are not changed.
$root = Split-Path $PSScriptRoot -Parent
$paths = @()
foreach ($dir in @('.tools/dotnet', '.tools/node', '.tools/pnpm')) {
    $full = Join-Path $root $dir
    if (Test-Path $full) { $paths += $full }
}
# vcvarsall (used by the NativeAOT linker probe and CMake) prints a
# "'vswhere.exe' is not recognized" line into the detected linker path unless
# the VS Installer directory is on PATH.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
if (Test-Path $installer) { $paths += $installer }
$env:PATH = (($paths + $env:PATH) -join ';')
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
