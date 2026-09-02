using System.Text;

namespace WingetTuiSharp;

internal static class CsvExporter
{
    // Installed-package lists can be larger than search results, but export preparation should
    // remain predictably small even with a malformed backend. The character ceiling bounds the
    // dominant string payload to roughly 4 MiB; the row/cell limits bound object and line sizes.
    internal const int MaxRows = 10_000;
    internal const int MaxCellCharacters = 16 * 1024;
    internal const int MaxSnapshotCharacters = 2 * 1024 * 1024;

    internal static CsvSnapshot CreateSnapshot (IReadOnlyList<Package> packages)
    {
        ArgumentNullException.ThrowIfNull (packages);

        List<CsvRow> rows = new (Math.Min (packages.Count, MaxRows));
        int retainedCharacters = 0;
        int truncatedCells = 0;

        for (int index = 0; index < packages.Count && rows.Count < MaxRows; index++)
        {
            Package package = packages [index];
            string [] values =
            [
                package.Name,
                package.Id,
                package.Version,
                package.AvailableVersion ?? string.Empty,
                package.Source
            ];
            bool [] cellWasTruncated = new bool [values.Length];
            int rowCharacters = 0;

            for (int cell = 0; cell < values.Length; cell++)
            {
                values [cell] = TakeUtf16Prefix (values [cell], MaxCellCharacters, out cellWasTruncated [cell]);
                rowCharacters += values [cell].Length;
            }

            // Retain complete rows only. Emptying the tail of the last row to fit the aggregate
            // budget can erase its identity and produce a misleading CSV record.
            if (rowCharacters > MaxSnapshotCharacters - retainedCharacters)
            {
                break;
            }

            truncatedCells += cellWasTruncated.Count (truncated => truncated);
            retainedCharacters += rowCharacters;
            rows.Add (new (values [0], values [1], values [2], values [3], values [4]));
        }

        return new (
            rows.ToArray (),
            packages.Count,
            packages.Count - rows.Count,
            truncatedCells,
            retainedCharacters);
    }

