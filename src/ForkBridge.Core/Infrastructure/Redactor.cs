using System.Text.RegularExpressions;

namespace ForkBridge.Core.Infrastructure;

/// <summary>Removes credentials from URLs, headers and command output before anything is logged,
/// persisted or rendered.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<cred>[^/@\s:]+(:[^/@\s]*)?)@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*)(basic|bearer|token)?\s*[A-Za-z0-9+/=._~-]{8,}")]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|apikey|api_key|access_key|pat)\b(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s;,""']+)")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})\b")]
    private static partial Regex KnownTokenShapes();

    [GeneratedRegex(@"(?i)(AccountKey|SharedAccessSignature|SharedAccessKey|sig)=([A-Za-z0-9+/=%]{16,})")]
    private static partial Regex AzureKeys();

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = UrlCredentials().Replace(input, m => m.Groups["scheme"].Value + "<redacted>@");
        s = AuthHeader().Replace(s, m => m.Groups[1].Value + "<redacted>");
        s = KeyValueSecret().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "<redacted>");
        s = KnownTokenShapes().Replace(s, "<redacted-token>");
        s = AzureKeys().Replace(s, m => m.Groups[1].Value + "=<redacted>");
        return s;
    }

    /// <summary>Environment variable names that must never reach validation processes.</summary>
    public static bool IsSensitiveEnvName(string name)
    {
        var n = name.ToUpperInvariant();
        string[] markers = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "_PAT", "PAT_", "APIKEY", "API_KEY",
            "ACCESS_KEY", "CONNECTIONSTRING", "CREDENTIAL", "PRIVATE_KEY", "SAS", "AZURE_DEVOPS_EXT_PAT", "SYSTEM_ACCESSTOKEN"];
        return markers.Any(n.Contains) || n.StartsWith("CONNECTIONSTRINGS__", StringComparison.Ordinal);
    }
}
