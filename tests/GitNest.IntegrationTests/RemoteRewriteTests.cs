using SourceGit.Commands;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunRemoteRewriteTestsAsync(string realGit, string root)
    {
        var testDir = Path.Combine(root, "remote-rewrite-suite");
        var originBare = Path.Combine(testDir, "origin.git");
        var fetchBare = Path.Combine(testDir, "fetch.git");
        var otherBare = Path.Combine(testDir, "other.git");
        var clone1 = Path.Combine(testDir, "clone1");
        var clone2 = Path.Combine(testDir, "clone2");
        Directory.CreateDirectory(testDir);

        var journal = new OperationJournalStore(Path.Combine(testDir, "journal.json"));

        // 1. Bare origin with default branch 'release' -> verify BranchProtectionPolicy across default names & remote HEAD
        await InitBareRepoAsync(realGit, originBare, "release");
        await InitBareRepoAsync(realGit, fetchBare, "release");
        await InitBareRepoAsync(realGit, otherBare, "release");

        await GitAsync(realGit, "clone", originBare, clone1);
        await GitInAsync(realGit, clone1, "config", "--local", "user.name", "Alice");
        await GitInAsync(realGit, clone1, "config", "--local", "user.email", "alice@example.invalid");

        var c1 = await CommitFileAsync(realGit, clone1, "app.txt", "v1\n", "C1");
        var c2 = await CommitFileAsync(realGit, clone1, "app.txt", "v2\n", "C2");
        var c3 = await CommitFileAsync(realGit, clone1, "app.txt", "v3\n", "C3");
        await GitInAsync(realGit, clone1, "push", "-u", "origin", "release");
        await GitInAsync(realGit, clone1, "remote", "set-head", "origin", "release");

        var mainProt = await BranchProtectionPolicy.InspectAsync(clone1, "main");
        var masterProt = await BranchProtectionPolicy.InspectAsync(clone1, "master");
        var developProt = await BranchProtectionPolicy.InspectAsync(clone1, "develop");
        var releaseProt = await BranchProtectionPolicy.InspectAsync(clone1, "release");
        Check(mainProt.IsEffectivelyProtected && masterProt.IsEffectivelyProtected &&
              developProt.IsEffectivelyProtected && releaseProt.IsEffectivelyProtected &&
              releaseProt.IsRemoteDefaultBranch,
              "Default branch protection guards main, master, develop, and remote HEAD default branch");

        var releaseResetPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var releaseRemoteBlocked = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "release", c2);
        var blockedProtExec = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions
            {
                Mode = ResetCommitMode.DiscardCodeHard,
                Preflight = releaseResetPre,
                AlsoRewriteRemote = true,
                RemoteRewritePreflight = releaseRemoteBlocked,
            },
            journal);
        Check(!releaseRemoteBlocked.CanRewriteRemote && releaseRemoteBlocked.BlockedByProtection &&
              blockedProtExec.BlockedByProtection &&
              (await GitInAsync(realGit, clone1, "rev-parse", "HEAD")).Trim() == c3,
              "Protected remote default branch refuses remote history rewrite by default");

        // Local override scoped strictly to one branch in clone1
        await BranchProtectionPolicy.SetLocalOverrideAsync(clone1, "release", allowDestructiveHistoryRewrite: true);
        await GitAsync(realGit, "clone", originBare, clone2);
        await GitInAsync(realGit, clone2, "config", "--local", "user.name", "Bob");
        await GitInAsync(realGit, clone2, "config", "--local", "user.email", "bob@example.invalid");

        Check(!(await BranchProtectionPolicy.InspectAsync(clone1, "release")).IsEffectivelyProtected &&
              (await BranchProtectionPolicy.InspectAsync(clone1, "main")).IsEffectivelyProtected &&
              (await BranchProtectionPolicy.InspectAsync(clone2, "release")).IsEffectivelyProtected,
              "Explicit repository branch-protection opt-out is strictly scoped to one branch in one repository");

        // 2. Offline unknown remote (fast loopback fail + secret redaction) & diverged remote blocking
        const string secretToken = "ghp_ResetSecret888XYZ";
        await GitInAsync(realGit, clone1, "remote", "add", "broken", $"https://bob:{secretToken}@127.0.0.1:1/org/repo.git");
        var brokenPre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "release", c2, "broken", "release");
        Check(!brokenPre.CanRewriteRemote && brokenPre.RemoteStateUnknown &&
              brokenPre.BlockReason.Contains("远端状态未知", StringComparison.Ordinal) &&
              !brokenPre.BlockReason.Contains(secretToken, StringComparison.Ordinal) &&
              !brokenPre.RedactedRemoteUrl.Contains(secretToken, StringComparison.Ordinal),
              "Offline or unreachable remote reports '远端状态未知', blocks rewrite, and redacts credentials");

        await GitInAsync(realGit, clone2, "checkout", "release");
        var c4Collab = await CommitFileAsync(realGit, clone2, "collab-diverge.txt", "from bob\n", "C4 collaborator unintegrated");
        await GitInAsync(realGit, clone2, "push", "origin", "release");

        var divergedPre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "release", c2);
        Check(!divergedPre.CanRewriteRemote && divergedPre.RemoteDivergedWithUnintegratedCommits &&
              divergedPre.LiveRemoteTipSha == c4Collab,
              "Diverged or unintegrated collaborator commits on remote stop remote history rewrite before mutation");

        // 3. Split fetch/push URLs, changed push URL block, and multiple push URLs block
        await GitInAsync(realGit, clone1, "checkout", "-b", "feat/split-rewrite", c2);
        await GitInAsync(realGit, clone1, "remote", "add", "split", fetchBare);
        await GitInAsync(realGit, clone1, "push", "split", "feat/split-rewrite:feat/split-rewrite");
        await GitInAsync(realGit, clone1, "fetch", "split");
        var splitTip = await CommitFileAsync(realGit, clone1, "split.txt", "tip\n", "split rewrite tip");
        await GitInAsync(realGit, clone1, "push", originBare, "feat/split-rewrite:feat/split-rewrite");
        await GitInAsync(realGit, clone1, "remote", "set-url", "--push", "split", originBare);
        await new SourceGit.Commands.Branch(clone1, "feat/split-rewrite").SetUpstreamTargetedAsync("split", "feat/split-rewrite");

        var splitResetPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: false);
        var splitRewritePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/split-rewrite", c2);
        Check(splitRewritePre.CanRewriteRemote && splitRewritePre.LiveRemoteTipSha == splitTip && splitRewritePre.PushUrl == originBare,
              "Remote rewrite preflight pins actual push endpoint and live tip");

        await GitInAsync(realGit, clone1, "remote", "set-url", "--push", "split", otherBare);
        var linkedChangedBlock = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions { Preflight = splitResetPre, AlsoRewriteRemote = true, RemoteRewritePreflight = splitRewritePre },
            journal);
        var standaloneChangedBlock = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(
            clone1, new RemoteRewriteOptions { Preflight = splitRewritePre }, journal);
        Check(!linkedChangedBlock.Succeeded && !linkedChangedBlock.LocalBranchReset &&
              (await GitInAsync(realGit, clone1, "rev-parse", "HEAD")).Trim() == splitTip &&
              !standaloneChangedBlock.Succeeded &&
              string.IsNullOrEmpty((await GitAsync(realGit, "--git-dir", otherBare, "for-each-ref", "refs/heads/feat/split-rewrite")).Trim()),
              "Changing push URL after confirmation blocks both linked reset and standalone remote rewrite before local mutation");

        await GitInAsync(realGit, clone1, "remote", "set-url", "--push", "--add", "split", originBare);
        var multiRewritePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/split-rewrite", c2);
        Check(!multiRewritePre.CanRewriteRemote, "Multiple push URLs block remote rewrite");

        await GitInAsync(realGit, clone1, "config", "--unset-all", "remote.split.pushurl");
        await GitInAsync(realGit, clone1, "remote", "set-url", "--push", "split", originBare);
        var splitCfgHash = Hash(Path.Combine(clone1, ".git", "config"));
        var splitRewriteOk = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(
            clone1,
            new RemoteRewriteOptions { Preflight = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/split-rewrite", c2) },
            journal);
        Check(splitRewriteOk.Succeeded &&
              (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/feat/split-rewrite")).Trim() == c2 &&
              (await GitInAsync(realGit, clone1, "rev-parse", "refs/remotes/split/feat/split-rewrite")).Trim() == c2 &&
              Hash(Path.Combine(clone1, ".git", "config")) == splitCfgHash,
              "Split-endpoint lease rewrite updates push server and preserves fetch tracking without persisting temporary remote");

        // 4. Pinned --force-with-lease race against background fetch + recovery + clean happy-path rewrite (suppresses followTags)
        await GitInAsync(realGit, clone1, "checkout", "-b", "feat/lease-rewrite", c2);
        await CommitFileAsync(realGit, clone1, "lease.txt", "commit A\n", "L1");
        var l2 = await CommitFileAsync(realGit, clone1, "lease.txt", "commit B\n", "L2");
        await GitInAsync(realGit, clone1, "push", "-u", "origin", "feat/lease-rewrite");

        var leaseLocalPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var leaseRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/lease-rewrite", c2);

        await GitInAsync(realGit, clone2, "fetch", "origin");
        await GitInAsync(realGit, clone2, "checkout", "-b", "feat/lease-rewrite", "origin/feat/lease-rewrite");
        var l3 = await CommitFileAsync(realGit, clone2, "collab-race.txt", "L3 collaborator\n", "L3 post-confirm push");
        await GitInAsync(realGit, clone2, "push", "origin", "feat/lease-rewrite");
        await GitInAsync(realGit, clone1, "fetch", "origin");

        var leaseRaceOut = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = leaseLocalPre, AlsoRewriteRemote = true, RemoteRewritePreflight = leaseRemotePre },
            journal);
        var originLeaseTip = (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/feat/lease-rewrite")).Trim();
        var originAllRefs = await GitAsync(realGit, "--git-dir", originBare, "show-ref");
        Check(!leaseRaceOut.Succeeded && leaseRaceOut.IsPartialSuccess && leaseRaceOut.LocalBranchReset &&
              !leaseRaceOut.RemoteBranchRewritten && leaseRaceOut.RejectedByRemoteLease &&
              leaseRaceOut.CollaboratorCommitsPreserved && leaseRaceOut.ExpectedRemoteTipSha == l2 &&
              originLeaseTip == l3 &&
              leaseRaceOut.Summary.Contains("本地已撤回，远端未更新", StringComparison.Ordinal) &&
              !originAllRefs.Contains("refs/gitnest/backups", StringComparison.Ordinal),
              "Pinned --force-with-lease survives background fetch, rejects collaborator push, preserves L3, and marks PartialSuccess");

        var leaseRecoverPre = await HistoryResetService.InspectBackupRestoreForResetAsync(clone1, leaseRaceOut.BackupRefName, "feat/lease-rewrite", leaseRaceOut.OperationId, journal);
        await HistoryResetService.RestoreFromBackupAsync(
            clone1,
            new BackupResetRestoreOptions { Preflight = leaseRecoverPre, RestoreOntoExistingBranch = true, SourceOperationId = leaseRaceOut.OperationId },
            journal);
        Check((await GitInAsync(realGit, clone1, "rev-parse", "HEAD")).Trim() == l2,
              "Recovery after partial remote failure restores feat/lease-rewrite back to original tip");

        await GitInAsync(realGit, clone1, "checkout", "-b", "feat/clean-rewrite", c2);
        var cr1 = await CommitFileAsync(realGit, clone1, "clean.txt", "to be rewritten\n", "CR1 rewrite target");
        await GitInAsync(realGit, clone1, "push", "-u", "origin", "feat/clean-rewrite");
        await GitInAsync(realGit, clone1, "config", "push.followTags", "true");
        await GitInAsync(realGit, clone1, "tag", "-a", "unrequested-tag", "-m", "private tag");

        var happyLocalPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var happyRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/clean-rewrite", c2);
        var happyOut = await HistoryResetService.ExecuteCommitResetAsync(
            clone1,
            new HistoryResetOptions { Mode = ResetCommitMode.DiscardCodeHard, Preflight = happyLocalPre, AlsoRewriteRemote = true, RemoteRewritePreflight = happyRemotePre },
            journal);
        var originHappyTip = (await GitAsync(realGit, "--git-dir", originBare, "rev-parse", "refs/heads/feat/clean-rewrite")).Trim();
        var originHappyRefs = await GitAsync(realGit, "--git-dir", originBare, "show-ref");
        Check(happyOut.Succeeded && happyOut.LocalBranchReset && happyOut.RemoteBranchRewritten &&
              originHappyTip == c2 &&
              await BackupRefs.ExistsAsync(clone1, happyOut.BackupRefName) &&
              !originHappyRefs.Contains("refs/gitnest/backups", StringComparison.Ordinal) &&
              !originHappyRefs.Contains("refs/tags/", StringComparison.Ordinal),
              "Clean reset + remote rewrite updates both local and remote via --force-with-lease without pushing backups or tags");

        // Fingerprint persistence & endpoint verification on rewrite journal
        var happyRecord = journal.TryGet(happyOut.OperationId)!;
        Check(!string.IsNullOrEmpty(happyRecord.RemotePushUrlFingerprint),
              "Rewrite journal persists endpoint fingerprint without storing raw credentialed URL");
        await GitInAsync(realGit, clone1, "remote", "set-url", "origin", otherBare);
        var verifyChanged = await HistoryResetService.VerifyRemoteRewriteJournalRecordAsync(clone1, happyRecord, journal);
        Check(!verifyChanged.QuerySucceeded,
              "Restart verification rejects a replacement remote endpoint instead of reconciling against another server");
        await GitInAsync(realGit, clone1, "remote", "set-url", "origin", originBare);

        // 5. Ambiguous network timeout: single push attempt only, verified via ls-remote + startup reconcile
        await GitInAsync(realGit, clone1, "checkout", "-b", "feat/timeout-probe", c2);
        await CommitFileAsync(realGit, clone1, "timeout.txt", "timeout test\n", "TP1 commit");
        await GitInAsync(realGit, clone1, "push", "-u", "origin", "feat/timeout-probe");
        await GitInAsync(realGit, clone1, "reset", "--hard", c2);
        var tpRemotePre = await HistoryResetService.InspectRemoteRewriteForTargetAsync(clone1, "feat/timeout-probe", c2);

        int pushInvocationCount = 0;
        HistoryResetService.RemotePushExecutorOverride = async (_, _, _, _, _) =>
        {
            pushInvocationCount++;
            await GitAsync(realGit, "--git-dir", originBare, "update-ref", "refs/heads/feat/timeout-probe", c2);
            return new HistoryResetService.RemotePushRawResult
            {
                IsSuccess = false,
                TimedOutOrConnectionLost = true,
                StdErr = "fatal: early EOF / operation timed out",
            };
        };

        HistoryResetOutcome ambiguousVerifiedOut;
        try
        {
            ambiguousVerifiedOut = await HistoryResetService.ExecuteRemoteRewriteOnlyAsync(
                clone1, new RemoteRewriteOptions { Preflight = tpRemotePre }, journal);
        }
        finally
        {
            HistoryResetService.RemotePushExecutorOverride = null;
        }
        Check(pushInvocationCount == 1 && ambiguousVerifiedOut.Succeeded &&
              ambiguousVerifiedOut.RemoteBranchRewritten && ambiguousVerifiedOut.VerifiedRemoteTipSha == c2 &&
              ambiguousVerifiedOut.StepResults.Any(s => s.Kind == HistoryResetStepKind.RemoteStateProbe && s.Succeeded),
              "Ambiguous timeout does NOT blindly retry push and verifies server outcome directly via ls-remote");

        await GitInAsync(realGit, clone1, "checkout", "-b", "feat/pending-verify", cr1);
        await GitInAsync(realGit, clone1, "push", "-u", "origin", "feat/pending-verify");
        var pvPre = await HistoryResetService.InspectCommitResetAsync(clone1, c2, probeRemote: true);
        var uncertainRec = new GitOperationRecord
        {
            OperationId = "op_reset_uncertain_test",
            RepositoryCommonDir = pvPre.CanonicalCommonDir,
            WorktreePath = clone1,
            Kind = GitOperationKind.RewriteRemoteHistory,
            Title = "模拟中断的远程历史改写",
            BranchName = "feat/pending-verify",
            RemoteName = "origin",
            RemoteBranchName = "feat/pending-verify",
            OriginalTipSha = cr1,
            TargetTipSha = c2,
            ExpectedRemoteTipSha = cr1,
            BackupRefName = happyOut.BackupRefName,
            Stage = GitOperationStage.Executing,
        };
        journal.AppendOrUpdate(uncertainRec);
        await GitInAsync(realGit, clone1, "reset", "--hard", c2);

        var openReport = await OperationJournalService.ReconcileOnOpenAsync(clone1, journal);
        var reconciledUncertain = journal.TryGet(uncertainRec.OperationId)!;
        var stageAfterReopen = reconciledUncertain.Stage;
        var noteAfterReopen = reconciledUncertain.ReconciliationNote;
        var liveVerify = await HistoryResetService.VerifyRemoteRewriteJournalRecordAsync(clone1, reconciledUncertain, journal);
        Check(openReport.HasPendingAttention &&
              stageAfterReopen == GitOperationStage.PendingVerification &&
              (noteAfterReopen.Contains('待', StringComparison.Ordinal) ||
               noteAfterReopen.Contains("核对", StringComparison.Ordinal)) &&
              liveVerify.QuerySucceeded && liveVerify.LiveRemoteTipSha == cr1 &&
              liveVerify.UpdatedRecord.Stage == GitOperationStage.PartialSuccess &&
              liveVerify.UpdatedRecord.StepSummary.Contains("本地已撤回，远端未更新", StringComparison.Ordinal),
              "Interrupted reset/rewrite record is flagged on reopen and cleanly reconciled via VerifyRemoteRewriteJournalRecordAsync");
    }
}
