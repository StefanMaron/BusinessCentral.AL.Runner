// BcCompilerIncrementalInterfaceDependentsTests — issue #5089.
//
// BC derives an interface's id from its shape (its procedures and its extends list, transitively),
// and folds that id into the C# of objects whose files were not edited: an implementer's
// IsInterfaceOfType/IsInterfaceMethod case labels, and a user's IsInterfaceOfType(id) and
// InvokeInterfaceMethod(id, …). Measured with the fixtures below: adding `extends "… Other"` to
// "… Greeter" changes the implementer's case list while only the interface file changed. The
// incremental fast path re-emitted only the interface (which has no C#), so the implementer kept
// its old interface list and `G is "… Other"` kept answering false.
//
// Each RED test asserts that the edit really moved the dependent's C# (a cold build differs from
// the baseline), so it cannot pass against an edit that changed nothing; the control asserts the
// guard is keyed on the shape, not on "an interface file changed".
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalInterfaceDependentsTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalInterfaceDependentsTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-iface-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Greeter = """
        interface "Incr Iface Greeter"
        {
            procedure Greet(): Text;
        }
        """;

    private const string GreeterExtendsOther = """
        interface "Incr Iface Greeter" extends "Incr Iface Other"
        {
            procedure Greet(): Text;
        }
        """;

    private const string Other = """
        interface "Incr Iface Other"
        {
            procedure Hello(): Text;
        }
        """;

    /// <summary>Implements Greeter and, already, every procedure Other declares.</summary>
    private const string Impl = """
        codeunit 90430 "Incr Iface Impl" implements "Incr Iface Greeter"
        {
            procedure Hello(): Text
            begin
                exit('hi');
            end;

            procedure Greet(): Text
            begin
                exit('g');
            end;
        }
        """;

    /// <summary>Folds Greeter's and Other's ids; byte-identical across every edit.</summary>
    private const string User = """
        codeunit 90431 "Incr Iface User"
        {
            procedure IsOther(): Boolean
            var
                Impl: Codeunit "Incr Iface Impl";
                G: Interface "Incr Iface Greeter";
            begin
                G := Impl;
                exit(G is "Incr Iface Other");
            end;

            procedure CallGreet(G: Interface "Incr Iface Greeter"): Text
            begin
                exit(G.Greet());
            end;
        }
        """;

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    /// <summary>Baseline compile, the edits, the incremental attempt, and a cold build of the
    /// post-edit tree under the SAME module name (an interface id hashes the app, so a different
    /// module name would make every folded id differ for reasons unrelated to the edit).</summary>
    private (BcEmitOutput? Incremental, string Reason, Dictionary<string, string> Baseline, Dictionary<string, string> Fresh)
        Run(string module, IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> edits)
    {
        foreach (var (file, src) in before) File.WriteAllText(Path.Combine(_root, file), src);
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, module, trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);

        foreach (var (file, src) in edits) File.WriteAllText(Path.Combine(_root, file), src);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, module, appRootDir: null, out var reason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, module);
        Assert.Empty(freshOut.Diagnostics);
        return (incremental, reason, ByName(baselineOut), ByName(freshOut));
    }

    private static Dictionary<string, string> Fixture() => new()
    {
        ["Greeter.al"] = Greeter, ["Other.al"] = Other, ["Impl.al"] = Impl, ["User.al"] = User,
    };

    private static void AssertFellBack(
        BcEmitOutput? incremental, string reason, string interfaceName, Dictionary<string, string> fresh, string dependent)
    {
        Assert.True(incremental == null,
            $"the incremental path took the fast path after '{interfaceName}' changed shape. The unedited "
            + $"'{dependent}' was not re-emitted, so its C# still folds the interface's PREVIOUS id. Shipped C# "
            + (incremental != null && ByName(incremental)[dependent] == fresh[dependent]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build.")
            + $" fallbackReason: '{reason}'");
        Assert.Contains($"Interface '{interfaceName}'", reason, StringComparison.Ordinal);
        Assert.Contains("extends list", reason, StringComparison.Ordinal);
    }

    /// <summary>The issue's shape: Greeter gains `extends Other`; only Greeter.al changes.</summary>
    [SkippableFact]
    public void TryEmitIncremental_InterfaceGainsAnExtendsClause_FallsBackInsteadOfShippingAStaleImplementer()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, reason, baseline, fresh) = Run("IfaceExtendsModule", Fixture(),
            new Dictionary<string, string> { ["Greeter.al"] = GreeterExtendsOther });

        // Fixture guard: the edit moved the IMPLEMENTER's interface list, whose file was not edited.
        Assert.NotEqual(baseline["Incr Iface Impl"], fresh["Incr Iface Impl"]);
        AssertFellBack(incremental, reason, "Incr Iface Greeter", fresh, "Incr Iface Impl");
    }

    /// <summary>A procedure added to an interface moves its id; implementer and user both fold it.</summary>
    [SkippableFact]
    public void TryEmitIncremental_InterfaceGainsAProcedure_FallsBackInsteadOfShippingAStaleUser()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, reason, baseline, fresh) = Run("IfaceAddProcModule", Fixture(),
            new Dictionary<string, string>
            {
                ["Greeter.al"] = Greeter.Replace(
                    "procedure Greet(): Text;", "procedure Greet(): Text;\n    procedure Hello(): Text;", StringComparison.Ordinal),
            });

        Assert.NotEqual(baseline["Incr Iface User"], fresh["Incr Iface User"]);
        Assert.NotEqual(baseline["Incr Iface Impl"], fresh["Incr Iface Impl"]);
        AssertFellBack(incremental, reason, "Incr Iface Greeter", fresh, "Incr Iface User");
    }

    /// <summary>Transitive: Greeter extends Other, and OTHER gains an extends clause. The
    /// implementer names only Greeter, and its file is not edited.</summary>
    [SkippableFact]
    public void TryEmitIncremental_ExtendedInterfaceGainsAnExtendsClause_FallsBackInsteadOfShippingAStaleImplementer()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var before = Fixture();
        before["Greeter.al"] = GreeterExtendsOther;
        before["Third.al"] = """
            interface "Incr Iface Third"
            {
                procedure Third(): Integer;
            }
            """;
        before["Impl.al"] = Impl.Replace("    procedure Greet(): Text",
            "    procedure Third(): Integer\n    begin\n        exit(3);\n    end;\n\n    procedure Greet(): Text",
            StringComparison.Ordinal);

        var (incremental, reason, baseline, fresh) = Run("IfaceTransitiveModule", before,
            new Dictionary<string, string>
            {
                ["Other.al"] = Other.Replace("interface \"Incr Iface Other\"",
                    "interface \"Incr Iface Other\" extends \"Incr Iface Third\"", StringComparison.Ordinal),
            });

        Assert.NotEqual(baseline["Incr Iface Impl"], fresh["Incr Iface Impl"]);
        AssertFellBack(incremental, reason, "Incr Iface Other", fresh, "Incr Iface Impl");
    }

    /// <summary>The control: an interface edit that moves no procedure and no extends entry stays
    /// on the fast path, and what it ships equals a cold build.</summary>
    [SkippableFact]
    public void TryEmitIncremental_InterfaceEditThatKeepsItsShape_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, reason, _, fresh) = Run("IfaceCommentModule", Fixture(),
            new Dictionary<string, string>
            {
                ["Greeter.al"] = Greeter.Replace("{\n", "{\n    // a comment moves no procedure\n", StringComparison.Ordinal),
            });

        Assert.True(incremental != null,
            "an interface edit that changes neither its procedures nor its extends list fell back. The guard "
            + $"is keyed on 'an interface file changed' rather than on its shape. fallbackReason: {reason}");
        var shipped = ByName(incremental!);
        Assert.Equal(fresh["Incr Iface Impl"], shipped["Incr Iface Impl"]);
        Assert.Equal(fresh["Incr Iface User"], shipped["Incr Iface User"]);
    }
}
