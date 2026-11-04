using ForkBridge.Alignment.Analysis;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Manifests;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;
using ForkBridge.Alignment.Text;

namespace ForkBridge.Alignment.Tests;

public class WildcardTests
{
    [Theory]
    [InlineData("QiSL.Fabric/ISL.Overrides/X.cs", "QiSL.Fabric/ISL.Overrides/*", true)]
    [InlineData("a/b/_Fork/c/d.cs", "*/_Fork/*", true)]
    [InlineData("a/b/c.Fork.cs", "*.Fork.cs", true)]
    [InlineData("a/b/c.fork.cs", "*.Fork.cs", false)]               // -clike is case-sensitive
    [InlineData("x/packages.lock.json", "*/packages.lock.json", true)]
    [InlineData("packages.lock.json", "*/packages.lock.json", false)]
    [InlineData("a/registry/x.module.registry.ts", "a/registry/*.module.registry.ts", true)]
    [InlineData("abc", "a?c", true)]
    [InlineData("abc", "a[bx]c", true)]
    [InlineData("a.b", "a.b", true)]
    [InlineData("axb", "a.b", false)]
    public void Clike_semantics(string text, string pattern, bool expected) => Assert.Equal(expected, Wildcard.IsMatch(text, pattern));
}

public class TextOpsTests
{
    [Fact]
    public void Format_key_ignores_whitespace_bom_and_line_endings()
    {
        var a = "\uFEFFnamespace S;\r\npublic   class X { }\r\n"u8.ToArray();
        var b = "namespace S;\npublic class X { }\n"u8.ToArray();
        Assert.Equal(TextOps.FormatKey(a), TextOps.FormatKey(b));
        Assert.NotEqual(TextOps.FormatKey(b), TextOps.FormatKey("namespace S;\npublic class Y { }\n"u8.ToArray()));
    }

    [Fact]
    public void Content_key_ignores_bom_and_crlf_only()
    {
        Assert.Equal(TextOps.ContentKey("\uFEFFa\r\nb"u8.ToArray()), TextOps.ContentKey("a\nb"u8.ToArray()));
        Assert.NotEqual(TextOps.ContentKey("a  \nb"u8.ToArray()), TextOps.ContentKey("a\nb"u8.ToArray()));
    }
}

public class ClassifierTests
{
    private static readonly RuleFlags Be = new() { ManifestPrecedence = "first", FormatGated = false, BehindDetection = false };
    private static readonly RuleFlags Fe = new() { ManifestPrecedence = "afterBehind", FormatGated = true, BehindDetection = true };
    private static bool Fmt(string a, string b) => a.TrimEnd('~') == b.TrimEnd('~');   // "x~" is a format variant of "x"

    private static (Category, Decision)? C(string? b, string? f, string? p, RuleFlags r, bool fi = true, bool manifest = false, bool clean = false, bool behind = false)
    {
        var res = Classifier.Classify(new ClassifyInput("p", b, f, p, fi, manifest, clean, behind, Fmt), r, "Backend", "ISL");
        return res is null ? null : (res.Value.Category, res.Value.Decision);
    }

    [Fact] public void Identical_is_not_a_row() => Assert.Null(C("b", "x", "x", Be));
    [Fact] public void Primary_only_change() => Assert.Equal((Category.PrimaryChanged, Decision.TakePrimary), C("b", "b", "p", Be));
    [Fact] public void Primary_added() => Assert.Equal((Category.PrimaryAdded, Decision.TakePrimary), C(null, null, "p", Be));
    [Fact] public void Primary_deleted() => Assert.Equal((Category.PrimaryDeleted, Decision.TakePrimary), C("b", "b", null, Be));
    [Fact] public void Fork_change_is_promoted() => Assert.Equal((Category.ForkChanged, Decision.TakeFork), C("b", "f", "b", Be));
    [Fact] public void Fork_deletion_is_restored() => Assert.Equal((Category.ForkDeleted, Decision.TakePrimary), C("b", null, "b", Be));
    [Fact] public void Fork_addition() => Assert.Equal((Category.ForkAdded, Decision.KeepForkOnly), C(null, "f", null, Be));
    [Fact] public void Both_changed_clean() => Assert.Equal((Category.BothChanged, Decision.Merged), C("b", "f", "p", Be, clean: true));
    [Fact] public void Both_changed_conflict() => Assert.Equal((Category.Conflict, Decision.Manual), C("b", "f", "p", Be));
    [Fact] public void Modify_delete_conflict() => Assert.Equal((Category.Conflict, Decision.Manual), C("b", null, "p", Be));
    [Fact] public void Format_only_fork_change_counts_as_unchanged() => Assert.Equal((Category.FormatOnly, Decision.TakePrimary), C("b", "b~", "b", Be));
    [Fact] public void Format_only_fork_with_primary_change() => Assert.Equal((Category.PrimaryChanged, Decision.TakePrimary), C("b", "b~", "p", Be));
    [Fact] public void Format_only_between_sides() => Assert.Equal((Category.FormatOnly, Decision.TakePrimary), C("b", "x~", "x", Be));

