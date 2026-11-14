using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal static class CommandArgumentHelper
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        /// <summary>
        /// Ensures a ref/branch/commit/strategy argument cannot be parsed as a Git option flag
        /// or contain embedded control characters.
        /// </summary>
        public static string ValidateNonOptionToken(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("参数不能为空。", paramName);

            var trimmed = value.Trim();
            if (trimmed.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("参数不能以 '-' 开头。", paramName);

            foreach (var ch in trimmed)
            {
                if (ch is '\0' or '\r' or '\n')
                    throw new ArgumentException("参数包含非法控制字符。", paramName);
            }

            return trimmed;
        }

        /// <summary>
        /// Escapes a single argument for process command lines (handling whitespace, quotes,
        /// and backslashes cleanly across Windows CommandLineToArgvW and POSIX tokenizers).
        /// </summary>
        public static string Quote(string value)
        {
            if (value == null)
                return "\"\"";

            var sb = new StringBuilder(value.Length + 4);
            sb.Append('"');
            var pendingBackslashes = 0;

            foreach (var ch in value)
            {
                if (ch == '\\')
                {
                    pendingBackslashes++;
                }
                else if (ch == '"')
                {
                    sb.Append('\\', pendingBackslashes * 2 + 1);
                    sb.Append('"');
                    pendingBackslashes = 0;
                }
                else
                {
                    if (pendingBackslashes > 0)
                    {
                        sb.Append('\\', pendingBackslashes);
                        pendingBackslashes = 0;
                    }

                    sb.Append(ch);
                }
            }

            if (pendingBackslashes > 0)
                sb.Append('\\', pendingBackslashes * 2);

            sb.Append('"');
            return sb.ToString();
        }

        public static async Task<string> WriteNulPathspecFileAsync(
            IEnumerable<string> paths,
            CancellationToken cancellationToken = default)
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var seen = new HashSet<string>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                using var ms = new MemoryStream();
                foreach (var path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var normalized = path.Replace('\\', '/');
                    if (!seen.Add(normalized))
                        continue;

                    var bytes = Utf8NoBom.GetBytes(normalized);
                    ms.Write(bytes, 0, bytes.Length);
                    ms.WriteByte(0);
                }

                await File.WriteAllBytesAsync(tempFile, ms.ToArray(), cancellationToken).ConfigureAwait(false);
                return tempFile;
            }
            catch
            {
                TryDeleteTempFile(tempFile);
                throw;
            }
        }

        public static async Task<string> WriteUtf8TempFileAsync(
            string content,
            CancellationToken cancellationToken = default)
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempFile, content ?? string.Empty, Utf8NoBom, cancellationToken)
                    .ConfigureAwait(false);
                return tempFile;
            }
            catch
            {
                TryDeleteTempFile(tempFile);
                throw;
            }
        }

        public static void TryDeleteTempFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Ignore best-effort temp cleanup failures.
            }
        }
    }

    internal sealed class CapturingCommandLog : Models.ICommandLog
    {
        private readonly Models.ICommandLog _inner;
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public CapturingCommandLog(Models.ICommandLog inner = null)
        {
            _inner = inner;
        }

        public void AppendLine(string line = null)
        {
            var text = line ?? string.Empty;
            var redacted = Models.GitConfigSnapshot.RedactText(text);

            lock (_gate)
                _lines.Add(redacted);

            _inner?.AppendLine(redacted);
        }

        public string GetCombinedText()
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _lines);
        }
    }
}
