using System.Text;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Merge.Tests;

public class ConflictTypeTests
{
    private static async Task<(MergeEngine M, MergePlan Plan, MergeSession S)> Session(MergeFixture fx, string source = "feature/x")
    {
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, source, "develop");
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        return (m, plan, s);
    }

    private static async Task<SessionView> Resolve(MergeEngine m, string sid, MergePlan plan, string path, ResolutionAction a, string? content = null)
    {
        var v = await MergeFixture.FileAsync(m, sid, plan, path);
        return await m.ResolveAsync(sid, new ResolveRequest(v.Entry.Id, a, v.SessionRevision, v.ResultContentHash, content), CancellationToken.None);
    }

    private static string Show(MergeEngine m, string rev) => MergeFixture.GitRaw(m.Store.Core.MirrorDir("app"), null, "show", rev);

    [Fact]
    public async Task Add_add_text_conflict_can_be_edited_or_take_a_version()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/new.txt", "from source\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/new.txt", "from target\n"));
        var (m, plan, s) = await Session(fx);
        var e = plan.Entries.Single();
        Assert.Equal(ConflictKind.AddAdd, e.Conflict);
        Assert.Null(e.Base);
        Assert.True(e.Capabilities.EditText);
        var v = await Resolve(m, s.Id, plan, "src/new.txt", ResolutionAction.TakeSource);
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        Assert.Equal("from source\n", Show(m, f.Session.CandidateSha + ":src/new.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modify_delete_is_an_explicit_keep_or_delete_never_an_empty_file(bool keep)
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src deletes", ("src/keep.txt", null));
        fx.OnBranch("develop", "develop", "tgt modifies", ("src/keep.txt", "modified in target\n"));
        var (m, plan, s) = await Session(fx);
        var e = plan.Entries.Single(x => x.Path == "src/keep.txt");
        Assert.Equal(ConflictKind.DeletedInSource, e.Conflict);
        Assert.True(e.Capabilities.KeepModified && e.Capabilities.Delete);
        Assert.False(e.Capabilities.EditText || e.Capabilities.TakeSource);
        var v = await Resolve(m, s.Id, plan, "src/keep.txt", keep ? ResolutionAction.KeepModified : ResolutionAction.Delete);
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        var tree = MergeFixture.Git(m.Store.Core.MirrorDir("app"), "ls-tree", "-r", "--name-only", f.Session.CandidateSha!);
        if (keep) Assert.Equal("modified in target\n", Show(m, f.Session.CandidateSha + ":src/keep.txt"));
        else Assert.DoesNotContain("src/keep.txt", tree.Split('\n'));
        Assert.False(File.Exists(Path.Combine(f.Session.Worktree, "src", "keep.txt")) && !keep);
    }

    [Fact]
    public async Task Deleted_in_target_modified_in_source_keeps_the_source_version()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src modifies", ("src/keep.txt", "modified in source\n"));
        fx.OnBranch("develop", "develop", "tgt deletes", ("src/keep.txt", null));
        var (m, plan, s) = await Session(fx);
        Assert.Equal(ConflictKind.DeletedInTarget, plan.Entries.Single().Conflict);
        var v = await Resolve(m, s.Id, plan, "src/keep.txt", ResolutionAction.KeepModified);
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        Assert.Equal("modified in source\n", Show(m, f.Session.CandidateSha + ":src/keep.txt"));
    }

    [Theory]
    [InlineData(ResolutionAction.TakeTarget, 3)]
    [InlineData(ResolutionAction.TakeSource, 2)]
    public async Task Binary_conflict_offers_only_a_version_choice(ResolutionAction action, byte expected)
    {
        using var fx = new MergeFixture();
        fx.WriteBytes("img.bin", [0, 1, 1]); fx.Switch("develop"); fx.Commit("bin"); fx.Push("develop");
        fx.Switch("feature/x", create: true); fx.WriteBytes("img.bin", [0, 1, 2]); fx.Commit("src bin"); fx.Push("feature/x");
        fx.Switch("develop"); fx.WriteBytes("img.bin", [0, 1, 3]); fx.Commit("tgt bin"); fx.Push("develop");
        var (m, plan, s) = await Session(fx);
        var e = plan.Entries.Single();
        Assert.True(e.Binary);
        Assert.False(e.Capabilities.EditText);
        var view = await MergeFixture.FileAsync(m, s.Id, plan, "img.bin");
        Assert.False(view.Editable);
        Assert.Null(view.Result.Text);
        await Assert.ThrowsAsync<MergeException>(() => m.ResolveAsync(s.Id, new ResolveRequest(e.Id, ResolutionAction.EditResult, view.SessionRevision, view.ResultContentHash, "x"), CancellationToken.None));
        var v = await Resolve(m, s.Id, plan, "img.bin", action);
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        var bytes = Encoding.Latin1.GetBytes(Show(m, f.Session.CandidateSha + ":img.bin"));
        Assert.Equal(expected, bytes[2]);
    }

    [Fact]
    public async Task Rename_delete_and_file_directory_conflicts_are_blocked_without_a_fake_resolve()
    {
        using var fx = new MergeFixture();
        fx.Write("src/big.txt", string.Join("\n", Enumerable.Range(0, 40).Select(i => "line " + i)) + "\n");
        fx.Switch("develop"); fx.Commit("big"); fx.Push("develop");
        fx.Switch("feature/x", create: true);
        MergeFixture.Git(fx.Checkout, "mv", "src/big.txt", "src/renamed.txt");
        fx.Write("src/keep.txt", "now a file in source\n"); fx.Write("dirfile", "file\n");
        fx.Commit("rename + file"); fx.Push("feature/x");
        fx.Switch("develop"); MergeFixture.Git(fx.Checkout, "rm", "-q", "src/big.txt"); fx.Write("dirfile/inner.txt", "dir\n"); fx.Commit("delete + dir"); fx.Push("develop");
        var (m, plan, s) = await Session(fx);
        var renamed = plan.Entries.Single(e => e.Path == "src/renamed.txt");
        Assert.Equal(ConflictKind.RenameDelete, renamed.Conflict);
        Assert.False(renamed.Capabilities.Supported);
        Assert.Contains(plan.Entries, e => e.Conflict == ConflictKind.FileDirectory && !e.Capabilities.Supported);
        Assert.Contains(plan.Findings, f => f.Code == "UNSUPPORTED_CONFLICTS");
        var view = await MergeFixture.FileAsync(m, s.Id, plan, "src/renamed.txt");
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.ResolveAsync(s.Id, new ResolveRequest(renamed.Id, ResolutionAction.TakeSource, view.SessionRevision, view.ResultContentHash), CancellationToken.None));
        Assert.Equal(MergeErrorKind.Unsupported, ex.Kind);
        var v = m.View(s.Id);
        Assert.False(v.CanFinalize);
        var fin = await Assert.ThrowsAsync<MergeException>(() => m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None));
        Assert.Equal(MergeErrorKind.Unsupported, fin.Kind);
    }

    [Fact]
    public async Task File_modes_are_preserved_through_edit_and_take()
    {
        using var fx = new MergeFixture();
        MergeFixture.Git(fx.Checkout, "config", "core.fileMode", "false"); // the exec bit lives in the index only
        void ExecCommit(string content, string msg)
        {
            fx.Write("run.sh", content);
            MergeFixture.Git(fx.Checkout, "add", "run.sh");
            MergeFixture.Git(fx.Checkout, "update-index", "--chmod=+x", "run.sh");
            MergeFixture.Git(fx.Checkout, "commit", "-q", "-m", msg);
        }
        fx.Switch("develop"); ExecCommit("#!/bin/sh\necho base\n", "exec base"); fx.Push("develop");
        fx.Switch("feature/x", create: true); ExecCommit("#!/bin/sh\necho source\n", "src"); fx.Push("feature/x");
        fx.Switch("develop"); ExecCommit("#!/bin/sh\necho target\n", "tgt"); fx.Push("develop");
        var (m, plan, s) = await Session(fx);
        Assert.Equal("100755", plan.Entries.Single().Target!.Mode);
        var v = await Resolve(m, s.Id, plan, "run.sh", ResolutionAction.EditResult, "#!/bin/sh\necho merged\n");
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        Assert.StartsWith("100755 ", MergeFixture.Git(m.Store.Core.MirrorDir("app"), "ls-tree", f.Session.CandidateSha!, "run.sh"));
    }

    [Fact]
    public async Task Changing_a_resolution_is_allowed_until_finalized_then_requires_reopen()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (m, plan, s) = await Session(fx);
        await Resolve(m, s.Id, plan, "src/shared.txt", ResolutionAction.TakeTarget);
        var v = await Resolve(m, s.Id, plan, "src/shared.txt", ResolutionAction.TakeSource);
        Assert.Equal(ResolutionAction.TakeSource, v.Session.Resolutions.Values.Single().Action);
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        var first = f.Session.CandidateSha;
        var view = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        Assert.False(view.Editable);
        await Assert.ThrowsAsync<MergeException>(() => m.ResolveAsync(s.Id, new ResolveRequest(view.Entry.Id, ResolutionAction.TakeTarget, view.SessionRevision, view.ResultContentHash), CancellationToken.None));
        var val = await m.ValidateAsync(s.Id, f.Session.Revision, true, CancellationToken.None);
        var ap = await m.ApproveAsync(s.Id, val.Session.Revision, first!, false, false, "t");
        var re = await m.ReopenAsync(s.Id, ap.Session.Revision);
        Assert.Equal(SessionState.Resolving, re.Session.State);
        Assert.Null(re.Session.Approval);
        Assert.Null(re.Session.CandidateSha);
        var v2 = await Resolve(m, s.Id, plan, "src/shared.txt", ResolutionAction.TakeTarget);
        var f2 = await m.FinalizeAsync(s.Id, v2.Session.Revision, CancellationToken.None);
        Assert.NotEqual(first, f2.Session.CandidateSha);
        Assert.Null(f2.CurrentValidation); // the old validation is bound to the old candidate
        var refused = await Assert.ThrowsAsync<MergeException>(() => m.ApproveAsync(s.Id, f2.Session.Revision, f2.Session.CandidateSha!, false, false, "t"));
        Assert.Contains("Validate this exact candidate", refused.Message);
    }
}
