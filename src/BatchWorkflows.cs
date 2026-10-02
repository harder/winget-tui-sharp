namespace WingetTuiSharp;

public sealed record BatchPlanItem (Package Package, bool CanRun, string Reason);

public sealed record BatchPlan (string Action, List<BatchPlanItem> Items)
{
    public int Ready => Items.Count (x => x.CanRun);
    public int Skipped => Items.Count - Ready;
}

public static class BatchPlanning
{
    public static string Key (Package package) => $"{package.Source}\u001f{package.Id}";

    public static BatchPlan ForUpgrades (IEnumerable<Package> selected, bool pinsFresh)
    {
        List<BatchPlanItem> items = [];
        foreach (Package package in selected)
        {
            string? skip = string.IsNullOrWhiteSpace (package.Id) || package.IsTruncated
                ? "Package ID is unavailable."
                : !pinsFresh
                    ? "Pin status could not be verified. Refresh before upgrading."
                    : package.PinState.IsPinned
                        ? $"Pinned: {package.PinState.DisplayLabel ()}. Unpin to upgrade."
                        : string.IsNullOrWhiteSpace (package.AvailableVersion)
                            ? "No available version was reported."
                            : null;
            items.Add (new (package, skip is null, skip ?? $"{package.Version} → {package.AvailableVersion}"));
        }
        return new ("Upgrade", items);
    }

    public static BatchPlan ForInstalls (
        IEnumerable<Package> selected,
        IReadOnlyList<Package> installed,
        IReadOnlyList<string> sources)
    {
        HashSet<string> installedKeys = new (installed.Select (Key), StringComparer.OrdinalIgnoreCase);
        HashSet<string> installedIds = new (installed.Select (x => x.Id), StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownSources = new (sources, StringComparer.OrdinalIgnoreCase);
        List<BatchPlanItem> items = [];
        foreach (Package package in selected)
        {
            string? skip = string.IsNullOrWhiteSpace (package.Id) || package.IsTruncated
                ? "Package ID is unavailable."
                : string.IsNullOrWhiteSpace (package.Source) || !knownSources.Contains (package.Source)
                    ? "Source is unavailable. Refresh sources and try again."
                    : installedKeys.Contains (Key (package)) || installedIds.Contains (package.Id)
                        ? "Already installed. Use Upgrades for a newer version."
                        : string.IsNullOrWhiteSpace (package.Version)
                            ? "Package has not been resolved in the catalog."
                            : null;
            items.Add (new (package, skip is null, skip ?? $"Install {package.Version} from {package.Source}"));
        }
        return new ("Install", items);
    }
}
