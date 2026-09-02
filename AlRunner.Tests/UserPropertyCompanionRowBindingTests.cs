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

    /// <summary>
    /// A marker prepend that predates this one, used to tell "the file on disk has not been
    /// Cecil-rewritten yet" (a legitimate skip) apart from "it was rewritten and OUR prepend
    /// is missing" (the regression this test exists to catch). Without the distinction an
    /// un-rewritten bin would fail the test for the wrong reason.
    /// </summary>
    private const string PriorPrependMarker =
        "AlRunner.BcRuntime::AssignAutoIncrement(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private static void SkipUnlessRewritten(MethodDefinition alInsert)
        => Skip.IfNot(
            CalledMethods(alInsert).Any(name => name.Contains(PriorPrependMarker, StringComparison.Ordinal)),
            $"'{RewrittenNclPath}' has not been Cecil-rewritten (no prepends present at all), so "
            + "there is nothing to assert about the prepend list. Run the runner once to warm "
            + "the Cecil cache first — CI's bc-tests.yml does exactly that before `dotnet test`.");

    // These two read a FILE with Mono.Cecil and load no BC type, so they deliberately do NOT
    // gate on BcEngineFixture.Ready. That gate is about whether the engine can be brought up
    // in-process; a machine where it cannot (a cold Cecil cache, say) can still answer the
    // question these tests ask, and gating on it hid them behind an unrelated skip.
    [SkippableFact]
    public void AlInsertEntryPoint_CallsTheCompanionRowHelperAsItsFirstAct()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");

        using var module = ModuleDefinition.ReadModule(RewrittenNclPath);
        var alInsert = NavRecordMethod(module, "ALInsertAsync", "DataError", "Boolean", "Boolean");
        SkipUnlessRewritten(alInsert);
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

        // It must be in the PREPENDED PREFIX, not merely somewhere in the method: the
        // companion row has to be written before the original body runs, exactly as BC's own
        // OnBeforeInsertAsync does. Several prepends share this entry point (AutoIncrement,
        // SystemFields, the rowversion write note, the All Profile guard, this one) and each
        // contributes exactly `ldarg.0; call`, so the prefix is characterised by its SHAPE —
        // nothing but those two opcodes ahead of us — rather than by a fixed index that a
        // sixth prepend would invalidate.
        Assert.Equal(OpCodes.Ldarg_0, instructions[helperIndex!.Value - 1].OpCode);
        for (var i = 0; i < helperIndex.Value; i++)
            Assert.True(
                instructions[i].OpCode == OpCodes.Ldarg_0 || instructions[i].OpCode == OpCodes.Call,
                $"instruction {i} of NavRecord.ALInsertAsync is {instructions[i].OpCode}, so the "
                + "companion-row helper is no longer inside the prepended prefix — it would run "
                + "after part of the original body instead of before all of it.");
    }

    [SkippableFact]
    public void ModifyAndDeleteEntryPoints_DoNotCallTheCompanionRowHelper()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");

        using var module = ModuleDefinition.ReadModule(RewrittenNclPath);
        SkipUnlessRewritten(NavRecordMethod(module, "ALInsertAsync", "DataError", "Boolean", "Boolean"));

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
