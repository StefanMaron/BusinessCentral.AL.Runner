// MediaSetAddToSetResolutionTests — pins NclCecilRewrite.ResolveMediaSetAddToSetTarget, the
// version-conditional hook-resolution logic for NavMediaSet's internal "add a media id to
// the set" method (#1802).
//
// BC 27.x has NO async surface on NavMediaSet at all — only the synchronous
// AddMediaToSet(Guid, Guid) -> Guid. BC 28+ has only the async
// AddMediaToSetAsync(NavSession, Guid, Guid) -> ValueTask<Guid>. Before this fix, the Cecil
// hook only ever looked for the async shape: on BC 27.x the lookup silently returned null,
// the `if (m != null)` guard fell through with no diagnostic, and BC's own unpatched
// AddMediaToSet ran — which reaches an undeclared "Media Set" platform table this runner
// doesn't back, so every MediaSet membership operation silently degraded to Count()==0. A
// WRONG ANSWER, not a missing feature or a loud failure — exactly what loud-failures.md
// prohibits.
//
// These tests build minimal SYNTHETIC Mono.Cecil modules (no real BC artifacts needed, so
// they run identically in every CI leg) that reproduce each of the three shapes a real Ncl
// could plausibly present, and assert the resolver's behavior for each:
//   1. Only the BC 28+ async shape present  -> resolves to it.
//   2. Only the BC 27.x sync shape present   -> resolves to it.
//   3. NEITHER shape present (a genuinely unknown future Ncl) -> hard-errors with a message
//      naming BOTH candidate signatures, rather than silently returning null / no-op.
using System.Linq;
using AlRunner.Infrastructure;
using Mono.Cecil;
using Xunit;

namespace AlRunner.Tests;

public sealed class MediaSetAddToSetResolutionTests
{
    private static ModuleDefinition NewModule(string name) =>
        ModuleDefinition.CreateModule(name, ModuleKind.Dll);

