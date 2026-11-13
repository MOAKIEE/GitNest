using System;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class EditWorktreeTask : Popup
    {
        public Worktree Target { get; }
        public string Alias { get; set; }
        public bool IsCompleted { get; set; }

        public EditWorktreeTask(Repository repo, Worktree target)
        {
            _repo = repo;
            Target = target;
            Alias = target.Task.Alias;
            IsCompleted = target.Task.IsCompleted;
        }

        public override Task<bool> Sure()
        {
            var task = new Models.WorktreeTask()
            {
                CommonDirectory = Target.Task.CommonDirectory,
                FullPath = Target.FullPath,
                Alias = Alias ?? string.Empty,
                IsCompleted = IsCompleted,
                ComparisonTarget = Target.Task.ComparisonTarget,
            };
            try
            {
                Models.WorktreeTaskStore.Instance.Save(task);
                Target.LoadTask(task.CommonDirectory);
                _repo.RefreshWorktrees();
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _repo.SendNotification(App.Text("Worktree.Task.SaveFailed", ex.Message), true);
                return Task.FromResult(false);
            }
        }

        private readonly Repository _repo;
    }
}
