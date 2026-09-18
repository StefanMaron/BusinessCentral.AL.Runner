// PageSaveDataLayerPrependBindingTests — issue #4142.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable
// claim ("a row saved by a page gets an AutoIncrement value and stamped system fields, the
// same as a row saved by Record.Insert") belongs upstream in
// StefanMaron/BusinessCentral.AL.Language.Tests, where a live service tier adjudicates it —
// corpus PR #379, codeunit 60562.
//
// What THIS test pins is the runner's own wiring: WHICH NavRecord method each of the three
// data-layer prepends is bound to. The binding is registration-only code inside
// NclCecilRewrite, so it produces no observable C# call graph — a regression that drops a
// prepend, or rebinds it to the AL entry point it used to sit on, is invisible to every other
// C# test and shows up only as an AL suite failing far downstream with a zero key or an empty
// system field. Reading the rewritten IL is what makes that regression fail here instead.
//
// The shape of the defect, measured on Ncl 28.1.49838.53910 and written up in
// docs/page-save-data-layer-prepends.md#the-two-routes:
//
//   AL   Rec.Insert()   -> ALInsertAsync(3) -> InsertAsync(4)
//   page CurrPage.Update -> NavForm.SaveRecordAsync -> InsertAsync(4)          [direct]
//   AL   Rec.Modify()   -> ALModifyAsync(3) -> ModifyAsync(4)
//   page CurrPage.Update -> NavForm.SaveRecordAsync -> ModifyAsync(3) -> ModifyAsync(4)
//
// Insert's two routes meet at InsertAsync(4); modify's meet at ModifyAsync(4) but the page
// arrives via the 3-arg forwarder. Hence the two "must not also be on" assertions below: a
// prepend left on the AL entry point as well as the funnel runs TWICE per AL write, and one on
// ModifyAsync(3) as well as ModifyAsync(4) does the same for a page write.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageSaveDataLayerPrependBindingTests
{
    private const string AutoIncrement =
        "AlRunner.BcRuntime::AssignAutoIncrement(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private const string StampOnInsert =
        "AlRunner.BcRuntime::StampSystemFieldsOnInsert(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private const string StampOnModify =
        "AlRunner.BcRuntime::StampSystemFieldsOnModify(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    /// <summary>
    /// The Cecil-rewritten Ncl the test host itself loaded — the very bytes the prepends were
    /// written into (BcEngineBootstrap rewrites into this assembly's own bin directory).
    /// </summary>
    private static string RewrittenNclPath => Path.Combine(
        Path.GetDirectoryName(typeof(PageSaveDataLayerPrependBindingTests).Assembly.Location)
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
            + "— BC's shape changed, and the prepend that depends on it would be silently unbound.");
        return method!;
    }

    private static List<string> CalledMethods(MethodDefinition method)
        => method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => (i.Operand as MethodReference)?.FullName ?? string.Empty)
            .ToList();

    /// <summary>
    /// Tell "the file on disk has not been Cecil-rewritten yet" (a legitimate skip) apart from
    /// "it was rewritten and the prepend under test is missing" (the regression). The marker is
    /// a prepend this class asserts nothing about, on a method this class does not move things
    /// to — so it cannot go absent for the same reason a subject does, which is what would
    /// convert a real failure into a green skip.
    /// </summary>
    private static void SkipUnlessRewritten(ModuleDefinition module)
    {
        var marker = CalledMethods(NavRecordMethod(module, "ALDeleteAsync", "DataError", "Boolean", "Boolean"))
            .Any(name => name.Contains(
                "AlRunner.Patches.UserTableTriggerPatches::OnAfterUserDelete(System.Object)",
                StringComparison.Ordinal));
        Skip.IfNot(marker,
            $"'{RewrittenNclPath}' has not been Cecil-rewritten (no prepends present at all), so "
            + "there is nothing to assert about the prepend list. Run the runner once to warm the "
            + "Cecil cache first — CI's bc-tests.yml does exactly that before `dotnet test`.");
    }

    private static ModuleDefinition OpenRewrittenNcl()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");
        return ModuleDefinition.ReadModule(RewrittenNclPath);
    }

    /// <summary>
    /// A prepend contributes exactly <c>ldarg.0; call helper</c> ahead of the original body, so
    /// "is it in the prepended prefix" is a question about SHAPE — nothing but those two opcodes
    /// ahead of it — rather than about a fixed index, which another prepend landing later would
    /// invalidate.
    /// </summary>
    private static void AssertCalledInsideThePrependedPrefix(
        MethodDefinition method, string helperFullName, string whatBreaksOtherwise)
    {
        var instructions = method.Body.Instructions;
        var helperIndex = instructions
            .Select((instruction, index) => (instruction, index))
            .Where(x => (x.instruction.OpCode == OpCodes.Call || x.instruction.OpCode == OpCodes.Callvirt)
                        && (x.instruction.Operand as MethodReference)?.FullName == helperFullName)
            .Select(x => (int?)x.index)
            .FirstOrDefault();

        Assert.True(helperIndex.HasValue,
            $"NavRecord.{method.Name} does not call {helperFullName} — {whatBreaksOtherwise}");

        Assert.Equal(OpCodes.Ldarg_0, instructions[helperIndex!.Value - 1].OpCode);
        for (var i = 0; i < helperIndex.Value; i++)
            Assert.True(
                instructions[i].OpCode == OpCodes.Ldarg_0 || instructions[i].OpCode == OpCodes.Call,
                $"instruction {i} of NavRecord.{method.Name} is {instructions[i].OpCode}, so "
                + $"{helperFullName} is no longer inside the prepended prefix — it would run after "
                + "part of the original body instead of before all of it.");
    }

    [SkippableFact]
    public void InsertFunnel_CarriesTheAutoIncrementAssignment()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(
            NavRecordMethod(module, "InsertAsync", "DataError", "Boolean", "Boolean", "Boolean"),
            AutoIncrement,
            "a row saved by a page (NavForm.SaveRecordAsync, what CurrPage.Update reaches) would "
            + "keep an AutoIncrement key of 0, and a second page save on the same table would "
            + "duplicate-key on it (issue #4142).");
    }

    [SkippableFact]
    public void InsertFunnel_CarriesTheSystemFieldStamp()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(
            NavRecordMethod(module, "InsertAsync", "DataError", "Boolean", "Boolean", "Boolean"),
            StampOnInsert,
            "a row saved by a page would read SystemCreatedAt = 0DT and SystemCreatedBy = the "
            + "null GUID, while the same row inserted by Rec.Insert() reads both (issue #4142).");
    }

    [SkippableFact]
    public void ModifyFunnel_CarriesTheSystemModifiedStamp()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(
            NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean", "Boolean"),
            StampOnModify,
            "SystemModifiedAt/By would freeze at whatever the insert wrote for a row modified by "
            + "a page, because NavForm.SaveRecordAsync calls ModifyAsync(3) — which forwards to "
            + "this method — and never ALModifyAsync (issue #4142).");
    }

    [SkippableFact]
    public void TheAlEntryPoints_DoNotAlsoCarryTheDataLayerPrepends()
    {
        // The double-fire half. ALInsertAsync(3) forwards to InsertAsync(4) and ALModifyAsync(3)
        // forwards to ModifyAsync(4), so a helper left on both runs twice per AL write: two
        // AutoIncrement draws (the second is a no-op only because the helper re-reads the field
        // and finds it non-zero) and two system-field stamps.
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        Assert.NotNull(navRecord);

        foreach (var method in navRecord!.Methods.Where(m => m.Name == "ALInsertAsync" && m.HasBody))
        {
            Assert.DoesNotContain(AutoIncrement, CalledMethods(method));
            Assert.DoesNotContain(StampOnInsert, CalledMethods(method));
        }

        foreach (var method in navRecord.Methods.Where(m => m.Name == "ALModifyAsync" && m.HasBody))
            Assert.DoesNotContain(StampOnModify, CalledMethods(method));
    }

    [SkippableFact]
    public void TheThreeArgModifyForwarder_DoesNotAlsoCarryTheStamp()
    {
        // ModifyAsync(3) is a two-line forwarder to ModifyAsync(4). It is the overload a page
        // save calls, which makes it the tempting place to put the fix — and the wrong one: a
        // prepend there stamps a page-driven modify twice and an AL-driven one not at all.
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        var modify3 = NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean");
        Assert.DoesNotContain(StampOnModify, CalledMethods(modify3));

        // ...and it must still forward, or the funnel assertion above is about a method nothing
        // on the page route reaches.
        Assert.Contains(
            CalledMethods(modify3),
            name => name.Contains(
                "NavRecord::ModifyAsync(Microsoft.Dynamics.Nav.Types.DataError,System.Boolean,"
                + "System.Boolean,System.Boolean)",
                StringComparison.Ordinal));
    }

    [SkippableFact]
    public void TheInsertStampIsNotOnTheModifyFunnel_AndViceVersa()
    {
        // StampSystemFieldsOnInsert writes SystemCreatedAt/By as well as SystemModifiedAt/By.
        // On the modify funnel it would reset the creation stamp on every modify — the exact
        // thing corpus codeunit 60172's SystemCreatedBy_AfterModify_SameAsInsert forbids, and
        // the wrong fix a reader reaching for "one helper on one funnel" would write.
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        Assert.DoesNotContain(StampOnInsert, CalledMethods(
            NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean", "Boolean")));
        Assert.DoesNotContain(StampOnModify, CalledMethods(
            NavRecordMethod(module, "InsertAsync", "DataError", "Boolean", "Boolean", "Boolean")));
    }
}
