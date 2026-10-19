using System.Text;
using System.Text.RegularExpressions;
using ForkBridge.Core.Analysis;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Policy;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Validation;

namespace ForkBridge.Core.Transfer;

public sealed record SimulateOptions
{
    public bool TrustValidation { get; init; }
    public string? Profile { get; init; }
    public bool PreserveWorkspace { get; init; }
    public bool SkipValidation { get; init; }
    public string? BundleId { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraValidationEnv { get; init; }
}

public sealed class StaleException(string message) : Exception(message);

/// <summary>Applies an immutable plan in a harness-owned worktree of the harness-owned target mirror.
/// Never touches the user's checkout. Every step is persisted so a crash can be resumed.</summary>
public sealed class Simulator
{
    private readonly ForkBridgeConfig _cfg;
    private readonly GitClient _git;
    private readonly StateStore _store;
    private readonly MirrorManager _mirrors;
    private readonly ValidationRunner _validation;
    private readonly UserRepoGuard _guard;

    public Simulator(ForkBridgeConfig cfg, GitClient git, StateStore store, MirrorManager mirrors, ValidationRunner validation)
    {
        _cfg = cfg; _git = git; _store = store; _mirrors = mirrors; _validation = validation; _guard = new UserRepoGuard(git);
    }

    /// <summary>Verifies the plan is still valid: integrity, policy hash, target base unmoved.</summary>
    public async Task VerifyPlanStillValidAsync(Plan plan, CancellationToken ct)
    {
        var policy = ConfigLoader.PolicyHash(_cfg, plan.Pair);
        if (policy != plan.PolicyHash)
            throw new StaleException($"Policy changed since plan {plan.Id} was created ({plan.PolicyHash[..15]} -> {policy[..15]}). Re-plan.");
        var tgtAlias = _cfg.Pairs[plan.Pair].Products[plan.ToProduct];
        var sync = await _mirrors.SyncAsync(tgtAlias, plan.Freshness.Offline, ct);
        var now = await _mirrors.ResolveAsync(sync.MirrorDir, plan.TargetBaseRef, plan.Freshness.Offline, preferLocal: false);
        if (now.Sha != plan.TargetBaseSha)
            throw new StaleException($"Target base '{plan.TargetBaseRef}' moved from {plan.TargetBaseSha[..10]} to {now.Sha[..10]} since plan {plan.Id}. Re-plan.");
    }

    public async Task<Run> SimulateAsync(string planId, SimulateOptions opt, CancellationToken ct = default)
    {
        var plan = _store.LoadPlan(planId);
        if (plan.PreliminaryStatus == RunStatus.BLOCKED)
            throw new PlanningException($"Plan {plan.Id} is BLOCKED; resolve its blocking findings and re-plan.", ExitCodes.Blocked);
        if (plan.PreliminaryStatus == RunStatus.NO_CHANGES && plan.Mode != SelectionMode.Merge)
            throw new PlanningException($"Plan {plan.Id} has NO_CHANGES to simulate.", ExitCodes.NoChanges);
        await VerifyPlanStillValidAsync(plan, ct);
        var pair = _cfg.Pairs[plan.Pair];
        if (plan.Mode == SelectionMode.Merge && pair.Components.Any(c => plan.SelectedComponents.Contains(c.Id) &&
                Glob.Normalize(c.Paths.GetValueOrDefault(plan.FromProduct) ?? "") != Glob.Normalize(c.Paths.GetValueOrDefault(plan.ToProduct) ?? "")))
            throw new PlanningException("Merge mode requires identical component paths in both products; use pick or reconcile.", ExitCodes.PolicyViolation);

        var mirror = _store.MirrorDir(pair.Products[plan.ToProduct]);
        foreach (var c in plan.Commits.Where(c => c.State == CommitState.Pending))
            if (!await _git.ObjectExistsAsync(mirror, c.Sha)) throw new StaleException($"Source commit {c.Sha} is no longer available in the harness mirror. Re-plan.");

        var runId = StateStore.NewId("run");
        var runDir = _store.RunDir(runId);
        _store.Fs.CreateOwnedDirectory(runDir, runId);
        var branch = $"{pair.Defaults.BranchPrefix}/{plan.Pair}/{plan.FromProduct}-to-{plan.ToProduct}/{runId}";
        var run = new Run
        {
            Id = runId, PlanId = plan.Id, BundleId = opt.BundleId, StartedAt = DateTimeOffset.UtcNow, WorkspaceDir = runDir,
            CandidateWorktree = Path.Combine(runDir, "candidate"), Branch = branch, PreserveWorkspace = opt.PreserveWorkspace,
            ValidationTrusted = opt.TrustValidation,
        };
        _store.SaveRun(run);
        try { await _git.CheckedAsync(mirror, ["worktree", "add", "--quiet", "-b", branch, run.CandidateWorktree, plan.TargetBaseSha], ct: ct); }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.CANCELLED; run.PreserveWorkspace = true;
            run.Log.Add("cancelled while creating the workspace; resume recreates it");
            _store.SaveRun(run);
            throw;
        }
        run.Log.Add($"workspace {run.CandidateWorktree} on {branch} at {plan.TargetBaseSha[..10]}");
        _store.SaveRun(run);
        return await ContinueAsync(plan, run, opt, ct);
    }

