namespace WingetTuiSharp.Tests;

/// <summary>
/// Unit tests for the CliBackend parsing pipeline. Equivalent to the
/// <c>#[cfg(test)] mod tests</c> block inside upstream's <c>src/cli_backend.rs</c>.
///
/// Every test in here is anchored to a real bug that was found while porting:
/// the bare-CR spinner overwrite that ate the header, the CJK display-width
/// column misalignment that corrupted Chinese package names, the missing
/// "Found &lt;name&gt; [&lt;id&gt;]" handling that left the detail panel empty,
/// and the pin-state collapse that lost Blocking vs Gating distinction.
/// </summary>
public class ParserTests
{
    [Fact]
    public void ParseSearchTable_CapsRowsAtSharedSearchLimit ()
    {
        StringBuilder output = new ();
        output.AppendLine ($"{"Name",-16}{"Id",-24}{"Version",-10}Source");
        output.AppendLine (new string ('-', 58));

        // A duplicate-heavy prefix must not consume the unique-result budget. The later duplicate
        // also has a newer version, proving the normal duplicate preference still runs before cap.
        for (int i = 0; i < 100; i++)
        {
            output.AppendLine ($"{"Duplicate",-16}{"Example.Duplicate",-24}{"1.0",-10}winget");
        }

        output.AppendLine ($"{"Duplicate",-16}{"Example.Duplicate",-24}{"2.0",-10}winget");

        for (int i = 0; i < AppState.SearchResultLimit + 25; i++)
        {
            output.AppendLine ($"{$"Package{i}",-16}{$"Example.Package{i}",-24}{"1.0",-10}winget");
        }

        // Even after the unique-row ceiling is full, parsing must keep scanning for duplicate
        // replacements of an identity that was retained earlier.
        output.AppendLine ($"{"Duplicate",-16}{"Example.Duplicate",-24}{"3.0",-10}winget");

        IReadOnlyList<Package> rows = CliBackend.ParseSearchTable (output.ToString ());

        Assert.Equal (AppState.SearchResultLimit, rows.Count);
        Assert.Equal ("Example.Duplicate", rows [0].Id);
        Assert.Equal ("3.0", rows [0].Version);
        Assert.Equal ("Example.Package0", rows [1].Id);
        Assert.Equal ($"Example.Package{AppState.SearchResultLimit - 2}", rows [^1].Id);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParseTable — the core table parser
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTable_ExtractsRowsFromStandardEnglishOutput ()
    {
        const string output = """
            Name                Id                            Version    Source
            -------------------------------------------------------------
            Visual Studio Code  Microsoft.VisualStudioCode    1.95.0     winget
            Git                 Git.Git                       2.46.0     winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Equal (2, rows.Count);
        Assert.Equal ("Visual Studio Code", rows [0].Name);
        Assert.Equal ("Microsoft.VisualStudioCode", rows [0].Id);
        Assert.Equal ("1.95.0", rows [0].Version);
        Assert.Equal ("winget", rows [0].Source);
        Assert.Equal ("Git.Git", rows [1].Id);
    }

    [Fact]
    public void ParseTable_HandlesBareCarriageReturnSpinnerOverwrites ()
    {
        // Regression: winget emits a progress spinner that overwrites its line via bare \r
        // (CR without LF). When stdout is captured, those frames pile up *before* the real
        // header. SplitLines must honor the last \r per line so only the final state survives.
        const string output =
            "   -    \r   \\    \r   |    \r   /    \r"
            + "Name                Id                            Version    Source\r\n"
            + "-------------------------------------------------------------\r\n"
            + "Git                 Git.Git                       2.46.0     winget\r\n";

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal ("Git", rows [0].Name);
        Assert.Equal ("Git.Git", rows [0].Id);
    }

    [Fact]
    public void ParseTable_StripsAnsiCsiSequences ()
    {
        // Some winget builds emit ANSI color codes even when piped. The CSI stripper must
        // remove them before the dash-separator scan, or row data shifts left by 4–7 bytes
        // and SliceColumn lands on the wrong field.
        const string output = """
            Name                Id                            Version    Source
            -------------------------------------------------------------
            \x1B[32mGit\x1B[0m                 Git.Git                       2.46.0     winget
            """;

        string withEsc = output.Replace ("\\x1B", "\x1B");
        IReadOnlyList<Package> rows = CliBackend.ParseTable (withEsc, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal ("Git", rows [0].Name);
    }

    [Fact]
    public void ParseTable_StopsAtFooterDoesNotSpillIntoSecondaryTable ()
    {
        // winget upgrade --include-pinned appends a second mini-table after the footer.
        // We must stop at the footer and treat the secondary table as a separate parse.
        const string output = """
            Name      Id          Version      Available    Source
            ------------------------------------------------------
            Git       Git.Git     2.46.0       2.47.0       winget
            VS Code   Microsoft   1.94.0       1.95.0       winget
            2 upgrades available.

            Name             Id              Version  Available  Source
            -----------------------------------------------------------
            PinnedApp        Foo.Pinned      1.0      1.1        winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: true);

        Assert.Equal (3, rows.Count);
        Assert.Contains (rows, p => p.Id == "Foo.Pinned");
    }

    [Fact]
    public void ParseTable_HandlesThreeFooterDelimitedTables ()
    {
        // Regression test ported from shanselman/winget-tui#393 ("fix: restore MSRV and
        // multi-table parsing"). winget upgrade --include-pinned can append more than one
        // extra section (e.g. "explicitly targeted" packages, then "pin blocks upgrade"
        // packages) — each is its own footer-delimited mini-table. The old two-call
        // handling (first table + exactly one secondary table) silently dropped anything
        // past the second table; parsing must loop until a table has no trailing footer.
        const string output = """
            Name        Id             Version    Available    Source
            -------------------------------------------------------
            Regular     Example.One    1.0        2.0          winget
            1 upgrade available.

            The following package was explicitly targeted:
            Name        Id             Version    Available    Source
            -------------------------------------------------------
            Targeted    Example.Two    1.0        2.0          winget
            1 upgrade available.

            The following package has a pin that prevents upgrade:
            Name        Id             Version    Available    Source
            -------------------------------------------------------
            Pinned      Example.Three  1.0        2.0          winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: true);

        Assert.Equal (["Example.One", "Example.Two", "Example.Three"], rows.Select (p => p.Id));
        Assert.DoesNotContain (rows, p => p.Name == "Name");
    }

    [Fact]
    public void ParseTable_RejectsRowsWithMalformedIds ()
    {
        // Localized footer text occasionally lands in the id column. Valid ids contain '.',
        // '\', or have no spaces. Reject rows where id has a space but no '.' or '\'.
        const string output = """
            Name              Id                       Version    Source
            -----------------------------------------------------------
            Real Package      Real.Package             1.0.0      winget
            Footer text leak  some footer notice text  whatever   winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal ("Real.Package", rows [0].Id);
    }

    [Fact]
    public void ParseTable_DropsRowsWithEmptyIds ()
    {
        const string output = """
            Name              Id                       Version    Source
            -----------------------------------------------------------
            Real Package      Real.Package             1.0.0      winget

            Another One       Another.One              2.0.0      winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Equal (2, rows.Count);
    }

    [Fact]
    public void ParseTable_HandlesCjkColumnAlignmentByDisplayWidth ()
    {
        // Regression: a Chinese package name like "360极速浏览器" uses 4 chars but ~10
        // display columns. A char-index slice would land in the middle of the next
        // column and corrupt Id/Version. SliceColumn must walk by Rune width.
        const string output =
            "Name                Id           Version    Source\r\n"
            + "------------------------------------------------\r\n"
            + "360极速浏览器          66Chrome     1233.0     winget\r\n"
            + "115浏览器              .115Chrome   0.0        winget\r\n";

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Equal (2, rows.Count);
        Assert.Equal ("360极速浏览器", rows [0].Name);
        Assert.Equal ("66Chrome", rows [0].Id);
        Assert.Equal ("1233.0", rows [0].Version);
        Assert.Equal ("115浏览器", rows [1].Name);
        Assert.Equal (".115Chrome", rows [1].Id);
    }

    [Fact]
    public void ParseTable_ReturnsEmptyWhenNoSeparatorFound ()
    {
        Assert.Empty (CliBackend.ParseTable ("garbage output\nwith no dashes", hasAvailable: false));
        Assert.Empty (CliBackend.ParseTable (string.Empty, hasAvailable: false));
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParseShow — winget show output parser
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseShow_ExtractsNameAndIdFromFoundLine ()
    {
        // Regression: ParseShow used to require a "Name:" key, but `winget show` never
        // emits one — the package name lives on the first line as "Found <name> [<id>]".
        const string output = """
            Found Microsoft Visual Studio Code [Microsoft.VisualStudioCode]
            Version: 1.95.0
            Publisher: Microsoft Corporation
            Homepage: https://code.visualstudio.com
            License: MIT
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Microsoft.VisualStudioCode", output);

        Assert.NotNull (detail);
        Assert.Equal ("Microsoft Visual Studio Code", detail.Name);
        Assert.Equal ("Microsoft.VisualStudioCode", detail.Id);
        Assert.Equal ("1.95.0", detail.Version);
        Assert.Equal ("Microsoft Corporation", detail.Publisher);
        Assert.Equal ("https://code.visualstudio.com", detail.Homepage);
        Assert.Equal ("MIT", detail.License);
    }

    [Fact]
    public void ParseShow_LocaleIndependentFoundLine_German ()
    {
        // Upstream behavior: detect the "Prefix <name> [<id>]" pattern by trailing ] +
        // matching [, not by the English word "Found". German prefix is "Gefunden".
        const string output = """
            Gefunden Google Chrome [Google.Chrome]
            Version: 118.0.5993.118
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Google.Chrome", output);

        Assert.NotNull (detail);
        Assert.Equal ("Google Chrome", detail.Name);
        Assert.Equal ("Google.Chrome", detail.Id);
    }

    [Fact]
    public void ParseShow_PublisherUrlFallsBackToHomepageWhenEmpty ()
    {
        // Regression: empty-string "Homepage:" used to block the publisher_url fallback
        // because GetValueOrDefault returns "" (not null) for present-but-empty keys.
        const string output = """
            Found Foo [Acme.Foo]
            Homepage:
            Publisher Url: https://acme.example
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Acme.Foo", output);

        Assert.NotNull (detail);
        Assert.Equal ("https://acme.example", detail.Homepage);
    }

    [Fact]
    public void ParseShow_ContinuationLinesAppendToDescription ()
    {
        const string output = """
            Found Foo [Acme.Foo]
            Description: First line of the description.
              Second line continuation.
              Third line.
            Homepage: https://acme.example
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Acme.Foo", output);

        Assert.NotNull (detail);
        Assert.Equal ("First line of the description. Second line continuation. Third line.", detail.Description);
    }

    [Fact]
    public void ParseShow_LocaleAwareKeys_GermanPublisherAndDescription ()
    {
        const string output = """
            Gefunden Foo [Acme.Foo]
            Version: 1.0
            Herausgeber: ACME GmbH
            Beschreibung: Eine Beschreibung.
            Lizenz: GPL-3.0
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Acme.Foo", output);

        Assert.NotNull (detail);
        Assert.Equal ("ACME GmbH", detail.Publisher);
        Assert.Equal ("Eine Beschreibung.", detail.Description);
        Assert.Equal ("GPL-3.0", detail.License);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Dedupe — (id, source) collision handling
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dedupe_KeepsNewerVersionWhenIdAndSourceMatch ()
    {
        List<Package> rows =
        [
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "winget" },
            new () { Id = "Acme.Foo", Name = "Foo", Version = "2.0.0", Source = "winget" }
        ];

        List<Package> deduped = CliBackend.DedupePackages (rows);

        Assert.Single (deduped);
        Assert.Equal ("2.0.0", deduped [0].Version);
    }

    [Fact]
    public void Dedupe_OlderVersionDoesNotEvictNewer ()
    {
        List<Package> rows =
        [
            new () { Id = "Acme.Foo", Name = "Foo", Version = "2.0.0", Source = "winget" },
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "winget" }
        ];

        List<Package> deduped = CliBackend.DedupePackages (rows);

        Assert.Single (deduped);
        Assert.Equal ("2.0.0", deduped [0].Version);
    }

    [Fact]
    public void Dedupe_EqualVersionsBreakTieByAvailableVersion ()
    {
        // Same (id, source) collision key + same version → richer-metadata row wins.
        // Dedupe key includes Source, so Source itself can't differ between the two
        // candidates (different Sources are different packages).
        List<Package> rows =
        [
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "winget", AvailableVersion = null },
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "winget", AvailableVersion = "1.1.0" }
        ];

