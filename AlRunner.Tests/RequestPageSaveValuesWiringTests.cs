// RequestPageSaveValuesWiringTests — the runner-internal half of #4808.
//
// The BC-behaviour claim (a SaveValues request page reopens on the values confirmed with OK on
// the previous run; one without SaveValues reopens on its defaults) is corpus codeunit 67541
// "Rpt RequestPage SaveValues", green on a real service tier. These pin the runner's own
// wiring that the round trip depends on, so a later rewrite that cuts it again fails here by
// name rather than as an assertion mismatch in the corpus.
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class RequestPageSaveValuesWiringTests
{
    private readonly BcEngineFixture _engine;

    public RequestPageSaveValuesWiringTests(BcEngineFixture engine) => _engine = engine;

    private static TypeDefinition NclType(string fullName)
    {
        var asm = AssemblyDefinition.ReadAssembly(typeof(ITreeObject).Assembly.Location);
        var type = asm.MainModule.GetType(fullName);
        Assert.NotNull(type);
        return type!;
    }

    private void SkipUnlessEngine()
        => TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    // The setter's own `requestOptionsPage.GetReportOptions += OnGetReportOptions` (and the
    // Apply twin) is the only thing that lets StoreSaveValues save and InitializeReportValues
    // restore. The runner used to replace the body with a bare field store.
    [SkippableFact]
    public void NavReportRequestOptionsPageSetter_StillSubscribesTheSaveAndRestoreEvents()
    {
        SkipUnlessEngine();
        var setter = NclType("Microsoft.Dynamics.Nav.Runtime.NavReport").Methods
            .Single(m => m.Name == "set_RequestOptionsPage" && m.HasBody);
        var called = setter.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.Name)
            .ToHashSet();
        Assert.Contains("add_GetReportOptions", called);
        Assert.Contains("add_ApplyReportOptions", called);
    }

    // BC's ctor body is `Parent = parent;`. RequestPageBase.InitializeRequestPageWithCustomValues
    // reads Parent.Session, so a rewritten ctor that drops the store NREs the restore.
    [SkippableFact]
    public void RewrittenRequestPageBaseCtors_StoreParent()
    {
        SkipUnlessEngine();
        var ctors = NclType("Microsoft.Dynamics.Nav.Runtime.RequestPageBase").Methods
            .Where(m => m.IsConstructor && m.HasBody
                        && m.Parameters.Count is 2 or 3
                        && m.Parameters[0].ParameterType.FullName
                           == "Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase"
                        && m.Parameters[1].ParameterType.FullName
                           == "Microsoft.Dynamics.Nav.Types.Metadata.MasterPage")
            .ToList();
        Assert.Equal(2, ctors.Count);
        foreach (var ctor in ctors)
        {
            Assert.Contains(ctor.Body.Instructions, i =>
                i.OpCode == OpCodes.Stfld
                && i.Operand is FieldReference f && f.Name == "<Parent>k__BackingField");
        }
    }
}
