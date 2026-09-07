// BcCompilerIncrementalObjectIdRebindTests — issue #2929.
//
// The FOURTH folding an unmodified caller performs, and the one #2928's FoldedOrdinalMoved
// cannot see: the callee's own OBJECT ID.
//
// #2548 found the first hole in BcCompiler.Incremental.cs's "an unmodified caller never needs
// re-emitting" argument (an added overload); #2571/#2928 found the second family — a caller
// folds an enum value's ordinal, an Option member's position and a field's id into its OWN
// generated C# as literals. This file pins the object id, which is folded the same way and by
// the same callers, and which FoldedOrdinalMoved declines to answer for by construction:
// changing an object's id makes it VACATE one (Kind, Id) identity and APPEAR under another, so
// the old identity is absent from the after-surface, RadFoldedOrdinals has no entry for it, and
// the guard returns "cannot answer" rather than "moved".
//
// Measured with --dump-csharp, BC 28.1, a bundle of two codeunits (90310/90311), two enums
// (90320/90321) and one caller that references all four. Swapping each PAIR's ids, leaving every
// name and every body byte-for-byte identical, changes the CALLER's generated C# in four places
// while only the CALLEES' files were edited:
//
//     -   this.a = new NavCodeunitHandle(this, 90310);
//     +   this.a = new NavCodeunitHandle(this, 90311);
//     -   this.b = new NavCodeunitHandle(this, 90311);
//     +   this.b = new NavCodeunitHandle(this, 90310);
//     -   public NavOption e = NavOption.Create(NCLEnumMetadata.Create(90320), 0);
//     +   public NavOption e = NavOption.Create(NCLEnumMetadata.Create(90321), 0);
//     -   this.e = NavOption.Create(NCLEnumMetadata.Create(90320), 1);
//     +   this.e = NavOption.Create(NCLEnumMetadata.Create(90321), 1);
//
// The swap is the silent sub-case the issue singles out, and the measurement above is what
// makes it silent rather than merely suspected: after the swap every folded id STILL RESOLVES —
// to the other object. `A.Who()` dispatches member id -636290258 on a handle to 90311, and both
// codeunits declare `Who`, so the member id matches too. No exception, no diagnostic, no log
// line; the caller simply calls the wrong codeunit and reads the wrong enum's metadata.
//
// A plain RENUMBER (one object moves to an id nothing occupies) is the other sub-case. It is
// expected to be louder at runtime — the handle names an object that no longer exists — but the
// invariant the fast path has to hold is the same one the #2571 tests assert and does not depend
// on how loud the symptom is: what the fast path ships must equal what a cold build produces.
//
// Each RED test has a control that would be broken by "just always fall back", which would
// delete the fast path this file exists to protect.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalObjectIdRebindTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalObjectIdRebindTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-objid-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    // ---------------------------------------------------------------------------------------
    // Sub-case 2 from the issue: a SWAP of two same-kind object ids. Every folded id still
    // resolves, to the other object, so nothing can throw.
    // ---------------------------------------------------------------------------------------

    private const string CodeunitABefore = """
        codeunit 90310 "Incr ObjId A"
        {
            procedure Who(): Integer
            begin
                exit(1);
            end;
        }
        """;

    private const string CodeunitBBefore = """
        codeunit 90311 "Incr ObjId B"
        {
            procedure Who(): Integer
            begin
                exit(2);
            end;
        }
        """;

    /// <summary>The hazardous edit, half one: `Incr ObjId A` takes the id `Incr ObjId B` had.
    /// Its name, its members and its body are untouched.</summary>
    private const string CodeunitASwapped = """
        codeunit 90311 "Incr ObjId A"
        {
            procedure Who(): Integer
            begin
                exit(1);
            end;
        }
        """;

    /// <summary>The hazardous edit, half two: the mirror. Applied in the SAME cycle, so neither
    /// id is ever free and the pair swaps atomically.</summary>
    private const string CodeunitBSwapped = """
        codeunit 90310 "Incr ObjId B"
        {
            procedure Who(): Integer
            begin
                exit(2);
            end;
        }
        """;

    /// <summary>The caller, byte-for-byte identical across every edit. It folds BOTH callees'
    /// object ids into its own generated C# as `new NavCodeunitHandle(this, &lt;id&gt;)`.</summary>
    private const string ObjIdCallerSrc = """
        codeunit 90312 "Incr ObjId Caller"
        {
            procedure CallA(): Integer
            var
                A: Codeunit "Incr ObjId A";
            begin
                exit(A.Who());
            end;

            procedure CallB(): Integer
            var
                B: Codeunit "Incr ObjId B";
            begin
                exit(B.Who());
            end;
        }
        """;

    /// <summary>Compiles the fixture, records an incremental baseline, then applies the given
    /// callee edits and returns both the incremental attempt and an independent cold build of the
    /// same post-edit tree. The CALLER's file is never rewritten.</summary>
    private (BcEmitOutput? Incremental, string FallbackReason, Dictionary<string, string> Baseline, Dictionary<string, string> Fresh)
        RunCodeunitEdit(string editedA, string editedB)
    {
        WriteAl("ObjIdA.al", CodeunitABefore);
        WriteAl("ObjIdB.al", CodeunitBBefore);
        WriteAl("ObjIdCaller.al", ObjIdCallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "ObjIdModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);

        // Edit ONLY the callees.
        WriteAl("ObjIdA.al", editedA);
        WriteAl("ObjIdB.al", editedB);

        var incrOut = compiler.TryEmitIncremental(
            new[] { _root }, "ObjIdModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "ObjIdModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        return (incrOut, fallbackReason, baselineByName, ByName(freshOut));
    }

    /// <summary>
    /// The hazard, and the silent sub-case. Swapping two codeunits' object ids must not leave the
    /// fast path shipping a caller that still folds each callee's PREVIOUS id.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_SwappingTwoCodeunitObjectIds_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, baseline, fresh) = RunCodeunitEdit(CodeunitASwapped, CodeunitBSwapped);

        // Fixture guard: the edit really did move what the caller folded. Without this the whole
        // test could pass against an edit that changed nothing.
        Assert.NotEqual(baseline["Incr ObjId Caller"], fresh["Incr ObjId Caller"]);

        // ...and it moved exactly the thing this test is about: the folded object id literal.
        Assert.Contains("new NavCodeunitHandle(this, 90310)", baseline["Incr ObjId Caller"], StringComparison.Ordinal);
        Assert.Contains("new NavCodeunitHandle(this, 90311)", baseline["Incr ObjId Caller"], StringComparison.Ordinal);

        Assert.True(incremental == null,
            "the incremental path took the fast path after two codeunits swapped object ids. The "
            + "caller was not re-emitted, so `CallA` still holds a NavCodeunitHandle to 90310 — "
            + "which is now `Incr ObjId B`. Every folded id still RESOLVES, to the other object, "
            + "and both codeunits declare `Who`, so the member id matches too: nothing throws and "
            + "the run goes green calling the wrong codeunit. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr ObjId Caller"] == fresh["Incr ObjId Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build.")
            + $" fallbackReason: '{fallbackReason}'");

        // A fallback nobody can read is the silent default loud-failures.md forbids: the reason
        // must name the object and the shape.
        Assert.Contains("Incr ObjId", fallbackReason, StringComparison.Ordinal);
        Assert.Contains("object id", fallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sub-case 1 from the issue: a plain RENUMBER, where the vacated id is left free. Expected to
    /// be louder at runtime than the swap, but the invariant is the same — the fast path must not
    /// ship a caller whose folded id disagrees with a cold build.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_RenumberingACodeunitObjectId_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Only A moves, to an id nothing occupies. B is untouched.
        const string aRenumbered = """
            codeunit 90319 "Incr ObjId A"
            {
                procedure Who(): Integer
                begin
                    exit(1);
                end;
            }
            """;

        var (incremental, fallbackReason, baseline, fresh) = RunCodeunitEdit(aRenumbered, CodeunitBBefore);

        Assert.NotEqual(baseline["Incr ObjId Caller"], fresh["Incr ObjId Caller"]);
        Assert.Contains("new NavCodeunitHandle(this, 90310)", baseline["Incr ObjId Caller"], StringComparison.Ordinal);
        Assert.Contains("new NavCodeunitHandle(this, 90319)", fresh["Incr ObjId Caller"], StringComparison.Ordinal);

        Assert.True(incremental == null,
            "the incremental path took the fast path after a codeunit was renumbered. The caller "
            + "was not re-emitted, so `CallA` still holds a NavCodeunitHandle to 90310 even though "
            + "`Incr ObjId A` is now 90319 and nothing declares 90310 at all. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr ObjId Caller"] == fresh["Incr ObjId Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build.")
            + $" fallbackReason: '{fallbackReason}'");

        Assert.Contains("Incr ObjId A", fallbackReason, StringComparison.Ordinal);
        Assert.Contains("object id", fallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control, and the reason the fallback above is not simply "give up whenever a callee's
    /// file changed": editing a callee's BODY moves no object id, so every folded literal already
    /// in cached C# stays correct and the fast path must still apply — and what it ships must
    /// equal a cold build.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_EditingOnlyACodeunitBody_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        const string aBodyEdited = """
            codeunit 90310 "Incr ObjId A"
            {
                procedure Who(): Integer
                begin
                    exit(41 + 1);
                end;
            }
            """;

        var (incremental, fallbackReason, _, fresh) = RunCodeunitEdit(aBodyEdited, CodeunitBBefore);

        Assert.True(incremental != null,
            "editing only a codeunit's method BODY fell back to a full compile. No object id moved, "
            + "so no caller's folded handle can be stale — the guard is keyed on 'a callee's file "
            + $"changed' rather than on 'an object id moved'. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr ObjId Caller"], incrementalByName["Incr ObjId Caller"]);
    }

    /// <summary>
    /// The second control: ADDING a codeunit under a new name and an unused id. No existing
    /// object's id moves and no existing caller can reference the new one, so the fast path must
    /// still apply. This is the control that stops the guard degenerating into "any appeared or
    /// vacated identity falls back", which would be most of the fast path.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_AddingACodeunitUnderANewIdAndName_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("ObjIdA.al", CodeunitABefore);
        WriteAl("ObjIdB.al", CodeunitBBefore);
        WriteAl("ObjIdCaller.al", ObjIdCallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "ObjIdAddModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);

        // A brand-new object, at an id and a name the baseline never saw.
        WriteAl("ObjIdC.al", """
            codeunit 90318 "Incr ObjId C"
            {
                procedure Who(): Integer
                begin
                    exit(3);
                end;
            }
            """);

        var incremental = compiler.TryEmitIncremental(
            new[] { _root }, "ObjIdAddModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "ObjIdAddModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        var fresh = ByName(freshOut);

        Assert.True(incremental != null,
            "adding a codeunit under an id and a name the baseline never saw fell back to a full "
            + "compile. Nothing an existing caller folded moved — the guard is over-triggering on "
            + $"an APPEARED identity that vacated nothing. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr ObjId Caller"], incrementalByName["Incr ObjId Caller"]);
    }

    // ---------------------------------------------------------------------------------------
    // The same folding through an ENUM's object id. --dump-csharp shows the caller writing
    // `NavOption.Create(NCLEnumMetadata.Create(90320), 1)` — so it folds the enum's OBJECT id
    // alongside the value ordinal #2928 already guards. An enum emits a ZERO-BYTE C# file, so
    // there is no dispatch surface here at all and nothing can be loud.
    // ---------------------------------------------------------------------------------------

    private const string EnumOneBefore = """
        enum 90320 "Incr ObjId Enum One"
        {
            Extensible = false;
            value(0; Alpha) { }
            value(1; Beta) { }
        }
        """;

    private const string EnumTwoBefore = """
        enum 90321 "Incr ObjId Enum Two"
        {
            Extensible = false;
            value(0; Alpha) { }
            value(1; Beta) { }
        }
        """;

    private const string EnumCallerSrc = """
        codeunit 90322 "Incr ObjId Enum Caller"
        {
            procedure UseOne(): Integer
            var
                E: Enum "Incr ObjId Enum One";
            begin
                E := Enum::"Incr ObjId Enum One"::Beta;
                exit(E.AsInteger());
            end;

            procedure UseTwo(): Integer
            var
                E: Enum "Incr ObjId Enum Two";
            begin
                E := Enum::"Incr ObjId Enum Two"::Beta;
                exit(E.AsInteger());
            end;
        }
        """;

    /// <summary>
    /// The hazard through an enum's object id, and the most invisible shape of all: the two enums
    /// swap ids while every VALUE keeps its ordinal, so FoldedOrdinalMoved has nothing to report
    /// even where it can see an object at all, and the enums' own emitted C# is zero bytes.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_SwappingTwoEnumObjectIds_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("ObjIdEnumOne.al", EnumOneBefore);
        WriteAl("ObjIdEnumTwo.al", EnumTwoBefore);
        WriteAl("ObjIdEnumCaller.al", EnumCallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "ObjIdEnumModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baseline = ByName(baselineOut);

        // Swap the two enums' object ids in one cycle. Names, values and ordinals untouched.
        WriteAl("ObjIdEnumOne.al", EnumOneBefore.Replace("enum 90320", "enum 90321", StringComparison.Ordinal));
        WriteAl("ObjIdEnumTwo.al", EnumTwoBefore.Replace("enum 90321", "enum 90320", StringComparison.Ordinal));

        var incremental = compiler.TryEmitIncremental(
            new[] { _root }, "ObjIdEnumModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "ObjIdEnumModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        var fresh = ByName(freshOut);

        Assert.NotEqual(baseline["Incr ObjId Enum Caller"], fresh["Incr ObjId Enum Caller"]);
        Assert.Contains("NCLEnumMetadata.Create(90320)", baseline["Incr ObjId Enum Caller"], StringComparison.Ordinal);

        Assert.True(incremental == null,
            "the incremental path took the fast path after two enums swapped object ids. The caller "
            + "was not re-emitted, so `UseOne` still writes NCLEnumMetadata.Create(90320) — which is "
            + "now `Incr ObjId Enum Two`. Every value kept its ordinal, so FoldedOrdinalMoved has "
            + "nothing to report, and an enum emits a zero-byte C# file, so there is no dispatch "
            + "surface to be loud on. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr ObjId Enum Caller"] == fresh["Incr ObjId Enum Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build.")
            + $" fallbackReason: '{fallbackReason}'");

        Assert.Contains("Incr ObjId Enum", fallbackReason, StringComparison.Ordinal);
        Assert.Contains("object id", fallbackReason, StringComparison.OrdinalIgnoreCase);
    }
}
