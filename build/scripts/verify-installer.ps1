param(
    [switch]$RebuildInstaller,
    [switch]$KeepTestArtifacts
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$versionFile = Join-Path $repoRoot 'VERSION'
if (-not (Test-Path $versionFile)) { throw 'VERSION file not found.' }
$baseVersion = (Get-Content $versionFile -Raw).Trim()

Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PeManifestReader
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hLibModule);

    public static string Read(string exePath)
    {
        const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        IntPtr hModule = LoadLibraryExW(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (hModule == IntPtr.Zero) return string.Empty;
        try
        {
            IntPtr hRes = FindResourceW(hModule, (IntPtr)1, (IntPtr)24);
            if (hRes == IntPtr.Zero) hRes = FindResourceW(hModule, (IntPtr)2, (IntPtr)24);
            if (hRes == IntPtr.Zero) return string.Empty;
            uint size = SizeofResource(hModule, hRes);
            IntPtr hLoaded = LoadResource(hModule, hRes);
            IntPtr ptr = LockResource(hLoaded);
            if (ptr == IntPtr.Zero || size == 0) return string.Empty;
            byte[] data = new byte[size];
            Marshal.Copy(ptr, data, 0, (int)size);
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            FreeLibrary(hModule);
        }
    }
}
'@

function Send-ToRecycleBin {
    param([string]$TargetPath, [switch]$IsDirectory)
    if (-not (Test-Path $TargetPath)) { return }
    if ($IsDirectory) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
            $TargetPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    }
    else {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
            $TargetPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    }
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
    Write-Host "PASS $Message"
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\GitNest'
$appDataDir = Join-Path $env:APPDATA 'GitNest'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\GitNest'
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7E9C4B2-4F81-4D26-9C1E-8B5F0D4A2E71}_is1'
$userDesktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'GitNest.lnk'

Assert-Condition (-not (Test-Path $installDir)) "Clean baseline: $installDir does not exist prior to install test"
Assert-Condition (-not (Test-Path $appDataDir)) "Clean baseline: $appDataDir does not exist prior to install test"
Assert-Condition (-not (Test-Path $arpKey)) "Clean baseline: HKCU uninstall entry does not exist prior to install test"

$installerDir = Join-Path $repoRoot 'artifacts\installer'
$baseInstaller = Join-Path $installerDir "GitNest-Setup-$baseVersion-win-x64.exe"
$upgradeInstaller = Join-Path $installerDir 'GitNest-Setup-0.1.1-test-win-x64.exe'

if ($RebuildInstaller -or -not (Test-Path $baseInstaller)) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipPublish
    if ($LASTEXITCODE -ne 0) { throw 'Base installer build failed.' }
}
Assert-Condition (Test-Path $baseInstaller) "Base installer exists: $baseInstaller"

