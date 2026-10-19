using System.Text;
using ForkBridge.Core.Config;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;

namespace ForkBridge.Core.Validation;

/// <summary>Runs repository-defined validation commands. IMPORTANT: build scripts and package
/// lifecycle hooks execute arbitrary code. A git worktree is NOT a sandbox. Steps only run when the
/// profile is marked trusted AND the caller explicitly trusts validation; otherwise every step is
/// reported UNAVAILABLE (never as a pass). Use runner: docker for container isolation.</summary>
public sealed class ValidationRunner
{
    private readonly IProcessRunner _runner;
    public ValidationRunner(IProcessRunner runner) => _runner = runner;

    public async Task<List<ValidationStepResult>> RunAsync(ValidationProfile profile, string workDir, string role, bool callerTrusts,
        IReadOnlyCollection<string> touchedComponents, IReadOnlyDictionary<string, string>? extraEnv, CancellationToken ct)
    {
        var results = new List<ValidationStepResult>();
        foreach (var step in profile.Steps)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(Result(step, "CANCELLED", reason: "cancelled before start"));
                continue;
            }
            if (step.WhenComponents.Count > 0 && !step.WhenComponents.Intersect(touchedComponents).Any())
            {
                results.Add(Result(step, "SKIPPED", reason: $"not relevant: transfer does not touch {string.Join(", ", step.WhenComponents)}"));
                continue;
            }
            if (!profile.Trusted || !callerTrusts)
            {
                results.Add(Result(step, "UNAVAILABLE", reason: !profile.Trusted
                    ? "profile is not marked trusted in the config"
                    : "validation not trusted for this invocation (pass --trust-validation, or FORKBRIDGE_TRUST_VALIDATION=1 in CI)"));
                continue;
            }
            var missing = step.RequiresEnv.Where(e => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(e))).ToList();
            if (missing.Count > 0)
            {
                results.Add(Result(step, "UNAVAILABLE", reason: $"required environment not available: {string.Join(", ", missing)} (test infrastructure/credentials missing)"));
                continue;
            }
            var cwd = Path.GetFullPath(Path.Combine(workDir, step.Cwd));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDir));
            if (cwd != root && !cwd.StartsWith(root + Path.DirectorySeparatorChar))
            {
                results.Add(Result(step, "UNAVAILABLE", reason: $"cwd '{step.Cwd}' escapes the workspace"));
                continue;
            }
            if (!Directory.Exists(cwd))
            {
                results.Add(Result(step, "FAILED", reason: $"working directory '{step.Cwd}' does not exist in the {role} workspace"));
                continue;
            }

            var env = new Dictionary<string, string?>();
            foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
                if (Redactor.IsSensitiveEnvName((string)kv.Key)) env[(string)kv.Key] = null; // scrub credentials
            env["FORKBRIDGE_ROLE"] = role;
            env["FORKBRIDGE_WORKSPACE"] = workDir;
            env["CI"] = "true";
            env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            env["DOTNET_NOLOGO"] = "1";
            env["NG_CLI_ANALYTICS"] = "false";
            if (extraEnv is not null) foreach (var (k, v) in extraEnv) env[k] = v;
            foreach (var (k, v) in step.Env) env[k] = v;

            string file; List<string> args;
            if (profile.Runner == "docker")
            {
                if (string.IsNullOrWhiteSpace(profile.DockerImage)) { results.Add(Result(step, "UNAVAILABLE", reason: "runner docker requires dockerImage")); continue; }
                var rel = Path.GetRelativePath(root, cwd).Replace('\\', '/');
                file = "docker";
                args = ["run", "--rm", "-v", $"{root}:/workspace", "-w", rel == "." ? "/workspace" : $"/workspace/{rel}", profile.DockerImage!, .. step.Run];
            }
            else { file = step.Run[0]; args = step.Run.Skip(1).ToList(); }

            var r = await _runner.RunAsync(new ProcessSpec
            {
                FileName = file, Arguments = args, WorkingDirectory = cwd, Environment = env,
                Timeout = TimeSpan.FromSeconds(Math.Max(1, step.TimeoutSeconds)), MaxOutputBytes = step.MaxOutputKb * 1024,
            }, ct);
            string outcome = r.Cancelled ? "CANCELLED" : r.TimedOut ? "TIMED_OUT" : r.ExitCode == -1 && r.StdOutBytes.Length == 0 && r.StdErr.StartsWith("Failed to start") ? "UNAVAILABLE" : r.ExitCode == 0 ? "PASSED" : "FAILED";
            var tail = Tail(r.StdOut + "\n" + r.StdErr, 4000);
            results.Add(new ValidationStepResult
            {
                StepId = step.Id, Kind = step.Kind, Required = step.Required, Outcome = outcome, ExitCode = r.ExitCode,
                DurationSeconds = Math.Round(r.Duration.TotalSeconds, 2), OutputTail = Redactor.Redact(tail),
                Reason = outcome == "UNAVAILABLE" ? Redactor.Redact(r.StdErr.Trim()) : r.OutputTruncated ? "output truncated at limit" : null,
            });
        }
        return results;
    }

    public static List<ValidationComparison> Compare(List<ValidationStepResult> baseline, List<ValidationStepResult> candidate)
    {
        var list = new List<ValidationComparison>();
        foreach (var c in candidate)
        {
            var b = baseline.FirstOrDefault(x => x.StepId == c.StepId);
            var bo = b?.Outcome ?? "NOT_RUN";
            string verdict = c.Outcome switch
            {
                "PASSED" when bo == "PASSED" => "OK",
                "PASSED" when bo is "FAILED" or "TIMED_OUT" => "RESOLVED",
                "PASSED" => "OK (baseline not comparable)",
                "FAILED" or "TIMED_OUT" when bo == "PASSED" => "NEW_FAILURE",
                "FAILED" or "TIMED_OUT" when bo is "FAILED" or "TIMED_OUT" => "BASELINE_FAILURE",
                "FAILED" or "TIMED_OUT" => "FAILURE (baseline not comparable)",
                "SKIPPED" => "SKIPPED",
                _ => "UNAVAILABLE",
            };
            list.Add(new ValidationComparison { StepId = c.StepId, Baseline = bo, Candidate = c.Outcome, Verdict = verdict, Required = c.Required });
        }
        return list;
    }

    private static ValidationStepResult Result(ValidationStep s, string outcome, string? reason = null) => new()
    {
        StepId = s.Id, Kind = s.Kind, Required = s.Required, Outcome = outcome, Reason = reason,
    };

    private static string Tail(string s, int max) => s.Length <= max ? s : "..." + s[^max..];
}

