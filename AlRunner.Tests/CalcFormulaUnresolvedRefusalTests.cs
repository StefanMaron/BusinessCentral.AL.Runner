// CalcFormulaUnresolvedRefusalTests — the refusal half of #3279.
//
// WHAT IS PINNED, AND WHY IT NEEDED ITS OWN TEST
// ----------------------------------------------
// CalcFormulaExtensionFieldWiringTests proves the RECORDING: a CalcFormula naming something the
// runner cannot resolve builds no formula and notes which reference failed. That is the "does
// not answer a wrong number" half. This file is the other half — the throw that turns the note
// into something an AL author ever sees. Deleting
// `ThrowIfCalcFormulaReferenceUnresolved(fieldObj);` from FlowFieldPatches.ClassifyCalcFieldsRequest
// left every other test in the repository green, which is what this file exists to stop:
// without it, CalcFields raises BC's own "You must define a CalcFormula for the {0} FlowField in
// the {1} table", which sends the author to a declaration that is already correct and names
// neither the reason nor the reference that failed.
//
// WHY THIS IS NOT AN AL TEST IN tests/runner-extras/
// --------------------------------------------------
// Measured, not assumed: a bundle whose CalcFormula names an absent table or field does not
// compile. The runner drives Microsoft's own AL compiler, which rejects all three positions —
//
//   error AL0185: Table 'CFU Absent Line' is missing
//   error AL0186: Reference 'CFU Absent Amount' in application object 'CFU Line' does not exist
//   error AL0186: Reference 'CFU Absent Arm Field' in application object 'CFU Line' does not exist
//
// — and the objects are dropped from the module (EMIT-EXCLUDED), so the tests never run. The
// state this refusal answers is reachable only through a runner-side metadata gap, never from
// AL source, which is also why it cannot be asked of a service tier upstream. So it is pinned
// here, at the seam, against a REAL NCLMetaTable built by the runner's own builder from AL
// source — the same entry point CalcFields uses — rather than against a hand-made mock.
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CalcFormulaUnresolvedRefusalTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    // Process-wide unique among AlRunner.Tests statics: these land in the same static
    // _parsedTables / _metaTableCache the whole assembly shares.
    private const int ParentTableId = 93960;
    private const int LineTableId = 93961;
    private const int ResolvableFlowFieldId = 4;
    private const int UnresolvableFlowFieldId = 5;

    public CalcFormulaUnresolvedRefusalTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3279-refusal");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void CalcFieldsOnAFlowFieldWhoseFormulaDidNotResolve_RefusesNamingTheTableAndTheField()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var parent = BuildParentTable();

        Assert.True(parent.TryGetFieldByNo(UnresolvableFlowFieldId, out var unresolved));
        // The precondition, asserted rather than assumed: the builder could not resolve
        // "CFUX Absent Line", so BC's own ctor fell back to the EmptyFormula singleton. That is
        // exactly the state BC refuses with MustDefineFormula, and the state this fix
        // re-classifies as a runner gap.
        Assert.Same(NCLMetaCalculationFormula.EmptyFormula, unresolved.CalculationFormula);

        var refusal = Assert.Throws<RunnerOutOfScopeException>(
            () => ClassifyCalcFieldsRequest(unresolved));

        // The reason anchor a tests/expectations entry or a developer would match on...
        Assert.Contains("calcformula-reference-unresolved", refusal.Reason);
        // ...and the two names without which the message is no better than BC's: WHICH table
        // the runner was looking in, and WHICH reference it could not find there.
        Assert.Contains("CFUX Absent Line", refusal.Message);
        Assert.Contains("Unresolved Total", refusal.Message);
        Assert.Contains("CFUX Parent", refusal.Message);

        // Not BC's wording. A refusal that said this would send the AL author to a CalcFormula
        // that is already correct — the whole reason the runner does not reuse it here.
        Assert.DoesNotContain("You must define a CalcFormula", refusal.Message);

        // `not-yet-implemented` on purpose: ApplicationObjectBasePatches.IsPermanentOutOfScope
        // traps a PERMANENTLY out-of-scope refusal into `false` for an AL [TryFunction]. Real BC
        // resolves these names, so the gap is the runner's, and trapping it would turn this
        // refusal back into the silent default it replaced.
        Assert.StartsWith("not-yet-implemented", refusal.Reason);
    }

    [SkippableFact]
    public void CalcFieldsOnAFlowFieldWhoseFormulaResolved_IsNotRefused()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var parent = BuildParentTable();

        Assert.True(parent.TryGetFieldByNo(ResolvableFlowFieldId, out var resolvable));
        Assert.NotSame(NCLMetaCalculationFormula.EmptyFormula, resolvable.CalculationFormula);

        // The scoping control: the same classification call, on a FlowField declared on the same
        // table by the same builder, must pass the field through rather than refuse. A refusal
        // keyed on the table instead of on the field, or one that fired whenever any note
        // existed, fails here.
        var flowFields = ClassifyCalcFieldsRequest(resolvable);
        Assert.Single(flowFields);
        Assert.Same(resolvable, flowFields[0]);
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the parent table's real NCLMetaTable from AL source, through the same entry point
    /// CalcFields reaches. Two FlowFields: one whose source table this bundle declares, one
    /// naming a table nothing declares — which is what a runner-side metadata gap looks like
    /// from the builder's point of view, and the only way to reach that state at all (AL that
    /// names an absent table does not compile).
    /// </summary>
    private NCLMetaTable BuildParentTable()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Line.al"), $$"""
            table {{LineTableId}} "CFUX Line"
            {
                fields
                {
                    field(1; "Entry No."; Integer) { }
                    field(2; "Doc No."; Code[20]) { }
                    field(3; Amount; Decimal) { }
                }
                keys { key(PK; "Entry No.") { Clustered = true; } }
            }
            """);
        File.WriteAllText(Path.Combine(srcDir, "Parent.al"), $$"""
            table {{ParentTableId}} "CFUX Parent"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field({{ResolvableFlowFieldId}}; "Resolvable Total"; Decimal)
                    {
                        FieldClass = FlowField;
                        CalcFormula = sum("CFUX Line".Amount where("Doc No." = field("No.")));
                        Editable = false;
                    }
                    field({{UnresolvableFlowFieldId}}; "Unresolved Total"; Decimal)
                    {
                        FieldClass = FlowField;
                        CalcFormula = sum("CFUX Absent Line".Amount where("Doc No." = field("No.")));
                        Editable = false;
                    }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }
            """);
        RecordPatches.AddSourceDir(srcDir);

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        var parent = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, ParentTableId, false, 0);
        Assert.NotNull(parent);
        return parent!;
    }

    /// <summary>
    /// Drive <c>FlowFieldPatches.ClassifyCalcFieldsRequest</c> — the private method BC's own
    /// <c>RecordImplementation.CalcFieldsAsync</c> loop is replaced by, and the single place a
    /// CalcFields request is refused. Returns the FlowFields it accepted; throws whatever it
    /// throws, with the reflection wrapper unwrapped so the test sees the real exception type.
    /// </summary>
    private static List<object> ClassifyCalcFieldsRequest(NCLMetaField field)
    {
        var m = typeof(FlowFieldPatches).GetMethod("ClassifyCalcFieldsRequest",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "FlowFieldPatches.ClassifyCalcFieldsRequest not found — this test drives it.");
        var blobFields = new List<object>();
        var flowFields = new List<object>();
        try
        {
            m.Invoke(null, new object?[] { new object[] { field }, blobFields, flowFields });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(tie.InnerException).Throw();
        }
        return flowFields;
    }
}
