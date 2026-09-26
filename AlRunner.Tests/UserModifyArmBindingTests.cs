// UserModifyArmBindingTests — issues #2363 and #4701.
//
// A RUNNER-MECHANISM test. The BC-observable claims are pinned upstream in the OnPrem app:
// corpus 61206 "Test User Auth Email Trigger" (what the User modify arm does) and corpus 61208
// "Test User Auth Email Order" (that it runs after the OnBeforeModify subscribers).
//
// What this pins is the runner's own wiring, which no C# call graph shows: that
// UserTableTriggerPatches.OnBeforeUserModify is prepended to RecordImplementation.ModifyRecordAsync
// — below NavRecord.ModifyAsync(4)'s subscriber and trigger dispatch, where BC's own
// SystemTableTriggers arm sits (#4701) — and to no NavRecord modify entry point, where it would run
// before the subscribers or twice; and that the BC member the patch invokes rather than
// re-implements, SystemTableTriggers.TrimAndAnalyzeAuthenticationEmail(string), still has the
// shape the reflection bind asks for.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class UserModifyArmBindingTests
{
    private const string UserModifyArm =
        "System.Void AlRunner.Patches.UserTableTriggerPatches::OnBeforeUserModify(System.Object)";

    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(UserModifyArmBindingTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static ModuleDefinition OpenNcl()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath), $"the Ncl is not present at '{RewrittenNclPath}'.");
        return ModuleDefinition.ReadModule(RewrittenNclPath);
    }

    private static MethodDefinition NavRecordMethod(ModuleDefinition module, string name, params string[] parameterTypeNames)
    {
        var method = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")?.Methods.FirstOrDefault(m =>
            m.Name == name && m.HasBody
            && m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(parameterTypeNames));
        Assert.True(method != null, $"NavRecord.{name}({string.Join(", ", parameterTypeNames)}) not found in Ncl.");
        return method!;
    }

    private static List<string> CalledMethods(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    /// <summary>A rewritten Ncl carries the delete arm's prepend, which this class asserts nothing
    /// about; an un-rewritten one is a skip, never a pass, and never a failure of the subject.</summary>
    private static void SkipUnlessRewritten(ModuleDefinition module)
        => Skip.IfNot(
            CalledMethods(NavRecordMethod(module, "ALDeleteAsync", "DataError", "Boolean", "Boolean"))
                .Any(n => n.Contains("UserTableTriggerPatches::OnAfterUserDelete(System.Object)", StringComparison.Ordinal)),
            $"'{RewrittenNclPath}' has not been Cecil-rewritten; run the runner once to warm the Cecil cache.");

    private static MethodDefinition RecordImplementationMethod(ModuleDefinition module, string name)
    {
        var method = module.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation")?.Methods.FirstOrDefault(m =>
            m.Name == name && m.HasBody
            && m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(new[] { "DataError" }));
        Assert.True(method != null, $"RecordImplementation.{name}(DataError) not found in Ncl.");
        return method!;
    }

    [SkippableFact]
    public void ModifyRecord_CarriesTheUserModifyArm_OnItsParentRecord_AheadOfTheOriginalBody()
    {
        using var module = OpenNcl();
        SkipUnlessRewritten(module);

        var instructions = RecordImplementationMethod(module, "ModifyRecordAsync").Body.Instructions;
        var index = instructions.ToList().FindIndex(i =>
            i.OpCode == OpCodes.Call && (i.Operand as MethodReference)?.FullName == UserModifyArm);

        Assert.True(index >= 2,
            "RecordImplementation.ModifyRecordAsync does not call OnBeforeUserModify — a User Modify "
            + "(AL or page save) would store an un-normalised Authentication Email and accept "
            + "duplicates (#2363).");
        Assert.Equal(OpCodes.Ldarg_0, instructions[index - 2].OpCode);
        Assert.Equal(OpCodes.Ldfld, instructions[index - 1].OpCode);
        Assert.Equal("parentRecord", (instructions[index - 1].Operand as FieldReference)?.Name);
        for (var i = 0; i < index; i++)
            Assert.True(
                instructions[i].OpCode == OpCodes.Ldarg_0 || instructions[i].OpCode == OpCodes.Ldfld
                || instructions[i].OpCode == OpCodes.Call,
                $"instruction {i} of ModifyRecordAsync is {instructions[i].OpCode}: OnBeforeUserModify "
                + "would run after part of the original body instead of before the write.");
    }

    [SkippableFact]
    public void NavRecordModifyEntryPoints_DoNotCarryTheUserModifyArm()
    {
        // #4701: on NavRecord.ModifyAsync(4) the arm ran BEFORE the OnBeforeModify subscribers
        // and the OnModify triggers; on ALModifyAsync or the 3-arg forwarder it would also run
        // twice per AL modify.
        using var module = OpenNcl();
        SkipUnlessRewritten(module);

        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")!;
        foreach (var method in navRecord.Methods.Where(m => (m.Name == "ALModifyAsync" || m.Name == "ModifyAsync") && m.HasBody))
            Assert.DoesNotContain(UserModifyArm, CalledMethods(method));
        Assert.Single(CalledMethods(RecordImplementationMethod(module, "ModifyRecordAsync")), n => n == UserModifyArm);
    }

    [SkippableFact]
    public void BcsAuthenticationEmailNormaliser_HasTheShapeThePatchBindsTo()
    {
        using var module = OpenNcl();
        var triggers = module.GetType("Microsoft.Dynamics.Nav.Runtime.SystemTableTriggers");
        Assert.NotNull(triggers);

        var candidates = triggers!.Methods.Where(m => m.Name == "TrimAndAnalyzeAuthenticationEmail").ToList();
        var method = Assert.Single(candidates);
        Assert.True(method.IsStatic, "the patch invokes it with a null target");
        Assert.Equal("System.String", method.ReturnType.FullName);
        Assert.Equal(new[] { "System.String" }, method.Parameters.Select(p => p.ParameterType.FullName));
    }
}
