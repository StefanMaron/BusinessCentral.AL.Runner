using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2875 — the Object system table (2000000001) could not tell a --test-data backup's
/// rows from its OWN projection replayed back into a fresh provider by an install-baseline
/// restore.
///
/// <para>The latch in PopulateObjectSystemTable asked <c>ProviderHasAnyRow</c>, and that
/// question cannot distinguish the two writers. A boundary restore builds a BRAND-NEW
/// in-memory provider, which gets a fresh ConditionalWeakTable entry, so rows the projection
/// itself wrote before the capture read back as "somebody else owns this table" and the
/// top-up never ran again for that provider. #2842 narrowed it to runs that have a
/// --test-data loader at all; the residue was --test-data plus an install baseline.</para>
///
/// <para>The fix removes the ambiguity at its source rather than guessing better: the
/// projection's rows are never put in a baseline in the first place, exactly like the
/// self-populating virtual tables of #2272, so the only rows a restored provider can hold for
/// this table are a backup's. Provenance is recorded by the one other writer —
/// TestDataProvisioner's on-demand load — so "the backup owns this table" is a fact the
/// runner has measured, not one it infers from row presence.</para>
///
/// <para>WHAT THIS TEST CAN AND CANNOT REACH. The exclusion half is drivable in CI with no
/// backup at all: a fixture whose Install trigger touches <c>Record "Object"</c> materialises
/// the projection inside the capture window, and the capture must then report 2000000001 as
/// left out. The --test-data half is not — CI has no BC database backup — so the provenance
/// switch itself is pinned by ObjectSystemTableRowProvenanceTests, which drives the same two
/// writers directly.</para>
///
/// <para>Both halves of the #2272 standard are asserted here, because either alone is
/// worthless: the table is GONE from the baseline (from the capture marker, which names the
/// ids it skipped), and it still ANSWERS truthfully afterwards — across a codeunit boundary
/// and a test boundary, on all four DataAccess request paths (find, count, keyed Get and
/// IsEmpty, which RecordImplementation.IsEmptyAsync serves from its own ExistsAsync rather
/// than from CountAsync), positively for objects the fixture declares and negatively for an
/// id it does not.</para>
///
/// <para>The fixture also seeds an ordinary row from the same Install trigger and asserts it
/// survives every boundary. That is the control: it fails if the change broke the install
/// baseline generally rather than narrowing what goes into it.</para>
///
/// <para>Spawns the real runner; needs the BC artifact cache. Skips (loudly) when absent.</para>
/// </summary>
public class ObjectSystemTableBaselineExclusionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>The literal id, not RecordPatches.ObjectSystemTableId: a test that reads the
    /// same constant the implementation does cannot notice that constant changing.</summary>
    private const int ObjectSystemTableId = 2000000001;

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
            // 1. Touch Object, so the projection materialises INSIDE the capture window. That
            //    is what makes "was 2000000001 in the baseline?" a deterministic question on
            //    every BC version, rather than one that depends on whether a dependency
            //    Install trigger or Company-Initialize happened to read the table.
            // 2. Seed an ordinary row. That is the control for the whole change: if the
            //    install baseline stopped working generally rather than getting narrower, the
            //    seed stops surviving the boundary and every test below fails on it.
            trigger OnInstallAppPerCompany()
            var
                Obj: Record "Object";
                Widget: Record "IT2875 Widget";
            begin
                Obj.SetRange(Type, Obj.Type::Table);
                if Obj.FindFirst() then;

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

            // Every claim is a concrete value for a concrete object THIS app group declares,
            // and every one has a negative twin against $ABSENT$ — an id in the fixture's own
            // range that it deliberately does not declare. An empty table fails the positive
            // arms; a table answering for an object that does not exist fails the negative
            // ones. Both are needed: after the restore stops carrying this table, "it is gone
            // from the baseline" and "it still answers" have to be true at the same time.
            //
            // All FOUR DataAccess request paths are exercised, not just find. IsEmpty() is not
            // a spelling of Count(): RecordImplementation.IsEmptyAsync calls its own
            // ExistsAsync, so a fix that repopulates on find and count and forgets IsEmpty is
            // green until somebody writes this line.
            procedure CheckObjectTable()
            var
                Obj: Record "Object";
                Widget: Record "IT2875 Widget";
            begin
                // ── keyed Get (InternalTryGetByPrimaryKeyAsync) ──────────────────────────
                IsTrue(Obj.Get(Obj.Type::Table, '', $TABLE$), 'Object must list table $TABLE$');
                AreEqual('IT2875 Widget', Obj.Name, 'Object.Name for table $TABLE$');
                IsTrue(Obj.Get(Obj.Type::Codeunit, '', $ASSERT$), 'Object must list codeunit $ASSERT$');
                AreEqual('IT2875 Assert', Obj.Name, 'Object.Name for codeunit $ASSERT$');
                IsTrue(Obj.Get(Obj.Type::Page, '', $PAGE$), 'Object must list page $PAGE$');
                AreEqual('IT2875 Widget Card', Obj.Name, 'Object.Name for page $PAGE$');
                IsFalse(Obj.Get(Obj.Type::Table, '', $ABSENT$), 'Object must NOT list table $ABSENT$');
                IsFalse(Obj.Get(Obj.Type::Table, '', $ASSERT$), 'Object must NOT list $ASSERT$ as a table');

                // ── find (InnerFindAsync) ────────────────────────────────────────────────
                Obj.Reset();
                Obj.SetRange(Type, Obj.Type::Table);
                Obj.SetRange(ID, $TABLE$);
                IsTrue(Obj.FindSet(), 'Object.FindSet must return table $TABLE$');
                AreEqual('IT2875 Widget', Obj.Name, 'Object.FindSet name for table $TABLE$');

                // ── count (CountAsync) ───────────────────────────────────────────────────
                AreEqual('1', Format(Obj.Count()), 'Object.Count for table $TABLE$');

                // ── IsEmpty (ExistsAsync — a fourth path, not a spelling of Count) ───────
                IsFalse(Obj.IsEmpty(), 'Object.IsEmpty must be false for table $TABLE$');

                // Negative twins on the same three non-Get paths.
                Obj.Reset();
                Obj.SetRange(Type, Obj.Type::Table);
                Obj.SetRange(ID, $ABSENT$);
                IsFalse(Obj.FindSet(), 'Object.FindSet must find nothing for table $ABSENT$');
                AreEqual('0', Format(Obj.Count()), 'Object.Count for table $ABSENT$');
                IsTrue(Obj.IsEmpty(), 'Object.IsEmpty must be true for table $ABSENT$');

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

    private static void AssertObjectExcludedAndStillAnswers(string output, int exitCode)
    {
        // [THEN] Every AL assertion passed — Object still answers on all four request paths
        // after the restores below, and the ordinary install-seeded row still survives them.
        Assert.Equal(0, exitCode);
        Assert.Contains("4P/0F/0E", output);

        // [THEN] The capture NAMED 2000000001 among the tables it left out. The marker names
        // ids rather than counting them precisely so "skipped Object" cannot be confused with
        // "skipped something else".
        //
        // The LAST marker is the per-app-group CaptureInstallBaseline(), which runs after the
        // fixture's own Install trigger — i.e. after the touch that materialised the
        // projection. That is the capture whose output a codeunit/test boundary restores.
        var captures = CaptureLine.Matches(output);
        Assert.True(captures.Count > 0,
            $"expected at least one InstallBaseline.Capture marker naming its skipped tables, got:\n{output}");
        var lastSkipped = captures[^1].Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToHashSet();
        Assert.True(lastSkipped.Contains(ObjectSystemTableId),
            $"the fixture's Install trigger materialised table {ObjectSystemTableId} (Object), but the "
            + $"install-baseline capture did not report skipping it (skipped: "
            + $"{string.Join(",", lastSkipped.OrderBy(i => i))}). A projection captured into the "
            + $"baseline is replayed into a fresh provider at the next boundary, which is the row "
            + $"set #2875 says the runner cannot tell from a backup's. Output:\n{output}");

        // [THEN] A boundary restore actually happened, so the assertions above were made on
        // the far side of one rather than on the install-time store.
        Assert.True(RestoreLine.Matches(output).Count > 0,
            $"expected at least one InstallBaseline.Restore marker (no boundary restore happened, "
            + $"so this test asserted nothing) in:\n{output}");
    }

    /// <summary>Default isolation (codeunit): a restore runs at every codeunit boundary.</summary>
    [SkippableFact]
    public void CodeunitBoundary_BaselineOmitsObjectProjection_AndObjectStillAnswers()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-2875-codeunit");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var (output, exitCode) = RunRunner(Array.Empty<string>(), app);
            AssertObjectExcludedAndStillAnswers(output, exitCode);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>TestIsolation.Test: a restore runs at every TEST boundary — a separate code
    /// path in TestExecutor, and the one that replays the baseline most often.</summary>
    [SkippableFact]
    public void TestBoundary_BaselineOmitsObjectProjection_AndObjectStillAnswers()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-2875-test");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app);
            var (output, exitCode) = RunRunner(new[] { "--isolation", "test" }, app);
            AssertObjectExcludedAndStillAnswers(output, exitCode);

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
