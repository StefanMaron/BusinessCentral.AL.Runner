// PageSaveValuesWiringTests — the runner-internal half of #4818.
//
// The BC-behaviour claim (a page that is not a request page and declares SaveValues reopens on
// the values it was closed with) is corpus codeunit 67545 "Page SaveValues Tests". These pin
// the two pieces of runner wiring the round trip depends on, so a later change that cuts it
// again fails here by name rather than as a value mismatch in the corpus.
using System.Linq;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

// Reads the Ncl image this process actually loaded, which BcEngineBootstrap has already
// Cecil-rewritten in place — so it must share the serial bc-engine collection.
[Collection(BcEngineCollection.Name)]
public class PageSaveValuesWiringTests
{
    private readonly BcEngineFixture _engine;

    public PageSaveValuesWiringTests(BcEngineFixture engine) => _engine = engine;

    private static bool Calls(MethodDefinition method, string calleeName)
        => method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference m && m.Name == calleeName);

    private static int IndexOfCallTo(MethodDefinition method, string calleeName)
    {
        var body = method.Body.Instructions;
        for (var i = 0; i < body.Count; i++)
            if ((body[i].OpCode == OpCodes.Call || body[i].OpCode == OpCodes.Callvirt)
                && body[i].Operand is MethodReference callee && callee.Name == calleeName)
                return i;
        return -1;
    }

    // ReadValues (the restore) goes through LoadPageDataPersonalization, which the runner used
    // to replace with `return default`. Its real body reads table 2000000080 and ends in
    // ALFindFirst; a rewritten one has no calls at all.
    [SkippableFact]
    public void LoadPageDataPersonalization_KeepsBcsOwnBody()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        var asm = AssemblyDefinition.ReadAssembly(typeof(ITreeObject).Assembly.Location);
        var helper = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavPageDataPersonalizationHelper");
        Assert.NotNull(helper);
        var loads = helper!.Methods.Where(m => m.Name == "LoadPageDataPersonalization" && m.HasBody).ToList();
        Assert.NotEmpty(loads);
        Assert.All(loads, m => Assert.True(Calls(m, "ALFindFirst"),
            $"{m.FullName} no longer reads the personalization table, so a SaveValues page cannot restore (#4818)"));
    }

    // A [ModalPageHandler]'s OK().Invoke() closes the page itself through
    // LiveNavTestPage.AttemptHandlerDrivenClose, which raises the close triggers and then
    // ForceClose — which saves nothing. The save has to sit between the two: after the triggers
    // (BC's CloseFormAsync order) and before the form is gone.
    [Fact]
    public void HandlerDrivenClose_StoresSaveValues_AfterTheTriggers_BeforeForceClose()
    {
        var module = ModuleDefinition.ReadModule(typeof(AlRunner.TestExecutor).Assembly.Location);
        var method = module.GetTypes().Single(t => t.FullName == "AlRunner.LiveNavTestPage")
            .Methods.Single(m => m.Name == "AttemptHandlerDrivenClose");

        var raise = IndexOfCallTo(method, "RaiseOnClosePage");
        var store = IndexOfCallTo(method, "StoreSaveValuesOnClose");
        var force = IndexOfCallTo(method, "ForceCloseForm");

        Assert.True(store >= 0, "AttemptHandlerDrivenClose must call StoreSaveValuesOnClose (#4818)");
        Assert.True(raise >= 0 && raise < store,
            $"StoreSaveValuesOnClose (IL {store}) must follow RaiseOnClosePage (IL {raise})");
        Assert.True(force > store,
            $"StoreSaveValuesOnClose (IL {store}) must precede ForceCloseForm (IL {force})");
    }
}
