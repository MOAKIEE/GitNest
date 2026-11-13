param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sdkVersion = (Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$toolDir = Join-Path $repoRoot '.tools'
$installDir = Join-Path $toolDir 'dotnet'
$dotnet = Join-Path $installDir 'dotnet.exe'

if (Test-Path (Join-Path $installDir "sdk/$sdkVersion")) {
    Write-Host "SDK $sdkVersion is already installed at $installDir"
    return
}

New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
$channel = ($sdkVersion.Split('.')[0..1] -join '.')
$metadata = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"
$sdks = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $sdkVersion })
$asset = @($sdks | ForEach-Object { $_.files } | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') }) | Select-Object -First 1
if (-not $asset) { throw "The official release metadata has no Windows x64 SDK $sdkVersion." }

$archivePath = Join-Path $toolDir "dotnet-sdk-$sdkVersion.zip"
& curl.exe --fail --location --retry 3 --silent --show-error --output $archivePath $asset.url
if ($LASTEXITCODE -ne 0) { throw 'The SDK download failed.' }
if ((Get-FileHash $archivePath -Algorithm SHA512).Hash -ne $asset.hash) {
    throw 'The downloaded SDK does not match its official SHA-512 hash.'
}

Expand-Archive -LiteralPath $archivePath -DestinationPath $installDir -Force
& $dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'The installed SDK could not start.' }
Write-Host "Installed SDK $sdkVersion at $installDir"
