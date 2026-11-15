using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutAndFastForward : BranchSwitchPopup
    {
        public Models.Branch LocalBranch
        {
            get;
        }

        public Models.Branch RemoteBranch
        {
            get;
        }

        public CheckoutAndFastForward(Repository repo, Models.Branch localBranch, Models.Branch remoteBranch)
            : base(repo)
        {
            LocalBranch = localBranch;
            RemoteBranch = remoteBranch;
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            return await ExecuteSafeSwitchAsync(
                $"Checkout and Fast-Forward '{LocalBranch.Name}'",
                $"Checkout and Fast-Forward '{LocalBranch.Name}' ...",
                LocalBranch.Name,
                ["checkout", "-B", LocalBranch.Name, RemoteBranch.Head],
                () =>
                {
                    LocalBranch.Behind.Clear();
                    LocalBranch.Head = RemoteBranch.Head;
                    LocalBranch.CommitterDate = RemoteBranch.CommitterDate;
                    _repo.RefreshAfterCheckoutBranch(LocalBranch);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
    }
}
