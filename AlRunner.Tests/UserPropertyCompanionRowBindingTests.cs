// UserPropertyCompanionRowBindingTests — issue #2355.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable
// claim ("a User row always has a matching User Property row, created with it, so
// UserManagement.DirectSetUserFieldValue can Get it with the raising error level") belongs
// upstream in StefanMaron/BusinessCentral.AL.Language.Tests, where a live service tier
// adjudicates it.
//
// What THIS test pins is the runner's own wiring. Ncl's
// SystemTableTriggers.OnBeforeInsertAsync has a `case 2000000120:` arm that inserts the
// companion User Property (2000000121) row, and the runner bypasses BC's trigger dispatch on
// insert (RecordWritePatches.NavRecord_InsertAsync), so nothing created that row. The fix
// prepends UserTableTriggerPatches.CreateUserPropertyOnUserInsert to
// NavRecord.ALInsertAsync(DataError, bool, bool) — the single AL insert entry point
// AssignAutoIncrement and StampSystemFieldsOnInsert are already prepended to.
//
// The prepend is registration-only code: it lives in NclCecilRewrite and produces no
// observable C# call graph, so a regression that drops it — or moves it onto the wrong entry
// point — is invisible to every other test until an AL suite that creates a user fails far
// downstream with "The User Property does not exist". Reading the rewritten IL is what makes
// that regression fail here instead, in milliseconds.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.CompilerServices;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class UserPropertyCompanionRowBindingTests
{
    private readonly BcEngineFixture _engine;

    public UserPropertyCompanionRowBindingTests(BcEngineFixture engine) => _engine = engine;

    private const string HelperFullName =
        "System.Void AlRunner.Patches.UserTableTriggerPatches::CreateUserPropertyOnUserInsert(System.Object)";

    /// <summary>
    /// The Cecil-rewritten Ncl the test host itself loaded — the very bytes the prepend was
    /// written into (see BcEngineBootstrap, which rewrites into this assembly's own bin dir).
    /// </summary>
    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(UserPropertyCompanionRowBindingTests).Assembly.Location)
            ?? AppContext.BaseDirectory,
        "Microsoft.Dynamics.Nav.Ncl.dll");

    private static MethodDefinition NavRecordMethod(
        ModuleDefinition module, string name, params string[] parameterTypeNames)
    {
        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        Assert.NotNull(navRecord);
        var method = navRecord!.Methods.FirstOrDefault(m =>
            m.Name == name
            && m.HasBody
            && m.Parameters.Count == parameterTypeNames.Length
            && m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(parameterTypeNames));
        Assert.True(method != null,
            $"NavRecord.{name}({string.Join(", ", parameterTypeNames)}) not found in the rewritten Ncl "
            + "— BC shape changed, and the prepend that depends on it would be silently unbound.");
        return method!;
    }

    private static List<string> CalledMethods(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    [SkippableFact]
    public void AlInsertEntryPoint_CallsTheCompanionRowHelperAsItsFirstAct()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");

        using var module = ModuleDefinition.ReadModule(RewrittenNclPath);
        var alInsert = NavRecordMethod(module, "ALInsertAsync", "DataError", "Boolean", "Boolean");
        var instructions = alInsert.Body.Instructions;

        // The prepend is `ldarg.0; call helper` inserted before the original body, so the
        // helper call must sit within the first few instructions — not merely somewhere in
        // the method, which a later, conditional call site would also satisfy.
        var helperIndex = instructions
            .Select((instruction, index) => (instruction, index))
            .Where(x => (x.instruction.OpCode == OpCodes.Call || x.instruction.OpCode == OpCodes.Callvirt)
                        && (x.instruction.Operand as MethodReference)?.FullName == HelperFullName)
            .Select(x => (int?)x.index)
            .FirstOrDefault();

        Assert.True(helperIndex.HasValue,
            "NavRecord.ALInsertAsync(DataError, bool, bool) does not call "
            + "UserTableTriggerPatches.CreateUserPropertyOnUserInsert — the User Property row BC's "
            + "own User insert trigger creates would never be written, and every AL path that "
            + "reaches UserManagement.DirectSetUserFieldValue would fail with "
            + "\"The User Property does not exist\" (issue #2355).");

        // Three prepends share this entry point (AutoIncrement, SystemFields, this one), each
        // contributing `ldarg.0; call`, so index 5 is the last slot the third can occupy.
        Assert.True(helperIndex!.Value <= 5,
            $"the companion-row helper is called at instruction {helperIndex} of "
            + "NavRecord.ALInsertAsync, not in the prepended prefix — it must run before the "
            + "original body, exactly as BC's own OnBeforeInsertAsync does.");
        Assert.Equal(OpCodes.Ldarg_0, instructions[helperIndex.Value - 1].OpCode);
    }

    [SkippableFact]
    public void ModifyAndDeleteEntryPoints_DoNotCallTheCompanionRowHelper()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");

        using var module = ModuleDefinition.ReadModule(RewrittenNclPath);

        // BC creates the companion row in OnBEFOREInsert only. Modify must not create a second
        // one, and Delete must not create one for a user being removed.
        foreach (var name in new[] { "ALModifyAsync", "ALDeleteAsync" })
        {
            var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
            foreach (var method in navRecord!.Methods.Where(m => m.Name == name && m.HasBody))
                Assert.DoesNotContain(HelperFullName, CalledMethods(method));
        }
    }

    [SkippableFact]
    public void Helper_IsANoOp_ForAnythingThatIsNotAUserRecord()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Null and a record with no metatable are the two shapes the prepend sees on every
        // insert of every other table in the run; neither may throw, and neither may reach
        // the session lookup, which would raise RunnerOutOfScopeException on a bare record.
        UserTableTriggerPatches.CreateUserPropertyOnUserInsert(null);
        UserTableTriggerPatches.CreateUserPropertyOnUserInsert(new object());
        UserTableTriggerPatches.CreateUserPropertyOnUserInsert(
            (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(NavRecord)));
    }
}
