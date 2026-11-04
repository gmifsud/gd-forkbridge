using System.Text;
using ForkBridge.Core.Config;
using ForkBridge.Core.Storage;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Planning;
using ForkBridge.Merge.Storage;
using ForkBridge.Merge.Workspace;

namespace ForkBridge.Merge.Resolution;

public sealed record SideView(string Label, SideBlob? Blob, CodecInfo? Codec, string? Text);

public sealed record DiffView(string Diff, bool Truncated, bool Binary);

/// <summary>Everything the inspector needs for one entry. Text is only included when it is complete (never a
/// truncated preview); diffs are display-only and may be truncated.</summary>
public sealed record FileView
{
    public required MergeEntry Entry { get; init; }
    public required string SourceLabel { get; init; }
    public required string TargetLabel { get; init; }
    /// <summary>unresolved | draft | resolved | clean</summary>
    public required string State { get; init; }
    public required int SessionRevision { get; init; }
    public required SideView Base { get; init; }
    public required SideView Target { get; init; }
    public required SideView Source { get; init; }
    /// <summary>Current result: draft, resolved content, or Git's merge output (with conflict markers).</summary>
    public required SideView Result { get; init; }
    public bool ResultDeleted { get; init; }
    /// <summary>Hash the client must send back as expectedContentHash.</summary>
    public required string ResultContentHash { get; init; }
    public bool Editable { get; init; }
    public string? EditBlockedReason { get; init; }
    public int MarkerLines { get; init; }
    public ResolutionRecord? Resolution { get; init; }
    public DraftRecord? Draft { get; init; }
    public required DiffView TargetChanges { get; init; }
    public required DiffView SourceChanges { get; init; }
    public required DiffView TargetToResult { get; init; }
    public string? FilterAttribute { get; init; }
    public string? MergeAttribute { get; init; }
}

public sealed record SaveDraftRequest(string EntryId, int ExpectedSessionRevision, string ExpectedContentHash, string Content);

public sealed record ResolveRequest(string EntryId, ResolutionAction Action, int ExpectedSessionRevision, string ExpectedContentHash,
    string? Content = null, bool AcknowledgeMarkers = false);

public sealed class ResolutionService
{
    public const int MaxDiffBytes = 1024 * 1024;
    public const string AbsentHash = "absent";
    private readonly ForkBridgeConfig _cfg;
    private readonly MergeGit _git;
    private readonly MergeStore _store;
    private readonly SessionWorkspace _ws;

    public ResolutionService(ForkBridgeConfig cfg, MergeGit git, MergeStore store, SessionWorkspace ws)
    {
        _cfg = cfg; _git = git; _store = store; _ws = ws;
    }

    private long MaxEditable => _cfg.Merge.MaxEditableBytes;

    private static string Hash(byte[]? b) => b is null ? AbsentHash : "sha256:" + StateStore.Sha256(b);

    // ---- read ---------------------------------------------------------------------------------
    public async Task<FileView> GetAsync(MergeSession s, MergePlan plan, string entryId, CancellationToken ct = default)
    {
        var entry = plan.Entries.FirstOrDefault(e => e.Id == entryId) ?? throw new MergeException($"Entry '{entryId}' is not part of this merge.", MergeErrorKind.NotFound);
        var mirror = _ws.Mirror(s);
        var (current, state) = await CurrentResultAsync(s, entry, ct);
        string? filter = null, mergeAttr = null;
        if (entry.Status == EntryStatus.Conflicted && Directory.Exists(s.Worktree)) (filter, mergeAttr) = await AttributesAsync(s, entry.Path);
        var resultCodec = current is null ? null : ContentCodec.Decode(current, MaxEditable);
        var (editable, reason) = EditState(s, entry, resultCodec?.Info, filter);
        var markers = resultCodec?.Text is { } t ? ContentCodec.CountMarkerLines(t) : 0;
        var resultBlob = current is null ? null : new SideBlob(s.Resolutions.GetValueOrDefault(entryId)?.Result?.Mode ?? entry.Target?.Mode ?? entry.Source?.Mode ?? "100644",
            await _git.HashObjectAsync(mirror, current, ct));
        return new FileView
        {
            Entry = entry, SourceLabel = plan.Source.Name, TargetLabel = plan.Target.Name, State = state, SessionRevision = s.Revision,
            Base = await SideAsync(mirror, "Base", entry.Base, ct),
            Target = await SideAsync(mirror, $"Target ({plan.Target.Name})", entry.Target, ct),
            Source = await SideAsync(mirror, $"Source ({plan.Source.Name})", entry.Source, ct),
            Result = new SideView("Result", resultBlob, resultCodec?.Info, resultCodec?.Text),
            ResultDeleted = current is null, ResultContentHash = Hash(current), Editable = editable, EditBlockedReason = reason, MarkerLines = markers,
            Resolution = s.Resolutions.GetValueOrDefault(entryId), Draft = s.Drafts.GetValueOrDefault(entryId),
            TargetChanges = await DiffAsync(mirror, entry.Base?.Sha, entry.Target?.Sha, "base", "target", ct),
            SourceChanges = await DiffAsync(mirror, entry.Base?.Sha, entry.Source?.Sha, "base", "source", ct),
            TargetToResult = await DiffAsync(mirror, entry.Target?.Sha, resultBlob?.Sha, "target", "result", ct),
            FilterAttribute = filter, MergeAttribute = mergeAttr,
        };
    }