    public async Task<Run> ResumeAsync(string runId, SimulateOptions opt, CancellationToken ct = default)
    {
        var run = _store.LoadRun(runId);
        var plan = _store.LoadPlan(run.PlanId);
        if (run.Status is RunStatus.ABORTED) throw new PlanningException($"Run {runId} was aborted.", ExitCodes.UsageError);
        if (_store.Fs.ReadOwner(run.WorkspaceDir) != run.Id) throw new Infrastructure.HarnessSafetyException($"Run directory of {runId} is not owned by it.");
        var mirror = _store.MirrorDir(_cfg.Pairs[plan.Pair].Products[plan.ToProduct]);
        if (run.ResultCommits.Count == 0 && run.Conflict is null)
        {
            // Nothing applied yet: the workspace may be missing or half-created by a cancelled
            // `worktree add`. Recreate it on the run's own harness branch at the pinned base.
            // HEAD on the run branch is not enough: a `worktree add` killed after writing HEAD but before
            // finishing the checkout leaves the registration `locked: initializing` (incomplete checkout).
            var ok = Directory.Exists(run.CandidateWorktree) &&
                     (await _git.RunAsync(run.CandidateWorktree, ["rev-parse", "--abbrev-ref", "HEAD"], ct: ct)).StdOut.Trim() == run.Branch;
            if (ok)
            {
                var gd = (await _git.RunAsync(run.CandidateWorktree, ["rev-parse", "--absolute-git-dir"], ct: ct)).StdOut.Trim();
                var lockedFile = Path.Combine(gd, "locked");
                ok = gd.Length > 0 && !(File.Exists(lockedFile) && File.ReadAllText(lockedFile).StartsWith("initializing", StringComparison.Ordinal));
            }
            if (!ok)
            {
                await ForgetWorktreeAsync(mirror, run, run.CandidateWorktree, ct);
                await _git.CheckedAsync(mirror, ["branch", "-f", run.Branch, plan.TargetBaseSha], ct: ct);
                await _git.CheckedAsync(mirror, ["worktree", "add", "--quiet", run.CandidateWorktree, run.Branch], ct: ct);
                run.Log.Add("recovered: candidate workspace recreated at the pinned target base");
            }
        }
        else if (!Directory.Exists(run.CandidateWorktree))
            throw new PlanningException($"Workspace for {runId} no longer exists (cleaned up). Re-simulate the plan.", ExitCodes.Stale);
        await VerifyPlanStillValidAsync(plan, ct);
        run.Status = RunStatus.IN_PROGRESS;
        run = run with { ValidationTrusted = opt.TrustValidation || run.ValidationTrusted };

        if (run.Conflict is not null)
        {
            var wt = run.CandidateWorktree;
            var unmerged = await _git.LinesAsync(wt, "diff", "--name-only", "--diff-filter=U");
            var stillMarked = new List<string>();
            foreach (var f in run.Conflict.Files)
            {
                var p = Path.Combine(wt, f);
                if (File.Exists(p) && Regex.IsMatch(File.ReadAllText(p), @"^(<{7}|>{7})( |$)", RegexOptions.Multiline)) stillMarked.Add(f);
            }
            if (stillMarked.Count > 0)
                throw new PlanningException($"Conflict markers remain in: {string.Join(", ", stillMarked)}. Resolve them in {wt} first.", ExitCodes.Blocked);
            await _git.CheckedAsync(wt, ["add", "-A", "--", .. run.Conflict.Files], ct: ct);
            if (unmerged.Except(run.Conflict.Files).Any())
                throw new PlanningException($"Unexpected unmerged files: {string.Join(", ", unmerged.Except(run.Conflict.Files))}", ExitCodes.Blocked);
            var idx = run.NextCommitIndex;
            if (plan.Reconciliation is not null && run.Conflict.Operation.StartsWith("reconcile"))
                await CommitReconciliationAsync(plan, run, resolvedManually: true, ct);
            else if (plan.Mode == SelectionMode.Merge)
                await CommitMergeAsync(plan, run, resolvedManually: true, ct);
            else
                await CommitPickedAsync(plan, run, plan.Commits[idx], resolvedManually: true, ct);
            run.Findings.RemoveAll(f => f.Code == "CONFLICT"); // superseded by the manual-resolution review item
            run.Findings.Add(new Finding
            {
                Id = Finding.MakeId("MANUAL_RESOLUTION", run.Id, run.Conflict.SourceCommit), Code = "MANUAL_RESOLUTION", Category = "conflict",
                Severity = Severity.Warning, RequiresReview = true, Commits = run.Conflict.SourceCommit is null ? [] : [run.Conflict.SourceCommit],
                Files = run.Conflict.Files, Title = "A conflict was resolved manually",
                Evidence = [$"Resolution committed during resume of {run.Id}; evidence kept in {run.Conflict.EvidenceDir}."],
                SuggestedAction = "Review the candidate diff for the resolved files before approval.",
            });
            run.Conflict = null;
            if (plan.Reconciliation is null && plan.Mode != SelectionMode.Merge) run.NextCommitIndex = idx + 1;
            _store.SaveRun(run);
        }
        else if (run.ResultCommits.Count > 0 || run.Status == RunStatus.IN_PROGRESS)
        {
            // Crash recovery: put the harness workspace back on the last recorded commit. A git
            // process killed mid-operation can leave lock files behind: index.lock / HEAD.lock /
            // ORIG_HEAD.lock in the worktree's admin dir and the run branch's ref lock in the mirror.
            // All are harness-owned and only this run touches them, so removing them is safe.
            foreach (var name in new[] { "index.lock", "HEAD.lock", "ORIG_HEAD.lock", $"refs/heads/{run.Branch}.lock" })
            {
                var lockPath = (await _git.OutAsync(run.CandidateWorktree, "rev-parse", "--git-path", name)).Trim();
                if (!Path.IsPathRooted(lockPath)) lockPath = Path.Combine(run.CandidateWorktree, lockPath);
                lockPath = Path.GetFullPath(lockPath);
                if (File.Exists(lockPath) && _store.Fs.IsInsideRoot(lockPath)) { File.Delete(lockPath); run.Log.Add($"crash recovery: removed stale {name}"); }
            }
            var head = (await _git.OutAsync(run.CandidateWorktree, "rev-parse", "HEAD")).Trim();
            var expected = run.ResultCommits.Count > 0 ? run.ResultCommits.Values.Last() : plan.TargetBaseSha;
            if (head != expected || (await _git.OutAsync(run.CandidateWorktree, "status", "--porcelain")).Length > 0)
            {
                await _git.CheckedAsync(run.CandidateWorktree, ["reset", "--hard", "--quiet", expected], ct: ct);
                run.Log.Add($"crash recovery: workspace reset to last recorded commit {expected[..10]}");
            }
        }
        return await ContinueAsync(plan, run, opt, ct);
    }

