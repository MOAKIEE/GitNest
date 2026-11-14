using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Safe ignore-rule inspection and persistence service for project `.gitignore` files
    /// and local-only `info/exclude` (resolved accurately for both main repos and linked worktrees).
    /// Handles literal special character escaping, line deduplication, `!` negation rule warnings,
    /// live Git engine match previews, and already-tracked file warnings without auto `git rm --cached`.
    /// </summary>
    public static class IgnoreRuleService
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        public static async Task<List<Models.IgnoreRuleTargetOption>> GetSupportedTargetsAsync(
            string repoPath,
            string selectedRelativePath = null,
            bool isDirectory = false,
            CancellationToken cancellationToken = default)
        {
            var targets = new List<Models.IgnoreRuleTargetOption>();
            if (string.IsNullOrWhiteSpace(repoPath))
                return targets;

            var repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
            var rootIgnore = Path.Combine(repoRoot, ".gitignore");
            targets.Add(new Models.IgnoreRuleTargetOption
            {
                Kind = Models.IgnoreRuleTargetKind.RootGitIgnore,
                DisplayName = ".gitignore (仓库根目录)",
                FullPath = rootIgnore,
                BaseRelativeDir = string.Empty,
                IsLocalOnly = false,
            });

            if (!string.IsNullOrWhiteSpace(selectedRelativePath))
            {
                var rel = NormalizeRelPath(selectedRelativePath);
                var subDir = isDirectory ? rel : GetUnixParentDir(rel);
                if (!string.IsNullOrEmpty(subDir))
                {
                    var subIgnore = Path.Combine(repoRoot, subDir.Replace('/', Path.DirectorySeparatorChar), ".gitignore");
                    targets.Add(new Models.IgnoreRuleTargetOption
                    {
                        Kind = Models.IgnoreRuleTargetKind.ParentGitIgnore,
                        DisplayName = $"{subDir}/.gitignore",
                        FullPath = subIgnore,
                        BaseRelativeDir = subDir,
                        IsLocalOnly = false,
                    });
                }
            }

            var excludeRes = await Command.RunGitDirectAsync(
                repoPath,
                cancellationToken,
                "rev-parse",
                "--path-format=absolute",
                "--git-path",
                "info/exclude").ConfigureAwait(false);

            var excludePath = excludeRes.IsSuccess && !string.IsNullOrWhiteSpace(excludeRes.StdOut)
                ? Path.GetFullPath(excludeRes.StdOut.Trim())
                : Path.Combine(repoRoot, ".git", "info", "exclude");

            targets.Add(new Models.IgnoreRuleTargetOption
            {
                Kind = Models.IgnoreRuleTargetKind.LocalExclude,
                DisplayName = ".git/info/exclude (仅本机，不进入提交)",
                FullPath = excludePath,
                BaseRelativeDir = string.Empty,
                IsLocalOnly = true,
            });

            return targets;
        }

        public static List<Models.IgnoreRulePresetOption> BuildPresetOptions(
            string selectedRelativePath,
            bool isDirectory,
            string targetBaseRelativeDir = "")
        {
            var presets = new List<Models.IgnoreRulePresetOption>();
            if (string.IsNullOrWhiteSpace(selectedRelativePath))
                return presets;

            var relToTarget = RebaseRelativePath(NormalizeRelPath(selectedRelativePath), targetBaseRelativeDir);
            if (string.IsNullOrEmpty(relToTarget))
                return presets;

            if (isDirectory)
            {
                var escapedDir = EscapeAnchoredPathPattern(relToTarget, appendTrailingSlash: true);
                presets.Add(new Models.IgnoreRulePresetOption
                {
                    Kind = Models.IgnoreRulePresetKind.ParentFolder,
                    Pattern = escapedDir,
                    DisplayLabel = $"忽略此目录 ({escapedDir})",
                });
                return presets;
            }

            var exactPattern = EscapeAnchoredPathPattern(relToTarget, appendTrailingSlash: false);
            presets.Add(new Models.IgnoreRulePresetOption
            {
                Kind = Models.IgnoreRulePresetKind.ExactFile,
                Pattern = exactPattern,
                DisplayLabel = $"忽略此文件 ({exactPattern})",
            });

            var fileName = GetUnixFileName(relToTarget);
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && ext.Length > 1 && fileName.Length > ext.Length)
            {
                var extPattern = $"*{EscapeWildcardLiteralPart(ext)}";
                presets.Add(new Models.IgnoreRulePresetOption
                {
                    Kind = Models.IgnoreRulePresetKind.ByExtension,
                    Pattern = extPattern,
                    DisplayLabel = $"忽略所有 {ext} 文件 ({extPattern})",
                });

                var parentDir = GetUnixParentDir(relToTarget);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    var folderExtPattern = $"{EscapeAnchoredPathPattern(parentDir, appendTrailingSlash: true)}*{EscapeWildcardLiteralPart(ext)}";
                    presets.Add(new Models.IgnoreRulePresetOption
                    {
                        Kind = Models.IgnoreRulePresetKind.ByExtensionInFolder,
                        Pattern = folderExtPattern,
                        DisplayLabel = $"仅在当前目录忽略 {ext} ({folderExtPattern})",
                    });
                }
            }

            var folder = GetUnixParentDir(relToTarget);
            if (!string.IsNullOrEmpty(folder))
            {
                var dirPattern = EscapeAnchoredPathPattern(folder, appendTrailingSlash: true);
                presets.Add(new Models.IgnoreRulePresetOption
                {
                    Kind = Models.IgnoreRulePresetKind.ParentFolder,
                    Pattern = dirPattern,
                    DisplayLabel = $"忽略所在目录 ({dirPattern})",
                });
            }

            return presets;
        }

        public static string EscapeLiteralGitIgnoreSegment(string segment, bool isFirstSegment, bool isLastSegment)
        {
            if (string.IsNullOrEmpty(segment))
                return string.Empty;

            var sb = new StringBuilder(segment.Length + 4);
            for (var i = 0; i < segment.Length; i++)
            {
                var ch = segment[i];
                if (i == 0 && isFirstSegment && (ch == '#' || ch == '!' || ch == ' '))
                {
                    sb.Append('\\').Append(ch);
                    continue;
                }

                if (ch is '\\' or '*' or '?' or '[' or ']')
                {
                    sb.Append('\\').Append(ch);
                    continue;
                }

                if (i == segment.Length - 1 && isLastSegment && ch == ' ')
                {
                    sb.Append('\\').Append(' ');
                    continue;
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        public static string EscapeAnchoredPathPattern(string relativePath, bool appendTrailingSlash)
        {
            var normalized = NormalizeRelPath(relativePath);
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "/";

            var sb = new StringBuilder();
            sb.Append('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    sb.Append('/');

                //Leading '#' / '!' check applies only if pattern weren't anchored with '/'; keep literal char escaping consistent.
                sb.Append(EscapePathSegmentBody(parts[i], isLastSegment: i == parts.Length - 1 && !appendTrailingSlash));
            }

            if (appendTrailingSlash)
                sb.Append('/');

            return sb.ToString();
        }

        public static async Task<Models.IgnoreRuleInspection> InspectAsync(
            string repoPath,
            string pattern,
            Models.IgnoreRuleTargetOption target,
            CancellationToken cancellationToken = default)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.FullPath))
            {
                return new Models.IgnoreRuleInspection
                {
                    IsValid = false,
                    ValidationError = "未指定有效的忽略规则目标文件。",
                };
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                return new Models.IgnoreRuleInspection
                {
                    IsValid = false,
                    RawPattern = pattern ?? string.Empty,
                    TargetFilePath = target.FullPath,
                    IsLocalOnly = target.IsLocalOnly,
                    ValidationError = "忽略规则不能为空。",
                };
            }

            if (pattern.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                return new Models.IgnoreRuleInspection
                {
                    IsValid = false,
                    RawPattern = pattern,
                    TargetFilePath = target.FullPath,
                    IsLocalOnly = target.IsLocalOnly,
                    ValidationError = "单条忽略规则不能包含换行符或空字符。",
                };
            }

            var effectivePattern = TrimUnescapedLeadingNewlines(pattern);
            if (string.IsNullOrWhiteSpace(effectivePattern))
            {
                return new Models.IgnoreRuleInspection
                {
                    IsValid = false,
                    RawPattern = pattern,
                    TargetFilePath = target.FullPath,
                    IsLocalOnly = target.IsLocalOnly,
                    ValidationError = "忽略规则不能为空白字符。",
                };
            }

            var existingLines = await ReadExistingLinesSafeAsync(target.FullPath, cancellationToken).ConfigureAwait(false);
            var alreadyExists = existingLines.Any(line => !IsCommentOrEmpty(line) && LineMatchesPattern(line, effectivePattern));
            var negations = FindPotentialNegationConflicts(existingLines, effectivePattern);

            var workingDir = string.IsNullOrWhiteSpace(target.BaseRelativeDir)
                ? repoPath
                : Path.Combine(repoPath, target.BaseRelativeDir.Replace('/', Path.DirectorySeparatorChar));

            var matchedUntracked = new List<string>();
            var matchedTracked = new List<string>();

            if (Directory.Exists(workingDir))
            {
                var tempExclude = await CommandArgumentHelper.WriteUtf8TempFileAsync(effectivePattern + "\n", cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var untrackedRes = await Command.RunGitDirectAsync(
                        workingDir,
                        TimeSpan.FromSeconds(15),
                        cancellationToken,
                        "--no-pager",
                        "-c",
                        "core.quotepath=off",
                        "ls-files",
                        "--others",
                        "--ignored",
                        $"--exclude-from={tempExclude}",
                        "-z",
                        "--",
                        ".").ConfigureAwait(false);

                    if (untrackedRes.IsSuccess && !string.IsNullOrEmpty(untrackedRes.StdOut))
                    {
                        matchedUntracked = untrackedRes.StdOut
                            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => PrefixWithBaseDir(target.BaseRelativeDir, NormalizeRelPath(p)))
                            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                            .ToList();
                    }

                    var trackedRes = await Command.RunGitDirectAsync(
                        workingDir,
                        TimeSpan.FromSeconds(15),
                        cancellationToken,
                        "--no-pager",
                        "-c",
                        "core.quotepath=off",
                        "ls-files",
                        "--cached",
                        "--ignored",
                        $"--exclude-from={tempExclude}",
                        "-z",
                        "--",
                        ".").ConfigureAwait(false);

                    if (trackedRes.IsSuccess && !string.IsNullOrEmpty(trackedRes.StdOut))
                    {
                        matchedTracked = trackedRes.StdOut
                            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => PrefixWithBaseDir(target.BaseRelativeDir, NormalizeRelPath(p)))
                            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                            .ToList();
                    }
                }
                finally
                {
                    CommandArgumentHelper.TryDeleteTempFile(tempExclude);
                }
            }

            var warnings = new List<string>();
            if (alreadyExists)
                warnings.Add("目标文件已包含相同规则，写入时将自动去重跳过。");

            if (negations.Count > 0)
                warnings.Add($"目标文件中已存在反选否定规则（如 {negations[0]}），该规则可能与本条规则互相抵消或受顺序影响。");

            if (matchedTracked.Count > 0)
                warnings.Add($"警告：有 {matchedTracked.Count} 个匹配文件已被 Git 跟踪（如 {matchedTracked[0]}）；写入忽略规则不会将其移出版本控制，如需取消跟踪请手动确认后操作（GitNest 不会自动执行 git rm --cached）。");

            return new Models.IgnoreRuleInspection
            {
                IsValid = true,
                RawPattern = pattern,
                EffectivePattern = effectivePattern,
                TargetFilePath = target.FullPath,
                IsLocalOnly = target.IsLocalOnly,
                AlreadyExists = alreadyExists,
                NegationConflicts = negations,
                MatchedUntrackedPaths = matchedUntracked,
                MatchedTrackedPaths = matchedTracked,
                Warnings = warnings,
            };
        }

        public static async Task<Models.IgnoreRuleApplyResult> ApplyAsync(
            string repoPath,
            string pattern,
            Models.IgnoreRuleTargetOption target,
            Models.OperationJournalStore journalStore = null,
            Models.ICommandLog outerLog = null,
            CancellationToken cancellationToken = default)
        {
            var inspection = await InspectAsync(repoPath, pattern, target, cancellationToken).ConfigureAwait(false);
            if (!inspection.IsValid)
            {
                return new Models.IgnoreRuleApplyResult
                {
                    Success = false,
                    TargetFilePath = target?.FullPath ?? string.Empty,
                    Inspection = inspection,
                    Summary = inspection.ValidationError,
                };
            }

            if (inspection.AlreadyExists)
            {
                return new Models.IgnoreRuleApplyResult
                {
                    Success = true,
                    SkippedDuplicate = true,
                    WrittenPattern = inspection.EffectivePattern,
                    TargetFilePath = inspection.TargetFilePath,
                    Inspection = inspection,
                    Summary = $"忽略规则 \"{inspection.EffectivePattern}\" 已存在于 {target.DisplayName}，已自动跳过重复写入。",
                };
            }

            var exec = await OperationJournalService.ExecuteGuardedWriteAsync(
                repoPath: repoPath,
                kind: Models.GitOperationKind.Stash, // Tracked under repository journal without modifying refs
                title: $"添加忽略规则至 {target.DisplayName}",
                checkIndexLock: false,
                action: async ctx =>
                {
                    var fullTarget = Path.GetFullPath(inspection.TargetFilePath);
                    var dir = Path.GetDirectoryName(fullTarget);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var existingContent = File.Exists(fullTarget)
                        ? await File.ReadAllTextAsync(fullTarget, cancellationToken).ConfigureAwait(false)
                        : string.Empty;

                    var lineEnding = existingContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                    var sb = new StringBuilder(existingContent);
                    if (sb.Length > 0 && sb[^1] != '\n')
                        sb.Append(lineEnding);
                    sb.Append(inspection.EffectivePattern).Append(lineEnding);

                    var tempFile = fullTarget + $".tmp-{Guid.NewGuid():N}";
                    await File.WriteAllTextAsync(tempFile, sb.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);
                    if (File.Exists(fullTarget))
                        File.Replace(tempFile, fullTarget, null, ignoreMetadataErrors: true);
                    else
                        File.Move(tempFile, fullTarget);

                    ctx.Log.AppendLine($"[IgnoreRule] 已写入 '{inspection.EffectivePattern}' -> {fullTarget}");
                    var msg = $"已将 \"{inspection.EffectivePattern}\" 写入 {target.DisplayName}。";

                    return new OperationActionOutcome<Models.IgnoreRuleApplyResult>
                    {
                        Stage = Models.GitOperationStage.Succeeded,
                        Summary = msg,
                        Payload = new Models.IgnoreRuleApplyResult
                        {
                            Success = true,
                            SkippedDuplicate = false,
                            WrittenPattern = inspection.EffectivePattern,
                            TargetFilePath = fullTarget,
                            Inspection = inspection,
                            Summary = msg,
                        },
                    };
                },
                journalStore: journalStore,
                outerLog: outerLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return exec.Payload ?? new Models.IgnoreRuleApplyResult
            {
                Success = exec.Succeeded,
                TargetFilePath = inspection.TargetFilePath,
                Inspection = inspection,
                Summary = exec.Summary,
            };
        }

        private static string EscapePathSegmentBody(string segment, bool isLastSegment)
        {
            var sb = new StringBuilder(segment.Length + 4);
            for (var i = 0; i < segment.Length; i++)
            {
                var ch = segment[i];
                if (ch is '\\' or '*' or '?' or '[' or ']')
                {
                    sb.Append('\\').Append(ch);
                }
                else if (i == segment.Length - 1 && isLastSegment && ch == ' ')
                {
                    sb.Append('\\').Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private static string EscapeWildcardLiteralPart(string part)
        {
            var sb = new StringBuilder(part.Length + 2);
            foreach (var ch in part)
            {
                if (ch is '\\' or '[' or ']' or '*' or '?')
                    sb.Append('\\').Append(ch);
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static async Task<List<string>> ReadExistingLinesSafeAsync(string filePath, CancellationToken ct)
        {
            if (!File.Exists(filePath))
                return [];

            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return text.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
        }

        private static bool IsCommentOrEmpty(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return true;

            return line.TrimStart().StartsWith('#');
        }

        private static bool LineMatchesPattern(string existingLine, string candidate)
        {
            if (string.Equals(existingLine, candidate, StringComparison.Ordinal))
                return true;

            // Only strip unescaped trailing spaces unless trailing space was escaped with "\ ".
            var normExisting = TrimUnescapedTrailingSpaces(existingLine);
            var normCandidate = TrimUnescapedTrailingSpaces(candidate);
            return string.Equals(normExisting, normCandidate, StringComparison.Ordinal);
        }

        private static string TrimUnescapedTrailingSpaces(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var end = line.Length;
            while (end > 0 && line[end - 1] == ' ')
            {
                if (end >= 2 && line[end - 2] == '\\')
                    break;
                end--;
            }

            return line[..end];
        }

        private static List<string> FindPotentialNegationConflicts(List<string> lines, string candidate)
        {
            var conflicts = new List<string>();
            var candidateCore = candidate.TrimStart('!', '/');
            var candidateName = GetUnixFileName(candidateCore);
            var candidateExt = Path.GetExtension(candidateName);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;

                if (!line.StartsWith('!'))
                    continue;

                var negatedBody = line[1..].TrimStart('/');
                if (string.Equals(negatedBody, candidateCore, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(negatedBody, candidateName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(candidateExt) && negatedBody.Contains(candidateExt, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(candidateName) && candidateName != "*" && negatedBody.Contains(candidateName, StringComparison.OrdinalIgnoreCase)))
                {
                    conflicts.Add(line);
                }
            }

            return conflicts;
        }

        private static string TrimUnescapedLeadingNewlines(string pattern)
        {
            var trimmed = pattern.TrimStart('\r', '\n');
            return TrimUnescapedTrailingSpaces(trimmed.TrimEnd('\r', '\n'));
        }

        private static string RebaseRelativePath(string relPath, string baseRelDir)
        {
            if (string.IsNullOrWhiteSpace(baseRelDir))
                return relPath;

            var cleanBase = NormalizeRelPath(baseRelDir);
            var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (relPath.StartsWith(cleanBase + "/", comp))
                return relPath[(cleanBase.Length + 1)..];

            return string.Empty;
        }

        private static string PrefixWithBaseDir(string baseRelDir, string path)
        {
            if (string.IsNullOrWhiteSpace(baseRelDir))
                return path;

            var cleanBase = NormalizeRelPath(baseRelDir);
            var cleanPath = NormalizeRelPath(path);
            return string.IsNullOrEmpty(cleanPath) ? cleanBase : $"{cleanBase}/{cleanPath}";
        }

        private static string NormalizeRelPath(string path) => (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

        private static string GetUnixParentDir(string relPath)
        {
            var idx = relPath.LastIndexOf('/');
            return idx > 0 ? relPath[..idx] : string.Empty;
        }

        private static string GetUnixFileName(string relPath)
        {
            var idx = relPath.LastIndexOf('/');
            return idx >= 0 ? relPath[(idx + 1)..] : relPath;
        }
    }
}
