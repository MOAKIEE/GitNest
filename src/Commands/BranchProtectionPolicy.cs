using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Enforces default protection for main, master, develop, and configured remote default branches,
    /// while supporting explicit per-repository unlock settings stored under git common dir.
    /// </summary>
    public static class BranchProtectionPolicy
    {
        private static readonly HashSet<string> DefaultProtectedNames = new(StringComparer.Ordinal)
        {
            "main",
            "master",
            "develop",
        };

        public static async Task<Models.BranchProtectionStatus> InspectAsync(
            string repoPath,
            string branchName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return new Models.BranchProtectionStatus
                {
                    BranchName = string.Empty,
                    Summary = "未指定分支名称。",
                };
            }

            var trimmed = branchName.Trim();
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            var remoteDefaults = await ReadRemoteDefaultBranchMapAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var settings = string.IsNullOrWhiteSpace(commonDir) ? null : Models.RepositorySettings.Get(commonDir);

            return BuildStatus(trimmed, remoteDefaults, settings);
        }

        public static async Task<IReadOnlyDictionary<string, Models.BranchProtectionStatus>> InspectMultipleAsync(
            string repoPath,
            IEnumerable<string> branchNames,
            CancellationToken cancellationToken = default)
        {
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var remoteDefaults = await ReadRemoteDefaultBranchMapAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var settings = string.IsNullOrWhiteSpace(commonDir) ? null : Models.RepositorySettings.Get(commonDir);

            var map = new Dictionary<string, Models.BranchProtectionStatus>(StringComparer.Ordinal);
            if (branchNames == null)
                return map;

            foreach (var raw in branchNames)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var trimmed = raw.Trim();
                if (!map.ContainsKey(trimmed))
                    map[trimmed] = BuildStatus(trimmed, remoteDefaults, settings);
            }

            return map;
        }

        public static async Task<IReadOnlyList<Models.BranchProtectionStatus>> InspectProtectedCandidatesAsync(
            string repoPath,
            IEnumerable<string> branchNames,
            CancellationToken cancellationToken = default)
        {
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);
            var remoteDefaults = await ReadRemoteDefaultBranchMapAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var settings = string.IsNullOrWhiteSpace(commonDir) ? null : Models.RepositorySettings.Get(commonDir);

            var candidates = new HashSet<string>(DefaultProtectedNames, StringComparer.Ordinal);
            foreach (var name in remoteDefaults.Keys)
                candidates.Add(name);

            if (branchNames != null)
            {
                foreach (var raw in branchNames)
                {
                    if (!string.IsNullOrWhiteSpace(raw))
                        candidates.Add(raw.Trim());
                }
            }

            if (settings?.UnprotectedBranches != null)
            {
                foreach (var unlocked in settings.UnprotectedBranches)
                {
                    if (!string.IsNullOrWhiteSpace(unlocked))
                        candidates.Add(unlocked.Trim());
                }
            }

            return candidates
                .Select(name => BuildStatus(name, remoteDefaults, settings))
                .Where(status => status.IsProtectedByDefault)
                .OrderBy(status => status.BranchName, StringComparer.Ordinal)
                .ToList();
        }

        public static bool IsBranchEffectivelyProtectedFast(
            string gitCommonDir,
            Models.RepositorySettings settings,
            string localBranchName,
            string remoteBranchName = null)
        {
            return IsSingleBranchEffectivelyProtectedFast(gitCommonDir, settings, localBranchName) ||
                IsSingleBranchEffectivelyProtectedFast(gitCommonDir, settings, remoteBranchName);
        }

        private static bool IsSingleBranchEffectivelyProtectedFast(
            string gitCommonDir,
            Models.RepositorySettings settings,
            string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
                return false;

            var trimmed = branchName.Trim();
            var isProtected = DefaultProtectedNames.Contains(trimmed);
            if (!isProtected && !string.IsNullOrWhiteSpace(gitCommonDir))
                isProtected = IsRemoteHeadTargetFast(gitCommonDir, trimmed);

            if (!isProtected)
                return false;

            return !(settings?.IsBranchProtectionDisabled(trimmed) ?? false);
        }

        private static bool IsRemoteHeadTargetFast(string gitCommonDir, string branchName)
        {
            try
            {
                var remotesRoot = System.IO.Path.Combine(gitCommonDir, "refs", "remotes");
                if (!System.IO.Directory.Exists(remotesRoot))
                    return false;

                foreach (var remoteDir in System.IO.Directory.EnumerateDirectories(remotesRoot))
                {
                    var headPath = System.IO.Path.Combine(remoteDir, "HEAD");
                    if (!System.IO.File.Exists(headPath))
                        continue;

                    var content = System.IO.File.ReadAllText(headPath).Trim();
                    var remoteName = System.IO.Path.GetFileName(remoteDir);
                    var expected = $"ref: refs/remotes/{remoteName}/{branchName}";
                    if (string.Equals(content, expected, StringComparison.Ordinal))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        public static async Task<Models.BranchProtectionStatus> SetLocalOverrideAsync(
            string repoPath,
            string branchName,
            bool allowDestructiveHistoryRewrite,
            CancellationToken cancellationToken = default)
        {
            var validated = CommandArgumentHelper.ValidateNonOptionToken(branchName, nameof(branchName));
            var (_, commonDir) = await Models.RepositoryWriteGuard.ResolveGitDirectoriesAsync(repoPath, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(commonDir))
            {
                var settings = Models.RepositorySettings.Get(commonDir);
                settings.SetBranchProtectionDisabled(validated, allowDestructiveHistoryRewrite);
            }

            return await InspectAsync(repoPath, validated, cancellationToken).ConfigureAwait(false);
        }

        private static Models.BranchProtectionStatus BuildStatus(
            string branchName,
            Dictionary<string, List<string>> remoteDefaults,
            Models.RepositorySettings settings)
        {
            var isDefaultName = DefaultProtectedNames.Contains(branchName);
            remoteDefaults.TryGetValue(branchName, out var remotesForBranch);
            remotesForBranch ??= new List<string>();
            var isRemoteDefault = remotesForBranch.Count > 0;
            var isProtectedByDefault = isDefaultName || isRemoteDefault;
            var isUnlocked = isProtectedByDefault && (settings?.IsBranchProtectionDisabled(branchName) ?? false);

            var reasons = new List<string>();
            if (isDefaultName)
                reasons.Add($"常见主线保护分支名称（{branchName}）");
            if (isRemoteDefault)
                reasons.Add($"远端默认分支指向（{string.Join(", ", remotesForBranch.Select(r => $"{r}/HEAD"))}）");

            string summary;
            if (!isProtectedByDefault)
            {
                summary = $"分支 '{branchName}' 未命中默认保护规则。";
            }
            else if (isUnlocked)
            {
                summary = $"分支 '{branchName}' 原受保护（{string.Join("；", reasons)}），但当前仓库已显式关闭该分支的历史改写保护。";
            }
            else
            {
                summary = $"分支 '{branchName}' 受保护（{string.Join("；", reasons)}），默认禁止改写远程分支历史或强制回退；如需放行请在当前仓库设置中显式关闭保护。";
            }

            return new Models.BranchProtectionStatus
            {
                BranchName = branchName,
                IsDefaultProtectedName = isDefaultName,
                IsRemoteDefaultBranch = isRemoteDefault,
                DefaultBranchRemotes = remotesForBranch,
                IsLocallyUnlocked = isUnlocked,
                Reasons = reasons,
                Summary = summary,
            };
        }

        private static async Task<Dictionary<string, List<string>>> ReadRemoteDefaultBranchMapAsync(
            string repoPath,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var res = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "for-each-ref",
                "--format=%(refname)\t%(symref)",
                "refs/remotes").ConfigureAwait(false);

            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.StdOut))
                return result;

            var lines = res.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var tabIdx = line.IndexOf('\t');
                if (tabIdx <= 0)
                    continue;

                var refName = line[..tabIdx].Trim();
                var symref = line[(tabIdx + 1)..].Trim();
                if (string.IsNullOrEmpty(symref) || !refName.EndsWith("/HEAD", StringComparison.Ordinal))
                    continue;

                if (!refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
                    continue;

                var withoutRemotes = refName.Substring("refs/remotes/".Length);
                var slashIdx = withoutRemotes.IndexOf('/');
                if (slashIdx <= 0)
                    continue;

                var remoteName = withoutRemotes[..slashIdx];
                var expectedTargetPrefix = $"refs/remotes/{remoteName}/";
                if (!symref.StartsWith(expectedTargetPrefix, StringComparison.Ordinal))
                    continue;

                var targetBranch = symref.Substring(expectedTargetPrefix.Length);
                if (string.IsNullOrWhiteSpace(targetBranch) || targetBranch.Equals("HEAD", StringComparison.Ordinal))
                    continue;

                if (!result.TryGetValue(targetBranch, out var remoteList))
                {
                    remoteList = new List<string>();
                    result[targetBranch] = remoteList;
                }

                if (!remoteList.Contains(remoteName, StringComparer.Ordinal))
                    remoteList.Add(remoteName);
            }

            return result;
        }
    }
}
