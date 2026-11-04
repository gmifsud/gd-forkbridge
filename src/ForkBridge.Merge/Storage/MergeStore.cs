using System.Text.Json;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Storage;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;

namespace ForkBridge.Merge.Storage;

/// <summary>Merge workspace state under &lt;stateDir&gt;/merge:
///   plans/&lt;id&gt;/plan.json        immutable, read-only, content-hashed (id + time excluded)
///   sessions/&lt;id&gt;/session.json  replaced atomically on every mutation; revision increments
///   sessions/&lt;id&gt;/audit.log     append-only mutation log
///   sessions/&lt;id&gt;/drafts/       saved draft content (outside the worktree)
///   sessions/&lt;id&gt;/workspace/    harness worktree of the alias mirror
///   refs/&lt;alias&gt;.json           last ref refresh (freshness)
/// Every directory is created through SafeFileSystem with an ownership marker.</summary>
public sealed class MergeStore
{
    public StateStore Core { get; }
    public string Root { get; }

    public MergeStore(StateStore core)
    {
        Core = core;
        Root = Path.Combine(core.Root, "merge");
        foreach (var d in new[] { "plans", "sessions", "refs" })
        {
            var p = Path.Combine(Root, d);
            core.Fs.AssertNoLinksBetweenRootAndPath(p);
            Directory.CreateDirectory(p);
        }
    }

    public string PlanDir(string id) => Path.Combine(Root, "plans", StateStore.Sanitize(id));
    public string SessionDir(string id) => Path.Combine(Root, "sessions", StateStore.Sanitize(id));
    public string DraftFile(string sessionId, string entryId) => Path.Combine(SessionDir(sessionId), "drafts", StateStore.Sanitize(entryId) + ".bin");

    // ---- plans ---------------------------------------------------------------------------------
    public static string ComputeAnalysisHash(MergePlan plan)
    {
        var canonical = plan with { Id = "", CreatedAt = default, AnalysisHash = null };
        return "sha256:" + StateStore.Sha256(JsonSerializer.Serialize(canonical, StateStore.Json));
    }

    public MergePlan SavePlan(MergePlan plan)
    {
        var dir = PlanDir(plan.Id);
        var file = Path.Combine(dir, "plan.json");
        if (File.Exists(file)) throw new InvalidOperationException($"Merge plan {plan.Id} already exists and is immutable.");
        Core.Fs.CreateOwnedDirectory(dir, plan.Id);
        plan.AnalysisHash = ComputeAnalysisHash(plan);
        File.WriteAllText(file, JsonSerializer.Serialize(plan, StateStore.Json));
        File.SetAttributes(file, FileAttributes.ReadOnly);
        return plan;
    }

    public MergePlan LoadPlan(string id)
    {
        var file = Path.Combine(PlanDir(id), "plan.json");
        if (!File.Exists(file)) throw new MergeException($"Merge plan '{id}' not found.", MergeErrorKind.NotFound);
        var plan = JsonSerializer.Deserialize<MergePlan>(File.ReadAllText(file), StateStore.Json)!;
        if (plan.AnalysisHash != ComputeAnalysisHash(plan))
            throw new MergeException($"Merge plan '{id}' was modified after creation (hash mismatch). Analyze again.", MergeErrorKind.Stale);
        return plan;
    }

    public IEnumerable<MergePlan> ListPlans(string? alias = null)
    {
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(Root, "plans")).OrderByDescending(x => x))
        {
            MergePlan? p = null;
            try { p = LoadPlan(Path.GetFileName(d)); } catch { }
            if (p is not null && (alias is null || p.RepositoryAlias == alias)) yield return p;
        }
    }

    // ---- sessions ------------------------------------------------------------------------------
    public void SaveSession(MergeSession s)
    {
        var dir = SessionDir(s.Id);
        var file = Path.Combine(dir, "session.json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, StateStore.Json));
        File.Move(tmp, file, overwrite: true);
    }

    public MergeSession LoadSession(string id)
    {
        var dir = SessionDir(id);
        var file = Path.Combine(dir, "session.json");
        if (!File.Exists(file)) throw new MergeException($"Merge session '{id}' not found.", MergeErrorKind.NotFound);
        if (Core.Fs.ReadOwner(dir) != id) throw new HarnessSafetyException($"Merge session directory '{dir}' is not owned by session {id}.");
        return JsonSerializer.Deserialize<MergeSession>(File.ReadAllText(file), StateStore.Json)!;
    }

    public IEnumerable<MergeSession> ListSessions()
    {
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(Root, "sessions")).OrderByDescending(x => x))
        {
            MergeSession? s = null;
            try { s = LoadSession(Path.GetFileName(d)); } catch { }
            if (s is not null) yield return s;
        }
    }

    public void Audit(MergeSession s, string line)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} r{s.Revision} {Redactor.Redact(line)}";
        s.Log.Add(entry);
        if (s.Log.Count > 400) s.Log.RemoveRange(0, s.Log.Count - 400);
        File.AppendAllText(Path.Combine(SessionDir(s.Id), "audit.log"), entry + "\n");
    }

    /// <summary>Exclusive cross-process lock for one session's mutations.</summary>
    public IDisposable LockSession(string id) => Core.Lock("merge-session-" + StateStore.Sanitize(id), TimeSpan.FromSeconds(30));

    public void WriteAtomic(string file, byte[] bytes)
    {
        Core.Fs.AssertNoLinksBetweenRootAndPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
    }

    // ---- ref freshness -------------------------------------------------------------------------
    private string RefsFile(string alias) => Path.Combine(Root, "refs", StateStore.Sanitize(alias) + ".json");

    public void SaveFreshness(string alias, RefFreshness f) => File.WriteAllText(RefsFile(alias), JsonSerializer.Serialize(f, StateStore.Json));

    public RefFreshness LoadFreshness(string alias, TimeSpan staleAfter)
    {
        var file = RefsFile(alias);
        if (!File.Exists(file)) return new RefFreshness { Stale = true, Note = "Refs have never been refreshed for this repository." };
        var f = JsonSerializer.Deserialize<RefFreshness>(File.ReadAllText(file), StateStore.Json)!;
        var stale = f.Offline || !f.RemoteFetched || f.RefreshedAt is null || DateTimeOffset.UtcNow - f.RefreshedAt > staleAfter;
        return f with { Stale = stale };
    }
}