/// <summary>Captures enough state of a user checkout to prove it was not modified: HEAD, the
/// symbolic ref, index bytes, local config and working-tree status.</summary>
public sealed class UserRepoGuard
{
    private readonly Git.GitClient _git;
    public UserRepoGuard(Git.GitClient git) => _git = git;

    public async Task<string> SnapshotAsync(string repo)
    {
        var sb = new StringBuilder();
        foreach (var args in new[]
                 {
                     new[] { "rev-parse", "HEAD" }, ["symbolic-ref", "-q", "HEAD"], ["config", "--local", "--list"],
                     ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no"], ["stash", "list"],
                     ["for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/remotes"],
                 })
        {
            var r = await _git.RunAsync(repo, args);
            sb.Append(string.Join(' ', args)).Append('=').Append(r.ExitCode).Append(':').Append(r.StdOut).Append('\n');
        }
        var gitDir = (await _git.RunAsync(repo, ["rev-parse", "--absolute-git-dir"])).StdOut.Trim();
        foreach (var f in new[] { "index", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REBASE_HEAD", "rebase-merge", "rebase-apply" })
        {
            var p = Path.Combine(gitDir, f);
            sb.Append(f).Append('=').Append(File.Exists(p) ? Storage.StateStore.Sha256(File.ReadAllBytes(p)) : Directory.Exists(p) ? "dir" : "-").Append('\n');
        }
        return Storage.StateStore.Sha256(sb.ToString());
    }
}
