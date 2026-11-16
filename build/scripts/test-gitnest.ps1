param(
    [string[]]$Suite = @(),
    [switch]$Changed,
    [string]$BaseRef = '',
    [int]$Jobs = 0,
    [switch]$List,
    [switch]$NoBuild,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

function Get-RunnerCommand {
    param([string]$Root)
    $binDir = Join-Path $Root 'tests/GitNest.IntegrationTests/bin/Release/net10.0'
    $exeName = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'GitNest.IntegrationTests.exe' } else { 'GitNest.IntegrationTests' }
    $exePath = Join-Path $binDir $exeName
    if (Test-Path $exePath) {
        return @{ File = $exePath; PrefixArgs = @() }
    }
    $dllPath = Join-Path $binDir 'GitNest.IntegrationTests.dll'
    if (Test-Path $dllPath) {
        return @{ File = $dotnet; PrefixArgs = @($dllPath) }
    }
    return $null
}

function Resolve-SuitesForChangedPaths {
    param([string[]]$Paths)

    $matched = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $nonDocChange = $false
    $unmappedSource = [Collections.Generic.List[string]]::new()

    foreach ($rawPath in $Paths) {
        $p = ($rawPath -replace '\\', '/').Trim()
        if ([string]::IsNullOrWhiteSpace($p)) { continue }

        # Pure docs, markdown, or installer/packaging resources do not require integration suite execution.
        if ($p -match '^(docs/|build/installer/|build/resources/|artifacts/|\.issuetracker$|AGENTS\.md$|LICENSE$|NOTICE\.md$|THIRD-PARTY-LICENSES\.md$|VERSION$|\.editorconfig$|\.gitattributes$|\.gitignore$)' -or
            $p -match '\.md$') {
            if ($p -eq 'VERSION') {
                $nonDocChange = $true
                [void]$matched.Add('update')
            }
            continue
        }

        $nonDocChange = $true
        $hit = $false

        # Direct test suite files
        switch -Regex ($p) {
            '^tests/GitNest\.IntegrationTests/CoreTests\.cs$'           { [void]$matched.Add('core'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/UpdateTests\.cs$'         { [void]$matched.Add('update'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/WorktreeTests\.cs$'       { [void]$matched.Add('worktree'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/ComparisonTests\.cs$'     { [void]$matched.Add('comparison'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/HistoryDiffTests\.cs$'    { [void]$matched.Add('history-diff'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/ConfigTests\.cs$'         { [void]$matched.Add('config'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/OperationSafetyTests\.cs$' { [void]$matched.Add('operation-safety'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/CommitTests\.cs$'         { [void]$matched.Add('commit'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/SyncTests\.cs$'           { [void]$matched.Add('sync'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/AmendTests\.cs$'          { [void]$matched.Add('amend'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/BranchDeleteTests\.cs$'   { [void]$matched.Add('branch-delete'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/WorktreeDeleteTests\.cs$' { [void]$matched.Add('worktree-delete'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/MergeTests\.cs$'          { [void]$matched.Add('merge'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/RevertTests\.cs$'         { [void]$matched.Add('revert'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/DiscardTests\.cs$'        { [void]$matched.Add('discard'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/StashIgnoreTests\.cs$'    { [void]$matched.Add('stash-ignore'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/SubmoduleLfsTests\.cs$'   { [void]$matched.Add('submodule-lfs'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/HistoryResetTests\.cs$'   { [void]$matched.Add('history-reset'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/RemoteRewriteTests\.cs$'  { [void]$matched.Add('remote-rewrite'); $hit = $true }
            '^tests/GitNest\.IntegrationTests/(Program\.cs|GitNest\.IntegrationTests\.csproj)$' {
                [void]$matched.Add('core'); $hit = $true
            }
            '^tests/GitNest\.IntegrationTests/' { $hit = $true }
            '^tests/GitNest\.UIProbe/'          { $hit = $true } # UIProbe is tested via probe-gitnest.ps1
            '^build/scripts/probe-gitnest\.ps1$' { $hit = $true }
            '^build/scripts/test-gitnest\.ps1$'  { [void]$matched.Add('core'); $hit = $true }
        }
        if ($hit) { continue }

        # Source file to feature suite mapping
        switch -Regex ($p) {
            '^src/(Models/(UpdateChecker|ReleaseVersion|SelfUpdate)|ViewModels/SelfUpdate|Views/SelfUpdate\.)' {
                [void]$matched.Add('update'); $hit = $true
            }
            '^src/(Models/TaskComparison|Commands/QueryTaskComparison|ViewModels/(TaskComparison|Repository\.TaskComparison)|Views/TaskComparison\.)' {
                [void]$matched.Add('comparison'); $hit = $true
            }
            '^src/(Models/(Worktree|WorktreeStatus|WorktreeTaskStore|WorktreeToolLauncher)|Commands/(QueryWorktreeStatus|Worktree)|ViewModels/(Worktree|EditWorktreeTask|Repository\.TaskOverview)|Views/(EditWorktreeTask\.|Worktree))' {
                [void]$matched.Add('worktree'); $hit = $true
            }
            '^src/(Commands/(QueryCommits|CompareRevisions|Diff|IsBinary)|ViewModels/(Histories|CommitDetail|RevisionCompare|DiffContext|TextDiffContext))' {
                [void]$matched.Add('history-diff'); $hit = $true
            }
            '^src/(Commands/(Config|IssueTracker)|Models/(GitConfig|RepositorySettings|IssueTracker)|ViewModels/(RepositoryConfigure|ConfigureModels)|Views/(RepositoryConfigure\.|ConfigFieldMetaBar\.))' {
                [void]$matched.Add('config'); $hit = $true
            }
            '^src/(Commands/(Remote|Branch)|ViewModels/(AddRemote|EditRemote|SetPushURL|SetUpstream))' {
                [void]$matched.Add('config'); [void]$matched.Add('sync'); $hit = $true
            }
            '^src/(Commands/(CaptureOperationSnapshot|OperationJournalService|BackupRefs|QueryAssumeUnchangedFiles|AssumeUnchanged)|Models/(OperationSafetyModels|OperationJournalStore|RepositoryWriteGuard|Watcher)|ViewModels/(AssumeUnchangedManager|ViewLogs)|Views/(AssumeUnchangedManager\.|ViewLogs\.))' {
                [void]$matched.Add('operation-safety'); $hit = $true
            }
            '^src/(Commands/(QueryStagedSnapshot|Add|Commit)|Models/(StagedSnapshot|CommitIdentity|WorkingCopySafetyModels|RepositoryUIStates)|ViewModels/WorkingCopy\.cs$|Views/(WorkingCopy\.|CommitMessageToolBox\.))' {
                [void]$matched.Add('commit'); $hit = $true
            }
            '^src/ViewModels/WorkingCopy\.CommitSync\.cs$' {
                [void]$matched.Add('commit'); [void]$matched.Add('sync'); $hit = $true
            }
            '^src/(Commands/(GitSyncService\.cs$|RemotePushTarget|Fetch|Pull|Push)|Models/CommitSyncModels|ViewModels/(Fetch|Pull|Push|PushRevision)|Views/(Fetch\.|Pull\.|Push\.|PushRevision\.))' {
                [void]$matched.Add('sync'); $hit = $true
            }
            '^src/(Commands/GitSyncService\.Amend|Models/AmendModels|ViewModels/AmendLastCommit|Views/AmendLastCommit\.)' {
                [void]$matched.Add('amend'); $hit = $true
            }
            '^src/(Commands/BranchDeletionService|Models/BranchDeletionModels|ViewModels/(DeleteBranch|DeleteMultipleBranches)|Views/(DeleteBranch\.|DeleteMultipleBranches\.))' {
                [void]$matched.Add('branch-delete'); $hit = $true
            }
            '^src/(Commands/WorktreeDeletionService|Models/WorktreeDeletionModels|ViewModels/(RemoveWorktree|PruneWorktrees)|Views/(RemoveWorktree\.|PruneWorktrees\.))' {
                [void]$matched.Add('worktree-delete'); $hit = $true
            }
            '^src/(Commands/(Merge|MergeBase)|ViewModels/Merge|Views/Merge\.)' {
                [void]$matched.Add('merge'); $hit = $true
            }
            '^src/(Commands/Revert|ViewModels/Revert|Views/Revert\.)' {
                [void]$matched.Add('revert'); $hit = $true
            }
            '^src/(Commands/MergeRevertService|Models/MergeRevertModels|ViewModels/Conflict|Views/Conflict\.)' {
                [void]$matched.Add('merge'); [void]$matched.Add('revert'); $hit = $true
            }
            '^src/(Commands/(DiscardBackupService|Discard)|Models/DiscardBackupStore|ViewModels/(Discard|ViewLogs\.DiscardBackups)|Views/Discard\.)' {
                [void]$matched.Add('discard'); $hit = $true
            }
            '^src/(Commands/(StashWorkflowService|Stash|IgnoreRuleService)|Models/(Stash|GitIgnoreFile)|ViewModels/(StashChanges|ApplyStash|DropStash|AddToIgnore)|Views/(Stash|ApplyStash\.|DropStash\.|AddToIgnore\.))' {
                [void]$matched.Add('stash-ignore'); $hit = $true
            }
            '^src/(Commands/(LFS|IsLFSFiltered|Submodule|QuerySubmodules|QuerySubmoduleRevision|QueryUpdatableSubmodules|QueryTags|Checkout)|Models/LFSObject|ViewModels/(Checkout|LFS)|Views/(Checkout\.|LFS))' {
                [void]$matched.Add('submodule-lfs'); $hit = $true
            }
            '^src/(Commands/Reset|ViewModels/(Reset|ResetWithoutCheckout)|Views/(Reset\.|ResetWithoutCheckout\.))' {
                [void]$matched.Add('history-reset'); $hit = $true
            }
            '^src/Commands/BranchProtectionPolicy\.cs$' {
                [void]$matched.Add('branch-delete'); [void]$matched.Add('remote-rewrite'); [void]$matched.Add('amend'); $hit = $true
            }
            '^src/(ViewModels/RewriteRemoteHistory|Views/RewriteRemoteHistory\.)' {
                [void]$matched.Add('remote-rewrite'); $hit = $true
            }
            '^src/(Commands/HistoryResetService|Models/HistoryResetModels)' {
                [void]$matched.Add('history-reset'); [void]$matched.Add('remote-rewrite'); $hit = $true
            }
            '^src/(Models/(GitInstallation|GitVersions|Locales|AvatarManager)|Resources/|Commands/(Statistics|Archive)|ViewModels/(ImageSource|Preferences|Statistics)|Views/(Statistics\.|Preferences\.)|App\.JsonCodeGen\.cs$)' {
                [void]$matched.Add('core'); $hit = $true
            }
            '^src/(Commands/(Command|CommandArgumentHelper)|Native/|SourceGit\.csproj$)|^global\.json$' {
                [void]$matched.Add('core')
                [void]$matched.Add('operation-safety')
                [void]$matched.Add('commit')
                [void]$matched.Add('sync')
                $hit = $true
            }
            default {
                $unmappedSource.Add($p)
            }
        }
    }

    if ($nonDocChange -and $matched.Count -eq 0 -and $unmappedSource.Count -gt 0) {
        [void]$matched.Add('core')
    }

    return @{
        Suites = @($matched)
        HasNonDocChange = $nonDocChange
        Unmapped = @($unmappedSource)
    }
}

Push-Location $repoRoot
try {
    if (-not $NoBuild) {
        & $dotnet build tests/GitNest.IntegrationTests/GitNest.IntegrationTests.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build GitNest.IntegrationTests.' }
    }

    $runner = Get-RunnerCommand -Root $repoRoot
    if (-not $runner) {
        if ($NoBuild) { throw 'GitNest.IntegrationTests build output not found. Re-run without -NoBuild.' }
        throw 'GitNest.IntegrationTests executable was not produced.'
    }

    if ($List) {
        $listArgs = $runner.PrefixArgs + @('--list-suites-detailed')
        $lines = & $runner.File @listArgs
        if ($LASTEXITCODE -ne 0) { throw 'Failed to list test suites.' }
        foreach ($line in $lines) {
            $parts = $line -split "`t", 2
            if ($parts.Length -eq 2) {
                Write-Host ("{0,-18} {1}" -f $parts[0], $parts[1])
            } else {
                Write-Host $line
            }
        }
        return
    }

    $allSuitesArgs = $runner.PrefixArgs + @('--list-suites')
    $allSuites = @(& $runner.File @allSuitesArgs)
    if ($LASTEXITCODE -ne 0 -or $allSuites.Count -eq 0) { throw 'Unable to query built-in suites.' }

    $targetSuites = [Collections.Generic.List[string]]::new()
    if ($Suite.Count -gt 0) {
        foreach ($raw in $Suite) {
            foreach ($token in ($raw -split ',')) {
                $trimmed = $token.Trim()
                if ([string]::IsNullOrEmpty($trimmed)) { continue }
                $canonical = $allSuites | Where-Object { $_ -ieq $trimmed } | Select-Object -First 1
                if (-not $canonical) {
                    throw "Unknown suite '$trimmed'. Run with -List to see available suites."
                }
                if (-not $targetSuites.Contains($canonical)) {
                    $targetSuites.Add($canonical)
                }
            }
        }
    }
    elseif ($Changed) {
        $changedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $statusLines = & git status --porcelain=v1 -uall
        foreach ($line in $statusLines) {
            if ($line.Length -lt 4) { continue }
            $entry = $line.Substring(3).Trim().Trim('"')
            if ($entry -match ' -> (.+)$') {
                [void]$changedFiles.Add($Matches[1].Trim('"'))
            } else {
                [void]$changedFiles.Add($entry)
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($BaseRef)) {
            $diffLines = & git diff --name-only "$BaseRef...HEAD"
            foreach ($dl in $diffLines) {
                if (-not [string]::IsNullOrWhiteSpace($dl)) {
                    [void]$changedFiles.Add($dl.Trim())
                }
            }
        }

        if ($changedFiles.Count -eq 0) {
            Write-Host 'No uncommitted or diffed files detected. Nothing to run.'
            return
        }

        $resolution = Resolve-SuitesForChangedPaths -Paths @($changedFiles)
        if (-not $resolution.HasNonDocChange) {
            Write-Host 'Only documentation or non-code files changed. Skipping integration tests.'
            return
        }

        foreach ($s in $allSuites) {
            if ($resolution.Suites -contains $s) {
                $targetSuites.Add($s)
            }
        }

        Write-Host ("Changed files ({0}): {1}" -f $changedFiles.Count, ((@($changedFiles) | Select-Object -First 8) -join ', '))
        if ($resolution.Unmapped.Count -gt 0) {
            Write-Host ("Note: unmapped source file(s) fell back to core: {0}" -f ($resolution.Unmapped -join ', '))
        }
        Write-Host ("Matched suites ({0}): {1}" -f $targetSuites.Count, ($targetSuites -join ', '))
        if ($targetSuites.Count -eq 0) {
            Write-Host 'No integration suites impacted by these changes. Skipping.'
            return
        }
        if ($DryRun) { return }
    }
    else {
        foreach ($s in $allSuites) { $targetSuites.Add($s) }
    }

    if ($DryRun) {
        Write-Host ("Selected suites ({0}): {1}" -f $targetSuites.Count, ($targetSuites -join ', '))
        return
    }

    # Single suite runs directly with live output
    if ($targetSuites.Count -eq 1) {
        $singleArgs = $runner.PrefixArgs + @('--suite', $targetSuites[0])
        $swSingle = [Diagnostics.Stopwatch]::StartNew()
        & $runner.File @singleArgs
        $swSingle.Stop()
        if ($LASTEXITCODE -ne 0) { throw "Suite '$($targetSuites[0])' failed." }
        Write-Host ("Completed [$($targetSuites[0])] in {0:N2}s." -f $swSingle.Elapsed.TotalSeconds)
        return
    }

    # Multiple suites run across isolated worker processes
    $workerLimit = if ($Jobs -gt 0) {
        $Jobs
    } else {
        [Math]::Min(6, [Math]::Max(2, [int]([Environment]::ProcessorCount / 2)))
    }
    $workerLimit = [Math]::Min($workerLimit, $targetSuites.Count)

    $totalStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $logDir = Join-Path ([IO.Path]::GetTempPath()) ("GitNest-runner-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    try {
        $queue = [Collections.Generic.Queue[string]]::new()
        foreach ($s in $targetSuites) { $queue.Enqueue($s) }

        $active = [Collections.Generic.List[hashtable]]::new()
        $completed = [Collections.Generic.List[hashtable]]::new()

        while ($queue.Count -gt 0 -or $active.Count -gt 0) {
            while ($active.Count -lt $workerLimit -and $queue.Count -gt 0) {
                $suiteName = $queue.Dequeue()
                $outFile = Join-Path $logDir "$suiteName.out.txt"
                $errFile = Join-Path $logDir "$suiteName.err.txt"
                $psi = [Diagnostics.ProcessStartInfo]::new($runner.File)
                $psi.WorkingDirectory = $repoRoot
                $psi.UseShellExecute = $false
                $psi.CreateNoWindow = $true
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                foreach ($p in $runner.PrefixArgs) { [void]$psi.ArgumentList.Add($p) }
                [void]$psi.ArgumentList.Add('--suite')
                [void]$psi.ArgumentList.Add($suiteName)

                $proc = [Diagnostics.Process]::new()
                $proc.StartInfo = $psi
                [void]$proc.Start()
                $stdOutTask = $proc.StandardOutput.ReadToEndAsync()
                $stdErrTask = $proc.StandardError.ReadToEndAsync()

                $active.Add(@{
                    Name = $suiteName
                    Process = $proc
                    StdOutTask = $stdOutTask
                    StdErrTask = $stdErrTask
                    OutFile = $outFile
                    ErrFile = $errFile
                    Stopwatch = [Diagnostics.Stopwatch]::StartNew()
                })
            }

            $finishedThisTick = $false
            for ($idx = $active.Count - 1; $idx -ge 0; $idx--) {
                $item = $active[$idx]
                if ($item.Process.HasExited) {
                    $finishedThisTick = $true
                    $item.Process.WaitForExit()
                    $item.Stopwatch.Stop()
                    $stdOut = $item.StdOutTask.GetAwaiter().GetResult()
                    $stdErr = $item.StdErrTask.GetAwaiter().GetResult()
                    $exitCode = $item.Process.ExitCode
                    $item.Process.Dispose()

                    $checkCount = 0
                    if ($stdOut -match 'Passed (\d+) checks\.') {
                        $checkCount = [int]$Matches[1]
                    }

                    $secs = [Math]::Round($item.Stopwatch.Elapsed.TotalSeconds, 2)
                    if ($exitCode -eq 0) {
                        Write-Host ("PASS [{0}] {1} checks in {2:N2}s" -f $item.Name, $checkCount, $secs)
                    } else {
                        Write-Host ("FAIL [{0}] exit={1} in {2:N2}s" -f $item.Name, $exitCode, $secs)
                    }

                    $completed.Add(@{
                        Name = $item.Name
                        ExitCode = $exitCode
                        Checks = $checkCount
                        Seconds = $secs
                        StdOut = $stdOut
                        StdErr = $stdErr
                    })
                    $active.RemoveAt($idx)
                }
            }

            if (-not $finishedThisTick -and $active.Count -gt 0) {
                Start-Sleep -Milliseconds 100
            }
        }

        $totalStopwatch.Stop()
        $failures = @($completed | Where-Object { $_.ExitCode -ne 0 })
        $totalChecks = ($completed | Measure-Object -Property Checks -Sum).Sum

        if ($failures.Count -gt 0) {
            foreach ($fail in $failures) {
                Write-Host "`n--- FAILED SUITE: $($fail.Name) ---"
                if (-not [string]::IsNullOrWhiteSpace($fail.StdOut)) { Write-Host $fail.StdOut }
                if (-not [string]::IsNullOrWhiteSpace($fail.StdErr)) { Write-Host $fail.StdErr }
            }
            throw "$($failures.Count) integration test suite(s) failed."
        }

        Write-Host ("Passed {0} checks across {1} suites in {2:N2}s (workers={3})." -f $totalChecks, $completed.Count, $totalStopwatch.Elapsed.TotalSeconds, $workerLimit)
    }
    finally {
        if (Test-Path $logDir) {
            Remove-Item -Path $logDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    Pop-Location
}
