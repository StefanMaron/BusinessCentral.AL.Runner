// ExcludedObjectTriageTests — issue #3476.
//
// The decision that used to be "profile or refuse", made per excluded object. These are unit
// tests over temp directories: no runner subprocess, no BC engine, so the whole file runs in
// well under a second. The end-to-end proof that the decision is wired up — a survivor that
// runs, a dropped test counted as skipped, a module refused because a survivor reaches the
// dropped object by id — is EmitExclusionLoudnessTests.
//
// Every case below is a fact about a real MS bucket. Tests-Misc drops
// `Azure Key Vault Module Test` and nothing in the bucket names it; Tests-Integration drops
// `Item From Picture Tests`, which declares a variable of its OWN type, so a triage that
// scanned its own file would refuse it and lose the bucket's 340 tests for nothing.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExcludedObjectTriageTests : IDisposable
{
    private readonly string _dir = TestScratch.Dir("al-runner-excl-triage-" + Guid.NewGuid().ToString("N")[..8]);

    public ExcludedObjectTriageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static TddExcludedObjectDetail Detail(string path, string label) =>
        new(path, label, new[] { "SourceFile(x@1:1): error AL0185: DotNet 'Missing' is missing" });

    private IReadOnlyList<ProgramSupport.ExcludedObjectVerdict> Triage(params string[] excludedPaths)
        => ProgramSupport.ExcludedObjectTriage.Triage(
            excludedPaths.Select(p => Detail(p, Path.GetFileNameWithoutExtension(p))).ToList(),
            Directory.GetFiles(_dir, "*.al"));

    private const string TestCodeunit = """
        codeunit 135209 "Azure Key Vault Module Test"
        {
            Subtype = Test;

            [Test]
            procedure TestOne()
            begin
            end;
        }
        """;

    [Fact]
    public void UnreferencedTestCodeunit_IsDroppable()
    {
        var broken = Write("Broken.Codeunit.al", TestCodeunit);
        Write("Other.Codeunit.al", """
            codeunit 135300 "Some Other Tests"
            {
                Subtype = Test;

                [Test]
                procedure Unrelated()
                begin
                end;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.True(v.Droppable, v.Reason);
        Assert.Empty(v.ReferencedBy);
        // Not just "true": the reason has to say what was established, because it is what the
        // run prints when it decides to keep going.
        Assert.Contains("no surviving source", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCodeunitNamedByASurvivor_IsNotDroppable_AndNamesTheFile()
    {
        var broken = Write("Broken.Codeunit.al", TestCodeunit);
        Write("Caller.Codeunit.al", """
            codeunit 135301 "Caller Tests"
            {
                Subtype = Test;

                var
                    Kv: Codeunit "Azure Key Vault Module Test";

                [Test]
                procedure Uses()
                begin
                end;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.False(v.Droppable);
        Assert.Equal("Caller.Codeunit.al", Path.GetFileName(Assert.Single(v.ReferencedBy)));
    }

    [Fact]
    public void TestCodeunitReachedByObjectId_IsNotDroppable()
    {
        // The case the compiler cannot catch: Codeunit.Run takes an Integer, so excluding the
        // callee does not make this file fail to bind, and BC's emit-retry loop leaves it in
        // the survivor set. Measured on Fixtures/EmitExclusion with `Codeunit.Run(60620)`:
        // 1 excluded object, not 2.
        var broken = Write("Broken.Codeunit.al", TestCodeunit);
        Write("Caller.Codeunit.al", """
            codeunit 135302 "Id Caller Tests"
            {
                Subtype = Test;

                [Test]
                procedure RunsById()
                begin
                    if Codeunit.Run(135209) then;
                end;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.False(v.Droppable);
        Assert.Equal("Caller.Codeunit.al", Path.GetFileName(Assert.Single(v.ReferencedBy)));
    }

    [Fact]
    public void AnIdThatMerelyLooksLikeTheObjectId_DoesNotCount()
    {
        // Negative direction for the id scan. Without the boundary guards, `1135209` and
        // `135209.5` would both match and refuse a module for nothing — and refusing is the
        // behaviour that costs 3,555 tests, so an over-eager pattern here is not free.
        var broken = Write("Broken.Codeunit.al", TestCodeunit);
        Write("Caller.Codeunit.al", """
            codeunit 135303 "Numeric Tests"
            {
                Subtype = Test;

                [Test]
                procedure Numbers()
                var
                    Big: Integer;
                    D: Decimal;
                begin
                    Big := 1135209;
                    D := 135209.5;
                end;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.True(v.Droppable, v.Reason);
    }

    [Fact]
    public void TheExcludedObjectsOwnSelfReference_DoesNotBlockIt()
    {
        // Real: Tests-Integration's `Item From Picture Tests` declares
        // `ItemFromPictureTests: Codeunit "Item From Picture Tests"` — the EventSubscriberInstance
        // = Manual pattern. The file is not a survivor, so it must not be scanned as one.
        var broken = Write("Broken.Codeunit.al", """
            codeunit 135215 "Item From Picture Tests"
            {
                Subtype = Test;
                EventSubscriberInstance = Manual;

                var
                    ItemFromPictureTests: Codeunit "Item From Picture Tests";

                [Test]
                procedure TestOne()
                begin
                end;
            }
            """);
        Write("Other.Codeunit.al", """
            codeunit 135304 "Unrelated Tests"
            {
                Subtype = Test;

                [Test]
                procedure Unrelated()
                begin
                end;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.True(v.Droppable, v.Reason);
    }

    [Fact]
    public void ACodeunitWithoutSubtypeTest_IsNotDroppable()
    {
        // A library codeunit, even an unreferenced one. Narrow on purpose: nothing here
        // establishes that a survivor cannot reach it through a route the scan cannot see.
        var broken = Write("Broken.Codeunit.al", """
            codeunit 135209 "Some Library"
            {
                procedure Helper()
                begin
                end;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.False(v.Droppable);
        Assert.Contains("Subtype = Test", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATable_IsNotDroppable()
    {
        var broken = Write("Broken.Table.al", """
            table 135209 "Some Table"
            {
                fields { field(1; "No."; Code[20]) { } }
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.False(v.Droppable);
        Assert.Contains("not a test codeunit", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfile_IsDroppable_WithoutAReferenceScan()
    {
        // #2238's carve-out, preserved. Asserted here so a later edit to the per-object path
        // cannot quietly take it away.
        var broken = Write("Broken.Profile.al", """
            profile 135209 "Some Profile"
            {
                Caption = 'Some Profile';
                RoleCenter = 9022;
            }
            """);

        var v = Assert.Single(Triage(broken));
        Assert.True(v.Droppable, v.Reason);
        Assert.Contains("profile", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreadableExcludedObject_IsNotDroppable()
    {
        var missing = Path.Combine(_dir, "GoneBeforeTriage.Codeunit.al");
        var v = Assert.Single(ProgramSupport.ExcludedObjectTriage.Triage(
            new[] { Detail(missing, "Codeunit .\"Gone\"") }, Array.Empty<string>()));
        Assert.False(v.Droppable);
        Assert.Contains("could not be re-read", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneBlockedObjectAmongDroppableOnes_IsReportedIndividually()
    {
        // The whole point of deciding per object rather than per module: the verdicts must not
        // collapse into a single answer for the set.
        var safe = Write("Safe.Codeunit.al", TestCodeunit);
        var blocked = Write("Blocked.Codeunit.al", """
            codeunit 135210 "A Library"
            {
                procedure Helper()
                begin
                end;
            }
            """);

        var verdicts = Triage(safe, blocked);
        Assert.Equal(2, verdicts.Count);
        Assert.True(verdicts[0].Droppable, verdicts[0].Reason);
        Assert.False(verdicts[1].Droppable);
    }
}
