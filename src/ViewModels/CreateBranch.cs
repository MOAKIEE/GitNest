using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CreateBranch : BranchSwitchPopup
    {
        [Required(ErrorMessage = "Branch name is required!")]
        [CustomValidation(typeof(CreateBranch), nameof(ValidateBranchName))]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, true);
        }

        public object BasedOn
        {
            get;
        }

        public bool CheckoutAfterCreated
        {
            get => _repo.UIStates.CheckoutBranchOnCreateBranch;
            set
            {
                if (_repo.UIStates.CheckoutBranchOnCreateBranch != value)
                {
                    _repo.UIStates.CheckoutBranchOnCreateBranch = value;
                    OnPropertyChanged();
                    UpdateOverrideTip();
                }
            }
        }

        public bool IsBareRepository
        {
            get => _repo.IsBare;
        }

        public string OverrideTip
        {
            get => _overrideTip;
            private set => SetProperty(ref _overrideTip, value);
        }

        public bool AllowOverwrite
        {
            get => _allowOverwrite;
            set
            {
                if (SetProperty(ref _allowOverwrite, value))
                    ValidateProperty(_name, nameof(Name));
            }
        }

        public CreateBranch(Repository repo, Models.Branch branch)
            : base(repo)
        {
            _baseOnRevision = branch.Head;
            _committerDate = branch.CommitterDate;
            _head = branch.Head;

            if (!branch.IsLocal)
                Name = branch.Name;

            BasedOn = branch;
            UpdateOverrideTip();
        }

        public CreateBranch(Repository repo, Models.Commit commit)
            : base(repo)
        {
            _baseOnRevision = commit.SHA;
            _committerDate = commit.CommitterTime;
            _head = commit.SHA;

            BasedOn = commit;
            UpdateOverrideTip();
        }

        public CreateBranch(Repository repo, Models.Tag tag)
            : base(repo)
        {
            _baseOnRevision = tag.SHA;
            _committerDate = tag.CreatorDate;
            _head = tag.SHA;

            BasedOn = tag;
            UpdateOverrideTip();
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is CreateBranch creator)
            {
                if (!Models.RefName.IsValidBranchName(name))
                    return new ValidationResult("Bad branch name format!");

                if (!creator._allowOverwrite)
                {
                    foreach (var b in creator._repo.Branches)
                    {
                        if (b.FriendlyName.Equals(name, StringComparison.Ordinal))
                            return new ValidationResult("A branch with same name already exists!");
                    }
                }

                return ValidationResult.Success;
            }

            return new ValidationResult("Missing runtime context to create branch!");
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            Models.Branch created = new()
            {
                Name = _name,
                FullName = $"refs/heads/{_name}",
                CommitterDate = _committerDate,
                Head = _head,
                IsLocal = true,
            };

            if (CheckoutAfterCreated && !_repo.IsBare)
            {
                var skipDetachedCheck = _repo.CurrentBranch is { IsDetachedHead: true } &&
                    _repo.CurrentBranch.Head.Equals(_baseOnRevision, StringComparison.Ordinal);

                return await ExecuteSafeSwitchAsync(
                    $"Create Branch '{_name}'",
                    $"Create branch '{_name}' ...",
                    _name,
                    ["checkout", _allowOverwrite ? "-B" : "-b", _name, _baseOnRevision],
                    async () =>
                    {
                        if (BasedOn is Models.Branch { IsLocal: false } basedOn &&
                            _name.Equals(basedOn.Name, StringComparison.Ordinal))
                        {
                            var branchLog = _repo.CreateLog($"Set Upstream '{_name}'");
                            await new Commands.Branch(_repo.FullPath, _name)
                                .Use(branchLog)
                                .SetUpstreamAsync(basedOn)
                                .ConfigureAwait(false);
                            branchLog.Complete();
                            created.Upstream = basedOn.FullName;
                        }
                        else
                        {
                            created.Upstream = string.Empty;
                        }

                        _repo.RefreshAfterCreateBranch(created, true);
                    },
                    skipDetachedHeadCheck: skipDetachedCheck).ConfigureAwait(false);
            }

            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog($"Create Branch '{_name}'");
            Use(log);

            var succ = await new Commands.Branch(_repo.FullPath, _name)
                .Use(log)
                .CreateAsync(_baseOnRevision, _allowOverwrite);

            if (succ)
            {
                if (BasedOn is Models.Branch { IsLocal: false } basedOn && _name.Equals(basedOn.Name, StringComparison.Ordinal))
                {
                    await new Commands.Branch(_repo.FullPath, _name)
                        .Use(log)
                        .SetUpstreamAsync(basedOn);

                    created.Upstream = basedOn.FullName;
                }
                else
                {
                    created.Upstream = string.Empty;
                }

                _repo.RefreshAfterCreateBranch(created, false);
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }

            log.Complete();
            return succ;
        }

        private void UpdateOverrideTip()
        {
            OverrideTip = CheckoutAfterCreated ? "-B in `git checkout`" : "-f in `git branch`";
        }

        private readonly string _baseOnRevision = null;
        private readonly ulong _committerDate = 0;
        private readonly string _head = string.Empty;
        private string _name = null;
        private string _overrideTip = "-B";
        private bool _allowOverwrite = false;
    }
}
