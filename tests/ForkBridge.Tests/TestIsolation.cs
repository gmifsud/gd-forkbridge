using System.Runtime.CompilerServices;

namespace ForkBridge.Tests;

/// <summary>Runs before any test: isolates every git process started by the tests (fixture
/// helpers AND the engine under test) from the developer's system/global git configuration.
/// Without this, settings such as core.autocrlf=true change fixture checkouts, and system-level
/// credential helpers (e.g. Git Credential Manager) can be queried and return REAL credentials.</summary>
internal static class TestIsolation
{
    public static string EmptyGlobalConfig { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fbtest-gitconfig-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        EmptyGlobalConfig = Path.Combine(dir, "gitconfig");
        File.WriteAllText(EmptyGlobalConfig,
            "[core]\n\tautocrlf = false\n\tsafecrlf = false\n[init]\n\tdefaultBranch = development\n" +
            "[user]\n\tname = ForkBridge Test\n\temail = test@example.invalid\n[credential]\n\thelper =\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");          // ignore system gitconfig (GCM lives there on Windows)
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", EmptyGlobalConfig); // ignore ~/.gitconfig
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
        Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");
        foreach (var k in new[] { "GIT_CONFIG_COUNT", "GIT_ASKPASS", "SSH_ASKPASS" }) Environment.SetEnvironmentVariable(k, null);
    }
}
