using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ForkBridge.Core.Git;

namespace ForkBridge.Core.Analysis;

public sealed record ProjectNode
{
    public required string Path { get; init; }          // repo-relative csproj / package.json path
    public required string Kind { get; init; }          // dotnet | npm
    public required string Name { get; init; }
    public string? AssemblyName { get; init; }
    public List<string> TargetFrameworks { get; init; } = new();
    public List<string> ProjectReferences { get; init; } = new();   // repo-relative paths
    public List<string> AssemblyReferences { get; init; } = new();  // <Reference Include="X">
    public List<string> HintPaths { get; init; } = new();
    public Dictionary<string, string?> Packages { get; init; } = new(); // name -> version (null when centrally managed)
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
}

/// <summary>Project/package graph of one repository snapshot, built directly from git objects
/// (no checkout needed, nothing executed).</summary>
public sealed class DependencyGraph
{
    public Dictionary<string, ProjectNode> Projects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CentralPackageVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> TsPathAliases { get; } = new(); // tsconfig path -> alias -> target
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AssemblyProducers { get; } = new(StringComparer.OrdinalIgnoreCase); // assembly -> csproj

    public static async Task<DependencyGraph> BuildAsync(GitClient git, string repo, string treeish, int maxManifestBytes = 2_000_000)
    {
        var g = new DependencyGraph();
        var entries = await git.LsTreeAsync(repo, treeish);
        foreach (var e in entries) if (e.Type == "blob") g.Files.Add(e.Path);

        foreach (var e in entries.Where(e => e.Type == "blob" && e.Size < maxManifestBytes))
        {
            var name = System.IO.Path.GetFileName(e.Path);
            if (e.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await git.ReadBlobAsync(repo, treeish, e.Path);
                if (bytes is not null) g.AddCsproj(e.Path, bytes);
            }
            else if (name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await git.ReadBlobAsync(repo, treeish, e.Path);
                if (bytes is not null) foreach (var (k, v) in ParseCentralPackages(bytes)) g.CentralPackageVersions[k] = v;
            }
            else if (name == "package.json" && !e.Path.Contains("node_modules/") && !e.Path.Contains("bower_components/") && !e.Path.Contains("wwwroot/"))
            {
                var bytes = await git.ReadBlobAsync(repo, treeish, e.Path);
                if (bytes is not null) g.AddPackageJson(e.Path, bytes);
            }
            else if (Regex.IsMatch(name, @"^tsconfig.*\.json$") && !e.Path.Contains("node_modules/"))
            {
                var bytes = await git.ReadBlobAsync(repo, treeish, e.Path);
                if (bytes is not null) g.AddTsconfig(e.Path, bytes);
            }
        }
        foreach (var p in g.Projects.Values.Where(p => p.Kind == "dotnet"))
        {
            g.AssemblyProducers.TryAdd(p.AssemblyName ?? p.Name, p.Path);
        }
        return g;
    }

