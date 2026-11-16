using System.Net;
using System.Net.Sockets;
using System.Text;

using SourceGit.Commands;
using SourceGit.Models;
using RemoteModel = SourceGit.Models.Remote;

internal static partial class Program
{
    private static async Task RunSyncTestsAsync(string realGit, string root)
    {
        var suiteRoot = Path.Combine(root, "sync-suite");
        Directory.CreateDirectory(suiteRoot);
        var journal = new OperationJournalStore(Path.Combine(suiteRoot, "sync-journal.json"));

        var bareOrigin = Path.Combine(suiteRoot, "origin.git");
        var bareBackup = Path.Combine(suiteRoot, "backup.git");
        var cloneA = Path.Combine(suiteRoot, "clone-a");
        var cloneB = Path.Combine(suiteRoot, "clone-b");

        await InitBareRepoAsync(realGit, bareOrigin);
        await InitBareRepoAsync(realGit, bareBackup);

        var seedDir = Path.Combine(suiteRoot, "seed");
        await InitRepoAsync(realGit, seedDir, "main", "Seed Bot", "seed@example.invalid");
        await CommitFileAsync(realGit, seedDir, "shared.txt", "seed-v1\n", "seed commit");
        var baseSha = await CommitFileAsync(realGit, seedDir, "other.txt", "other-v1\n", "seed other");
        await GitInAsync(realGit, seedDir, "remote", "add", "origin", bareOrigin);
        await GitInAsync(realGit, seedDir, "push", "-u", "origin", "main");
        await GitInAsync(realGit, seedDir, "remote", "add", "backup", bareBackup);
        await GitInAsync(realGit, seedDir, "checkout", "-b", "feat/no-upstream");
        await CommitFileAsync(realGit, seedDir, "backup-only.txt", "backup\n", "existing branch on backup");
        await GitInAsync(realGit, seedDir, "push", "backup", "refs/heads/feat/no-upstream:refs/heads/feat/no-upstream");

        await GitAsync(realGit, "clone", bareOrigin, cloneA);
        await GitAsync(realGit, "clone", bareOrigin, cloneB);
        foreach (var c in new[] { cloneA, cloneB })
        {
            await GitInAsync(realGit, c, "config", "--local", "user.name", "Clone User");
            await GitInAsync(realGit, c, "config", "--local", "user.email", "clone@example.invalid");
        }
        await GitInAsync(realGit, cloneA, "remote", "add", "backup", bareBackup);

        var originRemote = new RemoteModel { Name = "origin", URL = bareOrigin };
        var backupRemote = new RemoteModel { Name = "backup", URL = bareBackup };

        // 1. Overview helper never fakes unknown/unfetched state as zero
        var unfetchedSummary = RepositorySyncOverviewBuilder.Build(
            uncommittedCount: 2,
            currentBranchName: "main",
            upstreamFriendlyName: "origin/main",
            aheadCount: 1,
            behindCount: 0,
            hasRemotes: true,
            isUpstreamGone: false,
            lastFetchAt: null,
            lastFetchFailed: false,
            lastFetchError: string.Empty);
        Check(unfetchedSummary.HasUnknownRemoteState &&
              unfetchedSummary.SummaryText.Contains("远端状态未获取", StringComparison.Ordinal) &&
              !unfetchedSummary.SummaryText.Contains("0 次远端提交待拉取", StringComparison.Ordinal),
              "Status summary distinguishes unfetched state from 0 behind commits");

        var noUpstreamSummary = RepositorySyncOverviewBuilder.Build(
            uncommittedCount: 1,
            currentBranchName: "feat/local",
            upstreamFriendlyName: string.Empty,
            aheadCount: 0,
            behindCount: 0,
            hasRemotes: true,
            isUpstreamGone: false,
            lastFetchAt: DateTime.Now,
            lastFetchFailed: false,
            lastFetchError: string.Empty);
        Check(noUpstreamSummary.SummaryText.Contains("未设置上游分支", StringComparison.Ordinal),
              "Status summary states missing upstream explicitly instead of showing 0");

        // 2. Single-branch push & selected-revision push suppress inherited followTags, unpushed branches, and annotated tags
        await GitInAsync(realGit, cloneA, "checkout", "-b", "local-unpushed-branch");
        await CommitFileAsync(realGit, cloneA, "unpushed.txt", "branch only\n", "local branch commit");

        await GitInAsync(realGit, cloneA, "checkout", "main");
        var mainV2Sha = await CommitFileAsync(realGit, cloneA, "shared.txt", "cloneA-v2\n", "main commit v2");
        await GitInAsync(realGit, cloneA, "tag", "-a", "v9.9.9-local", "-m", "private tag");
        await GitInAsync(realGit, cloneA, "config", "push.followTags", "true");

        var singlePushRes = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions
            {
                LocalBranch = "main",
                Remote = originRemote,
                RemoteBranch = "main",
                SetUpstream = false,
            });
        var bareRefsAfterPush = await GitInAsync(realGit, bareOrigin, "for-each-ref", "--format=%(refname)", "refs/");
        Check(singlePushRes.Succeeded &&
              bareRefsAfterPush.Contains("refs/heads/main", StringComparison.Ordinal) &&
              !bareRefsAfterPush.Contains("local-unpushed-branch", StringComparison.Ordinal) &&
              !bareRefsAfterPush.Contains("v9.9.9-local", StringComparison.Ordinal) &&
              (await GitInAsync(realGit, cloneA, "config", "push.followTags")).Trim() == "true",
              "Single-branch push updates only target branch and suppresses inherited followTags without altering user config");

