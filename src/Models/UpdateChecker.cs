using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    internal static class UpdateChecker
    {
        public const string ReleasesUrl = "https://github.com/MOAKIEE/GitNest/releases";
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/MOAKIEE/GitNest/releases/latest";

        public static async Task<Version> ReadLatestAsync(HttpClient client, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd("GitNest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException(App.Text("SelfUpdate.Unavailable"));
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync(stream, JsonCodeGen.Default.Version, cancellationToken).ConfigureAwait(false);
            if (release == null || !ReleaseVersion.TryCompare(release.TagName, release.TagName, out _))
                throw new InvalidOperationException(App.Text("SelfUpdate.InvalidVersion"));
            return release;
        }
    }
}
