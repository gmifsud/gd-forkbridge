using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Policy;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Transfer;

namespace ForkBridge.Core.Publishing;

public sealed class PublishRefusedException(string message, int exitCode = ExitCodes.PolicyViolation) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public interface IPullRequestProvider
{
    string Name { get; }
    Task<string> CreateAsync(string remoteUrl, string sourceBranch, string targetBranch, string title, string description, bool draft, CancellationToken ct);
}

/// <summary>Publication is opt-in, approval-bound and never forced.</summary>
public sealed class Publisher
{
    private readonly ForkBridgeConfig _cfg;
    private readonly GitClient _git;
    private readonly StateStore _store;
    private readonly Simulator _sim;
    private readonly IPullRequestProvider? _pr;

    public Publisher(ForkBridgeConfig cfg, GitClient git, StateStore store, Simulator sim, IPullRequestProvider? pr)
    {
        _cfg = cfg; _git = git; _store = store; _sim = sim; _pr = pr;
    }

    private async Task<string> TargetRemoteUrlAsync(Plan plan)
    {
        var alias = _cfg.Pairs[plan.Pair].Products[plan.ToProduct];
        var repo = _cfg.Repositories[alias];
        return await _git.ConfigGetAsync(_cfg.ResolvePath(repo.Path), $"remote.{repo.Remote}.url")
               ?? throw new PublishRefusedException($"Target checkout has no remote '{repo.Remote}'.");
    }

