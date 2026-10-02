using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace WingetTuiSharp;

public sealed record PackageChoice (string Id, string Name, string Source);
public sealed record PackageSet (string Name, List<PackageChoice> Packages);
public sealed record PackageSetFile (List<PackageSet> Sets);

public sealed record RunItem (string Id, string Name, string Source, string Status, string Reason);
public sealed record RunRecord (DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc, string Action, List<RunItem> Items)
{
    public static RunRecord SinglePackage (
        Package package, OperationKind kind, DateTimeOffset startedAtUtc, DateTimeOffset finishedAtUtc,
        OpResult? result, bool cancelled)
    {
        if (!cancelled && result is null) throw new ArgumentNullException (nameof (result));
        RunItem item = new (package.Id, package.Name, package.Source,
            cancelled ? "Skipped" : result!.Success ? "Succeeded" : "Failed",
            cancelled ? "Cancelled." : CompactReason (result!.Message));
        return new (startedAtUtc, finishedAtUtc, kind.ToString (), [item]);
    }

    public static string CompactReason (string? message)
    {
        if (string.IsNullOrWhiteSpace (message)) return "Completed.";
        StringBuilder compact = new ();
        bool pendingSpace = false;
        for (int i = 0; i < message.Length; i++)
        {
            char ch = message [i];
            if (ch == '\u001b' && i + 1 < message.Length && message [i + 1] == '[')
            {
                i += 2;
                while (i < message.Length && message [i] is < '@' or > '~') i++;
                continue;
            }
            if (char.IsWhiteSpace (ch))
            {
                pendingSpace = compact.Length > 0;
                continue;
            }
            if (char.IsControl (ch)) continue;
            if (pendingSpace) compact.Append (' ');
            pendingSpace = false;
            compact.Append (ch);
            if (compact.Length >= 320) break;
        }
        return compact.Length == 0 ? "Completed." : StatusOwnership.TruncateScalarSafe (compact.ToString (), 300);
    }

    public int Succeeded => Items.Count (x => x.Status == "Succeeded");
    public int Skipped => Items.Count (x => x.Status == "Skipped");
    public int Failed => Items.Count (x => x.Status == "Failed");
    public string Summary => $"{Succeeded} succeeded · {Skipped} skipped · {Failed} failed";
    public (string Message, bool IsError) Completion (string? historyError) => historyError is null
        ? (Summary, Failed > 0)
        : ($"{Summary} · history save failed: {historyError}", true);
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

    public UpdateCheckSettings Schedule ()
    {
        UpdateCheckSettings? settings = ReadStrict (PathFor ("schedule.json"), WorkflowJsonContext.Default.UpdateCheckSettings);
        if (settings is null) return new (false, "09:00", true);
        if (settings.DailyAt is null || !UpdateChecks.TryParseDailyTime (settings.DailyAt, out _))
        {
            throw new InvalidDataException ("Schedule settings are invalid.");
        }
        return settings;
    }

    public void SaveSchedule (UpdateCheckSettings value) => Write (PathFor ("schedule.json"), value, WorkflowJsonContext.Default.UpdateCheckSettings);

    public UpdateCheckSnapshot? LatestCheck () => Read (PathFor ("latest-check.json"), WorkflowJsonContext.Default.UpdateCheckSnapshot);
    public UpdateCheckSnapshot? LastSuccessfulCheck () => ReadStrict (PathFor ("last-successful-check.json"), WorkflowJsonContext.Default.UpdateCheckSnapshot);

    public void SaveCheck (UpdateCheckSnapshot value)
    {
        Write (PathFor ("latest-check.json"), value, WorkflowJsonContext.Default.UpdateCheckSnapshot);
        if (value.Status == "Succeeded")
        {
            Write (PathFor ("last-successful-check.json"), value, WorkflowJsonContext.Default.UpdateCheckSnapshot);
        }
    }

    public IReadOnlyList<PackageSet> Sets ()
    {
        PackageSetFile? file = ReadStrict (PathFor ("sets.json"), WorkflowJsonContext.Default.PackageSetFile);
        if (file is null) return [];
        if (file.Sets is null || file.Sets.Any (set => set is null
            || string.IsNullOrWhiteSpace (set.Name)
            || set.Packages is null
            || set.Packages.Any (item => item is null || string.IsNullOrWhiteSpace (item.Id)
                || item.Name is null || item.Source is null)))
        {
            throw new InvalidDataException ("Saved sets are invalid.");
        }
        return file.Sets;
    }

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

    public IReadOnlyList<RunRecord> Runs ()
    {
        RunHistory? file = ReadStrict (PathFor ("runs.json"), WorkflowJsonContext.Default.RunHistory);
        if (file is null) return [];
        if (file.Runs is null || file.Runs.Any (run => run is null
            || string.IsNullOrWhiteSpace (run.Action)
            || run.Items is null
            || run.Items.Any (item => item is null || item.Id is null || item.Name is null
                || item.Source is null || item.Status is null || item.Reason is null)))
        {
            throw new InvalidDataException ("Run history is invalid.");
        }
        return file.Runs;
    }

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

    private static T? ReadStrict<T> (string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (!File.Exists (path)) return default;
        T? value = JsonSerializer.Deserialize (File.ReadAllText (path), type);
        return value ?? throw new InvalidDataException ($"Could not read {Path.GetFileName (path)}.");
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
