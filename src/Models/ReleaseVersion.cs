using System;
using System.Globalization;
using System.Linq;

namespace SourceGit.Models
{
    internal static class ReleaseVersion
    {
        public static bool TryCompare(string left, string right, out int comparison)
        {
            comparison = 0;
            if (!TryParse(left, out var leftCore, out var leftPre) ||
                !TryParse(right, out var rightCore, out var rightPre))
                return false;

            comparison = leftCore.CompareTo(rightCore);
            if (comparison != 0)
                return true;
            if (leftPre.Length == 0 || rightPre.Length == 0)
            {
                comparison = (leftPre.Length == 0 ? 1 : 0) - (rightPre.Length == 0 ? 1 : 0);
                return true;
            }

            for (var i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
            {
                var a = leftPre[i];
                var b = rightPre[i];
                var aNumeric = IsNumeric(a);
                var bNumeric = IsNumeric(b);
                if (aNumeric && bNumeric)
                    comparison = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                else if (aNumeric != bNumeric)
                    comparison = aNumeric ? -1 : 1;
                else
                    comparison = string.CompareOrdinal(a, b);
                if (comparison != 0)
                    return true;
            }

            comparison = leftPre.Length.CompareTo(rightPre.Length);
            return true;
        }

        private static bool TryParse(string value, out System.Version core, out string[] prerelease)
        {
            core = null;
            prerelease = [];
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (value[0] is 'v' or 'V')
                value = value.Substring(1);

            var metadata = value.Split('+', 2);
            if (metadata.Length == 2 && !metadata[1].Split('.').All(IsIdentifier))
                return false;
            var parts = metadata[0].Split('-', 2);
            if (parts.Length == 2)
            {
                prerelease = parts[1].Split('.');
                if (!prerelease.All(p => IsIdentifier(p) && !(IsNumeric(p) && p.Length > 1 && p[0] == '0')))
                    return false;
            }

            var numbers = parts[0].Split('.');
            if (numbers.Length != 3 || !numbers.All(n => IsNumeric(n) && !(n.Length > 1 && n[0] == '0')) ||
                !int.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
                !int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
                !int.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
                return false;
            core = new System.Version(major, minor, patch);
            return true;
        }

        private static bool IsIdentifier(string value) => value.Length > 0 &&
            value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-');

        private static bool IsNumeric(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
    }
}