    public static ProjectNode? ParseCsproj(string path, byte[] bytes)
    {
        XDocument doc;
        try { doc = XDocument.Parse(DecodeText(bytes)); } catch { return null; }
        var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        string? Prop(string n) => doc.Descendants().FirstOrDefault(x => x.Name.LocalName == n)?.Value.Trim();
        var tfms = (Prop("TargetFrameworks") ?? Prop("TargetFramework") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var node = new ProjectNode
        {
            Path = path, Kind = "dotnet",
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            AssemblyName = Prop("AssemblyName"),
            TargetFrameworks = tfms,
        };
        foreach (var el in doc.Descendants())
        {
            var inc = el.Attribute("Include")?.Value;
            if (inc is null) continue;
            switch (el.Name.LocalName)
            {
                case "ProjectReference": node.ProjectReferences.Add(NormalizeRelative(dir, inc)); break;
                case "Reference":
                    node.AssemblyReferences.Add(inc.Split(',')[0].Trim());
                    var hint = el.Descendants().FirstOrDefault(x => x.Name.LocalName == "HintPath")?.Value;
                    if (hint is not null) node.HintPaths.Add(NormalizeRelative(dir, hint));
                    break;
                case "PackageReference":
                    node.Packages[inc] = el.Attribute("Version")?.Value ?? el.Descendants().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value;
                    break;
            }
        }
        return node;
    }

    private void AddCsproj(string path, byte[] bytes)
    {
        var n = ParseCsproj(path, bytes);
        if (n is not null) Projects[path] = n;
    }

    public static Dictionary<string, string> ParseCentralPackages(byte[] bytes)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(DecodeText(bytes));
            foreach (var el in doc.Descendants().Where(x => x.Name.LocalName == "PackageVersion"))
            {
                var inc = el.Attribute("Include")?.Value; var ver = el.Attribute("Version")?.Value;
                if (inc is not null && ver is not null) d[inc] = ver;
            }
        }
        catch { }
        return d;
    }

    public static ProjectNode? ParsePackageJson(string path, byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(DecodeText(bytes), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var node = new ProjectNode
            {
                Path = path, Kind = "npm",
                Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? path : path,
            };
            foreach (var sect in new[] { "dependencies", "devDependencies", "peerDependencies" })
                if (root.TryGetProperty(sect, out var deps) && deps.ValueKind == JsonValueKind.Object)
                    foreach (var p in deps.EnumerateObject()) node.Packages[p.Name] = p.Value.GetString();
            if (root.TryGetProperty("packageManager", out var pm)) node.Packages["(packageManager)"] = pm.GetString();
            if (root.TryGetProperty("engines", out var eng) && eng.ValueKind == JsonValueKind.Object)
                foreach (var p in eng.EnumerateObject()) node.Packages[$"(engine:{p.Name})"] = p.Value.GetString();
            return node;
        }
        catch { return null; }
    }

    private void AddPackageJson(string path, byte[] bytes)
    {
        var n = ParsePackageJson(path, bytes);
        if (n is not null) Projects[path] = n;
    }

    public static Dictionary<string, string> ParseTsPaths(string path, byte[] bytes)
    {
        var aliases = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(DecodeText(bytes), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("compilerOptions", out var co)) return aliases;
            var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            var baseUrl = co.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "." : ".";
            if (co.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
                foreach (var p in paths.EnumerateObject())
                {
                    var first = p.Value.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null);
                    if (first is not null) aliases[p.Name] = NormalizeRelative(NormalizeRelative(dir, baseUrl), first);
                }
        }
        catch { }
        return aliases;
    }

    private void AddTsconfig(string path, byte[] bytes)
    {
        var a = ParseTsPaths(path, bytes);
        if (a.Count > 0) TsPathAliases[path] = a;
    }

    public static string DecodeText(byte[] bytes)
    {
        var s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
    }

    public static string NormalizeRelative(string baseDir, string rel)
    {
        var parts = new List<string>();
        foreach (var seg in (baseDir + "/" + rel.Replace('\\', '/')).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>Project owning a file (closest csproj/package.json in an ancestor directory).</summary>
    public ProjectNode? OwnerOf(string file, string kind = "dotnet")
    {
        var dir = System.IO.Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "";
        while (true)
        {
            var candidate = Projects.Values.Where(p => p.Kind == kind && p.Directory == dir).OrderBy(p => p.Path).FirstOrDefault();
            if (candidate is not null) return candidate;
            if (dir.Length == 0) return null;
            var i = dir.LastIndexOf('/');
            dir = i < 0 ? "" : dir[..i];
        }
    }

    /// <summary>Direct consumers of a project: by ProjectReference path, or by assembly reference
    /// to the project's assembly name (covers the HintPath-to-lib/ pattern).</summary>
    public IEnumerable<ProjectNode> DirectConsumers(ProjectNode p)
    {
        var asm = p.AssemblyName ?? p.Name;
        return Projects.Values.Where(c => c.Kind == "dotnet" && c.Path != p.Path &&
            (c.ProjectReferences.Contains(p.Path) || c.AssemblyReferences.Contains(asm, StringComparer.OrdinalIgnoreCase)));
    }

    public List<ProjectNode> TransitiveConsumers(IEnumerable<ProjectNode> roots)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<ProjectNode>(roots);
        var result = new List<ProjectNode>();
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var c in DirectConsumers(p))
                if (seen.Add(c.Path)) { result.Add(c); queue.Enqueue(c); }
        }
        return result;
    }
}