    internal static async Task WriteAtomicAsync (
        string destinationPath,
        CsvSnapshot snapshot,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace (destinationPath);
        ArgumentNullException.ThrowIfNull (snapshot);

        string destination = Path.GetFullPath (destinationPath);
        string directory = Path.GetDirectoryName (destination)!;
        string fileName = Path.GetFileName (destination);
        string tempPath = Path.Combine (directory, $".{fileName}.{Guid.NewGuid ():N}.tmp");
        bool committed = false;
        Exception? primaryFailure = null;

        try
        {
            await using (FileStream stream = new (
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (StreamWriter writer = new (stream, new UTF8Encoding (encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteLineAsync ("Name,Id,Version,Available,Source".AsMemory (), cancellationToken);

                foreach (CsvRow row in snapshot.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested ();
                    string line = string.Join (',',
                                               Quote (row.Name),
                                               Quote (row.Id),
                                               Quote (row.Version),
                                               Quote (row.AvailableVersion),
                                               Quote (row.Source));
                    await writer.WriteLineAsync (line.AsMemory (), cancellationToken);
                }

                await writer.FlushAsync (cancellationToken);
                // Flush the temp file's contents before the atomic replacement. The rename is
                // atomic for concurrent readers; parent-directory metadata is not fsync'd, so this
                // does not claim full crash durability across sudden power loss.
                stream.Flush (flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested ();

            if (beforeCommit is not null)
            {
                await beforeCommit (cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested ();
            File.Move (tempPath, destination, overwrite: true);
            committed = true;
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            if (!committed && File.Exists (tempPath))
            {
                try
                {
                    File.Delete (tempPath);
                }
                catch when (primaryFailure is not null)
                {
                    // Preserve the cancellation/write exception that caused cleanup. When no
                    // primary failure exists, surface cleanup failure instead of hiding a temp.
                }
            }
        }
    }

    private static string TakeUtf16Prefix (string value, int allowedCharacters, out bool truncated)
    {
        int length = Math.Min (value.Length, allowedCharacters);

        if (length > 0
            && length < value.Length
            && char.IsHighSurrogate (value [length - 1])
            && char.IsLowSurrogate (value [length]))
        {
            length--;
        }

        truncated = length < value.Length;

        return truncated ? value [..length] : value;
    }

    internal static string EscapeCell (string value)
    {
        string escaped = LooksLikeCsvFormula (value) ? "'" + value : value;

        return escaped.Replace ("\"", "\"\"");
    }

    private static string Quote (string value) => $"\"{EscapeCell (value)}\"";

    private static bool LooksLikeCsvFormula (string value)
    {
        if (string.IsNullOrEmpty (value))
        {
            return false;
        }

        string trimmed = value.TrimStart ();

        return trimmed.Length > 0 && trimmed [0] is '=' or '+' or '-' or '@';
    }
}

internal sealed record CsvSnapshot (
    IReadOnlyList<CsvRow> Rows,
    int SourceRowCount,
    int OmittedRowCount,
    int TruncatedCellCount,
    int RetainedCharacters)
{
    internal bool WasTruncated => OmittedRowCount > 0 || TruncatedCellCount > 0;
}

internal sealed record CsvRow (string Name, string Id, string Version, string AvailableVersion, string Source);

/// <summary>
/// Owns export single-flight identity, linked cancellation, and its loading lease independently
/// from Terminal.Gui so lifecycle transitions can be exercised deterministically.
/// </summary>
internal sealed class ExportWorkflowState : IDisposable
{
    internal const string AdmissionRejectedMessage =
        "Too many background requests are still pending; export was not started";

    private readonly object _gate = new ();
    private ExportOperation? _active;
    private bool _disposed;

    internal bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    internal bool TryBegin (
        CancellationToken lifetimeToken,
        string activity,
        Func<IDisposable> acquireLoading,
        out ExportOperation operation)
    {
        ArgumentNullException.ThrowIfNull (activity);
        ArgumentNullException.ThrowIfNull (acquireLoading);

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource (lifetimeToken);
        IDisposable loading;

        try
        {
            loading = acquireLoading ();
        }
        catch
        {
            cancellation.Dispose ();
            throw;
        }

        ExportOperation candidate = new (cancellation, loading, activity);

        bool accepted;

        lock (_gate)
        {
            accepted = !_disposed && _active is null && !lifetimeToken.IsCancellationRequested;

            if (accepted)
            {
                _active = candidate;
            }
        }

        if (!accepted)
        {
            // Loading leases are supplied by the caller and may run arbitrary disposal code.
            // Keep that callback outside the coordinator lock.
            candidate.Dispose ();
            operation = null!;

            return false;
        }

        operation = candidate;

        return true;
    }

    internal bool IsCurrent (ExportOperation operation)
    {
        lock (_gate)
        {
            return ReferenceEquals (_active, operation);
        }
    }

    internal ExportCompletion Complete (ExportOperation operation, string currentStatus)
    {
        bool wasCurrent;

        lock (_gate)
        {
            wasCurrent = ReferenceEquals (_active, operation);

            if (wasCurrent)
            {
                _active = null;
            }
        }

        if (!wasCurrent)
        {
            return new (WasCurrent: false, OwnedStatus: false);
        }

        operation.Dispose ();

        return new (
            WasCurrent: true,
            OwnedStatus: string.Equals (currentStatus, operation.Activity, StringComparison.Ordinal));
    }

    internal bool RejectAdmission (ExportOperation operation, out string message)
    {
        ExportCompletion completion = Complete (operation, operation.Activity);
        message = AdmissionRejectedMessage;

        return completion.WasCurrent;
    }

    internal void Release (ExportOperation operation) => Complete (operation, currentStatus: string.Empty);

    internal void CancelActive ()
    {
        ExportOperation? active;

        lock (_gate)
        {
            active = _active;
        }

        active?.Cancel ();
    }

    public void Dispose ()
    {
        ExportOperation? active;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            active = _active;
            _active = null;
        }

        active?.Cancel ();
        active?.Dispose ();
    }
}

internal sealed class ExportOperation : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private IDisposable? _loading;

    internal ExportOperation (CancellationTokenSource cancellation, IDisposable loading, string activity)
    {
        _cancellation = cancellation;
        _loading = loading;
        Activity = activity;
    }

    internal string Activity { get; }
    internal CancellationToken Token => _cancellation?.Token ?? new (canceled: true);

    internal void Cancel ()
    {
        try
        {
            _cancellation?.Cancel ();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race and has already released the request.
        }
    }

    public void Dispose ()
    {
        IDisposable? loading = Interlocked.Exchange (ref _loading, null);
        CancellationTokenSource? cancellation = Interlocked.Exchange (ref _cancellation, null);

        try
        {
            loading?.Dispose ();
        }
        finally
        {
            cancellation?.Dispose ();
        }
    }
}

internal readonly record struct ExportCompletion (bool WasCurrent, bool OwnedStatus);
