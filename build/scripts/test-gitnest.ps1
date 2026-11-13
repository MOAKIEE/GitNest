param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

Push-Location $repoRoot
try {
    & $dotnet run --project tests/GitNest.IntegrationTests/GitNest.IntegrationTests.csproj -c Release --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'GitNest integration checks failed.' }
}
finally {
    Pop-Location
}