    [Fact] public void Backend_format_check_between_sides_is_ungated() => Assert.Equal((Category.FormatOnly, Decision.TakePrimary), C("b", "x~", "x", Be, fi: false));
    [Fact] public void Frontend_format_check_between_sides_is_gated() => Assert.Equal((Category.BothChanged, Decision.Merged), C("b", "x~", "x", Fe, fi: false, clean: true));

    [Fact] public void Backend_manifest_union_even_when_one_sided() => Assert.Equal((Category.Packages, Decision.ManifestUnion), C("b", "b", "p", Be, manifest: true));
    [Fact] public void Frontend_manifest_one_sided_change_is_taken() => Assert.Equal((Category.PrimaryChanged, Decision.TakePrimary), C("b", "b", "p", Fe, manifest: true));
    [Fact] public void Frontend_manifest_union_when_both_changed() => Assert.Equal((Category.Packages, Decision.ManifestUnion), C("b", "f", "p", Fe, manifest: true));

    [Fact] public void Frontend_behind() => Assert.Equal((Category.ForkBehind, Decision.TakePrimary), C("b", "old", "p", Fe, behind: true));
    [Fact] public void Frontend_stale() => Assert.Equal((Category.ForkStale, Decision.DeleteBoth), C("b", "old", null, Fe, behind: true));
    [Fact] public void Backend_ignores_behind() => Assert.Equal((Category.Conflict, Decision.Manual), C("b", "old", "p", Be, behind: true));

    [Fact]
    public void Side_targets_follow_the_decision()
    {
        var row = new AlignmentRow { Path = "x", Primary = new("p", "100644"), Fork = new("f", "100644"), Merged = new("m", "100644"), Decision = Decision.KeepBoth };
        Assert.Equal("p", Classifier.TargetFor(row, Side.Primary).Ref!.Sha);
        Assert.Equal("f", Classifier.TargetFor(row, Side.Fork).Ref!.Sha);
        row.Decision = Decision.TakeFork; Assert.Equal("f", Classifier.TargetFor(row, Side.Primary).Ref!.Sha);
        row.Decision = Decision.DeleteBoth; Assert.True(Classifier.TargetFor(row, Side.Primary).Absent);
        row.Decision = Decision.ManifestUnion; Assert.True(Classifier.TargetFor(row, Side.Fork).IsUnion);
    }

    [Theory]
    [InlineData("TAKE_BE", Decision.TakePrimary)] [InlineData("TAKE_FE", Decision.TakePrimary)] [InlineData("TAKE_ISL", Decision.TakeFork)]
    [InlineData("KEEP_ISL_ONLY", Decision.KeepForkOnly)] [InlineData("PACKAGES_UNION", Decision.ManifestUnion)] [InlineData("KeepBoth", Decision.KeepBoth)]
    public void Legacy_decisions_parse(string token, Decision d) => Assert.Equal(d, DecisionNames.Parse(token));

    [Fact]
    public void Legacy_tokens_round_trip()
    {
        foreach (var c in Enum.GetValues<Category>()) Assert.Equal(c, DecisionNames.ParseLegacyCategory(DecisionNames.Legacy(c, "FE")));
        foreach (var d in Enum.GetValues<Decision>()) Assert.Equal(d, DecisionNames.Parse(DecisionNames.Legacy(d, "BE")));
    }
}

public class ManifestTests
{
    private const string Props = "<Project>\r\n  <ItemGroup>\r\n    <PackageVersion Include=\"A\" Version=\"1.0.0\" />\r\n    <PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" />\r\n  </ItemGroup>\r\n</Project>\r\n";
    private const string OtherProps = "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"a\" Version=\"2.0.0\" />\n    <PackageVersion Include=\"B\" Version=\"3.0.0\" />\n  </ItemGroup>\n</Project>\n";

    [Fact]
    public void Props_union_is_add_only_preserves_crlf_and_applies_overrides()
    {
        var r = new DirectoryPackagesPropsMerger().Merge(Props, OtherProps, "ISL", new Dictionary<string, string> { ["Microsoft.NET.Test.Sdk"] = "17.4.1" });
        Assert.Contains("<PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.4.1\" />", r.Text);
        Assert.Contains("    <!-- Added by branch alignment: present in ISL -->\r\n    <PackageVersion Include=\"B\" Version=\"3.0.0\" />\r\n  </ItemGroup>", r.Text);
        Assert.DoesNotContain("Include=\"a\"", r.Text);                                   // ids are case-insensitive: A already present
        Assert.Contains(r.Report, x => x.StartsWith("DIFFER A here=1.0.0 ISL=2.0.0"));
        Assert.Contains(r.Report, x => x.StartsWith("SET    Microsoft.NET.Test.Sdk 17.0.0 -> 17.4.1"));
    }

