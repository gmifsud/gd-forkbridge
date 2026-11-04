using System.Text;

namespace ForkBridge.Merge.Workspace;

/// <summary>How a file's bytes map to editor text and back. The editor always works on LF text; the codec
/// restores the original encoding, BOM and line-ending style on save so an edit never silently re-encodes a file.</summary>
public sealed record CodecInfo
{
    /// <summary>utf-8 | utf-8-bom | utf-16le | utf-16be (null when the content is binary or undecodable).</summary>
    public string? Encoding { get; init; }
    /// <summary>lf | crlf | mixed | none</summary>
    public string Eol { get; init; } = "none";
    public bool FinalNewline { get; init; }
    public bool Binary { get; init; }
    public long Size { get; init; }
    public bool Editable { get; init; }
    public Model.Restriction Restriction { get; init; }
    public string? Reason { get; init; }
}

public sealed record Decoded(CodecInfo Info, string? Text);

public static class ContentCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>Git's own heuristic: a NUL byte in the first 8000 bytes means binary (unless a UTF-16 BOM explains it).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> b)
    {
        if (HasUtf16Bom(b)) return false;
        var n = Math.Min(b.Length, 8000);
        for (int i = 0; i < n; i++) if (b[i] == 0) return true;
        return false;
    }

    private static bool HasUtf16Bom(ReadOnlySpan<byte> b) => b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    /// <summary>Decodes for display/editing. Text is returned for display even when not editable (mixed EOL), but
    /// never when it exceeds <paramref name="maxEditable"/> (no truncated previews that could be saved back).</summary>
    public static Decoded Decode(byte[] bytes, long maxEditable)
    {
        if (LooksBinary(bytes))
            return new Decoded(new CodecInfo { Binary = true, Size = bytes.Length, Restriction = Model.Restriction.None, Reason = "Binary content: choose a version." }, null);
        if (bytes.Length > maxEditable)
            return new Decoded(new CodecInfo { Size = bytes.Length, Restriction = Model.Restriction.Oversized,
                Reason = $"File is {bytes.Length:N0} bytes; the editor limit is {maxEditable:N0} bytes. Choose a version." }, null);
        string enc; string text;
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { enc = "utf-8-bom"; text = StrictUtf8.GetString(bytes, 3, bytes.Length - 3); }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = "utf-16le"; text = StrictUtf16Le.GetString(bytes, 2, bytes.Length - 2); }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = "utf-16be"; text = StrictUtf16Be.GetString(bytes, 2, bytes.Length - 2); }
            else { enc = "utf-8"; text = StrictUtf8.GetString(bytes); }
        }
        catch (DecoderFallbackException)
        {
            return new Decoded(new CodecInfo { Size = bytes.Length, Restriction = Model.Restriction.UnsupportedEncoding,
                Reason = "Not valid UTF-8 (and no UTF-16 byte-order mark): editing could corrupt it. Choose a version." }, null);
        }
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; } else cr++; }
            else if (text[i] == '\n') lf++;
        }
        var eol = cr > 0 || (crlf > 0 && lf > 0) ? "mixed" : crlf > 0 ? "crlf" : lf > 0 ? "lf" : "none";
        var display = eol is "crlf" or "mixed" ? text.Replace("\r\n", "\n") : text;
        var info = new CodecInfo
        {
            Encoding = enc, Eol = eol, FinalNewline = text.EndsWith('\n'), Size = bytes.Length,
            Editable = eol != "mixed",
            Restriction = eol == "mixed" ? Model.Restriction.MixedLineEndings : Model.Restriction.None,
            Reason = eol == "mixed" ? "The file mixes line endings (CRLF and LF, or bare CR); the editor cannot preserve that. Choose a version." : null,
        };
        return new Decoded(info, display);
    }

    /// <summary>Encodes LF editor text back to bytes with the original encoding, BOM and line endings.</summary>
    public static byte[] Encode(string text, CodecInfo info)
    {
        if (!info.Editable || info.Encoding is null) throw new InvalidOperationException("Content is not editable.");
        var lf = text.Replace("\r\n", "\n");
        if (lf.Contains('\r')) throw new ArgumentException("Bare carriage returns are not accepted in edited text.");
        var t = info.Eol == "crlf" ? lf.Replace("\n", "\r\n") : lf;
        return info.Encoding switch
        {
            "utf-8" => StrictUtf8.GetBytes(t),
            "utf-8-bom" => [0xEF, 0xBB, 0xBF, .. StrictUtf8.GetBytes(t)],
            "utf-16le" => [0xFF, 0xFE, .. StrictUtf16Le.GetBytes(t)],
            "utf-16be" => [0xFE, 0xFF, .. StrictUtf16Be.GetBytes(t)],
            _ => throw new InvalidOperationException($"Unknown encoding {info.Encoding}."),
        };
    }

    private static readonly System.Text.RegularExpressions.Regex Markers =
        new(@"^(<{7} |={7}\r?$|>{7} |\|{7} )", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Conflict-marker lines (a warning, not the definition of "resolved").</summary>
    public static int CountMarkerLines(string text) => Markers.Matches(text).Count;
}
