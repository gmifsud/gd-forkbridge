namespace ForkBridge.Core.Policy;

/// <summary>Repository-relative glob matching with forward slashes.
/// '**' matches any number of segments, '*' and '?' stay within a segment.
/// A pattern without wildcards matches the path itself and everything beneath it.</summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string path)
    {
        pattern = Normalize(pattern);
        path = Normalize(path);
        if (pattern.Length == 0) return false;
        if (!HasWildcard(pattern))
            return path.Equals(pattern, StringComparison.Ordinal) || path.StartsWith(pattern + "/", StringComparison.Ordinal);
        var ps = pattern.Split('/');
        var xs = path.Split('/');
        // A trailing directory-ish pattern like "a/*" should match "a/b/c" only via "**".
        return Match(ps, 0, xs, 0);
    }

    public static string Normalize(string p) => p.Replace('\\', '/').Trim().TrimStart('.', '/').TrimEnd('/');

    public static bool HasWildcard(string p) => p.IndexOfAny(['*', '?', '[']) >= 0;

    /// <summary>Number of literal characters - used to pick the most specific matching rule.</summary>
    public static int Specificity(string pattern) => Normalize(pattern).Count(c => c is not ('*' or '?'));

    private static bool Match(string[] p, int pi, string[] x, int xi)
    {
        while (pi < p.Length)
        {
            if (p[pi] == "**")
            {
                if (pi == p.Length - 1) return true;
                for (int k = xi; k <= x.Length; k++)
                    if (Match(p, pi + 1, x, k)) return true;
                return false;
            }
            if (xi >= x.Length || !SegmentMatch(p[pi], x[xi])) return false;
            pi++; xi++;
        }
        return xi == x.Length;
    }

    private static bool SegmentMatch(string pat, string s)
    {
        int pi = 0, si = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < pat.Length && (pat[pi] == '?' || pat[pi] == s[si])) { pi++; si++; }
            else if (pi < pat.Length && pat[pi] == '*') { star = pi++; mark = si; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < pat.Length && pat[pi] == '*') pi++;
        return pi == pat.Length;
    }
}