& (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipPublish -AppVersion '0.1.1-test' -NumericVersion '0.1.1.0' -OutputBaseFilename 'GitNest-Setup-0.1.1-test-win-x64'
if ($LASTEXITCODE -ne 0) { throw 'Upgrade test installer build failed.' }
Assert-Condition (Test-Path $upgradeInstaller) "Upgrade test installer exists: $upgradeInstaller"

$userPathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')
$machinePathBefore = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$desktopExistedBefore = Test-Path $userDesktopShortcut

$testRepoRoot = Join-Path ([IO.Path]::GetTempPath()) ("GitNest-installer-repo-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRepoRoot -Force | Out-Null

try {
    # Prepare a temporary user git repository outside the install directory
    $gitCmd = (Get-Command git -ErrorAction Stop).Source
    & $gitCmd -C $testRepoRoot init -b main | Out-Null
    & $gitCmd -C $testRepoRoot config user.name 'Installer Tester' | Out-Null
    & $gitCmd -C $testRepoRoot config user.email 'tester@example.invalid' | Out-Null
    Set-Content -Path (Join-Path $testRepoRoot 'README.md') -Value '# User Repo Preserved' -Encoding UTF8
    & $gitCmd -C $testRepoRoot add README.md | Out-Null
    & $gitCmd -C $testRepoRoot commit -m 'initial commit' | Out-Null
    $repoHeadBefore = (& $gitCmd -C $testRepoRoot rev-parse HEAD).Trim()

    # 1. Silent clean install
    $procInstall = Start-Process -FilePath $baseInstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-') -Wait -PassThru
    Assert-Condition ($procInstall.ExitCode -eq 0) "Clean install exited with code 0"

    $installedExe = Join-Path $installDir 'GitNest.exe'
    Assert-Condition (Test-Path $installedExe) "Installed executable present at $installedExe"
    foreach ($req in @('LICENSE', 'NOTICE.md', 'THIRD-PARTY-LICENSES.md', 'licenses\AvaloniaEdit-LICENSE.txt')) {
        Assert-Condition (Test-Path (Join-Path $installDir $req)) "Installed license/notice present: $req"
    }
    $zhSatellite = Get-ChildItem -Path (Join-Path $installDir 'zh-Hans') -Filter '*.resources.dll' -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $zhSatellite -and $zhSatellite.Count -gt 0) "zh-Hans localization resource satellite present in install directory"

    foreach ($forbiddenPattern in @('git.exe', '*.pdb', 'createdump.exe', 'OpenAI*.dll', 'Azure*.dll')) {
        $found = Get-ChildItem -Path $installDir -Filter $forbiddenPattern -Recurse -ErrorAction SilentlyContinue
        Assert-Condition (-not $found) "Installed payload excludes forbidden file pattern: $forbiddenPattern"
    }

    $installedManifest = [PeManifestReader]::Read($installedExe)
    Assert-Condition ($installedManifest -like '*PerMonitorV2*' -and $installedManifest -like '*true/pm*') "Installed GitNest.exe embeds PerMonitorV2 and true/pm manifest"

    $userPathAfterInstall = [Environment]::GetEnvironmentVariable('Path', 'User')
    $machinePathAfterInstall = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    Assert-Condition ($userPathAfterInstall -eq $userPathBefore -and $machinePathAfterInstall -eq $machinePathBefore) "Install did not modify User or Machine PATH"

    Assert-Condition (Test-Path (Join-Path $startMenuDir 'GitNest.lnk')) "Start Menu GitNest shortcut created"
    Assert-Condition (Test-Path (Join-Path $startMenuDir 'Uninstall GitNest.lnk')) "Start Menu Uninstall shortcut created"
    if (-not $desktopExistedBefore) {
        Assert-Condition (-not (Test-Path $userDesktopShortcut)) "Desktop icon remains optional (not forced during silent default install)"
    }

    Assert-Condition (Test-Path $arpKey) "HKCU uninstall ARP registry key exists"
    $arp = Get-ItemProperty -Path $arpKey
    Assert-Condition ($arp.DisplayName -eq 'GitNest' -and $arp.DisplayVersion -eq $baseVersion) "ARP entry DisplayName/DisplayVersion match ($baseVersion)"

    # 2. Smoke launch installed app -> creates %APPDATA%\GitNest -> preserve user marker
    $appProc = Start-Process -FilePath $installedExe -PassThru
    Start-Sleep -Seconds 3
    $appProc.Refresh()
    Assert-Condition (-not $appProc.HasExited) "Installed GitNest.exe remained running after cold launch"
    if (-not $appProc.CloseMainWindow()) {
        Stop-Process -Id $appProc.Id -Force
        $appProc.WaitForExit()
    }
    else {
        if (-not $appProc.WaitForExit(3000)) {
            Stop-Process -Id $appProc.Id -Force
            $appProc.WaitForExit()
        }
    }

    Assert-Condition (Test-Path $appDataDir) "App created user configuration directory %APPDATA%\GitNest"
    $markerPath = Join-Path $appDataDir 'user-marker.json'
    Set-Content -Path $markerPath -Value '{"preserve":"yes","version":"1"}' -Encoding UTF8

    # 3. Higher-version silent in-place upgrade
    $procUpgrade = Start-Process -FilePath $upgradeInstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-') -Wait -PassThru
    Assert-Condition ($procUpgrade.ExitCode -eq 0) "In-place upgrade to 0.1.1-test exited with code 0"

    $arpUpgraded = Get-ItemProperty -Path $arpKey
    Assert-Condition ($arpUpgraded.DisplayVersion -eq '0.1.1-test') "ARP DisplayVersion updated after upgrade to 0.1.1-test"
    Assert-Condition ((Test-Path $markerPath) -and ((Get-Content $markerPath -Raw).Trim() -eq '{"preserve":"yes","version":"1"}')) "Upgrade preserved %APPDATA%\GitNest user marker file"

    $appProc2 = Start-Process -FilePath $installedExe -PassThru
    Start-Sleep -Seconds 3
    $appProc2.Refresh()
    Assert-Condition (-not $appProc2.HasExited) "Upgraded GitNest.exe remained running after launch"
    if (-not $appProc2.CloseMainWindow()) {
        Stop-Process -Id $appProc2.Id -Force
        $appProc2.WaitForExit()
    }
    else {
        if (-not $appProc2.WaitForExit(3000)) {
            Stop-Process -Id $appProc2.Id -Force
            $appProc2.WaitForExit()
        }
    }

    # 4. Silent uninstall -> install dir/Start Menu/ARP gone, %APPDATA%\GitNest & user Git repo preserved
    $uninstallerExe = Join-Path $installDir 'unins000.exe'
    Assert-Condition (Test-Path $uninstallerExe) "Uninstaller executable exists: $uninstallerExe"
    $procUninstall = Start-Process -FilePath $uninstallerExe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    Assert-Condition ($procUninstall.ExitCode -eq 0) "Silent uninstall exited with code 0"

    # Wait briefly for Inno uninstaller self-delete stub cleanup
    for ($i = 0; $i -lt 20 -and (Test-Path $installDir); $i++) {
        Start-Sleep -Milliseconds 250
    }

    Assert-Condition (-not (Test-Path $installDir)) "Uninstall completely removed install directory $installDir"
    Assert-Condition (-not (Test-Path $startMenuDir)) "Uninstall removed Start Menu folder $startMenuDir"
    Assert-Condition (-not (Test-Path $arpKey)) "Uninstall removed HKCU ARP registry entry"
    Assert-Condition ((Test-Path $markerPath) -and ((Get-Content $markerPath -Raw).Trim() -eq '{"preserve":"yes","version":"1"}')) "Uninstall preserved user data directory %APPDATA%\GitNest and marker"

    $repoHeadAfter = (& $gitCmd -C $testRepoRoot rev-parse HEAD).Trim()
    $repoStatusAfter = (& $gitCmd -C $testRepoRoot status --short)
    Assert-Condition ($repoHeadAfter -eq $repoHeadBefore -and [string]::IsNullOrWhiteSpace($repoStatusAfter)) "Uninstall left user Git repository and commits completely intact"

    $userPathAfterUninstall = [Environment]::GetEnvironmentVariable('Path', 'User')
    $machinePathAfterUninstall = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    Assert-Condition ($userPathAfterUninstall -eq $userPathBefore -and $machinePathAfterUninstall -eq $machinePathBefore) "Uninstall left User and Machine PATH unchanged"

    Write-Host 'ALL INSTALLER VERIFICATION CHECKS PASSED.'
}
finally {
    if (-not $KeepTestArtifacts) {
        Send-ToRecycleBin -TargetPath $appDataDir -IsDirectory
        Send-ToRecycleBin -TargetPath $testRepoRoot -IsDirectory
        Send-ToRecycleBin -TargetPath $upgradeInstaller
    }
}