        await GitInAsync(realGit, cloneA, "tag", "-a", "v9.9.8-base", "-m", "private base tag", baseSha);
        var selectedRevRes = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions
            {
                LocalBranch = "main",
                SourceRevisionSha = baseSha,
                Remote = originRemote,
                RemoteBranch = "revision-only",
            },
            journalStore: journal);
        var bareRevTip = (await GitAsync(realGit, "--git-dir", bareOrigin, "rev-parse", "refs/heads/revision-only")).Trim();
        var bareTagRefs = await GitAsync(realGit, "--git-dir", bareOrigin, "for-each-ref", "refs/tags");
        Check(selectedRevRes.Succeeded && selectedRevRes.HeadSha == baseSha &&
              bareRevTip == baseSha && string.IsNullOrEmpty(bareTagRefs),
              "Selected-revision push publishes exactly the chosen commit without newer commits or annotated tags");
        await GitInAsync(realGit, cloneA, "config", "--unset", "push.followTags");

        // 3. Split fetch/push URL preserves fetch tracking & config; multiple push URLs block push & selected-revision push
        var splitPushBare = Path.Combine(suiteRoot, "split-push.git");
        await InitBareRepoAsync(realGit, splitPushBare);
        await GitInAsync(realGit, cloneA, "remote", "add", "split", bareOrigin);
        await GitInAsync(realGit, cloneA, "fetch", "split");
        await GitInAsync(realGit, cloneA, "remote", "set-url", "--push", "split", splitPushBare);
        var splitTrackingBefore = (await GitInAsync(realGit, cloneA, "rev-parse", "refs/remotes/split/main")).Trim();
        var splitCfgHash = Hash(Path.Combine(cloneA, ".git", "config"));
        var splitRemote = new RemoteModel { Name = "split", URL = bareOrigin, PushURL = splitPushBare };
        var splitPush = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions { LocalBranch = "main", Remote = splitRemote, RemoteBranch = "main" },
            journalStore: journal);
        Check(splitPush.Succeeded &&
              (await GitAsync(realGit, "--git-dir", splitPushBare, "rev-parse", "refs/heads/main")).Trim() == mainV2Sha &&
              (await GitInAsync(realGit, cloneA, "rev-parse", "refs/remotes/split/main")).Trim() == splitTrackingBefore &&
              Hash(Path.Combine(cloneA, ".git", "config")) == splitCfgHash,
              "Split-endpoint push preserves fetch tracking and leaves no temporary remote configuration");

        await GitInAsync(realGit, cloneA, "remote", "set-url", "--push", "--add", "split", bareBackup);
        var multiPush = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions { LocalBranch = "main", Remote = splitRemote, RemoteBranch = "main" },
            journalStore: journal);
        var multiRevPush = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions { LocalBranch = "main", SourceRevisionSha = baseSha, Remote = splitRemote, RemoteBranch = "multi-rev" },
            journalStore: journal);
        Check(!multiPush.Succeeded && !multiRevPush.Succeeded &&
              string.IsNullOrEmpty(await GitAsync(realGit, "--git-dir", splitPushBare, "for-each-ref", "refs/heads/multi-rev")),
              "Multiple push URLs block both ordinary push and selected-revision push");

        // 4. No-upstream branch collision vs explicit tracking creation
        await GitInAsync(realGit, cloneA, "checkout", "-b", "feat/no-upstream");
        await CommitFileAsync(realGit, cloneA, "feat.txt", "new feat\n", "feat commit");

        var collisionPush = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions
            {
                LocalBranch = "feat/no-upstream",
                Remote = backupRemote,
                RemoteBranch = "feat/no-upstream",
                SetUpstream = true,
                ConfirmOverwriteExistingRemoteBranchWithoutUpstream = false,
            });
        Check(!collisionPush.Succeeded && collisionPush.Outcome == PushOutcomeKind.RemoteBranchExistsWithoutUpstream,
              "Push without upstream stops to confirm when selected remote branch already exists on target remote");

        var originNewBranchPush = await GitSyncService.PushSingleBranchAsync(
            cloneA,
            new PushExecutionOptions
            {
                LocalBranch = "feat/no-upstream",
                Remote = originRemote,
                RemoteBranch = "feat/no-upstream",
                SetUpstream = true,
            });
        var configuredUpstream = (await GitInAsync(realGit, cloneA, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")).Trim();
        Check(originNewBranchPush.Succeeded && configuredUpstream == "origin/feat/no-upstream",
              "Push without upstream sets explicit tracking branch on selected remote");

        await GitInAsync(realGit, cloneA, "checkout", "main");

        // 5. Two-step Commit & Push: push failure preserves commit and RetryPushSessionAsync avoids duplicate commit
        var preReceiveHook = Path.Combine(bareOrigin, "hooks", "pre-receive");
        await File.WriteAllTextAsync(preReceiveHook, "#!/bin/sh\necho \"protected branch rule declined push\" >&2\nexit 1\n", Utf8NoBom);

        await File.WriteAllTextAsync(Path.Combine(cloneA, "two-step.txt"), "step1\n", Utf8NoBom);
        await GitSyncService.StagePathsAsync(cloneA, ["two-step.txt"]);
        var twoStepSnap = await QueryStagedSnapshot.CaptureAsync(cloneA);
        var commitCountBefore = int.Parse((await GitInAsync(realGit, cloneA, "rev-list", "--count", "HEAD")).Trim());

        var twoStepSession = await GitSyncService.CommitAndPushAsync(
            cloneA,
            new CommitExecutionOptions { Message = "feat: two-step commit", ExpectedSnapshot = twoStepSnap },
            new PushExecutionOptions { LocalBranch = "main", Remote = originRemote, RemoteBranch = "main" });

        var commitCountAfterFailPush = int.Parse((await GitInAsync(realGit, cloneA, "rev-list", "--count", "HEAD")).Trim());
        Check(twoStepSession.CommitSucceeded &&
              twoStepSession.PushAttempted &&
              !twoStepSession.PushSucceeded &&
              twoStepSession.PushResult.Outcome == PushOutcomeKind.ProtectedOrHookRejected &&
              twoStepSession.CanRetryPushOnly &&
              commitCountAfterFailPush == commitCountBefore + 1,
              "Two-step Commit & Push retains local commit when push is rejected and enables retry-push-only");

        File.Delete(preReceiveHook);
        var retriedSession = await GitSyncService.RetryPushSessionAsync(twoStepSession);
        var commitCountAfterRetry = int.Parse((await GitInAsync(realGit, cloneA, "rev-list", "--count", "HEAD")).Trim());
        var bareMainShaAfterRetry = (await GitInAsync(realGit, bareOrigin, "rev-parse", "refs/heads/main")).Trim();
        Check(retriedSession.IsFullySucceeded &&
              commitCountAfterRetry == commitCountAfterFailPush &&
              bareMainShaAfterRetry == retriedSession.CommitResult.CommitSha,
              "Retrying push pushes the existing commit cleanly without creating duplicate commits");

        // 6. Fetch & Fast-Forward Pull (respects pull.rebase, dirty conflict, divergence handoff, gone/unreachable/detached)
        await GitInAsync(realGit, cloneB, "config", "--local", "pull.rebase", "true");
        var fetchRes = await GitSyncService.FetchAsync(cloneB, [originRemote]);
        Check(fetchRes.Succeeded && fetchRes.FetchedAtUtc.HasValue, "Fetch updates remote refs and records timestamp");

        var ffPullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
        Check(ffPullRes.Succeeded &&
              ffPullRes.Outcome == PullOutcomeKind.UpdatedFastForward &&
              ffPullRes.AfterHead == bareMainShaAfterRetry &&
              (await GitInAsync(realGit, cloneB, "config", "--local", "--get", "pull.rebase")).Trim() == "true",
              "Fast-forward pull advances cleanly while preserving user pull.rebase config untouched");

        await CommitFileAsync(realGit, cloneA, "other.txt", "remote-change\n", "remote touches other.txt");
        await GitInAsync(realGit, cloneA, "push", "origin", "main");

        await File.WriteAllTextAsync(Path.Combine(cloneB, "other.txt"), "local-dirty-uncommitted\n", Utf8NoBom);
        var dirtyPullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
        Check(!dirtyPullRes.Succeeded &&
              dirtyPullRes.Outcome == PullOutcomeKind.LocalChangesConflict &&
              await File.ReadAllTextAsync(Path.Combine(cloneB, "other.txt"), Encoding.UTF8) == "local-dirty-uncommitted\n",
              "Fast-forward pull refuses to overwrite dirty local modifications and preserves working tree");
        await GitInAsync(realGit, cloneB, "checkout", "--", "other.txt");

        await GitInAsync(realGit, cloneB, "pull", "--ff-only", "origin", "main");
        await CommitFileAsync(realGit, cloneB, "cloneb.txt", "cloneB diverge\n", "cloneB ahead");
        await CommitFileAsync(realGit, cloneA, "clonea.txt", "cloneA diverge\n", "cloneA ahead");
        await GitInAsync(realGit, cloneA, "push", "origin", "main");

        var cloneBHeadBeforeDivergedPull = (await GitInAsync(realGit, cloneB, "rev-parse", "HEAD")).Trim();
        var divergedPullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
        Check(!divergedPullRes.Succeeded &&
              divergedPullRes.Outcome == PullOutcomeKind.Diverged &&
              divergedPullRes.RequiresMergeHandoff &&
              divergedPullRes.LocalAheadCount == 1 &&
              divergedPullRes.RemoteBehindCount == 1 &&
              (await GitInAsync(realGit, cloneB, "rev-parse", "HEAD")).Trim() == cloneBHeadBeforeDivergedPull,
              "Diverged pull stops cleanly with exact counts, requires merge handoff, and never rewrites local commit");

        var nonFfPush = await GitSyncService.PushSingleBranchAsync(
            cloneB,
            new PushExecutionOptions { LocalBranch = "main", Remote = originRemote, RemoteBranch = "main" });
        Check(!nonFfPush.Succeeded && nonFfPush.Outcome == PushOutcomeKind.NonFastForwardRejected,
              "Normal push refuses non-fast-forward update when remote branch has diverged");

        var gonePullRes = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "deleted-remote-branch");
        Check(!gonePullRes.Succeeded && gonePullRes.Outcome == PullOutcomeKind.UpstreamGone,
              "Pull identifies deleted/missing remote branch accurately");

        var missingRemote = new RemoteModel { Name = "missing-remote", URL = Path.Combine(suiteRoot, "does-not-exist.git") };
        await GitInAsync(realGit, cloneB, "remote", "add", "missing-remote", missingRemote.URL);
        var unreachableFetch = await GitSyncService.FetchAsync(cloneB, [missingRemote]);
        Check(!unreachableFetch.Succeeded &&
              (unreachableFetch.Outcome == FetchOutcomeKind.RemoteNotFound || unreachableFetch.Outcome == FetchOutcomeKind.OfflineOrUnreachable),
              "Fetch reports unreachable/missing remote cleanly without false success");

        await GitInAsync(realGit, cloneB, "checkout", "--detach", "HEAD");
        var detachedPull = await GitSyncService.PullFastForwardAsync(cloneB, originRemote, "main");
        var detachedPush = await GitSyncService.PushSingleBranchAsync(
            cloneB,
            new PushExecutionOptions { LocalBranch = "main", Remote = originRemote, RemoteBranch = "main" });
        Check(!detachedPull.Succeeded && detachedPull.Outcome == PullOutcomeKind.DetachedHead &&
              !detachedPush.Succeeded && detachedPush.Outcome == PushOutcomeKind.DetachedHead,
              "Detached HEAD prevents unsafe pull and single-branch push explicitly");

        // 7. AuthCanceled, HTTP 401 AuthFailed, preserved local commit state & secret redaction in journal
        var authJournalFile = Path.Combine(suiteRoot, "auth-journal.json");
        var authJournal = new OperationJournalStore(authJournalFile);
        var authRepo = Path.Combine(suiteRoot, "auth-sync-repo");
        await InitRepoAsync(realGit, authRepo, "main", "Normal Author", "author@example.invalid");

        const string secretToken = "ghp_SuperSecretToken987654321";
        const string secretHeader = "Bearer SecretBearerXYZ999";
        await GitInAsync(realGit, authRepo, "config", "--local", "http.extraheader", $"AUTHORIZATION: {secretHeader}");
        await GitInAsync(realGit, authRepo, "remote", "add", "origin", $"https://oauth2:{secretToken}@127.0.0.1:1/org/private.git");

        await File.WriteAllTextAsync(Path.Combine(authRepo, "work.txt"), "local commit preserved\n", Utf8NoBom);
        await GitSyncService.StagePathsAsync(authRepo, ["work.txt"]);
        var authCommit = await GitSyncService.CommitAsync(
            authRepo,
            new CommitExecutionOptions
            {
                Message = "本地提交不与远端认证混淆",
                ExpectedSnapshot = await QueryStagedSnapshot.CaptureAsync(authRepo),
            });
        var headBeforeAuthPush = (await GitInAsync(realGit, authRepo, "rev-parse", "HEAD")).Trim();
        var authRemotes = await new QueryRemotes(authRepo).GetResultAsync();
        var authOrigin = authRemotes.First(r => r.Name == "origin");

        var cancelScript = Path.Combine(suiteRoot, "ssh-cancel.sh");
        await File.WriteAllTextAsync(cancelScript, "#!/bin/sh\necho ASKPASS_CANCELLED >&2\nexit 128\n", Utf8NoBom);
        await GitInAsync(realGit, authRepo, "remote", "add", "sshremote", "ssh://git@127.0.0.1/org/repo.git");
        await GitInAsync(realGit, authRepo, "config", "--local", "core.sshCommand", $"sh \"{cancelScript.Replace('\\', '/')}\"");
        var sshRemote = (await new QueryRemotes(authRepo).GetResultAsync()).First(r => r.Name == "sshremote");

        var cancelFetchOrigin = await GitSyncService.FetchAsync(authRepo, [authOrigin], journalStore: authJournal);
        Check(!cancelFetchOrigin.Succeeded &&
              !cancelFetchOrigin.DiagnosticDetail.Contains(secretToken, StringComparison.Ordinal) &&
              !cancelFetchOrigin.DiagnosticDetail.Contains(secretHeader, StringComparison.Ordinal),
              "Fetch failure against credentialed HTTPS remote redacts secrets cleanly");

        var cancelPush = await GitSyncService.PushSingleBranchAsync(
            authRepo,
            new PushExecutionOptions { LocalBranch = "main", Remote = sshRemote, RemoteBranch = "main" },
            journalStore: authJournal);
        Check(!cancelPush.Succeeded && cancelPush.Outcome == PushOutcomeKind.AuthCanceled &&
              cancelPush.Summary.Contains("取消", StringComparison.Ordinal),
              "Authentication cancellation returns AuthCanceled outcome and explicit feedback");

        using var httpListener = new TcpListener(IPAddress.Loopback, 0);
        httpListener.Start();
        var port = ((IPEndPoint)httpListener.LocalEndpoint).Port;
        using var ctsListener = new CancellationTokenSource();
        var listenerTask = Task.Run(async () =>
        {
            while (!ctsListener.IsCancellationRequested)
            {
                try
                {
                    using var client = await httpListener.AcceptTcpClientAsync(ctsListener.Token);
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    _ = await stream.ReadAsync(buf, ctsListener.Token);
                    var resp = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 401 Unauthorized\r\n" +
                        "WWW-Authenticate: Basic realm=\"GitNestTest\"\r\n" +
                        "Content-Length: 0\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(resp, ctsListener.Token);
                }
                catch
                {
                    break;
                }
            }
        });

        try
        {
            await GitInAsync(realGit, authRepo, "config", "--local", "credential.helper", "");
            await GitInAsync(realGit, authRepo, "remote", "add", "localhttp", $"http://user:{secretToken}@127.0.0.1:{port}/org/repo.git");
            var httpRemote = (await new QueryRemotes(authRepo).GetResultAsync()).First(r => r.Name == "localhttp");

            var prevAskPass = Environment.GetEnvironmentVariable("GIT_TERMINAL_PROMPT");
            Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
            try
            {
                var httpAuthFail = await GitSyncService.PushSingleBranchAsync(
                    authRepo,
                    new PushExecutionOptions { LocalBranch = "main", Remote = httpRemote, RemoteBranch = "main" },
                    journalStore: authJournal);

                Check(authCommit.Succeeded &&
                      !httpAuthFail.Succeeded &&
                      httpAuthFail.Outcome is PushOutcomeKind.AuthFailed or PushOutcomeKind.AuthCanceled,
                      "Local HTTP 401 authentication failure returns explicit AuthFailed/AuthCanceled outcome");
                Check((await GitInAsync(realGit, authRepo, "rev-parse", "HEAD")).Trim() == headBeforeAuthPush &&
                      string.IsNullOrEmpty((await GitInAsync(realGit, authRepo, "status", "--short")).Trim()),
                      "Failed authenticated push leaves local commit HEAD and clean working tree untouched");

                var allJournalText = File.ReadAllText(authJournalFile);
                Check(allJournalText.Length > 0 &&
                      !allJournalText.Contains(secretToken, StringComparison.Ordinal) &&
                      !allJournalText.Contains(secretHeader, StringComparison.Ordinal) &&
                      !httpAuthFail.DiagnosticDetail.Contains(secretToken, StringComparison.Ordinal) &&
                      !httpAuthFail.Summary.Contains(secretToken, StringComparison.Ordinal),
                      "Sensitive credentials and extraheader tokens never appear in diagnostics or persisted operation logs");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", prevAskPass);
            }
        }
        finally
        {
            ctsListener.Cancel();
            httpListener.Stop();
            await listenerTask;
        }
    }
}
