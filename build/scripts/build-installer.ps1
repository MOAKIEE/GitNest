param(
    [switch]$SkipPublish,
    [string]$AppVersion,
    [string]$NumericVersion,
    [string]$OutputBaseFilename
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'build-gitnest.ps1') -Publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish before packaging installer failed.' }
}

if ([string]::IsNullOrWhiteSpace($AppVersion)) {
    $versionFile = Join-Path $repoRoot 'VERSION'
    if (-not (Test-Path $versionFile)) { throw 'VERSION file not found.' }
    $AppVersion = (Get-Content $versionFile -Raw).Trim()
}

if ([string]::IsNullOrWhiteSpace($NumericVersion)) {
    if ($AppVersion -match '^(\d+\.\d+\.\d+)(?:\.(\d+))?') {
        $patch4 = if ($Matches[2]) { $Matches[2] } else { '0' }
        $NumericVersion = "$($Matches[1]).$patch4"
    }
    else {
        $NumericVersion = '0.1.0.0'
    }
}

$sourceDir = Join-Path $repoRoot 'artifacts/windows-x64'
$gitNestExe = Join-Path $sourceDir 'GitNest.exe'
if (-not (Test-Path $gitNestExe)) {
    throw "Published binary not found: $gitNestExe"
}

foreach ($required in @('LICENSE', 'NOTICE.md', 'THIRD-PARTY-LICENSES.md', 'licenses\AvaloniaEdit-LICENSE.txt')) {
    $p = Join-Path $sourceDir $required
    if (-not (Test-Path $p)) {
        throw "Required license/notice file missing from publish directory: $p"
    }
}

$bundledGit = Get-ChildItem -Path $sourceDir -Filter 'git.exe' -Recurse -ErrorAction SilentlyContinue
if ($bundledGit) {
    throw 'Release policy violation: git.exe must not be bundled in the installer payload.'
}

$isccCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 (ISCC.exe) was not found.'
}

$outputDir = Join-Path $repoRoot 'artifacts/installer'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($OutputBaseFilename)) {
    $OutputBaseFilename = "GitNest-Setup-$AppVersion-win-x64"
}

$issPath = Join-Path $repoRoot 'build/installer/gitnest.iss'
$isccArgs = @(
    "/DAppVersion=$AppVersion",
    "/DNumericVersion=$NumericVersion",
    "/DSourceDir=$sourceDir",
    "/DOutputDir=$outputDir",
    "/DOutputBaseFilename=$OutputBaseFilename",
    $issPath
)

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw 'ISCC failed to compile GitNest Windows x64 installer.'
}

$installerPath = Join-Path $outputDir "$OutputBaseFilename.exe"
if (-not (Test-Path $installerPath)) {
    throw "Expected installer not found: $installerPath"
}

$hash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash
$sizeMB = [Math]::Round((Get-Item $installerPath).Length / 1MB, 2)
Write-Host "Built installer: $installerPath ($sizeMB MB, SHA256=$hash)"
