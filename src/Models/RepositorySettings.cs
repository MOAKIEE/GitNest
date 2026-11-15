using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceGit.Models
{
    public class RepositorySettings
    {
        public string DefaultRemote
        {
            get;
            set;
        } = string.Empty;

        public int PreferredMergeMode
        {
            get;
            set;
        } = 0;

        public string ConventionalTypesOverride
        {
            get;
            set;
        } = string.Empty;

        public bool EnableRecursiveWhenAutoUpdatingSubmodules
        {
            get;
            set;
        } = true;

        public bool AskBeforeAutoUpdatingSubmodules
        {
            get;
            set;
        } = false;

        public static RepositorySettings Get(string gitCommonDir)
        {
            var fileInfo = new FileInfo(Path.Combine(gitCommonDir, "gitnest.settings"));
            var fullpath = fileInfo.FullName;
            if (_cache.TryGetValue(fullpath, out var setting))
                return setting;

            var hasLegacyCommitTemplates = false;
            if (!File.Exists(fullpath))
            {
                setting = new();
            }
            else
            {
                try
                {
                    var rawBytes = File.ReadAllBytes(fullpath);
                    setting = JsonSerializer.Deserialize(rawBytes, JsonCodeGen.Default.RepositorySettings) ?? new();
                    using var doc = JsonDocument.Parse(rawBytes);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (string.Equals(prop.Name, "CommitTemplates", StringComparison.OrdinalIgnoreCase))
                            {
                                hasLegacyCommitTemplates = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    setting = new();
                }
            }

            var formatted = JsonSerializer.Serialize(setting, JsonCodeGen.Default.RepositorySettings);
            setting._orgHash = HashContent(formatted);
            setting._pendingLegacyTemplateMigration = hasLegacyCommitTemplates;
            setting._file = fullpath;
            _cache[fullpath] = setting;
            return setting;
        }

        public void Save()
        {
            try
            {
                var content = JsonSerializer.Serialize(this, JsonCodeGen.Default.RepositorySettings);
                var hash = HashContent(content);
                if (!hash.Equals(_orgHash, StringComparison.Ordinal) || _pendingLegacyTemplateMigration)
                {
                    if (_pendingLegacyTemplateMigration && File.Exists(_file))
                    {
                        var backupFile = GetLegacyBackupPath(_file);
                        File.Copy(_file, backupFile, false);
                    }

                    var tmpfile = $"{_file}.tmp";
                    File.WriteAllText(tmpfile, content);
                    File.Move(tmpfile, _file, true);
                    _orgHash = hash;
                    _pendingLegacyTemplateMigration = false;
                }
            }
            catch
            {
                // Ignore save errors
            }
        }

        internal static void ClearCacheForRepository(string gitCommonDir)
        {
            if (string.IsNullOrWhiteSpace(gitCommonDir))
                return;

            var fullpath = new FileInfo(Path.Combine(gitCommonDir, "gitnest.settings")).FullName;
            _cache.Remove(fullpath);
        }

        private static string GetLegacyBackupPath(string settingsFile)
        {
            var candidate = settingsFile + ".bak";
            if (!File.Exists(candidate))
                return candidate;

            for (int i = 1; ; i++)
            {
                var alt = $"{settingsFile}.legacy-{i}.bak";
                if (!File.Exists(alt))
                    return alt;
            }
        }

        private static string HashContent(string source)
        {
            var hash = MD5.HashData(Encoding.Default.GetBytes(source));
            return Convert.ToHexStringLower(hash);
        }

        public List<string> UnprotectedBranches { get; set; } = new();

        public bool IsBranchProtectionDisabled(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName) || UnprotectedBranches == null || UnprotectedBranches.Count == 0)
                return false;

            var trimmed = branchName.Trim();
            foreach (var item in UnprotectedBranches)
            {
                if (string.Equals(item, trimmed, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public void SetBranchProtectionDisabled(string branchName, bool disabled)
        {
            if (string.IsNullOrWhiteSpace(branchName))
                return;

            UnprotectedBranches ??= new List<string>();
            var trimmed = branchName.Trim();
            UnprotectedBranches.RemoveAll(b => string.IsNullOrWhiteSpace(b) || string.Equals(b, trimmed, StringComparison.Ordinal));
            if (disabled)
                UnprotectedBranches.Add(trimmed);

            Save();
        }

        private static Dictionary<string, RepositorySettings> _cache = new();
        private string _file = string.Empty;
        private string _orgHash = string.Empty;
        private bool _pendingLegacyTemplateMigration = false;
    }
}