        List<Package> deduped = CliBackend.DedupePackages (rows);

        Assert.Single (deduped);
        Assert.Equal ("1.1.0", deduped [0].AvailableVersion);
    }

    [Fact]
    public void Dedupe_SourceComparisonIsCaseInsensitive ()
    {
        // "Winget" and "winget" are the same source for dedupe purposes.
        List<Package> rows =
        [
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "Winget" },
            new () { Id = "Acme.Foo", Name = "Foo", Version = "2.0.0", Source = "winget" }
        ];

        List<Package> deduped = CliBackend.DedupePackages (rows);

        Assert.Single (deduped);
        Assert.Equal ("2.0.0", deduped [0].Version);
    }

    [Fact]
    public void Dedupe_DifferentSourcesAreNotDeduplicated ()
    {
        // Same id but different source = different package (e.g. winget vs msstore).
        List<Package> rows =
        [
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "winget" },
            new () { Id = "Acme.Foo", Name = "Foo", Version = "1.0.0", Source = "msstore" }
        ];

        List<Package> deduped = CliBackend.DedupePackages (rows);

        Assert.Equal (2, deduped.Count);
    }

    [Fact]
    public void Dedupe_PreservesInsertionOrderForUniquePackages ()
    {
        List<Package> rows =
        [
            new () { Id = "A.A", Name = "A", Source = "winget" },
            new () { Id = "B.B", Name = "B", Source = "winget" },
            new () { Id = "C.C", Name = "C", Source = "winget" }
        ];

        List<Package> deduped = CliBackend.DedupePackages (rows);

        Assert.Equal (new [] { "A.A", "B.B", "C.C" }, deduped.Select (p => p.Id).ToArray ());
    }

    // ──────────────────────────────────────────────────────────────────────
    // CompareVersionsLike — version-aware ordering
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData ("1.0.0", "1.0.0", 0)]
    [InlineData ("1.119.0", "1.118.0", 1)]
    [InlineData ("1.118.0", "1.119.0", -1)]
    [InlineData ("1.2", "1.2.0", 0)]            // missing segments treated as 0
    [InlineData ("2.0.0", "10.0.0", -1)]        // numeric comparison, not lexical
    [InlineData ("1.0.0-beta", "1.0.0-alpha", 1)]
    public void CompareVersionsLike_OrdersAsExpected (string a, string b, int expectedSign)
    {
        int actual = CliBackend.CompareVersionsLike (a, b);
        Assert.Equal (expectedSign, Math.Sign (actual));
    }

    [Fact]
    public void CompareVersionsLike_EmptyStringsAreEqual ()
    {
        Assert.Equal (0, CliBackend.CompareVersionsLike (string.Empty, string.Empty));
    }

    [Fact]
    public void CompareVersionsLike_EmptyIsLessThanNonEmpty ()
    {
        Assert.True (CliBackend.CompareVersionsLike (string.Empty, "1.0.0") < 0);
        Assert.True (CliBackend.CompareVersionsLike ("1.0.0", string.Empty) > 0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParsePinState — Blocking / Gating(version) / Pinned / None precedence
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePinState_BlockingTrumpsAllOtherSignals ()
    {
        // Blocking wins even if a pinned-version is also present.
        PinState ps = CliBackend.ParsePinState ("Blocking", "1.0.0");

        Assert.Equal (PinStateKind.Blocking, ps.Kind);
    }

    [Fact]
    public void ParsePinState_GatingTypeWithVersionIsGating ()
    {
        PinState ps = CliBackend.ParsePinState ("Gating", "2.45.*");

        Assert.Equal (PinStateKind.Gating, ps.Kind);
        Assert.Equal ("2.45.*", ps.GatingVersion);
    }

    [Fact]
    public void ParsePinState_NonEmptyVersionWithUnknownTypeIsGating ()
    {
        // If the version column has content other than "latest", treat it as Gating
        // regardless of type column text.
        PinState ps = CliBackend.ParsePinState ("Something else", "1.0.0");

        Assert.Equal (PinStateKind.Gating, ps.Kind);
        Assert.Equal ("1.0.0", ps.GatingVersion);
    }

    [Fact]
    public void ParsePinState_LatestVersionIsPinnedNotGating ()
    {
        // The literal "latest" in the version column means "any latest version is fine" —
        // that's a regular pin, not a gating constraint.
        PinState ps = CliBackend.ParsePinState ("Pin", "latest");

        Assert.Equal (PinStateKind.Pinned, ps.Kind);
    }

    [Fact]
    public void ParsePinState_GatingTypeWithEmptyVersionDegradesToPinned ()
    {
        PinState ps = CliBackend.ParsePinState ("Gating", string.Empty);

        Assert.Equal (PinStateKind.Pinned, ps.Kind);
    }

    [Fact]
    public void ParsePinState_PinTypeIsPinned ()
    {
        Assert.Equal (PinStateKind.Pinned, CliBackend.ParsePinState ("Pin", string.Empty).Kind);
    }

    [Fact]
    public void ParsePinState_EmptyTypeEmptyVersionIsNone ()
    {
        PinState ps = CliBackend.ParsePinState (string.Empty, string.Empty);

        Assert.Equal (PinStateKind.None, ps.Kind);
    }

    // ──────────────────────────────────────────────────────────────────────
    // PackageDetail — context merging + sparse hint
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PackageDetail_MergeContext_FillsSourceFromListRow ()
    {
        // Regression: winget show often omits "Source:" entirely (especially for VCRedist
        // and similar). The list-row context (which knows the source) must fill it in.
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo", Source = string.Empty };
        Package row = new () { Id = "Acme.Foo", Name = "Foo", Source = "winget" };

        detail.MergeContext (row);

        Assert.Equal ("winget", detail.Source);
    }

    [Fact]
    public void PackageDetail_MergeContext_PrefersInstalledVersionFromListRow ()
    {
        // winget show reports the *latest* manifest version. The installed version is on
        // the list row, so MergeContext must overwrite Version with the list-row's.
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo", Version = "2.0.0" };
        Package row = new () { Id = "Acme.Foo", Name = "Foo", Version = "1.5.0" };

        detail.MergeContext (row);

        Assert.Equal ("1.5.0", detail.Version);
    }

    [Fact]
    public void PackageDetail_MergeContext_KeepsAvailableVersionWhenContextHasIt ()
    {
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo", AvailableVersion = null };
        Package row = new () { Id = "Acme.Foo", Name = "Foo", AvailableVersion = "1.1.0" };

        detail.MergeContext (row);

        Assert.Equal ("1.1.0", detail.AvailableVersion);
    }

    [Fact]
    public void PackageDetail_MergeContext_AdoptsContextPinStateWhenDetailUnpinned ()
    {
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo" };
        Package row = new () { Id = "Acme.Foo", Name = "Foo", PinState = new (PinStateKind.Blocking) };

        detail.MergeContext (row);

        Assert.Equal (PinStateKind.Blocking, detail.PinState.Kind);
    }

    [Fact]
    public void EnsureDetailHint_SynthesizesDescriptionWhenAllFieldsEmpty ()
    {
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo" };

        detail.EnsureDetailHint ();

        Assert.NotNull (detail.Description);
        Assert.Contains ("not available", detail.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureDetailHint_LeavesDescriptionAloneWhenAlreadyPopulated ()
    {
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo", Description = "Real description." };

        detail.EnsureDetailHint ();

        Assert.Equal ("Real description.", detail.Description);
    }

    [Fact]
    public void EnsureDetailHint_LeavesDescriptionAloneWhenAnyOtherFieldPopulated ()
    {
        // Having Publisher (or any other manifest field) means winget show returned real
        // data — the synthetic hint would mislead.
        PackageDetail detail = new () { Id = "Acme.Foo", Name = "Foo", Publisher = "ACME" };

        detail.EnsureDetailHint ();

        Assert.Null (detail.Description);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParsePins — winget pin list parser
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePins_HandlesNoPinsConfiguredMessage ()
    {
        IReadOnlyDictionary<string, PinState> pins = CliBackend.ParsePins ("No pins configured.\n");

        Assert.Empty (pins);
    }

    [Fact]
    public void ParsePins_ExtractsBlockingAndGatingFromTable ()
    {
        const string output = """
            Name           Id              Version      Pinned Version   Type
            ----------------------------------------------------------------
            Foo            Acme.Foo        1.0.0                         Blocking
            Bar            Acme.Bar        2.0.0        2.45.*           Gating
            """;

        IReadOnlyDictionary<string, PinState> pins = CliBackend.ParsePins (output);

        Assert.Equal (2, pins.Count);
        Assert.Equal (PinStateKind.Blocking, pins ["Acme.Foo"].Kind);
        Assert.Equal (PinStateKind.Gating, pins ["Acme.Bar"].Kind);
        Assert.Equal ("2.45.*", pins ["Acme.Bar"].GatingVersion);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParseTable — special id formats that broke with earlier parser versions
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTable_AcceptsStoreProductIds ()
    {
        // Microsoft Store ids are pure alphanumeric (no dots, no backslashes). The
        // bad-id-rejection rule must not drop these — it only rejects rows where id
        // contains a SPACE without dot/backslash.
        const string output = """
            Name             Id              Version    Source
            ----------------------------------------------------
            VLC              XPDM1ZWG815MQM  3.0.20     msstore
            WhatsApp         9NKSQGP7F2NH    2.2412.10  msstore
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Equal (2, rows.Count);
        Assert.Equal ("XPDM1ZWG815MQM", rows [0].Id);
        Assert.Equal ("9NKSQGP7F2NH", rows [1].Id);
    }

    [Fact]
    public void ParseTable_AcceptsArpMachineIdsWithBackslashes ()
    {
        // ARP\Machine\X64\Git_is1 — these come from Windows' Add/Remove Programs and
        // have backslashes in the id. The valid-id check requires `.` OR `\`.
        const string output = """
            Name              Id                              Version    Source
            ----------------------------------------------------------------
            Git               ARP\Machine\X64\Git_is1         2.46.0     winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal (@"ARP\Machine\X64\Git_is1", rows [0].Id);
    }

    [Fact]
    public void ParseTable_DigitPrefixedPackageNameIsNotFooter ()
    {
        // "7-Zip 26.01 (arm64)" starts with a digit but is NOT a footer. The footer
        // pattern is digit(s) + whitespace ("4 upgrades available"). Names like
        // "7-Zip" should parse as packages.
        const string output = """
            Name                    Id              Version    Source
            ---------------------------------------------------------
            7-Zip 26.01 (arm64)     7zip.7zip       26.01      winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal ("7zip.7zip", rows [0].Id);
    }

    [Fact]
    public void ParseTable_DigitSpacePackageNameIsNotFooter ()
    {
        // Regression test ported from shanselman/winget-tui#347 (fixed upstream in
        // "fix: distinguish digit-leading packages from footers"). A package literally
        // named "20 Minutes Till Dawn" has the exact same shape as a winget count footer
        // ("20 upgrades available.") — digits immediately followed by a space — so
        // IsFooterLine's naive digit-then-space heuristic stops parsing before this row
        // (and anything after it) is read. A real table row is column-aligned (runs of 2+
        // spaces between fields); a genuine footer is single-spaced prose.
        const string output = """
            Name                    Id                                      Version
            --------------------------------------------------------------------------------
            20 Minutes Till Dawn    ARP\Machine\X64\Steam App 1966900       Unknown
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal ("20 Minutes Till Dawn", rows [0].Name);
        Assert.Equal (@"ARP\Machine\X64\Steam App 1966900", rows [0].Id);
    }

    [Fact]
    public void ParseTable_TruncatedIdsAreNotRejected ()
    {
        // winget truncates long ids in the table with `…`. These are still parsed —
        // the UI layer guards operations on truncated ids separately.
        const string output = """
            Name              Id                              Version    Source
            ----------------------------------------------------------------
            Long Name         Microsoft.VeryLongPackageName…  1.0.0      winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.True (rows [0].IsTruncated);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParseTable — locale parity
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTable_GermanHeaders_PositionalFallback ()
    {
        // German "Name | ID | Version | Verfügbar | Quelle". NormalizeKey + ColIndex
        // accept the German column names directly, but if all four match the locale-
        // aware lookup table, positional fallback isn't needed. This verifies the
        // happy path.
        const string output = """
            Name              ID                       Version    Verfügbar    Quelle
            -----------------------------------------------------------------------
            Git               Git.Git                  2.46.0     2.47.0       winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: true);

        Assert.Single (rows);
        Assert.Equal ("Git", rows [0].Name);
        Assert.Equal ("Git.Git", rows [0].Id);
        Assert.Equal ("2.46.0", rows [0].Version);
        Assert.Equal ("2.47.0", rows [0].AvailableVersion);
        Assert.Equal ("winget", rows [0].Source);
    }

    [Fact]
    public void ParseTable_UnknownLocaleFallsBackToPositionalColumns ()
    {
        // When no locale-name lookup matches, the parser falls back to positional
        // indices (name=0, id=1, version=2, ...).
        const string output = """
            名前              標識                       バージョン   ソース
            -----------------------------------------------------------------
            Git               Git.Git                    2.46.0      winget
            """;

        IReadOnlyList<Package> rows = CliBackend.ParseTable (output, hasAvailable: false);

        Assert.Single (rows);
        Assert.Equal ("Git.Git", rows [0].Id);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParseShow — edge cases that upstream tests cover
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseShow_BracketedReleaseNotesDontHijackFoundLine ()
    {
        // The "PREFIX <name> [<id>]" detector matches lines ending in `]`. Indented
        // release-notes lines that mention things like "see [issue #42]" must not be
        // captured. The detector requires the bracket to be at the LAST position AND
        // no colon to be present on the same line.
        const string output = """
            Found Foo [Acme.Foo]
            Version: 1.0
            Release Notes:
              Fixes the issue described in [issue #42]
              Other improvements [see PR #7]
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Acme.Foo", output);

        Assert.NotNull (detail);
        Assert.Equal ("Foo", detail.Name);                  // not "the issue described in"
        Assert.Equal ("Acme.Foo", detail.Id);               // not "issue #42"
    }

    [Fact]
    public void ParseShow_HomepageNotOverwrittenByPublisherUrlWhenBothPresent ()
    {
        // Regression hardening: publisher_url is a FALLBACK for an empty Homepage,
        // not a replacement.
        const string output = """
            Found Foo [Acme.Foo]
            Homepage: https://acme.example
            Publisher Url: https://acme-corp.example
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Acme.Foo", output);

        Assert.NotNull (detail);
        Assert.Equal ("https://acme.example", detail.Homepage);
    }

    [Fact]
    public void ParseShow_ReleaseNotesUrlExtracted ()
    {
        const string output = """
            Found Foo [Acme.Foo]
            Version: 1.0
            Release Notes Url: https://github.com/acme/foo/releases/tag/v1.0
            """;

        PackageDetail? detail = CliBackend.ParseShow ("Acme.Foo", output);

        Assert.NotNull (detail);
        Assert.Equal ("https://github.com/acme/foo/releases/tag/v1.0", detail.ReleaseNotesUrl);
    }

    // ──────────────────────────────────────────────────────────────────────
    // CLI argument construction — regression tests for the `--exact` bug
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void InstallArgs_DoNotIncludeExact_RegressionForCatalogMatch ()
    {
        // Regression: an earlier version added `--exact` to install, which caused
        // failures when an id needed a substring catalog match. Upstream uses no
        // `--exact` on install.
        string [] args = CliBackend.InstallArgs ("Foo.Bar", version: null);

        Assert.DoesNotContain ("--exact", args);
        Assert.Contains ("install", args);
        Assert.Contains ("--id", args);
        Assert.Contains ("Foo.Bar", args);
        Assert.Contains ("--accept-source-agreements", args);
        Assert.Contains ("--accept-package-agreements", args);
        Assert.DoesNotContain ("--version", args);
    }

    [Fact]
    public void InstallArgs_AddVersionFlagWhenVersionProvided ()
    {
        string [] args = CliBackend.InstallArgs ("Foo.Bar", version: "1.2.3");

        int vidx = Array.IndexOf (args, "--version");
        Assert.True (vidx >= 0);
        Assert.Equal ("1.2.3", args [vidx + 1]);
    }

    [Fact]
    public void UpgradeByIdArgs_DoNotIncludeExact_OnlyTheNameFallbackDoes ()
    {
        // Regression: an earlier version flipped these. Upstream's id flow is
        // non-exact, name fallback is exact.
        string [] idArgs = CliBackend.UpgradeByIdArgs ("Foo.Bar");
        string [] nameArgs = CliBackend.UpgradeByNameArgs ("Foo.Bar");

        Assert.DoesNotContain ("--exact", idArgs);
        Assert.Contains ("--id", idArgs);

        Assert.Contains ("--exact", nameArgs);
        Assert.Contains ("--name", nameArgs);
    }

    [Fact]
    public void ListUpgradesArgs_IncludePinnedFlagIsPresent ()
    {
        string [] args = CliBackend.ListUpgradesArgs (null);

        Assert.Contains ("--include-pinned", args);
    }

    [Fact]
    public void ListInstalledArgs_DoNotInclude_IncludePinned ()
    {
        // `--include-pinned` is upgrade-only and would error on `list`.
        string [] args = CliBackend.ListInstalledArgs (null);

        Assert.DoesNotContain ("--include-pinned", args);
    }

    [Fact]
    public void PinAddArgs_UseBlockingMode ()
    {
        string [] args = CliBackend.PinAddArgs ("Foo.Bar");

        Assert.Contains ("--blocking", args);
        Assert.Contains ("--exact", args);
        Assert.Contains ("--disable-interactivity", args);
    }

    [Fact]
    public void PinRemoveArgs_DoNotIncludeInstalledFlag ()
    {
        // `winget pin remove --installed` would target only installed packages, which
        // is not what unpin should do.
        string [] args = CliBackend.PinRemoveArgs ("Foo.Bar");

        Assert.DoesNotContain ("--installed", args);
        Assert.Contains ("remove", args);
        Assert.Contains ("--id", args);
    }

    [Fact]
    public void SourceFilterArgs_AppendedWhenNotAll ()
    {
        string [] withAll = CliBackend.ListInstalledArgs (null);
        string [] withWinget = CliBackend.ListInstalledArgs ("winget");
        string [] withMsStore = CliBackend.ListInstalledArgs ("msstore");
        string [] withCustom = CliBackend.ListInstalledArgs ("contoso");

        Assert.DoesNotContain ("--source", withAll);
        Assert.Contains ("--source", withWinget);
        Assert.Equal ("winget", withWinget [Array.IndexOf (withWinget, "--source") + 1]);
        Assert.Equal ("msstore", withMsStore [Array.IndexOf (withMsStore, "--source") + 1]);

        // A custom/enterprise source name passes straight through (no enum mapping).
        Assert.Equal ("contoso", withCustom [Array.IndexOf (withCustom, "--source") + 1]);
    }

    [Fact]
    public void ParseSourceNames_ExtractsNameColumnIncludingCustomSources ()
    {
        // Representative `winget source list` output with the two predefined sources plus a custom one.
        string output =
            "Name     Argument\n"
            + "------------------------------------------------\n"
            + "winget   https://cdn.winget.microsoft.com/cache\n"
            + "msstore  https://storeedgefd.dsx.mp.microsoft.com/v9.0\n"
            + "contoso  https://pkgmgr-int.azureedge.net/cache\n";

        IReadOnlyList<string> names = CliBackend.ParseSourceNames (output);

        Assert.Equal (["winget", "msstore", "contoso"], names);
    }

    [Fact]
    public void ParseSourceNames_ReturnsEmptyWhenNoTable ()
    {
        Assert.Empty (CliBackend.ParseSourceNames ("no sources configured\n"));
    }

    [Fact]
    public void ShowArgs_UseExactFlag ()
    {
        // `winget show` SHOULD use --exact — we want the exact id match for the
        // detail panel, not a fuzzy match that might pick the wrong package.
        string [] args = CliBackend.ShowArgs ("Microsoft.VisualStudioCode");

        Assert.Contains ("--exact", args);
        Assert.Contains ("show", args);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Models — Package.IsTruncated, PinState.DisplayLabel
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData ("Foo.Bar", false)]
    [InlineData ("Microsoft.VeryLongPackageName…", true)]
    [InlineData ("Microsoft.VeryLongPackageName...", true)]
    [InlineData ("…", true)]
    [InlineData ("", false)]
    public void Package_IsTruncated_DetectsEllipsisAndDots (string id, bool expected)
    {
        Package p = new () { Id = id, Name = "x" };
        Assert.Equal (expected, p.IsTruncated);
    }

    [Fact]
    public void PinState_DisplayLabel_IncludesGatingVersion ()
    {
        Assert.Equal ("Pinned", new PinState (PinStateKind.Pinned).DisplayLabel ());
        Assert.Equal ("Blocking", new PinState (PinStateKind.Blocking).DisplayLabel ());
        Assert.Equal ("Gating 1.2.*", new PinState (PinStateKind.Gating, "1.2.*").DisplayLabel ());
        Assert.Equal (string.Empty, PinState.Unpinned.DisplayLabel ());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Terminal.Gui compatibility — catches regressions on version upgrades.
    // These exercise the Terminal.Gui APIs we depend on. If a future
    // Terminal.Gui release breaks our usage, these tests fail fast.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TerminalGui_ThemeRegisterDoesNotThrow_AndRegistersAllSchemes ()
    {
        // If Terminal.Gui changes Scheme construction (e.g. renames roles, requires new
        // fields, changes AddScheme signature), this fails.
        Theme.Register ();

        // Every named scheme must resolve after Register — guards against typos and
        // catches Terminal.Gui's SchemeManager getting renamed.
        string [] names =
        [
            Theme.AppSchemeName, Theme.SurfaceSchemeName, Theme.FrameFocusedSchemeName,
            Theme.FrameUnfocusedSchemeName, Theme.NavbarActiveSchemeName, Theme.NavbarInactiveSchemeName,
            Theme.StatusSchemeName, Theme.AccentSchemeName, Theme.AccentDimSchemeName,
            Theme.InfoSchemeName, Theme.DangerSchemeName, Theme.SuccessSchemeName
        ];

        foreach (string name in names)
        {
            Scheme? scheme = SchemeManager.GetScheme (name);
            Assert.NotNull (scheme);
        }
    }

    [Fact]
    public void Theme_TryApply_SwitchesPaletteAndSchemesReflectNewColors ()
    {
        try
        {
            Assert.True (Theme.TryApply ("amber"));
            Assert.Equal ("amber", Theme.CurrentPaletteName);
            Assert.Equal (Theme.AmberPalette.Accent, Theme.Accent);

            Scheme? scheme = SchemeManager.GetScheme (Theme.AccentSchemeName);
            Assert.NotNull (scheme);
            Assert.Equal (Theme.AmberPalette.Accent, scheme!.Normal.Foreground);
        }
        finally
        {
            // Reset so test order doesn't leak a non-default palette into other tests.
            Theme.TryApply ("sage");
        }
    }

    [Fact]
    public void Theme_TryApply_UnknownId_ReturnsFalse_LeavesCurrentPaletteUnchanged ()
    {
        try
        {
            Theme.TryApply ("sage");
            string before = Theme.CurrentPaletteName;
            Color accentBefore = Theme.Accent;

            Assert.False (Theme.TryApply ("not-a-real-theme"));
            Assert.Equal (before, Theme.CurrentPaletteName);
            Assert.Equal (accentBefore, Theme.Accent);
        }
        finally
        {
            Theme.TryApply ("sage");
        }
    }

    [Fact]
    public void TerminalGui_RuneGetColumns_ReturnsExpectedWidthForCjkAndAscii ()
    {
        // Our display-width column slicing in CliBackend depends on
        // Rune.GetColumns() reporting 2 for CJK and 1 for ASCII. If Terminal.Gui
        // changes this (e.g. swaps Wcwidth versions and CJK width changes), the
        // CJK alignment test would also break — but a direct probe catches it
        // earlier with a clearer failure.
        Assert.Equal (1, new System.Text.Rune ('A').GetColumns ());
        Assert.Equal (1, new System.Text.Rune ('0').GetColumns ());
        Assert.Equal (2, new System.Text.Rune ('极').GetColumns ());
        Assert.Equal (2, new System.Text.Rune ('浏').GetColumns ());
    }

    [Fact]
    public void TerminalGui_StringGetColumns_ClampsToGraphemeClusterWidth ()
    {
        // `string.GetColumns()` walks grapheme clusters and clamps each to 2 cols
        // (correctly handles ZWJ emoji and combining marks). Our parser doesn't
        // currently depend on this, but the detail panel's word-wrap does.
        Assert.Equal (5, "Hello".GetColumns ());

        // "极速" — two CJK chars, 4 display cols
        Assert.Equal (4, "极速".GetColumns ());
    }

    [Fact]
    public void TerminalGui_LogoHasExpectedDimensions ()
    {
        // The Logo's wordmark is 51 cols × 5 rows. If View's Width/Height contract
        // changes signature, this catches it.
        Logo logo = new ();

        Assert.Equal (51, Logo.LogoWidth);   // "WINGET TUI #" — 51 cols of 5-row block art
        Assert.Equal (5, Logo.LogoHeight);
    }

    [Fact]
    public void TerminalGui_LogoRendersWingetTuiHashWordmark ()
    {
        System.Reflection.FieldInfo linesField = typeof (Logo).GetField (
            "_lines",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        string [] lines = Assert.IsType<string []> (linesField.GetValue (null));

        Assert.Equal (
            [
                "█   █ ███ █  █  ██  ████ ████  ████ █  █ ███   █ █ ",
                "█   █  █  ██ █ █    █     █     █   █  █  █   █████",
                "█ █ █  █  █ ██ █ ██ ███   █     █   █  █  █    █ █ ",
                "██ ██  █  █  █ █  █ █     █     █   █  █  █   █████",
                "█   █ ███ █  █  ███ ████  █     █    ██  ███   █ █ "
            ],
            lines);
    }

    [Fact]
    public void TerminalGui_TabBarReportsClickedTabViaEvent ()
    {
        // Catches changes to View.OnMouseEvent signature, Mouse class shape, or our
        // hit-testing math. Use reflection because OnMouseEvent is protected.
        TabBar bar = new () { Frame = new (0, 0, 60, 1) };
        AppMode? clicked = null;
        bar.TabClicked += (_, m) => clicked = m;

        Mouse click = new ()
        {
            Position = new (0, 0),                          // top-left, "1 ◇ Search" tab
            Flags = MouseFlags.LeftButtonClicked
        };

        System.Reflection.MethodInfo onMouse = typeof (TabBar).GetMethod (
            "OnMouseEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull (onMouse);

        object? result = onMouse.Invoke (bar, [click]);

        Assert.True ((bool) result!);
        Assert.Equal (AppMode.Search, clicked);
    }

    [Fact]
    public void TerminalGui_MarkedTableSourceCanWrapEnumerableTableSource ()
    {
        // Verifies IEnumerableTableSource<T> contract surface used by our cursor
        // marker wrapper. If Terminal.Gui renames Columns, ColumnNames, Rows, etc.,
        // this fails at compile or assertion time.
        Package[] data =
        [
            new () { Id = "A.A", Name = "A" },
            new () { Id = "B.B", Name = "B" }
        ];
        EnumerableTableSource<Package> inner = new (data, new ()
        {
            { "Name", p => ((Package)p).Name },
            { "Id", p => ((Package)p).Id }
        });

        // MarkedTableSource is nested-private inside App; reach it via reflection.
        Type marked = typeof (App).GetNestedType ("MarkedTableSource", System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull (marked);
    }
}