    private async Task<Run> ContinueAsync(Plan plan, Run run, SimulateOptions opt, CancellationToken ct)
    {
        try
        {
            if (plan.Mode == SelectionMode.Merge)
            {
                if (run.CandidateSha is null && !await ApplyMergeAsync(plan, run, ct)) return Finish(plan, run, conflict: true);
            }
            else if (plan.Reconciliation is not null)
            {
                if (plan.Reconciliation.State == CommitState.Pending && run.ResultCommits.Count == 0 && !await ApplyReconciliationAsync(plan, run, ct))
                    return Finish(plan, run, conflict: true);
            }
            else
            {
                while (run.NextCommitIndex < plan.Commits.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    var pc = plan.Commits[run.NextCommitIndex];
                    if (pc.State == CommitState.Pending)
                    {
                        if (!await ApplyPickedAsync(plan, run, pc, ct)) return Finish(plan, run, conflict: true);
                    }
                    else run.Log.Add($"skip {pc.Sha[..10]}: {pc.State}");
                    run.NextCommitIndex++;
                    _store.SaveRun(run);
                }
            }
            run.CandidateSha = (await _git.OutAsync(run.CandidateWorktree, "rev-parse", "HEAD")).Trim();
            await PostApplyAnalysisAsync(plan, run, ct);
            if (!opt.SkipValidation) await ValidateAsync(plan, run, opt, ct);
            ct.ThrowIfCancellationRequested(); // a cancelled validation is CANCELLED, never a verdict
            return Finish(plan, run, conflict: false);
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.CANCELLED;
            run.Log.Add("cancelled; resume with `forkbridge resume --run " + run.Id + "`");
            run.PreserveWorkspace = true;
            _store.SaveRun(run);
            throw;
        }
        catch (Exception ex) when (ex is not PlanningException and not StaleException)
        {
            run.Status = RunStatus.BLOCKED;
            run.Error = Infrastructure.Redactor.Redact(ex.Message);
            run.PreserveWorkspace = true;
            _store.SaveRun(run);
            throw;
        }
    }

    // ---- pick -----------------------------------------------------------------------------------
    private async Task<bool> ApplyPickedAsync(Plan plan, Run run, PlannedCommit pc, CancellationToken ct)
    {
        var patch = _store.ReadPlanArtifact(plan, pc.PatchFile!, pc.PatchSha256!);
        var r = await _git.RunAsync(run.CandidateWorktree, ["apply", "--3way", "--index", "--binary", "--whitespace=nowarn"], stdin: patch, ct: ct);
        if (!r.Success)
        {
            await RecordConflictAsync(plan, run, $"apply {pc.Sha[..10]}", pc.Sha, r.StdErr + r.StdOut, ct);
            return false;
        }
        await CommitPickedAsync(plan, run, pc, resolvedManually: false, ct);
        return true;
    }

