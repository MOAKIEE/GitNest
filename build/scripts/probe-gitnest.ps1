param(
    [string[]]$Probe = @('overview'),
    [string]$OutputDir = '',
    [switch]$All,
    [switch]$Screenshots,
    [switch]$List,
    [switch]$NoBuild,
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 90
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

    $allProbes = @(& $runnerFile @($prefixArgs + @('--list-probes')))
    if ($LASTEXITCODE -ne 0) { throw 'Failed to list UI probes.' }
    $groups = @{
        'commit-sync' = @('commit', 'sync')
        'merge-revert' = @('merge', 'revert', 'conflict')
        'stash-discard' = @('stash-ignore', 'discard')
    }
    if ($All -and $PSBoundParameters.ContainsKey('Probe')) { throw 'Choose either -All or -Probe.' }
    $requested = if ($All) { @($allProbes | Where-Object { -not $groups.ContainsKey($_) }) } else { $Probe }
    $selected = [Collections.Generic.List[string]]::new()
    foreach ($raw in $requested) {
        foreach ($token in $raw.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
            $name = $token.Trim().ToLowerInvariant()
            if ($allProbes -notcontains $name) { throw "Unknown probe '$name'. Run with -List to see available probes." }
            $expanded = if ($groups.ContainsKey($name)) { $groups[$name] } else { @($name) }
            foreach ($entry in $expanded) {
                if (-not $selected.Contains($entry)) { $selected.Add($entry) }
            }
        }
    }
    if ($selected.Count -eq 0) { throw 'Select at least one UI probe.' }

    $failed = [Collections.Generic.List[string]]::new()
    $total = [Diagnostics.Stopwatch]::StartNew()
    foreach ($name in $selected) {
        $output = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
            Join-Path $repoRoot "artifacts/probes/$name"
        } elseif ($selected.Count -gt 1) {
            Join-Path $OutputDir $name
        } else {
            $OutputDir
        }
        $info = [Diagnostics.ProcessStartInfo]::new($runnerFile)
        $info.WorkingDirectory = $repoRoot
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = [Text.Encoding]::UTF8
        $info.StandardErrorEncoding = [Text.Encoding]::UTF8
        foreach ($arg in ($prefixArgs + @('--probe', $name, $output))) { $info.ArgumentList.Add($arg) }
        if ($Screenshots) { $info.ArgumentList.Add('--screenshots') }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $started = $false
        try {
            [void]$process.Start()
            $started = $true
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $exited = $process.WaitForExit($TimeoutSeconds * 1000)
            if (-not $exited) {
                $process.Kill($true)
                $process.WaitForExit()
            }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            if ($exited -and $process.ExitCode -eq 0) {
                $summary = if ($stdout -match 'Passed (\d+) probe checks') { "$($Matches[1]) checks" } else { 'completed' }
                Write-Host ("PASS [{0}] {1} in {2:N2}s" -f $name, $summary, $timer.Elapsed.TotalSeconds)
            } else {
                $failed.Add($name)
                Write-Host ("FAIL [{0}] in {1:N2}s" -f $name, $timer.Elapsed.TotalSeconds)
                if (-not $exited) { Write-Host "Timed out after $TimeoutSeconds seconds." }
                Write-Host $stdout
                Write-Host $stderr
            }
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    if ($failed.Count -gt 0) { throw "Failed UI probes: $($failed -join ', ')." }
    Write-Host ("Passed {0} UI probes in {1:N2}s." -f $selected.Count, $total.Elapsed.TotalSeconds)
}
finally {
    Pop-Location
}
