using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;

namespace ForkBridge.Alignment.Tests;

public class RowDiffTests
{
    [Fact]
    public async Task Row_diffs_are_unified_hunks_between_the_plan_blobs_with_side_labels_and_last_commits()
    {
        using var fx = new AlignFixture("backend");
        fx.WriteAlignConfig(AlignFixture.BackendProfileYaml.Replace("@SYNC@", fx.SyncBase));
        var p = fx.Profile();
        var plan = await new AlignmentPlanner(p, fx.Git).PlanAsync(new AlignmentPlanRequest(new SideSelection { Rev = fx.PrimaryTip }, new SideSelection { Rev = fx.ForkTip }));

        var both = await RowDiffs.ComputeAsync(p, fx.Git, plan, "Shared/C.cs");             // BothChanged → Merged
        Assert.Equal(Decision.Merged, both.Decision);
        Assert.StartsWith("--- ISL/Shared/C.cs\n+++ QiSL/Shared/C.cs\n@@", both.ForkVsPrimary);
        Assert.StartsWith("--- SyncBase/Shared/C.cs\n+++ ISL/Shared/C.cs\n@@", both.BaseToFork);
        Assert.StartsWith("--- SyncBase/Shared/C.cs\n+++ QiSL/Shared/C.cs\n@@", both.BaseToPrimary);
        Assert.Contains("+++ merged/Shared/C.cs", both.ForkToMerged);
        Assert.NotNull(both.PrimaryText); Assert.NotNull(both.ForkText);
        Assert.NotNull(both.PrimaryLastCommit); Assert.NotNull(both.ForkLastCommit);
        Assert.False(both.Binary);

        var added = plan.Rows.First(r => r.Primary is null || r.Fork is null);                 // one side absent → /dev/null
        var d = await RowDiffs.ComputeAsync(p, fx.Git, plan, added.Path);
        Assert.Contains("/dev/null", d.ForkVsPrimary);

        await Assert.ThrowsAsync<ArgumentException>(() => RowDiffs.ComputeAsync(p, fx.Git, plan, "../../etc/passwd"));
    }
}
