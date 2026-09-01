using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2272 — the self-populating virtual tables (AllObj 2000000038, Field 2000000041,
/// AllObjWithCaption 2000000058, Table Metadata 2000000136, Page Metadata 2000000138 and
/// the rest of RecordPatches.IsSelfPopulatingVirtualTableId) were captured into the install
/// baseline and re-inserted at EVERY codeunit boundary — and, under TestIsolation.Test, at
/// every test boundary. On a trivial fixture with the Base Application closure that was
/// 23,651 rows per boundary instead of 278.
///
/// They never needed to be there: GetDataAccessForTableCore re-populates each of them on
/// every access (PopulateAllObjVirtualTable and its siblings) before it hands the data
/// access back, and the populated-guards those top-ups consult are ConditionalWeakTables
/// keyed by the in-memory PROVIDER — which a boundary restore replaces — so the table is
/// re-derived from scratch on the next read whether or not the restore carried its rows.
/// The on-disk baseline codec has filtered exactly this set out on write since it landed,
/// so a disk-cache HIT has been running without them on every warm machine already.
///
/// The claim under test is BOTH halves, because either alone is worthless:
///
///   1. They are gone from the baseline. Asserted from the AL_RUNNER_PERF marker
///      CaptureInstallBaselineSnapshot logs — which now NAMES the skipped ids rather than
///      counting them, so "skipped AllObj" and "skipped something that should have been
///      captured" cannot be confused — plus the per-boundary restore row count, which is
///      the cost the issue is about.
///
///   2. They still answer truthfully afterwards. The fixture's AL asserts AllObj,
///      AllObjWithCaption, Field, Table Metadata and Page Metadata for concrete objects it
///      declares itself, positively (the object is there, with the right name) and
///      negatively (a neighbouring id it does not declare is NOT there) — a stale or
///      foreign inventory fails the negative half, which a row-count assertion could never
///      catch. It runs in two symmetric test codeunits, each with two tests, so at least
///      one boundary of each kind is crossed before an assertion regardless of the order
///      the executor picks (codeunit order is NOT id order — measured).
///
/// The fixture materialises all five tables from its OWN Install trigger, which runs
/// immediately before CaptureInstallBaseline(). Without that, whether a virtual table was
/// in the baseline at all would depend on whether Company-Initialize happened to touch it
/// on the BC version under test, and the marker assertion would be flaky across the matrix.
///
/// AL_RUNNER_NO_DEP_COMPANY_CACHE=1 is set so the dependency+company baseline is computed
/// fresh: on a machine with a warm on-disk baseline the restore already omits these tables
/// (that is the issue's whole point), and the test would measure the cache rather than the
/// change.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (loudly) when absent.
/// </summary>
public class InstallBaselineVirtualTableExclusionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>Every table id the fixture's Install trigger materialises, and therefore
    /// every id the capture must report as skipped. Kept as the literal numbers rather than
    /// referencing the runner's constants: a test that reads the same constant the
    /// implementation does cannot notice one of them changing.</summary>
    private static readonly int[] ExpectedSkippedIds =
    {
        2000000038,  // AllObj
        2000000041,  // Field
        2000000058,  // AllObjWithCaption
        2000000136,  // Table Metadata
        2000000138,  // Page Metadata
    };

    /// <summary>Ceiling on rows re-inserted at one boundary for this fixture. The measured
    /// numbers either side are 23,651 (before) and 278 (after), so the bound is nowhere near
    /// either — it is a statement that the virtual tables are absent, not a perf budget that
    /// needs re-tuning when the Base App closure grows by a row.</summary>
    private const int MaxRestoredRowsPerBoundary = 3000;

    private static (string output, int exit) RunRunner(string[] extraArgs, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        // The virtual tables only have a meaningful inventory (and Table/Page Metadata only
        // resolve at all) with the platform apps on the package-cache path.
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            Environment =
            {
                ["AL_RUNNER_PERF"] = "1",
                ["AL_RUNNER_NO_DEP_COMPANY_CACHE"] = "1",
            },
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void WriteFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "IT2272 Virtual Table Baseline",
          "publisher": "IssueTest2272",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 62500, "to": 62519 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), """
        table 62500 "IT2272 Widget"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Description; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 62501 "IT2272 Widget Card"
        {
            PageType = Card;
            SourceTable = "IT2272 Widget";
            layout
            {
                area(content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                    field(Description; Rec.Description) { ApplicationArea = All; }
                }
            }
        }

        codeunit 62505 "IT2272 Install"
        {
            Subtype = Install;

            // Materialises every virtual table this fixture asserts on BEFORE the install
            // baseline is captured, so "was this table in the baseline?" is a deterministic
            // question on every BC version instead of depending on whether the dependency
            // Install triggers or Company-Initialize happened to touch it.
            trigger OnInstallAppPerCompany()
            var
                AllObjRec: Record AllObj;
                AllObjCap: Record AllObjWithCaption;
                FieldRec: Record "Field";
                TableMeta: Record "Table Metadata";
                PageMeta: Record "Page Metadata";
            begin
                AllObjRec.SetRange("Object Type", AllObjRec."Object Type"::Table);
                if AllObjRec.FindFirst() then;
                AllObjCap.SetRange("Object Type", AllObjCap."Object Type"::Table);
                if AllObjCap.FindFirst() then;
                FieldRec.SetRange(TableNo, 62500);
                if FieldRec.FindFirst() then;
                if TableMeta.FindFirst() then;
                if PageMeta.FindFirst() then;
            end;
        }

        codeunit 62504 "IT2272 Assert"
        {
            procedure IsTrue(Condition: Boolean; Msg: Text)
            begin
                if not Condition then
                    Error('Assert.IsTrue failed. %1', Msg);
            end;

            procedure IsFalse(Condition: Boolean; Msg: Text)
            begin
                if Condition then
                    Error('Assert.IsFalse failed. %1', Msg);
            end;

            procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Assert.AreEqual failed. Expected <%1>, got <%2>. %3', Expected, Actual, Msg);
            end;

            // Every assertion is a concrete value for a concrete object, and every one has a
            // negative twin against id 62599 — an id this app declares nothing at. A restore
            // that left a stale or foreign inventory behind passes the positive half and
            // fails the negative one; an empty table fails the positive half. Neither can be
            // satisfied by a top-up that silently does nothing.
            procedure CheckVirtualTables()
            var
                AllObjRec: Record AllObj;
                AllObjCap: Record AllObjWithCaption;
                FieldRec: Record "Field";
                TableMeta: Record "Table Metadata";
                PageMeta: Record "Page Metadata";
                FieldNames: Text;
            begin
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Table, 62500), 'AllObj must list table 62500');
                AreEqual('IT2272 Widget', AllObjRec."Object Name", 'AllObj object name for table 62500');
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Page, 62501), 'AllObj must list page 62501');
                AreEqual('IT2272 Widget Card', AllObjRec."Object Name", 'AllObj object name for page 62501');
                IsFalse(AllObjRec.Get(AllObjRec."Object Type"::Table, 62599), 'AllObj must NOT list table 62599');

                IsTrue(AllObjCap.Get(AllObjCap."Object Type"::Table, 62500), 'AllObjWithCaption must list table 62500');
                AreEqual('IT2272 Widget', AllObjCap."Object Name", 'AllObjWithCaption object name for table 62500');
                IsFalse(AllObjCap.Get(AllObjCap."Object Type"::Table, 62599), 'AllObjWithCaption must NOT list table 62599');

                FieldRec.SetRange(TableNo, 62500);
                IsTrue(FieldRec.FindSet(), 'Field must have rows for table 62500');
                repeat
                    FieldNames += FieldRec."Field Caption" + ';';
                until FieldRec.Next() = 0;
                IsTrue(StrPos(FieldNames, 'No.;') > 0, 'Field must list "No." for table 62500, got ' + FieldNames);
                IsTrue(StrPos(FieldNames, 'Description;') > 0, 'Field must list Description for table 62500, got ' + FieldNames);
                FieldRec.Reset();
                FieldRec.SetRange(TableNo, 62599);
                IsTrue(FieldRec.IsEmpty(), 'Field must have no rows for nonexistent table 62599');

                IsTrue(TableMeta.Get(62500), 'Table Metadata must list 62500');
                AreEqual('IT2272 Widget', TableMeta.Name, 'Table Metadata name for 62500');
                IsFalse(TableMeta.Get(62599), 'Table Metadata must NOT list 62599');

                IsTrue(PageMeta.Get(62501), 'Page Metadata must list 62501');
                IsFalse(PageMeta.Get(62599), 'Page Metadata must NOT list 62599');
            end;
        }

        codeunit 62502 "IT2272 Tests A"
        {
            Subtype = Test;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryA1()
            var
                IT2272Assert: Codeunit "IT2272 Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryA2()
            var
                IT2272Assert: Codeunit "IT2272 Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;
        }

        // Symmetric with A, sharing one body: codeunit execution order is not id order, so a
        // fixture whose proof depends on which of the two runs first proves nothing.
        codeunit 62503 "IT2272 Tests B"
        {
            Subtype = Test;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryB1()
            var
                IT2272Assert: Codeunit "IT2272 Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryB2()
            var
                IT2272Assert: Codeunit "IT2272 Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;
        }
        """);
    }

    private static readonly Regex CaptureLine = new(
        @"InstallBaseline\.Capture .* skipped-self-populating \[([0-9,]*)\]", RegexOptions.Compiled);
    private static readonly Regex RestoreLine = new(
        @"InstallBaseline\.Restore (\d+) row\(s\)", RegexOptions.Compiled);

    private static void AssertBaselineExcludesVirtualTablesAndAlPasses(string output, int exitCode)
    {
        // [THEN] Every AL assertion above passed — the virtual tables still answer truthfully
        // for this app's own objects, and still refuse an id it does not declare, after the
        // boundary restores below.
        Assert.Equal(0, exitCode);
        Assert.Contains("4P/0F/0E", output);

        // [THEN] The capture NAMED the tables it left out, and the set covers every one the
        // fixture's Install trigger materialised. A capture that silently kept one of them
        // would report a shorter list here, not merely a bigger row count.
        var captures = CaptureLine.Matches(output);
        Assert.True(captures.Count > 0,
            $"expected at least one InstallBaseline.Capture marker naming its skipped tables, got:\n{output}");
        var lastSkipped = captures[^1].Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToHashSet();
        foreach (var id in ExpectedSkippedIds)
            Assert.True(lastSkipped.Contains(id),
                $"table {id} was materialised by the fixture's Install trigger but the "
                + $"install-baseline capture did not report skipping it (skipped: "
                + $"{string.Join(",", lastSkipped.OrderBy(i => i))}) in:\n{output}");

        // [THEN] And the cost the issue is about is gone: no boundary re-inserted the tens of
        // thousands of rows those tables contribute. Asserted over EVERY restore, not just
        // the first — the whole point is that this repeats at every boundary.
        var restores = RestoreLine.Matches(output);
        Assert.True(restores.Count > 0,
            $"expected at least one InstallBaseline.Restore marker (no boundary restore "
            + $"happened, so this test asserted nothing) in:\n{output}");
        foreach (Match m in restores)
        {
            var rows = int.Parse(m.Groups[1].Value);
            Assert.True(rows <= MaxRestoredRowsPerBoundary,
                $"a boundary restored {rows} rows, above the {MaxRestoredRowsPerBoundary} ceiling — "
                + $"the self-populating virtual tables are back in the install baseline. Output:\n{output}");
        }
    }

    /// <summary>Default isolation (codeunit): a restore runs at every codeunit boundary.</summary>
    [SkippableFact]
    public void CodeunitBoundary_RestoresWithoutSelfPopulatingVirtualTables_AndTheyStillAnswer()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-2272-codeunit", Guid.NewGuid().ToString("N"));
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var (output, exitCode) = RunRunner(Array.Empty<string>(), app);
            AssertBaselineExcludesVirtualTablesAndAlPasses(output, exitCode);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>TestIsolation.Test: a restore runs at every TEST boundary, so the same
    /// per-boundary cost is paid once per test rather than once per codeunit. This is the
    /// multiplier the issue names, and it is a separate code path in TestExecutor from the
    /// codeunit one above.</summary>
    [SkippableFact]
    public void TestBoundary_RestoresWithoutSelfPopulatingVirtualTables_AndTheyStillAnswer()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-2272-test", Guid.NewGuid().ToString("N"));
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var (output, exitCode) = RunRunner(new[] { "--isolation", "test" }, app);
            AssertBaselineExcludesVirtualTablesAndAlPasses(output, exitCode);

            // [THEN] Under TestIsolation.Test every one of the four tests got its own restore
            // — otherwise the loop above would have asserted the ceiling over the two
            // codeunit-boundary restores and called that the per-test path.
            Assert.True(RestoreLine.Matches(output).Count >= 4,
                $"expected at least one baseline restore per test under --isolation test, got "
                + $"{RestoreLine.Matches(output).Count} in:\n{output}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