    public async Task<ApprovalRecord> ApproveAsync(string runId, string candidatePrefix, IEnumerable<string> accept, string approvedBy, CancellationToken ct = default)
    {
        var run = _store.LoadRun(runId);
        var plan = _store.LoadPlan(run.PlanId);
        if (run.CandidateSha is null) throw new PublishRefusedException("Run has no candidate commit.", ExitCodes.Blocked);
        if (candidatePrefix.Length < 7 || !run.CandidateSha.StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase))
            throw new PublishRefusedException($"Approval must name the exact candidate SHA (at least 7 characters). Candidate is {run.CandidateSha}.", ExitCodes.UsageError);
        var accepted = accept.ToList();
        var known = plan.Findings.Concat(run.Findings).Select(f => f.Id).ToHashSet();
        var unknown = accepted.Where(a => !known.Contains(a)).ToList();
        if (unknown.Count > 0) throw new PublishRefusedException($"Unknown finding id(s): {string.Join(", ", unknown)}", ExitCodes.UsageError);
        var status = StatusEvaluator.ForRun(plan, run, plan.AcceptedFindings.Concat(accepted));
        if (status != RunStatus.READY)
        {
            var open = plan.Findings.Concat(run.Findings).Where(f => f.Blocking || (f.RequiresReview && !accepted.Contains(f.Id))).Select(f => $"{f.Id} {f.Title}");
            throw new PublishRefusedException($"Run is {status}; cannot approve.\n  " + string.Join("\n  ", open), ExitCodes.For(status));
        }
        await _sim.VerifyPlanStillValidAsync(plan, ct);
        var url = await TargetRemoteUrlAsync(plan);
        var approval = new ApprovalRecord
        {
            CandidateSha = run.CandidateSha, TargetRemoteRedacted = Redactor.Redact(url), Branch = run.Branch,
            ReportSha256 = ReportWriter.ReportHash(_store, run.Id), PolicyHash = plan.PolicyHash, TargetBaseSha = plan.TargetBaseSha,
            AcceptedFindings = accepted, ApprovedBy = approvedBy, ApprovedAt = DateTimeOffset.UtcNow,
        };
        run.Approval = approval;
        run.Status = RunStatus.READY;
        run.Log.Add($"approved by {approvedBy}: {approval.Binding}");
        _store.SaveRun(run);
        return approval;
    }

    public async Task<PublicationRecord> PublishAsync(string runId, bool createPr, CancellationToken ct = default)
    {
        var run = _store.LoadRun(runId);
        var plan = _store.LoadPlan(run.PlanId);
        var pair = _cfg.Pairs[plan.Pair];
        var a = run.Approval ?? throw new PublishRefusedException("Run is not approved. Use --approve --candidate <sha>.", ExitCodes.ReviewRequired);
        var url = await TargetRemoteUrlAsync(plan);
        // The approval is bound to the exact candidate, remote, branch, report, policy and target base.
        var checks = new List<string>();
        if (a.CandidateSha != run.CandidateSha) checks.Add("candidate SHA changed since approval");
        if (a.Branch != run.Branch) checks.Add("branch changed since approval");
        if (a.TargetRemoteRedacted != Redactor.Redact(url)) checks.Add("target remote URL changed since approval");
        if (a.ReportSha256 != ReportWriter.ReportHash(_store, run.Id)) checks.Add("report changed since approval");
        if (a.PolicyHash != ConfigLoader.PolicyHash(_cfg, plan.Pair)) checks.Add("policy changed since approval");
        if (checks.Count > 0) throw new PublishRefusedException("Approval no longer matches: " + string.Join("; ", checks) + ". Re-approve.", ExitCodes.Stale);
        await _sim.VerifyPlanStillValidAsync(plan, ct);

        if (pair.ProtectedBranches.Any(p => Glob.HasWildcard(p) ? Glob.IsMatch(p, run.Branch) : p == run.Branch) || !run.Branch.StartsWith(pair.Defaults.BranchPrefix + "/"))
            throw new PublishRefusedException($"Refusing to publish to protected/non-harness branch '{run.Branch}'.");

        var mirror = _store.MirrorDir(pair.Products[plan.ToProduct]);
        var ls = await _git.RunAsync(mirror, ["ls-remote", "--heads", url, run.Branch], ct: ct);
        if (!ls.Success) throw new PublishRefusedException("Could not query the target remote: " + Redactor.Redact(ls.StdErr.Trim()), ExitCodes.FetchFailed);
        if (ls.StdOut.Trim().Length > 0)
            throw new PublishRefusedException($"Remote branch '{run.Branch}' already exists. ForkBridge never force-pushes.");
        var baseLs = await _git.RunAsync(mirror, ["ls-remote", "--heads", url, plan.TargetBaseRef], ct: ct);
        var remoteBase = baseLs.StdOut.Split('\t').FirstOrDefault()?.Trim();
        if (!plan.Freshness.Offline && !string.IsNullOrEmpty(remoteBase) && remoteBase != plan.TargetBaseSha)
            throw new PublishRefusedException($"Target base moved on the remote ({plan.TargetBaseSha[..10]} -> {remoteBase[..10]}). Re-plan.", ExitCodes.Stale);

        // No '+' in the refspec: a non-fast-forward is rejected by git itself.
        var push = await _git.RunAsync(mirror, ["push", "--porcelain", url, $"{run.CandidateSha}:refs/heads/{run.Branch}"], timeout: TimeSpan.FromMinutes(15), ct: ct);
        var rec = new PublicationRecord
        {
            At = DateTimeOffset.UtcNow, Branch = run.Branch, CandidateSha = run.CandidateSha!, Pushed = push.Success,
            Error = push.Success ? null : Redactor.Redact(push.StdErr.Trim()),
        };
        if (push.Success && createPr)
        {
            if (_pr is null) rec = rec with { Error = "PR creation requested but no provider configured (publish.provider)." };
            else
            {
                try
                {
                    var md = File.ReadAllText(ReportWriter.MarkdownPath(_store, run.Id));
                    var desc = $"ForkBridge transfer {plan.FromProduct} → {plan.ToProduct} ({plan.Mode}).\n\nRun `{run.Id}` · Plan `{plan.Id}`" +
                               (run.BundleId is null ? "" : $" · Bundle `{run.BundleId}`") + $"\n\nCandidate `{run.CandidateSha}`\n\n" + (md.Length > 3500 ? md[..3500] + "\n…" : md);
                    var prUrl = await _pr.CreateAsync(url, run.Branch, plan.TargetBaseRef, $"[ForkBridge] {plan.FromProduct}→{plan.ToProduct}: {plan.SourceRef ?? plan.Mode.ToString()}", desc, _cfg.Publish.Draft, ct);
                    rec = rec with { PullRequestUrl = prUrl };
                }
                catch (Exception ex) { rec = rec with { Error = "Branch pushed; PR creation failed: " + Redactor.Redact(ex.Message) }; }
            }
        }
        run.Publications.Add(rec);
        run.Log.Add(rec.Pushed ? $"published {run.Branch}" : $"publish failed: {rec.Error}");
        _store.SaveRun(run);
        // Only the Markdown view is refreshed; report.json stays exactly as approved.
        File.WriteAllText(ReportWriter.MarkdownPath(_store, run.Id), ReportWriter.Markdown(plan, run));
        if (!rec.Pushed) throw new PublishRefusedException("Push failed: " + rec.Error, ExitCodes.FetchFailed);
        return rec;
    }
}

