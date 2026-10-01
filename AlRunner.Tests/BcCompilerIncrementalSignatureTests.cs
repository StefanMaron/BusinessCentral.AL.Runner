// BcCompilerIncrementalSignatureTests — issue #5093, at the compiler.
//
// A caller folds a procedure's signature into its own generated C#: an argument passed to a
// Code[20] parameter becomes `new NavCode(20, …)`, and a Code[20] variable assigned from a call
// returning Code[30] gets a `new NavCode(20, …)` narrowing. A length-only change keeps the member
// id, so the fast path used to reuse the unedited caller with the previous length. Every
// signature change now falls back; body edits and caller-invisible edits stay on the fast path.
// ServerIncrementalSignatureChangeTests proves the AL-observable result.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalSignatureTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalSignatureTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-signature-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Lib = """
        codeunit 90450 "Incr Sig Lib"
        {
            procedure Len(C: Code[20]): Integer
            begin
                exit(StrLen(C));
            end;

            procedure Get(): Code[20]
            begin
                exit('ABC');
            end;

            procedure Bump(X: Integer): Integer
            begin
                exit(X + 1);
            end;

            procedure DoIt()
            begin
                Error('boom');
            end;
        }
        """;

    private const string Table = """
        table 90452 "Incr Sig Tab"
        {
            fields
            {
                field(1; Code; Code[20]) { }
            }
            keys { key(PK; Code) { } }

            procedure Describe(Prefix: Text[10]): Text
            begin
                exit(Prefix + Code);
            end;
        }
        """;

    /// <summary>Byte-identical across every edit; calls every procedure above.</summary>
    private const string Caller = """
        codeunit 90451 "Incr Sig Caller"
        {
            procedure Go(): Integer
            var
                Lib: Codeunit "Incr Sig Lib";
                T: Record "Incr Sig Tab";
                Txt: Text;
                C: Code[20];
                V: Integer;
            begin
                V := 1;
                Txt := 'abc';
                C := Lib.Get();
                Txt := T.Describe('p');
                exit(Lib.Len(Txt) + Lib.Bump(V) + StrLen(C));
            end;

            procedure Fire()
            var
                Lib: Codeunit "Incr Sig Lib";
            begin
                Lib.DoIt();
            end;
        }
        """;

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    private (BcEmitOutput? Incremental, string Reason, string BaselineCaller, string FreshCaller) Run(
        string module, string? editedLib = null, string? editedTable = null)
    {
        File.WriteAllText(Path.Combine(_root, "Lib.al"), Lib);
        File.WriteAllText(Path.Combine(_root, "Tab.al"), Table);
        File.WriteAllText(Path.Combine(_root, "Caller.al"), Caller);
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, module, trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);

        if (editedLib != null) File.WriteAllText(Path.Combine(_root, "Lib.al"), editedLib);
        if (editedTable != null) File.WriteAllText(Path.Combine(_root, "Tab.al"), editedTable);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, module, appRootDir: null, out var reason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, module);
        Assert.Empty(freshOut.Diagnostics);
        return (incremental, reason, ByName(baselineOut)["Incr Sig Caller"], ByName(freshOut)["Incr Sig Caller"]);
    }

    private static void AssertFellBack(BcEmitOutput? incremental, string reason, string freshCaller, string procedure)
    {
        Assert.True(incremental == null,
            $"the incremental path took the fast path after procedure '{procedure}' changed its signature. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr Sig Caller"] == freshCaller
                ? "matched a cold build."
                : "did NOT match a cold build.")
            + $" fallbackReason: '{reason}'");
        Assert.Contains($"procedure '{procedure}'", reason, StringComparison.Ordinal);
        Assert.Contains("changed its signature", reason, StringComparison.Ordinal);
    }

    private static void AssertFastPathMatchesCold(BcEmitOutput? incremental, string reason, string freshCaller, string what)
    {
        Assert.True(incremental != null, $"{what} fell back; it changes no signature. fallbackReason: {reason}");
        Assert.Equal(freshCaller, ByName(incremental!)["Incr Sig Caller"]);
    }

    private void SkipIfNoEngine()
        => TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    [SkippableFact]
    public void CodeParameterLengthChange_FallsBack()
    {
        SkipIfNoEngine();
        var (inc, reason, baseline, fresh) = Run("SigCodeParam",
            editedLib: Lib.Replace("Len(C: Code[20])", "Len(C: Code[30])", StringComparison.Ordinal));
        Assert.Contains("new NavCode(20, ", baseline, StringComparison.Ordinal);
        Assert.Contains("new NavCode(30, ", fresh, StringComparison.Ordinal);
        AssertFellBack(inc, reason, fresh, "Len");
    }

    [SkippableFact]
    public void ReturnLengthChange_FallsBack()
    {
        SkipIfNoEngine();
        var (inc, reason, baseline, fresh) = Run("SigReturn",
            editedLib: Lib.Replace("Get(): Code[20]", "Get(): Code[30]", StringComparison.Ordinal));
        Assert.NotEqual(baseline, fresh);
        AssertFellBack(inc, reason, fresh, "Get");
    }

    [SkippableFact]
    public void TableProcedureTextParameterLengthChange_FallsBack()
    {
        SkipIfNoEngine();
        var (inc, reason, baseline, fresh) = Run("SigTableText",
            editedTable: Table.Replace("Prefix: Text[10]", "Prefix: Text[15]", StringComparison.Ordinal));
        Assert.Contains("new NavText(10, ", baseline, StringComparison.Ordinal);
        Assert.Contains("new NavText(15, ", fresh, StringComparison.Ordinal);
        AssertFellBack(inc, reason, fresh, "Describe");
    }

    /// <summary>These move the member id, so a stale caller was loud rather than silent; a cold
    /// build is still the right answer, and the same rule covers them.</summary>
    [SkippableTheory]
    [InlineData("var", "Bump(X: Integer)", "Bump(var X: Integer)", "Bump")]
    [InlineData("try", "    procedure DoIt(", "    [TryFunction]\n    procedure DoIt(", "DoIt")]
    public void IdMovingSignatureChange_FallsBack(string module, string from, string to, string procedure)
    {
        SkipIfNoEngine();
        var edited = Lib.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Lib, edited);
        var (inc, reason, _, fresh) = Run("SigId" + module, editedLib: edited);
        AssertFellBack(inc, reason, fresh, procedure);
    }

    [SkippableTheory]
    [InlineData("body", "exit(StrLen(C));", "exit(StrLen(C) + 0);")]
    [InlineData("rename", "Len(C: Code[20]): Integer\n    begin\n        exit(StrLen(C));",
        "Len(D: Code[20]): Integer\n    begin\n        exit(StrLen(D));")]
    [InlineData("obsolete", "    procedure Len(", "    [Obsolete('x', '1.0')]\n    procedure Len(")]
    [InlineData("added", "    procedure Get(", "    procedure Fresh(): Integer\n    begin\n        exit(1);\n    end;\n\n    procedure Get(")]
    public void CallerInvisibleEdit_StaysOnTheFastPathAndMatchesAColdBuild(string what, string from, string to)
    {
        SkipIfNoEngine();
        var edited = Lib.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Lib, edited);
        var (inc, reason, _, fresh) = Run("SigOk" + what, editedLib: edited);
        AssertFastPathMatchesCold(inc, reason, fresh, $"a '{what}' edit");
    }
}
