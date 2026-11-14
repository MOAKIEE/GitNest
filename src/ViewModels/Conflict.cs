using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class ConflictFileListItem
    {
        public string Marker { get; init; } = "U";
        public string Path { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool IsSelected { get; init; }
    }

    public class Conflict : ObservableObject
    {
        public string ChangePath => _change?.Path ?? string.Empty;

        public string Marker => _change.ConflictMarker;

        public string Description => _change.ConflictDesc;

        public Models.ConflictFileState State
        {
            get => _state;
            private set => SetProperty(ref _state, value);
        }

        public object Theirs
        {
            get => _theirs;
            private set => SetProperty(ref _theirs, value);
        }

        public object Mine
        {
            get => _mine;
            private set => SetProperty(ref _mine, value);
        }

        public string OriginLabel
        {
            get => _originLabel;
            private set => SetProperty(ref _originLabel, value);
        }

        public bool IsGitNestRetainedOrigin
        {
            get => _isGitNestRetainedOrigin;
            private set => SetProperty(ref _isGitNestRetainedOrigin, value);
        }

        public bool IsExternalOrigin
        {
            get => _isExternalOrigin;
            private set => SetProperty(ref _isExternalOrigin, value);
        }

        public string Headline
        {
            get => _headline;
            private set => SetProperty(ref _headline, value);
        }

        public string Detail
        {
            get => _detail;
            private set
            {
                if (SetProperty(ref _detail, value))
                    OnPropertyChanged(nameof(HasDetail));
            }
        }

        public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);

        public string ActiveMarkersText
        {
            get => _activeMarkersText;
            private set
            {
                if (SetProperty(ref _activeMarkersText, value))
                    OnPropertyChanged(nameof(HasActiveMarkers));
            }
        }

        public bool HasActiveMarkers => !string.IsNullOrWhiteSpace(_activeMarkersText);

        public bool ShowExternalAbortDisabledNotice
        {
            get => _showExternalAbortDisabledNotice;
            private set => SetProperty(ref _showExternalAbortDisabledNotice, value);
        }

        public string ConflictedFilesHeader => App.Text("WorkingCopy.Conflicts.FilesHeader", _conflictedFiles.Count);

        public IReadOnlyList<ConflictFileListItem> ConflictedFiles
        {
            get => _conflictedFiles;
            private set
            {
                if (SetProperty(ref _conflictedFiles, value))
                {
                    OnPropertyChanged(nameof(HasConflictedFiles));
                    OnPropertyChanged(nameof(HasEmptyConflictedFiles));
                    OnPropertyChanged(nameof(ConflictedFilesHeader));
                }
            }
        }

        public bool HasConflictedFiles => _conflictedFiles.Count > 0;

        public bool HasEmptyConflictedFiles => _conflictedFiles.Count == 0;

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => SetProperty(ref _isRefreshing, value);
        }

        public Conflict(Repository repo, WorkingCopy wc, Models.Change change)
        {
            _repo = repo;
            _wc = wc;
            _change = change;

            SyncState(wc.InProgressInspection, wc.ConflictedChanges);

            Task.Run(async () =>
            {
                _head = await new Commands.QuerySingleCommit(repo.FullPath, "HEAD")
                    .GetResultAsync()
                    .ConfigureAwait(false);

                var (mine, theirs) = wc.InProgressContext switch
                {
                    CherryPickInProgress cherryPick => (_head, cherryPick.Head),
                    RebaseInProgress rebase => (rebase.Onto, rebase.StoppedAt),
                    AmInProgress => (_head, (object)"Patch"),
                    RevertInProgress revert => (_head, revert.Head),
                    MergeInProgress merge => (_head, merge.Source),
                    _ => (_head, (object)"Stash or Patch"),
                };

                if (theirs == null)
                {
                    if (wc.InProgressContext is MergeInProgress &&
                        System.IO.File.Exists(System.IO.Path.Combine(repo.GitDir, "MERGE_HEAD")))
                    {
                        theirs = await new Commands.QuerySingleCommit(repo.FullPath, "MERGE_HEAD")
                            .GetResultAsync()
                            .ConfigureAwait(false);
                    }
                    else if (wc.InProgressContext is RevertInProgress &&
                             System.IO.File.Exists(System.IO.Path.Combine(repo.GitDir, "REVERT_HEAD")))
                    {
                        theirs = await new Commands.QuerySingleCommit(repo.FullPath, "REVERT_HEAD")
                            .GetResultAsync()
                            .ConfigureAwait(false);
                    }
                    else if (wc.InProgressContext is CherryPickInProgress &&
                             System.IO.File.Exists(System.IO.Path.Combine(repo.GitDir, "CHERRY_PICK_HEAD")))
                    {
                        theirs = await new Commands.QuerySingleCommit(repo.FullPath, "CHERRY_PICK_HEAD")
                            .GetResultAsync()
                            .ConfigureAwait(false);
                    }
                }

                var state = Models.ConflictFileState.Unknown;
                if ((_change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified)
                    && !System.IO.Directory.Exists(System.IO.Path.Combine(_repo.FullPath, _change.Path)))
                {
                    state = await new Commands.QueryConflictFileState(repo.FullPath, change)
                        .GetResultAsync()
                        .ConfigureAwait(false);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    State = state;
                    Mine = mine;
                    Theirs = theirs;
                });
            });
        }

        public void SyncState(Models.RepositoryInProgressInspection inspection, IReadOnlyList<Models.Change> conflictedChanges)
        {
            if (inspection != null)
            {
                IsGitNestRetainedOrigin =
                    inspection.Origin is Models.ConflictOwnershipOrigin.GitNestOwnedClean or
                    Models.ConflictOwnershipOrigin.GitNestOwnedWithExternalIntervention;
                IsExternalOrigin = !IsGitNestRetainedOrigin;
                OriginLabel = IsGitNestRetainedOrigin
                    ? App.Text("WorkingCopy.Conflicts.Origin.GitNest")
                    : App.Text("WorkingCopy.Conflicts.Origin.External");
                Headline = string.IsNullOrWhiteSpace(inspection.Headline)
                    ? App.Text("WorkingCopy.Conflicts")
                    : inspection.Headline;
                Detail = inspection.Detail ?? string.Empty;

                var markerParts = new List<string>();
                if (inspection.ActiveMarkers.Count > 0)
                    markerParts.AddRange(inspection.ActiveMarkers);
                if (!string.IsNullOrWhiteSpace(inspection.MarkerTargetSha))
                {
                    var shortSha = inspection.MarkerTargetSha.Length <= 8
                        ? inspection.MarkerTargetSha
                        : inspection.MarkerTargetSha.Substring(0, 8);
                    markerParts.Add($"({shortSha})");
                }
                ActiveMarkersText = string.Join(" ", markerParts);
                ShowExternalAbortDisabledNotice = !inspection.CanOfferSafeAbort;
            }
            else
            {
                IsGitNestRetainedOrigin = false;
                IsExternalOrigin = true;
                OriginLabel = App.Text("WorkingCopy.Conflicts.Origin.External");
                Headline = App.Text("WorkingCopy.Conflicts");
                Detail = string.Empty;
                ActiveMarkersText = string.Empty;
                ShowExternalAbortDisabledNotice = true;
            }

            var items = new List<ConflictFileListItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (conflictedChanges != null)
            {
                foreach (var c in conflictedChanges)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Path) || !seen.Add(c.Path))
                        continue;

                    items.Add(new ConflictFileListItem
                    {
                        Marker = c.ConflictMarker,
                        Path = c.Path,
                        Description = c.ConflictDesc,
                        IsSelected = string.Equals(c.Path, _change?.Path, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            if (inspection?.ConflictedPaths != null)
            {
                foreach (var p in inspection.ConflictedPaths)
                {
                    if (string.IsNullOrWhiteSpace(p) || !seen.Add(p))
                        continue;

                    items.Add(new ConflictFileListItem
                    {
                        Marker = "U",
                        Path = p,
                        Description = string.Empty,
                        IsSelected = string.Equals(p, _change?.Path, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            ConflictedFiles = items;
        }

        public async Task RefreshStatusAsync()
        {
            if (IsRefreshing)
                return;

            IsRefreshing = true;
            try
            {
                await _wc.RefreshInProgressStateAsync();
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        public void NavigateTo(string sha)
        {
            _repo.NavigateToCommit(sha);
        }

        private readonly Repository _repo;
        private readonly WorkingCopy _wc;
        private readonly Models.Change _change;
        private Models.Commit _head;
        private Models.ConflictFileState _state = Models.ConflictFileState.Unknown;
        private object _mine;
        private object _theirs;
        private string _originLabel = string.Empty;
        private bool _isGitNestRetainedOrigin;
        private bool _isExternalOrigin = true;
        private string _headline = string.Empty;
        private string _detail = string.Empty;
        private string _activeMarkersText = string.Empty;
        private bool _showExternalAbortDisabledNotice = true;
        private IReadOnlyList<ConflictFileListItem> _conflictedFiles = [];
        private bool _isRefreshing;
    }
}
