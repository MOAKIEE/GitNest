namespace SourceGit.Models
{
    public sealed class CommitIdentity
    {
        public string Name { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public bool IsReadable { get; init; } = true;
        public bool IsComplete => IsReadable && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);
    }
}