    /// <summary>The bytes the result currently holds (null = deleted/absent) and its state.</summary>
    private async Task<(byte[]? Bytes, string State)> CurrentResultAsync(MergeSession s, MergeEntry entry, CancellationToken ct)
    {
        var mirror = _ws.Mirror(s);
        if (entry.Status != EntryStatus.Conflicted)
            return (entry.Result is null ? null : await _git.ReadBlobAsync(mirror, entry.Result.Sha, ct), "clean");
        if (s.Drafts.ContainsKey(entry.Id))
        {
            var f = _store.DraftFile(s.Id, entry.Id);
            if (File.Exists(f)) return (await File.ReadAllBytesAsync(f, ct), "draft");
        }
        if (s.Resolutions.TryGetValue(entry.Id, out var r))
            return (r.Result is null ? null : await _git.ReadBlobAsync(mirror, r.Result.Sha, ct), "resolved");
        // Unresolved: Git's own merge output in the workspace (with conflict markers for text conflicts).
        var file = Path.Combine(s.Worktree, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(s.Worktree) && EntryClassifier.PathProblem(entry.Path) is null)
        {
            _store.Core.Fs.AssertNoLinksBetweenRootAndPath(file);
            if (File.Exists(file)) return (await File.ReadAllBytesAsync(file, ct), "unresolved");
        }
        return (null, "unresolved");
    }

    private (bool, string?) EditState(MergeSession s, MergeEntry e, CodecInfo? codec, string? filter)
    {
        if (e.Status != EntryStatus.Conflicted) return (false, "Merged by Git without conflict (read-only).");
        if (!e.Capabilities.EditText) return (false, e.Capabilities.Reason ?? "This conflict type cannot be edited as text.");
        if (s.State is not SessionState.Resolving) return (false, $"The session is {s.State}; reopen it to change resolutions.");
        if (filter is not null and not "unspecified" and not "unset")
            return (false, $"The path uses the '{filter}' filter attribute (e.g. Git LFS): the stored content is not the real file. Choose a version.");
        if (codec is null) return (false, "The result is currently a deletion; choose a version to restore content.");
        if (!codec.Editable) return (false, codec.Reason ?? "Content cannot be edited.");
        return (true, null);
    }

    private async Task<(string? Filter, string? Merge)> AttributesAsync(MergeSession s, string path)
    {
        var r = await _git.RunAsync(s.Worktree, ["check-attr", "-z", "filter", "merge", "--", path]);
        if (!r.Success) return (null, null);
        var t = Encoding.UTF8.GetString(r.StdOutBytes).Split('\0');
        string? f = null, m = null;
        for (int i = 0; i + 2 < t.Length; i += 3) { if (t[i + 1] == "filter") f = t[i + 2]; else if (t[i + 1] == "merge") m = t[i + 2]; }
        return (f is "unspecified" ? null : f, m is "unspecified" ? null : m);
    }

    private async Task<SideView> SideAsync(string mirror, string label, SideBlob? blob, CancellationToken ct)
    {
        if (blob is null || blob.Mode is "160000") return new SideView(label, blob, null, null);
        var size = await _git.BlobSizeAsync(mirror, blob.Sha);
        if (size > MaxEditable) return new SideView(label, blob, new CodecInfo { Size = size, Restriction = Restriction.Oversized, Reason = "Too large to display in full." }, null);
        var d = ContentCodec.Decode(await _git.ReadBlobAsync(mirror, blob.Sha, ct), MaxEditable);
        return new SideView(label, blob, d.Info, d.Text);
    }

