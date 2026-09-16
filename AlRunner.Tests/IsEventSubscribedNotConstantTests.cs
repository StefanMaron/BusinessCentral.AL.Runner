// IsEventSubscribedNotConstantTests — issue #3576.
//
// NclCecilRewrite.RewriteNcl used to replace every Boolean
// NCLMetaApplicationObject.IsEventSubscribed overload with `ldc.i4.1; ret`, so a table
// whose event has NO subscribers still answered "subscribed". That is the silent fake
// loud-failures.md forbids: BC's own dispatch reads the return value to decide whether to
// take the trigger-event path at all.
//
// BC's real body (Ncl.dll sha256 6f2cf682…, the 28.x binary) is a null-safe delegation:
//
//     public bool IsEventSubscribed(NavTriggerEventType eventType, NavAppGroup appGroup)
//         => TriggerEventHandler?.IsEventSubscribed(eventType, appGroup) ?? false;
//
// and NavTriggerEventHandler.IsEventSubscribed is
// `GetEventScope(...)?.HasSubscribersForAppGroup(appGroup) ?? false`, i.e. it consults the
// registry EventSubscriberPatches already populates. So the runner does not need to
// substitute anything here — it needs to stop substituting.
//
// This file is the runner-side MECHANISM test (require-tests.yml needs one under
// AlRunner.Tests/). It asserts over the rewritten IL rather than over BC behaviour: the
// BC-behaviour claim — "an event with no subscribers reports unsubscribed" — is adjudicated
// by the corpus PR cited in the pull request body, on a real service tier.

using System.Linq;
using AlRunner.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class IsEventSubscribedNotConstantTests
{
    private const string MetaAppObject = "Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject";

    private static MethodDefinition[] BooleanOverloads(AssemblyDefinition asm)
    {
        var type = asm.MainModule.GetType(MetaAppObject);
        Assert.NotNull(type);
        return type!.Methods
            .Where(m => m.Name == "IsEventSubscribed"
                        && m.ReturnType.FullName == "System.Boolean")
            .ToArray();
    }

    /// <summary>
    /// The #3576 assertion. No Boolean IsEventSubscribed overload may carry the
    /// constant-true body `ldc.i4.1; ret` after the rewrite.
    ///
    /// Reads the REWRITTEN bytes RewriteNcl produces, not the file on disk, so it measures
    /// the rewrite itself rather than whatever a previous run happened to leave in bin/.
    /// </summary>
    [SkippableFact]
    public void RewriteNcl_LeavesNoConstantTrueIsEventSubscribedOverload()
    {
        TestArtifacts.SkipIfMissing();

        var nclPath = Path.Combine(BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        using var rewritten = new MemoryStream(NclCecilRewrite.RewriteNcl(nclPath));
        using var asm = AssemblyDefinition.ReadAssembly(rewritten);

        var overloads = BooleanOverloads(asm);
        Assert.NotEmpty(overloads);

        foreach (var method in overloads)
        {
            var il = method.Body.Instructions;
            var constantTrue = il.Count == 2
                               && il[0].OpCode == OpCodes.Ldc_I4_1
                               && il[1].OpCode == OpCodes.Ret;
            Assert.False(
                constantTrue,
                $"{method.FullName} was rewritten to constant true. BC's own body delegates to "
                + "TriggerEventHandler, which reads the subscriber registry EventSubscriberPatches "
                + "populates; a constant cannot report an unsubscribed event (issue #3576).");
        }
    }

    /// <summary>
    /// The other direction, and the one that makes the test above mean something: the
    /// overloads must still CALL something. A rewrite that replaced constant-true with
    /// constant-false, or with any other body that consults nothing, would pass the
    /// assertion above while being just as blind — so pin the delegation itself.
    ///
    /// BC's body loads TriggerEventHandler and calls IsEventSubscribed on it. Asserting on
    /// the presence of a call to a member of that name is what distinguishes "BC's original
    /// body is intact" from "some other constant".
    /// </summary>
    [SkippableFact]
    public void RewriteNcl_KeepsIsEventSubscribedDelegatingToTriggerEventHandler()
    {
        TestArtifacts.SkipIfMissing();

        var nclPath = Path.Combine(BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        using var rewritten = new MemoryStream(NclCecilRewrite.RewriteNcl(nclPath));
        using var asm = AssemblyDefinition.ReadAssembly(rewritten);

        var overloads = BooleanOverloads(asm);
        Assert.NotEmpty(overloads);

        foreach (var method in overloads)
        {
            var callsHandler = method.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && (mr.Name == "IsEventSubscribed" || mr.Name == "get_TriggerEventHandler"));

            Assert.True(
                callsHandler,
                $"{method.FullName} no longer delegates to TriggerEventHandler. BC's real body is "
                + "`TriggerEventHandler?.IsEventSubscribed(...) ?? false`; a body that calls nothing "
                + "cannot consult the subscriber registry, whatever constant it returns (issue #3576).");
        }
    }
}
