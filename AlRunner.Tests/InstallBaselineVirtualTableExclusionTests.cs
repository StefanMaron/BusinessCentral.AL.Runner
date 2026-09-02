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
        => RunRunner(extraArgs, freshDepCompanyBaseline: true, bundles);

    /// <param name="freshDepCompanyBaseline">Sets AL_RUNNER_NO_DEP_COMPANY_CACHE=1. On for the
    /// single-app-group tests, where a warm on-disk baseline would already omit these tables
    /// and the run would measure the cache instead of the change. OFF for the app-group test,
    /// which needs the second app group to take a cache HIT — restoring a snapshot at the
    /// app-group boundary is the code path under test there, and the kill switch would make
    /// every app group recompute instead of restore.</param>
    private static (string output, int exit) RunRunner(
        string[] extraArgs, bool freshDepCompanyBaseline, params string[] bundles)
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
            Environment = { ["AL_RUNNER_PERF"] = "1" },
        };
        if (freshDepCompanyBaseline)
            psi.Environment["AL_RUNNER_NO_DEP_COMPANY_CACHE"] = "1";
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

    /// <summary>Writes one app group. Parameterised by id base and tag so a SECOND, disjoint
    /// app group can be generated for the app-group-boundary test — AL resolves objects by
    /// name, so the names have to differ too, not just the ids.
    ///
    /// Layout, relative to <paramref name="baseId"/>: +0 table, +1 page, +2 and +3 the two
    /// symmetric test codeunits, +4 the assert helper, +5 the Install codeunit.</summary>
    private static void WriteFixture(string dir, int baseId, string tag)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "IT2272 Virtual Table Baseline {{tag}}",
          "publisher": "IssueTest2272",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 19}} } ],
          "runtime": "14.0"
        }
        """);

        var al = FixtureAlTemplate
            .Replace("$TABLE$", (baseId + 0).ToString())
            .Replace("$PAGE$", (baseId + 1).ToString())
            .Replace("$TESTSA$", (baseId + 2).ToString())
            .Replace("$TESTSB$", (baseId + 3).ToString())
            .Replace("$ASSERT$", (baseId + 4).ToString())
            .Replace("$INSTALL$", (baseId + 5).ToString())
            .Replace("$ABSENT$", NeverDeclaredId.ToString())
            .Replace("$TAG$", tag);
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), al);
    }

    /// <summary>An id no app group in any of these runs declares anything at, and which is
    /// outside every fixture idRange. The negative half of every assertion below rests on
    /// it, so it must stay outside the ranges if a fixture ever grows.</summary>
    private const int NeverDeclaredId = 62599;

    private const string FixtureAlTemplate = """
        table $TABLE$ "IT2272 $TAG$ Widget"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Description; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page $PAGE$ "IT2272 $TAG$ Widget Card"
        {
            PageType = Card;
            SourceTable = "IT2272 $TAG$ Widget";
            layout
            {
                area(content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                    field(Description; Rec.Description) { ApplicationArea = All; }
                }
            }
        }

        codeunit $INSTALL$ "IT2272 $TAG$ Install"
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
                FieldRec.SetRange(TableNo, $TABLE$);
                if FieldRec.FindFirst() then;
                if TableMeta.FindFirst() then;
                if PageMeta.FindFirst() then;
            end;
        }

        codeunit $ASSERT$ "IT2272 $TAG$ Assert"
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

            // Every assertion is a concrete value for a concrete object THIS app group
            // declares, and every one has a negative twin against $ABSENT$ — an id nothing in
            // the run declares. A restore that left an empty table fails the positive half; a
            // top-up that silently does nothing fails it too; a table answering for an id that
            // does not exist fails the negative half. Deliberately says nothing about ANOTHER
            // app group's objects: those ARE visible here, on this branch and on main alike
            // (the parsed-object registries accumulate across bundles), which is a separate
            // pre-existing defect and not something this fixture should encode either way.
            procedure CheckVirtualTables()
            var
                AllObjRec: Record AllObj;
                AllObjCap: Record AllObjWithCaption;
                FieldRec: Record "Field";
                TableMeta: Record "Table Metadata";
                PageMeta: Record "Page Metadata";
                FieldNames: Text;
            begin
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Table, $TABLE$), 'AllObj must list table $TABLE$');
                AreEqual('IT2272 $TAG$ Widget', AllObjRec."Object Name", 'AllObj object name for table $TABLE$');
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Page, $PAGE$), 'AllObj must list page $PAGE$');
                AreEqual('IT2272 $TAG$ Widget Card', AllObjRec."Object Name", 'AllObj object name for page $PAGE$');
                IsFalse(AllObjRec.Get(AllObjRec."Object Type"::Table, $ABSENT$), 'AllObj must NOT list table $ABSENT$');

                IsTrue(AllObjCap.Get(AllObjCap."Object Type"::Table, $TABLE$), 'AllObjWithCaption must list table $TABLE$');
                AreEqual('IT2272 $TAG$ Widget', AllObjCap."Object Name", 'AllObjWithCaption object name for table $TABLE$');
                IsFalse(AllObjCap.Get(AllObjCap."Object Type"::Table, $ABSENT$), 'AllObjWithCaption must NOT list table $ABSENT$');

                FieldRec.SetRange(TableNo, $TABLE$);
                IsTrue(FieldRec.FindSet(), 'Field must have rows for table $TABLE$');
                repeat
                    FieldNames += FieldRec."Field Caption" + ';';
                until FieldRec.Next() = 0;
                IsTrue(StrPos(FieldNames, 'No.;') > 0, 'Field must list "No." for table $TABLE$, got ' + FieldNames);
                IsTrue(StrPos(FieldNames, 'Description;') > 0, 'Field must list Description for table $TABLE$, got ' + FieldNames);
                FieldRec.Reset();
                FieldRec.SetRange(TableNo, $ABSENT$);
                IsTrue(FieldRec.IsEmpty(), 'Field must have no rows for nonexistent table $ABSENT$');

                IsTrue(TableMeta.Get($TABLE$), 'Table Metadata must list $TABLE$');
                AreEqual('IT2272 $TAG$ Widget', TableMeta.Name, 'Table Metadata name for $TABLE$');
                IsFalse(TableMeta.Get($ABSENT$), 'Table Metadata must NOT list $ABSENT$');

                IsTrue(PageMeta.Get($PAGE$), 'Page Metadata must list $PAGE$');
                IsFalse(PageMeta.Get($ABSENT$), 'Page Metadata must NOT list $ABSENT$');
            end;
        }

        codeunit $TESTSA$ "IT2272 $TAG$ Tests A"
        {
            Subtype = Test;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryA1()
            var
                IT2272Assert: Codeunit "IT2272 $TAG$ Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryA2()
            var
                IT2272Assert: Codeunit "IT2272 $TAG$ Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;
        }

        // Symmetric with A, sharing one body: codeunit execution order is not id order, so a
        // fixture whose proof depends on which of the two runs first proves nothing.
        codeunit $TESTSB$ "IT2272 $TAG$ Tests B"
        {
            Subtype = Test;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryB1()
            var
                IT2272Assert: Codeunit "IT2272 $TAG$ Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;

            [Test]
            procedure VirtualTablesAnswerAcrossBoundaryB2()
            var
                IT2272Assert: Codeunit "IT2272 $TAG$ Assert";
            begin
                IT2272Assert.CheckVirtualTables();
            end;
        }
        """;

    private static readonly Regex CaptureLine = new(
        @"InstallBaseline\.Capture .* skipped-self-populating \[([0-9,]*)\]", RegexOptions.Compiled);
    private static readonly Regex RestoreLine = new(
        @"InstallBaseline\.Restore (\d+) row\(s\)", RegexOptions.Compiled);

    private static void AssertBaselineExcludesVirtualTablesAndAlPasses(
        string output, int exitCode, int expectedPassGroups = 1)
    {
        // [THEN] Every AL assertion above passed — the virtual tables still answer truthfully
        // for this app's own objects, and still refuse an id it does not declare, after the
        // boundary restores below.
        Assert.Equal(0, exitCode);
        Assert.True(CountOccurrences(output, "4P/0F/0E") >= expectedPassGroups,
            $"expected {expectedPassGroups} app group(s) reporting 4P/0F/0E, got:\n{output}");

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
            WriteFixture(app, 62500, "A");
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
            WriteFixture(app, 62500, "A");
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

    /// <summary>
    /// The app-group boundary — a THIRD restore call site, and the one the issue's
    /// cross-bundle question hangs on. Two app groups with the identical (empty) dependency
    /// closure: the first computes the dep+company baseline, the second takes a cache HIT and
    /// is restored from it (TestExecutor.Run's HIT branch calls
    /// RestoreInstallBaselineSnapshot directly, not via the codeunit-boundary path the other
    /// two tests exercise). The kill switch is deliberately NOT set here, because with it on
    /// there would be no restore at that boundary at all.
    ///
    /// Each app group asserts only its OWN objects, positively and negatively. It does not
    /// assert anything about the other app group's objects: those are visible in AllObj and
    /// Table Metadata here, and were equally visible before this change — measured on both,
    /// with byte-identical results — because the parsed-object registries accumulate across
    /// bundles in the CLI run loop and the top-up therefore reports the union. That is a
    /// separate pre-existing defect (tracked on its own), and encoding it here in either
    /// direction would be wrong.
    /// </summary>
    [SkippableFact]
    public void AppGroupBoundary_RestoredFromCachedBaselineWithoutVirtualTables_AndTheyStillAnswer()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-2272-appgroup", Guid.NewGuid().ToString("N"));
        try
        {
            var appA = Path.Combine(root, "app-a");
            var appB = Path.Combine(root, "app-b");
            WriteFixture(appA, 62500, "A");
            WriteFixture(appB, 62520, "B");

            var (output, exitCode) = RunRunner(
                Array.Empty<string>(), freshDepCompanyBaseline: false, appA, appB);

            // [THEN] Both app groups' four tests passed — each one's own objects resolve, by
            // name, in all five virtual tables, on the far side of the app-group boundary.
            Assert.Equal(0, exitCode);
            Assert.True(CountOccurrences(output, "4P/0F/0E") >= 2,
                $"expected both app groups to report 4P/0F/0E, got:\n{output}");

            // [THEN] The second app group really was RESTORED from the first's snapshot rather
            // than recomputing — without this the test would be two independent app groups
            // that never crossed the boundary it claims to cover.
            Assert.True(CountOccurrences(output, "InstallBaseline.DepCompanyCache HIT") >= 1,
                $"expected the second app group to take an in-memory dep+company cache HIT, got:\n{output}");

            // [THEN] And the snapshot it was restored from carried no virtual tables, at any
            // boundary, in either app group.
            AssertBaselineExcludesVirtualTablesAndAlPasses(output, exitCode, expectedPassGroups: 2);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
