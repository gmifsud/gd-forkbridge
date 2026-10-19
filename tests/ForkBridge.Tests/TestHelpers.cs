using ForkBridge.Core;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

internal static class T
{
    public static Task<Plan> Feature(ForkBridgeEngine e, string pair, string from, string to, string branch, params string[] components)
        => e.Planner.PlanAsync(new PlanRequest { Pair = pair, From = from, To = to, Mode = SelectionMode.Feature, SourceRef = branch, Components = components.ToList() });

    public static Task<Plan> Commits(ForkBridgeEngine e, string pair, string from, string to, params string[] shas)
        => e.Planner.PlanAsync(new PlanRequest { Pair = pair, From = from, To = to, Mode = SelectionMode.Commits, Commits = shas.ToList() });

    public static Task<Run> Simulate(ForkBridgeEngine e, Plan p, bool trust = true, bool preserve = false)
        => e.Simulator.SimulateAsync(p.Id, new SimulateOptions { TrustValidation = trust, PreserveWorkspace = preserve });

    /// <summary>Creates a branch in a user checkout, applies edits, commits and pushes.</summary>
    public static string Branch(string repo, string branch, string message, params (string Path, string? Content)[] edits)
    {
        var current = RepoFixture.Git(repo, "rev-parse", "--abbrev-ref", "HEAD");
        if (current != branch)
        {
            var exists = RepoFixture.Git(repo, "branch", "--list", branch).Length > 0;
            if (exists) RepoFixture.Git(repo, "switch", "-q", branch); else RepoFixture.Git(repo, "switch", "-q", "-c", branch);
        }
        foreach (var (p, c) in edits)
        {
            if (c is null) RepoFixture.Git(repo, "rm", "-q", "-r", p);
            else RepoFixture.Write(repo, p, c);
        }
        var sha = RepoFixture.Commit(repo, message);
        RepoFixture.Push(repo, branch);
        return sha;
    }

    public static string OnDevelopment(string repo, string message, params (string Path, string? Content)[] edits)
    {
        RepoFixture.Git(repo, "switch", "-q", "development");
        return Branch(repo, "development", message, edits);
    }

    public static string MirrorFile(ForkBridgeEngine e, string alias, string sha, string path)
        => RepoFixture.Git(e.Store.MirrorDir(alias), "show", $"{sha}:{path}");

    public static bool Has(this IEnumerable<Finding> f, string code) => f.Any(x => x.Code == code);
}
