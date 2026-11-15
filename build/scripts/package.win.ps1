param(
    [string]$SourceDir = 'artifacts/windows-x64',
    [string]$Version = $env:VERSION,
    [string]$Runtime = $env:RUNTIME,
    [string]$OutputDir = 'artifacts/packages'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $Runtime = 'win-x64'
}

$resolvedSource = if ([IO.Path]::IsPathRooted($SourceDir)) { $SourceDir } else { Join-Path $repoRoot $SourceDir }
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }

if (-not (Test-Path $resolvedSource)) {
    throw "Source directory not found: $resolvedSource"
}

Get-ChildItem -Path $resolvedSource -Include '*.pdb', 'createdump.exe' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

Copy-Item (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'NOTICE.md'), (Join-Path $repoRoot 'THIRD-PARTY-LICENSES.md') -Destination $resolvedSource -Force
$licenseDir = Join-Path $resolvedSource 'licenses'
New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
Copy-Item (Join-Path $repoRoot 'depends/AvaloniaEdit/LICENSE') -Destination (Join-Path $licenseDir 'AvaloniaEdit-LICENSE.txt') -Force

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$zipPath = Join-Path $resolvedOutput "GitNest-$Version-$Runtime.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $resolvedSource '*') -DestinationPath $zipPath -Force
Write-Host "Packaged Windows archive: $zipPath"
