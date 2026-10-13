// ExcludedObjectTriageSetTests — #5557 follow-up. The per-object verdicts are combined into one
// decision for the module (TriageDrops.EveryDropSafe), and one rule is about the SET: the profile
// carve-out (#2238) holds only when EVERY dropped object is a profile. Seeing a real
// `profile "X"` (it has no id, which the old text scan needed) must not relax that. Each shape is
// asserted against the merge base's verdicts too, printed by the probe below.
//
// Runner-specific: the triage is the runner's own decision. In-process, BC's parser only.
using AlRunner;
using Xunit;
using Xunit.Abstractions;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ExcludedObjectTriageSetTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = TestScratch.Dir("al-runner-excl-triage-set-" + Guid.NewGuid().ToString("N")[..8]);

    public ExcludedObjectTriageSetTests(BcEngineFixture engine, ITestOutputHelper output)
    {
        _engine = engine;
        _out = output;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Healthy.al"), """
            codeunit 62361 "Set Healthy"
            {
                Subtype = Test;
                [Test] procedure A() begin end;
            }
            """);
        File.WriteAllText(Path.Combine(_dir, "P1.al"), "profile \"P One\"\n{\n    Caption = 'P One';\n    RoleCenter = \"Nope\";\n}\n");
        File.WriteAllText(Path.Combine(_dir, "P2.al"), "profile \"P Two\"\n{\n    Caption = 'P Two';\n    RoleCenter = \"Nope\";\n}\n");
        File.WriteAllText(Path.Combine(_dir, "PId.al"), "profile 135209 \"Id Profile\"\n{\n    Caption = 'x';\n}\n");
        File.WriteAllText(Path.Combine(_dir, "BrokenTest.al"), """
            codeunit 62362 "Set Broken Test"
            {
                Subtype = Test;
                [Test] procedure B() begin end;
            }
            """);
        File.WriteAllText(Path.Combine(_dir, "BrokenLib.al"), "codeunit 62363 \"Set Broken Lib\"\n{\n    procedure H() begin end;\n}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    /// <summary>(shape name, excluded files, expected EveryDropSafe, expected per-file Droppable, merge-base EveryDropSafe)</summary>
    public static IEnumerable<object[]> Shapes() => new[]
    {
        new object[] { "profile only", new[] { "P1" }, true, new[] { true }, false },
        new object[] { "two profiles", new[] { "P1", "P2" }, true, new[] { true, true }, false },
        new object[] { "id-bearing profile only", new[] { "PId" }, true, new[] { true }, true },
        new object[] { "test codeunit only", new[] { "BrokenTest" }, true, new[] { true }, true },
        new object[] { "profile + test codeunit", new[] { "P1", "BrokenTest" }, false, new[] { false, true }, false },
        new object[] { "id-bearing profile + test codeunit", new[] { "PId", "BrokenTest" }, false, new[] { false, true }, true },
        new object[] { "two profiles + test codeunit", new[] { "P1", "P2", "BrokenTest" }, false, new[] { false, false, true }, false },
        new object[] { "profile + non-test codeunit", new[] { "P1", "BrokenLib" }, false, new[] { false, false }, false },
        new object[] { "non-test codeunit only", new[] { "BrokenLib" }, false, new[] { false }, false },
    };

    [SkippableTheory]
    [MemberData(nameof(Shapes))]
    public void EverySetShape_HasTheVerdictTheDecisionTableStates(
        string shape, string[] files, bool everyDropSafe, bool[] droppable, bool mergeBaseEveryDropSafe)
    {
        RequireEngine();
        var paths = files.Select(f => Path.Combine(_dir, f + ".al")).ToList();
        var details = paths.Select(p => new TddExcludedObjectDetail(p, Path.GetFileNameWithoutExtension(p), new[] { "error" })).ToList();

        var (verdicts, safe) = ProgramSupport.ExcludedObjectTriage.TriageDrops(
            files.ToList(), details, Directory.GetFiles(_dir, "*.al"));

        _out.WriteLine($"SHAPE {shape}: safe={safe} droppable=[{string.Join(",", verdicts.Select(v => v.Droppable))}] mergeBaseSafe={mergeBaseEveryDropSafe}");
        Assert.Equal(everyDropSafe, safe);
        Assert.Equal(droppable, verdicts.Select(v => v.Droppable).ToArray());
    }
}
