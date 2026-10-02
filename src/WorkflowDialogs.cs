namespace WingetTuiSharp;

public sealed class BatchPlanDialog : Runnable<bool>
{
    public BatchPlanDialog (BatchPlan plan)
    {
        Title = $" Review {plan.Action.ToLowerInvariant ()} plan ";
        BorderStyle = LineStyle.Rounded;
        Width = Dim.Percent (78);
        Height = Dim.Percent (65);
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;

        Label summary = new () { X = 1, Y = 0, Width = Dim.Fill (1), Text = $"{plan.Ready} ready · {plan.Skipped} skipped  —  review each package before continuing" };
        ListView rows = new () { X = 1, Y = 2, Width = Dim.Fill (1), Height = Dim.Fill (3), SchemeName = Theme.SurfaceSchemeName };
        rows.SetSource (new ObservableCollection<string> (plan.Items.Select (x =>
            $"{(x.CanRun ? "READY" : "SKIP ")}  {x.Package.Name} [{x.Package.Id}]  —  {x.Reason}").ToList ()));
        Button run = new () { X = Pos.Center () - 8, Y = Pos.AnchorEnd (1), Text = $"_{plan.Action}", IsDefault = true, Enabled = plan.Ready > 0 };
        Button cancel = new () { X = Pos.Center () + 3, Y = Pos.AnchorEnd (1), Text = "Cancel" };
        run.Accepting += (_, e) => { Result = true; RequestStop (); e.Handled = true; };
        cancel.Accepting += (_, e) => { Result = false; RequestStop (); e.Handled = true; };
        Add (summary, rows, run, cancel);
        rows.SetFocus ();
    }
}

public sealed class RunRecordDialog : Runnable
{
    public RunRecordDialog (RunRecord record)
    {
        Title = $" {record.Action} results ";
        BorderStyle = LineStyle.Rounded;
        Width = Dim.Percent (78);
        Height = Dim.Percent (65);
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Label summary = new () { X = 1, Y = 0, Width = Dim.Fill (1), Text = record.Summary };
        ListView rows = new () { X = 1, Y = 2, Width = Dim.Fill (1), Height = Dim.Fill (3), SchemeName = Theme.SurfaceSchemeName };
        rows.SetSource (new ObservableCollection<string> (record.Items.Select (x =>
            $"{x.Status,-9} {x.Name} [{x.Id}]  —  {x.Reason}").ToList ()));
        Button close = new () { X = Pos.Center (), Y = Pos.AnchorEnd (1), Text = "_Close", IsDefault = true };
        close.Accepting += (_, e) => { RequestStop (); e.Handled = true; };
        Add (summary, rows, close);
        rows.SetFocus ();
    }
}

public sealed class TextPromptDialog : Runnable<string?>
{
    public TextPromptDialog (string title, string prompt)
    {
        Title = $" {title} ";
        BorderStyle = LineStyle.Rounded;
        Width = 64;
        Height = 8;
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Label label = new () { X = 1, Y = 0, Text = prompt };
        TextField input = new () { X = 1, Y = 2, Width = Dim.Fill (1) };
        Button save = new () { X = Pos.Center () - 8, Y = Pos.AnchorEnd (1), Text = "_Save", IsDefault = true };
        Button cancel = new () { X = Pos.Center () + 2, Y = Pos.AnchorEnd (1), Text = "Cancel" };
        save.Accepting += (_, e) => { Result = input.Text?.Trim (); RequestStop (); e.Handled = true; };
        cancel.Accepting += (_, e) => { Result = null; RequestStop (); e.Handled = true; };
        Add (label, input, save, cancel);
        input.SetFocus ();
    }
}

public sealed record SetManagerChoice (string Action, string? Name);

public sealed class SetManagerDialog : Runnable<SetManagerChoice?>
{
    public SetManagerDialog (IReadOnlyList<PackageSet> sets, int selectedCount)
    {
        Title = " Saved package sets ";
        BorderStyle = LineStyle.Rounded;
        Width = Dim.Percent (65);
        Height = Math.Clamp (sets.Count + 8, 11, 21);
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Label hint = new () { X = 1, Y = 0, Width = Dim.Fill (1), Text = $"{selectedCount} currently selected · choose a set or save this selection" };
        ListView rows = new () { X = 1, Y = 2, Width = Dim.Fill (1), Height = Dim.Fill (3), SchemeName = Theme.SurfaceSchemeName };
        rows.SetSource (new ObservableCollection<string> (sets.Select (x => $"{x.Name} ({x.Packages.Count})").ToList ()));
        Button save = new () { X = 1, Y = Pos.AnchorEnd (1), Text = "_Save current", Enabled = selectedCount > 0 };
        Button load = new () { X = 19, Y = Pos.AnchorEnd (1), Text = "_Load", Enabled = sets.Count > 0 };
        Button delete = new () { X = 30, Y = Pos.AnchorEnd (1), Text = "_Delete", Enabled = sets.Count > 0 };
        Button close = new () { X = 43, Y = Pos.AnchorEnd (1), Text = "Close" };
        void Select (string action)
        {
            int index = rows.SelectedItem ?? -1;
            Result = new (action, index >= 0 && index < sets.Count ? sets [index].Name : null);
            RequestStop ();
        }
        save.Accepting += (_, e) => { Select ("Save"); e.Handled = true; };
        load.Accepting += (_, e) => { Select ("Load"); e.Handled = true; };
        delete.Accepting += (_, e) => { Select ("Delete"); e.Handled = true; };
        close.Accepting += (_, e) => { RequestStop (); e.Handled = true; };
        Add (hint, rows, save, load, delete, close);
        (sets.Count > 0 ? (View)rows : save).SetFocus ();
    }
}

