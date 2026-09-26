// NclRewriteMarkerTests — issue #4782. Pins both halves of NclRewriteMarker against constructed
// inputs, so they are provable on a box in any state: the marker answers from metadata and no
// single prepend, and an un-rewritten Ncl fails on CI instead of skipping green.
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclRewriteMarkerTests
{
    private static ModuleDefinition ModuleCalling(Func<ModuleDefinition, MethodReference?> callee)
    {
        var module = ModuleDefinition.CreateModule("Fake.Ncl", ModuleKind.Dll);
        var type = new TypeDefinition("Microsoft.Dynamics.Nav.Runtime", "NavRecord",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("ALInsertAsync",
            MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        type.Methods.Add(method);
        var il = method.Body.GetILProcessor();
        var target = callee(module);
        if (target != null)
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, target);
        }
        il.Emit(OpCodes.Ret);

        // GetMemberReferences reads the MemberRef TABLE, which an in-memory module does not have
        // until written — round-trip it, the way the real Ncl is read off disk.
        var bytes = new MemoryStream();
        module.Write(bytes);
        module.Dispose();
        bytes.Position = 0;
        return ModuleDefinition.ReadModule(bytes);
    }

    [Fact]
    public void AModuleCallingIntoAnAlRunnerType_ReadsAsRewritten()
    {
        // Any AlRunner.* member, not one named prepend: this is what makes a moved prepend
        // unable to flip the answer.
        using var module = ModuleCalling(m => m.ImportReference(
            typeof(UserTableTriggerPatches).GetMethod(nameof(UserTableTriggerPatches.OnBeforeUserModify))));

        Assert.True(NclRewriteMarker.IsRewritten(module));
    }

    [Fact]
    public void AModuleCallingOnlyNonRunnerMembers_ReadsAsNotRewritten()
    {
        using var module = ModuleCalling(m => m.ImportReference(
            typeof(GC).GetMethod(nameof(GC.KeepAlive))));

        Assert.False(NclRewriteMarker.IsRewritten(module));
    }

    [Fact]
    public void AModuleWithNoCallsAtAll_ReadsAsNotRewritten()
    {
        using var module = ModuleCalling(_ => null);

        Assert.False(NclRewriteMarker.IsRewritten(module));
    }

    [Fact]
    public void NotRewritten_OnCi_Fails_RatherThanSkipping()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            NclRewriteMarker.SkipUnlessRewrittenIn(rewritten: false, "/bin/Ncl.dll", runningOnCi: true));

        Assert.IsNotType<SkipException>(ex);
        Assert.IsType<Xunit.Sdk.FailException>(ex);
        Assert.Contains("CI leg", ex.Message);
        Assert.Contains("/bin/Ncl.dll", ex.Message);
    }

    [Fact]
    public void NotRewritten_OffCi_Skips_WithTheReason()
    {
        var ex = Assert.Throws<SkipException>(() =>
            NclRewriteMarker.SkipUnlessRewrittenIn(rewritten: false, "/bin/Ncl.dll", runningOnCi: false));

        Assert.Contains("/bin/Ncl.dll", ex.Message);
        Assert.Contains("engine-test-bootstrap.sh", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rewritten_NeitherSkipsNorFails(bool runningOnCi)
        => NclRewriteMarker.SkipUnlessRewrittenIn(rewritten: true, "/bin/Ncl.dll", runningOnCi);
}
