using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WingetTuiSharp;

public static class UpdateChecks
{
    public static bool TryParseDailyTime (string value, out TimeOnly time) =>
        TimeOnly.TryParseExact (value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static async Task<UpdateCheckSnapshot> CheckAsync (IBackend backend, WorkflowStore store, CancellationToken ct)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        string description = string.Empty;

        try
        {
            description = await backend.DescribeAsync (ct);
            IReadOnlyList<Package> packages;
            IReadOnlyDictionary<string, PinState> pins;
            if (backend is CliBackend cli)
            {
                (packages, pins) = await cli.ReadUpdateCheckAsync (ct);
            }
            else
            {
                packages = await backend.ListUpgradesAsync (null, ct);
                pins = await backend.ListPinsAsync (ct);
            }
            UpdateCheckSnapshot? previous = store.LastSuccessfulCheck ();
            HashSet<string> old = new (StringComparer.OrdinalIgnoreCase);
            if (previous is not null)
            {
                foreach (UpdateItem item in previous.Updates.Where (x => !x.Pinned))
                {
                    old.Add (Key (item.Id, item.Source, item.AvailableVersion));
                }
            }

            List<UpdateItem> updates = [];
            int changed = 0;
            foreach (Package package in packages)
            {
                ct.ThrowIfCancellationRequested ();
                bool pinned = pins.TryGetValue (package.Id, out PinState state) && state.IsPinned;
                UpdateItem item = new (package.Id, package.Name, package.Source, package.Version,
                    package.AvailableVersion ?? string.Empty, pinned);
                updates.Add (item);
                if (!pinned && previous is not null && !old.Contains (Key (item.Id, item.Source, item.AvailableVersion))) changed++;
            }

            // The first successful check establishes a baseline rather than alerting on every
            // pre-existing upgrade. Later checks report only new packages or changed versions.
            UpdateCheckSnapshot snapshot = new (checkedAt, "Succeeded", null, description, updates, changed);
            store.SaveCheck (snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            UpdateCheckSnapshot failed = new (checkedAt, "Failed", ex.Message, description, [], 0);
            store.SaveCheck (failed);
            return failed;
        }
    }

    private static string Key (string id, string source, string version) => $"{source}\u001f{id}\u001f{version}";
}

public static class UpdateTaskScheduler
{
    public const string TaskName = "winget-tui-sharp Daily Update Check";

    public static string? ExecutablePath ()
    {
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return null;
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty (path) || !File.Exists (path)) return null;
        string name = Path.GetFileNameWithoutExtension (path);
        return name.Equals ("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    public static async Task<string?> RegisterAsync (string executablePath, string time, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows ()) return "Windows Task Scheduler is required.";
        if (!UpdateChecks.TryParseDailyTime (time, out _)) return "Enter a time in 24-hour HH:mm format.";
        if (!File.Exists (executablePath)) return "The executable is missing. Publish the app before scheduling checks.";

        string taskRun = $"\"{executablePath}\" --check-updates";
        return await RunAsync (["/Create", "/F", "/SC", "DAILY", "/ST", time, "/TN", TaskName,
            "/TR", taskRun, "/IT", "/RL", "LIMITED"], ct);
    }

    public static async Task<string?> UnregisterAsync (CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows ()) return "Windows Task Scheduler is required.";
        if (!await ExistsAsync (ct)) return null;
        return await RunAsync (["/Delete", "/F", "/TN", TaskName], ct);
    }

    public static async Task<bool> ExistsAsync (CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows ()) return false;
        return await RunAsync (["/Query", "/TN", TaskName], ct) is null;
    }

    private static async Task<string?> RunAsync (IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using Process process = new ();
        process.StartInfo = new ProcessStartInfo ("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add (argument);
        process.Start ();
        Task<string> output = process.StandardOutput.ReadToEndAsync (ct);
        Task<string> error = process.StandardError.ReadToEndAsync (ct);
        await process.WaitForExitAsync (ct);
        string message = ((await error) + " " + (await output)).Trim ();
        return process.ExitCode == 0 ? null : (message.Length == 0 ? $"Task Scheduler exited with code {process.ExitCode}." : message);
    }
}

public static class UpdateNotification
{
    public static async Task TryShowAsync (UpdateCheckSnapshot snapshot, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows ()) return;
        string? message = snapshot.Status == "Failed"
            ? "Scheduled update check failed. Open WinGet TUI for details."
            : snapshot.NewOrChanged > 0
                ? $"{snapshot.NewOrChanged} new or changed upgrade(s) are ready for review."
                : null;
        if (message is null) return;

        // Use the Windows notification API from the scheduled user's session. Notification
        // delivery is best effort; the persisted check result remains authoritative.
        string encodedMessage = Convert.ToBase64String (Encoding.UTF8.GetBytes (message));
        string script = "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] | Out-Null;" +
                        "[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime] | Out-Null;" +
                        $"$m=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedMessage}'));" +
                        "$m=[Security.SecurityElement]::Escape($m);" +
                        "$x=[Windows.Data.Xml.Dom.XmlDocument]::new();" +
                        "$x.LoadXml(\"<toast><visual><binding template='ToastGeneric'><text>WinGet TUI</text><text>$m</text></binding></visual></toast>\");" +
                        "$t=[Windows.UI.Notifications.ToastNotification]::new($x);" +
                        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('WinGet TUI').Show($t)";
        string encodedScript = Convert.ToBase64String (Encoding.Unicode.GetBytes (script));
        try
        {
            using Process process = Process.Start (new ProcessStartInfo ("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedScript }
            })!;
            await process.WaitForExitAsync (ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A notification failure must not turn a successful inventory check into a failure.
        }
    }
}
