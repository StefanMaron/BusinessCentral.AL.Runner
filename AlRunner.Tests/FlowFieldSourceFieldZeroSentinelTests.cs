// FlowFieldSourceFieldZeroSentinelTests — field id 0 is a REAL field, not "no field" (#3307).
//
// The defect
// ----------
// FlowFieldPatches.CalcFlowFieldValuesCore resolves a formula's aggregated SOURCE field from
// the MetaCalcFormula's FieldId. It used to gate that lookup on `fieldId != 0`, reading id 0
// as "this formula names no source field" — which is true for count() and exist(), the two
// CalculationMethods that carry no source field.
//
// That sentinel was only ever safe because nothing could NAME field 0. SystemRowVersion can.
// Microsoft's AL compiler synthesizes it at field id 0 with metadata name `timestamp` —
// SynthesizedFieldHelper.AppendSystemFields in Microsoft.Dynamics.Nav.CodeAnalysis:
//
//     if (runtimeVersionOrCurrent >= RuntimeVersion.Fall2022)
//         builder.Add(SynthesizedFieldSymbol.Create(
//             owner, 0, "SystemRowVersion", NavCorLib.BigIntegerType, "timestamp"));
//
// confirmed a second time in the same assembly by XmlPortMetadataEmitter.GetFieldName, which
// special-cases exactly the triple (Id == 0, Name == "SystemRowVersion", MetadataName ==
// "timestamp"). So `max("T".SystemRowVersion where(...))` arrived with fieldId == 0, skipped
// the lookup, left srcFieldColumn at -1, and the aggregate branch was never entered. The
// FlowField answered TypedDefaultForField — 0 — for every possible row set.
//
// That is the silent-wrong-answer shape `.claude/rules/loud-failures.md` exists to prevent,
// and it is the worst-disguised version of it: a rowversion of 0 reads like "this row was
// never stamped" rather than like a bug, so nothing about the answer looks wrong.
//
// What this file pins, and what it deliberately does not
// ------------------------------------------------------
// The BC-observable claim — what max/min/lookup over SystemRowVersion actually answers, and
// that a where-arm still narrows it — is plain BC behaviour and is asserted UPSTREAM, against
// a real service tier, in the al-language corpus: CFSF Tests (codeunit 60818,
// record/TestCalcFormulaSystemFieldsTests.Codeunit.al), which is the same fixture corpus PR
// #216 used for the other five system fields. Repeating it here would be the runner agreeing
// with itself.
//
// So this file pins only the runner-side MECHANISM that made the claim unprovable: that the
// source-field lookup is gated on the CalculationMethod, which is where BC states "this
// formula has no source field", rather than on an id being zero, which merely correlated with
// it until BC put a real field at id 0.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class FlowFieldSourceFieldZeroSentinelTests
{
    private static string FlowFieldPatchesSource()
    {
        var path = Path.Combine(RepoRoot(), "AlRunner", "Patches", "FlowFieldPatches.cs");
        Assert.True(File.Exists(path), $"expected FlowFieldPatches.cs at {path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "AlRunner.sln"))
                || Directory.Exists(Path.Combine(d.FullName, "AlRunner", "Patches")))
                return d.FullName;
        throw new InvalidOperationException("could not locate the repository root from " + dir);
    }

    /// The source-field lookup must not be gated on the field id being non-zero.
    ///
    /// Stated as the absence of the specific guard rather than as a general "no zero
    /// comparisons anywhere", so it names the one line that regressed and cannot be satisfied
    /// by renaming a variable.
    [Fact]
    public void SourceFieldLookup_IsNotGatedOnFieldIdBeingNonZero()
    {
        var src = FlowFieldPatchesSource();

        Assert.DoesNotContain("srcTable != null && fieldId != 0", src);
        Assert.DoesNotMatch(
            new Regex(@"if\s*\(\s*srcTable\s*!=\s*null\s*&&\s*fieldId\s*!=\s*0\s*\)"),
            src);
    }

    /// ...and it IS gated on the CalculationMethod instead, which is the fact the id was
    /// standing in for. Count and Exist are exactly the two methods BC gives no source field.
    [Fact]
    public void SourceFieldLookup_IsGatedOnTheCalculationMethodInstead()
    {
        var src = FlowFieldPatchesSource();

        Assert.Contains("formulaHasSourceField", src);
        Assert.Matches(
            new Regex(
                @"bool\s+formulaHasSourceField\s*=\s*!Equals\(calcMethod,\s*_cmCount\)\s*"
                + @"&&\s*!Equals\(calcMethod,\s*_cmExist\)\s*;"),
            src);
        Assert.Contains("if (srcTable != null && formulaHasSourceField)", src);
    }

    /// The rewritten guard has to keep the property the old one had: count() and exist() must
    /// still take the no-source-field path. If a future edit dropped either method from the
    /// predicate, a count formula would resolve field 0 — the timestamp column — as its source
    /// field, which is exactly the confusion this issue was about, pointed the other way.
    [Fact]
    public void CountAndExist_AreBothStillExcluded()
    {
        var src = FlowFieldPatchesSource();
        var m = Regex.Match(src, @"bool\s+formulaHasSourceField\s*=\s*([^;]+);");

        Assert.True(m.Success, "formulaHasSourceField must be declared with an initialiser");
        var predicate = m.Groups[1].Value;

        Assert.Contains("_cmCount", predicate);
        Assert.Contains("_cmExist", predicate);
        Assert.DoesNotContain("_cmSum", predicate);
        Assert.DoesNotContain("_cmMin", predicate);
        Assert.DoesNotContain("_cmMax", predicate);
        Assert.DoesNotContain("_cmLookup", predicate);
        Assert.DoesNotContain("_cmAverage", predicate);
    }
}
