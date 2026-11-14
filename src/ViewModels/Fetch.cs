using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Fetch : Popup
    {
        public List<Models.Remote> Remotes
        {
            get => _repo.Remotes;
        }

        public bool IsFetchAllRemoteVisible
        {
            get;
        }

        public bool FetchAllRemotes
        {
            get => _fetchAllRemotes;
            set
            {
                if (SetProperty(ref _fetchAllRemotes, value) && IsFetchAllRemoteVisible)
                    _repo.UIStates.FetchAllRemotes = value;
            }
        }

        public Models.Remote SelectedRemote
        {
            get;
            set;
        }

        public bool NoTags
        {
            get => _repo.UIStates.FetchWithoutTags;
            set => _repo.UIStates.FetchWithoutTags = value;
        }

        public bool Force
        {
            get => _repo.UIStates.EnableForceOnFetch;
            set => _repo.UIStates.EnableForceOnFetch = value;
        }

        public Fetch(Repository repo, Models.Remote preferredRemote = null)
        {
            _repo = repo;
            IsFetchAllRemoteVisible = repo.Remotes.Count > 1 && preferredRemote == null;
            _fetchAllRemotes = IsFetchAllRemoteVisible && _repo.UIStates.FetchAllRemotes;
            CanTerminate = true;

            if (preferredRemote != null)
            {
                SelectedRemote = preferredRemote;
            }
            else if (!string.IsNullOrEmpty(_repo.Settings.DefaultRemote))
            {
                var def = _repo.Remotes.Find(r => r.Name == _repo.Settings.DefaultRemote);
                SelectedRemote = def ?? _repo.Remotes[0];
            }
            else
            {
                SelectedRemote = _repo.Remotes[0];
            }
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();

            var navigateToUpstreamHEAD = _repo.IsDashboardVisible &&
                _repo.Histories.SelectedCommits.Count == 1 &&
                _repo.Histories.SelectedCommits[0].IsCurrentHead;

            var notags = _repo.UIStates.FetchWithoutTags;
            var force = _repo.UIStates.EnableForceOnFetch;
            var log = _repo.CreateLog("Fetch");
            Use(log);

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            if (FetchAllRemotes)
            {
                var targets = _repo.Remotes;
                var res = await Commands.GitSyncService.FetchAsync(_repo.FullPath, targets, notags, force, log, token, Models.OperationJournalStore.Instance);
                log.Complete();
                _repo.MarkFetchResult(res);

                if (!res.Succeeded)
                {
                    if (res.Outcome != Models.FetchOutcomeKind.Canceled)
                    {
                        HasFetchFeedbackBanner = true;
                        FetchFeedbackMessage = res.Summary;
                    }

                    _cancellation = null;
                    return false;
                }
            }
            else
            {
                var res = await Commands.GitSyncService.FetchAsync(_repo.FullPath, [SelectedRemote], notags, force, log, token, Models.OperationJournalStore.Instance);
                log.Complete();
                _repo.MarkFetchResult(res);

                if (!res.Succeeded)
                {
                    if (res.Outcome != Models.FetchOutcomeKind.Canceled)
                    {
                        HasFetchFeedbackBanner = true;
                        FetchFeedbackMessage = res.Summary;
                    }

                    _cancellation = null;
                    return false;
                }
            }

            if (navigateToUpstreamHEAD && !token.IsCancellationRequested)
            {
                var upstream = _repo.CurrentBranch?.Upstream;
                if (!string.IsNullOrEmpty(upstream) && upstream.Length > 13)
                {
                    var upstreamHead = await new Commands.QueryRevisionByRefName(_repo.FullPath, upstream.Substring(13)).GetResultAsync();
                    _repo.NavigateToCommit(upstreamHead, true);
                }
            }

            HasFetchFeedbackBanner = false;
            FetchFeedbackMessage = string.Empty;
            OnPropertyChanged(nameof(LastFetchStatusText));

            _cancellation = null;
            return true;
        }

        public string LastFetchStatusText
        {
            get
            {
                var last = _repo.LastFetchDisplayTime;
                var text = string.IsNullOrEmpty(last)
                    ? App.Text("Fetch.NeverFetched")
                    : last;
                if (_repo.LastFetchFailed && !string.IsNullOrEmpty(_repo.LastFetchError))
                    return $"{text} · {_repo.LastFetchError}";
                return text;
            }
        }

        public bool HasFetchFeedbackBanner
        {
            get => _hasFetchFeedbackBanner;
            private set => SetProperty(ref _hasFetchFeedbackBanner, value);
        }

        public string FetchFeedbackMessage
        {
            get => _fetchFeedbackMessage;
            private set => SetProperty(ref _fetchFeedbackMessage, value);
        }

        public override void Terminate()
        {
            // Just fire cancel event and UI will auto wait the `Sure` complete
            var _ = _cancellation?.CancelAsync();
        }

        private readonly Repository _repo = null;
        private bool _fetchAllRemotes = false;
        private bool _hasFetchFeedbackBanner = false;
        private string _fetchFeedbackMessage = string.Empty;
        private CancellationTokenSource _cancellation = null;
    }
}
