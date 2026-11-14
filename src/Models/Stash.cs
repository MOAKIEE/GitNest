using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public class Stash
    {
        public string Name { get; set; } = "";
        public string SHA { get; set; } = "";
        public List<string> Parents { get; set; } = [];
        public ulong Time { get; set; } = 0;
        public string Message { get; set; } = "";
        public string Subject => Message.Split('\n', 2)[0].Trim();
        public string UntrackedParent => EmptyTreeHash.Guess(SHA);
        public bool HasUntrackedParent => Parents is { Count: >= 3 };

        public string SourceBranch
        {
            get
            {
                TryParseSubject(Subject, out var branch, out _);
                return branch;
            }
        }

        public string DisplaySubject
        {
            get
            {
                return TryParseSubject(Subject, out _, out var clean) ? clean : Subject;
            }
        }

        public bool HasStandardPrefix => TryParseSubject(Subject, out _, out _);

        public static bool TryParseSubject(string subject, out string sourceBranch, out string cleanSubject)
        {
            sourceBranch = string.Empty;
            cleanSubject = subject?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(cleanSubject))
                return false;

            string remainder = null;
            if (cleanSubject.StartsWith("WIP on ", StringComparison.Ordinal))
                remainder = cleanSubject["WIP on ".Length..];
            else if (cleanSubject.StartsWith("On ", StringComparison.Ordinal))
                remainder = cleanSubject["On ".Length..];

            if (string.IsNullOrEmpty(remainder))
                return false;

            var colonIdx = remainder.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx <= 0)
                return false;

            sourceBranch = remainder[..colonIdx].Trim();
            cleanSubject = remainder[(colonIdx + 2)..].Trim();
            return true;
        }
    }
}
