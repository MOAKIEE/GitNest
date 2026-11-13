namespace SourceGit.ViewModels
{
    public partial class Repository
    {
        public TaskComparison TaskComparison => _taskComparison;
        public bool IsTaskComparisonVisible => SelectedViewIndex == 3;

        public void ShowTaskComparison(Worktree worktree)
        {
            _taskComparison?.Dispose();
            _taskComparison = new TaskComparison(this, worktree.FullPath, _gitCommonDir);
            OnPropertyChanged(nameof(TaskComparison));
            SelectedViewIndex = 3;
            _ = _taskComparison.RefreshAsync();
        }

        private void EnsureTaskComparison()
        {
            if (_taskComparison != null) return;
            _taskComparison = new TaskComparison(this, FullPath, _gitCommonDir);
            OnPropertyChanged(nameof(TaskComparison));
            _ = _taskComparison.RefreshAsync();
        }

        private TaskComparison _taskComparison;
    }
}
