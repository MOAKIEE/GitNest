param([switch]$Comparison)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$batch = if ($Comparison) { 'phase-2.3' } else { 'phase-2.2' }
$probeArgs = @((Join-Path $repoRoot "artifacts/probes/$batch"))
if ($Comparison) { $probeArgs += '--comparison' }
Push-Location $repoRoot
try {
    & $dotnet run --project tests/GitNest.UIProbe/GitNest.UIProbe.csproj -c Release --no-launch-profile -- @probeArgs
    if ($LASTEXITCODE -ne 0) { throw 'GitNest UI probe failed.' }
}
finally { Pop-Location }
