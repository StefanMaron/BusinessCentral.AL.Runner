// CompanyWritePermissionSetupBindingTests — issue #5020.
//
// A RUNNER-MECHANISM test. The BC-observable claim (a permission set composed before a grant is
// recomposed by the next read after a Company insert or delete, inside the same transaction) is
// measured upstream by corpus codeunit 67947, ExpandedPermission_Company*.
//
// What this pins is the runner's wiring: that the two RecordPatches Company helpers are
// prepended to RecordImplementation.InsertRecordAsync / DeleteRecordAsync on their parentRecord,
// ahead of the original body — the point below NavRecord's trigger dispatch where BC's
// SystemTableTriggers arms run — and to no NavRecord entry point, where they would run before
// the OnInsert/OnDelete triggers. Rename is #5071.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class CompanyWritePermissionSetupBindingTests
{
    private const string InsertArm =
        "System.Void AlRunner.Patches.RecordPatches::OnCompanyInsertRecord(System.Object)";
    private const string DeleteArm =
        "System.Void AlRunner.Patches.RecordPatches::OnCompanyDeleteRecord(System.Object)";

    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(CompanyWritePermissionSetupBindingTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static ModuleDefinition OpenNcl()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath), $"the Ncl is not present at '{RewrittenNclPath}'.");
        var module = ModuleDefinition.ReadModule(RewrittenNclPath);
        NclRewriteMarker.SkipUnlessRewritten(module, RewrittenNclPath);
        return module;
    }

    private static MethodDefinition RecordImplementationMethod(ModuleDefinition module, string name, params string[] parameterTypeNames)
    {
        var method = module.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation")?.Methods.FirstOrDefault(m =>
            m.Name == name && m.HasBody
            && m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(parameterTypeNames));
        Assert.True(method != null, $"RecordImplementation.{name}({string.Join(", ", parameterTypeNames)}) not found in Ncl.");
        return method!;
    }

    private static List<string> CalledMethods(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    public static TheoryData<string, string> SingleArgumentArms => new()
    {
        { "InsertRecordAsync", InsertArm },
        { "DeleteRecordAsync", DeleteArm },
    };

    [SkippableTheory]
    [MemberData(nameof(SingleArgumentArms))]
    public void RecordWrite_CarriesTheCompanyArm_OnItsParentRecord_AheadOfTheOriginalBody(string method, string arm)
    {
        using var module = OpenNcl();
        var instructions = RecordImplementationMethod(module, method, "DataError").Body.Instructions.ToList();
        var index = instructions.FindIndex(i =>
            i.OpCode == OpCodes.Call && (i.Operand as MethodReference)?.FullName == arm);

        Assert.True(index >= 2, $"RecordImplementation.{method} does not call {arm}: a Company write would "
            + "leave a composed permission set answering until the transaction ends (#5020).");
        Assert.Equal(OpCodes.Ldarg_0, instructions[index - 2].OpCode);
        Assert.Equal("parentRecord", (instructions[index - 1].Operand as FieldReference)?.Name);
        AssertOnlyPrependsBefore(instructions, index, method);
        Assert.Single(CalledMethods(RecordImplementationMethod(module, method, "DataError")), n => n == arm);
    }

    [SkippableFact]
    public void NavRecordWriteEntryPoints_DoNotCarryTheCompanyArms()
    {
        using var module = OpenNcl();
        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord")!;
        foreach (var method in navRecord.Methods.Where(m => m.HasBody))
        {
            var called = CalledMethods(method);
            Assert.DoesNotContain(InsertArm, called);
            Assert.DoesNotContain(DeleteArm, called);
        }
    }

    private static void AssertOnlyPrependsBefore(List<Instruction> instructions, int index, string method)
    {
        for (var i = 0; i < index; i++)
            Assert.True(
                instructions[i].OpCode == OpCodes.Ldarg_0 || instructions[i].OpCode == OpCodes.Ldfld || instructions[i].OpCode == OpCodes.Call,
                $"instruction {i} of {method} is {instructions[i].OpCode}: the Company arm would run after "
                + "part of the original body instead of before the write.");
    }
}
