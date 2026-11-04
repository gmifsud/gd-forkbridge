using System.Text;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Planning;
using ForkBridge.Merge.Resolution;
using ForkBridge.Merge.Workspace;

namespace ForkBridge.Merge.Tests;

public class ContentTests
{
    private static async Task<(MergeEngine M, MergePlan Plan, MergeSession S)> ConflictOn(MergeFixture fx, string path, byte[] baseBytes, byte[] src, byte[] tgt)
    {
        fx.Switch("develop"); fx.WriteBytes(path, baseBytes); fx.Commit("base " + path); fx.Push("develop");
        fx.Switch("feature/x", create: true); fx.WriteBytes(path, src); fx.Commit("src"); fx.Push("feature/x");
        fx.Switch("develop"); fx.WriteBytes(path, tgt); fx.Commit("tgt"); fx.Push("develop");
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        return (m, plan, await m.CreateSessionAsync(plan.Id, CancellationToken.None));
    }

    private static async Task<byte[]> ResolveEditAndRead(MergeEngine m, MergePlan plan, MergeSession s, string path, string text)
    {
        var v = await MergeFixture.FileAsync(m, s.Id, plan, path);
        Assert.True(v.Editable, v.EditBlockedReason);
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(v.Entry.Id, ResolutionAction.EditResult, v.SessionRevision, v.ResultContentHash, text), CancellationToken.None);
        var f = await m.FinalizeAsync(s.Id, r.Session.Revision, CancellationToken.None);
        var mirror = m.Store.Core.MirrorDir("app");
        var blob = MergeFixture.Git(mirror, "rev-parse", $"{f.Session.CandidateSha}:{path}");
        return await m.Git.ReadBlobAsync(mirror, blob);
    }

    [Fact]
    public async Task Crlf_line_endings_are_preserved_and_editor_text_is_lf()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await ConflictOn(fx, "win.txt", "a\r\nb\r\nc\r\n"u8.ToArray(), "a\r\nSRC\r\nc\r\n"u8.ToArray(), "a\r\nTGT\r\nc\r\n"u8.ToArray());
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "win.txt");
        Assert.Equal("crlf", v.Result.Codec!.Eol);  // git writes markers with CRLF in a CRLF file
        Assert.DoesNotContain('\r', v.Result.Text!);
        var bytes = await ResolveEditAndRead(m, plan, s, "win.txt", "a\nMERGED\nc\n");
        Assert.Equal("a\r\nMERGED\r\nc\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Missing_final_newline_and_utf8_bom_are_preserved()
    {
        using var fx = new MergeFixture();
        byte[] Bom(string t) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(t)];
        var (m, plan, s) = await ConflictOn(fx, "bom.txt", Bom("x\ny\nz"), Bom("x\nSRC\nz"), Bom("x\nTGT\nz"));
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "bom.txt");
        Assert.Equal("utf-8-bom", v.Result.Codec!.Encoding);
        var bytes = await ResolveEditAndRead(m, plan, s, "bom.txt", "x\nMERGED\nz");
        Assert.Equal(Bom("x\nMERGED\nz"), bytes);
    }

    [Fact]
    public async Task Undecodable_content_cannot_be_edited_but_a_version_can_be_chosen()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await ConflictOn(fx, "latin1.txt", [0x61, 0x0A, 0x62, 0x0A], [0x61, 0x0A, 0xE9, 0x0A], [0x61, 0x0A, 0xE8, 0x0A]);
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "latin1.txt");
        Assert.False(v.Editable);
        Assert.Equal(Restriction.UnsupportedEncoding, v.Result.Codec!.Restriction);
        Assert.Null(v.Result.Text);
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.SaveDraftAsync(s.Id, new SaveDraftRequest(v.Entry.Id, v.SessionRevision, v.ResultContentHash, "a\nb\n"), CancellationToken.None));
        Assert.Equal(MergeErrorKind.Unsupported, ex.Kind);
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(v.Entry.Id, ResolutionAction.TakeSource, v.SessionRevision, v.ResultContentHash), CancellationToken.None);
        Assert.True(r.CanFinalize);
    }

    [Fact]
    public async Task Oversized_content_is_never_sent_as_editable_and_cannot_be_overwritten()
    {
        using var fx = new MergeFixture(maxEditable: 1024);
        string Big(string mid) => string.Join("\n", Enumerable.Range(0, 200).Select(i => i == 100 ? mid : "line " + i)) + "\n";
        var (m, plan, s) = await ConflictOn(fx, "big.txt", Encoding.UTF8.GetBytes(Big("base")), Encoding.UTF8.GetBytes(Big("SRC")), Encoding.UTF8.GetBytes(Big("TGT")));
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "big.txt");
        Assert.False(v.Editable);
        Assert.Null(v.Result.Text);           // no truncated preview is ever offered for editing
        Assert.Equal(Restriction.Oversized, v.Result.Codec!.Restriction);
        Assert.NotEmpty(v.TargetChanges.Diff); // diffs remain available for review
        await Assert.ThrowsAsync<MergeException>(() => m.ResolveAsync(s.Id,
            new ResolveRequest(v.Entry.Id, ResolutionAction.EditResult, v.SessionRevision, v.ResultContentHash, "line 0\n"), CancellationToken.None));
    }

    [Fact]
    public async Task Unicode_paths_and_spaces_round_trip()
    {
        using var fx = new MergeFixture();
        var path = "dir with space/ünïcødé ñame.txt";
        var (m, plan, s) = await ConflictOn(fx, path, "a\nb\n"u8.ToArray(), "a\nSRC\n"u8.ToArray(), "a\nTGT\n"u8.ToArray());
        Assert.Equal(path, plan.Entries.Single().Path);
        var bytes = await ResolveEditAndRead(m, plan, s, path, "a\nüñí\n");
        Assert.Equal("a\nüñí\n", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../b")]
    [InlineData(".git/config")]
    [InlineData("sub/.GIT/hooks/x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    [InlineData("a\\b")]
    [InlineData("bad\u0001name")]
    public void Unsafe_paths_are_rejected(string path)
    {
        Assert.NotNull(EntryClassifier.PathProblem(path));
        Assert.False(EntryClassifier.Capabilities(ConflictKind.Content, false, path).Supported);
    }

    [Fact]
    public void Codec_round_trips_utf16_and_flags_mixed_line_endings()
    {
        byte[] le = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a\r\nb\r\n")];
        var d = ContentCodec.Decode(le, 1 << 20);
        Assert.Equal(("utf-16le", "crlf", true), (d.Info.Encoding, d.Info.Eol, d.Info.Editable));
        Assert.Equal(le, ContentCodec.Encode(d.Text!, d.Info));
        var mixed = ContentCodec.Decode("a\r\nb\nc\n"u8.ToArray(), 1 << 20);
        Assert.False(mixed.Info.Editable);
        Assert.Equal(Restriction.MixedLineEndings, mixed.Info.Restriction);
        Assert.True(ContentCodec.Decode([0x00, 0x01], 1 << 20).Info.Binary);
        Assert.Equal(4, ContentCodec.CountMarkerLines("<<<<<<< a\nx\n||||||| b\n=======\ny\n>>>>>>> c\n"));
        Assert.Equal(0, ContentCodec.CountMarkerLines("a <<<<<<< b\n========\n"));
    }

    [Fact]
    public async Task Symlinked_directory_in_the_workspace_is_never_followed()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await ConflictOn(fx, "dir/f.txt", "a\nb\n"u8.ToArray(), "a\nSRC\n"u8.ToArray(), "a\nTGT\n"u8.ToArray());
        var outside = Path.Combine(fx.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "f.txt"), "outside\n");
        var view = await MergeFixture.FileAsync(m, s.Id, plan, "dir/f.txt");
        var dir = Path.Combine(s.Worktree, "dir");
        Directory.Move(dir, dir + ".moved");
        try { Directory.CreateSymbolicLink(dir, outside); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            return; // creating symlinks needs Developer Mode or elevation on Windows; nothing to test without one
        }
        await Assert.ThrowsAnyAsync<Exception>(() => m.ResolveAsync(s.Id,
            new ResolveRequest(view.Entry.Id, ResolutionAction.EditResult, view.SessionRevision, view.ResultContentHash, "a\nX\n"), CancellationToken.None));
        Assert.Equal("outside\n", File.ReadAllText(Path.Combine(outside, "f.txt")));
        await Assert.ThrowsAnyAsync<Exception>(() => MergeFixture.FileAsync(m, s.Id, plan, "dir/f.txt"));
    }
}
