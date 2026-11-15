using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed class RemotePushTarget
    {
        public string Url { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public bool IsValid => !string.IsNullOrEmpty(Url) && string.IsNullOrEmpty(Error);
        public string RedactedUrl => Models.GitConfigSnapshot.RedactText(Url);
        public static string BuildPushArguments(string pushUrl, string options, string refspec)
        {
            // A configured, process-local remote prevents URL matching from updating another remote's tracking refs.
            var config = CreatePushRemoteConfiguration(pushUrl, out var name);
            return $"-c {CommandArgumentHelper.Quote(config)} push {options} -- {name} {CommandArgumentHelper.Quote(refspec)}";
        }

        public static string CreatePushRemoteConfiguration(string pushUrl, out string remoteName)
        {
            remoteName = $"gitnest-push-{Guid.NewGuid():N}";
            return $"remote.{remoteName}.url={pushUrl}";
        }

        public static string Fingerprint(string url) => string.IsNullOrEmpty(url)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

        public static async Task<RemotePushTarget> ResolveAsync(string repoPath, string remoteName, CancellationToken cancellationToken)
        {
            var result = await Command.RunGitDirectAsync(repoPath, cancellationToken,
                "remote", "get-url", "--push", "--all", "--", remoteName).ConfigureAwait(false);
            if (!result.IsSuccess)
                return new RemotePushTarget { Error = Models.GitConfigSnapshot.RedactText(result.StdErr.Trim()) };

            var urls = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (urls.Length != 1)
                return new RemotePushTarget { Error = $"远端 {remoteName} 配置了 {urls.Length} 个推送地址；请在外部将本次目标配置为独立的单地址远端后重试。" };
            if (string.IsNullOrWhiteSpace(urls[0]) || urls[0].StartsWith('-'))
                return new RemotePushTarget { Error = "远端推送地址无效。" };

            return new RemotePushTarget { Url = urls[0] };
        }

        public static async Task<string> VerifyAsync(string repoPath, string remoteName, string confirmedUrl, CancellationToken cancellationToken)
        {
            var current = await ResolveAsync(repoPath, remoteName, cancellationToken).ConfigureAwait(false);
            if (!current.IsValid)
                return current.Error;
            return !string.IsNullOrEmpty(confirmedUrl) && string.Equals(current.Url, confirmedUrl, StringComparison.Ordinal)
                ? string.Empty
                : "远端推送地址在确认后已变化，请刷新并重新确认。";
        }

        public static async Task UpdateTrackingRefAsync(string repoPath, string remoteName, string branchName,
            string pushedSha, string expectedTrackingSha, string pushUrl, CancellationToken cancellationToken)
        {
            if (!await CanUpdateTrackingRefAsync(repoPath, remoteName, pushUrl, cancellationToken).ConfigureAwait(false))
                return;
            await Command.RunGitDirectAsync(repoPath, cancellationToken, "update-ref",
                $"refs/remotes/{remoteName}/{branchName}", pushedSha, expectedTrackingSha ?? string.Empty).ConfigureAwait(false);
        }

        public static async Task<bool> CanUpdateTrackingRefAsync(string repoPath, string remoteName,
            string pushUrl, CancellationToken cancellationToken)
        {
            var fetchUrl = await Command.RunGitDirectAsync(repoPath, cancellationToken,
                "remote", "get-url", "--", remoteName).ConfigureAwait(false);
            if (!fetchUrl.IsSuccess || !string.Equals(fetchUrl.StdOut.TrimEnd('\r', '\n'), pushUrl, StringComparison.Ordinal))
                return false;
            var fetch = await Command.RunGitDirectAsync(repoPath, cancellationToken,
                "config", "--get-all", "--", $"remote.{remoteName}.fetch").ConfigureAwait(false);
            var refspecs = fetch.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var standard = $"refs/heads/*:refs/remotes/{remoteName}/*";
            return fetch.IsSuccess && !Array.Exists(refspecs, r => r.StartsWith('^')) &&
                Array.Exists(refspecs, r => r.TrimStart('+') == standard);
        }
    }
}
