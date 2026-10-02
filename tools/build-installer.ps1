<#
F18.1: stage the published application, generate the staged-file manifest, optionally sign, and compile the NSIS per-user installer.

  ./tools/build-installer.ps1 -Version 0.1.0                       # stage + manifest + compile (needs makensis)
  ./tools/build-installer.ps1 -Version 0.1.0 -StageOnly            # stage + manifest only (no NSIS needed)

Inputs: the NativeAOT publish output (default artifacts/app; see docs/development/BUILD.md) and LICENSES/NOTICE.txt (made by
`node tools/generate-notices.mjs`, F17.2; pass -RegenerateNotices to run it first).

Signing is a hook, not a certificate. There is no real certificate in this repository. Pass -SignTool and -SignArgs (an argument array in which
{file} is replaced by each file to sign), or set SUSU_SIGN_TOOL and SUSU_SIGN_ARGS (arguments separated by '|'). Example with signtool and a
certificate in the user's store (the thumbprint is a <PLACEHOLDER>, never committed):
  -SignTool signtool.exe -SignArgs sign,/sha1,<THUMBPRINT>,/fd,SHA256,/tr,<TIMESTAMP_URL>,/td,SHA256,{file}
Without a signing hook the build is UNSIGNED: stage/UNSIGNED.txt exists, the manifest says "signed": false, the installer title says
"UNSIGNED BUILD" and the installer file name ends in -UNSIGNED.exe. -RequireSigned fails the build instead of producing an unsigned one.
makensis is never downloaded by this script: pass -MakeNsis, or have it on PATH, in Program Files (x86)\NSIS or in .tools\nsis.
#>
param(
  [string]$Version = '0.1.0',
  [string]$PublishDir = 'artifacts/app',
  [string]$StageDir = 'artifacts/installer/stage',
  [string]$OutputDir = 'artifacts/installer',
  [string]$LicensesDir = 'LICENSES',
  [string]$MakeNsis = '',
  [string]$SignTool = $env:SUSU_SIGN_TOOL,
  [string[]]$SignArgs = @(),
  [switch]$StageOnly,
  [switch]$RegenerateNotices,
  [switch]$RequireSigned
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Absolute([string]$path) { if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $root $path } }
Set-Location $root
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got '$Version'." }
if ($SignArgs.Count -eq 0 -and $env:SUSU_SIGN_ARGS) { $SignArgs = $env:SUSU_SIGN_ARGS -split '\|' }
$signing = [bool]$SignTool
if ($signing -and -not (@($SignArgs | Where-Object { $_ -like '*{file}*' }))) { throw 'SignArgs must contain the {file} placeholder.' }
if ($RequireSigned -and -not $signing) { throw '-RequireSigned was given but no signing hook (-SignTool/-SignArgs) is configured.' }

# Files an installed Su-Su cannot start without. A publish missing one of them is refused, not staged.
$required = @('susu.exe', 'susu_native.dll', 'susu_plugin_sandbox.dll', 'susu_quickjs.dll', 'e_sqlite3.dll', 'assets/susu.ico', 'ui/index.html')

if ($RegenerateNotices) {
  . (Join-Path $PSScriptRoot 'env.ps1')
  & node tools/generate-notices.mjs
  if ($LASTEXITCODE -ne 0) { throw 'generate-notices failed' }
}
foreach ($name in @('NOTICE.txt', 'third-party.json')) {
  if (-not (Test-Path (Join-Path $LicensesDir $name))) { throw "$LicensesDir/$name is missing; run node tools/generate-notices.mjs (F17.2)." }
}
foreach ($name in $required) {
  if (-not (Test-Path (Join-Path $PublishDir $name))) { throw "Publish output is incomplete: $PublishDir/$name is missing." }
}

# ---- stage ----
$stageFull = [IO.Path]::GetFullPath((Absolute $StageDir))
if (Test-Path $stageFull) { Remove-Item $stageFull -Recurse -Force }
New-Item -ItemType Directory -Force $stageFull | Out-Null
$publishFull = (Resolve-Path $PublishDir).Path
Get-ChildItem $publishFull -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
  $relative = $_.FullName.Substring($publishFull.Length).TrimStart('\', '/')
  $target = Join-Path $stageFull $relative
  New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
  Copy-Item $_.FullName $target
}
New-Item -ItemType Directory -Force (Join-Path $stageFull 'LICENSES') | Out-Null
Copy-Item (Join-Path $LicensesDir 'NOTICE.txt') (Join-Path $stageFull 'LICENSES/NOTICE.txt')
Copy-Item (Join-Path $LicensesDir 'third-party.json') (Join-Path $stageFull 'LICENSES/third-party.json')

# ---- sign (hook) ----
function Invoke-Sign([string]$file) {
  $arguments = $SignArgs | ForEach-Object { $_ -replace '\{file\}', $file }
  & $SignTool @arguments
  if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file (exit $LASTEXITCODE)." }
}
if ($signing) {
  # Only files Su-Su itself builds; third-party DLLs keep their own signatures.
  Get-ChildItem $stageFull -File | Where-Object { $_.Name -eq 'susu.exe' -or $_.Name -like 'susu_*.dll' } | ForEach-Object { Invoke-Sign $_.FullName }
} else {
  Set-Content -Path (Join-Path $stageFull 'UNSIGNED.txt') -Encoding UTF8 -Value @(
    'This Su-Su build is NOT code-signed. Windows SmartScreen may warn when it is run.',
    'It was produced without a signing certificate (tools/build-installer.ps1); do not distribute it as a release.')
}

# ---- manifest ----
$files = Get-ChildItem $stageFull -Recurse -File | Sort-Object FullName | ForEach-Object {
  [ordered]@{
    path   = $_.FullName.Substring($stageFull.Length).TrimStart('\', '/').Replace('\', '/')
    size   = $_.Length
    sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  }
}
[ordered]@{ product = 'Su-Su'; version = $Version; signed = $signing; files = @($files) } |
  ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $stageFull 'staged-manifest.json') -Encoding UTF8
Write-Host "Staged $(@($files).Count) files in $stageFull ($(if ($signing) { 'signed' } else { 'UNSIGNED' }))."
if ($StageOnly) { return }

# ---- compile ----
$candidates = @($MakeNsis, (Get-Command makensis -ErrorAction SilentlyContinue).Source,
  (Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'), (Join-Path $env:ProgramFiles 'NSIS/makensis.exe'),
  (Join-Path $root '.tools/nsis/makensis.exe')) | Where-Object { $_ -and (Test-Path $_) }
if (-not $candidates) {
  [Console]::Error.WriteLine('makensis was not found. Staging is complete. Install NSIS (https://nsis.sourceforge.io) yourself or pass -MakeNsis <path>; this script does not download it.')
  exit 3
}
$nsis = @($candidates)[0]
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$suffix = if ($signing) { '' } else { '-UNSIGNED' }
$installer = [IO.Path]::GetFullPath((Join-Path (Absolute $OutputDir) "Su-Su-Setup-$Version$suffix.exe"))
$defines = @("/DVERSION=$Version", "/DSTAGE=$stageFull", "/DOUTFILE=$installer")
if (-not $signing) { $defines += '/DUNSIGNED' }
& $nsis /V2 @defines (Join-Path $PSScriptRoot 'installer/susu.nsi')
if ($LASTEXITCODE -ne 0) { throw "makensis failed: $LASTEXITCODE" }
if ($signing) { Invoke-Sign $installer }
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$installer.sha256" -Value "$hash  $(Split-Path $installer -Leaf)" -Encoding ASCII
Write-Host "Installer: $installer`nSHA-256: $hash"
