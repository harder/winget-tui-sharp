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

    [Fact]
    public async Task CreateSnapshot_PerCellBoundaryDoesNotSplitSurrogatePair ()
    {
        string name = new string ('x', CsvExporter.MaxCellCharacters - 1) + "😀tail";
        CsvSnapshot snapshot = CsvExporter.CreateSnapshot ([Package (string.Empty, name, string.Empty)]);
        string truncated = snapshot.Rows [0].Name;

        Assert.Equal (CsvExporter.MaxCellCharacters - 1, truncated.Length);
        AssertValidUtf16 (truncated);
        Assert.DoesNotContain ('\uFFFD', truncated);
        Assert.Equal (1, snapshot.TruncatedCellCount);

        using TempDirectory temp = new ();
        string path = Path.Combine (temp.Path, "scalar-safe.csv");
        await CsvExporter.WriteAtomicAsync (path, snapshot, TestContext.Current.CancellationToken);
        string csv = await File.ReadAllTextAsync (path, TestContext.Current.CancellationToken);
        AssertValidUtf16 (csv);
        Assert.DoesNotContain ('\uFFFD', csv);
        Assert.Contains ($"\"{truncated}\",\"\",\"\",\"\",\"winget\"", csv);
    }

    [Fact]
    public async Task CreateSnapshot_AggregateBoundaryOmitsWholeRow ()
    {
        const int remainingForTarget = 100;
        List<Package> packages = CreateFillerPackages (CsvExporter.MaxSnapshotCharacters - remainingForTarget);
        packages.Add (new ()
        {
            Id = string.Empty,
            Name = new string ('z', remainingForTarget - 1) + "😀tail",
            Version = string.Empty,
            Source = string.Empty
        });

        CsvSnapshot snapshot = CsvExporter.CreateSnapshot (packages);
        Assert.Equal (packages.Count - 1, snapshot.Rows.Count);
        Assert.Equal (1, snapshot.OmittedRowCount);
        Assert.Equal (CsvExporter.MaxSnapshotCharacters - remainingForTarget, snapshot.RetainedCharacters);
        Assert.DoesNotContain (snapshot.Rows, row => row.Name.StartsWith ('z'));
        Assert.True (snapshot.WasTruncated);

        using TempDirectory temp = new ();
        string path = Path.Combine (temp.Path, "aggregate-safe.csv");
        await CsvExporter.WriteAtomicAsync (path, snapshot, TestContext.Current.CancellationToken);
        string csv = await File.ReadAllTextAsync (path, TestContext.Current.CancellationToken);
        AssertValidUtf16 (csv);
        Assert.DoesNotContain ('\uFFFD', csv);
        Assert.DoesNotContain ("zzz", csv);
    }

    private static Package Package (string id, string name, string version = "1.0") =>
        new () { Id = id, Name = name, Version = version, Source = "winget" };

    private static List<Package> CreateFillerPackages (int characters)
    {
        List<Package> packages = [];
        int remaining = characters;

        while (remaining > 0)
        {
            string [] cells = new string [5];

            for (int index = 0; index < cells.Length; index++)
            {
                int length = Math.Min (remaining, CsvExporter.MaxCellCharacters);
                cells [index] = new string ((char)('a' + index), length);
                remaining -= length;
            }

            packages.Add (new ()
            {
                Name = cells [0],
                Id = cells [1],
                Version = cells [2],
                AvailableVersion = cells [3],
                Source = cells [4]
            });
        }

        return packages;
    }

    private static void AssertValidUtf16 (string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char current = value [index];

            if (char.IsHighSurrogate (current))
            {
                Assert.True (index + 1 < value.Length && char.IsLowSurrogate (value [index + 1]));
                index++;
            }
            else
            {
                Assert.False (char.IsLowSurrogate (current));
            }
        }
    }

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
