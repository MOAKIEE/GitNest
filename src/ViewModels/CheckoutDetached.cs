using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutDetached : BranchSwitchPopup
    {
        public object Target
        {
            get;
        }

        public CheckoutDetached(Repository repo, Models.Commit commit)
            : base(repo)
        {
            _revision = commit.SHA;
            Target = commit;
        }

        public CheckoutDetached(Repository repo, Models.Tag tag)
            : base(repo)
        {
            _revision = tag.SHA;
            Target = tag;
        }

        public override async Task<bool> Sure()
        {
            if (HasResultView)
                return false;

            return await ExecuteSafeSwitchAsync(
                "Checkout Commit",
                $"Checkout Commit '{_revision}' ...",
                _revision,
                ["checkout", "--detach", _revision],
                () =>
                {
                    _repo.MarkBranchesDirtyManually();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }

        private readonly string _revision = string.Empty;
    }
}
