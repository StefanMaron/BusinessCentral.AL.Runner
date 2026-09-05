// BcCompilerIncrementalConstantRebindTests — issue #2571.
//
// A SECOND family of edits where BcCompiler.Incremental.cs's "an unmodified caller never needs
// re-emitting" argument does not hold, and where the damage is silent. #2548 found the first one
// (an added overload); this one is not about methods at all, which is exactly why the file
// header's argument does not reach it.
//
// That argument is entirely about METHOD DISPATCH: a cross-object call compiles to
// `new NavCodeunitHandle(this, <object id>).Target.Invoke(<memberId>, args)`, so a breaking edit
// retires a member id and the call throws NavNCLMissingMethodException — loud.
//
// An ENUM emits no dispatch surface whatsoever. Measured with `--dump-csharp` on BC 28.x: a
// two-value `enum 90280 "Probe Enum"` produces a ZERO-BYTE C# file, and the caller that says
// `E := Enum::"Probe Enum"::Beta` compiles to a folded ordinal LITERAL in the CALLER's own C#:
//
//     this.e = NavOption.Create(NCLEnumMetadata.Create(90280), 1);
//                                                             ^ Beta's ordinal, baked here
//
// Renumbering Beta from 1 to 7 changes that literal to 7 in a cold build — in the CALLER's file,
// while only the ENUM's file was edited. There is no member id, no OnInvoke switch and no
// dispatch involved, so nothing can throw: an un-rebound caller keeps writing ordinal 1 into a
// field whose metadata now says Beta is 7. No exception, no diagnostic, no log line.
//
// The same fold happens for a table field's Option members — `R.Status := R.Status::Closed`
// compiles to `NavOption.Create(..., 2)` against `OptionMembers = Open,Released,Closed` — so the
// hazard is a class, not a single case. This file pins the enum half; the table half is tracked
// separately (see the PR for #2571).
//
// Each RED test has a control that would be broken by "just always fall back", which would delete
// the fast path this whole file exists to provide.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalConstantRebindTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalConstantRebindTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-constant-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    /// <summary>The callee. `Beta` sits at ordinal 1; `Alpha` exists so the enum has a value
    /// neither edit disturbs.</summary>
    private const string EnumBefore = """
        enum 90300 "Incr Const Enum"
        {
            Extensible = false;
            value(0; Alpha) { Caption = 'Alpha'; }
            value(1; Beta) { Caption = 'Beta'; }
        }
        """;

    /// <summary>The hazardous edit: `Beta` keeps its NAME and moves its ORDINAL. Every caller that
    /// wrote `::Beta` folded the old ordinal into its own C# and must be re-emitted.</summary>
    private const string EnumRenumbered = """
        enum 90300 "Incr Const Enum"
        {
            Extensible = false;
            value(0; Alpha) { Caption = 'Alpha'; }
            value(7; Beta) { Caption = 'Beta'; }
        }
        """;

    /// <summary>The control edit: a value ADDED under a name the enum did not already have, at an
    /// ordinal no existing value used. No existing caller can reference it (it did not exist when
    /// they were compiled) and no existing name's ordinal moves, so every folded literal already
    /// in cached C# stays correct and the fast path must still apply.</summary>
    private const string EnumValueAdded = """
        enum 90300 "Incr Const Enum"
        {
            Extensible = false;
            value(0; Alpha) { Caption = 'Alpha'; }
            value(1; Beta) { Caption = 'Beta'; }
            value(2; Gamma) { Caption = 'Gamma'; }
        }
        """;

    /// <summary>The second control: a CAPTION-only edit. Nothing an existing caller folded can
    /// have moved, so this must stay on the fast path — it is what stops the guard degenerating
    /// into "any edit to an enum falls back".</summary>
    private const string EnumCaptionEdited = """
        enum 90300 "Incr Const Enum"
        {
            Extensible = false;
            value(0; Alpha) { Caption = 'Alpha renamed'; }
            value(1; Beta) { Caption = 'Beta'; }
        }
        """;

    /// <summary>The caller, byte-for-byte identical across every edit. It folds `Beta`'s ordinal
    /// into its own generated C#, so it is only ever correct after the renumber if the delta
    /// decides it must be re-emitted.</summary>
    private const string CallerSrc = """
        codeunit 90301 "Incr Const Caller"
        {
            procedure Call(): Integer
            var
                E: Enum "Incr Const Enum";
            begin
                E := Enum::"Incr Const Enum"::Beta;
                exit(E.AsInteger());
            end;
        }
        """;

    /// <summary>Compiles the fixture, records an incremental baseline, then applies
    /// <paramref name="editedEnum"/> and returns both the incremental attempt and an independent
    /// cold build of the same post-edit tree.</summary>
    private (BcEmitOutput? Incremental, string FallbackReason, Dictionary<string, string> Baseline, Dictionary<string, string> Fresh)
        RunEdit(string editedEnum)
    {
        WriteAl("ConstEnum.al", EnumBefore);
        WriteAl("ConstCaller.al", CallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "ConstRebindModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);

        // Edit ONLY the enum. The caller's file is not touched at all.
        WriteAl("ConstEnum.al", editedEnum);

        var incrOut = compiler.TryEmitIncremental(
            new[] { _root }, "ConstRebindModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "ConstRebindModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        return (incrOut, fallbackReason, baselineByName, ByName(freshOut));
    }

    /// <summary>
    /// The hazard. Renumbering an existing enum value must not leave the fast path shipping a
    /// caller that still folds the value's PREVIOUS ordinal.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_RenumberingAnEnumValue_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, baseline, fresh) = RunEdit(EnumRenumbered);

        // Fixture guard: the edit really did move what the caller folded. Without this the whole
        // test could pass against an edit that changed nothing.
        Assert.NotEqual(baseline["Incr Const Caller"], fresh["Incr Const Caller"]);

        Assert.True(incremental == null,
            "the incremental path took the fast path after an enum value was renumbered. The caller "
            + "was not re-emitted, so it still folds Beta's PREVIOUS ordinal (1) even though the enum "
            + "now declares Beta as 7. An enum emits no dispatch surface at all — no member id, no "
            + "OnInvoke switch — so nothing throws and the run goes green on a stale constant. "
            + "Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr Const Caller"] == fresh["Incr Const Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build."));

        // A fallback nobody can read is the silent default loud-failures.md forbids: the reason
        // must name the object and the shape.
        Assert.Contains("Incr Const Enum", fallbackReason, StringComparison.Ordinal);
        Assert.Contains("ordinal", fallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control, and the reason the fallback above is not simply "give up on every enum edit":
    /// a value added under a NEW name at an unused ordinal moves nothing any existing caller
    /// folded, so the fast path must still apply — and what it ships must equal a cold build.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_AddingAnEnumValueUnderANewName_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, _, fresh) = RunEdit(EnumValueAdded);

        Assert.True(incremental != null,
            "adding an enum value under a name the enum did not already have fell back to a full "
            + "compile. No existing value's ordinal moved and no existing caller could reference the "
            + "new name, so every folded literal in cached C# is still correct — the guard is "
            + $"over-triggering. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr Const Caller"], incrementalByName["Incr Const Caller"]);
    }

    /// <summary>
    /// The second control: a caption edit changes no ordinal at all. If this falls back, the guard
    /// is keyed on "the enum's file changed" rather than on "a folded constant moved".
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_EditingOnlyAnEnumValueCaption_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, _, fresh) = RunEdit(EnumCaptionEdited);

        Assert.True(incremental != null,
            "a caption-only edit to an enum fell back to a full compile. No ordinal moved, so no "
            + $"caller's folded literal can be stale — the guard is over-triggering. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr Const Caller"], incrementalByName["Incr Const Caller"]);
    }

    // ---------------------------------------------------------------------------------------
    // The SAME hazard, reached through a table field's Option members rather than an enum. This
    // is what makes #2571 a class rather than a single case: `R.Status := R.Status::Closed`
    // compiles to `NavOption.Create(..., 2)` in the CALLER's C# — measured with --dump-csharp —
    // where 2 is Closed's POSITION in `OptionMembers = Open,Released,Closed`. Inserting a member
    // ahead of Closed moves that position while the field, its id and its name all stay put, so
    // again nothing can throw.
    // ---------------------------------------------------------------------------------------

    private const string TableBefore = """
        table 90302 "Incr Const Tbl"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(5; Status; Option) { OptionMembers = Open,Released,Closed; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    /// <summary>The hazardous edit: a member INSERTED ahead of `Closed`, which moves Closed from
    /// ordinal 2 to 3. The field keeps its id, its name and its type.</summary>
    private const string TableMemberInserted = """
        table 90302 "Incr Const Tbl"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(5; Status; Option) { OptionMembers = Open,Pending,Released,Closed; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    /// <summary>The control edit: a member APPENDED after every existing one. Each existing
    /// member keeps its position, so every folded literal already in cached C# stays correct and
    /// the fast path must still apply.</summary>
    private const string TableMemberAppended = """
        table 90302 "Incr Const Tbl"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(5; Status; Option) { OptionMembers = Open,Released,Closed,Cancelled; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    private const string TableCallerSrc = """
        codeunit 90303 "Incr Const Tbl Caller"
        {
            procedure Call(): Integer
            var
                R: Record "Incr Const Tbl";
                V: Integer;
            begin
                R.Status := R.Status::Closed;
                V := R.Status;
                exit(V);
            end;
        }
        """;

    private (BcEmitOutput? Incremental, string FallbackReason, Dictionary<string, string> Baseline, Dictionary<string, string> Fresh)
        RunTableEdit(string editedTable)
    {
        WriteAl("ConstTbl.al", TableBefore);
        WriteAl("ConstTblCaller.al", TableCallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "ConstTblModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);

        WriteAl("ConstTbl.al", editedTable);

        var incrOut = compiler.TryEmitIncremental(
            new[] { _root }, "ConstTblModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "ConstTblModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        return (incrOut, fallbackReason, baselineByName, ByName(freshOut));
    }

    /// <summary>
    /// The hazard, reached through a table's Option members. Inserting a member ahead of one an
    /// unmodified caller references must not leave the fast path shipping that caller's previous
    /// folded position.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_InsertingAnOptionMemberAheadOfAnExistingOne_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, baseline, fresh) = RunTableEdit(TableMemberInserted);

        // Fixture guard: the edit really did move what the caller folded.
        Assert.NotEqual(baseline["Incr Const Tbl Caller"], fresh["Incr Const Tbl Caller"]);

        Assert.True(incremental == null,
            "the incremental path took the fast path after an Option member was inserted ahead of "
            + "`Closed`. The caller was not re-emitted, so it still folds Closed's PREVIOUS position "
            + "(2) even though Closed is now 3 — it writes the ordinal that now means `Released`. "
            + "The field kept its id, its name and its type, so nothing throws. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr Const Tbl Caller"] == fresh["Incr Const Tbl Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build."));

        Assert.Contains("Incr Const Tbl", fallbackReason, StringComparison.Ordinal);
        Assert.Contains("ordinal", fallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control: appending an Option member after every existing one moves nothing, so the fast
    /// path must still apply and what it ships must equal a cold build.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_AppendingAnOptionMember_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, _, fresh) = RunTableEdit(TableMemberAppended);

        Assert.True(incremental != null,
            "appending an Option member after every existing one fell back to a full compile. No "
            + "existing member's position moved, so every folded literal in cached C# is still "
            + $"correct — the guard is over-triggering. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr Const Tbl Caller"], incrementalByName["Incr Const Tbl Caller"]);
    }

    // ---------------------------------------------------------------------------------------
    // The third folded constant on the same element: a field's ID. `R.Status := ...` compiles to
    // `this.r.Target.SetFieldValueSafe(5, NavType.Option, ...)` — measured with --dump-csharp —
    // so the caller folds the field id too, by name, at compile time.
    //
    // Renumbering one field is loud at runtime (the id is simply absent). SWAPPING two fields of
    // the same type is not: every folded id still resolves, to the other field. Either way the
    // invariant the fast path has to hold is the same one the tests above assert — what it ships
    // must equal what a cold build produces — so the guard covers the id alongside the ordinals
    // rather than reasoning per-edit about which renumbers happen to be loud.
    // ---------------------------------------------------------------------------------------

    private const string IdTableBefore = """
        table 90304 "Incr Const Id Tbl"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(5; Amount; Integer) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    /// <summary>The hazardous edit: `Amount` keeps its name and its type, and changes its id.</summary>
    private const string IdTableRenumbered = """
        table 90304 "Incr Const Id Tbl"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(9; Amount; Integer) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    /// <summary>The control edit: a field ADDED under a new name and an unused id. Nothing an
    /// existing caller folded moves.</summary>
    private const string IdTableFieldAdded = """
        table 90304 "Incr Const Id Tbl"
        {
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(5; Amount; Integer) { }
                field(7; Quantity; Integer) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    private const string IdTableCallerSrc = """
        codeunit 90305 "Incr Const Id Caller"
        {
            procedure Call(): Integer
            var
                R: Record "Incr Const Id Tbl";
            begin
                R.Amount := 42;
                exit(R.Amount);
            end;
        }
        """;

    private (BcEmitOutput? Incremental, string FallbackReason, Dictionary<string, string> Baseline, Dictionary<string, string> Fresh)
        RunIdTableEdit(string editedTable)
    {
        WriteAl("ConstIdTbl.al", IdTableBefore);
        WriteAl("ConstIdCaller.al", IdTableCallerSrc);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "ConstIdModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);

        WriteAl("ConstIdTbl.al", editedTable);

        var incrOut = compiler.TryEmitIncremental(
            new[] { _root }, "ConstIdModule", appRootDir: null, out var fallbackReason);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "ConstIdModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        return (incrOut, fallbackReason, baselineByName, ByName(freshOut));
    }

    /// <summary>
    /// The hazard, reached through a field's id. Renumbering a field an unmodified caller
    /// references must not leave the fast path shipping that caller's previous folded id.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_RenumberingAField_FallsBackInsteadOfShippingAStaleCaller()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, baseline, fresh) = RunIdTableEdit(IdTableRenumbered);

        // Fixture guard: the edit really did move what the caller folded.
        Assert.NotEqual(baseline["Incr Const Id Caller"], fresh["Incr Const Id Caller"]);

        Assert.True(incremental == null,
            "the incremental path took the fast path after a field was renumbered. The caller was "
            + "not re-emitted, so it still folds Amount's PREVIOUS id (5) even though Amount is now "
            + "field 9. Shipped caller C# "
            + (incremental != null && ByName(incremental)["Incr Const Id Caller"] == fresh["Incr Const Id Caller"]
                ? "matched a cold build, so something else changed — re-derive this test."
                : "did NOT match a cold build."));

        Assert.Contains("Incr Const Id Tbl", fallbackReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: adding a field under a new name and an unused id moves nothing, so the fast
    /// path must still apply and what it ships must equal a cold build.
    /// </summary>
    [SkippableFact]
    public void TryEmitIncremental_AddingAFieldUnderANewName_StaysOnTheFastPathAndMatchesAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (incremental, fallbackReason, _, fresh) = RunIdTableEdit(IdTableFieldAdded);

        Assert.True(incremental != null,
            "adding a field under a name the table did not already have fell back to a full compile. "
            + "No existing field's id moved, so every folded id in cached C# is still correct — the "
            + $"guard is over-triggering. fallbackReason: {fallbackReason}");

        var incrementalByName = ByName(incremental!);
        Assert.Equal(fresh["Incr Const Id Caller"], incrementalByName["Incr Const Id Caller"]);
    }
}
