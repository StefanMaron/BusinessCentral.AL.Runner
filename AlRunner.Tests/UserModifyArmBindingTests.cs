// UserModifyArmBindingTests — issue #2363.
//
// A RUNNER-MECHANISM test. The BC-observable claim (Insert/Modify on User normalise and
// validate "Authentication Email"; Modify refuses a user name another user carries) is pinned
// upstream, corpus codeunit 61206 "Test User Auth Email Trigger" in the OnPrem app.
//
// What this pins is the runner's own wiring, which no C# call graph shows: that
// UserTableTriggerPatches.OnBeforeUserModify is prepended to the modify FUNNEL
// NavRecord.ModifyAsync(4) — where AL Modify() and a page save meet
// (docs/page-save-data-layer-prepends.md) — and not also to ALModifyAsync or the 3-arg
// forwarder, which would run the validation twice; and that the BC member the patch invokes
// rather than re-implements, SystemTableTriggers.TrimAndAnalyzeAuthenticationEmail(string),
// still has the shape the reflection bind asks for.
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

    /// <summary>A rewritten Ncl carries the insert arm's prepend; an un-rewritten one is a skip,
    /// never a pass, and never a failure of the subject below.</summary>
    private static void SkipUnlessRewritten(ModuleDefinition module)
        => Skip.IfNot(
            CalledMethods(NavRecordMethod(module, "InsertAsync", "DataError", "Boolean", "Boolean", "Boolean"))
                .Any(n => n.Contains("UserTableTriggerPatches::OnBeforeUserInsert(System.Object)", StringComparison.Ordinal)),
            $"'{RewrittenNclPath}' has not been Cecil-rewritten; run the runner once to warm the Cecil cache.");

    [SkippableFact]
    public void ModifyFunnel_CarriesTheUserModifyArm_InsideThePrependedPrefix()
    {
        using var module = OpenNcl();
        SkipUnlessRewritten(module);

        var modify4 = NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean", "Boolean");
        var instructions = modify4.Body.Instructions;
        var index = instructions.ToList().FindIndex(i =>
            i.OpCode == OpCodes.Call && (i.Operand as MethodReference)?.FullName == UserModifyArm);

        Assert.True(index > 0,
            "NavRecord.ModifyAsync(4) does not call OnBeforeUserModify — a User Modify (AL or page "
            + "save) would store an un-normalised Authentication Email and accept duplicates (#2363).");
        for (var i = 0; i < index; i++)
            Assert.True(instructions[i].OpCode == OpCodes.Ldarg_0 || instructions[i].OpCode == OpCodes.Call,
                $"instruction {i} of ModifyAsync(4) is {instructions[i].OpCode}: OnBeforeUserModify would "
                + "run after part of the original body instead of before the write.");
    }

    [SkippableFact]
    public void AlModifyAndTheThreeArgForwarder_DoNotAlsoCarryTheUserModifyArm()
    {
        using var module = OpenNcl();
        SkipUnlessRewritten(module);

        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")!;
        foreach (var method in navRecord.Methods.Where(m => m.Name == "ALModifyAsync" && m.HasBody))
            Assert.DoesNotContain(UserModifyArm, CalledMethods(method));
        Assert.DoesNotContain(UserModifyArm, CalledMethods(
            NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean")));
        Assert.Single(CalledMethods(
            NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean", "Boolean")),
            n => n == UserModifyArm);
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
