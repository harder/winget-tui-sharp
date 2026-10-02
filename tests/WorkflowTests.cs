namespace WingetTuiSharp.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void InstallPlan_SkipsInstalledUnavailableAndUnresolvedPackages ()
    {
        Package ready = new () { Id = "A.Tool", Name = "Tool", Version = "2", Source = "winget" };
        Package installed = new () { Id = "B.Tool", Name = "Installed", Version = "1", Source = "winget" };
        Package missingSource = new () { Id = "C.Tool", Name = "Missing source", Version = "1", Source = "other" };
        Package unresolved = new () { Id = "D.Tool", Name = "Unresolved", Source = "winget" };

        BatchPlan plan = BatchPlanning.ForInstalls ([ready, installed, missingSource, unresolved],
            [installed], ["winget"]);

        Assert.Equal (1, plan.Ready);
        Assert.Equal (3, plan.Skipped);
        Assert.Contains ("Already installed", plan.Items [1].Reason);
        Assert.Contains ("Source is unavailable", plan.Items [2].Reason);
        Assert.Contains ("not been resolved", plan.Items [3].Reason);
    }

    [Fact]
    public void InstallPlan_MatchesInstalledIdentityBySourceAndId ()
    {
        Package fromA = new () { Id = "Contoso.Tool", Name = "Tool A", Version = "1", Source = "sourceA" };
        Package fromB = new () { Id = "Contoso.Tool", Name = "Tool B", Version = "1", Source = "sourceB" };
        BatchPlan knownSource = BatchPlanning.ForInstalls ([fromB], [fromA], ["sourceA", "sourceB"]);
        Assert.Equal (1, knownSource.Ready);

        Package sourceUnknown = new () { Id = "Contoso.Tool", Name = "Unknown", Version = "1" };
        BatchPlan unknownSource = BatchPlanning.ForInstalls ([fromB], [sourceUnknown], ["sourceA", "sourceB"]);
        Assert.Equal (0, unknownSource.Ready);
        Assert.Contains ("Already installed", unknownSource.Items [0].Reason);
    }

    [Fact]
    public void UpgradeSelection_MatchesSourceAndId ()
    {
        Package fromA = new () { Id = "Contoso.Tool", Name = "Tool A", Source = "sourceA" };
        Package fromB = new () { Id = "Contoso.Tool", Name = "Tool B", Source = "sourceB" };
        HashSet<string> selected = new (StringComparer.OrdinalIgnoreCase) { BatchPlanning.Key (fromB) };

        Assert.Equal ("sourceB", Assert.Single (BatchPlanning.SelectedPackages ([fromA, fromB], selected)).Source);
    }

    [Fact]
    public async Task SavedSetResolution_IsBoundedAndPreservesOrder ()
    {
        List<Package> stubs = [.. Enumerable.Range (0, 12).Select (i => new Package
        {
            Id = $"Tool{i}", Name = $"Tool{i}", Source = "winget"
        })];
        int active = 0;
        int maximum = 0;
        List<Package> resolved = await BatchPlanning.ResolveUnresolvedAsync (stubs, async (item, ct) =>
        {
            int now = Interlocked.Increment (ref active);
            InterlockedExtensions.RecordMaximum (ref maximum, now);
            try
            {
                await Task.Delay (25, ct);
                return new Package { Id = item.Id, Name = item.Name, Source = item.Source, Version = "1" };
            }
            finally { Interlocked.Decrement (ref active); }
        }, CancellationToken.None);

        Assert.InRange (maximum, 2, 4);
        Assert.Equal (stubs.Select (x => x.Id), resolved.Select (x => x.Id));
        Assert.All (resolved, x => Assert.Equal ("1", x.Version));
    }

    [Fact]
    public void Completion_CarriesHistoryFailureIntoStatus ()
    {
        RunRecord run = new (DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Install",
            [new ("A.Tool", "Tool", "winget", "Succeeded", "Done")]);
        (string message, bool isError) = run.Completion ("Access denied");
        Assert.True (isError);
        Assert.Contains ("history save failed: Access denied", message);
    }

    [Fact]
    public void UpgradePlan_RequiresFreshPinsAndSkipsPinnedRows ()
    {
        Package upgrade = new () { Id = "A.Tool", Name = "Tool", Version = "1", AvailableVersion = "2", Source = "winget" };
        Package pinned = new () { Id = "B.Tool", Name = "Pinned", Version = "1", AvailableVersion = "2",
            Source = "winget", PinState = new (PinStateKind.Blocking) };

        Assert.Equal (0, BatchPlanning.ForUpgrades ([upgrade], pinsFresh: false).Ready);
        BatchPlan plan = BatchPlanning.ForUpgrades ([upgrade, pinned], pinsFresh: true);
        Assert.Equal (1, plan.Ready);
        Assert.Contains ("Pinned", plan.Items [1].Reason);
    }

    [Fact]
    public void Store_RoundTripsSetsAndKeepsOnlyRecentRuns ()
    {
        string root = Path.Combine (Path.GetTempPath (), "winget-tui-workflow-test-" + Guid.NewGuid ().ToString ("N"));
        try
        {
            WorkflowStore store = new (root);
            store.SaveSet (new ("My tools", [new ("A.Tool", "Tool", "winget")]));
            Assert.Equal ("A.Tool", Assert.Single (Assert.Single (store.Sets ()).Packages).Id);

            for (int i = 0; i < 25; i++)
            {
                store.SaveRun (new (DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Install",
                    [new ($"Tool{i}", $"Tool{i}", "winget", "Succeeded", "Done")]));
            }
            Assert.Equal (20, store.Runs ().Count);
            Assert.Equal ("Tool24", store.Runs () [0].Items [0].Id);
            store.DeleteSet ("My tools");
            Assert.Empty (store.Sets ());
        }
        finally
        {
            if (Directory.Exists (root)) Directory.Delete (root, true);
        }
    }

    [Fact]
    public async Task UpdateChecks_UseBaselineThenReportNewVersion ()
    {
        string root = Path.Combine (Path.GetTempPath (), "winget-tui-check-test-" + Guid.NewGuid ().ToString ("N"));
        try
        {
            WorkflowStore store = new (root);
            MockBackend backend = new ();
            UpdateCheckSnapshot first = await UpdateChecks.CheckAsync (backend, store, CancellationToken.None);
            Assert.Equal ("Succeeded", first.Status);
            Assert.Equal (0, first.NewOrChanged);

            UpdateCheckSnapshot second = await UpdateChecks.CheckAsync (backend, store, CancellationToken.None);
            Assert.Equal (0, second.NewOrChanged);

            List<UpdateItem> changed = [.. second.Updates];
            changed [0] = changed [0] with { AvailableVersion = "999" };
            store.SaveCheck (second with { Updates = changed });
            UpdateCheckSnapshot third = await UpdateChecks.CheckAsync (backend, store, CancellationToken.None);
            Assert.Equal (1, third.NewOrChanged);
            Assert.Equal ("Succeeded", store.LatestCheck ()?.Status);
        }
        finally
        {
            if (Directory.Exists (root)) Directory.Delete (root, true);
        }
    }

    [Fact]
    public void ScheduledTime_AndSourceScopedUpgradeArguments ()
    {
        Assert.True (UpdateChecks.TryParseDailyTime ("09:30", out _));
        Assert.False (UpdateChecks.TryParseDailyTime ("25:00", out _));
        Assert.Contains ("--source", CliBackend.UpgradeByIdArgs ("A.Tool", "winget"));
        Assert.Contains ("--exact", CliBackend.UpgradeByIdArgs ("A.Tool", "winget"));
        string [] install = CliBackend.InstallArgs ("A.Tool", null, source: "winget");
        Assert.Contains ("--source", install);
        Assert.DoesNotContain ("--exact", install);
    }
}

internal static class InterlockedExtensions
{
    internal static void RecordMaximum (ref int maximum, int observed)
    {
        int current = Volatile.Read (ref maximum);
        while (observed > current)
        {
            int seen = Interlocked.CompareExchange (ref maximum, observed, current);
            if (seen == current) return;
            current = seen;
        }
    }
}
