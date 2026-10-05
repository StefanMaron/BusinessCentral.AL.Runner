// RenameStampPrependBindingTests — issue #5209.
//
// A RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable claim
// ("Rename stamps SystemModifiedAt/By; a Modify that changes nothing does not") is adjudicated by
// the corpus tests in codeunit 60061, StefanMaron/BusinessCentral.AL.Language.Tests. What this pins is
// the runner's wiring: the Rename stamp is a Cecil prepend on RecordImplementation.RenameRecordAsync
// taking the renamed clone, and it is NOT also on a method a Rename passes through. The binding is
// registration-only code in NclCecilRewrite, so no other C# test sees it dropped; only an AL suite
// failing far downstream does.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class RenameStampPrependBindingTests
{
    // Cecil's MethodReference.FullName carries the RETURN TYPE; without the prefix every equality
    // below is false and every DoesNotContain passes vacuously (PageSaveDataLayerPrependBindingTests).
    private const string StampOnRename =
        "System.Void AlRunner.BcRuntime::StampSystemFieldsOnRename(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private const string StampOnModify =
        "System.Void AlRunner.BcRuntime::StampSystemFieldsOnModify(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(RenameStampPrependBindingTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static ModuleDefinition OpenRewrittenNcl()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");
        var module = ModuleDefinition.ReadModule(RewrittenNclPath);
        NclRewriteMarker.SkipUnlessRewritten(module, RewrittenNclPath);
        return module;
    }

    private static List<string> CalledMethods(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    private static MethodDefinition RenameRecordAsync(ModuleDefinition module)
    {
        var recImpl = module.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        Assert.NotNull(recImpl);
        return Assert.Single(recImpl!.Methods, m =>
            m.Name == "RenameRecordAsync" && m.HasBody && m.Parameters.Count == 2
            && m.Parameters[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavRecord");
    }

    [SkippableFact]
    public void RenameRecordAsync_StampsTheRenamedClone_BeforeTheOriginalBody()
    {
        using var module = OpenRewrittenNcl();
        var method = RenameRecordAsync(module);
        var instructions = method.Body.Instructions;

        // The prepend is `ldarg <renamedRecord>; call helper`, ahead of the whole original body.
        Assert.Equal(OpCodes.Ldarg, instructions[0].OpCode);
        var argument = Assert.IsAssignableFrom<ParameterDefinition>(instructions[0].Operand);
        Assert.Equal("renamedRecord", argument.Name);
        Assert.Equal(OpCodes.Call, instructions[1].OpCode);
        Assert.Equal(StampOnRename, ((MethodReference)instructions[1].Operand).FullName);

        // Once: a second call would be a second stamp per rename.
        Assert.Single(CalledMethods(method), name => name == StampOnRename);
    }

    [SkippableFact]
    public void TheRenameFunnel_AndTheModifyFunnel_DoNotCarryEachOthersStamp()
    {
        // NavRecord.RenameAsync(4) reaches RenameRecordAsync, so a stamp on it too would stamp twice
        // per rename and, on a refused rename (a duplicate key), would already have moved the
        // caller's own SystemModifiedAt. The Modify funnel must not carry the Rename helper,
        // nor the other way round.
        using var module = OpenRewrittenNcl();
        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        Assert.NotNull(navRecord);

        var rename4 = Assert.Single(navRecord!.Methods, m =>
            m.Name == "RenameAsync" && m.HasBody && m.Parameters.Count == 4);
        Assert.DoesNotContain(StampOnRename, CalledMethods(rename4));
        Assert.DoesNotContain(StampOnModify, CalledMethods(rename4));

        foreach (var modify in navRecord.Methods.Where(m => m.Name.Contains("ModifyAsync") && m.HasBody))
            Assert.DoesNotContain(StampOnRename, CalledMethods(modify));
        Assert.DoesNotContain(StampOnModify, CalledMethods(RenameRecordAsync(module)));
    }
}
