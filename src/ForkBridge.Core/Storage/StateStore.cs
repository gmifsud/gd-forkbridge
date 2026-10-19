using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;

namespace ForkBridge.Core.Storage;

/// <summary>Harness-owned persistent state:
///   state/mirrors/&lt;alias&gt;.git   bare mirrors (fetched from the user's checkout + its remote)
///   state/plans/&lt;planId&gt;/      immutable plan.json + patch artifacts
///   state/runs/&lt;runId&gt;/        run.json, worktrees, evidence, reports
///   state/bundles/&lt;id&gt;.json
/// Nothing here ever lives inside a user repository.</summary>
public sealed class StateStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Root { get; }
    public SafeFileSystem Fs { get; }

    public StateStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        var marker = Path.Combine(Root, SafeFileSystem.OwnershipMarker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "forkbridge-state-root");
        Fs = new SafeFileSystem(Root);
        foreach (var d in new[] { "mirrors", "plans", "runs", "bundles", "locks" }) Directory.CreateDirectory(Path.Combine(Root, d));
    }

    public string MirrorDir(string alias) => Path.Combine(Root, "mirrors", Sanitize(alias) + ".git");
    public string PlanDir(string id) => Path.Combine(Root, "plans", Sanitize(id));
    public string RunDir(string id) => Path.Combine(Root, "runs", Sanitize(id));

    public static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        var r = sb.ToString().Trim('.');
        if (r.Length == 0 || r.Contains("..")) throw new HarnessSafetyException($"Invalid identifier '{s}'.");
        return r;
    }

    /// <summary>Collision-resistant id: prefix + UTC timestamp + 48 random bits.</summary>
    public static string NewId(string prefix)
        => $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string Sha256(string s) => Sha256(Encoding.UTF8.GetBytes(s));

    // ---- plans (immutable) -------------------------------------------------------------------
    public void SavePlan(Plan plan)
    {
        var dir = PlanDir(plan.Id);
        if (File.Exists(Path.Combine(dir, "plan.json"))) throw new InvalidOperationException($"Plan {plan.Id} already exists and is immutable.");
        if (!Directory.Exists(dir)) Fs.CreateOwnedDirectory(dir, plan.Id);
        plan.ContentHash = null;
        var body = JsonSerializer.Serialize(plan, Json);
        plan.ContentHash = Sha256(body);
        File.WriteAllText(Path.Combine(dir, "plan.json"), JsonSerializer.Serialize(plan, Json));
        File.SetAttributes(Path.Combine(dir, "plan.json"), FileAttributes.ReadOnly);
    }

    public Plan LoadPlan(string id)
    {
        var file = Path.Combine(PlanDir(id), "plan.json");
        if (!File.Exists(file)) throw new FileNotFoundException($"Plan '{id}' not found.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(file), Json)!;
        var expected = plan.ContentHash;
        plan.ContentHash = null;
        var actual = Sha256(JsonSerializer.Serialize(plan, Json));
        plan.ContentHash = expected;
        if (expected != actual) throw new PlanIntegrityException($"Plan '{id}' was modified after creation (hash mismatch). Re-plan.");
        return plan;
    }

    public byte[] ReadPlanArtifact(Plan plan, string file, string expectedSha256)
    {
        var path = Path.Combine(PlanDir(plan.Id), Sanitize(file));
        var bytes = File.ReadAllBytes(path);
        if (Sha256(bytes) != expectedSha256) throw new PlanIntegrityException($"Plan artifact '{file}' was modified (hash mismatch). Re-plan.");
        return bytes;
    }

    public IEnumerable<Plan> ListPlans()
    {
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(Root, "plans")).OrderByDescending(x => x))
        {
            Plan? p = null;
            try { p = LoadPlan(Path.GetFileName(d)); } catch { }
            if (p is not null) yield return p;
        }
    }

    // ---- runs (mutable, append-mostly) -------------------------------------------------------
    public void SaveRun(Run run)
    {
        var file = Path.Combine(RunDir(run.Id), "run.json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(run, Json));
        File.Move(tmp, file, overwrite: true); // atomic replace -> crash-safe
    }

    public Run LoadRun(string id)
    {
        var file = Path.Combine(RunDir(id), "run.json");
        if (!File.Exists(file)) throw new FileNotFoundException($"Run '{id}' not found.");
        return JsonSerializer.Deserialize<Run>(File.ReadAllText(file), Json)!;
    }

    public IEnumerable<Run> ListRuns()
    {
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(Root, "runs")).OrderByDescending(x => x))
        {
            Run? r = null;
            try { r = LoadRun(Path.GetFileName(d)); } catch { }
            if (r is not null) yield return r;
        }
    }

    public void SaveBundle(Bundle b) => File.WriteAllText(Path.Combine(Root, "bundles", Sanitize(b.Id) + ".json"), JsonSerializer.Serialize(b, Json));
    public Bundle LoadBundle(string id)
    {
        var f = Path.Combine(Root, "bundles", Sanitize(id) + ".json");
        if (!File.Exists(f)) throw new FileNotFoundException($"Bundle '{id}' not found.");
        return JsonSerializer.Deserialize<Bundle>(File.ReadAllText(f), Json)!;
    }

    /// <summary>Cross-process lock for a mirror or run (exclusive file handle).</summary>
    public IDisposable Lock(string name, TimeSpan? wait = null)
    {
        var path = Path.Combine(Root, "locks", Sanitize(name) + ".lock");
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(200); }
        }
    }
}

public sealed class PlanIntegrityException(string message) : Exception(message);
