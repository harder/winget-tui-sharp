using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WinGetScout;

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
    public const string TaskName = "WinGet Scout Daily Update Check";

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
        return await DeleteIfPresentAsync (TaskName, ct);
    }

    private static async Task<string?> DeleteIfPresentAsync (string name, CancellationToken ct)
    {
        if (await RunAsync (["/Query", "/TN", name], ct) is not null) return null;
        return await RunAsync (["/Delete", "/F", "/TN", name], ct);
    }

    private static async Task<string?> RunAsync (IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using Process process = new ();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource (ct);
        timeout.CancelAfter (TimeSpan.FromSeconds (30));
        CancellationToken token = timeout.Token;
        process.StartInfo = new ProcessStartInfo ("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add (argument);
        process.Start ();
        Task<string> output = process.StandardOutput.ReadToEndAsync (token);
        Task<string> error = process.StandardError.ReadToEndAsync (token);
        try { await process.WaitForExitAsync (token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill (entireProcessTree: true); }
            catch (Exception) { }
            return ct.IsCancellationRequested ? throw new OperationCanceledException (ct) : "Task Scheduler timed out.";
        }
        string message = ((await error) + " " + (await output)).Trim ();
        return process.ExitCode == 0 ? null : (message.Length == 0 ? $"Task Scheduler exited with code {process.ExitCode}." : message);
    }
}

public static class ScheduleWorkflow
{
    public static async Task<string?> ApplyAsync (
        WorkflowStore store, UpdateCheckSettings desired,
        Func<CancellationToken, Task<string?>> changeTask, CancellationToken ct)
    {
        UpdateCheckSettings previous = store.Schedule ();
        store.SaveSchedule (desired);
        string? error;
        try { error = await changeTask (ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            store.SaveSchedule (previous);
            throw;
        }
        catch (Exception ex) { error = ex.Message; }
        if (error is null) return null;
        try { store.SaveSchedule (previous); }
        catch (Exception ex) { error += $" Settings could not be restored: {ex.Message}"; }
        return error;
    }
}

public static class UpdateNotification
{
    public static Task<string?> RegisterAsync (CancellationToken ct) => RunScriptAsync (null, ct);

    public static async Task<string?> TryShowAsync (UpdateCheckSnapshot snapshot, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows ()) return null;
        string? message = snapshot.Status == "Failed"
            ? "Scheduled update check failed. Open WinGet Scout for details."
            : snapshot.NewOrChanged > 0
                ? $"{snapshot.NewOrChanged} new or changed upgrade(s) are ready for review."
                : null;
        if (message is null) return null;
        return await RunScriptAsync (message, ct);
    }

    private static async Task<string?> RunScriptAsync (string? message, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows ()) return "Windows notifications are required.";
        string? executable = UpdateTaskScheduler.ExecutablePath ();
        if (executable is null) return "A published executable is required for notifications.";
        try
        {
            using Stream? resource = typeof (UpdateNotification).Assembly.GetManifestResourceStream ("WinGetScout.notification.ps1");
            if (resource is null) return "Notification script is missing.";
            using StreamReader reader = new (resource);
            string encodedScript = Convert.ToBase64String (Encoding.Unicode.GetBytes (await reader.ReadToEndAsync (ct)));
            ProcessStartInfo start = new ("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedScript }
            };
            start.Environment.Remove ("WGS_NOTIFICATION_SHORTCUT");
            start.Environment["WGS_NOTIFICATION_EXE"] = executable;
            start.Environment["WGS_NOTIFICATION_MESSAGE"] = message ?? string.Empty;
            using Process process = Process.Start (start) ?? throw new InvalidOperationException ("PowerShell did not start.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource (ct);
            timeout.CancelAfter (TimeSpan.FromSeconds (15));
            Task<string> stderr = process.StandardError.ReadToEndAsync (timeout.Token);
            try { await process.WaitForExitAsync (timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill (entireProcessTree: true); }
                catch (Exception) { }
                if (ct.IsCancellationRequested) throw;
                return "Notification delivery timed out.";
            }
            string error = (await stderr).Trim ();
            return process.ExitCode == 0 ? null : error.Length > 0 ? error : $"Notification process exited with code {process.ExitCode}.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ex.Message;
        }
    }
}
