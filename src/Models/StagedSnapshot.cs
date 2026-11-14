using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public sealed class StagedChangeEntry
    {
        public string Path { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public ChangeState IndexState { get; init; } = ChangeState.None;
        public ChangeState WorkTreeState { get; init; } = ChangeState.None;

        public bool IsConflicted =>
            IndexState == ChangeState.Conflicted ||
            WorkTreeState == ChangeState.Conflicted ||
            (IndexState == ChangeState.Deleted && WorkTreeState == ChangeState.Deleted) ||
            (IndexState == ChangeState.Added && WorkTreeState == ChangeState.Added);

        public bool IsStaged => !IsConflicted && IndexState is not (ChangeState.None or ChangeState.Untracked);
        public bool IsUnstaged => IsConflicted || WorkTreeState != ChangeState.None;
        public bool IsPartiallyStaged => IsStaged && WorkTreeState != ChangeState.None;
    }

    /// <summary>
    /// Immutable point-in-time snapshot of HEAD, branch state, and index contents used to prevent
    /// committing unexpected index mutations caused by concurrent external processes.
    /// </summary>
    public sealed class StagedSnapshot
    {
        public string RepoPath { get; init; } = string.Empty;
        public string GitDir { get; init; } = string.Empty;
        public string GitCommonDir { get; init; } = string.Empty;
        public string HeadOid { get; init; } = string.Empty;
        public bool IsUnborn { get; init; }
        public bool IsDetachedHead { get; init; }
        public string CurrentBranchName { get; init; } = string.Empty;
        public string IndexFingerprint { get; init; } = string.Empty;
        public bool HasConflicts { get; init; }
        public IReadOnlyList<StagedChangeEntry> AllChanges { get; init; } = [];
        public IReadOnlyList<StagedChangeEntry> StagedChanges { get; init; } = [];
        public IReadOnlyList<StagedChangeEntry> UnstagedChanges { get; init; } = [];
        public IReadOnlyList<string> PartiallyStagedPaths { get; init; } = [];
        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public bool HasStagedChanges => StagedChanges.Count > 0;
        public bool HasUnstagedChanges => UnstagedChanges.Count > 0;
        public int TotalUncommittedCount => AllChanges.Count;

        public bool MatchesCommitBaseline(StagedSnapshot baseline)
        {
            if (baseline == null)
                return false;

            return IsUnborn == baseline.IsUnborn &&
                   IsDetachedHead == baseline.IsDetachedHead &&
                   string.Equals(HeadOid, baseline.HeadOid, StringComparison.Ordinal) &&
                   string.Equals(CurrentBranchName, baseline.CurrentBranchName, StringComparison.Ordinal) &&
                   string.Equals(IndexFingerprint, baseline.IndexFingerprint, StringComparison.Ordinal);
        }

        public IReadOnlyList<string> GetPartiallyStagedTargets(IEnumerable<string> targetPaths)
        {
            if (targetPaths == null || PartiallyStagedPaths.Count == 0)
                return [];

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var partialSet = new HashSet<string>(PartiallyStagedPaths, comparer);
            var matched = new List<string>();
            var seen = new HashSet<string>(comparer);

            foreach (var raw in targetPaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var normalized = raw.Replace('\\', '/');
                if (partialSet.Contains(normalized) && seen.Add(normalized))
                    matched.Add(normalized);
            }

            return matched;
        }
    }
}
