$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
New-Item -ItemType Directory -Force .tools/metadata, .tools/downloads | Out-Null
foreach ($package in @('microsoft.data.sqlite','yamldotnet','jint','nsec.cryptography','microsoft.web.webview2')) {
    $data = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$package/index.json"
    $stable = @($data.versions | Where-Object { $_ -notmatch '-' })
    Write-Output "$package : $($stable[-1])"
    $data | ConvertTo-Json -Depth 6 | Set-Content ".tools/metadata/$package.json"
}
$pin = (Get-Content native/dependencies.json | ConvertFrom-Json).quickjs
Write-Output "QuickJS-NG $($pin.tag) $($pin.commit)"
Invoke-WebRequest "https://github.com/quickjs-ng/quickjs/archive/$($pin.commit).zip" -OutFile .tools/downloads/quickjs.zip
Expand-Archive .tools/downloads/quickjs.zip -DestinationPath .tools/quickjs-source -Force
