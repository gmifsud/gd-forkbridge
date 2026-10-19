using System.Diagnostics;
using System.Text;
using ForkBridge.Core;
using ForkBridge.Core.Config;
using ForkBridge.Core.Validation;

namespace ForkBridge.Tests.Fixtures;

/// <summary>Builds throwaway repositories with real git histories that mimic the QiSL/ISL layout:
/// a backend pair sharing history with identical component paths, and a frontend pair whose ISL
/// fork moved the shared library to a different path.</summary>
public sealed class RepoFixture : IDisposable
{
    public string Root { get; }
    public string QislBackend => Path.Combine(Root, "work", "Backend");
    public string IslBackend => Path.Combine(Root, "work", "ISL-Backend");
    public string QislFrontend => Path.Combine(Root, "work", "Frontend");
    public string IslFrontend => Path.Combine(Root, "work", "ISL-Frontend");
    public string Remote(string name) => Path.Combine(Root, "remotes", name + ".git");
    public string ConfigPath => Path.Combine(Root, "forkbridge.yml");
    public string StateDir => Path.Combine(Root, "state");
    public static string Tool => Path.Combine(AppContext.BaseDirectory, "FixtureTool.dll");

    private static int _seq;

    public RepoFixture(bool frontend = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "fbtest-" + Interlocked.Increment(ref _seq) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Root, "work"));
        Directory.CreateDirectory(Path.Combine(Root, "remotes"));
        BuildBackend();
        if (frontend) BuildFrontend();
        WriteConfig();
    }

    // ---------------------------------------------------------------------------------------------
    public static string Git(string dir, params string[] args) => GitEnv(dir, null, args);

    public static string GitEnv(string dir, Dictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = "Fixture Author";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "author@example.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "Fixture Committer";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "committer@example.com";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} in {dir} failed: {e}");
        return o.Result.Trim();
    }

    public static void Write(string repo, string rel, string content) => WriteBytes(repo, rel, Encoding.UTF8.GetBytes(content));

    public static void WriteBytes(string repo, string rel, byte[] content)
    {
        var p = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
    }

    public static string Commit(string repo, string message, string? author = null)
    {
        Git(repo, "add", "-A");
        var env = author is null ? null : new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = author.ToLowerInvariant().Replace(' ', '.') + "@example.com" };
        GitEnv(repo, env, "commit", "-q", "-m", message);
        return Git(repo, "rev-parse", "HEAD");
    }

    public static void Push(string repo, string branch = "development") => Git(repo, "push", "-q", "origin", branch);

    private void InitRepo(string dir, string remote)
    {
        Git(Path.Combine(Root, "remotes"), "init", "-q", "--bare", "-b", "development", remote);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "development");
        Git(dir, "config", "core.autocrlf", "false");
        Git(dir, "remote", "add", "origin", Remote(Path.GetFileNameWithoutExtension(remote)));
    }

    private void ForkFrom(string sourceRemote, string dir, string remote)
    {
        Git(Path.Combine(Root, "remotes"), "-c", "core.autocrlf=false", "clone", "-q", "--bare", Remote(sourceRemote), remote + ".git");
        Git(Path.Combine(Root, "work"), "-c", "core.autocrlf=false", "clone", "-q", "--config", "core.autocrlf=false", Remote(remote), Path.GetFileName(dir));
        Git(dir, "config", "core.autocrlf", "false");
    }

    private void BuildBackend()
    {
        var q = QislBackend;
        InitRepo(q, "Backend.git");
        Write(q, "Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """);
        Write(q, "Fabric/Kernel/Kernel.Domain/Kernel.Domain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Newtonsoft.Json" /></ItemGroup>
            </Project>
            """);
        Write(q, "Fabric/Kernel/Kernel.Domain/Entity.cs", "namespace Kernel.Domain;\n\npublic class Entity\n{\n    public int Id { get; set; }\n    public string Name { get; set; } = \"\";\n}\n");
        Write(q, "Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"\";\n}\n");
        Write(q, "Fabric/Platform/Platform.Core/Platform.Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../Kernel/Kernel.Domain/Kernel.Domain.csproj" /></ItemGroup>
            </Project>
            """);
        Write(q, "Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\n\npublic class Service\n{\n    public string Describe(Kernel.Domain.Entity e) => e.Name;\n}\n");
        Write(q, "Fabric/Platform/Web.Host/appsettings.json", "{ \"Env\": \"qisl-base\" }\n");
        Write(q, "App.VAM/Vam/Vam.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><Reference Include="Kernel.Domain"><HintPath>../../lib/Kernel.Domain.dll</HintPath></Reference></ItemGroup>
            </Project>
            """);
        Write(q, "App.VAM/Vam/Vehicle.cs", "namespace Vam;\npublic class Vehicle : Kernel.Domain.Entity { }\n");
        WriteBytes(q, "lib/Kernel.Domain.dll", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0xFF, 0x00, 0x01]);
        Write(q, "README.md", "# QiSL Backend\n");
        Write(q, "docs/notes.md", "notes\n");
        Commit(q, "Initial platform");
        Push(q);

        ForkFrom("Backend", IslBackend, "ISL-Backend");
        var i = IslBackend;
        Git(i, "rm", "-q", "-r", "App.VAM");
        Write(i, "App.MTL/Mtl/Mtl.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><Reference Include="Kernel.Domain"><HintPath>../../lib/Kernel.Domain.dll</HintPath></Reference></ItemGroup>
            </Project>
            """);
        Write(i, "App.MTL/Mtl/FuelTransaction.cs", "namespace Mtl;\npublic class FuelTransaction : Kernel.Domain.Entity\n{\n    public string Label => Name;\n}\n");
        Write(i, "Fabric/Platform/Web.ISL.Host/appsettings.json", "{ \"Env\": \"isl-host\" }\n");
        Write(i, "Fabric/Platform/Web.Host/appsettings.json", "{ \"Env\": \"isl-base\" }\n");
        Write(i, "README.md", "# ISL Backend\n");
        Commit(i, "ISL fork: MTL module and ISL host");
        Push(i);
    }

    private void BuildFrontend()
    {
        var q = QislFrontend;
        InitRepo(q, "Frontend.git");
        Write(q, "package.json", "{\n  \"name\": \"qisl-frontend\",\n  \"packageManager\": \"npm@10.9.0\",\n  \"dependencies\": {\n    \"@angular/core\": \"21.2.23\",\n    \"rxjs\": \"7.8.1\"\n  }\n}\n");
        Write(q, "package-lock.json", "{ \"lockfileVersion\": 3 }\n");
        Write(q, "tsconfig.json", "{\n  \"compilerOptions\": {\n    \"baseUrl\": \".\",\n    \"paths\": { \"@shared/*\": [\"libs/shared-ui/src/*\"] }\n  }\n}\n");
        Write(q, "libs/shared-ui/src/button.component.ts", "import { Component } from '@angular/core';\n\n@Component({ selector: 'fb-button', template: '<button>{{label}}</button>' })\nexport class ButtonComponent {\n  label = 'OK';\n}\n");
        Write(q, "libs/shared-ui/src/format.ts", "export function formatMoney(v: number): string {\n  return v.toFixed(2);\n}\n");
        Write(q, "libs/shared-ui/src/branding/theme.ts", "export const brand = 'qisl';\n");
        Write(q, "apps/qisl-app/src/main.ts", "import { formatMoney } from '@shared/format';\nconsole.log(formatMoney(1));\n");
        Commit(q, "Initial frontend");
        Push(q);

        ForkFrom("Frontend", IslFrontend, "ISL-Frontend");
        var i = IslFrontend;
        Git(i, "mv", "libs/shared-ui", "projects-common-shared-ui");
        Directory.CreateDirectory(Path.Combine(i, "projects", "common"));
        Git(i, "mv", "projects-common-shared-ui", "projects/common/shared-ui");
        Git(i, "rm", "-q", "-r", "apps/qisl-app");
        Write(i, "projects/common/shared-ui/src/branding/theme.ts", "export const brand = 'isl';\n");
        Write(i, "apps/isl-app/src/main.ts", "import { formatMoney } from '@shared/format';\nimport { ButtonComponent } from '@shared/button.component';\nconsole.log(formatMoney(2), ButtonComponent);\n");
        Write(i, "apps/isl-app/src/app.html", "<fb-button></fb-button>\n");
        Write(i, "tsconfig.json", "{\n  \"compilerOptions\": {\n    \"baseUrl\": \".\",\n    \"paths\": { \"@shared/*\": [\"projects/common/shared-ui/src/*\"], \"@isl/*\": [\"apps/isl-app/src/*\"] }\n  }\n}\n");
        Write(i, "package.json", "{\n  \"name\": \"isl-frontend\",\n  \"packageManager\": \"npm@10.9.0\",\n  \"dependencies\": {\n    \"@angular/core\": \"21.2.23\",\n    \"rxjs\": \"7.8.1\"\n  }\n}\n");
        Commit(i, "ISL fork: move shared-ui, ISL app");
        Push(i);
    }

    /// <summary>Validation profile YAML, indented for pairs.&lt;pair&gt;.validation. Extra steps are
    /// given as (id, kind, required, args...) and rendered with absolute indentation.</summary>
    public static string ValidationBlock(bool trusted, IEnumerable<(string Id, string Kind, bool Required, string[] Args)> extra, string forbidden = "BUILD_BREAK")
    {
        string Q(string x) => "\"" + x.Replace("\\", "/") + "\"";
        var sb = new StringBuilder();
        sb.Append("      default:\n");
        sb.Append($"        trusted: {trusted.ToString().ToLowerInvariant()}\n");
        sb.Append("        steps:\n");
        var steps = new List<(string Id, string Kind, bool Required, string[] Args)> { ("build-shared-and-consumers", "build", true, ["absent", forbidden, "."]) };
        steps.AddRange(extra);
        foreach (var st in steps)
        {
            sb.Append($"          - id: {st.Id}\n            kind: {st.Kind}\n            required: {st.Required.ToString().ToLowerInvariant()}\n            timeoutSeconds: 120\n");
            sb.Append($"            run: [{Q("dotnet")}, {Q(Tool)}, {string.Join(", ", st.Args.Select(Q))}]\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    public List<(string Id, string Kind, bool Required, string[] Args)> BackendExtraValidation { get; } = new();
    public List<(string Id, string Kind, bool Required, string[] Args)> FrontendExtraValidation { get; } = new();
    public string BackendModes { get; set; } = "[pick, reconcile]";
    public bool BackendTrusted { get; set; } = true;
    public string BackendProtected { get; set; } = "[main, master, development, \"release/*\"]";

    public void WriteConfig()
    {
        string P(string p) => p.Replace("\\", "/");
        var yaml = $$"""
            version: 1
            stateDir: "{{P(StateDir)}}"
            repositories:
              qisl-backend:  { path: "{{P(QislBackend)}}", kind: dotnet }
              isl-backend:   { path: "{{P(IslBackend)}}", kind: dotnet }
              qisl-frontend: { path: "{{P(QislFrontend)}}", kind: angular }
              isl-frontend:  { path: "{{P(IslFrontend)}}", kind: angular }
            publish:
              provider: none
            pairs:
              backend:
                products: { qisl: qisl-backend, isl: isl-backend }
                defaults: { targetBase: development, sourceBase: development }
                directions:
                  - { from: qisl, to: isl, modes: {{BackendModes}} }
                  - { from: isl, to: qisl, modes: {{BackendModes}} }
                components:
                  - id: kernel
                    status: confirmed
                    paths: { qisl: Fabric/Kernel, isl: Fabric/Kernel }
                  - id: platform
                    status: confirmed
                    paths: { qisl: Fabric/Platform, isl: Fabric/Platform }
                    dependsOn: [kernel]
                  - id: packages
                    status: confirmed
                    paths: { qisl: Directory.Packages.props, isl: Directory.Packages.props }
                  - id: reporting
                    status: proposed
                    paths: { qisl: Fabric/Reporting, isl: Fabric/Reporting }
                    evidence: "test fixture: not yet verified"
                productSpecific:
                  qisl: ["App.VAM/**"]
                  isl: ["App.MTL/**", "Fabric/Platform/Web.ISL.Host/**"]
                targetPinned:
                  qisl: ["Fabric/Platform/Web.Host/appsettings*.json"]
                  isl: ["Fabric/Platform/Web.Host/appsettings*.json"]
                generated:
                  - { pattern: "lib/**", action: block, note: "tracked build output" }
                  - { pattern: "**/*.g.cs", action: block, note: "source-generated" }
                ignored: ["docs/**", "README.md"]
                protectedBranches: {{BackendProtected}}
                validation:
            {{ValidationBlock(BackendTrusted, BackendExtraValidation)}}
              frontend:
                products: { qisl: qisl-frontend, isl: isl-frontend }
                directions:
                  - { from: qisl, to: isl, modes: [pick, reconcile] }
                  - { from: isl, to: qisl, modes: [pick, reconcile] }
                components:
                  - id: shared-ui
                    status: confirmed
                    paths: { qisl: libs/shared-ui, isl: projects/common/shared-ui }
                  - id: workspace
                    status: confirmed
                    paths: { qisl: package.json, isl: package.json }
                  - id: lockfile
                    status: confirmed
                    paths: { qisl: package-lock.json, isl: package-lock.json }
                productSpecific:
                  qisl: ["apps/qisl-app/**", "libs/shared-ui/src/branding/**"]
                  isl: ["apps/isl-app/**", "projects/common/shared-ui/src/branding/**"]
                targetPinned:
                  qisl: ["tsconfig.json"]
                  isl: ["tsconfig.json"]
                validation:
            {{ValidationBlock(true, FrontendExtraValidation)}}
            bundles:
              fullstack:
                pairs: [backend, frontend]
                validation:
                  trusted: true
                  steps:
                    - id: contract-check
                      kind: contract
                      run: ["dotnet", "{{P(Tool)}}", "pass"]
            """;
        File.WriteAllText(ConfigPath, yaml);
    }

    public ForkBridgeEngine Engine() => ForkBridgeEngine.Load(ConfigPath);

    /// <summary>Snapshot of every user checkout (HEAD, symbolic ref, index, local config incl.
    /// remotes, status, stash, refs).</summary>
    public async Task<Dictionary<string, string>> SnapshotUserReposAsync()
    {
        var g = new UserRepoGuard(new ForkBridge.Core.Git.GitClient(new ForkBridge.Core.Infrastructure.ProcessRunner()));
        var d = new Dictionary<string, string>();
        foreach (var r in new[] { QislBackend, IslBackend, QislFrontend, IslFrontend }.Where(Directory.Exists)) d[r] = await g.SnapshotAsync(r);
        return d;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { /* best effort */ }
    }
}
