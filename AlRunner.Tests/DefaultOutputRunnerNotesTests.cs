// DefaultOutputRunnerNotesTests — issues #4561, #4564, #4565 (tracker #4559).
//
// Three kinds of runner bookkeeping moved off a user's default output, each still reachable
// under --verbose. The spawn-level half of #4561 is in CleanRunStartupVerbosityTests; this
// file drives the pieces that decide what prints, in process.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// Serial: FailureOnlyNotes is process-wide and reads Log.Verbose. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class DefaultOutputRunnerNotesTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // ── #4561 (1): the [expectations] lines ──────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void ExpectationsLines_PrintOnlyWhenVerboseOrAnExpectationsFlagWasGiven(
        bool verbose, bool dirGiven, bool requireMatch, bool expected)
    {
        Assert.Equal(expected, ExpectationsDirectoryResolution.ShouldAnnounce(verbose, dirGiven, requireMatch));
    }

    // ── #4561 (3): the TableNo warning ────────────────────────────────────────────────────

    private const string FullText = "[warn] RecordPatches: CodeUnit Metadata: 3 declared TableNo ... full list";
    private const string Footer = "[warn] CodeUnit Metadata: 3 declared TableNo reference(s) could not be resolved";

    private static (string Immediate, string AtEnd, int Written) AddThenFlush(bool verbose, bool anyTestFailed)
    {
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var immediate = new StringWriter();
        var atEnd = new StringWriter();
        FailureOnlyNotes.ResetForTests();
        try
        {
            Console.SetError(immediate);
            Log.Verbose = verbose;
            FailureOnlyNotes.Add(FullText, Footer);
            FailureOnlyNotes.Add(FullText, Footer); // the table is rebuilt per generation
            var written = FailureOnlyNotes.Flush(atEnd, anyTestFailed);
            return (immediate.ToString(), atEnd.ToString(), written);
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetError(savedErr);
            FailureOnlyNotes.ResetForTests();
        }
    }

    [Fact]
    public void TableNoNote_DefaultRunThatPassed_PrintsNothing()
    {
        var (immediate, atEnd, written) = AddThenFlush(verbose: false, anyTestFailed: false);
        Assert.Equal("", immediate);
        Assert.Equal("", atEnd);
        Assert.Equal(0, written);
    }

    [Fact]
    public void TableNoNote_DefaultRunThatFailed_PrintsOneFooterLine()
    {
        var (immediate, atEnd, written) = AddThenFlush(verbose: false, anyTestFailed: true);
        Assert.Equal("", immediate);
        Assert.Equal(Footer + Environment.NewLine, atEnd);
        Assert.Equal(1, written);
    }

    [Fact]
    public void TableNoNote_Verbose_PrintsTheFullTextWhereItArises()
    {
        var (immediate, _, _) = AddThenFlush(verbose: true, anyTestFailed: false);
        Assert.Contains(FullText, immediate);
    }

    [Fact]
    public void TableNoNote_FlushClearsSoAServerCycleDoesNotReprintIt()
    {
        FailureOnlyNotes.ResetForTests();
        try
        {
            var savedVerbose = Log.Verbose;
            Log.Verbose = false;
            try { FailureOnlyNotes.Add(FullText, Footer); } finally { Log.Verbose = savedVerbose; }
            FailureOnlyNotes.Flush(TextWriter.Null, anyTestFailed: false);
            Assert.Empty(FailureOnlyNotes.PendingForTests());
        }
        finally { FailureOnlyNotes.ResetForTests(); }
    }

    /// <summary>
    /// The footer line must survive Log's default filter: a `[RecordPatches]` prefix would be
    /// dropped and the failing run would never name the limitation.
    /// </summary>
    [Fact]
    public void TableNoFooter_FromTheRealCallSite_SurvivesTheDefaultFilter()
    {
        var src = File.ReadAllText(Path.Combine(
            RepoRoot, "AlRunner", "Patches", "RecordPatches.CodeunitMetadataVirtualTable.cs"));
        var call = src.IndexOf("AlRunner.Infrastructure.FailureOnlyNotes.Add(", StringComparison.Ordinal);
        Assert.True(call >= 0, "the TableNo warning is no longer routed through FailureOnlyNotes");
        var window = src.Substring(call, Math.Min(1500, src.Length - call));
        Assert.Contains("declared TableNo", window);
        Assert.DoesNotContain("Console.Error.WriteLine(", window.Substring(0, window.IndexOf("_codeunitMetaRows", StringComparison.Ordinal)));

        const string footerStart = "$\"[warn] CodeUnit Metadata: ";
        var footerAt = window.IndexOf(footerStart, StringComparison.Ordinal);
        Assert.True(footerAt >= 0, "the footer line no longer starts with `[warn] CodeUnit Metadata:`");
        var literalStart = footerAt + 2;
        var literal = window.Substring(literalStart, window.IndexOf('"', literalStart) - literalStart);
        var rendered = System.Text.RegularExpressions.Regex.Replace(literal, @"\{[^}]*\}", "3");

        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();
            Log.Verbose = false;
            Console.Error.WriteLine(rendered);
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
        Assert.StartsWith("[warn] CodeUnit Metadata: 3 declared TableNo", rendered);
        Assert.Contains(rendered, sink.ToString());
    }

    [Fact]
    public void ProgramCs_FlushesFailureOnlyNotesOnTheTestFailureCount()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        Assert.Contains("anyTestFailedOrErrored = failed + errored > 0;", src);
        Assert.Contains("AlRunner.Infrastructure.FailureOnlyNotes.Flush(Console.Error, anyTestFailedOrErrored);", src);
    }

    // The one-shot flush above is never reached by --server, --dap or --watch: each returns or
    // loops before it. So every resident mode flushes per request/cycle, or the footer is lost.
    private static string ProgramRegion(string src, string startMarker, string endMarker)
    {
        var start = src.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Program.cs no longer contains `{startMarker}`");
        var end = src.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Program.cs no longer contains `{endMarker}` after `{startMarker}`");
        return src.Substring(start, end - start);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private const string ResidentFlush = "AlRunner.Infrastructure.FailureOnlyNotes.FlushAfter(";

    [Fact]
    public void ProgramCs_ServerMode_FlushesFailureOnlyNotesOnEveryRequest()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        var server = src.Substring(src.IndexOf("int RunServerLoop(", StringComparison.Ordinal));
        // One flush per per-request drain: runTests and execute each drain once.
        var drains = Count(server, "CompanyInitializer.DrainFailures()");
        Assert.True(drains >= 2, $"expected the runTests and execute drains in RunServerLoop, found {drains}");
        Assert.Equal(drains, Count(server, ResidentFlush));
    }

    [Fact]
    public void ProgramCs_DapMode_FlushesFailureOnlyNotesWhenTheRunFinishes()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        var dap = ProgramRegion(src, "_ = bundleRunTask.ContinueWith(t =>", "SendTerminatedOnce();");
        Assert.Contains(ResidentFlush, dap);
    }

    [Fact]
    public void ProgramCs_WatchMode_FlushesFailureOnlyNotesEveryCycle()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        var watch = ProgramRegion(src, "Reporter.PrintSummary(results, Console.Out);", "[watch] waiting for AL source changes");
        Assert.Contains(ResidentFlush, watch);
    }

    [Theory]
    [InlineData(TestOutcome.Pass, 0)]
    [InlineData(TestOutcome.Skipped, 0)]
    [InlineData(TestOutcome.Fail, 1)]
    [InlineData(TestOutcome.Error, 1)]
    public void FlushAfter_WritesTheFooterOnlyWhenATestFailedOrErrored(TestOutcome outcome, int expected)
    {
        var savedVerbose = Log.Verbose;
        FailureOnlyNotes.ResetForTests();
        try
        {
            Log.Verbose = false;
            FailureOnlyNotes.Add(FullText, Footer);
            var sink = new StringWriter();
            var tests = new[]
            {
                new TestResult("Codeunit50000", "Passes", TestOutcome.Pass, null, null, TimeSpan.Zero),
                new TestResult("Codeunit50000", "Other", outcome, null, null, TimeSpan.Zero),
            };
            Assert.Equal(expected, FailureOnlyNotes.FlushAfter(sink, tests));
            Assert.Equal(expected == 1 ? Footer + Environment.NewLine : "", sink.ToString());
            Assert.Empty(FailureOnlyNotes.PendingForTests());
        }
        finally
        {
            Log.Verbose = savedVerbose;
            FailureOnlyNotes.ResetForTests();
        }
    }

    // ── #4564: the Program.cs wiring ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("AlRunner.Provisioning.ArtifactDownloader.TestApps(")]
    [InlineData("AlRunner.Provisioning.ArtifactDownloader.PlatformApps(")]
    public void ProgramCs_ManifestAppDownloads_AreCondensedOnVerbose(string call)
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        var at = src.IndexOf(call, StringComparison.Ordinal);
        Assert.True(at >= 0, $"Program.cs no longer calls `{call}`");
        Assert.Equal(1, Count(src, call));
        var args = src.Substring(at, src.IndexOf(");", at, StringComparison.Ordinal) + 2 - at);
        Assert.Contains("AlRunner.Infrastructure.ProvisionProgressLog.Condense(", args);
        Assert.Contains("AlRunner.Log.Verbose)", args);
    }

    // ── #4564: one line per download set ─────────────────────────────────────────────────

    // The shape ArtifactDownloader.TestApps logs for a successful set.
    private static readonly string[] TestToolkitDownload =
    {
        "Resolving artifact size for BC 28.1.49838.55128 (platform)...",
        "Downloading ZIP directory...",
        "Downloading central directory...",
        "Found 108 test-toolkit .app files",
        "  Written Microsoft_Any.app (0 MB)",
        "  Written Microsoft_Library Assert.app (0 MB)",
        "Downloaded 108 test .app file(s) (20 MB) to /a/test-apps",
    };

    private static List<string> Feed(IEnumerable<string> messages, bool verbose)
    {
        var got = new List<string>();
        var log = ProvisionProgressLog.Condense(got.Add, verbose);
        foreach (var m in messages) log(m);
        return got;
    }

    [Fact]
    public void Download_Default_KeepsOnlyTheSetSummary()
    {
        Assert.Equal(
            new[] { "Downloaded 108 test .app file(s) (20 MB) to /a/test-apps" },
            Feed(TestToolkitDownload, verbose: false));
    }

    [Fact]
    public void Download_Verbose_KeepsEveryLine()
    {
        Assert.Equal(TestToolkitDownload, Feed(TestToolkitDownload, verbose: true));
    }

    [Fact]
    public void Download_Default_AFailureStaysLoudWithItsUrlAndFix()
    {
        var failure = new[]
        {
            "Resolving artifact size for BC 99.0.0.0 (platform)...",
            "Error: no BC artifact published for 99.0.0.0 (platform): https://example/99.0.0.0/platform",
            "       Check the version, or resolve the latest for a prefix:",
            "         al-runner provision --resolve-version 99",
        };
        Assert.Equal(failure.Skip(1), Feed(failure, verbose: false));
    }

    [Fact]
    public void Download_Default_WarningsAndRetriesStayVisible()
    {
        var messages = new[]
        {
            "  Written A.app (0 MB)",
            "  Retrying download of B.app (timeout)...",
            "  WARNING: C.app: truncated — skipping",
            "Warning: skipping System.app",
            "  Written D.app (0 MB)",
        };
        Assert.Equal(
            new[] { messages[1], messages[2], messages[3] },
            Feed(messages, verbose: false));
    }

    /// <summary>The wrapper reaches the downloader's log only: AutoProvision's own start and
    /// failure lines are not per-file progress and must stay.</summary>
    [Fact]
    public void AutoProvision_WrapsOnlyTheDownloaderLog()
    {
        var dir = TestScratch.Dir("al-runner-autoprovision-wrap");
        var got = new List<string>();
        try
        {
            var ok = ProvisioningCheck.AutoProvision(
                "99.0.0.0", dir, got.Add,
                downloader: (_, _, log) =>
                {
                    log!("Found 501 service-tier DLLs");
                    log!("  …50/501 extracted (30 MB)");
                    return 1;
                },
                wrapDownloadLog: sink => ProvisionProgressLog.Condense(sink, verbose: false));
            Assert.False(ok);
            Assert.Contains(got, l => l.StartsWith("[provision] downloading BC 99.0.0.0", StringComparison.Ordinal));
            Assert.Contains(got, l => l.StartsWith("[provision] download failed (exit 1)", StringComparison.Ordinal));
            Assert.DoesNotContain(got, l => l.Contains("extracted", StringComparison.Ordinal));
            Assert.DoesNotContain(got, l => l.StartsWith("Found ", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── #4565: the two [test-data] lines ─────────────────────────────────────────────────

    private static TestDataProvisioner.Summary OwnersRun(int refusedByReader = 0) => new(
        "/a/28.1.49838.53910/w1/BusinessCentral-W1.bak", "CRONUS International Ltd_",
        TablesHydrated: 93, RowsHydrated: 4597, TablesSkippedAmbiguous: 0, TablesRefused: 0,
        TablesRefusedByReader: refusedByReader, ColumnsFromUninstalledApps: 233, ColumnsNotInThisBuild: 0);

    [Fact]
    public void TestDataEnd_Default_IsOneLineWithOnlyNonZeroCounts()
    {
        Assert.Equal(
            "Test data: 4597 rows loaded from 93 tables; 233 extension column(s) dropped for apps this run does not install",
            OwnersRun().Describe(verbose: false));
    }

    [Fact]
    public void TestDataEnd_Default_ANonZeroRefusalIsNamed()
    {
        var line = OwnersRun(refusedByReader: 2).Describe(verbose: false);
        Assert.Contains("2 refused by the backup reader", line);
        Assert.DoesNotContain("refused (unsupported", line);
        Assert.DoesNotContain("#4123", line);
        Assert.DoesNotContain("timestamp", line);
    }

    [Fact]
    public void TestDataEnd_AllZero_IsTheBareCounts()
    {
        var s = OwnersRun() with { ColumnsFromUninstalledApps = 0 };
        Assert.Equal("Test data: 4597 rows loaded from 93 tables", s.Describe(verbose: false));
    }

    [Fact]
    public void TestDataEnd_Verbose_AddsTheFullBreakdown()
    {
        var text = OwnersRun().Describe(verbose: true);
        Assert.StartsWith("Test data: 4597 rows loaded from 93 tables", text);
        Assert.Contains("[test-data] loaded 4597 row(s) in 93 table(s) this run touched", text);
        Assert.Contains("0 refused by the backup reader", text);
        Assert.Contains("seeds the stamp counter (#4123)", text);
    }

    [Fact]
    public void TestDataStart_Default_NamesCompanyBackupAndBc()
    {
        var lines = TestDataProvisioner.DescribeArm(
            "/a/28.1.49838.53910/w1/BusinessCentral-W1.bak", "CRONUS International Ltd_",
            new Version(28, 1, 49838, 53910), 359, 68, 0, verbose: false);
        Assert.Equal(
            new[] { "Test data: company 'CRONUS International Ltd_' from BusinessCentral-W1.bak (BC 28.1)" },
            lines);
    }

    [Fact]
    public void TestDataStart_ExplicitBackup_OmitsTheBcItCannotKnow()
    {
        var lines = TestDataProvisioner.DescribeArm(
            "/x/mine.bak", "My Co", bcVersion: null, 1, 0, 0, verbose: false);
        Assert.Equal(new[] { "Test data: company 'My Co' from mine.bak" }, lines);
    }

    [Fact]
    public void TestDataStart_Verbose_KeepsThePlanLine()
    {
        var lines = TestDataProvisioner.DescribeArm(
            "/a/w1/BusinessCentral-W1.bak", "CRONUS", new Version(28, 1), 359, 68, 0, verbose: true);
        Assert.Equal(2, lines.Count);
        Assert.Equal(
            "[test-data] backup '/a/w1/BusinessCentral-W1.bak', company 'CRONUS', 359 table(s) in scope "
            + "(68 with table-extension data to merge, 0 sharing a name no app id could select); loading on first touch.",
            lines[1]);
    }
}