    private async Task CommitPickedAsync(Plan plan, Run run, PlannedCommit pc, bool resolvedManually, CancellationToken ct)
    {
        var info = await _git.GetCommitAsync(run.CandidateWorktree, pc.Sha);
        var excluded = pc.Paths.Count(p => p.Decision != Decision.Include);
        var trailers = new List<string>
        {
            $"Fork-Over-Source: {plan.FromProduct}@{pc.Sha}",
            $"ForkBridge-Scope: {string.Join(",", pc.Components)}",
            $"ForkBridge-Plan: {plan.Id}",
            $"ForkBridge-Run: {run.Id}",
            $"ForkBridge-Policy: {plan.PolicyHash[..23]}",
        };
        if (excluded > 0) trailers.Add($"ForkBridge-Partial: {excluded} path(s) excluded by policy");
        if (resolvedManually) trailers.Add("ForkBridge-Resolved: manual conflict resolution");
        var env = new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = info.AuthorName, ["GIT_AUTHOR_EMAIL"] = info.AuthorEmail, ["GIT_AUTHOR_DATE"] = info.AuthorDate,
        };
        await CommitAsync(run, AppendTrailers(info.Message, trailers), env, ct);
        var sha = (await _git.OutAsync(run.CandidateWorktree, "rev-parse", "HEAD")).Trim();
        run.ResultCommits[pc.Sha] = sha;
        run.AppliedCommits.Add(pc.Sha);
        run.Log.Add($"applied {pc.Sha[..10]} -> {sha[..10]} {pc.Subject}");
    }

    // ---- reconcile ------------------------------------------------------------------------------
    private async Task<bool> ApplyReconciliationAsync(Plan plan, Run run, CancellationToken ct)
    {
        var rc = plan.Reconciliation!;
        var patch = _store.ReadPlanArtifact(plan, rc.PatchFile!, rc.PatchSha256!);
        var r = await _git.RunAsync(run.CandidateWorktree, ["apply", "--3way", "--index", "--binary", "--whitespace=nowarn"], stdin: patch, ct: ct);
        if (!r.Success)
        {
            await RecordConflictAsync(plan, run, "reconcile", null, r.StdErr + r.StdOut, ct);
            return false;
        }
        await CommitReconciliationAsync(plan, run, false, ct);
        return true;
    }

    private async Task CommitReconciliationAsync(Plan plan, Run run, bool resolvedManually, CancellationToken ct)
    {
        var rc = plan.Reconciliation!;
        var included = rc.Paths.Where(p => p.Decision == Decision.Include).ToList();
        var msg = new StringBuilder();
        msg.Append($"ForkBridge reconciliation: {plan.FromProduct} {plan.SourceHeadSha[..10]} -> {plan.ToProduct}\n\n");
        msg.Append("This commit reconciles shared component content between two snapshots. It is NOT a\n");
        msg.Append("transfer of the original source commits and carries no source authorship.\n\n");
        msg.Append($"Components: {string.Join(", ", plan.SelectedComponents)}\n");
        msg.Append($"Baseline: {plan.ReconcileBaseline}\nSource snapshot: {plan.SourceHeadSha}\nFiles: {included.Count}\n");
        var trailers = new List<string>
        {
            $"ForkBridge-Reconcile-Source: {plan.FromProduct}@{plan.SourceHeadSha}",
            $"ForkBridge-Reconcile-Baseline: {plan.ReconcileBaseline}",
            $"ForkBridge-Scope: {string.Join(",", rc.Components)}",
            $"ForkBridge-Plan: {plan.Id}", $"ForkBridge-Run: {run.Id}", $"ForkBridge-Policy: {plan.PolicyHash[..23]}",
        };
        if (resolvedManually) trailers.Add("ForkBridge-Resolved: manual conflict resolution");
        await CommitAsync(run, AppendTrailers(msg.ToString(), trailers), null, ct);
        var sha = (await _git.OutAsync(run.CandidateWorktree, "rev-parse", "HEAD")).Trim();
        run.ResultCommits["reconcile"] = sha;
        run.Log.Add($"reconciliation commit {sha[..10]}");
    }

    // ---- merge (advanced) ----------------------------------------------------------------------
    private async Task<bool> ApplyMergeAsync(Plan plan, Run run, CancellationToken ct)
    {
        var wt = run.CandidateWorktree;
        // Directory-rename inference is disabled: it can silently relocate product-specific files
        // into another product's module (e.g. App.VAM/* -> App.MTL/*).
        var r = await _git.RunAsync(wt, ["-c", "merge.directoryRenames=false", "merge", "--no-ff", "--no-commit", "--no-verify", plan.SourceHeadSha], ct: ct);
        // Enforce policy on every path the source side changed since the baseline.
        foreach (var d in plan.Reconciliation!.Paths)
        {
            if (d.Decision == Decision.Include) continue;
            foreach (var path in new[] { d.SourceOldPath, d.SourcePath }.Where(p => p is not null).Distinct())
            {
                var tgtPath = path!;
                if (await _git.ObjectExistsAsync(wt, $"{plan.TargetBaseSha}:{tgtPath}"))
                    await _git.CheckedAsync(wt, ["checkout", plan.TargetBaseSha, "--", tgtPath], ct: ct);
                else
                {
                    await _git.RunAsync(wt, ["rm", "-q", "-f", "--cached", "--ignore-unmatch", "--", tgtPath], ct: ct);
                    var full = Path.Combine(wt, tgtPath);
                    if (File.Exists(full)) File.Delete(full);
                }
                run.Log.Add($"merge policy: kept target state of {tgtPath} ({d.Reason})");
            }
        }
        // Defence in depth: any path the merge changed that policy did not explicitly include is reset
        // to the target's state and reported.
        var allowed = plan.Reconciliation.Paths.Where(p => p.Decision == Decision.Include)
            .SelectMany(p => new[] { p.TargetPath, p.TargetOldPath }).Where(p => p is not null).ToHashSet();
        var touched = await _git.RunAsync(wt, ["-c", "core.quotePath=false", "diff", "--cached", "--name-only", "-z", "--no-renames", plan.TargetBaseSha], ct: ct);
        var unexpected = Encoding.UTF8.GetString(touched.StdOutBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => !allowed.Contains(p)).ToList();
        foreach (var path in unexpected)
        {
            if (await _git.ObjectExistsAsync(wt, $"{plan.TargetBaseSha}:{path}")) await _git.CheckedAsync(wt, ["checkout", plan.TargetBaseSha, "--", path], ct: ct);
            else
            {
                await _git.RunAsync(wt, ["rm", "-q", "-f", "--cached", "--ignore-unmatch", "--", path], ct: ct);
                var full = Path.Combine(wt, path);
                if (File.Exists(full)) File.Delete(full);
            }
        }
        if (unexpected.Count > 0)
            run.Findings.Add(new Finding
            {
                Id = Finding.MakeId("MERGE_UNEXPECTED_PATHS", run.Id), Code = "MERGE_UNEXPECTED_PATHS", Category = "history", Severity = Severity.Warning,
                RequiresReview = true, Files = unexpected.Take(50).ToList(), Title = "The merge touched paths outside the planned shared changes; they were reset to the target",
                Evidence = unexpected.Take(30).Select(u => $"reset: {u}").ToList(), SuggestedAction = "Review; ancestry still records these source changes as merged.",
            });
        var unmerged = await _git.LinesAsync(wt, "diff", "--name-only", "--diff-filter=U");
        if (unmerged.Count > 0 || (!r.Success && r.ExitCode != 1))
        {
            await RecordConflictAsync(plan, run, "merge", null, r.StdOut + r.StdErr, ct);
            return false;
        }
        await CommitMergeAsync(plan, run, false, ct);
        return true;
    }

    private async Task CommitMergeAsync(Plan plan, Run run, bool resolvedManually, CancellationToken ct)
    {
        var msg = $"ForkBridge merge (advanced): {plan.FromProduct} {plan.SourceHeadSha[..10]} -> {plan.ToProduct}\n\n" +
                  "Full-history merge. Product-specific, target-pinned and unselected paths were reset to the target's\n" +
                  "state; git ancestry nevertheless records ALL source commits as merged.\n";
        var trailers = new List<string>
        {
            $"ForkBridge-Merge-Source: {plan.FromProduct}@{plan.SourceHeadSha}", $"ForkBridge-Scope: {string.Join(",", plan.SelectedComponents)}",
            $"ForkBridge-Plan: {plan.Id}", $"ForkBridge-Run: {run.Id}", $"ForkBridge-Policy: {plan.PolicyHash[..23]}",
        };
        if (resolvedManually) trailers.Add("ForkBridge-Resolved: manual conflict resolution");
        await CommitAsync(run, AppendTrailers(msg, trailers), null, ct);
        run.ResultCommits["merge"] = (await _git.OutAsync(run.CandidateWorktree, "rev-parse", "HEAD")).Trim();
    }

    // ---- shared helpers -------------------------------------------------------------------------
    private async Task CommitAsync(Run run, string message, Dictionary<string, string?>? env, CancellationToken ct)
    {
        env ??= new();
        if (await _git.ConfigGetAsync(run.CandidateWorktree, "user.name") is null) { env["GIT_COMMITTER_NAME"] = "ForkBridge"; env.TryAdd("GIT_AUTHOR_NAME", "ForkBridge"); }
        if (await _git.ConfigGetAsync(run.CandidateWorktree, "user.email") is null) { env["GIT_COMMITTER_EMAIL"] = "forkbridge@localhost"; env.TryAdd("GIT_AUTHOR_EMAIL", "forkbridge@localhost"); }
        var msgFile = Path.Combine(run.WorkspaceDir, "commit-msg.txt");
        await File.WriteAllTextAsync(msgFile, message, new UTF8Encoding(false), ct);
        var staged = await _git.RunAsync(run.CandidateWorktree, ["diff", "--cached", "--quiet"], ct: ct);
        var args = new List<string> { "-c", "commit.gpgsign=false", "commit", "--quiet", "--no-verify", "--cleanup=verbatim", "-F", msgFile };
        if (staged.ExitCode == 0) args.Add("--allow-empty");
        await _git.CheckedAsync(run.CandidateWorktree, args, env, ct: ct);
        File.Delete(msgFile);
    }

    public static string AppendTrailers(string message, IEnumerable<string> trailers)
    {
        var m = message.TrimEnd('\n', '\r', ' ');
        var lastPara = m.Split("\n\n").Last();
        bool hasTrailerBlock = lastPara.Split('\n').All(l => Regex.IsMatch(l, @"^[\w-]+: "));
        return m + (hasTrailerBlock && m.Contains("\n\n") ? "\n" : "\n\n") + string.Join("\n", trailers) + "\n";
    }

    private async Task RecordConflictAsync(Plan plan, Run run, string operation, string? sourceCommit, string output, CancellationToken ct)
    {
        var wt = run.CandidateWorktree;
        var files = (await _git.LinesAsync(wt, "diff", "--name-only", "--diff-filter=U")).ToList();
        var evidence = Path.Combine(run.WorkspaceDir, "evidence", $"conflict-{run.NextCommitIndex:D4}");
        Directory.CreateDirectory(evidence);
        var stages = await _git.RunAsync(wt, ["ls-files", "-u", "-z"], ct: ct);
        foreach (var rec in Encoding.UTF8.GetString(stages.StdOutBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rec.IndexOf('\t');
            var meta = rec[..tab].Split(' ');
            var path = rec[(tab + 1)..];
            var label = meta[2] switch { "1" => "base", "2" => "target", "3" => "source", _ => "stage" + meta[2] };
            var blob = await _git.RunAsync(wt, ["cat-file", "blob", meta[1]], ct: ct);
            var dest = Path.Combine(evidence, StateStore.Sanitize(path.Replace('/', '_')) + "." + label);
            await File.WriteAllBytesAsync(dest, blob.StdOutBytes, ct);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "operation.txt"), $"{operation}\n{Infrastructure.Redactor.Redact(output)}", ct);
        // No unmerged index entries means `git apply` rejected the patch as a whole (it is atomic): nothing
        // was applied, so "resolve the conflicts" would be wrong advice. Typical cause: a path the patch
        // addresses does not exist in the target ("does not exist in index").
        var nothingApplied = files.Count == 0;
        if (nothingApplied)
            files = Regex.Matches(output, @"(?:error: patch failed: |error: )([^:\n]+):").Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
        run.Conflict = new ConflictRecord
        {
            Operation = operation, SourceCommit = sourceCommit, Files = files, EvidenceDir = evidence, Output = Infrastructure.Redactor.Redact(output.Trim()),
            Instructions =
            [
                $"Inspect evidence (base/target/source versions): {evidence}",
                $"Resolve in the harness workspace (NOT your checkout): {wt}",
                nothingApplied
                    ? $"NOTHING WAS APPLIED: git apply rejected the whole patch because of {(files.Count > 0 ? string.Join(", ", files) : "the error above")}. Lines such as 'Applied ... with conflicts' in the output describe the attempt, not the workspace. Abort this run and re-plan (a newer ForkBridge may handle the cause); hand-applying the full patch is not recommended."
                    : "Edit the conflicted files and remove all conflict markers.",
                $"Continue: forkbridge resume --run {run.Id}",
                $"Abort:    forkbridge abort --run {run.Id}",
            ],
        };
        run.Findings.Add(new Finding
        {
            Id = Finding.MakeId("CONFLICT", run.PlanId, operation, string.Join(",", files)), Code = "CONFLICT", Category = "conflict",
            Severity = Severity.Error, Blocking = true, Files = files, Commits = sourceCommit is null ? [] : [sourceCommit],
            Title = nothingApplied ? $"Patch rejected during {operation}: nothing was applied" : $"Conflict during {operation}",
            Evidence = [Infrastructure.Redactor.Redact(output.Trim())],
            SuggestedAction = nothingApplied
                ? "Abort and re-plan. The workspace is unchanged from the target base."
                : "Shared-code conflicts are never auto-resolved. Resolve in the workspace and resume, or abort.",
        });
        run.PreserveWorkspace = true;
        _store.SaveRun(run);
    }

    private async Task PostApplyAnalysisAsync(Plan plan, Run run, CancellationToken ct)
    {
        var mirror = _store.MirrorDir(_cfg.Pairs[plan.Pair].Products[plan.ToProduct]);
        var diff = await _git.RunAsync(mirror, ["-c", "core.quotePath=false", "diff", "--binary", "--full-index", plan.TargetBaseSha, run.CandidateSha!], ct: ct);
        var diffFile = Path.Combine(run.WorkspaceDir, "candidate.diff");
        await File.WriteAllBytesAsync(diffFile, diff.StdOutBytes, ct);
        run.CandidateDiffFile = diffFile;
        run.CandidateDiffStat = (await _git.RunAsync(mirror, ["-c", "core.quotePath=false", "diff", "--stat=200", plan.TargetBaseSha, run.CandidateSha!], ct: ct)).StdOut;

        // Broken project references introduced by the transfer (pre-existing ones are reported separately).
        var baseG = await DependencyGraph.BuildAsync(_git, mirror, plan.TargetBaseSha);
        var candG = await DependencyGraph.BuildAsync(_git, mirror, run.CandidateSha!);
        var broken = (DependencyGraph g) => g.Projects.Values.SelectMany(p => p.ProjectReferences.Where(r => !g.Files.Contains(r)).Select(r => $"{p.Path} -> {r}")).ToHashSet();
        var bBase = broken(baseG); var bCand = broken(candG);
        foreach (var b in bCand.Except(bBase))
            run.Findings.Add(new Finding
            {
                Id = Finding.MakeId("BROKEN_PROJECT_REFERENCE", plan.Id, b), Code = "BROKEN_PROJECT_REFERENCE", Category = "dependency", Severity = Severity.Error,
                Blocking = true, Title = "Candidate contains a project reference to a missing project", Evidence = [b],
                SuggestedAction = "Transfer the referenced project or remove the reference.",
            });
        if (bBase.Count > 0)
            run.Findings.Add(new Finding
            {
                Id = Finding.MakeId("PREEXISTING_BROKEN_REFERENCES", plan.TargetBaseSha), Code = "PREEXISTING_BROKEN_REFERENCES", Category = "dependency",
                Severity = Severity.Info, Origin = Origin.PreExisting, Title = $"{bBase.Count} broken project reference(s) already exist in the target base",
                Evidence = bBase.Take(20).ToList(),
            });

        // Merge mode: validate component content independently of ancestry.
        if (plan.Mode == SelectionMode.Merge)
        {
            var mismatches = new List<string>();
            foreach (var d in plan.Reconciliation!.Paths)
            {
                if (d.Status == 'D') continue;
                var path = d.Decision == Decision.Include ? d.TargetPath! : d.SourcePath;
                var cand = await _git.RunAsync(mirror, ["rev-parse", "--verify", "--quiet", $"{run.CandidateSha}:{path}"], ct: ct);
                var expect = d.Decision == Decision.Include
                    ? await _git.RunAsync(mirror, ["rev-parse", "--verify", "--quiet", $"{plan.SourceHeadSha}:{d.SourcePath}"], ct: ct)
                    : await _git.RunAsync(mirror, ["rev-parse", "--verify", "--quiet", $"{plan.TargetBaseSha}:{path}"], ct: ct);
                if (cand.StdOut.Trim() != expect.StdOut.Trim())
                    mismatches.Add($"{path}: candidate {(cand.Success ? cand.StdOut.Trim()[..10] : "<absent>")} expected {(expect.Success ? expect.StdOut.Trim()[..10] : "<absent>")} ({(d.Decision == Decision.Include ? "source content (3-way merge may legitimately differ)" : "target content")})");
            }
            if (mismatches.Count > 0)
                run.Findings.Add(new Finding
                {
                    Id = Finding.MakeId("MERGE_CONTENT_CHECK", run.Id), Code = "MERGE_CONTENT_CHECK", Category = "history", Severity = Severity.Warning,
                    RequiresReview = true, Title = "Merge result differs from the expected component content", Evidence = mismatches.Take(40).ToList(),
                    SuggestedAction = "Review each file: ancestry now claims the source is fully merged.",
                });
        }
        _store.SaveRun(run);
    }

    private async Task ValidateAsync(Plan plan, Run run, SimulateOptions opt, CancellationToken ct)
    {
        var pair = _cfg.Pairs[plan.Pair];
        var profileName = opt.Profile ?? pair.Defaults.ValidationProfile;
        if (!pair.Validation.TryGetValue(profileName, out var profile))
        {
            run.Findings.Add(new Finding
            {
                Id = Finding.MakeId("NO_VALIDATION_PROFILE", plan.Pair, profileName), Code = "NO_VALIDATION_PROFILE", Category = "validation",
                Severity = Severity.Error, Origin = Origin.Environment, Title = $"Validation profile '{profileName}' is not configured",
                Evidence = ["Without validation the candidate can never be READY."], SuggestedAction = "Configure a validation profile for the pair.",
            });
            return;
        }
        var mirror = _store.MirrorDir(pair.Products[plan.ToProduct]);
        run.BaselineWorktree = Path.Combine(run.WorkspaceDir, "baseline");
        await EnsureWorktreeAsync(mirror, run, run.BaselineWorktree, plan.TargetBaseSha, ct);
        var touched = plan.Commits.Where(c => c.State == CommitState.Pending).SelectMany(c => c.Components)
            .Concat(plan.Reconciliation?.Components ?? []).Distinct().ToList();
        if (plan.Mode == SelectionMode.Merge) touched = plan.SelectedComponents;

        var userRepos = _cfg.Repositories.Values.Where(r => !r.Placeholder).Select(r => _cfg.ResolvePath(r.Path)).Where(Directory.Exists).ToList();
        var before = new Dictionary<string, string>();
        foreach (var r in userRepos) before[r] = await _guard.SnapshotAsync(r);

        run.BaselineValidation = await _validation.RunAsync(profile, run.BaselineWorktree, "baseline", opt.TrustValidation, touched, opt.ExtraValidationEnv, ct);
        run.CandidateValidation = await _validation.RunAsync(profile, run.CandidateWorktree, "candidate", opt.TrustValidation, touched, opt.ExtraValidationEnv, ct);
        run.ValidationComparison = ValidationRunner.Compare(run.BaselineValidation, run.CandidateValidation);

        foreach (var r in userRepos)
            if (await _guard.SnapshotAsync(r) != before[r])
                run.Findings.Add(new Finding
                {
                    Id = Finding.MakeId("USER_REPO_MODIFIED", run.Id, r), Code = "USER_REPO_MODIFIED", Category = "security", Severity = Severity.Critical,
                    Blocking = true, Origin = Origin.Environment, Title = "A user checkout changed while validation commands ran",
                    Evidence = [$"{r}: HEAD/index/config/status snapshot differs before vs after validation."],
                    SuggestedAction = "Treat the validation scripts as untrusted: inspect the checkout, and use runner: docker.",
                });
        foreach (var cmp in run.ValidationComparison.Where(c => c.Required))
        {
            if (cmp.Verdict == "NEW_FAILURE")
                run.Findings.Add(VFinding("VALIDATION_NEW_FAILURE", cmp, Severity.Error, Origin.TransferIntroduced, "Check passes on the baseline but fails on the candidate"));
            else if (cmp.Verdict == "BASELINE_FAILURE")
                run.Findings.Add(VFinding("VALIDATION_BASELINE_FAILURE", cmp, Severity.Error, Origin.PreExisting, "Check fails on both baseline and candidate (cannot prove the candidate is safe)"));
            else if (cmp.Verdict is "UNAVAILABLE")
                run.Findings.Add(VFinding("VALIDATION_UNAVAILABLE", cmp, Severity.Warning, Origin.Environment, "Required check could not be executed"));
        }
        _store.SaveRun(run);
    }

    /// <summary>Removes every trace of a harness worktree so it can be re-created. A `git worktree add`
    /// killed mid-way can leave its registration (mirror/worktrees/&lt;name&gt;) locked ("initializing"),
    /// or half-written (empty commondir/gitdir) which makes git refuse unrelated commands such as
    /// `branch -f`. git's own remove/prune cannot clear those, so the registration directory - inside
    /// the harness-owned mirror - is deleted when it belongs to this path or is unreadable. Safe because
    /// git-mutating engine operations are serialized (JobService), so no other `worktree add` is in flight.</summary>
    private async Task ForgetWorktreeAsync(string mirror, Run run, string path, CancellationToken ct)
    {
        await _git.RunAsync(mirror, ["worktree", "unlock", path], ct: ct);
        await _git.RunAsync(mirror, ["worktree", "remove", "--force", "--force", path], ct: ct);
        if (Directory.Exists(path)) _store.Fs.DeleteInsideOwned(path, run.WorkspaceDir, run.Id);
        foreach (var name in _mirrors.RepairWorktreeRegistrations(mirror))
            run.Log.Add($"recovered: removed unreadable worktree registration {name}");
        var admin = Path.Combine(mirror, "worktrees");
        if (Directory.Exists(admin))
        {
            // A readable registration that still points at this path (locked, so prune keeps it).
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var want = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var dir in Directory.GetDirectories(admin))
            {
                var gitdir = File.ReadAllText(Path.Combine(dir, "gitdir")).Trim();
                var ours = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(gitdir) ?? gitdir)), want, cmp);
                if (ours && _store.Fs.IsInsideRoot(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    run.Log.Add($"recovered: removed stale worktree registration {Path.GetFileName(dir)}");
                }
            }
        }
        await _git.RunAsync(mirror, ["worktree", "prune"], ct: ct);
    }

    /// <summary>Creates a detached worktree, or repairs one left half-created by a cancelled/killed
    /// `git worktree add` (directory present but not a valid checkout of the expected commit).</summary>
    private async Task EnsureWorktreeAsync(string mirror, Run run, string path, string sha, CancellationToken ct)
    {
        if (Directory.Exists(path))
        {
            var head = await _git.RunAsync(path, ["rev-parse", "HEAD"], ct: ct);
            var clean = head.Success && head.StdOut.Trim() == sha && (await _git.RunAsync(path, ["status", "--porcelain"], ct: ct)).StdOut.Trim().Length == 0;
            if (clean) return;
            await ForgetWorktreeAsync(mirror, run, path, ct);
            run.Log.Add($"recovered incomplete worktree {Path.GetFileName(path)}");
        }
        else await ForgetWorktreeAsync(mirror, run, path, ct);   // the registration can outlive the directory
        await _git.CheckedAsync(mirror, ["worktree", "add", "--quiet", "--detach", path, sha], ct: ct);
    }

    private Finding VFinding(string code, ValidationComparison c, Severity sev, Origin origin, string title) => new()
    {
        Id = Finding.MakeId(code, c.StepId), Code = code, Category = "validation", Severity = sev, Origin = origin, Title = $"{title}: {c.StepId}",
        Evidence = [$"baseline={c.Baseline} candidate={c.Candidate}"], SuggestedAction = "See the step output in the report.",
    };

    private Run Finish(Plan plan, Run run, bool conflict)
    {
        run.Status = conflict ? RunStatus.BLOCKED : StatusEvaluator.ForRun(plan, run, plan.AcceptedFindings);
        run.FinishedAt = DateTimeOffset.UtcNow;
        _store.SaveRun(run);
        ReportWriter.Write(_store, plan, run);
        if (!conflict && !run.PreserveWorkspace) RemoveWorktrees(run);
        _store.SaveRun(run);
        return run;
    }

    /// <summary>Removes the run's worktrees (harness-owned) but keeps the branch, reports and evidence.</summary>
    public void RemoveWorktrees(Run run)
    {
        var plan = _store.LoadPlan(run.PlanId);
        var mirror = _store.MirrorDir(_cfg.Pairs[plan.Pair].Products[plan.ToProduct]);
        foreach (var wt in new[] { run.CandidateWorktree, run.BaselineWorktree }.Where(w => w is not null && Directory.Exists(w)))
        {
            _store.Fs.AssertNoLinksBetweenRootAndPath(wt!);
            _git.RunAsync(mirror, ["worktree", "remove", "--force", wt!]).GetAwaiter().GetResult();
        }
        _git.RunAsync(mirror, ["worktree", "prune"]).GetAwaiter().GetResult();
        run.Log.Add("worktrees removed (branch, reports and evidence kept)");
    }
}
