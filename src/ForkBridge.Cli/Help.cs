namespace ForkBridge.Cli;

internal static class Help
{
    public static string Text(string? command) => command switch
    {
        "plan" => """
            forkbridge plan --pair <pair> --from <product> --to <product> [selection] [options]

            Selection (exactly one; kept semantically distinct):
              --source-ref <branch> [--source-base <branch>]   feature branch relative to its base (merge-base explained)
              --range <A>..<B>                                 explicit bounded range in the source repo
              --commits <sha> [<sha> ...]                      explicit ordered list
              --mode reconcile [--source-ref <ref>] [--baseline <sha>]
                                                               snapshot reconciliation of shared components (one new commit,
                                                               never presented as the original source commits)
              --mode merge                                     ADVANCED full-history merge (must be allowed for the direction)

            Options:
              --target-base <branch>     default from config (pairs.<pair>.defaults.targetBase)
              --components <id> [...]    restrict to these components (default: all confirmed)
              --allow-proposed           allow components whose mapping is only proposed (adds a REVIEW finding)
              --reapply <sha> [...]      re-apply commits whose provenance says transferred but content is missing
              --offline                  do not contact remotes; record stale-ref limitation in the plan
              --prefer-local             prefer local branches over remote-tracking branches
              --json                     machine-readable output

            Every ref is resolved to an exact SHA and stored in the immutable plan.
            """,
        "simulate" or "resume" => """
            forkbridge simulate --plan <plan-id> [--trust-validation] [--profile <name>] [--preserve-workspace] [--skip-validation]
            forkbridge resume   --run <run-id>  [--trust-validation]

            Applies the immutable plan in a harness-owned worktree (never your checkout), then runs the
            validation profile on the pinned target base (baseline) AND on the candidate and compares them.
            Validation commands execute repository code: they only run when the profile is marked trusted
            AND --trust-validation (or FORKBRIDGE_TRUST_VALIDATION=1) is given. Otherwise checks are UNAVAILABLE.
            Refuses with exit 13 if the target base or policy changed since planning.
            """,
        "publish" or "approve" => """
            forkbridge approve --run <run-id> --candidate <sha-prefix> [--accept <finding-id> ...]
            forkbridge publish --run <run-id> [--approve --candidate <sha-prefix> [--accept ...]] [--create-pr]

            Approval is bound to: candidate SHA, target remote, branch, report hash, policy hash and target base.
            Publishing pushes ONLY the reviewed candidate to a NEW harness branch (never forced, never a
            protected branch) using your git credential helper. --create-pr uses publish.provider (azure-devops).
            """,
        _ => """
            ForkBridge 0.1.0 - local-first cross-repository transfer and validation harness

            Usage: forkbridge <command> [options]      (config: --config <file> | FORKBRIDGE_CONFIG | ./forkbridge.yml)

            Commands
              discover    [--fetch] [--depth N] [--write-config draft.yml]   read-only repository audit + proposed mappings
              drift       --pair <p> [--from x --to y] [--offline]           shared-component drift between default bases
              plan        --pair <p> --from <x> --to <y> <selection>         create an immutable plan   (help plan)
              simulate    --plan <id> [--trust-validation]                   isolated apply + validation (help simulate)
              resume      --run <id>                                         continue after conflict resolution/crash/cancel
              abort       --run <id>                                         drop workspace + harness branch, keep reports
              report      --run <id> | --plan <id> [--format markdown|json]
              approve     --run <id> --candidate <sha> [--accept <ids>]
              publish     --run <id> [--approve --candidate <sha>] [--create-pr]   (help publish)
              cleanup     --run <id> | --all-finished                        remove harness-owned artifacts only
              runs | plans [--limit N]                                       history
              bundle create --plans <id> <id> [--name n] | simulate --bundle <id> [--bundle-profile p] | publish --bundle <id> | show --bundle <id>
              validate-config

            Exit codes
              0 READY / success      9 NO_CHANGES          10 REVIEW_REQUIRED     11 BLOCKED
              12 VALIDATION_FAILED   13 stale plan         14 refused by policy   15 remote fetch failed
              16 safety refusal      2 usage error         3 config error         20 internal   130 cancelled

            Guarantees: user checkouts are only read (never branch/index/worktree/remote changes); all
            writes go to the state directory; nothing is pushed without explicit approval.
            """,
    };
}
