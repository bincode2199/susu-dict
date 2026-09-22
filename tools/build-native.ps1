param([string]$OutputDirectory = 'artifacts/probes')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$deps = Get-Content native/dependencies.json | ConvertFrom-Json
$source = Join-Path $root ".tools/quickjs-source/quickjs-$($deps.quickjs.commit)"
if (-not (Test-Path "$source/quickjs.h")) { throw 'Fetch the pinned QuickJS source before building.' }
$cmake = 'C:/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (-not (Test-Path $cmake)) { $cmake = 'cmake' }
& $cmake -S native/quickjs-bridge -B artifacts/native -G 'Visual Studio 17 2022' -A x64 "-DQUICKJS_SOURCE=$source"
if ($LASTEXITCODE) { throw 'CMake configure failed' }
& $cmake --build artifacts/native --config Release --target susu_quickjs
if ($LASTEXITCODE) { throw 'Native build failed' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
Copy-Item artifacts/native/Release/susu_quickjs.dll $OutputDirectory
$webview = Join-Path $root '.tools/nuget/microsoft.web.webview2/1.0.4191.47/build/native'
& $cmake -S src/Susu.Windows/native -B artifacts/windows-native -G 'Visual Studio 17 2022' -A x64 "-DWEBVIEW_SDK=$webview"
if ($LASTEXITCODE) { throw 'Windows native configure failed' }
& $cmake --build artifacts/windows-native --config Release
if ($LASTEXITCODE) { throw 'Windows native build failed' }
Copy-Item artifacts/windows-native/Release/susu_windows_probe.dll $OutputDirectory
