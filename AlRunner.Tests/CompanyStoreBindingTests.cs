// CompanyStoreBindingTests — issue #5349.
//
// RUNNER-MECHANISM tests. That a record on another company reads and writes that company's own
// rows, and that an error rolls them back, is BC behaviour and is measured upstream by corpus
// codeunits 69970 to 69975. What is pinned HERE is the wiring that behaviour rests on, each piece
// of which can come loose silently:
//
//   - the FlowField core reads the record's company token off TableState. It looked the field up
//     under the property's spelling, got null, and calculated every FlowField for token 0 whatever
//     company the record was on; nothing failed, the aggregate was simply the session company's;
//   - RecordImplementation.GetActiveCompany is the runner's, because NavRecord.CloneRecord(keepCompany)
//     builds the working record of every row-wise bulk write from that name, and "" put the clone
//     back on the session company (a DeleteAll through a record on another company emptied the
//     session company's table);
//   - the read hook that routes RecordImplementation.dataAccess is still the one every record read goes through.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CompanyStoreBindingTests
{
    private const string Rt = "Microsoft.Dynamics.Nav.Runtime.";

    private readonly BcEngineFixture _engine;

    public CompanyStoreBindingTests(BcEngineFixture engine) => _engine = engine;

    private static string NclPath => Path.Combine(
        Path.GetDirectoryName(typeof(CompanyStoreBindingTests).Assembly.Location) ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static List<string> Called(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    [SkippableFact]
    public void FlowFieldCore_ReadsTheCompanyTokenFieldBCDeclares()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready.");

        var nclAsm = typeof(NavRecord).Assembly;
        var typesAsm = typeof(Microsoft.Dynamics.Nav.Types.DataError).Assembly;
        FlowFieldPatches.Register(nclAsm, typesAsm);

        var bound = typeof(FlowFieldPatches)
            .GetField("_fTableStateCompanyNameToken", BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null) as FieldInfo;

        Assert.NotNull(bound);
        Assert.Equal("companyNameToken", bound!.Name);
        Assert.Equal(typeof(int), bound.FieldType);
        Assert.Equal(Rt + "TableState", bound.DeclaringType!.FullName);
    }

    [SkippableFact]
    public void GetActiveCompany_IsTheRunnersCompanyAwareAnswer()
    {
        Skip.IfNot(File.Exists(NclPath), $"the Ncl is not present at '{NclPath}'.");
        using var asm = AssemblyDefinition.ReadAssembly(NclPath);
        NclRewriteMarker.SkipUnlessRewritten(asm.MainModule, NclPath);

        var method = asm.MainModule.GetType(Rt + "RecordImplementation")!.Methods.Single(m => m.Name == "GetActiveCompany");

        Assert.Equal(
            new[] { "System.String AlRunner.BcRuntime::RecordImplementation_GetActiveCompany(System.Object)" },
            Called(method));
    }

    [SkippableFact]
    public void EveryReadOfTheRecordsDataAccess_GoesThroughTheRoutingHook()
    {
        Skip.IfNot(File.Exists(NclPath), $"the Ncl is not present at '{NclPath}'.");
        using var asm = AssemblyDefinition.ReadAssembly(NclPath);
        NclRewriteMarker.SkipUnlessRewritten(asm.MainModule, NclPath);

        var deleteAll = asm.MainModule.GetType(Rt + "RecordImplementation")!.NestedTypes
            .Single(t => t.Name.StartsWith("<DeleteAllRecordsAsync>d__"))
            .Methods.Single(m => m.Name == "MoveNext");

        Assert.Contains(Called(deleteAll), n => n.Contains("RecordPatches::RecordImplementation_LiveDataAccess"));
    }
}
