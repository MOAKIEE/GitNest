param([switch]$Comparison, [switch]$Config, [switch]$CommitSync, [switch]$OperationRecovery, [switch]$BranchDelete)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$batch = if ($BranchDelete) { 'phase-3.2' } elseif ($OperationRecovery) { 'phase-3.1' } elseif ($CommitSync) { 'phase-2.5' } elseif ($Config) { 'phase-2.4' } elseif ($Comparison) { 'phase-2.3' } else { 'phase-2.2' }
$probeArgs = @((Join-Path $repoRoot "artifacts/probes/$batch"))
if ($Comparison) { $probeArgs += '--comparison' }
if ($Config) { $probeArgs += '--config' }
if ($CommitSync) { $probeArgs += '--commit-sync' }
if ($OperationRecovery) { $probeArgs += '--operation-recovery' }
if ($BranchDelete) { $probeArgs += '--branch-delete' }
Push-Location $repoRoot
try {
    & $dotnet run --project tests/GitNest.UIProbe/GitNest.UIProbe.csproj -c Release --no-launch-profile -- @probeArgs
    if ($LASTEXITCODE -ne 0) { throw 'GitNest UI probe failed.' }
}
finally { Pop-Location }
