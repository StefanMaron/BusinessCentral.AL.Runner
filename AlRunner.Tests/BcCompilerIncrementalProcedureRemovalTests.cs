// BcCompilerIncrementalProcedureRemovalTests — issue #5102, at the compiler.
//
// A procedure that is removed, or made local, while an UNEDITED object still calls it must not
// stay on the incremental fast path: a cold build fails to compile the caller and excludes it,
// whereas the fast path shipped the old caller C# (silently green for `local`, whose member id
// survives). Each case asserts the fall back AND that a cold build really excludes the caller.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalProcedureRemovalTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalProcedureRemovalTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-procedure-removal-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Lib = """
        codeunit 90460 "Incr Rm Lib"
        {
            procedure Len(C: Code[20]): Integer
            begin
                exit(StrLen(C));
            end;

            procedure DoIt()
            begin
            end;

            local procedure Helper(X: Integer): Integer
            begin
                exit(X);
            end;

            procedure UseHelper(): Integer
            begin
                exit(Helper(1));
            end;
        }
        """;

    /// <summary>Byte-identical across every edit.</summary>
    private const string Caller = """
        codeunit 90461 "Incr Rm Caller"
        {
            procedure Go(): Integer
            var
                Lib: Codeunit "Incr Rm Lib";
            begin
                Lib.DoIt();
                exit(Lib.Len('abc') + Lib.UseHelper());
            end;
        }
        """;

    private (BcEmitOutput? Incremental, string Reason, BcEmitOutput Fresh) Run(string module, string editedLib)
    {
        File.WriteAllText(Path.Combine(_root, "Lib.al"), Lib);
        File.WriteAllText(Path.Combine(_root, "Caller.al"), Caller);
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, module, trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);

        Assert.NotEqual(Lib, editedLib);
        File.WriteAllText(Path.Combine(_root, "Lib.al"), editedLib);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, module, appRootDir: null, out var reason);
        var fresh = new BcCompiler().Emit(new[] { _root }, module);
        return (incremental, reason, fresh);
    }

    private void SkipIfNoEngine()
        => TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    [SkippableTheory]
    [InlineData("local", "    procedure Len(", "    local procedure Len(", "Len")]
    [InlineData("removed", "    procedure DoIt()\n    begin\n    end;\n\n", "", "DoIt")]
    [InlineData("renamed", "procedure DoIt()", "procedure DoItNow()", "DoIt")]
    public void ProcedureThatLeftThePublicSurface_FallsBackAndColdBuildExcludesTheCaller(
        string module, string from, string to, string procedure)
    {
        SkipIfNoEngine();
        var (inc, reason, fresh) = Run("Rm" + module, Lib.Replace(from, to, StringComparison.Ordinal));

        // The premise: a cold build does not ship the unedited caller.
        Assert.DoesNotContain(fresh.Sources, s => s.Name == "Incr Rm Caller");

        Assert.True(inc == null,
            $"the incremental path took the fast path after procedure '{procedure}' left the public surface"
            + (inc != null && inc.Sources.Any(s => s.Name == "Incr Rm Caller") ? ", and shipped the unedited caller" : "")
            + $". fallbackReason: '{reason}'");
        Assert.Contains($"procedure '{procedure}'", reason, StringComparison.Ordinal);
        Assert.Contains("no longer declares a procedure", reason, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("helperbody", "exit(X);", "exit(X + 1);")]
    [InlineData("helpersig", "Helper(X: Integer): Integer", "Helper(X: Integer; Y: Integer): Integer")]
    [InlineData("added", "    procedure DoIt()", "    procedure Extra()\n    begin\n    end;\n\n    procedure DoIt()")]
    public void EditsThatKeepEveryPublicProcedure_StayOnTheFastPathAndMatchACold(string what, string from, string to)
    {
        SkipIfNoEngine();
        var edited = Lib.Replace(from, to, StringComparison.Ordinal);
        if (what == "helpersig") edited = edited.Replace("Helper(1)", "Helper(1, 2)", StringComparison.Ordinal);
        var (inc, reason, fresh) = Run("RmOk" + what, edited);
        Assert.True(inc != null, $"a '{what}' edit fell back; no public procedure left. fallbackReason: {reason}");
        Assert.Empty(fresh.Diagnostics);
        Assert.Equal(fresh.Sources.Single(s => s.Name == "Incr Rm Caller").Code,
            inc!.Sources.Single(s => s.Name == "Incr Rm Caller").Code);
    }
}
