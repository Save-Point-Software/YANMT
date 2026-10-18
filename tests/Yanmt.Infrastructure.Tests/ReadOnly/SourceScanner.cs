using System.Text;

namespace Yanmt.Infrastructure.Tests.ReadOnly;

/// <summary>Finds the repo's src/ folder and splits C# source into string literals and code.</summary>
internal static class SourceScanner
{
    public static string SrcRoot { get; } = FindSrcRoot();

    public static string Relative(string path) => Path.GetRelativePath(SrcRoot, path).Replace('\\', '/');

    public static IEnumerable<string> Files(string relativeDir, params string[] patterns) =>
        patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(SrcRoot, relativeDir), p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Splits C# text into the contents of its string/char literals and the remaining code, with
    /// comments removed. Handles regular, verbatim (@"), interpolated ($"), and raw (""") strings.
    /// Interpolation holes are kept as part of the literal, which only makes scans stricter.
    /// </summary>
    public static (List<string> Literals, string Code) Split(string source)
    {
        var literals = new List<string>();
        var code = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                code.Append(' ');
                continue;
            }

            // Prefixes: $, @, $@, @$, $$ (raw interpolation)
            var start = i;
            var verbatim = false;
            while (i < source.Length && (source[i] == '$' || source[i] == '@'))
            {
                verbatim |= source[i] == '@';
                i++;
            }

            if (i < source.Length && source[i] == '"')
            {
                literals.Add(ReadString(source, ref i, verbatim));
                code.Append("\"\"");
                continue;
            }

            if (i > start)
            {
                code.Append(source, start, i - start);
                continue;
            }

            if (c == '\'')
            {
                var j = i + 1;
                var sb = new StringBuilder();
                while (j < source.Length && source[j] != '\'')
                {
                    if (source[j] == '\\')
                    {
                        sb.Append(source[j++]);
                    }

                    if (j < source.Length)
                    {
                        sb.Append(source[j++]);
                    }
                }

                literals.Add(sb.ToString());
                i = j + 1;
                code.Append("''");
                continue;
            }

            code.Append(c);
            i++;
        }

        return (literals, code.ToString());
    }

    private static string ReadString(string s, ref int i, bool verbatim)
    {
        // Raw string literal: three or more quotes, closed by the same number.
        var quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            var fence = new string('"', quotes);
            var bodyStart = i + quotes;
            var end = s.IndexOf(fence, bodyStart, StringComparison.Ordinal);
            end = end < 0 ? s.Length : end;
            i = Math.Min(s.Length, end + quotes);
            return s[bodyStart..end];
        }

        if (quotes == 2 && !verbatim)
        {
            i += 2; // ""
            return "";
        }

        var sb = new StringBuilder();
        i++; // opening quote
        while (i < s.Length)
        {
            var ch = s[i];
            if (verbatim)
            {
                if (ch == '"' && i + 1 < s.Length && s[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                if (ch == '"')
                {
                    i++;
                    break;
                }
            }
            else
            {
                if (ch == '\\' && i + 1 < s.Length)
                {
                    sb.Append(s[i + 1] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var o => o });
                    i += 2;
                    continue;
                }

                if (ch == '"' || ch == '\n')
                {
                    i++;
                    break;
                }
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static string FindSrcRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yanmt.slnx")))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }

        throw new InvalidOperationException("Can't find the repo root (Yanmt.slnx) above " + AppContext.BaseDirectory);
    }
}
