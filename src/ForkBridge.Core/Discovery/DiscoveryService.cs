using System.Text;
using System.Text.RegularExpressions;
using ForkBridge.Core.Analysis;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Storage;

namespace ForkBridge.Core.Discovery;

public sealed record RepoDiscovery
{
    public required string Alias { get; init; }
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public bool Placeholder { get; init; }
    public string? Error { get; init; }
    public string? ObjectFormat { get; init; }
    public bool Shallow { get; init; }
    public string? CurrentBranch { get; init; }
    public bool WorkingTreeDirty { get; init; }
    public bool OperationInProgress { get; init; }
    public List<string> LocalBranches { get; init; } = new();
    public int RemoteTrackingBranches { get; init; }
    public Dictionary<string, string> Remotes { get; init; } = new();
    public string? AnalysedRef { get; init; }
    public string? AnalysedSha { get; init; }
    public List<string> RootCommits { get; init; } = new();
    public List<string> Solutions { get; init; } = new();
    public int CsprojCount { get; init; }
    public Dictionary<string, int> TargetFrameworks { get; init; } = new();
    public string? GlobalJsonSdk { get; init; }
    public int CentralPackageVersions { get; init; }
    public List<string> PackageManifests { get; init; } = new();
    public Dictionary<string, string> PackageManagers { get; init; } = new();
    public List<string> AngularWorkspaces { get; init; } = new();
    public Dictionary<string, string> AngularVersions { get; init; } = new();
    public Dictionary<string, Dictionary<string, string>> TsAliases { get; init; } = new();
    public List<string> Lockfiles { get; init; } = new();
    public List<string> PipelineFiles { get; init; } = new();
    public List<string> BuildCommands { get; init; } = new();
    public List<string> DeploymentConfig { get; init; } = new();
    public Dictionary<string, int> GeneratedArtifacts { get; init; } = new();
    public int ForkOverTrailers { get; init; }
    public int ForkBridgeTrailers { get; init; }
    public int BinaryAssemblyReferences { get; init; }
    public List<string> TopLevel { get; init; } = new();
}

public sealed record DirectoryComparison
{
    public required string Path { get; init; }
    public int FilesA { get; init; }
    public int FilesB { get; init; }
    public int IdenticalFiles { get; init; }
    public int CommonPaths { get; init; }
    public double Similarity { get; init; }
    public bool ExistedAtMergeBase { get; init; }
    public int CommitsSinceBaseA { get; init; }
    public int CommitsSinceBaseB { get; init; }
    public required string Verdict { get; init; } // proposed-shared | name-only | only-in-A | only-in-B
    public required string Evidence { get; init; }
    public List<string> ProductConsumersA { get; init; } = new();
    public List<string> ProductConsumersB { get; init; } = new();
}

public sealed record PairDiscovery
{
    public required string Pair { get; init; }
    public required string ProductA { get; init; }
    public required string ProductB { get; init; }
    public bool SharesHistory { get; init; }
    public string? MergeBase { get; init; }
    public string? Note { get; init; }
    public List<DirectoryComparison> Directories { get; init; } = new();
}

