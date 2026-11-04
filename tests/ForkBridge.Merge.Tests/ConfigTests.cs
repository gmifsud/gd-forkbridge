using ForkBridge.Core.Config;

namespace ForkBridge.Merge.Tests;

public class ConfigTests
{
    private static string Yaml(string merge) => $"version: 1\nstateDir: state\nrepositories:\n  app:\n    path: work/app\n{merge}";

    [Fact]
    public void Merge_section_is_optional_and_validated()
    {
        var cfg = ConfigLoader.LoadFromYaml(Yaml(""), "/tmp/fb.yml");
        Assert.Equal("forkbridge/merge", cfg.Merge.BranchPrefix);
        Assert.Contains("develop", cfg.Merge.ProtectedBranches);
        var bad = Assert.Throws<ConfigException>(() => ConfigLoader.LoadFromYaml(Yaml(
            "merge:\n  branchPrefix: '../x'\n  maxEditableBytes: 10\n  repositories:\n    nope:\n      validation:\n        steps:\n          - id: s\n            run: []\n"), "/tmp/fb.yml"));
        Assert.Contains("merge.repositories.nope references unknown repository", bad.Message);
        Assert.Contains("merge.branchPrefix", bad.Message);
        Assert.Contains("maxEditableBytes", bad.Message);
        Assert.Contains("'run' must list", bad.Message);
    }
}