    private async Task<DiffView> DiffAsync(string mirror, string? a, string? b, string la, string lb, CancellationToken ct)
    {
        if (a == b) return new DiffView("", false, false);
        var empty = (await _git.CheckedAsync(mirror, ["hash-object", "-t", "blob", "--stdin"], [], ct: ct)).StdOut.Trim();
        var r = await _git.RunAsync(mirror, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "-U3", a ?? empty, b ?? empty], ct: ct);
        if (r.ExitCode is not (0 or 1)) return new DiffView("(diff unavailable)", false, false);
        var bytes = r.StdOutBytes;
        var truncated = bytes.Length > MaxDiffBytes;
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxDiffBytes));
        if (text.Contains("\nBinary files ") || text.StartsWith("Binary files ")) return new DiffView("", false, true);
        var at = text.IndexOf("\n@@", StringComparison.Ordinal);
        var body = at >= 0 ? text[(at + 1)..] : text;
        return new DiffView($"--- {(a is null ? "/dev/null" : la)}\n+++ {(b is null ? "/dev/null" : lb)}\n" + body, truncated, false);
    }

    // ---- write --------------------------------------------------------------------------------
    private static void CheckRevision(MergeSession s, int expected)
    {
        if (s.Revision != expected)
            throw new MergeException($"The session changed since you loaded it (revision {expected} → {s.Revision}), possibly in another tab. Reload and reapply your change.", MergeErrorKind.Stale);
    }

    private async Task<MergeEntry> MutableEntryAsync(MergeSession s, MergePlan plan, string entryId, int expectedRevision, string expectedHash, CancellationToken ct)
    {
        if (s.State is not SessionState.Resolving)
            throw new MergeException(s.State is SessionState.Finalized or SessionState.Approved
                ? "The candidate is finalized; reopen the session to change resolutions (this discards the candidate, validation and approval)."
                : $"The session is {s.State}; it cannot be changed.", s.State is SessionState.Damaged ? MergeErrorKind.Stale : MergeErrorKind.Refused);
        CheckRevision(s, expectedRevision);
        var entry = plan.Entries.FirstOrDefault(e => e.Id == entryId) ?? throw new MergeException($"Entry '{entryId}' is not part of this merge.", MergeErrorKind.NotFound);
        if (entry.Status != EntryStatus.Conflicted) throw new MergeException($"'{entry.Path}' merged cleanly; there is nothing to resolve.", MergeErrorKind.Refused);
        if (!entry.Capabilities.Supported) throw new MergeException($"'{entry.Path}' cannot be resolved here: {entry.Capabilities.Reason}", MergeErrorKind.Unsupported);
        var integrity = await _ws.VerifyAsync(s, plan);
        if (!integrity.Ok)
        {
            s.State = SessionState.Damaged; s.StateReason = string.Join("; ", integrity.Problems);
            _store.Audit(s, "workspace integrity check failed: " + s.StateReason);
            _store.SaveSession(s);
            throw new MergeException("The workspace was changed outside ForkBridge or is missing: " + s.StateReason + ". Use Recover to rebuild it from the recorded resolutions.", MergeErrorKind.Stale);
        }
        var (current, _) = await CurrentResultAsync(s, entry, ct);
        if (Hash(current) != expectedHash)
            throw new MergeException($"The content of '{entry.Path}' changed since you loaded it (another tab or a later save). Reload before saving.", MergeErrorKind.Stale);
        return entry;
    }

    private async Task<(byte[] Bytes, CodecInfo Codec)> EncodeEditAsync(MergeSession s, MergeEntry entry, string content, CancellationToken ct)
    {
        var (current, _) = await CurrentResultAsync(s, entry, ct);
        if (current is null) throw new MergeException("The current result is a deletion; choose a version first.", MergeErrorKind.Refused);
        var d = ContentCodec.Decode(current, MaxEditable);
        var (filter, _) = await AttributesAsync(s, entry.Path);
        var (ok, why) = EditState(s, entry, d.Info, filter);
        if (!ok) throw new MergeException(why!, MergeErrorKind.Unsupported);
        if (Encoding.UTF8.GetByteCount(content) > MaxEditable * 2)
            throw new MergeException("Edited content exceeds the editor size limit.", MergeErrorKind.Usage);
        byte[] bytes;
        try { bytes = ContentCodec.Encode(content, d.Info); }
        catch (EncoderFallbackException) { throw new MergeException($"The edited text cannot be represented in {d.Info.Encoding}.", MergeErrorKind.Usage); }
        catch (ArgumentException ex) { throw new MergeException(ex.Message, MergeErrorKind.Usage); }
        if (bytes.Length > MaxEditable) throw new MergeException($"Edited content ({bytes.Length:N0} bytes) exceeds the editor limit ({MaxEditable:N0}).", MergeErrorKind.Usage);
        return (bytes, d.Info);
    }

    public async Task<MergeSession> SaveDraftAsync(MergeSession s, MergePlan plan, SaveDraftRequest req, CancellationToken ct)
    {
        var entry = await MutableEntryAsync(s, plan, req.EntryId, req.ExpectedSessionRevision, req.ExpectedContentHash, ct);
        var (bytes, _) = await EncodeEditAsync(s, entry, req.Content, ct);
        _store.WriteAtomic(_store.DraftFile(s.Id, entry.Id), bytes);
        s.Revision++;
        s.Drafts[entry.Id] = new DraftRecord { EntryId = entry.Id, ContentHash = Hash(bytes), Revision = s.Revision, At = DateTimeOffset.UtcNow };
        _store.Audit(s, $"draft saved for {entry.Path} ({bytes.Length} bytes, {Hash(bytes)[..19]})");
        _store.SaveSession(s);
        return s;
    }

    public async Task<MergeSession> ResolveAsync(MergeSession s, MergePlan plan, ResolveRequest req, CancellationToken ct)
    {
        var entry = await MutableEntryAsync(s, plan, req.EntryId, req.ExpectedSessionRevision, req.ExpectedContentHash, ct);
        var caps = entry.Capabilities;
        var mirror = _ws.Mirror(s);
        SideBlob? result; string? contentHash; bool markersAck = false;
        switch (req.Action)
        {
            case ResolutionAction.TakeTarget when caps.TakeTarget && entry.Target is not null:
                result = entry.Target; contentHash = Hash(await _git.ReadBlobAsync(mirror, result.Sha, ct)); break;
            case ResolutionAction.TakeSource when caps.TakeSource && entry.Source is not null:
                result = entry.Source; contentHash = Hash(await _git.ReadBlobAsync(mirror, result.Sha, ct)); break;
            case ResolutionAction.KeepModified when caps.KeepModified:
                result = entry.Conflict == ConflictKind.DeletedInSource ? entry.Target : entry.Source;
                if (result is null) throw new MergeException("There is no modified version to keep.", MergeErrorKind.Refused);
                contentHash = Hash(await _git.ReadBlobAsync(mirror, result.Sha, ct)); break;
            case ResolutionAction.Delete when caps.Delete:
                result = null; contentHash = null; break;
            case ResolutionAction.EditResult when caps.EditText:
                if (req.Content is null) throw new MergeException("Edit result requires content.", MergeErrorKind.Usage);
                var (bytes, _) = await EncodeEditAsync(s, entry, req.Content, ct);
                if (ContentCodec.CountMarkerLines(req.Content) > 0)
                {
                    if (!req.AcknowledgeMarkers)
                        throw new MergeException($"'{entry.Path}' still contains conflict-marker lines. Remove them, or confirm that they are intended content.", MergeErrorKind.Refused);
                    markersAck = true;
                }
                var mode = entry.Target?.Mode ?? entry.Source?.Mode ?? "100644";
                result = new SideBlob(mode, await _git.HashObjectAsync(mirror, bytes, ct));
                contentHash = Hash(bytes);
                break;
            default:
                throw new MergeException($"Action {req.Action} is not available for '{entry.Path}' ({entry.Conflict}).", MergeErrorKind.Unsupported);
        }
        await _ws.ApplyIndexResultAsync(s, entry.Path, result, ct);
        if ((await _ws.UnmergedAsync(s)).Any(u => u.Path == entry.Path))
            throw new MergeException($"'{entry.Path}' is still unmerged after staging.", MergeErrorKind.GitFailure);
        s.Revision++;
        s.Resolutions[entry.Id] = new ResolutionRecord
        {
            EntryId = entry.Id, Path = entry.Path, Base = entry.Base, Target = entry.Target, Source = entry.Source, Action = req.Action,
            Result = result, ResultContentHash = contentHash, MarkersAcknowledged = markersAck, Revision = s.Revision, At = DateTimeOffset.UtcNow,
        };
        if (s.Drafts.Remove(entry.Id))
        {
            var f = _store.DraftFile(s.Id, entry.Id);
            _store.Core.Fs.AssertNoLinksBetweenRootAndPath(f);
            if (File.Exists(f)) File.Delete(f);
        }
        _store.Audit(s, $"resolved {entry.Path}: {req.Action}{(result is null ? " (deleted)" : $" → {result.Mode} {result.Sha[..10]}")}{(markersAck ? " [markers acknowledged]" : "")}");
        _store.SaveSession(s);
        return s;
    }
}
