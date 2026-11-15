using System;
using System.Reflection;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class Version
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; }

        [JsonIgnore]
        public string CurrentVersionStr { get; }

        [JsonIgnore]
        public bool IsNewVersion => ReleaseVersion.TryCompare(TagName, CurrentVersionStr, out var comparison) && comparison > 0;

        [JsonIgnore]
        public string ReleaseDateStr => DateTimeFormat.Format(PublishedAt, true);

        public Version()
        {
            var assembly = Assembly.GetExecutingAssembly();
            CurrentVersionStr = $"v{assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
            foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (metadata.Key == "FriendlyVersion" && ReleaseVersion.TryCompare(metadata.Value, metadata.Value, out _))
                {
                    CurrentVersionStr = metadata.Value;
                    break;
                }
            }
        }
    }

    public class AlreadyUpToDate;

    public class SelfUpdateFailed
    {
        public string Reason
        {
            get;
            private set;
        }

        public SelfUpdateFailed(Exception e)
        {
            if (e.InnerException is { } inner)
                Reason = inner.Message;
            else
                Reason = e.Message;
        }
    }
}
