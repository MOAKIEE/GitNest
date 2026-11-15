param(
    [string[]]$Suite = @(),
    [switch]$List,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

Push-Location $repoRoot
try {
    $runArgs = @(
        'run',
        '--project', 'tests/GitNest.IntegrationTests/GitNest.IntegrationTests.csproj',
        '-c', 'Release',
        '--no-launch-profile'
    )
    if ($NoBuild) {
        $runArgs += '--no-build'
    }
    if ($List) {
        $runArgs += '--'
        $runArgs += '--list-suites'
    }
    elseif ($Suite.Count -gt 0) {
        $runArgs += '--'
        $runArgs += '--suite'
        $runArgs += ($Suite -join ',')
    }

    & $dotnet @runArgs
    if ($LASTEXITCODE -ne 0) { throw 'GitNest integration checks failed.' }
}
finally {
    Pop-Location
}