    private static TypeDefinition NewNavMediaSetType(ModuleDefinition module)
    {
        var type = new TypeDefinition(
            "Microsoft.Dynamics.Nav.Runtime", "NavMediaSet",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    /// <summary>Gives the method a real (empty) body so MethodDefinition.HasBody is true,
    /// matching how a real compiled Ncl method looks to the resolver.</summary>
    private static void GiveEmptyBody(MethodDefinition m)
    {
        var il = m.Body.GetILProcessor();
        il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
    }

    private static MethodDefinition AddAsyncShape(ModuleDefinition module, TypeDefinition type)
    {
        // BC 28+: AddMediaToSetAsync(NavSession session, Guid setId, Guid mediaId) -> ValueTask<Guid>
        // The exact return type doesn't matter to the resolver (it only inspects name +
        // param count), so System.Object stands in for ValueTask<Guid> here.
        var m = new MethodDefinition("AddMediaToSetAsync",
            MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.Object);
        m.Parameters.Add(new ParameterDefinition("session", ParameterAttributes.None, module.TypeSystem.Object));
        m.Parameters.Add(new ParameterDefinition("setId", ParameterAttributes.None, module.TypeSystem.Object));
        m.Parameters.Add(new ParameterDefinition("mediaId", ParameterAttributes.None, module.TypeSystem.Object));
        type.Methods.Add(m);
        GiveEmptyBody(m);
        return m;
    }

    private static MethodDefinition AddSyncShape(ModuleDefinition module, TypeDefinition type)
    {
        // BC 27.x: AddMediaToSet(Guid setId, Guid mediaId) -> Guid — no NavSession param.
        var guidRef = module.ImportReference(typeof(System.Guid));
        var m = new MethodDefinition("AddMediaToSet",
            MethodAttributes.Assembly | MethodAttributes.HideBySig, guidRef);
        m.Parameters.Add(new ParameterDefinition("setId", ParameterAttributes.None, guidRef));
        m.Parameters.Add(new ParameterDefinition("mediaId", ParameterAttributes.None, guidRef));
        type.Methods.Add(m);
        GiveEmptyBody(m);
        return m;
    }

    [Fact]
    public void OnlyAsyncShapePresent_ResolvesToTheAsyncMethod()
    {
        var module = NewModule("SyntheticNcl28");
        var type = NewNavMediaSetType(module);
        var expected = AddAsyncShape(module, type);

        var resolved = NclCecilRewrite.ResolveMediaSetAddToSetTarget(type);

        Assert.Same(expected, resolved);
        Assert.Equal("AddMediaToSetAsync", resolved.Name);
        Assert.Equal(3, resolved.Parameters.Count);
    }

    [Fact]
    public void OnlySyncShapePresent_ResolvesToTheSyncMethod()
    {
        var module = NewModule("SyntheticNcl27");
        var type = NewNavMediaSetType(module);
        var expected = AddSyncShape(module, type);

        var resolved = NclCecilRewrite.ResolveMediaSetAddToSetTarget(type);

        Assert.Same(expected, resolved);
        Assert.Equal("AddMediaToSet", resolved.Name);
        Assert.Equal(2, resolved.Parameters.Count);
        Assert.All(resolved.Parameters, p => Assert.Equal("System.Guid", p.ParameterType.FullName));
    }

    [Fact]
    public void BothShapesPresent_PrefersTheAsyncOne()
    {
        // Not a real BC shape (no Ncl ships both), but pins the precedence deterministically
        // rather than leaving it to FirstOrDefault's incidental Methods-collection order.
        var module = NewModule("SyntheticNclBoth");
        var type = NewNavMediaSetType(module);
        var asyncMethod = AddAsyncShape(module, type);
        AddSyncShape(module, type);

        var resolved = NclCecilRewrite.ResolveMediaSetAddToSetTarget(type);

        Assert.Same(asyncMethod, resolved);
    }

    [Fact]
    public void NeitherShapePresent_HardErrors_NamingBothCandidateSignatures()
    {
        // The actual #1802 failure mode, reproduced deterministically: an Ncl whose
        // NavMediaSet has neither the async nor the sync add-to-set method. Before this fix
        // there was no such check at all — the caller just skipped the hook silently. Now it
        // must throw, loudly, naming both signatures so the fix is greppable.
        var module = NewModule("SyntheticNclNeither");
        var type = NewNavMediaSetType(module);
        // Unrelated method on the type, so the resolver can't accidentally match on an
        // empty Methods collection alone — this proves it's checking name+shape, not count.
        var unrelated = new MethodDefinition("SomeOtherMethod",
            MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
        type.Methods.Add(unrelated);
        GiveEmptyBody(unrelated);

        var ex = Assert.Throws<System.InvalidOperationException>(
            () => NclCecilRewrite.ResolveMediaSetAddToSetTarget(type));

        Assert.Contains("AddMediaToSetAsync", ex.Message);
        Assert.Contains("AddMediaToSet(Guid, Guid)", ex.Message);
        Assert.Contains("#1802", ex.Message);
    }

    [Fact]
    public void MethodWithoutBody_IsNotResolved_EvenIfNameAndParamsMatch()
    {
        // An abstract/pinvoke declaration with no body must not satisfy the resolver — the
        // real hook mechanism (ReplaceBodyWithHelper) requires a real body to overwrite.
        var module = NewModule("SyntheticNclAbstractOnly");
        var type = NewNavMediaSetType(module);
        var guidRef = module.ImportReference(typeof(System.Guid));
        var abstractMethod = new MethodDefinition("AddMediaToSet",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            guidRef);
        abstractMethod.Parameters.Add(new ParameterDefinition("setId", ParameterAttributes.None, guidRef));
        abstractMethod.Parameters.Add(new ParameterDefinition("mediaId", ParameterAttributes.None, guidRef));
        type.Methods.Add(abstractMethod);
        // No body given — HasBody must be false for an abstract method.
        Assert.False(abstractMethod.HasBody);

        var ex = Assert.Throws<System.InvalidOperationException>(
            () => NclCecilRewrite.ResolveMediaSetAddToSetTarget(type));

        Assert.Contains("#1802", ex.Message);
    }
}
