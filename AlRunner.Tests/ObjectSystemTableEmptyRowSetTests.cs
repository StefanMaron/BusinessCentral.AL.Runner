using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #3071 — the Object system table (2000000001) holds NO rows in the runner, because a
/// real service tier holds none, and it must go on ANSWERING while it holds none.
///
/// <para>WHAT THIS TEST USED TO BE. It was ObjectSystemTableBaselineExclusionTests, for #2875:
/// the runner projected its own object inventory into 2000000001, an install-baseline restore
/// replayed that projection into a brand-new provider, and the projection could no longer tell
/// its own stale output from a --test-data backup's rows. The fix of the day was to keep the
/// projection out of the baseline entirely. Corpus codeunit 61202
/// (StefanMaron/BusinessCentral.AL.Language.Tests#197) has since measured the table on seven BC
/// OnPrem legs and found it empty, so the projection is gone and so is the ambiguity — the only
/// rows this table can now hold are a backup's, which a baseline SHOULD carry.</para>
///
/// <para>SO BOTH CLAIMS ARE INVERTED, deliberately, and this file keeps the fixture that made
/// them checkable. The install trigger still touches <c>Record "Object"</c> inside the capture
/// window, so "what did the capture do with 2000000001?" stays a deterministic question on
/// every BC version rather than one that depends on whether some dependency's Install trigger
/// happened to read the table.</para>
///
/// <list type="number">
/// <item>The capture must NOT report 2000000001 among the tables it skipped. It is an ordinary
/// application-database table again, handled like its sibling 2000000071.</item>
/// <item>The table must answer EMPTY on all four DataAccess request paths — keyed Get, find,
/// count and IsEmpty (which RecordImplementation.IsEmptyAsync serves from its own ExistsAsync
/// rather than from CountAsync) — across a codeunit boundary and a test boundary. Empty, not
/// refusing: #2519 is the trap where a table is emptied by throwing at row-build time, which
/// takes out all four paths at once and would ERROR here rather than FAIL.</item>
/// </list>
///
/// <para>THE FIXTURE'S OWN OBJECTS ARE THE PROVING ARM. A table, a page and a codeunit this app
/// group publishes moments earlier are each asked for by name and must be ABSENT — that is what
/// fails if the projection is ever reinstated, and an id the fixture deliberately does not
/// declare cannot distinguish the two. AllObj is read in the same session as the control: it is
/// projected from the very inventory Object's rows used to come from, so it lists these objects
/// and proves the emptiness is a policy for this one table rather than a run with no
/// inventory.</para>
///
/// <para>The fixture also seeds an ordinary row from the same Install trigger and asserts it
/// survives every boundary. That is the second control: it fails if the change broke the
/// install baseline generally rather than changing what goes into it.</para>
///
/// <para>Spawns the real runner; needs the BC artifact cache. Skips (loudly) when absent.</para>
/// </summary>
public class ObjectSystemTableEmptyRowSetTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>The literal id, not RecordPatches.ObjectSystemTableId: a test that reads the
    /// same constant the implementation does cannot notice that constant changing.</summary>
    private const int ObjectSystemTableId = 2000000001;

    /// <summary>AllObj — a genuinely self-populating virtual table, so the capture must go on
    /// skipping it. The fixture touches it inside the capture window so one marker carries both
    /// halves of the claim.</summary>
    private const int AllObjVirtualTableId = 2000000038;

    private const int BaseId = 62760;
    private const int TableId = BaseId + 0;
    private const int PageId = BaseId + 1;
    private const int TestsAId = BaseId + 2;
    private const int TestsBId = BaseId + 3;
    private const int AssertId = BaseId + 4;
    private const int InstallId = BaseId + 5;

    /// <summary>An id inside the fixture's own idRange that the fixture deliberately does not
    /// declare. It has to come from the fixture's OWN range: the runner accumulates its object
    /// inventory across every app group in a process, so an id borrowed from anywhere else
    /// could be claimed by something and the negative arms would fail for an unrelated
    /// reason.</summary>
    private const int NeverDeclaredId = BaseId + 19;

    private static (string output, int exit) RunRunner(string[] extraArgs, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
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
                // The capture marker is the claim. On a machine with a warm on-disk dep+company
                // baseline the dependency capture is skipped entirely, so force it to be
                // recomputed rather than measuring the cache.
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

    /// <summary>Writes the fixture. `target: OnPrem` is mandatory: Microsoft declares Object
    /// Scope = OnPrem, so a Cloud-target app fails AL0296 on `Record "Object"`. No
    /// "application" property — see .claude/rules/no-base-app-in-csharp-tests.md.</summary>
    private static void WriteFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "IT2875 Object Baseline",
          "publisher": "IssueTest2875",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{BaseId}}, "to": {{BaseId + 19}} } ],
          "runtime": "14.0",
          "target": "OnPrem"
        }
        """);

        var al = FixtureAlTemplate
            .Replace("$TABLE$", TableId.ToString())
            .Replace("$PAGE$", PageId.ToString())
            .Replace("$TESTSA$", TestsAId.ToString())
            .Replace("$TESTSB$", TestsBId.ToString())
            .Replace("$ASSERT$", AssertId.ToString())
            .Replace("$INSTALL$", InstallId.ToString())
            .Replace("$ABSENT$", NeverDeclaredId.ToString());
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), al);
    }

    private const string FixtureAlTemplate = """
        table $TABLE$ "IT2875 Widget"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Description; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page $PAGE$ "IT2875 Widget Card"
        {
            PageType = Card;
            SourceTable = "IT2875 Widget";
            layout
            {
                area(content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                    field(Description; Rec.Description) { ApplicationArea = All; }
                }
            }
        }

        codeunit $INSTALL$ "IT2875 Install"
        {
            Subtype = Install;

            // Two things, both before CaptureInstallBaseline() runs.
            //
            // Three things, all before CaptureInstallBaseline() runs.
            //
            // 1. Touch Object, so its store is MATERIALISED inside the capture window. That is
            //    what makes "what did the capture do with 2000000001?" a deterministic question
            //    on every BC version, rather than one that depends on whether a dependency
            //    Install trigger or Company-Initialize happened to read the table. The touch
            //    finds nothing now (#3071) -- the point is that the store exists, not that it
            //    has rows.
            // 2. Touch AllObj too, and for the same reason, but to the opposite end. AllObj IS
            //    a self-populating virtual table, so the capture must skip it -- which turns
            //    the capture marker into a DIFFERENTIAL rather than an absence: one marker,
            //    naming 2000000038 among the tables it left out and NOT naming 2000000001.
            //    Without this the skip list is empty in this fixture, and "Object is not in the
            //    skip list" is satisfied by a marker that skipped nothing at all.
            // 3. Seed an ordinary row. That is the control for the whole change: if the
            //    install baseline stopped working generally rather than changing what goes
            //    into it, the seed stops surviving the boundary and every test below fails.
            trigger OnInstallAppPerCompany()
            var
                Obj: Record "Object";
                AllObjRec: Record AllObj;
                Widget: Record "IT2875 Widget";
            begin
                Obj.SetRange(Type, Obj.Type::Table);
                if Obj.FindFirst() then;

                AllObjRec.SetRange("Object Type", AllObjRec."Object Type"::Table);
                if AllObjRec.FindFirst() then;

                Widget.Init();
                Widget."No." := 'SEED';
                Widget.Description := 'seeded by the install trigger';
                Widget.Insert();
            end;
        }

        codeunit $ASSERT$ "IT2875 Assert"
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

            // Every claim names a concrete object THIS app group declares, published moments
            // before these lines run -- a table, a page and a codeunit, so a partial
            // reinstatement of the projection (one kind only, or one option ordinal for
            // everything) fails here rather than slipping through. An id the fixture does not
            // declare could not tell a removed projection from a present one.
            //
            // AllObj is the CONTROL, read in the same session: it is projected from the very
            // EnumerateKnownAlObjects inventory Object's rows used to be projected from, so it
            // lists these same objects. Object empty WHILE AllObj is full is what makes the
            // emptiness a policy for this one table rather than a run with no inventory --
            // the same shape as corpus 61202's control arm.
            //
            // All FOUR DataAccess request paths are exercised, not just find. IsEmpty() is not
            // a spelling of Count(): RecordImplementation.IsEmptyAsync calls its own
            // ExistsAsync, so a change that handles find and count and forgets IsEmpty is green
            // until somebody writes this line. And every line here must ANSWER: a table emptied
            // by throwing at row-build time (#2519) would ERROR rather than FAIL, and takes out
            // all four paths together.
            procedure CheckObjectTable()
            var
                Obj: Record "Object";
                AllObjRec: Record AllObj;
                Widget: Record "IT2875 Widget";
            begin
                // ── CONTROL: the object inventory this run has, and still projects ───────
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Table, $TABLE$), 'CONTROL: AllObj must list table $TABLE$');
                AreEqual('IT2875 Widget', AllObjRec."Object Name", 'CONTROL: AllObj name for table $TABLE$');
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Codeunit, $ASSERT$), 'CONTROL: AllObj must list codeunit $ASSERT$');
                IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Page, $PAGE$), 'CONTROL: AllObj must list page $PAGE$');
                IsFalse(AllObjRec.Get(AllObjRec."Object Type"::Table, $ABSENT$), 'CONTROL: AllObj must NOT list table $ABSENT$');

                // ── keyed Get (InternalTryGetByPrimaryKeyAsync) ──────────────────────────
                IsFalse(Obj.Get(Obj.Type::Table, '', $TABLE$), 'Object must NOT list table $TABLE$');
                IsFalse(Obj.Get(Obj.Type::Codeunit, '', $ASSERT$), 'Object must NOT list codeunit $ASSERT$');
                IsFalse(Obj.Get(Obj.Type::Page, '', $PAGE$), 'Object must NOT list page $PAGE$');
                IsFalse(Obj.Get(Obj.Type::Table, '', $ABSENT$), 'Object must NOT list table $ABSENT$');

                // ── find (InnerFindAsync), filtered and unfiltered ───────────────────────
                Obj.Reset();
                Obj.SetRange(Type, Obj.Type::Table);
                Obj.SetRange(ID, $TABLE$);
                IsFalse(Obj.FindSet(), 'Object.FindSet must find nothing for table $TABLE$');

                // ── count (CountAsync) ───────────────────────────────────────────────────
                AreEqual('0', Format(Obj.Count()), 'Object.Count for table $TABLE$');

                // ── IsEmpty (ExistsAsync — a fourth path, not a spelling of Count) ───────
                IsTrue(Obj.IsEmpty(), 'Object.IsEmpty must be true for table $TABLE$');

                // The same three non-Get paths with no filter at all, so the emptiness is not
                // an artifact of the filter above.
                Obj.Reset();
                IsFalse(Obj.FindSet(), 'Object.FindSet must find nothing on the unfiltered table');
                AreEqual('0', Format(Obj.Count()), 'Object.Count on the unfiltered table');
                IsTrue(Obj.IsEmpty(), 'Object.IsEmpty must be true on the unfiltered table');

                // ── the control: ordinary install-seeded state still survives the boundary ─
                IsTrue(Widget.Get('SEED'), 'the install-seeded Widget row must survive the boundary restore');
                AreEqual('seeded by the install trigger', Widget.Description, 'seeded Widget description');
            end;
        }

        codeunit $TESTSA$ "IT2875 Tests A"
        {
            Subtype = Test;

            [Test]
            procedure ObjectAnswersAcrossBoundaryA1()
            var
                IT2875Assert: Codeunit "IT2875 Assert";
            begin
                IT2875Assert.CheckObjectTable();
            end;

            [Test]
            procedure ObjectAnswersAcrossBoundaryA2()
            var
                IT2875Assert: Codeunit "IT2875 Assert";
            begin
                IT2875Assert.CheckObjectTable();
            end;
        }

        // Symmetric with A, sharing one body: codeunit execution order is not id order, so a
        // fixture whose proof depends on which of the two runs first proves nothing.
        codeunit $TESTSB$ "IT2875 Tests B"
        {
            Subtype = Test;

            [Test]
            procedure ObjectAnswersAcrossBoundaryB1()
            var
                IT2875Assert: Codeunit "IT2875 Assert";
            begin
                IT2875Assert.CheckObjectTable();
            end;

            [Test]
            procedure ObjectAnswersAcrossBoundaryB2()
            var
                IT2875Assert: Codeunit "IT2875 Assert";
            begin
                IT2875Assert.CheckObjectTable();
            end;
        }
        """;

    private static readonly Regex CaptureLine = new(
        @"InstallBaseline\.Capture .* skipped-self-populating \[([0-9,]*)\]", RegexOptions.Compiled);
    private static readonly Regex RestoreLine = new(
        @"InstallBaseline\.Restore (\d+) row\(s\)", RegexOptions.Compiled);

    private static void AssertObjectIsCapturedNormallyAndAnswersEmpty(string output, int exitCode)
    {
        // [THEN] Every AL assertion passed — after the restores below, Object answers EMPTY on
        // all four request paths for objects this app group publishes, AllObj still lists those
        // same objects, and the ordinary install-seeded row still survives.
        Assert.Equal(0, exitCode);
        Assert.Contains("4P/0F/0E", output);

        // [THEN] The capture did NOT name 2000000001 among the tables it left out. This is the
        // inverted claim (#3071): with no projection to replay, the table is an ordinary
        // application-database table and belongs in the baseline like its sibling 2000000071 —
        // where, if a --test-data backup ever fills it, its real rows must survive the
        // boundary. The marker names ids rather than counting them, so this cannot be satisfied
        // by "it skipped a different number of tables".
        //
        // The LAST marker is the per-app-group CaptureInstallBaseline(), which runs after the
        // fixture's own Install trigger — i.e. after the touch that materialised the store.
        // That is the capture whose output a codeunit/test boundary restores.
        var captures = CaptureLine.Matches(output);
        Assert.True(captures.Count > 0,
            $"expected at least one InstallBaseline.Capture marker naming its skipped tables, got:\n{output}");
        var lastSkipped = captures[^1].Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToHashSet();
        Assert.False(lastSkipped.Contains(ObjectSystemTableId),
            $"the install-baseline capture reported skipping table {ObjectSystemTableId} (Object), but "
            + $"nothing projects rows into it any more (#3071) — a real service tier measured the legacy "
            + $"registry as empty, so its only possible writer is a --test-data backup and its rows must "
            + $"be carried across a boundary like any other table's (skipped: "
            + $"{string.Join(",", lastSkipped.OrderBy(i => i))}). Output:\n{output}");

        // [THEN] ...and the SAME marker did skip AllObj (2000000038), which the fixture's
        // Install trigger touches for exactly this purpose. That is what makes the assertion
        // above a differential rather than an absence: a marker that skipped nothing at all
        // would satisfy "Object is not in the skip list" while proving nothing, and #2272's
        // refusal for the genuinely self-populating tables is what must NOT have been loosened
        // on the way to dropping Object's disjunct from it.
        Assert.True(lastSkipped.Contains(AllObjVirtualTableId),
            $"expected the capture to still skip AllObj ({AllObjVirtualTableId}) as a self-populating "
            + $"virtual table (#2272) — the fixture's Install trigger touches it so this marker can "
            + $"show BOTH halves of the claim (skipped: {string.Join(",", lastSkipped.OrderBy(i => i))}). "
            + $"Output:\n{output}");

        // [THEN] A boundary restore actually happened, so the assertions above were made on
        // the far side of one rather than on the install-time store.
        Assert.True(RestoreLine.Matches(output).Count > 0,
            $"expected at least one InstallBaseline.Restore marker (no boundary restore happened, "
            + $"so this test asserted nothing) in:\n{output}");
    }

    /// <summary>Default isolation (codeunit): a restore runs at every codeunit boundary.</summary>
    [SkippableFact]
    public void CodeunitBoundary_ObjectIsCapturedNormally_AndAnswersEmpty()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-3071-codeunit");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var (output, exitCode) = RunRunner(Array.Empty<string>(), app);
            AssertObjectIsCapturedNormallyAndAnswersEmpty(output, exitCode);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>TestIsolation.Test: a restore runs at every TEST boundary — a separate code
    /// path in TestExecutor, and the one that replays the baseline most often.</summary>
    [SkippableFact]
    public void TestBoundary_ObjectIsCapturedNormally_AndAnswersEmpty()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-3071-test");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var (output, exitCode) = RunRunner(new[] { "--isolation", "test" }, app);
            AssertObjectIsCapturedNormallyAndAnswersEmpty(output, exitCode);

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