public sealed record DiscoveryReport
{
    public required DateTimeOffset At { get; init; }
    public required string GitVersion { get; init; }
    public required string ConfigFile { get; init; }
    public bool RemoteFetched { get; init; }
    public List<RepoDiscovery> Repositories { get; init; } = new();
    public List<PairDiscovery> Pairs { get; init; } = new();
    public string? LegacyScript { get; init; }
    public string? LegacyScriptSha256 { get; init; }
    public List<string> LegacyScriptCapabilities { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

/// <summary>Read-only discovery. User checkouts are inspected with read-only git commands
/// (GIT_OPTIONAL_LOCKS=0) and copied into harness-owned mirrors for tree analysis.</summary>
public sealed class DiscoveryService
{
    private readonly ForkBridgeConfig _cfg;
    private readonly GitClient _git;
    private readonly StateStore _store;
    private readonly MirrorManager _mirrors;

    public DiscoveryService(ForkBridgeConfig cfg, GitClient git, StateStore store, MirrorManager mirrors)
    {
        _cfg = cfg; _git = git; _store = store; _mirrors = mirrors;
    }

    public async Task<DiscoveryReport> DiscoverAsync(bool fetchRemote, int depth, CancellationToken ct = default)
    {
        var report = new DiscoveryReport
        {
            At = DateTimeOffset.UtcNow, GitVersion = (await _git.VersionAsync()).ToString(), ConfigFile = _cfg.SourceFile, RemoteFetched = fetchRemote,
        };
        var analysed = new Dictionary<string, (string mirror, string sha)>();
        foreach (var (alias, repo) in _cfg.Repositories)
        {
            var d = await DiscoverRepoAsync(alias, repo, fetchRemote, ct);
            report.Repositories.Add(d);
            if (d.AnalysedSha is not null) analysed[alias] = (_store.MirrorDir(alias), d.AnalysedSha);
        }
        foreach (var (name, pair) in _cfg.Pairs)
        {
            var prods = pair.Products.ToList();
            if (prods.Count != 2) continue;
            var (pa, aliasA) = (prods[0].Key, prods[0].Value);
            var (pb, aliasB) = (prods[1].Key, prods[1].Value);
            if (!analysed.TryGetValue(aliasA, out var a) || !analysed.TryGetValue(aliasB, out var b))
            {
                report.Pairs.Add(new PairDiscovery { Pair = name, ProductA = pa, ProductB = pb, Note = "One or both repositories unavailable (placeholder or missing) - pair not analysed." });
                continue;
            }
            report.Pairs.Add(await ComparePairAsync(name, pair, pa, aliasA, a, pb, aliasB, b, depth, ct));
        }
        if (_cfg.LegacyScript is not null)
        {
            var p = _cfg.ResolvePath(_cfg.LegacyScript);
            if (File.Exists(p))
            {
                var text = File.ReadAllText(p);
                report = report with { LegacyScript = p, LegacyScriptSha256 = StateStore.Sha256(File.ReadAllBytes(p)), LegacyScriptCapabilities = LegacyCapabilities(text) };
            }
            else report.Notes.Add($"Legacy script not found at {p}.");
        }
        report.Notes.Add("Discovery did not modify any configured repository. Harness mirrors under the state directory were created/updated.");
        if (!fetchRemote) report.Notes.Add("Remotes were not contacted (local refs only). Pass --fetch to include fresh remote refs.");
        return report;
    }

    private async Task<RepoDiscovery> DiscoverRepoAsync(string alias, RepositoryConfig repo, bool fetchRemote, CancellationToken ct)
    {
        var path = _cfg.ResolvePath(repo.Path);
        if (repo.Placeholder) return new RepoDiscovery { Alias = alias, Path = repo.Path, Placeholder = true, Error = "placeholder path - not inspected" };
        if (!Directory.Exists(path)) return new RepoDiscovery { Alias = alias, Path = path, Error = "path does not exist" };
        var probe = await _git.RunAsync(path, ["rev-parse", "--git-dir"], ct: ct);
        if (!probe.Success) return new RepoDiscovery { Alias = alias, Path = path, Exists = true, Error = "not a git repository" };

        string Out(ProcessResultLike r) => r.Ok ? r.Text.Trim() : "";
        async Task<ProcessResultLike> G(params string[] a) { var r = await _git.RunAsync(path, a, ct: ct); return new(r.Success, r.StdOut); }

        var remotes = new Dictionary<string, string>();
        foreach (var name in GitClient.SplitLines(Out(await G("remote"))))
            remotes[name] = Redactor.Redact(Out(await G("config", "--get", $"remote.{name}.url")));
        var gitDir = Out(await G("rev-parse", "--absolute-git-dir"));
        bool inProgress = new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" }
            .Any(f => File.Exists(Path.Combine(gitDir, f)) || Directory.Exists(Path.Combine(gitDir, f)));
        var status = await G("status", "--porcelain", "--untracked-files=no");

        MirrorManager.SyncResult sync;
        try { sync = await _mirrors.SyncAsync(alias, offline: !fetchRemote, ct); }
        catch (Exception ex) { return new RepoDiscovery { Alias = alias, Path = path, Exists = true, Error = "mirror sync failed: " + Redactor.Redact(ex.Message) }; }
        var mirror = sync.MirrorDir;
        string? analysedRef = null, sha = null;
        var defaultBase = _cfg.Pairs.Values.FirstOrDefault(p => p.Products.ContainsValue(alias))?.Defaults.TargetBase ?? "development";
        foreach (var cand in new[] { defaultBase, "main", "master" }.Distinct())
        {
            try { var r = await _mirrors.ResolveAsync(mirror, cand, !fetchRemote, preferLocal: false); analysedRef = cand; sha = r.Sha; break; } catch { }
        }
        sha ??= Out(await G("rev-parse", "HEAD"));
        analysedRef ??= "HEAD";

        var files = (await _git.LsTreeAsync(mirror, sha)).Where(e => e.Type == "blob").ToList();
        var paths = files.Select(f => f.Path).ToList();
        var graph = await DependencyGraph.BuildAsync(_git, mirror, sha);
        var tfms = graph.Projects.Values.Where(p => p.Kind == "dotnet").SelectMany(p => p.TargetFrameworks.DefaultIfEmpty("(none)"))
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        string? sdk = null;
        var gj = paths.FirstOrDefault(p => p == "global.json");
        if (gj is not null) sdk = Regex.Match(DependencyGraph.DecodeText(await _git.ReadBlobAsync(mirror, sha, gj) ?? []), "\"version\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        var pkgMgrs = new Dictionary<string, string>();
        var ngVersions = new Dictionary<string, string>();
        foreach (var p in graph.Projects.Values.Where(p => p.Kind == "npm"))
        {
            if (p.Packages.TryGetValue("(packageManager)", out var pm) && pm is not null) pkgMgrs[p.Path] = pm;
            if (p.Packages.TryGetValue("@angular/core", out var ng) && ng is not null) ngVersions[p.Path] = ng;
            foreach (var e in p.Packages.Where(k => k.Key.StartsWith("(engine:"))) pkgMgrs[p.Path + " " + e.Key] = e.Value ?? "";
        }
        var pipelines = paths.Where(p => Regex.IsMatch(p, @"(^|/)(azure-pipelines[^/]*\.ya?ml|\.github/workflows/[^/]+\.ya?ml|[^/]*\.pipeline\.ya?ml)$", RegexOptions.IgnoreCase)).ToList();
        var commands = new List<string>();
        foreach (var pf in pipelines.Take(20))
        {
            var text = DependencyGraph.DecodeText(await _git.ReadBlobAsync(mirror, sha, pf) ?? []);
            foreach (Match m in Regex.Matches(text, @"^(?!\s*#).*?(?<cmd>(dotnet|npm|npx|ng|yarn|pnpm|nuget|msbuild)\s[^\n'""]+|DotNetCoreCLI@\d|projects:\s*\S+|command:\s*\S+|arguments:\s*[^\n]+)", RegexOptions.Multiline))
                commands.Add($"{pf}: {m.Groups["cmd"].Value.Trim()}");
        }
        foreach (var p in graph.Projects.Values.Where(p => p.Kind == "npm"))
        {
            var bytes = await _git.ReadBlobAsync(mirror, sha, p.Path);
            var m = Regex.Match(DependencyGraph.DecodeText(bytes ?? []), "\"scripts\"\\s*:\\s*\\{(?<s>[^}]*)\\}", RegexOptions.Singleline);
            if (m.Success)
                foreach (Match s in Regex.Matches(m.Groups["s"].Value, "\"(?<k>build|test|lint|typecheck|e2e|start)[^\"]*\"\\s*:\\s*\"(?<v>[^\"]+)\""))
                    commands.Add($"{p.Path} script {s.Groups["k"].Value}: {s.Groups["v"].Value}");
        }
        var deploy = paths.Where(p => Regex.IsMatch(p, @"(?i)(^|/)(appsettings[^/]*\.json|web\.config|dockerfile|docker-compose[^/]*\.ya?ml|environment[^/]*\.ts|[^/]*\.bicep|[^/]*\.tf|helm/|k8s/|[^/]*\.pubxml|launchSettings\.json)$")).ToList();
        var generated = new Dictionary<string, int>
        {
            ["lib/**/*.dll (tracked binaries)"] = paths.Count(p => Regex.IsMatch(p, @"(^|/)lib/.*\.(dll|pdb|xml)$", RegexOptions.IgnoreCase)),
            ["nupkg/**"] = paths.Count(p => p.StartsWith("nupkg/")),
            ["*.Designer.cs / *.g.cs"] = paths.Count(p => Regex.IsMatch(p, @"\.(Designer|g)\.cs$")),
            ["T4 *.tt"] = paths.Count(p => p.EndsWith(".tt")),
            ["service-proxies / nswag"] = paths.Count(p => Regex.IsMatch(p, @"(?i)(service-proxies|nswag)")),
            ["wwwroot/dist bundles"] = paths.Count(p => Regex.IsMatch(p, @"(?i)wwwroot/(dist|lib)/")),
        };
        var trailers = await G("log", "--format=%B", sha);
        int legacy = Regex.Matches(trailers.Text, "^Fork-Over-Source:", RegexOptions.Multiline).Count;
        int fb = Regex.Matches(trailers.Text, "^ForkBridge-(Source|Reconcile-Source|Merge-Source|Scope):", RegexOptions.Multiline).Count;

        return new RepoDiscovery
        {
            Alias = alias, Path = path, Exists = true, ObjectFormat = sync.ObjectFormat,
            Shallow = Out(await G("rev-parse", "--is-shallow-repository")) == "true",
            CurrentBranch = Out(await G("symbolic-ref", "--short", "-q", "HEAD")) is { Length: > 0 } cb ? cb : "(detached)",
            WorkingTreeDirty = status.Ok && status.Text.Trim().Length > 0, OperationInProgress = inProgress,
            LocalBranches = GitClient.SplitLines(Out(await G("for-each-ref", "--format=%(refname:short)", "refs/heads"))).Take(100).ToList(),
            RemoteTrackingBranches = GitClient.SplitLines(Out(await G("for-each-ref", "--format=x", "refs/remotes"))).Count,
            Remotes = remotes, AnalysedRef = analysedRef, AnalysedSha = sha,
            RootCommits = GitClient.SplitLines(Out(await G("rev-list", "--max-parents=0", sha))).ToList(),
            Solutions = paths.Where(p => p.EndsWith(".sln") || p.EndsWith(".slnx")).ToList(),
            CsprojCount = graph.Projects.Values.Count(p => p.Kind == "dotnet"), TargetFrameworks = tfms, GlobalJsonSdk = sdk,
            CentralPackageVersions = graph.CentralPackageVersions.Count,
            PackageManifests = graph.Projects.Values.Where(p => p.Kind == "npm").Select(p => p.Path).ToList(),
            PackageManagers = pkgMgrs, AngularVersions = ngVersions,
            AngularWorkspaces = paths.Where(p => p.EndsWith("angular.json") && !p.Contains("node_modules/")).ToList(),
            TsAliases = graph.TsPathAliases, Lockfiles = paths.Where(p => Regex.IsMatch(p, @"(^|/)(package-lock\.json|yarn\.lock|pnpm-lock\.yaml|packages\.lock\.json)$") && !p.Contains("node_modules/")).ToList(),
            PipelineFiles = pipelines, BuildCommands = commands.Distinct().Take(80).ToList(), DeploymentConfig = deploy.Take(100).ToList(),
            GeneratedArtifacts = generated.Where(kv => kv.Value > 0).ToDictionary(), ForkOverTrailers = legacy, ForkBridgeTrailers = fb,
            BinaryAssemblyReferences = graph.Projects.Values.Sum(p => p.HintPaths.Count),
            TopLevel = (await _git.LsTreeAsync(mirror, sha, recursive: false)).Select(e => e.Path + (e.Type == "tree" ? "/" : "")).ToList(),
        };
    }

    private sealed record ProcessResultLike(bool Ok, string Text);

    private async Task<PairDiscovery> ComparePairAsync(string name, PairConfig pair, string pa, string aliasA, (string mirror, string sha) a,
        string pb, string aliasB, (string mirror, string sha) b, int depth, CancellationToken ct)
    {
        // Bring B's objects into A's mirror (harness-owned) so history can be compared.
        await _mirrors.FetchPeerAsync(a.mirror, aliasB, b.mirror, ct);
        var mb = await _git.MergeBaseAsync(a.mirror, a.sha, b.sha);
        var filesA = (await _git.LsTreeAsync(a.mirror, a.sha)).Where(e => e.Type == "blob").ToDictionary(e => e.Path, e => e.Sha);
        var filesB = (await _git.LsTreeAsync(a.mirror, b.sha)).Where(e => e.Type == "blob").ToDictionary(e => e.Path, e => e.Sha);
        var filesBase = mb is null ? new HashSet<string>() : (await _git.LsTreeAsync(a.mirror, mb)).Select(e => e.Path).ToHashSet();
        var graphA = await DependencyGraph.BuildAsync(_git, a.mirror, a.sha);
        var graphB = await DependencyGraph.BuildAsync(_git, a.mirror, b.sha);

        IEnumerable<string> Dirs(IEnumerable<string> paths) => paths.SelectMany(p =>
        {
            var parts = p.Split('/');
            return Enumerable.Range(1, Math.Min(depth, parts.Length - 1)).Select(n => string.Join('/', parts.Take(n)));
        }).Distinct();

        var dirs = Dirs(filesA.Keys).Union(Dirs(filesB.Keys)).OrderBy(x => x).ToList();
        var result = new List<DirectoryComparison>();
        foreach (var d in dirs)
        {
            string prefix = d + "/";
            var inA = filesA.Where(kv => kv.Key.StartsWith(prefix)).ToDictionary();
            var inB = filesB.Where(kv => kv.Key.StartsWith(prefix)).ToDictionary();
            if (inA.Count == 0 && inB.Count == 0) continue;
            var common = inA.Keys.Intersect(inB.Keys).ToList();
            int identical = common.Count(k => inA[k] == inB[k]);
            int union = inA.Keys.Union(inB.Keys).Count();
            double sim = union == 0 ? 0 : Math.Round((double)identical / union, 3);
            bool atBase = filesBase.Any(f => f.StartsWith(prefix));
            int ca = 0, cb = 0;
            if (mb is not null && inA.Count > 0) ca = int.Parse((await _git.OutAsync(a.mirror, "rev-list", "--count", $"{mb}..{a.sha}", "--", d)).Trim());
            if (mb is not null && inB.Count > 0) cb = int.Parse((await _git.OutAsync(a.mirror, "rev-list", "--count", $"{mb}..{b.sha}", "--", d)).Trim());
            string verdict; string evidence;
            if (inA.Count == 0) { verdict = $"only-in-{pb}"; evidence = $"{inB.Count} file(s) only in {pb}{(atBase ? "; existed at merge-base (removed in " + pa + ")" : "")}"; }
            else if (inB.Count == 0) { verdict = $"only-in-{pa}"; evidence = $"{inA.Count} file(s) only in {pa}{(atBase ? "; existed at merge-base (removed in " + pb + ")" : "")}"; }
            else if (atBase && sim >= 0.3)
            {
                verdict = "proposed-shared";
                evidence = $"existed at merge-base {mb![..10]}; {identical}/{union} files byte-identical ({sim:P0}); {common.Count} common paths; commits since base: {pa}={ca}, {pb}={cb}";
            }
            else
            {
                verdict = "name-only";
                evidence = $"same directory name but weak content evidence: {identical}/{union} identical ({sim:P0}){(atBase ? "" : "; not present at merge-base")}. NOT proposed.";
            }
            result.Add(new DirectoryComparison
            {
                Path = d, FilesA = inA.Count, FilesB = inB.Count, IdenticalFiles = identical, CommonPaths = common.Count, Similarity = sim,
                ExistedAtMergeBase = atBase, CommitsSinceBaseA = ca, CommitsSinceBaseB = cb, Verdict = verdict, Evidence = evidence,
                ProductConsumersA = verdict == "proposed-shared" ? Consumers(graphA, d) : [],
                ProductConsumersB = verdict == "proposed-shared" ? Consumers(graphB, d) : [],
            });
        }
        return new PairDiscovery
        {
            Pair = name, ProductA = pa, ProductB = pb, SharesHistory = mb is not null, MergeBase = mb, Directories = result,
            Note = mb is null ? "No common history - component mappings cannot be inferred from ancestry." : null,
        };
    }

    private static List<string> Consumers(DependencyGraph g, string dir)
    {
        var inside = g.Projects.Values.Where(p => p.Kind == "dotnet" && p.Path.StartsWith(dir + "/")).ToList();
        return g.TransitiveConsumers(inside).Where(c => !c.Path.StartsWith(dir + "/")).Select(c => c.Path).Take(40).ToList();
    }

    private static List<string> LegacyCapabilities(string script)
    {
        var caps = new List<string>();
        void Has(string pattern, string what) { if (Regex.IsMatch(script, pattern, RegexOptions.IgnoreCase)) caps.Add(what); }
        Has(@"remote\.\$?\w*\.fetch|Set-CrossRemote", "cross-repository fetch via a remote added to the TARGET checkout (mutates user remote config)");
        Has(@"refs/remotes/\$Alias/_local", "local-branch resolution through _local namespace");
        Has(@"merge',\s*'--no-ff", "merge mode in the user's checkout");
        Has(@"format-patch", "format-patch/am pick mode");
        Has(@"Fork-Over-Source", "Fork-Over-Source provenance trailer");
        Has(@"worktree', 'add'", "trial apply in a temporary worktree");
        Has(@"merge-tree", "conflict prediction via merge-tree");
        Has(@"PinnedPaths", "target-pinned paths");
        Has(@"autoExcluded|Source-only top-level", "auto-exclusion of source-only top-level entries");
        Has(@"'switch', '--no-track', '-c'", "creates the fork branch in the user's checkout (changes current branch)");
        Has(@"'push', '-u'", "optional push");
        return caps;
    }

    public static string Markdown(DiscoveryReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ForkBridge discovery report\n");
        sb.AppendLine($"- Generated: {r.At:u} · git {r.GitVersion} · config `{r.ConfigFile}` · remote fetched: {r.RemoteFetched}");
        foreach (var n in r.Notes) sb.AppendLine($"- {n}");
        sb.AppendLine();
        foreach (var d in r.Repositories)
        {
            sb.AppendLine($"## Repository `{d.Alias}`\n");
            if (d.Error is not null) { sb.AppendLine($"**Not analysed:** {d.Error} (`{d.Path}`)\n"); continue; }
            sb.AppendLine($"- Path: `{d.Path}` · object format {d.ObjectFormat} · shallow {d.Shallow}");
            sb.AppendLine($"- Current branch: {d.CurrentBranch} · dirty tracked files: {d.WorkingTreeDirty} · operation in progress: {d.OperationInProgress}");
            sb.AppendLine($"- Local branches ({d.LocalBranches.Count}): {string.Join(", ", d.LocalBranches.Take(15))}{(d.LocalBranches.Count > 15 ? " …" : "")} · remote-tracking: {d.RemoteTrackingBranches}");
            foreach (var (k, v) in d.Remotes) sb.AppendLine($"- Remote `{k}`: {v}");
            sb.AppendLine($"- Analysed `{d.AnalysedRef}` @ `{d.AnalysedSha}` · root commit(s): {string.Join(", ", d.RootCommits.Select(x => x[..10]))}");
            sb.AppendLine($"- .NET: {d.CsprojCount} projects; TFMs {string.Join(", ", d.TargetFrameworks.OrderByDescending(x => x.Value).Select(kv => $"{kv.Key}×{kv.Value}"))}; global.json SDK: {d.GlobalJsonSdk ?? "none (SDK not pinned)"}; central package versions: {d.CentralPackageVersions}; HintPath binary references: {d.BinaryAssemblyReferences}");
            sb.AppendLine($"- Solutions ({d.Solutions.Count}): {string.Join(", ", d.Solutions.Take(12).Select(s => $"`{s}`"))}{(d.Solutions.Count > 12 ? " …" : "")}");
            sb.AppendLine($"- npm manifests: {(d.PackageManifests.Count == 0 ? "none" : string.Join(", ", d.PackageManifests.Select(s => $"`{s}`")))}");
            if (d.AngularWorkspaces.Count > 0) sb.AppendLine($"- Angular workspaces: {string.Join(", ", d.AngularWorkspaces)}; @angular/core: {string.Join(", ", d.AngularVersions.Select(kv => $"{kv.Key}={kv.Value}"))}");
            foreach (var (k, v) in d.PackageManagers) sb.AppendLine($"- Package manager/engine: `{k}` = {v}");
            foreach (var (k, v) in d.TsAliases) sb.AppendLine($"- TS aliases in `{k}`: {string.Join(", ", v.Select(kv => $"{kv.Key}→{kv.Value}"))}");
            sb.AppendLine($"- Lockfiles: {(d.Lockfiles.Count == 0 ? "none" : string.Join(", ", d.Lockfiles))}");
            sb.AppendLine($"- Pipelines: {(d.PipelineFiles.Count == 0 ? "none" : string.Join(", ", d.PipelineFiles))}");
            foreach (var c in d.BuildCommands.Take(25)) sb.AppendLine($"  - `{c}`");
            sb.AppendLine($"- Generated artifacts: {(d.GeneratedArtifacts.Count == 0 ? "none" : string.Join("; ", d.GeneratedArtifacts.Select(kv => $"{kv.Key}: {kv.Value}")))}");
            sb.AppendLine($"- Deployment/config files: {d.DeploymentConfig.Count} (e.g. {string.Join(", ", d.DeploymentConfig.Take(6))})");
            sb.AppendLine($"- Provenance trailers: Fork-Over-Source {d.ForkOverTrailers}, ForkBridge {d.ForkBridgeTrailers}");
            sb.AppendLine($"- Top level: {string.Join(" ", d.TopLevel)}\n");
        }
        foreach (var p in r.Pairs)
        {
            sb.AppendLine($"## Pair `{p.Pair}` ({p.ProductA} ↔ {p.ProductB})\n");
            if (p.Note is not null) sb.AppendLine($"> {p.Note}\n");
            sb.AppendLine($"- Shares history: **{p.SharesHistory}**{(p.MergeBase is null ? "" : $" (merge-base `{p.MergeBase}`)")}\n");
            sb.AppendLine("Mappings below are **PROPOSED** from evidence (existed at the merge-base AND ≥30% byte-identical files). Name matches alone are never proposed.\n");
            sb.AppendLine($"| Directory | Verdict | Files {p.ProductA}/{p.ProductB} | Identical | Evidence |\n|---|---|---|---|---|");
            foreach (var d in p.Directories) sb.AppendLine($"| `{d.Path}` | {d.Verdict} | {d.FilesA}/{d.FilesB} | {d.IdenticalFiles} | {d.Evidence} |");
            sb.AppendLine();
        }
        if (r.LegacyScript is not null)
        {
            sb.AppendLine($"## Legacy script `{r.LegacyScript}`\n\n- sha256 `{r.LegacyScriptSha256}`");
            foreach (var c in r.LegacyScriptCapabilities) sb.AppendLine($"- {c}");
        }
        return sb.ToString();
    }

    /// <summary>Draft policy from discovery. Everything is marked proposed/placeholder; nothing is confirmed.</summary>
    public static string DraftConfig(DiscoveryReport r, ForkBridgeConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DRAFT generated by `forkbridge discover --write-config`. Review every entry.");
        sb.AppendLine("# Components are PROPOSED from evidence; change status to 'confirmed' only after verification.");
        sb.AppendLine("version: 1");
        sb.AppendLine($"stateDir: {cfg.StateDir}");
        sb.AppendLine("repositories:");
        foreach (var (a, repo) in cfg.Repositories)
            sb.AppendLine($"  {a}: {{ path: \"{repo.Path.Replace("\\", "/")}\", remote: {repo.Remote}, kind: {repo.Kind}, placeholder: {repo.Placeholder.ToString().ToLowerInvariant()} }}");
        sb.AppendLine("pairs:");
        foreach (var p in r.Pairs)
        {
            var pc = cfg.Pairs[p.Pair];
            sb.AppendLine($"  {p.Pair}:");
            sb.AppendLine($"    products: {{ {string.Join(", ", pc.Products.Select(kv => $"{kv.Key}: {kv.Value}"))} }}");
            sb.AppendLine($"    defaults: {{ targetBase: {pc.Defaults.TargetBase}, sourceBase: {pc.Defaults.SourceBase}, validationProfile: {pc.Defaults.ValidationProfile} }}");
            sb.AppendLine($"    directions:\n      - {{ from: {p.ProductA}, to: {p.ProductB}, modes: [pick, reconcile] }}\n      - {{ from: {p.ProductB}, to: {p.ProductA}, modes: [pick, reconcile] }}");
            sb.AppendLine("    components:");
            var proposed = p.Directories.Where(d => d.Verdict == "proposed-shared").ToList();
            // keep the shallowest proposed directories (children are covered)
            var roots = proposed.Where(d => !proposed.Any(o => o != d && d.Path.StartsWith(o.Path + "/"))).ToList();
            foreach (var d in roots)
            {
                var id = Regex.Replace(d.Path.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                sb.AppendLine($"      - id: {id}\n        status: proposed\n        paths: {{ {p.ProductA}: \"{d.Path}\", {p.ProductB}: \"{d.Path}\" }}\n        evidence: \"{d.Evidence.Replace("\"", "'")}\"");
            }
            if (roots.Count == 0) sb.AppendLine("      []  # no evidence-backed proposals; add components manually");
            sb.AppendLine("    productSpecific:");
            foreach (var prod in new[] { p.ProductA, p.ProductB })
            {
                var only = p.Directories.Where(d => d.Verdict == $"only-in-{prod}" && !d.Path.Contains('/')).Select(d => d.Path).ToList();
                sb.AppendLine($"      {prod}: [{string.Join(", ", only.Select(o => $"\"{o}/**\""))}]  # PROPOSED: top-level entries present only in {prod}");
            }
            sb.AppendLine("    targetPinned: {}   # PLACEHOLDER: add product configuration (appsettings*, environment*.ts, pipelines)");
            sb.AppendLine("    generated:\n      - { pattern: \"lib/**\", action: block, note: \"PLACEHOLDER: tracked build outputs - regenerate, never transfer\" }");
            sb.AppendLine("    ignored: []\n    validation: {}     # PLACEHOLDER: add trusted profiles with repository build/test commands");
        }
        return sb.ToString();
    }
}
