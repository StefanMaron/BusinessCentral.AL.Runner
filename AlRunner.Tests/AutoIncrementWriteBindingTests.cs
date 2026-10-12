// AutoIncrementWriteBindingTests -- issues #5517, #5518 and #5521, the runner-mechanism half.
//
// What BC does (Rename of an AutoIncrement key is refused; a refused Insert leaves the key at 0; a BigInteger key
// past Int32 follows the counter) is a claim about BC and is proven upstream by corpus codeunits 67822 and 67826
// against a real service tier. None of it is asserted here. What is runner-specific and invisible to every AL
// suite until it is far downstream: the two Cecil bindings these fixes rest on, and the value conversion.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class AutoIncrementWriteBindingTests
{
    // Cecil's MethodReference.FullName carries the RETURN TYPE; without the prefix every equality below is false
    // and every DoesNotContain passes vacuously (PageSaveDataLayerPrependBindingTests).
    private const string RenameGuard =
        "System.Void AlRunner.Patches.RecordPatches::GuardRenameOfAutoIncrementField("
        + "Microsoft.Dynamics.Nav.Runtime.NavRecord,Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private const string RenameStamp =
        "System.Void AlRunner.BcRuntime::StampSystemFieldsOnRename(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private const string InsertCompleted =
        "System.Void AlRunner.BcRuntime::InsertRecordCompleted(System.Boolean,Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(AutoIncrementWriteBindingTests).Assembly.Location)
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

    private static List<Instruction> Calls(MethodDefinition method, string fullName)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && (i.Operand as MethodReference)?.FullName == fullName)
            .ToList();

    [SkippableFact]
    public void RenameRecordAsync_GuardsTheAutoIncrementField_BeforeStampingAndOnce()
    {
        using var module = OpenRewrittenNcl();
        var recImpl = module.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        Assert.NotNull(recImpl);
        var rename = Assert.Single(recImpl!.Methods, m =>
            m.Name == "RenameRecordAsync" && m.HasBody && m.Parameters.Count == 2
            && m.Parameters[1].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavRecord");

        var guard = Assert.Single(Calls(rename, RenameGuard));
        var stamp = Assert.Single(Calls(rename, RenameStamp));
        var list = rename.Body.Instructions.ToList();
        var guardIndex = list.IndexOf(guard);
        // `ldarg.0; ldfld parentRecord; ldarg renamedRecord; call guard`: the record being renamed, then the clone.
        Assert.True(guardIndex >= 3, "the guard is not preceded by its three argument instructions");
        Assert.Equal(OpCodes.Ldarg, list[guardIndex - 1].OpCode);
        Assert.Equal("renamedRecord", Assert.IsAssignableFrom<ParameterDefinition>(list[guardIndex - 1].Operand).Name);
        Assert.Equal("parentRecord", Assert.IsAssignableFrom<FieldReference>(list[guardIndex - 2].Operand).Name);
        Assert.Equal(OpCodes.Ldarg_0, list[guardIndex - 3].OpCode);
        // Before the stamp: a refused Rename must not have moved the caller's SystemModifiedAt.
        Assert.True(guardIndex < list.IndexOf(stamp), "the guard runs after the Rename stamp");
    }

    [SkippableFact]
    public void InsertRecordAsync_ReportsItsOutcome_OnBothExits()
    {
        using var module = OpenRewrittenNcl();
        var recImpl = module.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        Assert.NotNull(recImpl);
        var machine = Assert.Single(recImpl!.NestedTypes, t => t.Name.StartsWith("<InsertRecordAsync>d__"));
        var moveNext = Assert.Single(machine.Methods, m => m.Name == "MoveNext" && m.HasBody);

        // Exactly two reports: one before builder.SetResult(bool), one before builder.SetException. A third would
        // be a report per Insert attempt; a missing one is a refused Insert (false, or an exception) that keeps its key.
        var reports = Calls(moveNext, InsertCompleted);
        Assert.Equal(2, reports.Count);
        var list = moveNext.Body.Instructions.ToList();
        var next = reports.Select(r => (list[list.IndexOf(r) + 1].Operand as MethodReference)?.Name).ToList();
        Assert.Contains("SetResult", next);
        Assert.Contains("SetException", next);
    }

    [Theory]
    [InlineData(true, 5_000_000_001L)]
    [InlineData(true, 705_032_705L)]
    [InlineData(true, 3L)]
    public void AutoIncrementBoxedValue_ForABigIntegerField_KeepsTheWholeLong(bool big, long next)
    {
        var boxed = BoxedValue(big, next);
        Assert.IsType<long>(boxed);
        Assert.Equal(next, boxed);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2_147_483_647L)]
    public void AutoIncrementBoxedValue_ForAnIntegerField_IsAnInt32(long next)
    {
        var boxed = BoxedValue(false, next);
        Assert.IsType<int>(boxed);
        Assert.Equal((int)next, boxed);
    }

    private static object BoxedValue(bool big, long next)
    {
        var method = typeof(AlRunner.Patches.RecordPatches).Assembly.GetType("AlRunner.BcRuntime")!
            .GetMethod("AutoIncrementBoxedValue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("BcRuntime.AutoIncrementBoxedValue not found (#5521)");
        return method.Invoke(null, new object[] { big, next })!;
    }
}