    [Fact]
    public void Package_json_union_inserts_after_the_brace_and_stays_valid()
    {
        var mine = "{\n  \"dependencies\": {\n    \"rxjs\": \"7.8.2\"\n  },\n  \"devDependencies\": {}\n}\n";
        var other = "{\n  \"dependencies\": {\n    \"rxjs\": \"7.8.1\",\n    \"tslib\": \"2.8.1\"\n  },\n  \"devDependencies\": {\n    \"typescript\": \"5.9.3\"\n  }\n}\n";
        var r = new PackageJsonMerger(["dependencies", "devDependencies"]).Merge(mine, other, "ISL", new Dictionary<string, string>());
        Assert.Contains("\"dependencies\": {\n    \"tslib\": \"2.8.1\",\n    \"rxjs\": \"7.8.2\"", r.Text);
        Assert.Contains("\"devDependencies\": {\n    \"typescript\": \"5.9.3\"\n  }", r.Text);
        Assert.Contains(r.Report, x => x == "DIFFER rxjs here=7.8.2 ISL=7.8.1 (left as is)");
        System.Text.Json.JsonDocument.Parse(r.Text).Dispose();
    }

    [Fact]
    public void Package_json_missing_section_refuses()
        => Assert.Throws<InvalidOperationException>(() => new PackageJsonMerger(["devDependencies"])
            .Merge("{ \"name\": \"x\" }", "{ \"devDependencies\": { \"a\": \"1\" } }", "ISL", new Dictionary<string, string>()));
}

public class AnalyzerTests
{
    [Fact]
    public void Csharp_top_level_types_ignore_comments_strings_and_nested_types()
    {
        var src = "namespace N;\n// class Commented\npublic sealed class Outer\n{\n        class Nested { }\n    string s = \"class InString\";\n}\ninternal record Rec;\npublic enum E { }\n";
        Assert.Equal(["Outer", "Rec", "E"], CSharpReferenceAnalyzer.TopLevelTypeNames(src));
    }

    [Fact]
    public void Csharp_reference_excludes_same_name_declarations()
    {
        var a = new CSharpReferenceAnalyzer();
        Assert.False(a.References("public class Stale2 { }", ["Stale2"], null));
        Assert.True(a.References("class X { Stale2 s; }", ["Stale2"], null));
        Assert.False(a.References("class X { /* Stale2 */ string t = \"Stale2\"; }", ["Stale2"], null));
    }

    [Fact]
    public void Typescript_exports_and_tokens()
    {
        var a = new TypeScriptReferenceAnalyzer();
        Assert.Equal(["Foo", "bar", "BAZ"], TypeScriptReferenceAnalyzer.ExportNames("export class Foo {}\nexport function bar() {}\nexport const BAZ = 1;\nconst hidden = 2;\n"));
        Assert.Equal("user-list.component", a.Token("x/user-list.component.ts"));
        Assert.Null(a.Token("x/index.ts"));
        Assert.Equal("logo.svg", a.Token("x/logo.svg"));
        Assert.True(a.References("import { X } from './user-list.component';", [], "user-list.component"));
        Assert.False(a.References("import { X } from './user-list.components';", [], "user-list.component"));
        Assert.True(a.References("import { X } from './user-list.component.spec';", [], "user-list.component"));   // PS semantics: a following dot still matches
    }
}

public class ConfigTests
{
    [Fact]
    public void Junction_paths_are_rejected()
    {
        Assert.True(PathCanon.UsesJunction(@"C:\Repos\Backend"));
        Assert.True(PathCanon.UsesJunction("c:/Repos"));
        Assert.False(PathCanon.UsesJunction(@"D:\Repos\Backend"));
    }

    [Fact]
    public void Invalid_profile_is_reported_with_every_problem()
    {
        using var fx = new AlignFixture("frontend");
        fx.WriteAlignConfig("""
            version: 1
            stateDir: "state"
            profiles:
              bad:
                primary: { repo: qisl, label: QiSL }
                fork:    { repo: nope, label: ISL }
                scope: ["/abs"]
                rules: { manifestPrecedence: sometimes }
                overrides: [ { pattern: "x", decision: TAKE_EVERYTHING } ]
                guards: [ { name: g, side: both, pattern: "(" } ]
            """);
        var ex = Assert.Throws<ForkBridge.Core.Config.ConfigException>(() => fx.Profile("bad"));
        foreach (var s in new[] { "'nope' is not a repository alias", "repo-relative", "manifestPrecedence", "TAKE_EVERYTHING", "invalid regex" })
            Assert.Contains(s, ex.Message);
    }

    [Fact]
    public void Profile_hash_changes_with_content()
    {
        var p = new AlignmentProfile { Scope = ["a"] };
        var h1 = AlignmentConfigLoader.Hash(p);
        p.Scope.Add("b");
        Assert.NotEqual(h1, AlignmentConfigLoader.Hash(p));
    }
}
