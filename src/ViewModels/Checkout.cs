using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Checkout : BranchSwitchPopup
    {
        public string BranchName => _branch.Name;

        public Checkout(Repository repo, Models.Branch branch)
            : base(repo, branch.FullName)
        {
            _branch = branch;
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            return await ExecuteSafeSwitchAsync(
                $"Checkout '{_branch.Name}'",
                $"Checkout '{_branch.Name}' ...",
                _branch.Name,
                ["checkout", _branch.Name],
                () =>
                {
                    _repo.RefreshAfterCheckoutBranch(_branch);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }

        private readonly Models.Branch _branch = null;
    }
}
