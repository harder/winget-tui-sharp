using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingetTuiSharp;

public sealed record PackageChoice (string Id, string Name, string Source);
public sealed record PackageSet (string Name, List<PackageChoice> Packages);
public sealed record PackageSetFile (List<PackageSet> Sets);

public sealed record RunItem (string Id, string Name, string Source, string Status, string Reason);
public sealed record RunRecord (DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc, string Action, List<RunItem> Items)
{
    public int Succeeded => Items.Count (x => x.Status == "Succeeded");
    public int Skipped => Items.Count (x => x.Status == "Skipped");
    public int Failed => Items.Count (x => x.Status == "Failed");
    public string Summary => $"{Succeeded} succeeded · {Skipped} skipped · {Failed} failed";
}
public sealed record RunHistory (List<RunRecord> Runs);

public sealed record UpdateCheckSettings (bool Enabled, string DailyAt, bool NotifyOnChange);
public sealed record UpdateItem (string Id, string Name, string Source, string InstalledVersion, string AvailableVersion, bool Pinned);
public sealed record UpdateCheckSnapshot (
    DateTimeOffset CheckedAtUtc,
    string Status,
    string? Error,
    string Backend,
    List<UpdateItem> Updates,
    int NewOrChanged)
{
    public int Actionable => Updates.Count (x => !x.Pinned);
    public int Pinned => Updates.Count (x => x.Pinned);
}

[JsonSourceGenerationOptions (WriteIndented = true)]
[JsonSerializable (typeof (PackageSetFile))]
[JsonSerializable (typeof (RunHistory))]
[JsonSerializable (typeof (UpdateCheckSettings))]
[JsonSerializable (typeof (UpdateCheckSnapshot))]
internal partial class WorkflowJsonContext : JsonSerializerContext;

/// <summary>Small local state files; writes replace complete JSON documents atomically.</summary>
public sealed class WorkflowStore (string? root = null)
{
    public string Root { get; } = root ?? Path.Combine (
        Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "WinGetTuiSharp");

    private string PathFor (string file) => Path.Combine (Root, file);

    public UpdateCheckSettings Schedule () => Read (PathFor ("schedule.json"), WorkflowJsonContext.Default.UpdateCheckSettings)
        ?? new (false, "09:00", true);

    public void SaveSchedule (UpdateCheckSettings value) => Write (PathFor ("schedule.json"), value, WorkflowJsonContext.Default.UpdateCheckSettings);

    public UpdateCheckSnapshot? LatestCheck () => Read (PathFor ("latest-check.json"), WorkflowJsonContext.Default.UpdateCheckSnapshot);
    public UpdateCheckSnapshot? LastSuccessfulCheck () => Read (PathFor ("last-successful-check.json"), WorkflowJsonContext.Default.UpdateCheckSnapshot);

    public void SaveCheck (UpdateCheckSnapshot value)
    {
        Write (PathFor ("latest-check.json"), value, WorkflowJsonContext.Default.UpdateCheckSnapshot);
        if (value.Status == "Succeeded")
        {
            Write (PathFor ("last-successful-check.json"), value, WorkflowJsonContext.Default.UpdateCheckSnapshot);
        }
    }

    public IReadOnlyList<PackageSet> Sets () => Read (PathFor ("sets.json"), WorkflowJsonContext.Default.PackageSetFile)?.Sets ?? [];

    public void SaveSet (PackageSet value)
    {
        if (string.IsNullOrWhiteSpace (value.Name) || value.Name.Length > 80 || value.Packages.Count is < 1 or > 100)
        {
            throw new ArgumentException ("A set needs a name and 1–100 packages.");
        }

        List<PackageSet> sets = [.. Sets ().Where (x => !x.Name.Equals (value.Name, StringComparison.OrdinalIgnoreCase))];
        sets.Add (value);
        sets.Sort ((a, b) => StringComparer.OrdinalIgnoreCase.Compare (a.Name, b.Name));
        if (sets.Count > 50) throw new InvalidOperationException ("At most 50 saved sets are supported.");
        Write (PathFor ("sets.json"), new PackageSetFile (sets), WorkflowJsonContext.Default.PackageSetFile);
    }

    public void DeleteSet (string name) => Write (PathFor ("sets.json"),
        new PackageSetFile ([.. Sets ().Where (x => !x.Name.Equals (name, StringComparison.OrdinalIgnoreCase))]),
        WorkflowJsonContext.Default.PackageSetFile);

    public IReadOnlyList<RunRecord> Runs () => Read (PathFor ("runs.json"), WorkflowJsonContext.Default.RunHistory)?.Runs ?? [];

    public void SaveRun (RunRecord value)
    {
        List<RunRecord> runs = [value, .. Runs ().Take (19)];
        Write (PathFor ("runs.json"), new RunHistory (runs), WorkflowJsonContext.Default.RunHistory);
    }

    private static T? Read<T> (string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return File.Exists (path) ? JsonSerializer.Deserialize (File.ReadAllText (path), type) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static void Write<T> (string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory (Path.GetDirectoryName (path)!);
        string temporary = path + "." + Guid.NewGuid ().ToString ("N") + ".tmp";
        try
        {
            File.WriteAllText (temporary, JsonSerializer.Serialize (value, type));
            File.Move (temporary, path, true);
        }
        finally
        {
            if (File.Exists (temporary)) File.Delete (temporary);
        }
    }
}
