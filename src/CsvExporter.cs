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

        for (int index = 0;
             index < packages.Count && rows.Count < MaxRows && retainedCharacters < MaxSnapshotCharacters;
             index++)
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

            for (int cell = 0; cell < values.Length; cell++)
            {
                string value = values [cell];
                int allowed = Math.Min (MaxCellCharacters, MaxSnapshotCharacters - retainedCharacters);

                if (value.Length > allowed)
                {
                    values [cell] = value [..allowed];
                    truncatedCells++;
                }

                retainedCharacters += values [cell].Length;
            }

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
        finally
        {
            if (!committed && File.Exists (tempPath))
            {
                File.Delete (tempPath);
            }
        }
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
