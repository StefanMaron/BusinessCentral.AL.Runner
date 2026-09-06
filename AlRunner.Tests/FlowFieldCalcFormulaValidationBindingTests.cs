// FlowFieldCalcFormulaValidationBindingTests — issue #2970.
//
// These are RUNNER-MECHANISM tests, not claims about what real BC does. The BC-observable
// claim ("a FlowField and the source field its CalcFormula aggregates must have the same
// type, and CalcFields refuses at calculation time when they differ") belongs upstream in
// StefanMaron/BusinessCentral.AL.Language.Tests, where a live service tier adjudicates it —
// corpus PR #183 and the already-merged #171 carry it.
//
// What THIS file pins is the wiring the fix rests on, and it exists because that wiring can
// break SILENTLY.
//
// FlowFieldPatches replaces BC's FlowFieldsHelper.CalcFieldsAsync, which is the method
// GetDistinctSourceTablesFromFlowFields sits under — and DistinctSourceTable.AddField's very
// first statement is FlowFieldsHelper.CheckFlowFieldProperties(field). Replacing the outer
// method therefore removed ALL FIVE of that validator's refusals in one go, which is how a
// mistyped average() came to compute a number here while real BC rejected it on all eight
// legs. The fix calls BC's own validator rather than restating any of its five rules.
//
// Two ways that could regress without any other test noticing:
//
//   1. The bind is by name and signature against an INTERNAL method on a precompiled DLL. If
//      a BC service update renames it, changes its parameter type, or makes it an instance
//      method, Register() silently falls back and every CalcFields starts throwing
//      out-of-scope. Nothing in the C# call graph shows that; the reflection lookup just
//      returns null. BindsAgainstTheShippedBcArtifact catches it in milliseconds.
//
//   2. All five refusals are reachable only because they live in that ONE method. If a future
//      BC splits them across several methods, calling this one keeps compiling and keeps
//      passing every existing test while quietly covering fewer rules than before.
//      CarriesAllFiveOfBcsRefusals reads the shipped IL and fails when that stops being true.
//
// And one ordering property of our own code: BC validates every FlowField in a CalcFields
// call BEFORE aggregating any of them, so a refused call writes nothing. Folding the check
// into the aggregation loop instead would compute the valid fields and then throw — still
// "an error", still green on any test that only asserts the error, and wrong.
using System;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class FlowFieldCalcFormulaValidationBindingTests
{
    private readonly BcEngineFixture _engine;

    public FlowFieldCalcFormulaValidationBindingTests(BcEngineFixture engine) => _engine = engine;

    /// <summary>The Cecil-rewritten Ncl this test host itself loaded.</summary>
    private static string NclPath => Path.Combine(
        Path.GetDirectoryName(typeof(FlowFieldCalcFormulaValidationBindingTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static MethodInfo? CheckFlowFieldPropertiesInfo()
    {
        var t = typeof(NCLMetaField).Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.FlowFieldsHelper");
        return t?.GetMethod("CheckFlowFieldProperties",
            BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(NCLMetaField) }, null);
    }

    /// <summary>
    /// The exact lookup <see cref="FlowFieldPatches.Register"/> performs, asserted against the
    /// artifact actually shipped. A null here means every CalcFields in the suite would take
    /// the RunnerOutOfScopeException branch instead of validating.
    /// </summary>
    [Fact]
    public void BindsAgainstTheShippedBcArtifact()
    {
        var m = CheckFlowFieldPropertiesInfo();

        Assert.NotNull(m);
        Assert.True(m!.IsStatic, "CheckFlowFieldProperties must stay static — the patch binds it as an open delegate");
        Assert.Equal(typeof(void), m.ReturnType);
        Assert.Single(m.GetParameters());
        Assert.Equal(typeof(NCLMetaField), m.GetParameters()[0].ParameterType);

        // The delegate construction itself, which is what Register() does. Delegate.CreateDelegate
        // throws rather than returning null on a signature mismatch, so this is the real check.
        var d = (Action<NCLMetaField>)Delegate.CreateDelegate(typeof(Action<NCLMetaField>), m);
        Assert.NotNull(d);
        Assert.Equal(m, d.Method);
    }

    /// <summary>
    /// BC raises five distinct refusals from CheckFlowFieldProperties, identified by their
    /// NavCSideException error numbers. Calling that one method is only equivalent to
    /// reproducing all five while all five remain inside it, so the numbers are read straight
    /// out of the shipped IL.
    ///
    /// 18023443 appears twice in BC's body (the Sum/Average arm and the Min/Max/Lookup arm)
    /// and is asserted once — the count is deliberately not pinned, because the C# compiler
    /// is free to share one constant load between the two arms.
    /// </summary>
    [Theory]
    [InlineData(18023676)] // Count must be Integer/BigInteger
    [InlineData(18023674)] // Sum/Average source must be aggregatable
    [InlineData(18023443)] // Sum/Average AND Min/Max/Lookup type mismatch
    [InlineData(18023675)] // Exists must be Boolean
    public void CarriesAllFiveOfBcsRefusals(int bcErrorNumber)
    {
        using var asm = AssemblyDefinition.ReadAssembly(NclPath);
        var helper = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.FlowFieldsHelper");
        Assert.NotNull(helper);

        var check = helper!.Methods.SingleOrDefault(m => m.Name == "CheckFlowFieldProperties");
        Assert.NotNull(check);
        Assert.True(check!.HasBody, "CheckFlowFieldProperties must have a readable body");

        var constants = check.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldc_I4)
            .Select(i => (int)i.Operand!)
            .ToHashSet();

        Assert.Contains(bcErrorNumber, constants);
    }

    /// <summary>
    /// The ordering guarantee, read off our own IL: CalcFlowFieldValuesCore must call
    /// ValidateFlowFieldFormulas, and must do so before it reads the session's
    /// DataAccessSource — the first statement of the aggregation half of that method.
    ///
    /// This is the property a per-field check folded into the aggregation loop would break.
    /// Such a version still throws, so a test that only asserts "CalcFields errors" stays
    /// green; what changes is that a valid FlowField named alongside an invalid one gets
    /// computed and left in the buffer, which real BC never does.
    /// </summary>
    [Fact]
    public void ValidatesEveryFlowFieldBeforeAggregatingAnyOfThem()
    {
        var alRunnerPath = typeof(FlowFieldPatches).Assembly.Location;
        using var asm = AssemblyDefinition.ReadAssembly(alRunnerPath);

        var patches = asm.MainModule.GetType("AlRunner.Patches.FlowFieldPatches");
        Assert.NotNull(patches);

        var core = patches!.Methods.SingleOrDefault(m => m.Name == "CalcFlowFieldValuesCore");
        Assert.NotNull(core);
        Assert.True(core!.HasBody);

        var instructions = core.Body.Instructions.ToList();

        int validateAt = instructions.FindIndex(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == "ValidateFlowFieldFormulas");
        Assert.True(validateAt >= 0,
            "CalcFlowFieldValuesCore must call ValidateFlowFieldFormulas — without it none of "
            + "BC's five CalcFormula refusals is reproduced (#2970)");

        // The aggregation half begins by loading the cached FieldInfo for
        // NavSession.dataAccessSource. Everything that computes a value is downstream of it.
        int aggregationAt = instructions.FindIndex(i =>
            i.OpCode == OpCodes.Ldsfld
            && i.Operand is FieldReference fr
            && fr.Name == "_fSessionDataAccessSource");
        Assert.True(aggregationAt >= 0,
            "expected CalcFlowFieldValuesCore to read _fSessionDataAccessSource — if this "
            + "changed, re-anchor the ordering assertion on whatever now starts the "
            + "aggregation half rather than deleting the test");

        Assert.True(validateAt < aggregationAt,
            $"ValidateFlowFieldFormulas must run before any aggregate is computed, but the "
            + $"call is at instruction {validateAt} and the aggregation half starts at "
            + $"{aggregationAt}. BC validates every FlowField in a CalcFields call before "
            + "aggregating any of them, so a refused call must write nothing.");
    }

    /// <summary>
    /// The negative direction, read off our own IL: when the validator cannot be bound, the
    /// runner must REFUSE rather than skip validation.
    ///
    /// Skipping is the tempting edit — it keeps every existing test green, because the only
    /// thing it changes is that CalcFormulas real BC rejects quietly compute a value again,
    /// which is the original defect. So the throw is pinned structurally: a
    /// RunnerOutOfScopeException must be constructed inside ValidateFlowFieldFormulas.
    /// </summary>
    [Fact]
    public void MissingValidatorRefusesInsteadOfSkippingValidation()
    {
        var alRunnerPath = typeof(FlowFieldPatches).Assembly.Location;
        using var asm = AssemblyDefinition.ReadAssembly(alRunnerPath);

        var patches = asm.MainModule.GetType("AlRunner.Patches.FlowFieldPatches");
        var validate = patches!.Methods.SingleOrDefault(m => m.Name == "ValidateFlowFieldFormulas");
        Assert.NotNull(validate);
        Assert.True(validate!.HasBody);

        var throwsOos = validate.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Newobj
            && i.Operand is MethodReference mr
            && mr.DeclaringType.Name == "RunnerOutOfScopeException");

        Assert.True(throwsOos,
            "ValidateFlowFieldFormulas must throw RunnerOutOfScopeException when BC's validator "
            + "cannot be bound. Silently skipping validation is exactly the defect #2970 fixed: "
            + "the runner computes a value real BC refuses, and every test stays green.");

        // And it must still call the validator on the normal path — a method that only ever
        // throws would satisfy the assertion above while validating nothing.
        var invokesValidator = validate.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
            && i.Operand is MethodReference mr
            && mr.Name == "Invoke"
            && mr.DeclaringType.FullName.Contains("Action`1"));

        Assert.True(invokesValidator,
            "ValidateFlowFieldFormulas must invoke the bound Action<NCLMetaField> — that call "
            + "IS the reproduction of all five of BC's refusals");
    }
}
