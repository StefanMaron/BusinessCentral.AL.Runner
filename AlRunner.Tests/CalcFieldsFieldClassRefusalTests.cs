// CalcFieldsFieldClassRefusalTests — AlRunner#3012.
//
// These are RUNNER-MECHANISM tests, not claims about what real BC does. The BC-observable
// claim ("Record.CalcFields accepts a field only if it is a FlowField carrying a CalcFormula,
// or a BLOB; everything else is refused before anything is calculated") is measured upstream
// against a live BC service tier — record/TestCalcFieldsFieldClass.Codeunit.al in
// StefanMaron/BusinessCentral.AL.Language.Tests, corpus PR #189.
//
// What THIS file pins is the wiring the fix rests on, which the corpus cannot see, and every
// one of these can regress SILENTLY:
//
//   * The runner REPLACES RecordImplementation.CalcFieldsAsync(DataError, NCLMetaField[], bool)
//     outright (FlowFieldPatches.RecordImpl_CalcFieldsAsync_3), so BC's own classification loop
//     — the one that raises both refusals — never runs. It has to be re-issued by the
//     replacement, and for four years it was not: every entry point dropped a non-FlowField on
//     the floor with `if (!Equals(fieldClass, _fcFlowField)) continue;`, so
//     `Rec.CalcFields("No.")` did nothing at all and reported success.
//
//   * The classification must run BEFORE the BLOB load and BEFORE the FlowField aggregation,
//     exactly as BC runs its loop before either. A version that classified per field inside
//     the work still throws — so a test asserting only "CalcFields errors" stays green — but
//     leaves the acceptable part of the call applied, which real BC never does.
//
//   * The AL-visible messages must be BC's own resources. If Lang.MustBeAFlowField or
//     Lang.MustDefineFormula stops resolving, BuildCalcFieldsRefusal silently falls back to a
//     literal kept here, and the runner's wording drifts from the service tier's without any
//     other test noticing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CalcFieldsFieldClassRefusalTests
{
    private readonly BcEngineFixture _engine;

    public CalcFieldsFieldClassRefusalTests(BcEngineFixture engine) => _engine = engine;

    /// <summary>The Cecil-rewritten Ncl this test host itself loaded.</summary>
    private static string NclPath => Path.Combine(
        Path.GetDirectoryName(typeof(CalcFieldsFieldClassRefusalTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static string? LangString(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var lang = asm.GetType("Microsoft.Dynamics.Nav.Common.Language.Lang", throwOnError: false);
            var value = lang?.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            ?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    private static AssemblyDefinition OpenOurAssembly() =>
        AssemblyDefinition.ReadAssembly(typeof(FlowFieldPatches).Assembly.Location);

    // Cecil reads method bodies LAZILY off the still-open file, so the AssemblyDefinition has
    // to outlive every Instruction walk below — hence the explicit `asm` parameter rather than
    // a self-contained lookup that disposes it on return.
    private static MethodDefinition OurMethod(AssemblyDefinition asm, string name)
    {
        var patches = asm.MainModule.GetType("AlRunner.Patches.FlowFieldPatches");
        Assert.NotNull(patches);

        var m = patches!.Methods.SingleOrDefault(x => x.Name == name);
        Assert.True(m != null, $"FlowFieldPatches.{name} not found — if it was renamed, re-anchor "
                               + "this test on the new name rather than deleting it (#3012)");
        Assert.True(m!.HasBody, $"FlowFieldPatches.{name} must have a readable body");
        return m;
    }

    private static int IndexOfCallTo(MethodDefinition m, string calleeName) =>
        m.Body.Instructions.ToList().FindIndex(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == calleeName);

    /// <summary>
    /// Both refusal messages come from BC's own resource class, and both must still say what
    /// the upstream corpus test asserts. A BC version that rewords either one breaks the
    /// corpus assertion too — this test is what makes that visible here rather than only on
    /// eight BC legs.
    /// </summary>
    [SkippableFact]
    public void BothRefusalMessagesComeFromBcsOwnLangResource()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Assert.Equal("The {0} field in the {1} table must be a FlowField.",
            LangString("MustBeAFlowField"));
        Assert.Equal("You must define a CalcFormula for the {0} FlowField in the {1} table.",
            LangString("MustDefineFormula"));
        Assert.Equal(
            "The field {0} in the {1} table is not a FlowField or a BLOB field and cannot be "
            + "passed in calls to CalcFields.",
            LangString("OnlyFlowFieldsAllowedInCallsToCalcFields"));
    }

    /// <summary>
    /// The ordering guarantee, read off our own IL: the RecordImplementation entry point must
    /// classify the field list BEFORE it loads a BLOB and BEFORE it enters the FlowField core.
    /// </summary>
    [Fact]
    public void RecordEntryPointClassifiesBeforeItLoadsOrCalculatesAnything()
    {
        using var asm = OpenOurAssembly();
        var entry = OurMethod(asm, "RecordImpl_CalcFieldsAsync_3");

        int classifyAt = IndexOfCallTo(entry, "ClassifyCalcFieldsRequest");
        Assert.True(classifyAt >= 0,
            "RecordImpl_CalcFieldsAsync_3 must call ClassifyCalcFieldsRequest — without it a "
            + "field that is neither a FlowField nor a BLOB is silently ignored (#3012)");

        int blobLoadAt = IndexOfCallTo(entry, "LoadBlobField");
        Assert.True(blobLoadAt >= 0,
            "expected RecordImpl_CalcFieldsAsync_3 to call LoadBlobField — if the BLOB load "
            + "moved, re-anchor this ordering assertion rather than deleting it");

        int calcAt = IndexOfCallTo(entry, "CalcFlowFieldValuesCore");
        Assert.True(calcAt >= 0,
            "expected RecordImpl_CalcFieldsAsync_3 to call CalcFlowFieldValuesCore");

        Assert.True(classifyAt < blobLoadAt,
            $"classification (instruction {classifyAt}) must precede the BLOB load "
            + $"({blobLoadAt}): BC throws before it loads any BLOB content, so a refused "
            + "CalcFields must leave the record's BLOB fields untouched");
        Assert.True(classifyAt < calcAt,
            $"classification (instruction {classifyAt}) must precede the FlowField core "
            + $"({calcAt}): a refused CalcFields computes nothing at all in BC, not even the "
            + "part of the field list that was acceptable");
    }

    /// <summary>
    /// The classification must REFUSE, not skip. Both of BC's record-level refusals are built
    /// through BuildCalcFieldsRefusal and thrown; a regression that turned either back into a
    /// `continue` would restore the exact silent-success defect #3012 is about, and no other
    /// runner-side test would fail.
    /// </summary>
    [Fact]
    public void ClassificationThrowsBothOfBcsRecordLevelRefusals()
    {
        using var asm = OpenOurAssembly();
        var classify = OurMethod(asm, "ClassifyCalcFieldsRequest");
        var instructions = classify.Body.Instructions.ToList();

        int refusalCalls = instructions.Count(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr && mr.Name == "BuildCalcFieldsRefusal");
        Assert.True(refusalCalls >= 2,
            $"ClassifyCalcFieldsRequest must build both of BC's record-level refusals — the "
            + $"non-FlowField one and the missing-CalcFormula one — but found {refusalCalls} "
            + "call(s) to BuildCalcFieldsRefusal");

        int throws = instructions.Count(i => i.OpCode == OpCodes.Throw);
        Assert.True(throws >= 2,
            $"ClassifyCalcFieldsRequest must THROW both refusals rather than skipping the "
            + $"field, but found {throws} throw instruction(s)");

        // The missing-CalcFormula refusal carries BC's own error number; the non-FlowField one
        // has none (BC constructs it through the message-only NavCSideException ctor).
        var constants = instructions
            .Where(i => i.OpCode == OpCodes.Ldc_I4)
            .Select(i => (int)i.Operand!)
            .ToHashSet();
        Assert.Contains(18023430, constants);
    }

    /// <summary>
    /// BC's third refusal in this area — 18023494, inside
    /// FlowFieldsHelper.GetDistinctSourceTablesFromFlowFields — is unreachable from AL because
    /// the record-level loop above throws first, but it still guards the FlowFieldsHelper entry
    /// point that BC's own code re-enters. It must stay wired to the validation pass.
    /// </summary>
    [Fact]
    public void HelperEntryPointStillCarriesBcs18023494Refusal()
    {
        using var asm = OpenOurAssembly();
        var validate = OurMethod(asm, "ValidateFlowFieldFormulas");
        Assert.True(IndexOfCallTo(validate, "BuildOnlyFlowFieldsAllowedRefusal") >= 0,
            "ValidateFlowFieldFormulas must refuse a non-FlowField reaching the FlowFieldsHelper "
            + "entry point, the way GetDistinctSourceTablesFromFlowFields does (#3012)");

        var builder = OurMethod(asm, "BuildOnlyFlowFieldsAllowedRefusal");
        var constants = builder.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldc_I4)
            .Select(i => (int)i.Operand!)
            .ToHashSet();
        Assert.Contains(18023494, constants);
    }

    /// <summary>
    /// The upstream shape this fix mirrors, read off the shipped artifact: BC still classifies
    /// the CalcFields field list inside RecordImplementation itself — reading
    /// Lang.MustBeAFlowField and raising 18023430 — rather than deferring it to FlowFieldsHelper.
    /// If a BC service update moves either refusal, the runner is reproducing a check at the
    /// wrong layer and the AL-visible message changes with it; nothing else here would notice,
    /// because the async body lives in a compiler-generated state machine that no call graph
    /// walks into.
    /// </summary>
    [Fact]
    public void BcStillClassifiesTheFieldListInsideRecordImplementation()
    {
        using var asm = AssemblyDefinition.ReadAssembly(NclPath);
        var recordImpl = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        Assert.NotNull(recordImpl);

        var bodies = new List<MethodDefinition>();
        void Collect(TypeDefinition t)
        {
            bodies.AddRange(t.Methods.Where(m => m.HasBody));
            foreach (var nested in t.NestedTypes) Collect(nested);
        }
        Collect(recordImpl!);

        bool readsMustBeAFlowField = bodies.Any(m => m.Body.Instructions.Any(i =>
            i.Operand is MethodReference mr && mr.Name == "get_MustBeAFlowField"));
        Assert.True(readsMustBeAFlowField,
            "RecordImplementation (or one of its async state machines) no longer reads "
            + "Lang.MustBeAFlowField — BC moved the non-FlowField refusal, so re-derive where "
            + "the runner should raise it instead of leaving the replacement at the old layer");

        bool raises18023430 = bodies.Any(m => m.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand! == 18023430));
        Assert.True(raises18023430,
            "RecordImplementation no longer raises 18023430 (MustDefineFormula) — BC moved the "
            + "missing-CalcFormula refusal");
    }
}