/// <summary>Azure DevOps PR adapter. Credentials come from the local git credential helper at call
/// time and are never stored, logged or written to reports.</summary>
public sealed class AzureDevOpsPullRequestProvider : IPullRequestProvider
{
    private readonly GitClient _git;
    private readonly HttpMessageHandler? _handler;
    private readonly string _workDir;
    public string Name => "azure-devops";

    public AzureDevOpsPullRequestProvider(GitClient git, string workDir, HttpMessageHandler? handler = null)
    {
        _git = git; _workDir = workDir; _handler = handler;
    }

    public static (string Org, string Project, string Repo, string ApiBase)? Parse(string url)
    {
        var m = Regex.Match(url, @"^https://(?:[^@/]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<proj>[^/]+)/_git/(?<repo>[^/?#]+)");
        if (m.Success) return (m.Groups["org"].Value, m.Groups["proj"].Value, m.Groups["repo"].Value, $"https://dev.azure.com/{m.Groups["org"].Value}");
        m = Regex.Match(url, @"^https://(?:[^@/]+@)?(?<org>[^./]+)\.visualstudio\.com/(?:DefaultCollection/)?(?<proj>[^/]+)/_git/(?<repo>[^/?#]+)");
        if (m.Success) return (m.Groups["org"].Value, m.Groups["proj"].Value, m.Groups["repo"].Value, $"https://{m.Groups["org"].Value}.visualstudio.com");
        return null;
    }

    public async Task<string> CreateAsync(string remoteUrl, string sourceBranch, string targetBranch, string title, string description, bool draft, CancellationToken ct)
    {
        var p = Parse(remoteUrl) ?? throw new InvalidOperationException("Remote is not an Azure DevOps Git URL.");
        var host = new Uri(p.ApiBase).Host;
        var fill = await _git.RunAsync(_workDir, ["credential", "fill"],
            env: new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" },
            stdin: Encoding.UTF8.GetBytes($"protocol=https\nhost={host}\npath={p.Org}/{p.Project}/_git/{p.Repo}\n\n"), ct: ct);
        var lines = fill.StdOut.Split('\n').Select(l => l.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        if (!fill.Success || !lines.TryGetValue("password", out var secret))
            throw new InvalidOperationException("No credential available from the git credential helper for " + host);
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{lines.GetValueOrDefault("username") ?? ""}:{secret}")));
        var body = JsonSerializer.Serialize(new
        {
            sourceRefName = "refs/heads/" + sourceBranch, targetRefName = "refs/heads/" + targetBranch,
            title, description = description.Length > 3900 ? description[..3900] : description, isDraft = draft,
        });
        var api = $"{p.ApiBase}/{Uri.EscapeDataString(Uri.UnescapeDataString(p.Project))}/_apis/git/repositories/{Uri.EscapeDataString(Uri.UnescapeDataString(p.Repo))}/pullrequests?api-version=7.1";
        using var resp = await http.PostAsync(api, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Azure DevOps returned {(int)resp.StatusCode}: {Redactor.Redact(text.Length > 400 ? text[..400] : text)}");
        using var doc = JsonDocument.Parse(text);
        var id = doc.RootElement.GetProperty("pullRequestId").GetInt32();
        return $"{p.ApiBase}/{p.Project}/_git/{p.Repo}/pullrequest/{id}";
    }
}
