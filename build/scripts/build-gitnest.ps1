param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Install the SDK with ./build/scripts/bootstrap-dotnet.ps1 first.' }
    $dotnet = $command.Source
}

Push-Location $repoRoot
try {
    & $dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'The SDK required by global.json is missing. Run ./build/scripts/bootstrap-dotnet.ps1.' }

    & git submodule update --init --recursive --depth=1
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the pinned submodules.' }

    $logDir = Join-Path $repoRoot 'artifacts/logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    & $dotnet build SourceGit.slnx -c Release --nologo "-flp:logfile=$logDir/gitnest-build.log;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw 'The GitNest Release build failed.' }

    if ($Publish) {
        $outputDir = Join-Path $repoRoot 'artifacts/windows-x64'
        & $dotnet publish src/SourceGit.csproj -c Release -r win-x64 --self-contained true -p:DisableAOT=true -o $outputDir --nologo "-flp:logfile=$logDir/gitnest-publish.log;verbosity=normal"
        if ($LASTEXITCODE -ne 0) { throw 'The GitNest Windows x64 publish failed.' }

        Copy-Item LICENSE,NOTICE.md,THIRD-PARTY-LICENSES.md -Destination $outputDir -Force
        $licenseDir = Join-Path $outputDir 'licenses'
        New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
        Copy-Item depends/AvaloniaEdit/LICENSE -Destination (Join-Path $licenseDir 'AvaloniaEdit-LICENSE.txt') -Force
        Write-Host "Published GitNest: $outputDir/GitNest.exe"
    }
}
finally {
    Pop-Location
}
