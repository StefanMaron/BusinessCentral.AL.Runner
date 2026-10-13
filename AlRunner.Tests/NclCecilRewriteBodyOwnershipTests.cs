// NclCecilRewriteBodyOwnershipTests — issue #3519.
//
// RewriteNcl runs its area passes in a fixed order and a later whole-body replacement clears the
// earlier one, so a target owned by two passes silently runs only the later copy. These tests pin
// the one-owner invariant from the IL the passes actually produced (NclCecilRewrite.BodyOwnershipLedger),
// not from a list of targets typed out here: the real-rewrite test reads production, the
// synthetic ones prove the measurement can see (and can refuse) the shape it exists to find.

using System.Linq;
using AlRunner.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclCecilRewriteBodyOwnershipTests
{
    private static (ModuleDefinition Module, MethodDefinition OverloadInt, MethodDefinition OverloadString) Synthetic()
    {
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Synthetic", new System.Version(1, 0)), "Synthetic", ModuleKind.Dll);
        var type = new TypeDefinition("S", "T", TypeAttributes.Public | TypeAttributes.Class, asm.MainModule.TypeSystem.Object);
        asm.MainModule.Types.Add(type);
        MethodDefinition Overload(TypeReference parameterType)
        {
            var m = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, asm.MainModule.TypeSystem.Void);
            m.Parameters.Add(new ParameterDefinition(parameterType));
            m.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(m);
            return m;
        }
        return (asm.MainModule, Overload(asm.MainModule.TypeSystem.Int32), Overload(asm.MainModule.TypeSystem.String));
    }

    // The shape every inline replacement site in the rewrite passes uses.
    private static void ReplaceInline(MethodDefinition m)
    {
        var body = m.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Nop));
        il.Append(il.Create(OpCodes.Ret));
    }

    [Fact]
    public void SecondWholeBodyReplacement_IsReportedWithBothOwnersNamed()
    {
        var (module, overloadInt, _) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        var audit = new NclCecilRewrite.BodyOwnershipAudit(module, ledger);

        ReplaceInline(overloadInt);
        audit.EndPass("Records");
        ReplaceInline(overloadInt);
        audit.EndPass("Runtime");

        var owners = Assert.Single(ledger.MultiplyOwned());
        Assert.Equal(overloadInt.FullName, owners.Key);
        Assert.Equal(new[] { "Records", "Runtime" }, owners.Value);

        var ex = Assert.Throws<InvalidOperationException>(ledger.AssertSingleOwner);
        Assert.Contains(overloadInt.FullName, ex.Message);
        Assert.Contains("Records", ex.Message);
        Assert.Contains("Runtime", ex.Message);
    }

    [Fact]
    public void OneReplacementPerTarget_PassesTheAssertion()
    {
        var (module, overloadInt, overloadString) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        var audit = new NclCecilRewrite.BodyOwnershipAudit(module, ledger);

        ReplaceInline(overloadInt);
        audit.EndPass("Records");
        ReplaceInline(overloadString);
        audit.EndPass("Runtime");

        ledger.AssertSingleOwner();
        Assert.Equal(new[] { "Records" }, ledger.Owners[overloadInt.FullName]);
        Assert.Equal(new[] { "Runtime" }, ledger.Owners[overloadString.FullName]);
    }

    [Fact]
    public void SameNameSameArityOverloads_AreDistinctTargetsButShareARegistryKey()
    {
        var (module, overloadInt, overloadString) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        var audit = new NclCecilRewrite.BodyOwnershipAudit(module, ledger);

        ReplaceInline(overloadInt);
        ReplaceInline(overloadString);
        audit.EndPass("Records");

        Assert.Equal(2, ledger.Owners.Count);
        Assert.NotEqual(overloadInt.FullName, overloadString.FullName);
        // CecilOwned keys carry name and arity only, so the two cannot be told apart there.
        Assert.Equal(NclCecilRewrite.Key(overloadInt), NclCecilRewrite.Key(overloadString));
        Assert.Empty(ledger.MultiplyOwned());
    }

    [Fact]
    public void ReplacingOverloadsInDifferentPasses_IsNotADoubleOwnership()
    {
        var (module, overloadInt, overloadString) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        var audit = new NclCecilRewrite.BodyOwnershipAudit(module, ledger);

        ReplaceInline(overloadInt);
        audit.EndPass("Records");
        ReplaceInline(overloadString);
        audit.EndPass("Runtime");

        Assert.Empty(ledger.MultiplyOwned());
    }

    [Fact]
    public void ComposedEdit_ThatKeepsTheOriginalInstructions_IsNotAnOwner()
    {
        var (module, overloadInt, _) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        var audit = new NclCecilRewrite.BodyOwnershipAudit(module, ledger);

        // A prepend, as PrependStaticCall does: the original first instruction moves down, not away.
        var il = overloadInt.Body.GetILProcessor();
        il.InsertBefore(overloadInt.Body.Instructions[0], il.Create(OpCodes.Nop));
        audit.EndPass("Media");
        ReplaceInline(overloadInt);
        audit.EndPass("Runtime");

        Assert.Equal(new[] { "Runtime" }, ledger.Owners[overloadInt.FullName]);
        ledger.AssertSingleOwner();
    }

    [Fact]
    public void TwoSharedHelperReplacementsInOnePass_AreTwoOwners()
    {
        var (module, overloadInt, _) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        var audit = new NclCecilRewrite.BodyOwnershipAudit(module, ledger);

        // ReplaceBodyWithHelper / ReplaceBodyConst report themselves, so a pass that routes the same
        // target through them twice is caught even though the pass boundary only sees one change.
        audit.NoteHelperReplacement(overloadInt);
        ReplaceInline(overloadInt);
        audit.NoteHelperReplacement(overloadInt);
        ReplaceInline(overloadInt);
        audit.EndPass("Runtime");

        var owners = Assert.Single(ledger.MultiplyOwned());
        Assert.Equal(new[] { "Runtime", "Runtime" }, owners.Value);
    }

    [Fact]
    public void AnAuditThatMeasuredNothing_RefusesInsteadOfPassing()
    {
        var (module, _, _) = Synthetic();
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        new NclCecilRewrite.BodyOwnershipAudit(module, ledger).EndPass("Records");

        var ex = Assert.Throws<InvalidOperationException>(ledger.AssertSingleOwner);
        Assert.Contains("not measure", ex.Message);
    }

    [SkippableFact]
    public void RewriteNcl_EveryReplacedBodyHasExactlyOneOwner()
    {
        TestArtifacts.SkipIfMissing();

        var nclPath = Path.Combine(BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        var ledger = new NclCecilRewrite.BodyOwnershipLedger();
        NclCecilRewrite.RewriteNcl(nclPath, ledger);

        ledger.AssertSingleOwner();

        // The three targets #3519 found replaced by both Records and Runtime. Each is now the
        // Runtime pass's alone — and being present at all shows the audit really sees the passes.
        foreach (var signature in new[]
                 {
                     "DataAccessSource::GetDataAccessForTable(",
                     "NavSession::get_SortingProperties(",
                     "RecordImplementation::InternalFindRecordWithoutCheckingValuesAsync(",
                 })
        {
            var owner = Assert.Single(ledger.Owners, kv => kv.Key.Contains(signature));
            Assert.Equal(new[] { "Runtime" }, owner.Value);
        }
    }

    [SkippableFact]
    public void RewriteNcl_ReplacedInternalFind_NoLongerAdvertisesAnAsyncStateMachine()
    {
        TestArtifacts.SkipIfMissing();

        // The Records copy of this replacement also stripped the marker; the Runtime copy that now
        // owns the target must still do it, or deleting the earlier copy changed the output.
        var nclPath = Path.Combine(BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        using var rewritten = new MemoryStream(NclCecilRewrite.RewriteNcl(nclPath));
        using var asm = AssemblyDefinition.ReadAssembly(rewritten);

        var internalFind = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation")!
            .Methods.Single(m => m.Name == "InternalFindRecordWithoutCheckingValuesAsync");
        Assert.DoesNotContain(internalFind.CustomAttributes, a => a.AttributeType.Name == "AsyncStateMachineAttribute");
        Assert.Contains(internalFind.Body.Instructions, i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == nameof(BcRuntime.RecordImpl_InternalFindRecordWithoutCheckingValuesAsync));
    }
}
