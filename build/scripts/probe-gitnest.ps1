param(
    [ValidateSet(
        'overview',
        'comparison',
        'config',
        'commit-sync',
        'amend',
        'operation-recovery',
        'branch-delete',
        'worktree-delete',
        'merge-revert',
        'stash-discard',
        'history-reset',
        'submodule-lfs',
        'surface',
        'high-dpi'
    )]
    [string]$Probe = 'overview',
    [string]$OutputDir = '',
    [switch]$List,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }

Push-Location $repoRoot
try {
    if (-not $NoBuild) {
        & $dotnet build tests/GitNest.UIProbe/GitNest.UIProbe.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build GitNest.UIProbe.' }
    }

    $binDir = Join-Path $repoRoot 'tests/GitNest.UIProbe/bin/Release/net10.0'
    $exeName = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'GitNest.UIProbe.exe' } else { 'GitNest.UIProbe' }
    $exePath = Join-Path $binDir $exeName
    $dllPath = Join-Path $binDir 'GitNest.UIProbe.dll'
    if (Test-Path $exePath) {
        $runnerFile = $exePath
        $prefixArgs = @()
    }
    elseif (Test-Path $dllPath) {
        $runnerFile = $dotnet
        $prefixArgs = @($dllPath)
    }
    else {
        throw 'GitNest.UIProbe build output not found.'
    }

    if ($List) {
        $lines = & $runnerFile @($prefixArgs + @('--list-probes-detailed'))
        if ($LASTEXITCODE -ne 0) { throw 'Failed to list UI probes.' }
        foreach ($line in $lines) {
            $parts = $line -split "`t", 2
            if ($parts.Length -eq 2) {
                Write-Host ("{0,-20} {1}" -f $parts[0], $parts[1])
            } else {
                Write-Host $line
            }
        }
        return
    }

    $resolvedOutput = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        Join-Path $repoRoot "artifacts/probes/$Probe"
    } else {
        $OutputDir
    }

    $probeArgs = $prefixArgs + @('--probe', $Probe, $resolvedOutput)
    & $runnerFile @probeArgs
    if ($LASTEXITCODE -ne 0) { throw "GitNest UI probe '$Probe' failed." }
}
finally {
    Pop-Location
}
