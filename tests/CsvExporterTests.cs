namespace WingetTuiSharp.Tests;

public sealed class CsvExporterTests
{
    [Fact]
    public async Task WriteAtomicAsync_EscapesQuotesNewlinesAndFormulaCells ()
    {
        using TempDirectory temp = new ();
        string destination = Path.Combine (temp.Path, "export.csv");
        CsvSnapshot snapshot = CsvExporter.CreateSnapshot (
        [
            Package ("id", "a,\"quoted\"\nname", "=1+1")
        ]);

        await CsvExporter.WriteAtomicAsync (destination, snapshot, TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync (destination, TestContext.Current.CancellationToken);
        Assert.Contains ("\"a,\"\"quoted\"\"\nname\"", content);
        Assert.Contains ("\"'=1+1\"", content);
    }

    [Fact]
    public async Task WriteAtomicAsync_ReplacesDestinationAndLeavesNoTemp ()
    {
        using TempDirectory temp = new ();
        string destination = Path.Combine (temp.Path, "export.csv");
        await File.WriteAllTextAsync (destination, "old", TestContext.Current.CancellationToken);
        CsvSnapshot snapshot = CsvExporter.CreateSnapshot ([Package ("new-id", "new-name")]);

        await CsvExporter.WriteAtomicAsync (destination, snapshot, TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync (destination, TestContext.Current.CancellationToken);
        Assert.DoesNotContain ("old", content);
        Assert.Contains ("new-id", content);
        Assert.Empty (Directory.GetFiles (temp.Path, ".export.csv.*.tmp"));
    }

    [Fact]
    public async Task WriteAtomicAsync_CancellationPreservesDestinationAndDeletesTemp ()
    {
        using TempDirectory temp = new ();
        string destination = Path.Combine (temp.Path, "export.csv");
        await File.WriteAllTextAsync (destination, "original", TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource (
            TestContext.Current.CancellationToken);
        cancellation.Cancel ();

        await Assert.ThrowsAnyAsync<OperationCanceledException> (
            () => CsvExporter.WriteAtomicAsync (
                destination,
                CsvExporter.CreateSnapshot ([Package ("id", "name")]),
                cancellation.Token));

        Assert.Equal ("original", await File.ReadAllTextAsync (destination, TestContext.Current.CancellationToken));
        Assert.Empty (Directory.GetFiles (temp.Path, ".export.csv.*.tmp"));
    }

    [Fact]
    public async Task WriteAtomicAsync_FailureBeforeCommitPreservesDestinationAndDeletesTemp ()
    {
        using TempDirectory temp = new ();
        string destination = Path.Combine (temp.Path, "export.csv");
        await File.WriteAllTextAsync (destination, "original", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException> (
            () => CsvExporter.WriteAtomicAsync (
                destination,
                CsvExporter.CreateSnapshot ([Package ("id", "name")]),
                TestContext.Current.CancellationToken,
                _ => throw new IOException ("injected")));

        Assert.Equal ("original", await File.ReadAllTextAsync (destination, TestContext.Current.CancellationToken));
        Assert.Empty (Directory.GetFiles (temp.Path, ".export.csv.*.tmp"));
    }

    [Fact]
    public void CreateSnapshot_EnforcesRowLimit ()
    {
        Package [] packages = Enumerable.Range (0, CsvExporter.MaxRows + 1)
                                        .Select (index => Package ($"id-{index}", "n"))
                                        .ToArray ();

        CsvSnapshot snapshot = CsvExporter.CreateSnapshot (packages);

        Assert.Equal (CsvExporter.MaxRows, snapshot.Rows.Count);
        Assert.Equal (1, snapshot.OmittedRowCount);
        Assert.True (snapshot.WasTruncated);
    }

    [Fact]
    public void CreateSnapshot_EnforcesCellAndAggregateCharacterLimits ()
    {
        string huge = new ('x', CsvExporter.MaxCellCharacters + 100);
        Package [] packages = Enumerable.Range (0, 100)
                                        .Select (index => Package ($"id-{index}", huge, huge))
                                        .ToArray ();

        CsvSnapshot snapshot = CsvExporter.CreateSnapshot (packages);

        Assert.InRange (snapshot.RetainedCharacters, 0, CsvExporter.MaxSnapshotCharacters);
        Assert.All (snapshot.Rows, row =>
        {
            Assert.InRange (row.Name.Length, 0, CsvExporter.MaxCellCharacters);
            Assert.InRange (row.Version.Length, 0, CsvExporter.MaxCellCharacters);
        });
        Assert.True (snapshot.TruncatedCellCount > 0);
        Assert.True (snapshot.WasTruncated);
    }

    private static Package Package (string id, string name, string version = "1.0") =>
        new () { Id = id, Name = name, Version = version, Source = "winget" };

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory ()
        {
            Path = System.IO.Path.Combine (System.IO.Path.GetTempPath (), $"winget-tui-tests-{Guid.NewGuid ():N}");
            Directory.CreateDirectory (Path);
        }

        internal string Path { get; }

        public void Dispose () => Directory.Delete (Path, recursive: true);
    }
}
