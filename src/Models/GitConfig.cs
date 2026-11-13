using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SourceGit.Models
{
    public enum GitConfigScope
    {
        Unknown,
        System,
        Global,
        Local,
        Worktree,
        Command,
        Submodule,
    }

    public enum GitConfigWriteScope
    {
        Local,
        Worktree,
        Global,
    }

    public sealed partial class GitConfigEntry
    {
        public string Key { get; init; } = string.Empty;
        public string NormalizedKey { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public bool HasValue { get; init; } = true;
        public GitConfigScope Scope { get; init; } = GitConfigScope.Unknown;
        public string Origin { get; init; } = string.Empty;
        public string OriginPath { get; init; } = string.Empty;
        public bool IsIncluded { get; init; } = false;
        public string IncludeDirective { get; init; } = string.Empty;
        public bool IsRuntime => Scope == GitConfigScope.Command;
        public bool IsSensitive { get; init; } = false;
        public string DisplayValue { get; init; } = string.Empty;
    }

    public sealed class GitConfigRemote
    {
        public string Name { get; init; } = string.Empty;
        public List<GitConfigEntry> FetchUrls { get; } = [];
        public List<GitConfigEntry> PushUrls { get; } = [];
        public List<GitConfigEntry> FetchRefspecs { get; } = [];
        public GitConfigEntry SshKey { get; set; } = null;
        public GitConfigEntry DisableAutoFetch { get; set; } = null;
    }

    public sealed class GitConfigUrlProxy
    {
        public string Pattern { get; init; } = string.Empty;
        public GitConfigEntry Entry { get; init; } = null;
    }

    public sealed class GitConfigSigningStatus
    {
        public bool CommitSigningEnabled { get; init; }
        public bool TagSigningEnabled { get; init; }
        public string Format { get; init; } = "openpgp";
        public string SigningKey { get; init; } = string.Empty;
        public string Program { get; init; } = string.Empty;
        public string AllowedSignersFile { get; init; } = string.Empty;
        public bool IsReady { get; init; }
        public string IssueKey { get; init; } = string.Empty;
        public string IssueDetail { get; init; } = string.Empty;
    }

    public sealed partial class GitConfigSnapshot
    {
        public bool IsReadable { get; init; } = true;
        public string Error { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;
        public string TopLevel { get; init; } = string.Empty;
        public string GitDir { get; init; } = string.Empty;
        public string CommonDir { get; init; } = string.Empty;
        public string LocalConfigPath { get; init; } = string.Empty;
        public string WorktreeConfigPath { get; init; } = string.Empty;
        public string CurrentBranch { get; init; } = string.Empty;
        public bool WorktreeConfigEnabled { get; init; } = false;
        public List<GitConfigEntry> Entries { get; } = [];
        public Dictionary<string, string> FileFingerprints { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public IReadOnlyList<GitConfigEntry> GetEntries(string key)
        {
            var normalized = NormalizeKey(key);
            var list = new List<GitConfigEntry>();
            foreach (var entry in Entries)
            {
                if (entry.NormalizedKey.Equals(normalized, StringComparison.Ordinal))
                    list.Add(entry);
            }
            return list;
        }

        public IReadOnlyList<GitConfigEntry> GetEntriesInScope(string key, GitConfigScope scope, bool includeIncluded = false)
        {
            var normalized = NormalizeKey(key);
            var list = new List<GitConfigEntry>();
            foreach (var entry in Entries)
            {
                if (entry.Scope == scope &&
                    (includeIncluded || !entry.IsIncluded) &&
                    entry.NormalizedKey.Equals(normalized, StringComparison.Ordinal))
                    list.Add(entry);
            }
            return list;
        }

        public GitConfigEntry GetEffectiveEntry(string key)
        {
            var normalized = NormalizeKey(key);
            GitConfigEntry last = null;
            foreach (var entry in Entries)
            {
                if (entry.NormalizedKey.Equals(normalized, StringComparison.Ordinal))
                    last = entry;
            }
            return last;
        }

        public string GetEffectiveValue(string key, string fallback = "")
        {
            var entry = GetEffectiveEntry(key);
            if (entry == null)
                return fallback;
            return entry.HasValue ? entry.Value : "true";
        }

        public bool? GetEffectiveBool(string key)
        {
            var entry = GetEffectiveEntry(key);
            if (entry == null)
                return null;
            return TryParseGitBool(entry);
        }

        public IReadOnlyList<GitConfigEntry> GetEffectiveCredentialHelpers()
        {
            var effective = new List<GitConfigEntry>();
            foreach (var entry in GetEntries("credential.helper"))
            {
                // Git resets any previously accumulated credential helpers when it sees an empty value.
                if (entry.HasValue && entry.Value.Length == 0)
                {
                    effective.Clear();
                    continue;
                }

                effective.Add(entry);
            }
            return effective;
        }

        public IReadOnlyList<GitConfigRemote> GetRemotes()
        {
            var map = new Dictionary<string, GitConfigRemote>(StringComparer.Ordinal);
            GitConfigRemote GetOrCreate(string name)
            {
                if (!map.TryGetValue(name, out var remote))
                {
                    remote = new GitConfigRemote { Name = name };
                    map.Add(name, remote);
                }
                return remote;
            }

            foreach (var entry in Entries)
            {
                if (!TrySplitRemoteKey(entry.Key, out var name, out var field))
                    continue;

                var remote = GetOrCreate(name);
                if (field.Equals("url", StringComparison.OrdinalIgnoreCase))
                    remote.FetchUrls.Add(entry);
                else if (field.Equals("pushurl", StringComparison.OrdinalIgnoreCase))
                    remote.PushUrls.Add(entry);
                else if (field.Equals("fetch", StringComparison.OrdinalIgnoreCase))
                    remote.FetchRefspecs.Add(entry);
                else if (field.Equals("sshkey", StringComparison.OrdinalIgnoreCase))
                    remote.SshKey = entry;
                else if (field.Equals("disableautofetch", StringComparison.OrdinalIgnoreCase))
                    remote.DisableAutoFetch = entry;
            }

            var list = new List<GitConfigRemote>(map.Values);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return list;
        }

        public IReadOnlyList<GitConfigUrlProxy> GetUrlProxyOverrides()
        {
            var overrides = new Dictionary<string, GitConfigUrlProxy>(StringComparer.Ordinal);
            foreach (var entry in Entries)
            {
                if (TryExtractSubsection(entry.Key, "http", "proxy", out var pattern))
                {
                    overrides[pattern] = new GitConfigUrlProxy
                    {
                        Pattern = pattern,
                        Entry = entry,
                    };
                }
            }

            var list = new List<GitConfigUrlProxy>(overrides.Values);
            list.Sort((a, b) => string.Compare(a.Pattern, b.Pattern, StringComparison.Ordinal));
            return list;
        }

        public string BuildSanitizedDiagnostics()
        {
            var lines = new List<string>();
            foreach (var entry in Entries)
            {
                var valueText = entry.HasValue ? entry.DisplayValue : "(true)";
                var scopeText = entry.IsIncluded ? $"{entry.Scope}+include" : entry.Scope.ToString();
                var originText = string.IsNullOrEmpty(entry.OriginPath) ? entry.Origin : entry.OriginPath;
                lines.Add($"[{scopeText}] {entry.Key} = {valueText} ({originText})");
            }
            return string.Join(Environment.NewLine, lines);
        }

        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var firstDot = key.IndexOf('.');
            var lastDot = key.LastIndexOf('.');
            if (firstDot <= 0 || firstDot == lastDot)
                return key.ToLowerInvariant();

            var section = key.Substring(0, firstDot).ToLowerInvariant();
            var subsection = key.Substring(firstDot + 1, lastDot - firstDot - 1);
            var variable = key.Substring(lastDot + 1).ToLowerInvariant();
            return $"{section}.{subsection}.{variable}";
        }

        public static bool? TryParseGitBool(GitConfigEntry entry)
        {
            if (entry == null)
                return null;
            if (!entry.HasValue)
                return true;

            var v = entry.Value.Trim();
            if (v.Length == 0 || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                v == "0")
                return false;

            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                v == "1")
                return true;

            if (int.TryParse(v, out var num))
                return num != 0;

            return null;
        }

        public static bool IsSensitiveKey(string normalizedKey)
        {
            if (string.IsNullOrEmpty(normalizedKey))
                return false;

            if (normalizedKey.Equals("http.extraheader", StringComparison.Ordinal) ||
                (normalizedKey.StartsWith("http.", StringComparison.Ordinal) && normalizedKey.EndsWith(".extraheader", StringComparison.Ordinal)))
                return true;

            var lastDot = normalizedKey.LastIndexOf('.');
            var variable = lastDot >= 0 ? normalizedKey.Substring(lastDot + 1) : normalizedKey;
            return variable is "password" or "smtppass" or "token" or "secret" or "apikey" or "api-key" or "api_key" or "accesstoken" or "access-token" or "privatekey";
        }

        public static string RedactValue(string normalizedKey, string value, bool hasValue, out bool isSensitive)
        {
            if (!hasValue)
            {
                isSensitive = false;
                return "(true)";
            }

            if (IsSensitiveKey(normalizedKey))
            {
                isSensitive = value.Length > 0;
                return value.Length == 0 ? string.Empty : "***";
            }

            if (string.IsNullOrEmpty(value))
            {
                isSensitive = false;
                return string.Empty;
            }

            var redacted = REG_URL_USERINFO().Replace(value, "$1***@");
            isSensitive = !redacted.Equals(value, StringComparison.Ordinal);
            return redacted;
        }

        public static string RedactText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return REG_URL_USERINFO().Replace(text, "$1***@");
        }

        private static bool TrySplitRemoteKey(string key, out string remoteName, out string field)
        {
            remoteName = string.Empty;
            field = string.Empty;
            if (!key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase))
                return false;

            var lastDot = key.LastIndexOf('.');
            if (lastDot <= 7)
                return false;

            remoteName = key.Substring(7, lastDot - 7);
            field = key.Substring(lastDot + 1);
            return remoteName.Length > 0 && field.Length > 0;
        }

        private static bool TryExtractSubsection(string key, string section, string variable, out string subsection)
        {
            subsection = string.Empty;
            var prefix = section + ".";
            var suffix = "." + variable;
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                key.Length <= prefix.Length + suffix.Length)
                return false;

            subsection = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);
            return subsection.Length > 0;
        }

        [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.-]*://)([^\s/@]+)@")]
        private static partial Regex REG_URL_USERINFO();
    }
}