public sealed class RunHistoryDialog : Runnable<int?>
{
    public RunHistoryDialog (IReadOnlyList<RunRecord> runs)
    {
        Title = " Recent runs ";
        BorderStyle = LineStyle.Rounded;
        Width = Dim.Percent (70);
        Height = Math.Clamp (runs.Count + 6, 9, 22);
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        ListView rows = new () { X = 1, Y = 1, Width = Dim.Fill (1), Height = Dim.Fill (3), SchemeName = Theme.SurfaceSchemeName };
        rows.SetSource (new ObservableCollection<string> (runs.Select (x =>
            $"{x.FinishedAtUtc.ToLocalTime ():g}  {x.Action,-10} {x.Summary}").ToList ()));
        Button open = new () { X = Pos.Center () - 7, Y = Pos.AnchorEnd (1), Text = "_Open", IsDefault = true };
        Button close = new () { X = Pos.Center () + 2, Y = Pos.AnchorEnd (1), Text = "Close" };
        open.Accepting += (_, e) => { Result = rows.SelectedItem; RequestStop (); e.Handled = true; };
        close.Accepting += (_, e) => { Result = null; RequestStop (); e.Handled = true; };
        Add (rows, open, close);
        rows.SetFocus ();
    }
}

public sealed record ScheduleChoice (string Action, string DailyAt, bool NotifyOnChange);

public sealed class ScheduleDialog : Runnable<ScheduleChoice?>
{
    public ScheduleDialog (UpdateCheckSettings settings, UpdateCheckSnapshot? latest)
    {
        Title = " Scheduled update checks ";
        BorderStyle = LineStyle.Rounded;
        Width = 72;
        Height = 13;
        X = Pos.Center ();
        Y = Pos.Center ();
        SchemeName = Theme.SurfaceSchemeName;
        Label state = new () { X = 1, Y = 0, Width = Dim.Fill (1), Text = settings.Enabled ? "Daily checks enabled" : "Daily checks disabled" };
        Label last = new () { X = 1, Y = 1, Width = Dim.Fill (1), Text = latest is null
            ? "No check has run yet."
            : latest.Status == "Succeeded"
                ? $"Last check: {latest.CheckedAtUtc.ToLocalTime ():g} · {latest.Actionable} available · {latest.NewOrChanged} new"
                : $"Last check failed: {latest.Error}" };
        Label timeLabel = new () { X = 1, Y = 3, Text = "Daily time (HH:mm):" };
        TextField time = new () { X = 21, Y = 3, Width = 8, Text = settings.DailyAt };
        Label notifyLabel = new () { X = 1, Y = 5, Text = "Notify on changes or failures:" };
        OptionSelector notify = new () { X = 32, Y = 5, Width = 20, Labels = ["Yes", "No"] };
        notify.Value = settings.NotifyOnChange ? 0 : 1;
        Label error = new () { X = 1, Y = 7, Width = Dim.Fill (1), Text = string.Empty, SchemeName = Theme.DangerSchemeName };
        Button save = new () { X = 1, Y = Pos.AnchorEnd (1), Text = "_Enable / save", IsDefault = true };
        Button disable = new () { X = 21, Y = Pos.AnchorEnd (1), Text = "_Disable" };
        Button check = new () { X = 35, Y = Pos.AnchorEnd (1), Text = "Check _now" };
        Button close = new () { X = 54, Y = Pos.AnchorEnd (1), Text = "Close" };
        save.Accepting += (_, e) =>
        {
            if (!UpdateChecks.TryParseDailyTime (time.Text ?? string.Empty, out TimeOnly ignoredTime))
            {
                error.Text = "Enter a 24-hour time, for example 09:00.";
            }
            else
            {
                Result = new ("Save", time.Text!, notify.Value == 0);
                RequestStop ();
            }
            e.Handled = true;
        };
        disable.Accepting += (_, e) => { Result = new ("Disable", settings.DailyAt, settings.NotifyOnChange); RequestStop (); e.Handled = true; };
        check.Accepting += (_, e) => { Result = new ("Check", settings.DailyAt, settings.NotifyOnChange); RequestStop (); e.Handled = true; };
        close.Accepting += (_, e) => { RequestStop (); e.Handled = true; };
        Add (state, last, timeLabel, time, notifyLabel, notify, error, save, disable, check, close);
        time.SetFocus ();
    }
}
