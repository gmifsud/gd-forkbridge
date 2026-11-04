using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForkBridge.Alignment.Text;

/// <summary>Text helpers ported 1:1 from Align.Common.psm1 (ConvertTo-Text, Test-FormatOnlyEqual, Get-ContentKey).</summary>
public static class TextOps
{
    private static readonly Regex Whitespace = new(@"[\s﻿]+", RegexOptions.Compiled);

    /// <summary>UTF-8 decode (invalid bytes become U+FFFD, as in PowerShell) and drop a leading BOM.</summary>
    public static string ToText(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    public static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    /// <summary>Text with every whitespace character and BOM removed: equal keys mean a format-only difference.
    /// Caveat (as in the PS suite): also ignores whitespace inside string literals.</summary>
    public static string FormatKey(byte[] bytes) => Whitespace.Replace(ToText(bytes), "");

    /// <summary>SHA-256 of the text ignoring BOM and CRLF/LF differences (pure-rename detection).</summary>
    public static string ContentKey(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToText(bytes).Replace("\r\n", "\n"))));
}

/// <summary>PowerShell <c>-clike</c> semantics: '*' any run of characters (including '/'), '?' one character,
/// '[...]' a character set, case-sensitive.</summary>
public static class Wildcard
{
    private static readonly Dictionary<(string, bool), Regex> Cache = new();

    public static bool IsMatch(string text, string pattern, bool ignoreCase = false)
    {
        Regex rx;
        lock (Cache)
        {
            if (!Cache.TryGetValue((pattern, ignoreCase), out rx!))
                Cache[(pattern, ignoreCase)] = rx = new Regex(ToRegex(pattern), RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
        }
        return rx.IsMatch(text);
    }

    public static bool Any(string text, IEnumerable<string> patterns, bool ignoreCase = false)
        => patterns.Any(p => !string.IsNullOrEmpty(p) && IsMatch(text, p, ignoreCase));

    internal static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*': sb.Append("[\\s\\S]*"); break;
                case '?': sb.Append("[\\s\\S]"); break;
                case '`' when i + 1 < pattern.Length: sb.Append(Regex.Escape(pattern[++i].ToString())); break;
                case '[':
                    var close = pattern.IndexOf(']', i + 1);
                    if (close > i + 1) { sb.Append('[').Append(pattern[(i + 1)..close].Replace("\\", "\\\\")).Append(']'); i = close; }
                    else sb.Append("\\[");
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.Append('$').ToString();
    }
}
