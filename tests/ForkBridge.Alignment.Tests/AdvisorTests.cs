using ForkBridge.Alignment.Advisor;

namespace ForkBridge.Alignment.Tests;

public class AdvisorTests
{
    /// <summary>Reproduces the 4412b4f48 story: the primary changed Repo.cs (P1) before the sync point S; the fork
    /// took S and later deliberately reverted Repo.cs to the pre-P1 content. A base before P1 would undo the fork's
    /// revert (fork-loss); S sees it as a deliberate fork change. The primary tip is too new: the fork never received its Other.cs.</summary>
    private static (AlignFixture Fx, string Old, string P1, string S, string Tip, string ForkTip) Build()
    {
        var fx = new AlignFixture("backend");
        var p = Path.Combine(fx.Root, "P"); var f = Path.Combine(fx.Root, "F");
        Directory.CreateDirectory(p);
        AlignFixture.G(p, "init", "-q", "-b", "Development");
        AlignFixture.W(p, "Shared/Repo.cs", "namespace S;\npublic class Repo { void Insert(int module) { } }\n");
        AlignFixture.W(p, "Shared/Other.cs", "namespace S;\npublic class Other { }\n");
        var old = AlignFixture.Commit(p, "old");
        AlignFixture.W(p, "Shared/Repo.cs", "namespace S;\npublic class Repo { void Insert(string module) { } }\n");
        var p1 = AlignFixture.Commit(p, "string module inserts");
        AlignFixture.W(p, "Shared/Other.cs", "namespace S;\npublic class Other { int v = 1; }\n");
        var s = AlignFixture.Commit(p, "sync point");
        AlignFixture.G(fx.Root, "clone", "-q", "--no-local", p, f);
        AlignFixture.W(p, "Shared/Other.cs", "namespace S;\npublic class Other { int v = 2; }\n");   // changed after the sync
        var tip = AlignFixture.Commit(p, "after sync");
        AlignFixture.W(f, "Shared/Repo.cs", "namespace S;\npublic class Repo { void Insert(int module) { } }\n");   // deliberate revert
        var forkTip = AlignFixture.Commit(f, "fork reverts string overloads");
        File.WriteAllText(fx.FbConfig, $"""
            version: 1
            stateDir: "{fx.Root.Replace('\\', '/')}/fbstate"
            repositories:
              qisl: {"{"} path: "{p.Replace('\\', '/')}" {"}"}
              isl:  {"{"} path: "{f.Replace('\\', '/')}" {"}"}
            """);
        fx.WriteAlignConfig(AlignFixture.BackendProfileYaml.Replace("@SYNC@", old));     // profile deliberately points at the wrong (old) base
        return (fx, old, p1, s, tip, forkTip);
    }

    [Fact]
    public async Task Recommends_the_true_sync_point_and_flags_fork_loss_on_an_older_base()
    {
        var (fx, old, p1, s, tip, forkTip) = Build();
        using var _ = fx;
        var res = await new SyncBaseAdvisor(fx.Profile(), fx.Git).AdviseAsync(new AdvisorRequest(tip, forkTip));
        Assert.Equal(s, res.Recommended);
        Assert.Equal(old, res.Configured);
        var cOld = res.Candidates.Single(c => c.Sha == old);
        var cS = res.Candidates.Single(c => c.Sha == s);
        Assert.Contains(cOld.RiskSample, r => r == "fork-loss: Shared/Repo.cs");
        Assert.Equal(0, cS.Risk);
        Assert.True(cS.Promotions >= 1);                                                 // the deliberate revert is a genuine fork change
        Assert.Contains("merge-base", cS.Sources);
        var line = res.Lines.Single(l => l.Selected);
        Assert.Equal(tip, line.Commits[0].Sha);                                          // newest first
        Assert.True(line.Commits.Single(c => c.Sha == tip).Risk > 0);                   // too new: promoting the fork's Other.cs would revert primary work
    }

    [Fact]
    public void Decide_reports_indifference_and_prefers_the_newest_ledger_entry()
    {
        CandidateScore C(string sha, string date, int score, string fp, bool transient = false, params string[] src)
            => new(sha.PadRight(40, '0'), date, "a", "s", src, score, 0, 0, 0, 0, 0, 0, 10, 1, [], transient, fp);
        var warnings = new List<string>();
        var (rec, conf, why) = SyncBaseAdvisor.Decide([C("aaaa", "2026-10-26", 0, "x", false, "configured"), C("bbbb", "2026-10-27", 0, "x", false, "ledger #2"), C("cccc", "2026-10-27", 0, "x", true, "sweep minimum")],
            "aaaa".PadRight(40, '0'), warnings);
        Assert.Equal("indifferent", conf);
        Assert.StartsWith("bbbb", rec);
        Assert.Contains(warnings, w => w.Contains("differs from the newest ledger entry"));
        Assert.Contains(why, w => w.Contains("does not depend on the SyncBase"));
    }

    [Fact]
    public void Decide_excludes_transient_bases_and_grades_confidence()
    {
        CandidateScore C(string sha, int score, int rows, bool transient, params string[] src)
            => new(sha.PadRight(40, '0'), "2026-10-20", "a", "s", src, score, 0, score, 0, 0, 0, 0, rows, 0.98, [], transient, sha);
        var (rec, conf, _) = SyncBaseAdvisor.Decide([C("t", 0, 170, true, "sweep minimum"), C("good", 6, 170, false, "sweep minimum", "overlay ≈ 385c4f8ef"), C("ef3", 9, 170, false, "configured")], null, []);
        Assert.StartsWith("good", rec);
        Assert.Equal("high", conf);
        var (_, low, _) = SyncBaseAdvisor.Decide([C("c56", 98, 249, false, "merge-base"), C("dcb", 115, 249, false, "sweep minimum")], null, []);
        Assert.Equal("low", low);
    }
}
