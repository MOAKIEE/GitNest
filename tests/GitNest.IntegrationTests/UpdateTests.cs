using System.Net;
using System.Net.Http;
using SourceGit.Models;

internal static partial class Program
{
    private static async Task RunUpdateTestsAsync()
    {
        (string Left, string Right, int Order)[] versions =
        [
            ("v0.1.0", "v0.1.0-dev", 1),
            ("v0.1.1", "0.1.0", 1),
            ("v0.2.0", "0.1.99", 1),
            ("0.1.0-dev", "v0.1.0", -1),
            ("v0.1.0-dev", "0.1.0-dev", 0),
            ("1.0.0+build.2", "1.0.0+build.1", 0),
            ("1.0.0-beta.10", "1.0.0-beta.2", 1),
            ("1.0.0-rc.1", "1.0.0-beta.11", 1),
            ("1.0.0-alpha.1", "1.0.0-alpha", 1),
            ("1.0.0-1", "1.0.0-alpha", -1),
            ("1.0.0-999999999999999999999", "1.0.0-10", 1),
        ];
        foreach (var (left, right, order) in versions)
            Check(ReleaseVersion.TryCompare(left, right, out var comparison) && Math.Sign(comparison) == order,
                  $"Release precedence: {left} compared with {right}");

        foreach (var invalid in new[] { "", "v", "release", "1.2", "01.2.3", "1.2.3.4", "1.2.3-", "1.2.3-alpha..1", "1.2.3-01", "1.2.3+", "1.2.3+a+b", "999999999999.0.0" })
            Check(!ReleaseVersion.TryCompare(invalid, "0.1.0", out _), $"Malformed release tag is rejected: '{invalid}'");

        var current = new SourceGit.Models.Version();
        Check(current.CurrentVersionStr == "v" + File.ReadAllText(Path.Combine(LocateSourceRepositoryRoot()!, "VERSION")).Trim(),
              "Update dialog shows the full project version including the prerelease suffix");

        using var successClient = new HttpClient(new UpdateResponseHandler(request =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/MOAKIEE/GitNest/releases/latest",
                  "Update check reads the latest GitNest release");
            Check(request.Headers.UserAgent.ToString() == "GitNest" &&
                  request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json") &&
                  request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2022-11-28",
                  "Update request carries GitHub API headers");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v0.2.0\",\"name\":\"GitNest 0.2.0\",\"body\":\"Changes\",\"published_at\":\"2026-11-15T00:00:00Z\"}")
            };
        }));
        var release = await UpdateChecker.ReadLatestAsync(successClient);
        Check(release.TagName == "v0.2.0" && release.Body == "Changes" && release.IsNewVersion,
              "GitHub release metadata produces a newer GitNest version");

        foreach (var (status, body) in new[]
        {
            (HttpStatusCode.NotFound, "{}"),
            (HttpStatusCode.Forbidden, "{\"message\":\"rate limit\"}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, "{\"tag_name\":\"bad-version\"}"),
            (HttpStatusCode.OK, "null"),
            (HttpStatusCode.OK, "not json"),
        })
        {
            using var client = new HttpClient(new UpdateResponseHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }));
            var failed = false;
            try { await UpdateChecker.ReadLatestAsync(client); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException) { failed = true; }
            Check(failed, $"Unavailable or malformed release cannot report up-to-date ({status}, {body})");
        }
    }

    private sealed class UpdateResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
