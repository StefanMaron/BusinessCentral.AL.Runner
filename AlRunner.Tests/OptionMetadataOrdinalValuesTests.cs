// OptionMetadataOrdinalValuesTests — issue #4798.
//
// Runner-side MECHANISM test. The BC-behaviour claim (FieldRef.GetEnumValueOrdinal/Caption by
// index answer the declared ordinal on a gapped or out-of-order enum) is adjudicated by corpus
// codeunit 67486 "FieldRef Enum Ordinal By Index". This file pins the two runner pieces that
// deliver it: the helper answering AlEnumOptionMetadata's ordinals, and the Cecil rewrite
// routing the base NCLOptionMetadata.get_OrdinalValues body to that helper.

using System.Linq;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class OptionMetadataOrdinalValuesTests
{
    [Fact]
    public void Helper_AnswersTheDeclaredOrdinals_ForAnAlEnum()
    {
        var meta = new AlEnumOptionMetadata(
            name: "Gap Kind", id: 4798001,
            options: new[] { "Zulu", "Alpha", "Beta" },
            indexes: new[] { 4, 0, 10 });

        Assert.Equal(new[] { 4, 0, 10 }, BcRuntime.NCLOptionMetadata_OrdinalValues(meta));
    }

    [Fact]
    public void Helper_AnswersNull_ForAPlainOptionSet()
    {
        // The base getter's own answer, kept for every metadata that is not ours.
        Assert.Null(BcRuntime.NCLOptionMetadata_OrdinalValues(NCLOptionMetadata.Create("A,B,C")));
    }

    [SkippableFact]
    public void RewriteNcl_RoutesTheBaseOrdinalValuesGetterToTheHelper()
    {
        TestArtifacts.SkipIfMissing();

        var nclPath = Path.Combine(BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        using var rewritten = new MemoryStream(NclCecilRewrite.RewriteNcl(nclPath));
        using var asm = AssemblyDefinition.ReadAssembly(rewritten);

        var getter = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NCLOptionMetadata")!
            .Methods.Single(m => m.Name == "get_OrdinalValues" && m.Parameters.Count == 0);
        Assert.Contains(getter.Body.Instructions, i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == nameof(BcRuntime.NCLOptionMetadata_OrdinalValues));

        // The override BC ships stays BC's own body: only the base getter is ours.
        var enumGetter = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NCLEnumMetadata")!
            .Methods.Single(m => m.Name == "get_OrdinalValues" && m.Parameters.Count == 0);
        Assert.DoesNotContain(enumGetter.Body.Instructions, i =>
            i.Operand is MethodReference mr && mr.Name == nameof(BcRuntime.NCLOptionMetadata_OrdinalValues));
    }
}
