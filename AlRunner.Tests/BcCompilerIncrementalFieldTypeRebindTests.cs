// BcCompilerIncrementalFieldTypeRebindTests — found while working issue #5089.
//
// A caller folds a table field's TYPE into its own generated C#, beside the field id
// FoldedOrdinalMoved already guards: `T.Amount := 7` compiles to
// `SetFieldValueSafe(2, NavType.Integer, …)`, and `T.Code := 'X'` to `new NavCode(20, "X")`.
// Measured with the fixtures below: changing Integer -> Decimal, or Code[20] -> Code[30], moves the
// caller's C# while only the table's file changed. The field keeps its id and name, so neither
// FoldedOrdinalMoved nor ObjectIdMoved sees it.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalFieldTypeRebindTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalFieldTypeRebindTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-fieldtype-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Table = """
        table 90440 "Incr FieldType Tab"
        {
            fields
            {
                field(1; Code; Code[20]) { }
                field(2; Amount; Integer) { }
            }
            keys { key(PK; Code) { } }
        }
        """;

    /// <summary>Byte-identical across every edit; folds both fields' types.</summary>
    private const string Caller = """
        codeunit 90441 "Incr FieldType Caller"
        {
            procedure SetBoth(var T: Record "Incr FieldType Tab"): Decimal
            begin
                T.Code := 'X';
                T.Amount := 7;
                exit(T.Amount);
            end;
        }
        """;

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    private (BcEmitOutput? Incremental, string Reason, string BaselineCaller, string FreshCaller) Run(string module, string editedTable)
    {
        File.WriteAllText(Path.Combine(_root, "Tab.al"), Table);
        File.WriteAllText(Path.Combine(_root, "Caller.al"), Caller);
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, module, trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);

        File.WriteAllText(Path.Combine(_root, "Tab.al"), editedTable);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, module, appRootDir: null, out var reason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, module);
        Assert.Empty(freshOut.Diagnostics);
        return (incremental, reason, ByName(baselineOut)["Incr FieldType Caller"], ByName(freshOut)["Incr FieldType Caller"]);
    }

    private static void AssertFellBack(BcEmitOutput? incremental, string reason, string freshCaller, string field)
    {
        Assert.True(incremental == null,
            $"the incremental path took the fast path after field '{field}' changed type. The unedited caller "
            + "still folds the PREVIOUS type. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr FieldType Caller"] == freshCaller
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build.")
            + $" fallbackReason: '{reason}'");
        Assert.Contains($"field '{field}'", reason, StringComparison.Ordinal);
        Assert.Contains("data type", reason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TryEmitIncremental_FieldChangesType_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, reason, baseline, fresh) = Run("FieldTypeModule",
            Table.Replace("Amount; Integer", "Amount; Decimal", StringComparison.Ordinal));

        Assert.Contains("NavType.Integer", baseline, StringComparison.Ordinal);
        Assert.Contains("NavType.Decimal", fresh, StringComparison.Ordinal);
        AssertFellBack(incremental, reason, fresh, "Amount");
    }

    [SkippableFact]
    public void TryEmitIncremental_CodeFieldChangesLength_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, reason, baseline, fresh) = Run("FieldLengthModule",
            Table.Replace("Code[20]", "Code[30]", StringComparison.Ordinal));

        Assert.Contains("new NavCode(20, ", baseline, StringComparison.Ordinal);
        Assert.Contains("new NavCode(30, ", fresh, StringComparison.Ordinal);
        AssertFellBack(incremental, reason, fresh, "Code");
    }

    /// <summary>The control: a property edit on a field moves no type, so the fast path still
    /// applies and ships what a cold build ships.</summary>
    [SkippableFact]
    public void TryEmitIncremental_FieldCaptionEdit_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, reason, _, fresh) = Run("FieldCaptionModule",
            Table.Replace("field(2; Amount; Integer) { }", "field(2; Amount; Integer) { Caption = 'Amt'; }", StringComparison.Ordinal));

        Assert.True(incremental != null,
            $"a field caption edit fell back; the guard is keyed on 'a table changed', not on a field's type. fallbackReason: {reason}");
        Assert.Equal(fresh, ByName(incremental!)["Incr FieldType Caller"]);
    }
}
