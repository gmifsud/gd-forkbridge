using System.Runtime.CompilerServices;

namespace ForkBridge.Merge.Tests;

/// <summary>Isolates every git process of the test run from the developer's system/global configuration. The test
/// "global" config deliberately defines a smudge filter and a merge driver: the merge workspace must never see them.</summary>
internal static class TestIsolation
{
    public static string GlobalConfig { get; private set; } = "";
    public static string DriverMarkerDir { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fbmerge-gitconfig-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        DriverMarkerDir = Path.Combine(dir, "markers");
        Directory.CreateDirectory(DriverMarkerDir);
        GlobalConfig = Path.Combine(dir, "gitconfig");
        File.WriteAllText(GlobalConfig,
            "[core]\n\tautocrlf = false\n\tsafecrlf = false\n[init]\n\tdefaultBranch = develop\n" +
            "[user]\n\tname = Merge Test\n\temail = merge-test@example.invalid\n[credential]\n\thelper =\n" +
            "[filter \"fbtest\"]\n\tsmudge = fbtest-smudge-must-not-run\n\tclean = fbtest-clean-must-not-run\n\trequired = true\n" +
            "[merge \"fbtest\"]\n\tdriver = fbtest-driver-must-not-run %O %A %B\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", GlobalConfig);
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
        Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");
        foreach (var k in new[] { "GIT_CONFIG_COUNT", "GIT_ASKPASS", "SSH_ASKPASS" }) Environment.SetEnvironmentVariable(k, null);
    }
}
