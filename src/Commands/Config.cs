using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Config : Command
    {
        public Config(string repository)
        {
            if (string.IsNullOrEmpty(repository))
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else
            {
                WorkingDirectory = repository;
                Context = repository;
                _isLocal = true;
            }
        }

        public Dictionary<string, string> ReadAll()
        {
            Args = "config -l";

            var output = ReadToEnd();
            var rs = new Dictionary<string, string>();
            if (output.IsSuccess)
            {
                var lines = output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                        rs[parts[0]] = parts[1];
                }
            }

            return rs;
        }

        public async Task<Dictionary<string, string>> ReadAllAsync()
        {
            Args = "config -l";

            var output = await ReadToEndAsync().ConfigureAwait(false);
            var rs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (output.IsSuccess)
            {
                var lines = output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0];
                        var value = parts[1];
                        rs[key] = value;
                    }
                }
            }

            return rs;
        }

        public string Get(string key)
        {
            Args = $"config {key}";
            return ReadToEnd().StdOut.Trim();
        }

        public async Task<Models.CommitIdentity> ReadCommitIdentityAsync()
        {
            var scope = _isLocal ? string.Empty : "--global ";
            Args = $"config {scope}--includes --null --get user.name";
            var name = await ReadToEndAsync().ConfigureAwait(false);
            Args = $"config {scope}--includes --null --get user.email";
            var email = await ReadToEndAsync().ConfigureAwait(false);

            // Exit code 1 means an unset key; other failures must not appear as a missing identity.
            return new Models.CommitIdentity
            {
                Name = name.IsSuccess ? name.StdOut.TrimEnd('\0') : string.Empty,
                Email = email.IsSuccess ? email.StdOut.TrimEnd('\0') : string.Empty,
                IsReadable = (name.IsSuccess || name.ExitCode == 1) && (email.IsSuccess || email.ExitCode == 1),
            };
        }

        public async Task<string> GetAsync(string key)
        {
            Args = $"config {key}";

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return rs.StdOut.Trim();
        }

        public async Task<bool> SetAsync(string key, string value, bool allowEmpty = false)
        {
            var scope = _isLocal ? "--local" : "--global";

            if (!allowEmpty && string.IsNullOrWhiteSpace(value))
                Args = $"config {scope} --unset {key}";
            else
                Args = $"config {scope} {key} {value.Quoted()}";

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<Models.GitConfigSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var gitExecutable = Native.OS.GitExecutable;
            if (string.IsNullOrWhiteSpace(gitExecutable) || !File.Exists(gitExecutable))
                return new Models.GitConfigSnapshot { IsReadable = false, Error = "Git executable not found." };

            if (string.IsNullOrWhiteSpace(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
                return new Models.GitConfigSnapshot { IsReadable = false, Error = "Working directory does not exist." };

            var topLevel = string.Empty;
            var gitDir = string.Empty;
            var commonDir = string.Empty;
            var localConfigPath = string.Empty;
            var worktreeConfigPath = string.Empty;
            var currentBranch = string.Empty;

            if (_isLocal)
            {
                var identity = await RunGitDirectAsync(WorkingDirectory, cancellationToken,
                    "rev-parse", "--path-format=absolute", "--is-bare-repository", "--git-dir", "--git-common-dir", "--git-path", "config", "--git-path", "config.worktree").ConfigureAwait(false);
                if (!identity.IsSuccess)
                    return new Models.GitConfigSnapshot { IsReadable = false, Error = Models.GitConfigSnapshot.RedactText(identity.StdErr.Trim()) };

                var lines = identity.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 5)
                    return new Models.GitConfigSnapshot { IsReadable = false, Error = "Unable to resolve repository configuration paths." };

                var isBare = lines[0].Equals("true", StringComparison.OrdinalIgnoreCase);
                gitDir = NormalizePath(lines[1]);
                commonDir = NormalizePath(lines[2]);
                localConfigPath = NormalizePath(lines[3]);
                worktreeConfigPath = NormalizePath(lines[4]);

                if (!isBare)
                {
                    var top = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "rev-parse", "--path-format=absolute", "--show-toplevel").ConfigureAwait(false);
                    if (top.IsSuccess)
                        topLevel = NormalizePath(top.StdOut.Trim());
                }

                var branch = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "symbolic-ref", "-q", "--short", "HEAD").ConfigureAwait(false);
                if (branch.IsSuccess)
                    currentBranch = branch.StdOut.Trim();
            }

            // Invoke git directly without Command.CreateGitStartInfo's synthetic -c overrides so runtime
            // environment overrides remain authentic and credential.helper is not masked.
            var listResult = await RunGitDirectAsync(WorkingDirectory, cancellationToken,
                "config", "--list", "--includes", "--show-origin", "--show-scope", "--null").ConfigureAwait(false);
            if (!listResult.IsSuccess)
                return new Models.GitConfigSnapshot { IsReadable = false, Error = Models.GitConfigSnapshot.RedactText(listResult.StdErr.Trim()) };

            var snapshot = new Models.GitConfigSnapshot
            {
                IsReadable = true,
                WorkingDirectory = NormalizePath(WorkingDirectory),
                TopLevel = topLevel,
                GitDir = gitDir,
                CommonDir = commonDir,
                LocalConfigPath = localConfigPath,
                WorktreeConfigPath = worktreeConfigPath,
                CurrentBranch = currentBranch,
            };

            if (!ParseEntries(listResult.StdOut, snapshot, out var parseError))
                return new Models.GitConfigSnapshot { IsReadable = false, Error = parseError };

            var worktreeConfigEnabled = snapshot.GetEffectiveBool("extensions.worktreeconfig") == true;
            var finalSnapshot = new Models.GitConfigSnapshot
            {
                IsReadable = true,
                WorkingDirectory = snapshot.WorkingDirectory,
                TopLevel = snapshot.TopLevel,
                GitDir = snapshot.GitDir,
                CommonDir = snapshot.CommonDir,
                LocalConfigPath = snapshot.LocalConfigPath,
                WorktreeConfigPath = snapshot.WorktreeConfigPath,
                CurrentBranch = snapshot.CurrentBranch,
                WorktreeConfigEnabled = worktreeConfigEnabled,
            };
            finalSnapshot.Entries.AddRange(snapshot.Entries);

            CollectFileFingerprints(finalSnapshot);
            return finalSnapshot;
        }

        public async Task<Result> SetScopedAsync(
            Models.GitConfigWriteScope scope,
            string key,
            string value,
            bool unset = false,
            bool allowEmpty = false,
            CancellationToken cancellationToken = default)
        {
            var gitExecutable = Native.OS.GitExecutable;
            if (string.IsNullOrWhiteSpace(gitExecutable) || !File.Exists(gitExecutable))
                return Result.Failed("Git executable not found.");

            if (string.IsNullOrWhiteSpace(key) || key.StartsWith('-'))
                return Result.Failed("Invalid configuration key.");

            if (scope != Models.GitConfigWriteScope.Global && !_isLocal)
                return Result.Failed("Repository directory is required for local or worktree scope.");

            if (scope == Models.GitConfigWriteScope.Worktree)
            {
                var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (!snapshot.IsReadable)
                    return Result.Failed(string.IsNullOrEmpty(snapshot.Error) ? "Unable to verify worktree config status." : snapshot.Error);
                if (!snapshot.WorktreeConfigEnabled)
                    return Result.Failed("extensions.worktreeConfig is not enabled for this repository.");
            }

            var scopeFlag = scope switch
            {
                Models.GitConfigWriteScope.Local => "--local",
                Models.GitConfigWriteScope.Worktree => "--worktree",
                Models.GitConfigWriteScope.Global => "--global",
                _ => throw new ArgumentOutOfRangeException(nameof(scope)),
            };

            if (unset || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            {
                var unsetResult = await RunGitDirectAsync(WorkingDirectory, cancellationToken, "config", scopeFlag, "--unset", "--", key).ConfigureAwait(false);
                // Exit code 5 means key was not present in that scope (unless multiple values existed).
                if (unsetResult.ExitCode == 5 && !unsetResult.StdErr.Contains("multiple values", StringComparison.OrdinalIgnoreCase))
                    unsetResult.IsSuccess = true;
                return RedactResult(unsetResult);
            }

            return RedactResult(await RunGitDirectAsync(WorkingDirectory, cancellationToken, "config", scopeFlag, "--", key, value ?? string.Empty).ConfigureAwait(false));
        }

        public static Models.GitConfigSigningStatus InspectSigningStatus(Models.GitConfigSnapshot snapshot, Models.GitInstallation gitInstallation)
        {
            var commitSign = snapshot.GetEffectiveBool("commit.gpgsign") == true;
            var tagSign = snapshot.GetEffectiveBool("tag.gpgsign") == true;
            var formatRaw = snapshot.GetEffectiveValue("gpg.format", "openpgp").Trim();
            var format = string.IsNullOrEmpty(formatRaw) ? "openpgp" : formatRaw.ToLowerInvariant();
            var key = snapshot.GetEffectiveValue("user.signingkey").Trim();
            var allowedSigners = snapshot.GetEffectiveValue("gpg.ssh.allowedsignersfile").Trim();
            var program = format switch
            {
                "ssh" => snapshot.GetEffectiveValue("gpg.ssh.program").Trim(),
                "x509" => snapshot.GetEffectiveValue("gpg.x509.program").Trim(),
                "openpgp" => FirstNonEmpty(snapshot.GetEffectiveValue("gpg.openpgp.program").Trim(), snapshot.GetEffectiveValue("gpg.program").Trim()),
                _ => string.Empty,
            };

            var defaultTool = format switch
            {
                "openpgp" => "gpg",
                "x509" => "gpgsm",
                "ssh" => "ssh-keygen",
                _ => string.Empty,
            };

            var issueKey = string.Empty;
            var issueDetail = string.Empty;

            if (format is not ("openpgp" or "x509" or "ssh"))
            {
                issueKey = "Configure.Signing.UnsupportedFormat";
                issueDetail = formatRaw;
            }
            else if (format == "ssh" && gitInstallation is { Version: var ver } && ver < new System.Version(2, 34, 0))
            {
                issueKey = "Configure.Signing.SshRequiresNewerGit";
                issueDetail = gitInstallation.VersionText;
            }
            else if (string.IsNullOrWhiteSpace(key))
            {
                issueKey = "Configure.Signing.MissingKey";
            }
            else
            {
                var toolToCheck = string.IsNullOrEmpty(program) ? defaultTool : program;
                if (!CanResolveExecutable(toolToCheck, gitInstallation?.Executable))
                {
                    issueKey = "Configure.Signing.MissingProgram";
                    issueDetail = toolToCheck;
                }
            }

            return new Models.GitConfigSigningStatus
            {
                CommitSigningEnabled = commitSign,
                TagSigningEnabled = tagSign,
                Format = format,
                SigningKey = key,
                Program = program,
                AllowedSignersFile = allowedSigners,
                IsReady = string.IsNullOrEmpty(issueKey),
                IssueKey = issueKey,
                IssueDetail = issueDetail,
            };
        }

        private static bool ParseEntries(string rawOutput, Models.GitConfigSnapshot snapshot, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(rawOutput))
                return true;

            var tokens = rawOutput.Split('\0');
            if ((tokens.Length - 1) % 3 != 0 || !string.IsNullOrEmpty(tokens[^1]))
            {
                error = "Unexpected Git configuration output format.";
                return false;
            }

            var includeTargets = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var baseDir = !string.IsNullOrEmpty(snapshot.TopLevel)
                ? snapshot.TopLevel
                : !string.IsNullOrEmpty(snapshot.GitDir)
                    ? snapshot.GitDir
                    : snapshot.WorkingDirectory;

            for (var i = 0; i + 2 < tokens.Length; i += 3)
            {
                var scope = ParseScope(tokens[i]);
                var origin = tokens[i + 1];
                var payload = tokens[i + 2];

                var nl = payload.IndexOf('\n');
                var hasValue = nl >= 0;
                var key = hasValue ? payload.Substring(0, nl) : payload;
                var value = hasValue ? payload.Substring(nl + 1) : string.Empty;
                var normalizedKey = Models.GitConfigSnapshot.NormalizeKey(key);
                var originPath = ResolveOriginPath(origin, baseDir);

                var includeDirective = string.Empty;
                var isIncluded = !string.IsNullOrEmpty(originPath) &&
                    !PathsEqual(originPath, snapshot.LocalConfigPath) &&
                    !PathsEqual(originPath, snapshot.WorktreeConfigPath) &&
                    includeTargets.TryGetValue(originPath, out includeDirective);
                if (!isIncluded)
                    includeDirective = string.Empty;

                var displayValue = Models.GitConfigSnapshot.RedactValue(normalizedKey, value, hasValue, out var isSensitive);
                snapshot.Entries.Add(new Models.GitConfigEntry
                {
                    Key = key,
                    NormalizedKey = normalizedKey,
                    Value = value,
                    HasValue = hasValue,
                    Scope = scope,
                    Origin = origin,
                    OriginPath = originPath,
                    IsIncluded = isIncluded,
                    IncludeDirective = includeDirective,
                    IsSensitive = isSensitive,
                    DisplayValue = displayValue,
                });

                if (hasValue && IsIncludePathKey(normalizedKey) && !string.IsNullOrWhiteSpace(value))
                {
                    var declaringDir = !string.IsNullOrEmpty(originPath) ? Path.GetDirectoryName(originPath) : baseDir;
                    var resolvedTarget = ResolveIncludeTarget(value, declaringDir);
                    if (!string.IsNullOrEmpty(resolvedTarget))
                        includeTargets[resolvedTarget] = key;
                }
            }

            return true;
        }

        private static bool IsIncludePathKey(string normalizedKey)
        {
            return normalizedKey.Equals("include.path", StringComparison.Ordinal) ||
                   (normalizedKey.StartsWith("includeif.", StringComparison.Ordinal) && normalizedKey.EndsWith(".path", StringComparison.Ordinal));
        }

        private static string ResolveIncludeTarget(string rawTarget, string declaringDir)
        {
            var expanded = rawTarget.Trim();
            if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                expanded = Path.Combine(home, expanded.Substring(2));
            }
            else if (!Path.IsPathFullyQualified(expanded) && !string.IsNullOrEmpty(declaringDir))
            {
                expanded = Path.Combine(declaringDir, expanded);
            }

            try
            {
                return NormalizePath(Path.GetFullPath(expanded));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveOriginPath(string origin, string baseDir)
        {
            if (string.IsNullOrEmpty(origin) || !origin.StartsWith("file:", StringComparison.Ordinal))
                return string.Empty;

            var path = origin.Substring(5);
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            if (!Path.IsPathFullyQualified(path) && !string.IsNullOrEmpty(baseDir))
                path = Path.Combine(baseDir, path);

            try
            {
                return NormalizePath(Path.GetFullPath(path));
            }
            catch
            {
                return NormalizePath(path);
            }
        }

        private static Models.GitConfigScope ParseScope(string scope)
        {
            return scope switch
            {
                "system" => Models.GitConfigScope.System,
                "global" => Models.GitConfigScope.Global,
                "local" => Models.GitConfigScope.Local,
                "worktree" => Models.GitConfigScope.Worktree,
                "command" => Models.GitConfigScope.Command,
                "submodule" => Models.GitConfigScope.Submodule,
                _ => Models.GitConfigScope.Unknown,
            };
        }

        private static void CollectFileFingerprints(Models.GitConfigSnapshot snapshot)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(snapshot.LocalConfigPath))
                paths.Add(snapshot.LocalConfigPath);
            if (!string.IsNullOrEmpty(snapshot.WorktreeConfigPath))
                paths.Add(snapshot.WorktreeConfigPath);

            foreach (var entry in snapshot.Entries)
            {
                if (!string.IsNullOrEmpty(entry.OriginPath))
                    paths.Add(entry.OriginPath);
            }

            foreach (var path in paths)
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    var bytes = File.ReadAllBytes(path);
                    snapshot.FileFingerprints[path] = Convert.ToHexString(SHA256.HashData(bytes));
                }
                catch
                {
                    snapshot.FileFingerprints[path] = "UNREADABLE";
                }
            }
        }

        private static bool CanResolveExecutable(string tool, string gitExecutable)
        {
            if (string.IsNullOrWhiteSpace(tool))
                return false;

            if (Path.IsPathFullyQualified(tool))
                return File.Exists(tool);

            if (tool.Contains(Path.DirectorySeparatorChar) || tool.Contains(Path.AltDirectorySeparatorChar))
                return File.Exists(tool);

            var candidates = OperatingSystem.IsWindows() && !Path.HasExtension(tool)
                ? new[] { tool + ".exe", tool + ".cmd", tool + ".bat", tool }
                : [tool];

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var candidate in candidates)
                {
                    if (File.Exists(Path.Combine(dir, candidate)))
                        return true;
                }
            }

            if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(gitExecutable))
            {
                var gitDir = Path.GetDirectoryName(gitExecutable);
                var gitRoot = string.IsNullOrEmpty(gitDir) ? null : Directory.GetParent(gitDir)?.FullName;
                if (!string.IsNullOrEmpty(gitRoot))
                {
                    var usrBin = Path.Combine(gitRoot, "usr", "bin");
                    foreach (var candidate in candidates)
                    {
                        if (File.Exists(Path.Combine(usrBin, candidate)))
                            return true;
                    }
                }
            }

            return false;
        }

        private static string FirstNonEmpty(string first, string second) => !string.IsNullOrEmpty(first) ? first : second;

        private static string NormalizePath(string path) => string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return NormalizePath(a).Equals(NormalizePath(b), cmp);
        }

        private bool _isLocal = false;
    }
}
