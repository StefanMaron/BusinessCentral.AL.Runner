// PageSaveDataLayerPrependBindingTests — issues #4142 and #5509.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does. The BC-observable
// claims belong upstream in StefanMaron/BusinessCentral.AL.Language.Tests, where a live service
// tier adjudicates them: corpus codeunit 60562 ("a row saved by a page gets an AutoIncrement
// value and stamped system fields, the same as Record.Insert", #4142) and codeunit 67812
// ("inside OnInsert / OnModify Rec still holds key 0 and the stored stamps; they are written
// below the trigger", #5509).
//
// What THIS test pins is the runner's own wiring, which is registration-only code inside
// NclCecilRewrite and so produces no observable C# call graph — a regression that drops a
// prepend, or rebinds it to a method on the wrong side of the table triggers, is invisible to
// every other C# test and shows up only as an AL suite failing far downstream. Reading the
// rewritten IL makes it fail here instead.
//
// The shape, measured on Ncl 28.1.49838.53910 and 28.4.53241.54346 and written up in
// docs/page-save-data-layer-prepends.md:
//
//   AL   Rec.Insert()   -> ALInsertAsync(3) -> InsertAsync(4) -> triggers -> RecordImplementation.InsertRecordAsync
//   page CurrPage.Update -> NavForm.SaveRecordAsync -> InsertAsync(4)         [direct]
//   AL   Rec.Modify()   -> ALModifyAsync(3) -> ModifyAsync(4) -> triggers -> RecordImplementation.ModifyRecordAsync
//   page CurrPage.Update -> NavForm.SaveRecordAsync -> ModifyAsync(3) -> ModifyAsync(4)
//
// The funnels InsertAsync(4) / ModifyAsync(4) only NOTE that a write is coming (so the
// NavForm.SyncTempTableWithSourceTableAsync route, which calls InsertRecordAsync /
// ModifyRecordAsync directly, is not written to); the AutoIncrement key and the stamps are written
// by the prepend on InsertRecordAsync / ModifyRecordAsync, BELOW the triggers (#5509). A prepend left
// on an AL entry point as well as the funnel runs twice per AL write, and one on ModifyAsync(3)
// as well as ModifyAsync(4) does the same for a page write.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageSaveDataLayerPrependBindingTests
{
    // Cecil's MethodReference.FullName carries the RETURN TYPE. Omitting it makes every
    // `==` comparison below false and every `DoesNotContain` true — the positive arms go
    // red for the wrong reason and the negative arms pass vacuously, which is the half
    // nothing would have reported. Measured on this file before the prefix was added.
    private const string NavRecordArg = "(Microsoft.Dynamics.Nav.Runtime.NavRecord)";

    private const string AutoIncrement = "System.Void AlRunner.BcRuntime::AssignAutoIncrement" + NavRecordArg;
    private const string StampOnInsert = "System.Void AlRunner.BcRuntime::StampSystemFieldsOnInsert" + NavRecordArg;
    private const string StampOnModify = "System.Void AlRunner.BcRuntime::StampSystemFieldsOnModify" + NavRecordArg;
    private const string NoteInsert = "System.Void AlRunner.BcRuntime::NoteInsertFunnel" + NavRecordArg;
    private const string NoteModify = "System.Void AlRunner.BcRuntime::NoteModifyFunnel" + NavRecordArg;
    private const string WriteInsert = "System.Void AlRunner.BcRuntime::WriteStorageFieldsOnInsert" + NavRecordArg;
    private const string WriteModify = "System.Void AlRunner.BcRuntime::WriteStorageFieldsOnModify" + NavRecordArg;

    private static readonly string[] EveryFieldWriteHelper =
        { AutoIncrement, StampOnInsert, StampOnModify, NoteInsert, NoteModify, WriteInsert, WriteModify };

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

    // "Was Ncl rewritten at all?" is keyed on no single prepend, and fails rather than skips on CI (#4782).
    private static void SkipUnlessRewritten(ModuleDefinition module)
        => NclRewriteMarker.SkipUnlessRewritten(module, RewrittenNclPath);

    private static ModuleDefinition OpenRewrittenNcl()
    {
        Skip.IfNot(File.Exists(RewrittenNclPath),
            $"the rewritten Ncl is not present at '{RewrittenNclPath}'.");
        return ModuleDefinition.ReadModule(RewrittenNclPath);
    }

    /// <summary>
    /// A prepend is a run of ldarg.0 / ldfld / call ahead of the original body, so "is it in the
    /// prepended prefix" is a question about SHAPE — nothing but those opcodes ahead of it — rather
    /// than about a fixed index, which another prepend landing later would invalidate.
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
            $"{method.DeclaringType.Name}.{method.Name} does not call {helperFullName} — {whatBreaksOtherwise}");

        // The argument is `this` (ldarg.0) or its parentRecord field (ldarg.0; ldfld).
        var argumentStart = instructions[helperIndex!.Value - 1].OpCode == OpCodes.Ldfld
            ? helperIndex.Value - 2
            : helperIndex.Value - 1;
        Assert.Equal(OpCodes.Ldarg_0, instructions[argumentStart].OpCode);
        for (var i = 0; i < helperIndex.Value; i++)
            Assert.True(
                instructions[i].OpCode == OpCodes.Ldarg_0 || instructions[i].OpCode == OpCodes.Ldfld
                || instructions[i].OpCode == OpCodes.Call,
                $"instruction {i} of {method.DeclaringType.Name}.{method.Name} is {instructions[i].OpCode}, so "
                + $"{helperFullName} is no longer inside the prepended prefix — it would run after "
                + "part of the original body instead of before all of it.");
    }

    private static MethodDefinition RecordImplementationMethod(ModuleDefinition module, string name)
    {
        var recImpl = module.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        Assert.NotNull(recImpl);
        var method = recImpl!.Methods.FirstOrDefault(m =>
            m.Name == name && m.HasBody
            && m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(new[] { "DataError" }));
        Assert.True(method != null, $"RecordImplementation.{name}(DataError) not found in the rewritten Ncl.");
        return method!;
    }

    private static MethodDefinition InsertFunnel(ModuleDefinition module)
        => NavRecordMethod(module, "InsertAsync", "DataError", "Boolean", "Boolean", "Boolean");

    private static MethodDefinition ModifyFunnel(ModuleDefinition module)
        => NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean", "Boolean");

    [SkippableFact]
    public void InsertFunnel_NotesThatAStorageWriteIsComing()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(InsertFunnel(module), NoteInsert,
            "InsertRecordAsync would never write the AutoIncrement key or the stamps for a row saved by "
            + "Rec.Insert() or by a page (NavForm.SaveRecordAsync): both would keep key 0 and default "
            + "system fields (issues #4142, #5509).");
    }

    [SkippableFact]
    public void InsertRecordAsync_WritesTheAutoIncrementKeyAndStamps_BelowTheTriggers()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(RecordImplementationMethod(module, "InsertRecordAsync"), WriteInsert,
            "a row would be stored with AutoIncrement key 0 and default SystemCreatedAt/By, on the AL route "
            + "and on the page-save route alike (issues #4142, #5509).");
        Assert.Single(CalledMethods(RecordImplementationMethod(module, "InsertRecordAsync")), n => n == WriteInsert);
    }

    [SkippableFact]
    public void ModifyFunnel_NotesThatAStorageWriteIsComing()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(ModifyFunnel(module), NoteModify,
            "ModifyRecordAsync would never stamp SystemModifiedAt/By for a row modified by Rec.Modify() "
            + "or by a page: NavForm.SaveRecordAsync calls ModifyAsync(3), which forwards to this method, "
            + "and never ALModifyAsync (issues #4142, #5509).");
    }

    [SkippableFact]
    public void ModifyRecordAsync_StampsTheModifiedPair_BelowTheTriggers()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        AssertCalledInsideThePrependedPrefix(RecordImplementationMethod(module, "ModifyRecordAsync"), WriteModify,
            "SystemModifiedAt/By would freeze at whatever the insert wrote (issues #4142, #5509).");
        Assert.Single(CalledMethods(RecordImplementationMethod(module, "ModifyRecordAsync")), n => n == WriteModify);
    }

    [SkippableFact]
    public void TheTriggerSideMethods_DoNotWriteTheFieldsThemselves()
    {
        // #5509, the regression this guards: the key and the stamps were written by a prepend on
        // InsertAsync(4) / ModifyAsync(4), which run BEFORE OnBeforeInsert/Modify subscribers and the
        // table trigger, so `Rec` already held them inside the trigger. Only the Note* helper may sit
        // on the trigger side of the funnel.
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        foreach (var method in new[] { InsertFunnel(module), ModifyFunnel(module) })
            foreach (var writer in new[] { AutoIncrement, StampOnInsert, StampOnModify, WriteInsert, WriteModify })
                Assert.DoesNotContain(writer, CalledMethods(method));

        var navRecord = module.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        Assert.NotNull(navRecord);
        foreach (var method in navRecord!.Methods.Where(m => (m.Name == "ALInsertAsync" || m.Name == "ALModifyAsync") && m.HasBody))
            foreach (var helper in EveryFieldWriteHelper)
                Assert.DoesNotContain(helper, CalledMethods(method));
    }

    [SkippableFact]
    public void TheStorageSideMethods_DoNotCarryTheOtherDirectionsHelpers()
    {
        // The insert write also stamps SystemCreatedAt/By; on the modify side it would reset the
        // creation stamp on every modify — what corpus codeunit 60172's
        // SystemCreatedBy_AfterModify_SameAsInsert forbids. And a Note* helper down here would mark
        // a write that has already happened, so the next direct call would write once too often.
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        var insertRecord = CalledMethods(RecordImplementationMethod(module, "InsertRecordAsync"));
        foreach (var other in new[] { WriteModify, NoteInsert, NoteModify, AutoIncrement, StampOnInsert, StampOnModify })
            Assert.DoesNotContain(other, insertRecord);

        var modifyRecord = CalledMethods(RecordImplementationMethod(module, "ModifyRecordAsync"));
        foreach (var other in new[] { WriteInsert, NoteInsert, NoteModify, AutoIncrement, StampOnInsert, StampOnModify })
            Assert.DoesNotContain(other, modifyRecord);
    }

    [SkippableFact]
    public void TheFunnels_DoNotCarryEachOthersNote()
    {
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        Assert.DoesNotContain(NoteModify, CalledMethods(InsertFunnel(module)));
        Assert.DoesNotContain(NoteInsert, CalledMethods(ModifyFunnel(module)));
        Assert.Single(CalledMethods(InsertFunnel(module)), n => n == NoteInsert);
        Assert.Single(CalledMethods(ModifyFunnel(module)), n => n == NoteModify);
    }

    [SkippableFact]
    public void TheThreeArgModifyForwarder_DoesNotAlsoCarryTheNote()
    {
        // ModifyAsync(3) is a two-line forwarder to ModifyAsync(4). It is the overload a page
        // save calls, which makes it the tempting place to put the note — and the wrong one: a
        // prepend there marks a page-driven modify twice and an AL-driven one not at all.
        using var module = OpenRewrittenNcl();
        SkipUnlessRewritten(module);

        var modify3 = NavRecordMethod(module, "ModifyAsync", "DataError", "Boolean", "Boolean");
        foreach (var helper in EveryFieldWriteHelper)
            Assert.DoesNotContain(helper, CalledMethods(modify3));

        // ...and it must still forward, or the funnel assertion above is about a method nothing
        // on the page route reaches.
        Assert.Contains(
            CalledMethods(modify3),
            name => name.Contains(
                "NavRecord::ModifyAsync(Microsoft.Dynamics.Nav.Types.DataError,System.Boolean,"
                + "System.Boolean,System.Boolean)",
                StringComparison.Ordinal));
    }
}
