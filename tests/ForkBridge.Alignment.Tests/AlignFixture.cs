using System.Diagnostics;
using System.Text;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Core.Config;
using ForkBridge.Core.Infrastructure;

namespace ForkBridge.Alignment.Tests;

/// <summary>A primary repository and a fork cloned from it at a sync point S, then diverging so every
/// classification category is exercised. Paths mimic the shared-scope layout of the real repositories.</summary>
public sealed class AlignFixture : IDisposable
{
    public string Root { get; }
    public string Primary => Path.Combine(Root, "Primary");
    public string Fork => Path.Combine(Root, "Fork");
    public string StateDir => Path.Combine(Root, "state");
    public string FbConfig => Path.Combine(Root, "forkbridge.yml");
    public string AlignConfig => Path.Combine(Root, "forkbridge.alignment.yml");
    public string SyncBase { get; private set; } = "";
    public string PrimaryTip { get; private set; } = "";
    public string ForkTip { get; private set; } = "";
    public AlignGit Git { get; } = new(new ProcessRunner());

    public AlignFixture(string flavour = "backend")
    {
        Root = Path.Combine(Path.GetTempPath(), "fbalign-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        if (flavour == "backend") BuildBackend(); else BuildFrontend();
        File.WriteAllText(FbConfig, $"""
            version: 1
            stateDir: "{Esc(Path.Combine(Root, "fbstate"))}"
            repositories:
              qisl: {"{"} path: "{Esc(Primary)}", remote: origin, kind: dotnet {"}"}
              isl:  {"{"} path: "{Esc(Fork)}", remote: origin, kind: dotnet {"}"}
            """);
    }

    private static string Esc(string p) => p.Replace("\\", "/");

    public void WriteAlignConfig(string yaml) => File.WriteAllText(AlignConfig, yaml);

    public ResolvedProfile Profile(string name = "backend")
        => AlignmentConfigLoader.Resolve(AlignmentConfigLoader.Load(AlignConfig), ConfigLoader.Load(FbConfig), name);

    public static string G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = "Fixture"; psi.Environment["GIT_AUTHOR_EMAIL"] = "f@example.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "Fixture"; psi.Environment["GIT_COMMITTER_EMAIL"] = "f@example.com";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {e}");
        return o.Result.Trim();
    }

    public static void W(string repo, string rel, string content)
    {
        var f = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, Encoding.UTF8.GetBytes(content));
    }
    public static void D(string repo, string rel) => File.Delete(Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar)));
    public static string Commit(string repo, string msg) { G(repo, "add", "-A"); G(repo, "commit", "-q", "-m", msg); return G(repo, "rev-parse", "HEAD"); }

    private const string Props = """
        <Project>
          <ItemGroup>
            <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
            <PackageVersion Include="Serilog" Version="3.1.1" />
          </ItemGroup>
        </Project>
        """;

    private void BuildBackend()
    {
        Directory.CreateDirectory(Primary);
        G(Primary, "init", "-q", "-b", "Development");
        W(Primary, "Shared/A.cs", "namespace S;\npublic class A { int x = 1; }\n");
        W(Primary, "Shared/B.cs", "namespace S;\npublic class B { int y = 1; }\n");
        W(Primary, "Shared/C.cs", "line1\nline2\nline3\nline4\nline5\nline6\nline7\n");
        W(Primary, "Shared/E.cs", "conflict-base\n");
        W(Primary, "Shared/D.cs", "namespace S;\npublic class D { }\n");
        W(Primary, "Shared/Fmt.cs", "namespace S;\npublic class Fmt { }\n");
        W(Primary, "Shared/Stale.cs", "namespace S;\npublic class OldThing { }\n");
        W(Primary, "Shared/Stale2.cs", "namespace S;\npublic class Stale2 { }\n");
        W(Primary, "Shared/Renamed.cs", "namespace S;\npublic class Renamed { }\n");
        W(Primary, "Shared/Host/Program.cs", "host\n");
        W(Primary, "Directory.Packages.props", Props);
        W(Primary, "Product/OnlyQisl.cs", "outside scope\n");
        Commit(Primary, "pre-sync (primary still has the stale files)");
        D(Primary, "Shared/Stale.cs"); D(Primary, "Shared/Stale2.cs");
        W(Primary, "Shared/Same.cs", "namespace S;\npublic class Same { }\n");
        SyncBase = Commit(Primary, "base (primary deleted the stale files)");

        G(Root, "clone", "-q", "--no-local", Primary, Fork);
        G(Fork, "checkout", "-q", "-b", "development");

        // primary moves on
        W(Primary, "Shared/A.cs", "namespace S;\npublic class A { int x = 2; }\n");                 // BE_CHANGED
        W(Primary, "Shared/New.cs", "namespace S;\npublic class NewOne { }\n");                     // BE_ADDED
        W(Primary, "Shared/C.cs", "line1-primary\nline2\nline3\nline4\nline5\nline6\nline7\n");     // BOTH (clean)
        W(Primary, "Shared/E.cs", "conflict-primary\n");                                            // CONFLICT
        W(Primary, "Directory.Packages.props", Props.Replace("    <PackageVersion Include=\"Serilog\"", "    <PackageVersion Include=\"Polly\" Version=\"8.4.0\" />\n    <PackageVersion Include=\"Serilog\""));
        PrimaryTip = Commit(Primary, "primary work");

        // fork moves on
        W(Fork, "Shared/B.cs", "namespace S;\npublic class B { int y = 2; }\n");                    // ISL_CHANGED
        W(Fork, "Shared/C.cs", "line1\nline2\nline3\nline4\nline5\nline6\nline7-fork\n");          // BOTH (clean)
        W(Fork, "Shared/E.cs", "conflict-fork\n");                                                  // CONFLICT
        D(Fork, "Shared/D.cs");                                                                     // ISL_DELETED
        W(Fork, "Shared/Fmt.cs", "namespace S;\r\npublic   class Fmt { }\r\n");                     // FORMAT_ONLY
        G(Fork, "mv", "Shared/Renamed.cs", "Shared/Renamed2.cs");                                   // ISL_RENAMED
        W(Fork, "Shared/IslOnly.cs", "namespace S;\npublic class IslOnly { Stale2 s; }\n");          // ISL_ONLY, keeps Stale2
        W(Fork, "Shared/_Fork/Own.cs", "fork owned\n");                                             // FORK_OWNED
        W(Fork, "Shared/Stale.cs", "namespace S;\npublic class OldThing { }\n");                   // ISL_STALE -> DELETE_BOTH
        W(Fork, "Shared/Stale2.cs", "namespace S;\npublic class Stale2 { }\n");                    // ISL_STALE, referenced -> kept
        W(Fork, "Shared/Host/Program.cs", "isl host\n");                                            // protected: ignored
        W(Fork, "Directory.Packages.props", Props.Replace("    <PackageVersion Include=\"Serilog\" Version=\"3.1.1\" />", "    <PackageVersion Include=\"Serilog\" Version=\"3.1.1\" />\n    <PackageVersion Include=\"xunit\" Version=\"2.9.3\" />"));
        ForkTip = Commit(Fork, "fork work");
    }

    private void BuildFrontend()
    {
        Directory.CreateDirectory(Primary);
        G(Primary, "init", "-q", "-b", "Development");
        W(Primary, "app/src/a.service.ts", "export class AService { v = 1; }\n");
        W(Primary, "app/src/b.service.ts", "export class BService { v = 1; }\n");
        W(Primary, "app/package.json", "{\n  \"name\": \"app\",\n  \"dependencies\": {\n    \"rxjs\": \"7.8.2\"\n  }\n}\n");
        SyncBase = Commit(Primary, "base");
        G(Root, "clone", "-q", "--no-local", Primary, Fork);
        G(Fork, "checkout", "-q", "-b", "development");
        W(Primary, "app/src/a.service.ts", "export class AService { v = 2; }\n");
        var mid = Commit(Primary, "a v2");
        W(Primary, "app/src/a.service.ts", "export class AService { v = 3; }\n");
        W(Primary, "app/src/b.service.ts", "export class BService { v = 2; }\n");
        W(Primary, "app/package.json", "{\n  \"name\": \"app\",\n  \"dependencies\": {\n    \"rxjs\": \"7.8.2\",\n    \"tslib\": \"2.8.1\"\n  }\n}\n");
        PrimaryTip = Commit(Primary, "a v3");
        // fork imported only the first primary commit's change to a.service.ts (per-commit import) -> behind
        W(Fork, "app/src/a.service.ts", "export class AService { v = 2; }\n");
        W(Fork, "app/package.json", "{\n  \"name\": \"app\",\n  \"dependencies\": {\n    \"rxjs\": \"7.8.2\",\n    \"zone.js\": \"0.15.0\"\n  }\n}\n");
        ForkTip = Commit(Fork, "import a v2 + zone");
        _ = mid;
    }

    public const string BackendProfileYaml = """
        version: 1
        stateDir: "state"
        profiles:
          backend:
            primary: { repo: qisl, label: QiSL, legacyName: Backend, historyRefs: [], referenceRef: Development }
            fork:    { repo: isl,  label: ISL,  legacyName: Isl }
            syncBase: "@SYNC@"
            scope: [Shared, Directory.Packages.props]
            protected: [Shared/Host]
            forkOwned: ["*/_Fork/*"]
            formatInsensitive: [.cs, .props]
            rules: { manifestPrecedence: first, formatGated: false, behindDetection: false }
            manifest: { kind: directoryPackagesProps, files: ["Directory.Packages.props", "*/Directory.Packages.props"] }
            references: { analyzer: csharp }
            overrides: []
            neverPush: [Development, development]
        """;

    public const string FrontendProfileYaml = """
        version: 1
        stateDir: "state"
        profiles:
          frontend:
            primary: { repo: qisl, label: QiSL, legacyName: Frontend, historyRefs: [] }
            fork:    { repo: isl,  label: ISL,  legacyName: Isl }
            syncBase: "@SYNC@"
            scope: [app]
            formatInsensitive: [.ts, .json]
            rules: { manifestPrecedence: afterBehind, formatGated: true, behindDetection: true }
            manifest: { kind: packageJson, files: ["*/package.json"], sections: [dependencies, devDependencies] }
            references: { analyzer: typescript }
        """;

    public void Dispose() { try { ForceDelete(Root); } catch { } }

    private static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}
