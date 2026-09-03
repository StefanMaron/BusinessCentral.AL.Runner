// BcCompilerIncrementalOverloadRebindTests — issue #2548. The one edit shape where the
// incremental (RAD) fast path's "an unmodified caller never needs re-emitting" argument does not
// hold, and where the damage is SILENT.
//
// BcCompiler.Incremental.cs's header argues that reusing an untouched object's cached C# is always
// safe, because a cross-object call compiles to
// `new NavCodeunitHandle(this, <object id>).Target.Invoke(<memberId>, args)` and a breaking edit
// surfaces as NavNCLMissingMethodException at the call site — loud, not silent.
//
// That holds for every edit that RETIRES or MOVES an existing member's id. It does not hold for
// one that ADDS a member under a name the object already had:
//
//   * `MethodSymbol.CalculateMethodIdForNewVersions` is method-local, so adding `Which(Integer)`
//     beside `Which(Decimal)` leaves the Decimal overload's id — and its `case` label in the
//     re-emitted callee's OnInvoke switch — bit-identical.
//   * What moves is the id the CALLER bakes. Before the edit an Integer argument widened to
//     `Which(Decimal)`; after it, overload resolution picks `Which(Integer)`.
//   * An un-rebound caller therefore dispatches a member that STILL EXISTS, and gets the previous
//     overload's answer. No exception, no diagnostic, no log line.
//
// Measured RED on main @ 2eebaedd: the fast path was taken with an empty fallbackReason, the
// callee's C# matched a cold build, and the caller's C# did not.
//
// Both tests here are needed, and each is the other's control:
//   * AddingAnOverload… asserts the fallback happens and says why. Alone, "always fall back"
//     would satisfy it — which would delete the fast path.
//   * AddingAProcedureUnderANewName… asserts the fast path is still taken for the neighbouring
//     shape AND that what it ships equals a cold build. Alone, "never fall back" would satisfy it.
//
// Credit: the hazard, the fixture shape and the compiler contract underneath it were found and
// pinned by Mikkel Mansa Vilhelmsen (vhn) in his AL Runner fork (RadSameAppOverloadTests,
// RadSameAppOverloadWatchTests). His fork rebinds the callers from an object-reference graph it
// maintains; this repo has no such graph, so the fallback is the available correct answer.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalOverloadRebindTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalOverloadRebindTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-incremental-overload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    /// <summary>The callee. One Decimal overload of `Which`; `Sibling` is never called and exists
    /// so the codeunit has a member neither edit disturbs.</summary>
    private const string LibBefore = """
        codeunit 90270 "Incr Ovl Lib"
        {
            procedure Which(Seed: Decimal): Text
            begin
                exit('DECIMAL');
            end;

            procedure Sibling(Value: Integer): Integer
            begin
                exit(Value);
            end;
        }
        """;

    /// <summary>The hazardous edit: a second overload of the SAME name, Integer where the existing
    /// one takes Decimal. Placed after the existing members so nothing about the result can be
    /// attributed to declaration order.</summary>
    private const string LibWithOverload = """
        codeunit 90270 "Incr Ovl Lib"
        {
            procedure Which(Seed: Decimal): Text
            begin
                exit('DECIMAL');
            end;

            procedure Which(Seed: Integer): Text
            begin
                exit('INTEGER');
            end;

            procedure Sibling(Value: Integer): Integer
            begin
                exit(Value);
            end;
        }
        """;

    /// <summary>The control edit: the same amount of new code, under a name the object did not
    /// already have. Overload resolution at every existing call site is untouched and no existing
    /// member's id moves, so this must stay on the fast path.</summary>
    private const string LibWithNewName = """
        codeunit 90270 "Incr Ovl Lib"
        {
            procedure Which(Seed: Decimal): Text
            begin
                exit('DECIMAL');
            end;

            procedure Fresh(Seed: Integer): Text
            begin
                exit('FRESH');
            end;

            procedure Sibling(Value: Integer): Integer
            begin
                exit(Value);
            end;
        }
        """;

    /// <summary>The caller, byte-for-byte identical across both edits. It passes an INTEGER to a
    /// method that today has only a Decimal overload, so overload resolution HERE is what the
    /// hazardous edit changes — which is why it is only ever re-emitted if the delta decides it
    /// must be.</summary>
    private const string CallerSrc = """
        codeunit 90271 "Incr Ovl Caller"
        {
            procedure Call(): Text
            var
                Lib: Codeunit "Incr Ovl Lib";
                Seed: Integer;
            begin
                Seed := 2;
                exit(Lib.Which(Seed));
            end;
        }
        """;

    /// <summary>Compiles the fixture, records an incremental baseline, then applies
    /// <paramref name="editedLib"/> and returns both the incremental attempt and an independent
    /// cold build of the same post-edit tree.</summary>
    private (BcEmitOutput? Incremental, string FallbackReason, Dictionary<string, string> Baseline, Dictionary<string, string> Fresh)
        RunEdit(string editedLib)
    {
        WriteAl("Lib.al", LibBefore);
        WriteAl("Caller.al", CallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "OverloadRebindModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);
        Assert.Equal(2, baselineByName.Count);

        // Edit ONLY the callee. The caller's file is not touched at all.
        WriteAl("Lib.al", editedLib);

        var incrOut = compiler.TryEmitIncremental(
            new[] { _root }, "OverloadRebindModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "OverloadRebindModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        return (incrOut, fallbackReason, baselineByName, ByName(freshOut));
    }

    /// <summary>
    /// The hazard. Adding an overload must not leave the fast path shipping a caller that still
    /// dispatches the member id of the overload it used to bind to.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_AddingAnOverloadToACallee_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, baseline, fresh) = RunEdit(LibWithOverload);

        // Fixture guard: the edit really did move what the caller binds to. Without this the
        // whole test could pass against an edit that changed nothing.
        Assert.NotEqual(baseline["Incr Ovl Caller"], fresh["Incr Ovl Caller"]);

        Assert.True(incremental == null,
            "the incremental path took the fast path for an added overload. The caller was not "
            + "re-emitted, so it still dispatches the member id of `Which(Decimal)` even though an "
            + "Integer argument now binds to `Which(Integer)`. That member still exists in the "
            + "re-emitted callee, so the call succeeds and returns the PREVIOUS overload's answer — "
            + "no exception, no diagnostic. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr Ovl Caller"] == fresh["Incr Ovl Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build."));

        // A fallback nobody can read is the silent default loud-failures.md forbids: the reason
        // must name the object and the shape, not just say "full compile".
        Assert.Contains("Incr Ovl Lib", fallbackReason, StringComparison.Ordinal);
        Assert.Contains("overload", fallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control, and the reason the fallback above is not simply "give up on everything": a
    /// procedure added under a name NEW to the object changes no existing call site's overload
    /// resolution and moves no existing member's id, so the fast path must still apply — and what
    /// it ships must equal a cold build of the same tree.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_AddingAProcedureUnderANewName_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, _, fresh) = RunEdit(LibWithNewName);

        Assert.True(incremental != null,
            "adding a procedure under a name the object did not already have fell back to a full "
            + "compile. Nothing an existing caller baked can have moved — overload resolution never "
            + "considered this name and no existing member's id changed — so the added-overload "
            + $"guard is over-triggering. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr Ovl Lib"], incrementalByName["Incr Ovl Lib"]);
        Assert.Equal(fresh["Incr Ovl Caller"], incrementalByName["Incr Ovl Caller"]);
    }
}
